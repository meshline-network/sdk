using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Components;

sealed partial class ProfileManager
{
    ProfileStateUpdates? _updates;
    ProfileStateUpdates Updates => _updates ??= new ProfileStateUpdates(this, databaseOptions);

    /// <inheritdoc/>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Updates.Start(RuntimeCancellationToken);
        return Task.CompletedTask;
    }

    async Task InitializeProfileUpdatesAsync(CancellationToken token)
    {
        _ = Updates;
        await using var database = new MeshlineDbContext(databaseOptions);
        var pending = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "profile.publish" && value.Pending && value.IntentJson == null && value.DocumentJson != null, token).ConfigureAwait(false);
        if (pending is null) return;
        var profile = JsonNode.Parse(pending.DocumentJson!)!.AsObject();
        var patch = new JsonObject();
        // Missing intent alone does not distinguish a legacy edit from initialization.
        // Require the retained initial workflow, matching values and no confirmed cache.
        var setup = await database.AccountEstablishments.AsNoTracking().SingleOrDefaultAsync(token).ConfigureAwait(false);
        if (setup is { IsInitialState: true, DeviceAuthorizationConfirmed: true } && setup.RelayId == pending.RelayId
            && !await database.AccountProfiles.AnyAsync(value => value.AccountId == AccountId, token).ConfigureAwait(false))
        {
            var initial = setup.InitialProfileJson is null ? new AccountInitialProfile() : JsonSerializer.Deserialize<AccountInitialProfile>(setup.InitialProfileJson)!;
            var request = ProtocolModel.FromJson<AccountProfile>(pending.DocumentJson!)!;
            if (request.Account == AccountId && request.Nickname == initial.Nickname && request.Bio == initial.Bio
                && request.Avatar?.ToJson() == initial.Avatar?.ToJson() && request.PublicDiscovery == initial.PublicDiscovery)
                patch["initialize_only"] = true;
        }
        foreach (var field in new[] { "nickname", "bio", "avatar", "public_discovery" }) patch[field] = profile[field]?.DeepClone();
        await Updates.ImportAsync(AccountId, patch, token).ConfigureAwait(false);
    }

    async Task<AccountProfile> UpdateLatestProfileAsync(ProfileUpdate update, CancellationToken token)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref token);
        ArgumentNullException.ThrowIfNull(update);
        if (update.PublicDiscovery.IsDeleted) throw new ArgumentException("PublicDiscovery cannot be deleted.", nameof(update));
        var patch = new JsonObject();
        StateUpdateJson.Field(patch, "nickname", update.Nickname);
        StateUpdateJson.Field(patch, "bio", update.Bio);
        StateUpdateJson.Field(patch, "avatar", update.Avatar);
        StateUpdateJson.Field(patch, "public_discovery", update.PublicDiscovery);
        // Validate supplied values before replacing a valid durable intent.
        var candidate = ProtocolModel.FromJson<AccountProfile>(StateUpdateJson.Apply(
            new AccountProfile { Account = AccountId, PublicDiscovery = false, UpdatedAt = 0, DeviceSignature = new byte[64].ToImmutableArray() }, patch,
            "nickname", "bio", "avatar", "public_discovery").ToJsonString())!;
        if (candidate.Validate() is { } invalid) throw new ArgumentException(invalid.Message, nameof(update));
        try { return await Updates.SubmitAsync<AccountProfile>(AccountId, patch, token).ConfigureAwait(false); }
        catch (Exception error) when (error is ProfilePublicationException or AggregateException)
        {
            ExceptionDispatchInfo.Throw(ProfilePublicationException.Unwrap(error));
            throw;
        }
    }

    async Task<object?> PublishLatestProfileAsync(StateUpdateAttempt attempt, CancellationToken token)
    {
        var relayId = (await accountManager.GetRouteAsync(cancellationToken: token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The account route could not be found.")).RelayId;
        var certificate = deviceSigner.Certificate;
        await using var database = new MeshlineDbContext(databaseOptions);
        var saved = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "profile.publish", token).ConfigureAwait(false);
        var local = await database.AccountProfiles.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, token).ConfigureAwait(false);
        var resolved = await ResolveProfileAsync(AccountId, token, relayId).ConfigureAwait(false);
        var initializeOnly = attempt.Intent["initialize_only"]?.GetValue<bool>() == true;
        if (initializeOnly && resolved is not null)
        {
            await ConfirmLatestProfileAsync(attempt, resolved, token).ConfigureAwait(false);
            return resolved.Profile;
        }
        if (initializeOnly && local is not null)
            throw new InvalidDataException("A previously known profile is unavailable; initial values cannot replace it.");
        var reusable = saved is { Pending: true, DocumentJson: not null, RequestState: PublicationRequestState.Prepared or PublicationRequestState.Superseded }
            && saved.DocumentJson != attempt.RejectedRequestJson ? saved.DocumentJson : null;
        var basis = resolved?.Profile;
        foreach (var json in new[] { attempt.Intent["snapshot"]?.GetValue<string>(), local?.DocumentJson, reusable, attempt.RequestJson })
        {
            if (json is null) continue;
            var candidate = ProtocolModel.FromJson<AccountProfile>(json)!;
            if (basis is null || candidate.UpdatedAt >= basis.UpdatedAt) basis = candidate;
        }
        basis ??= new() { Account = AccountId, PublicDiscovery = false, UpdatedAt = 0, DeviceSignature = [] };
        var request = ProtocolModel.FromJson<AccountProfile>(StateUpdateJson.Apply(basis, attempt.Intent, "nickname", "bio", "avatar", "public_discovery").ToJsonString())!;
        var retainedJson = attempt.RequestJson ?? (saved?.RelayId == relayId ? reusable : null);
        var retained = retainedJson is null ? null : ProtocolModel.FromJson<AccountProfile>(retainedJson)!;
        if (retained is not null && request.ToJson() == retained.ToJson()) request = retained;
        else
        {
            request = request with { UpdatedAt = Math.Max(Clock.UtcNow.ToUnixTimeSeconds(), checked(Math.Max(basis.UpdatedAt, saved?.Revision ?? -1) + 1)), DeviceSignature = [] };
            request = request with { DeviceSignature = [.. await deviceSigner.SignAsync(request.GetSigningInput(Context), token).ConfigureAwait(false)] };
            if (request.Validate() is { } invalid) throw new ArgumentException(invalid.Message);
        }
        ValidateCachedProfile(new() { Profile = request, SignerCertificate = certificate }, AccountId);
        await attempt.CurrentAsync(async () =>
        {
            await using var write = new MeshlineDbContext(databaseOptions);
            var row = await write.SignedRequests.SingleAsync(value => value.Method == "profile.publish", token).ConfigureAwait(false);
            if (row.Revision > request.UpdatedAt) throw new InvalidOperationException("Profile version changed while signing.");
            row.DocumentJson = request.ToJson();
            row.RelayId = relayId;
            row.Revision = request.UpdatedAt;
            row.Pending = true;
            row.RequestState = PublicationRequestState.Prepared;
            await write.SaveChangesAsync(token).ConfigureAwait(false);
            attempt.RecordRequest(row.DocumentJson);
        }, token).ConfigureAwait(false);
        var relay = await relayClients.GetAsync(relayId, deviceSigner, token).ConfigureAwait(false);
        await attempt.CheckAsync(token).ConfigureAwait(false);
        try { await relay.SendHttpAsync(HttpMethod.Put, "profile.publish", request, cancellationToken: token).ConfigureAwait(false); }
        catch (RelayException error) when (error.Error.IsDefinitiveRejection())
        {
            if (error.Error.Code != "clock_skew")
                attempt.RecordRejection(request.ToJson());
            throw;
        }
        catch (Exception error) when (error is HttpRequestException or TimeoutException or RelayException or InvalidDataException or CryptographicException or OperationCanceledException)
        {
            throw new ProfilePublicationException(error);
        }
        await ConfirmLatestProfileAsync(attempt, new() { Profile = request, SignerCertificate = certificate }, token).ConfigureAwait(false);
        return request;
    }

    async Task ConfirmLatestProfileAsync(StateUpdateAttempt attempt, ProfileResolveResult result, CancellationToken token)
    {
        var before = Profile?.ToJson();
        await attempt.CurrentAsync(() => SaveProfileAsync(result, token, published: true, notify: false), token).ConfigureAwait(false);
        if (before != Profile?.ToJson()) ProfileChanged?.Invoke(this, EventArgs.Empty);
    }

    sealed class ProfileStateUpdates(ProfileManager owner, DatabaseOptions options) : StateUpdateCoordinator(options)
    {
        protected override string Operation => "profile.publish";

        protected override void BeforeMerge(JsonObject previous, JsonObject patch)
        {
            // Snapshots are candidate bases, not field edits. Keep the newest pending
            // candidate under admission's gate, including after a rejected request.
            if (previous["snapshot"]?.GetValue<string>() is { } retained && patch["snapshot"]?.GetValue<string>() is { } incoming
                && ProtocolModel.FromJson<AccountProfile>(retained)!.UpdatedAt >= ProtocolModel.FromJson<AccountProfile>(incoming)!.UpdatedAt)
                patch.Remove("snapshot");

            // Republishing and empty updates retry the existing intent. Only explicit
            // field changes (including null for deletion) supersede creation-only values.
            if (patch["initialize_only"] is null && (patch.ContainsKey("nickname") || patch.ContainsKey("bio")
                || patch.ContainsKey("avatar") || patch.ContainsKey("public_discovery")))
                previous.Remove("initialize_only");
        }

        public Task<ProfileUpdateState> GetStateAsync(CancellationToken token) =>
            ReadAsync(owner.AccountId, (row, publishing, next) => ReadUpdateState((SignedRequestRecord?)row, publishing, next), token);

        public Task SaveDiscoveredAsync(Func<Task> save, CancellationToken token) =>
            WithPublicationLockAsync(owner.AccountId, save, token);

        protected override void OnChanged(string resource) => owner.NotifyUpdateStateChanged();

        protected override void SetError(IStatePublication publication, StateUpdateFailure? failure)
        {
            var error = failure?.Error is ProfilePublicationException { InnerException: { } cause } ? cause : failure?.Error;
            ((SignedRequestRecord)publication).LastErrorJson = error is null ? null : JsonSerializer.Serialize(new ProfileUpdateError
            {
                Message = error.Message,
                Code = (error as RelayException)?.Error.Code,
                OccurredAt = failure!.OccurredAt
            });
        }

        protected override async Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) =>
            await database.SignedRequests.FindAsync([Operation], token).ConfigureAwait(false);

        protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) =>
            database.SignedRequests.Add(new() { Method = Operation, RelayId = intent["relay"]?.GetValue<string>() ?? "", Revision = -1, RequestState = PublicationRequestState.None }).Entity;

        protected override async Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token)
        {
            var rows = await database.SignedRequests.AsNoTracking().Where(value => value.Method == Operation && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token).ConfigureAwait(false);
            return rows.Select(value => Publication(owner.AccountId, value)).ToList();
        }

        protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => owner.PublishLatestProfileAsync(attempt, token);

        protected override void Report(string resource, Exception error) => owner.ReportBackgroundError(BackgroundOperation.Synchronize, resource, ProfilePublicationException.Unwrap(error));

        protected override DateTimeOffset? GetNextAttemptAt(IStatePublication publication, Exception error, int failures)
        {
            if (error is ProfilePublicationException { InnerException: { } cause }) error = cause;
            var next = base.GetNextAttemptAt(publication, error, failures);
            if (next is not null && error is RelayException { Error.Code: "clock_skew" })
            {
                var revision = ((SignedRequestRecord)publication).Revision;
                if (publication.RequestJson is { } json)
                    revision = Math.Max(revision, ProtocolModel.FromJson<AccountProfile>(json)!.UpdatedAt);
                if (revision is > 0 && revision <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                {
                    var issuedAt = DateTimeOffset.FromUnixTimeSeconds(revision);
                    if (issuedAt > next) next = issuedAt;
                }
            }
            return next;
        }
    }
}
