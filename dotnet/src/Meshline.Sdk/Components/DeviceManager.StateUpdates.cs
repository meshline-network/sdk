using Meshline.Identity;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Meshline.Components;

sealed partial class DeviceManager
{
    DeviceStateUpdates? _updates;
    DeviceStateUpdates Updates => _updates ??= new DeviceStateUpdates(this, databaseOptions);

    static void MergeDeviceIntent(JsonObject previous, JsonObject patch)
    {
        if (patch.ContainsKey("certificates"))
            foreach (var name in previous.Select(value => value.Key).Where(value => value.StartsWith("remove:", StringComparison.Ordinal)).ToArray()) previous.Remove(name);
    }

    async Task RemoveLatestDeviceAsync(string deviceId, CancellationToken token)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref token);
        if (Models.Protocol.Identifiers.ValidateDeviceId(deviceId) is { } invalid) throw new ArgumentException(invalid.Message, nameof(deviceId));
        var result = await Updates.SubmitAsync<DeviceStatePublishResult>(Options.AccountId,
            new() { ["relay"] = null, ["remove:" + deviceId] = true, ["revision"] = null }, token).ConfigureAwait(false);
        if (result.Status != DeviceStatePublishStatus.Accepted) throw new InvalidOperationException("The device removal was only staged and is not yet authoritative.");
    }

    async Task InitializeDeviceUpdatesAsync(CancellationToken token)
    {
        _ = Updates;
        await using var database = new MeshlineDbContext(databaseOptions);
        var row = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "device.state.publish" && value.Pending && value.IntentJson == null && value.DocumentJson != null, token).ConfigureAwait(false);
        if (row is null) return;
        var state = ProtocolModel.FromJson<AccountDeviceState>(row.DocumentJson!)!;
        await Updates.ImportAsync(Options.AccountId, DeviceIntent(row.RelayId, state.Certificates, null), token).ConfigureAwait(false);
    }

    static JsonObject DeviceIntent(string relayId, IReadOnlyList<DeviceCertificate>? certificates, DeviceStatePublishOptions? options)
    {
        var intent = new JsonObject { ["relay"] = relayId, ["revision"] = options?.Revision, ["recovery"] = options?.IsRecovery ?? false, ["previous"] = options?.PreviousState?.ToJson(), ["preserve"] = certificates is null };
        if (certificates is not null) intent["certificates"] = new JsonArray(certificates.Select(value => JsonNode.Parse(value.ToJson())).ToArray());
        return intent;
    }

    async Task<DeviceStatePublishResult> UpdateDevicesAsync(string relayId, IReadOnlyList<DeviceCertificate>? certificates, DeviceStatePublishOptions? options, CancellationToken token)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref token);
        if (accountSigner is null) throw new InvalidOperationException("An account signer is required to publish device state.");
        if (options?.Revision is { } revision) _ = AccountDeviceState.GetNextRevision(revision, -1);
        if (RelayIdentity.ValidateRelayId(relayId) is { } invalid) throw new ArgumentException(invalid.Message, nameof(relayId));
        if (options?.PreviousState is { } previous && (previous.Account != Options.AccountId || previous.Validate(Context) is not null)) throw new ArgumentException("Invalid previous device state.", nameof(options));
        if (certificates is not null)
        {
            if (certificates.Count > 8) throw new ArgumentException("A device state supports at most eight certificates.", nameof(certificates));
            var deviceIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var certificate in certificates)
            {
                if (certificate is null || certificate.Account != Options.AccountId || certificate.Validate(Context) is not null) throw new ArgumentException("Invalid device certificate.", nameof(certificates));
                if (!deviceIds.Add(certificate.GetDeviceId(Context))) throw new ArgumentException("A device state cannot contain duplicate device identities.", nameof(certificates));
            }
        }
        return await Updates.SubmitAsync<DeviceStatePublishResult>(Options.AccountId, DeviceIntent(relayId, certificates, options), token).ConfigureAwait(false);
    }

    async Task<object?> PublishLatestDevicesAsync(StateUpdateAttempt attempt, CancellationToken token)
    {
        var relayId = attempt.Intent["relay"]?.GetValue<string>()
            ?? (await accountManager.GetRouteAsync(cancellationToken: token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account route could not be found. Establish or recover the account explicitly.")).RelayId;
        for (var retry = 0; ; retry++)
        {
            try { return await PublishLatestDevicesOnceAsync(attempt, relayId, token).ConfigureAwait(false); }
            catch (Exception error) when (retry < 2 && attempt.Intent["revision"] is null && IsDeviceStateConflict(error))
            {
                await attempt.CheckAsync(token).ConfigureAwait(false);
                await ReadAuthorizationStateAsync(relayId, token).ConfigureAwait(false);
            }
        }
    }

    async Task<object?> PublishLatestDevicesOnceAsync(StateUpdateAttempt attempt, string relayId, CancellationToken token)
    {
        var intent = attempt.Intent;
        var certificates = intent["certificates"]?.AsArray().Select(value => ProtocolModel.FromJson<DeviceCertificate>(value!.ToJsonString())!).ToArray();
        if (intent["preserve"]?.GetValue<bool>() == true && certificates is not null && Local is { } local)
            certificates = certificates.Select(value => value.GetDeviceId(Context) == local.GetDeviceId(Context) ? local : value).ToArray();
        var previous = intent["previous"]?.GetValue<string>();
        var removals = intent.Select(value => value.Key).Where(value => value.StartsWith("remove:", StringComparison.Ordinal)).Select(value => value[7..]).ToArray();
        AccountDeviceState? continuity = null;
        if (removals.Length != 0)
        {
            continuity = await ReadAuthorizationStateAsync(relayId, token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The account has no published device state.");
            if (certificates is null)
            {
                await using var database = new MeshlineDbContext(databaseOptions);
                var saved = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "device.state.publish", token).ConfigureAwait(false);
                if ((saved is null || saved.Revision <= continuity.Revision) && continuity.Certificates.All(value => !removals.Contains(value.GetDeviceId(Context), StringComparer.Ordinal)))
                    return new DeviceStatePublishResult { DeviceState = continuity, Status = DeviceStatePublishStatus.Accepted };
                certificates = saved is { Pending: true, DocumentJson: not null, RequestState: PublicationRequestState.Prepared or PublicationRequestState.Superseded } && saved.Revision > continuity.Revision
                    ? ProtocolModel.FromJson<AccountDeviceState>(saved.DocumentJson)!.Certificates.ToArray() : continuity.Certificates.ToArray();
            }
            certificates = certificates.Where(value => !removals.Contains(value.GetDeviceId(Context), StringComparer.Ordinal)).ToArray();
        }
        return await PublishDeviceStateCoreAsync(relayId, certificates,
            new()
            {
                Revision = intent["revision"]?.GetValue<long>(),
                IsRecovery = intent["recovery"]?.GetValue<bool>() == true,
                PreviousState = previous is null ? continuity : ProtocolModel.FromJson<AccountDeviceState>(previous)
            }, token, attempt, continuity, removals).ConfigureAwait(false);
    }

    sealed class DeviceStateUpdates(DeviceManager owner, DatabaseOptions options) : StateUpdateCoordinator(options)
    {
        protected override string Operation => "device.state.publish";

        protected override async Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) =>
            await database.SignedRequests.FindAsync([Operation], token).ConfigureAwait(false);

        protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) =>
            database.SignedRequests.Add(new() { Method = Operation, RelayId = intent["relay"]?.GetValue<string>() ?? "", Revision = -1, RequestState = PublicationRequestState.None }).Entity;

        protected override async Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token)
        {
            var rows = await database.SignedRequests.AsNoTracking().Where(value => value.Method == Operation && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token).ConfigureAwait(false);
            return rows.Select(value => Publication(owner.Options.AccountId, value)).ToList();
        }

        protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => owner.PublishLatestDevicesAsync(attempt, token);

        protected override void Report(string resource, Exception error) => owner.ReportBackgroundError(BackgroundOperation.Synchronize, resource, error);

        protected override void BeforeMerge(JsonObject previous, JsonObject patch) => MergeDeviceIntent(previous, patch);

        protected override DateTimeOffset? GetFollowUpAt(object? result) =>
            result is DeviceStatePublishResult { Status: DeviceStatePublishStatus.Staged } ? Clock.UtcNow.AddSeconds(30) : null;

        protected override bool CanRetry(Exception error)
        {
            var cause = error is DeviceAuthorizationException ? error.InnerException ?? error : error;
            return cause is DeviceStatePublicationException { Failure: DeviceStatePublicationFailure.StateChanged } || base.CanRetry(cause);
        }
    }
}
