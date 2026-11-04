using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Meshline.Components;

sealed partial class AccountManager
{
    AccountStateUpdates? _updates;
    AccountStateUpdates Updates => _updates ??= new AccountStateUpdates(this, databaseOptions);

    /// <inheritdoc/>
    protected override Task OnStartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Updates.Start(RuntimeCancellationToken);
        return Task.CompletedTask;
    }

    async Task<AccountRoute> UpdateRouteAsync(string relayId, TimeSpan validity, long? revision, bool recovery, CancellationToken token, bool retryRejected = false, bool classify = false)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref token);
        if (validity < TimeSpan.FromSeconds(1) || validity > TimeSpan.FromDays(3650)) throw new ArgumentOutOfRangeException(nameof(validity));
        if (revision is not null) _ = AccountRoute.GetNextRevision(revision, -1);
        if (accountSigner is null) throw new InvalidOperationException("An account signer is required to publish an account route.");
        if (Meshline.Identity.RelayIdentity.ValidateRelayId(relayId) is { } invalid) throw new ArgumentException(invalid.Message, nameof(relayId));
        return await Updates.SubmitAsync<AccountRoute>(AccountId, new()
        {
            ["relay"] = relayId,
            ["validity"] = (long)validity.TotalSeconds,
            ["revision"] = revision,
            ["recovery"] = recovery,
            ["retry_rejected"] = retryRejected,
            ["classify"] = classify
        }, token).ConfigureAwait(false);
    }

    async Task<object?> PublishLatestRouteAsync(StateUpdateAttempt attempt, CancellationToken token)
    {
        AccountRoute? remote = null;
        for (var retry = 0; ; retry++)
        {
            try { return await PublishLatestRouteOnceAsync(attempt, remote, token).ConfigureAwait(false); }
            catch (RelayException error) when (retry < 2 && attempt.Intent["revision"] is null && error.Error.Code is "stale_state" or "state_conflict")
            {
                await attempt.CheckAsync(token).ConfigureAwait(false);
                remote = await ResolveRouteAsync(AccountId, token).ConfigureAwait(false);
            }
        }
    }

    async Task<object?> PublishLatestRouteOnceAsync(StateUpdateAttempt attempt, AccountRoute? remote, CancellationToken token)
    {
        var signer = accountSigner ?? throw new InvalidOperationException("An account signer is required to publish a route.");
        var relayId = attempt.Intent["relay"]!.GetValue<string>();
        var validity = attempt.Intent["validity"]!.GetValue<long>();
        await using var database = new MeshlineDbContext(databaseOptions);
        var saved = await database.SignedRequests.AsNoTracking().SingleOrDefaultAsync(value => value.Method == "account.route.publish", token).ConfigureAwait(false);
        var known = await database.AccountRoutes.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, token).ConfigureAwait(false);
        var knownRevision = Math.Max(known?.Revision ?? -1, remote?.Revision ?? -1);
        var floor = Math.Max(knownRevision, saved?.Revision ?? -1);
        var explicitRevision = attempt.Intent["revision"]?.GetValue<long>();
        var request = saved is { DocumentJson: not null } && (saved.Pending && saved.RequestState.CanReuse() || attempt.Intent["retry_rejected"]?.GetValue<bool>() == true)
            ? ProtocolModel.FromJson<AccountRoute>(saved.DocumentJson!)! : null;
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        if (request is null || saved!.RelayId != relayId || request.ExpiresAt <= now || request.ExpiresAt - request.UpdatedAt != validity
            || explicitRevision is not null && explicitRevision != request.Revision || request.Revision < knownRevision)
        {
            var revision = explicitRevision;
            if (attempt.Intent["classify"]?.GetValue<bool>() == true && revision <= floor) revision = null;
            if (revision is null && attempt.Intent["recovery"]?.GetValue<bool>() == true) revision = Math.Max(checked(floor + 1), Clock.UtcNow.ToUnixTimeMilliseconds());
            request = new() { Account = AccountId, AccountPublicKey = signer.PublicKey, RelayId = relayId, Revision = AccountRoute.GetNextRevision(revision, floor), UpdatedAt = now, ExpiresAt = checked(now + validity), AccountSignature = [] };
            request = request with { AccountSignature = [.. await signer.SignAsync(request.GetAccountSigningInput(Context), token).ConfigureAwait(false)] };
        }
        if (request.Validate(Context) is { } invalid) throw new CryptographicException(invalid.Message);
        var sent = request;
        await attempt.CurrentAsync(async () =>
        {
            await using var write = new MeshlineDbContext(databaseOptions);
            var row = await write.SignedRequests.SingleAsync(value => value.Method == "account.route.publish", token).ConfigureAwait(false);
            if (row.Revision > sent.Revision) throw new InvalidOperationException("The route version changed while signing.");
            row.RelayId = relayId;
            row.DocumentJson = sent.ToJson();
            row.Revision = sent.Revision;
            row.Pending = true;
            row.RequestState = PublicationRequestState.Prepared;
            await write.SaveChangesAsync(token).ConfigureAwait(false);
        }, token).ConfigureAwait(false);
        var relay = await relayClients.GetAsync(relayId, signer, token).ConfigureAwait(false);
        await attempt.CheckAsync(token).ConfigureAwait(false);
        AccountRoute result;
        try
        {
            result = await relay.SendHttpAsync<AccountRoute>(HttpMethod.Put, "account.route.publish", sent, cancellationToken: token).ConfigureAwait(false);
            if ((result with { RelaySignature = null }).ToJson() != sent.ToJson()) throw new InvalidDataException("The relay modified the submitted route.");
            await VerifyRouteRelayAsync(result, token).ConfigureAwait(false);
        }
        catch (RelayException error) when (error.Error.IsDefinitiveRejection())
        {
            await attempt.CurrentAsync(() => StateUpdateJson.RejectAsync(databaseOptions, "account.route.publish", sent.ToJson()), CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (attempt.Intent["classify"]?.GetValue<bool>() == true && !token.IsCancellationRequested
            && error is HttpRequestException or TimeoutException or RelayException or InvalidDataException or CryptographicException or OperationCanceledException)
        {
            throw new AccountRouteException(AccountRouteFailure.SubmissionUnknown, "The route publication outcome is unknown; the latest state is retained.", error);
        }
        var changed = false;
        await attempt.CurrentAsync(async () =>
        {
            await _routeSaveGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await SaveRouteAsync(result, token).ConfigureAwait(false);
                lock (_routeCacheGate)
                {
                    var entry = GetRouteEntry(AccountId);
                    entry.Publication++;
                    entry.RefreshAfter = Clock.UtcNow + RouteFreshness;
                    changed = CacheRoute(entry, result);
                }
            }
            finally { _routeSaveGate.Release(); }
        }, token).ConfigureAwait(false);
        // Ensure raises its foreground notification after completion so callbacks can
        // publish again. Recovery has no foreground continuation to raise that event.
        if (changed && (attempt.IsBackground || attempt.Intent["classify"]?.GetValue<bool>() != true)) BackgroundFailure.Notify(() => AccountChanged?.Invoke(this, EventArgs.Empty));
        return result;
    }

    sealed class AccountStateUpdates(AccountManager owner, DatabaseOptions options) : StateUpdateCoordinator(options)
    {
        protected override string Operation => "account.route.publish";

        protected override async Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) =>
            await database.SignedRequests.FindAsync([Operation], token).ConfigureAwait(false);

        protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) =>
            database.SignedRequests.Add(new() { Method = Operation, RelayId = intent["relay"]?.GetValue<string>() ?? "", Revision = -1, RequestState = PublicationRequestState.None }).Entity;

        protected override async Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token)
        {
            var rows = await database.SignedRequests.AsNoTracking().Where(value => value.Method == Operation && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token).ConfigureAwait(false);
            return rows.Select(value => Publication(owner.AccountId, value)).ToList();
        }

        protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => owner.PublishLatestRouteAsync(attempt, token);

        protected override void Report(string resource, Exception error) => owner.ReportBackgroundError(BackgroundOperation.Synchronize, resource, error);

        protected override bool CanRetry(Exception error) =>
            base.CanRetry(error is AccountRouteException ? error.InnerException ?? error : error);
    }
}
