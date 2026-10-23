using Meshline.Identity;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Meshline.Components;

sealed partial class AccountManager
{
    // Local preflight only: callers can retain an unfinished request's parameters
    // before recording new work. EnsureRouteAsync rechecks under its publication gate.
    internal async Task<AccountRoute?> GetUnconfirmedRouteAsync(CancellationToken cancellationToken)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await using var database = new MeshlineDbContext(databaseOptions);
        var publication = await database.SignedRequests.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Method == "account.route.publish", cancellationToken).ConfigureAwait(false);
        var request = publication is null ? null : ProtocolModel.FromJson<AccountRoute>(publication.DocumentJson)!;
        var retained = Route;
        return request is not null && (retained is null || retained.CompareWith(request) is RouteComparison.Older or RouteComparison.Conflict)
            ? request : null;
    }

    /// <summary>Ensures a valid route to the same home relay, reconciling unfinished publications before reporting success.</summary>
    /// <remarks>
    /// An unexpired verified route with no unresolved publication is reused without network I/O or signing.
    /// Before publication, discovery must complete successfully. A missing response does not invalidate an unexpired retained route.
    /// Unexpired unfinished submissions retain their original signed document. After discovery, an expired request can be superseded only with a retained verified route to the same relay; an initial request alone does not establish that relay's authority.
    /// First publication requires authoritative or unexpired staged device state at the relay; use MeshlineClient.EstablishAccountAsync for complete setup.
    /// This operation does not authorize a device, initialize a profile, migrate relays, or start synchronization.
    /// Use one account manager per local account session; explicit publications and ensure calls share its publication gate.
    /// </remarks>
    /// <param name="relayId">The original or initial home relay's canonical identifier; this operation never selects another relay.</param>
    /// <param name="validity">The lifetime of a newly published route, from one second through 3650 days. Existing valid routes are retained; unfinished requests require their original lifetime.</param>
    /// <param name="cancellationToken">A token that cancels waiting or publication. Cancellation does not undo an accepted request; repeat the operation to reconcile it.</param>
    /// <returns>The verified, unexpired route after any unfinished publication has been reconciled.</returns>
    /// <exception cref="AccountRouteException">Route authorization could not complete. The reason distinguishes signing, relay, parameter, state and unknown-submission failures.</exception>
    /// <exception cref="ArgumentException">The relay identifier is invalid.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The validity is outside the supported range or the route revision cannot be incremented.</exception>
    /// <exception cref="OperationCanceledException">The caller cancels or the component is disposed.</exception>
    /// <exception cref="ObjectDisposedException">The component or a dependency has been disposed.</exception>
    /// <exception cref="InvalidOperationException">Initialization is incomplete or no active relay is available.</exception>
    /// <exception cref="DbUpdateException">Local publication state cannot be committed.</exception>
    public async Task<AccountRoute> EnsureRouteAsync(string relayId, TimeSpan validity, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        if (RelayIdentity.ValidateRelayId(relayId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(relayId));
        if (validity < TimeSpan.FromSeconds(1) || validity > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(validity));
        var refresh = false;
        long? attemptedRevision = null;
        try
        {
            for (var attempt = 0; ;)
            {
                // Discovery invokes callbacks outside the publication gate. A callback
                // may publish a newer route; decisions below always use retained state.
                var beforeRefresh = Route;
                AccountRoute? resolved = null;
                if (refresh)
                    resolved = await RefreshRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                var observed = Route;
                (AccountRoute Route, bool Changed) result;
                await _publicationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await using var database = new MeshlineDbContext(databaseOptions);
                    var publication = await database.SignedRequests.AsNoTracking()
                        .SingleOrDefaultAsync(value => value.Method == "account.route.publish", cancellationToken).ConfigureAwait(false);
                    var retained = Route;
                    if (retained is not null && retained.RelayId != relayId)
                        throw new AccountRouteException(AccountRouteFailure.RelayMismatch, "The home relay changed during authorization.");
                    if (refresh && retained?.ToJson() != observed?.ToJson()) continue;
                    var request = publication is null ? null : ProtocolModel.FromJson<AccountRoute>(publication.DocumentJson)!;
                    var unresolved = request is not null && (retained is null || retained.CompareWith(request) is RouteComparison.Older or RouteComparison.Conflict);
                    if (!unresolved && retained is not null && retained.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds())
                    {
                        // Discovery may have completed before a concurrent publication
                        // was confirmed. Its missing result applies only to the old route.
                        if (refresh && resolved is null && retained.ToJson() == beforeRefresh?.ToJson())
                            throw new AccountRouteException(AccountRouteFailure.StateUnavailable, "Discovery did not confirm the retained valid route. No replacement was published.");
                        return retained;
                    }
                    if (!refresh)
                    {
                        refresh = true;
                        continue;
                    }
                    long revision;
                    if (unresolved)
                    {
                        if (publication!.RelayId != relayId)
                            throw new AccountRouteException(AccountRouteFailure.RelayMismatch, "The unfinished route publication targets another home relay.");
                        if (request!.ExpiresAt - request.UpdatedAt != (long)validity.TotalSeconds)
                            throw new AccountRouteException(AccountRouteFailure.OperationConflict, "Retry the unfinished route publication with its original validity.");
                        if (request.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds() && retained is null)
                            throw new AccountRouteException(AccountRouteFailure.StateConflict,
                                "The initial route request expired without a verified home route. Its revision cannot be advanced without original route evidence.");
                        // A higher revision supersedes either outcome of an expired
                        // request without assuming it failed. Keep this floor even if
                        // a successor was rejected: its predecessor may have succeeded.
                        revision = request.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()
                            ? checked(Math.Max(retained?.Revision ?? -1, publication.Revision) + 1) : publication.Revision;
                    }
                    else
                        revision = checked((retained?.Revision ?? -1) + 1);
                    // Retry a definitively rejected document only on a later explicit
                    // call, allowing corrected relay preconditions without a new revision.
                    if (attemptedRevision == revision)
                        throw new AccountRouteException(AccountRouteFailure.StateConflict, "The relay rejected this revision and discovery has not revealed a newer authoritative route.");
                    if (accountSigner is null)
                        throw new AccountRouteException(AccountRouteFailure.SignerRequired, "An account signer is required to authorize the route.");
                    try
                    {
                        result = await PublishRouteCoreAsync(relayId, validity, revision, false, cancellationToken,
                            retryRejected: unresolved, classifySubmissionFailure: true).ConfigureAwait(false);
                    }
                    catch (RelayException exception) when (attempt < 2 && exception.Error.Code is "stale_state" or "state_conflict")
                    {
                        attemptedRevision = revision;
                        attempt++;
                        continue;
                    }
                }
                finally { _publicationGate.Release(); }
                if (result.Changed) AccountChanged?.Invoke(this, EventArgs.Empty);
                // A notification can publish a newer route before this call returns.
                var confirmed = Route!;
                if (confirmed.RelayId != relayId)
                    throw new AccountRouteException(AccountRouteFailure.RelayMismatch, "The home relay changed during authorization.");
                if (confirmed.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
                    throw new AccountRouteException(AccountRouteFailure.StateUnavailable, "The confirmed route expired before authorization completed.");
                return confirmed;
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AccountRouteException(AccountRouteFailure.SignatureDeclined, "Account route authorization was declined.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or RelayException or InvalidDataException or CryptographicException
            || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            var failure = exception is RelayException { Error.Code: "stale_state" or "state_conflict" } ? AccountRouteFailure.StateConflict
                : AccountRouteFailure.StateUnavailable;
            throw new AccountRouteException(failure, "Route authorization did not complete. The retained route and signed request have been preserved.", exception);
        }
    }
}
