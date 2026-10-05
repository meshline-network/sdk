using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Models.Registry;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Components;

/// <summary>
/// Resolves and publishes signed account routes and retains the current account's known route.
/// </summary>
/// <param name="options">The network and account configuration for this component.</param>
/// <param name="databaseOptions">The SQLite database configuration; create its parent directory and apply migrations before initialization.</param>
/// <param name="relayClients">The shared relay pool. The application owns it and must dispose it after all dependent components.</param>
/// <param name="accountSigner">The application-owned account signer required for account-authorized operations, or <see langword="null"/> when those operations are not needed.</param>
/// <exception cref="ArgumentNullException">The network context in <paramref name="options"/> is null. The <paramref name="options"/> argument is null.</exception>
/// <exception cref="ArgumentException">The configured account identifier is invalid.</exception>
/// <exception cref="NotSupportedException">The configured account identifier uses an unsupported account namespace.</exception>
public sealed class AccountManager(ClientOptions options, DatabaseOptions databaseOptions, RelayClientPool relayClients, IAccountSigner? accountSigner = null) : ClientComponent(options)
{
    /// <summary>
    /// Occurs when the current account's known route or establishment state changes.
    /// </summary>
    public event EventHandler? AccountChanged;

    private readonly SemaphoreSlim _routeGate = new(1, 1);

    /// <summary>
    /// The account's CAIP-10 identifier.
    /// </summary>
    public string AccountId => Options.AccountId;
    /// <summary>
    /// The currently known account establishment state.
    /// </summary>
    public AccountState State { get; private set; } = AccountState.Unknown;
    /// <summary>
    /// The current account's last known route, or <see langword="null"/> when no route is known.
    /// </summary>
    public AccountRoute? Route { get; private set; }

    /// <inheritdoc/>
    /// <exception cref="OperationCanceledException">The operation observes cancellation of <paramref name="cancellationToken"/>. Disposal of the component or relay session can also cancel pending work.</exception>
    /// <exception cref="InvalidOperationException">The database is bound to another network or account, or a required dependency has not been initialized.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    protected override async Task OnInitializeAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        await EnsureDatabaseBindingAsync(database, cancellationToken).ConfigureAwait(false);
        var route = await database.AccountRoutes.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, cancellationToken).ConfigureAwait(false);
        Route = route is null ? null : ProtocolModel.FromJson<AccountRoute>(route.DocumentJson);
        State = Route is { } current && current.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds() ? AccountState.Established : AccountState.Unknown;
    }

    /// <summary>
    /// Resolves and verifies an account route and updates the current account's known route when applicable.
    /// </summary>
    /// <param name="accountId">The target account identifier, or <see langword="null"/> for the current account.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The verified route, or <see langword="null"/> when no route can be resolved.</returns>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/> or the component lifetime ends. Per-relay timeouts encountered during discovery are collected as transport failures.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. No active relay is available for route discovery.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">No attempted relay can complete route discovery; individual transient or verification failures are available in the inner aggregate exception.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">A resolved route conflicts with or predates the route already stored locally.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="ArgumentException">The requested account identifier is invalid.</exception>
    /// <exception cref="NotSupportedException">The requested account uses an unsupported account namespace.</exception>
    public async Task<AccountRoute?> GetRouteAsync(string? accountId = null, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        accountId ??= AccountId;
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        bool changed = false;
        AccountRoute? route;
        try
        {
            route = await ResolveRouteAsync(accountId, cancellationToken).ConfigureAwait(false);
            if (accountId == AccountId)
            {
                var state = route is null ? AccountState.Unknown : AccountState.Established;
                changed = route is not null && Route?.ToJson() != route.ToJson() || State != state;
                Route = route ?? Route;
                State = state;
            }
        }
        finally
        {
            _routeGate.Release();
        }
        if (changed)
            AccountChanged?.Invoke(this, EventArgs.Empty);
        return route;
    }

    /// <summary>
    /// Signs and publishes a newer home-relay route using the account signer.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="validity">The validity duration, from one second through 3650 days.</param>
    /// <param name="revision">An explicit newer revision, or <see langword="null"/> for automatic revision selection.</param>
    /// <param name="isRecovery">Whether to select a recovery revision when no revision is supplied.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The published route with the relay's verified signature.</returns>
    /// <exception cref="OperationCanceledException">The caller cancels the operation or a component or relay lifetime ends.</exception>
    /// <exception cref="TimeoutException">An SDK request deadline expires. Data contains operation and timeoutSeconds; InnerException preserves the cancellation cause.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. An account signer is unavailable, the relay is not active, or an unresolved publication would be replaced by different input.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The account signer produced a route whose identity or account signature is invalid, or signature verification fails.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The route validity is outside one second through 3650 days, or the selected revision is not a newer nonnegative safe integer.</exception>
    /// <exception cref="ArgumentException">The relay identifier is invalid or the account signer belongs to another account.</exception>
    public async Task<AccountRoute> PublishRouteAsync(string relayId, TimeSpan validity, long? revision = null, bool isRecovery = false, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var _ = BeginOperation(ref cancellationToken);
        if (validity < TimeSpan.FromSeconds(1) || validity > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(validity));
        var signer = accountSigner ?? throw new InvalidOperationException("An account signer is required to publish an account route.");
        await _routeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        AccountRoute result;
        bool changed;
        try
        {
            await using var database = new MeshlineDbContext(databaseOptions);
            var signed = await database.SignedRequests.SingleOrDefaultAsync(value => value.Method == "account.route.publish", cancellationToken).ConfigureAwait(false);
            var known = await database.AccountRoutes.AsNoTracking().SingleOrDefaultAsync(value => value.AccountId == AccountId, cancellationToken).ConfigureAwait(false);
            var knownRevision = Math.Max(known?.Revision ?? -1, signed?.Revision ?? -1);
            var pending = signed is { Pending: true } ? ProtocolModel.FromJson<AccountRoute>(signed.DocumentJson)! : null;
            if (isRecovery && revision is null && (pending is null || signed!.RelayId != relayId || pending.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds()))
                revision = Math.Max(knownRevision + 1, Clock.UtcNow.ToUnixTimeMilliseconds());
            else if (revision is null && pending is not null && signed!.RelayId == relayId && pending.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
                revision = AccountRoute.GetNextRevision(null, knownRevision);
            AccountRoute request;
            if (signed is { Pending: true } && (revision is null || revision == signed.Revision))
            {
                request = ProtocolModel.FromJson<AccountRoute>(signed.DocumentJson)!;
                if (signed.RelayId != relayId || revision is { } specified && specified != request.Revision || request.ExpiresAt - request.UpdatedAt != (long)validity.TotalSeconds)
                    throw new InvalidOperationException("A route publication has an unknown result. Retry the original publication or resolve the account route first.");
            }
            else
            {
                var next = AccountRoute.GetNextRevision(revision, knownRevision);
                var now = Clock.UtcNow.ToUnixTimeSeconds();
                request = new AccountRoute { Account = AccountId, AccountPublicKey = signer.PublicKey, Revision = next, RelayId = relayId, UpdatedAt = now, ExpiresAt = checked(now + (long)validity.TotalSeconds), AccountSignature = [] };
                request = request with { AccountSignature = [.. await signer.SignAsync(request.GetAccountSigningInput(Context), cancellationToken).ConfigureAwait(false)] };
                if (request.Validate(Context) is { } violation)
                    throw new CryptographicException(violation.Message);
                if (signed is null)
                {
                    signed = new SignedRequestRecord { Method = "account.route.publish", RelayId = relayId, DocumentJson = request.ToJson(), Revision = next, Pending = true };
                    database.SignedRequests.Add(signed);
                }
                else
                {
                    signed.RelayId = relayId;
                    signed.DocumentJson = request.ToJson();
                    signed.Revision = next;
                    signed.Pending = true;
                }
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            var relayClient = await relayClients.GetAsync(relayId, signer, cancellationToken).ConfigureAwait(false);
            try
            {
                result = await relayClient.SendHttpAsync<AccountRoute>(HttpMethod.Put, "account.route.publish", request, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RelayException exception) when (exception.Error.IsDefinitiveRejection())
            {
                signed.Pending = false;
                await database.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            if ((result with { RelaySignature = null }).ToJson() != request.ToJson())
                throw new InvalidDataException("The relay modified the submitted route instead of only adding its signature.");
            await VerifyRouteRelayAsync(result, cancellationToken).ConfigureAwait(false);
            await SaveRouteAsync(result, cancellationToken).ConfigureAwait(false);
            changed = Route?.ToJson() != result.ToJson();
            Route = result;
            State = AccountState.Established;
        }
        finally
        {
            _routeGate.Release();
        }
        if (changed)
            AccountChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    async Task<AccountRoute?> ResolveRouteAsync(string accountId, CancellationToken cancellationToken)
    {
        if (AccountAdapter.ValidateAccountId(accountId) is { } violation)
            throw new ArgumentException(violation.Message, nameof(accountId));
        long? knownRevision;
        await using (var database = new MeshlineDbContext(databaseOptions))
            knownRevision = await database.AccountRoutes.Where(value => value.AccountId == accountId).Select(value => (long?)value.Revision).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var attempted = false;
        List<Exception> failures = [];
        await foreach (var entry in relayClients.Registry.GetRelaysAsync(cancellationToken).ConfigureAwait(false))
        {
            if (entry.Status != RelayStatus.Active)
                continue;
            attempted = true;
            AccountRoute route;
            try
            {
                var relayClient = await relayClients.GetAsync(entry.RelayId, cancellationToken).ConfigureAwait(false);
                route = await relayClient.SendHttpAsync<AccountRoute>(HttpMethod.Get, "account.route.resolve", new AccountQuery { Account = accountId }, authenticated: false, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (route.Account != accountId)
                    throw new InvalidDataException("The resolved route belongs to another account.");
                if (knownRevision is { } revision && route.Revision < revision)
                    throw new InvalidDataException("A relay returned a route older than the known revision.");
                await VerifyRouteRelayAsync(route, cancellationToken).ConfigureAwait(false);
            }
            catch (RelayException exception) when (exception.Error.Code == "not_found")
            {
                continue;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && (exception is HttpRequestException or InvalidDataException or JsonException or CryptographicException or OperationCanceledException or TimeoutException
                || exception is RelayException { Error.Code: "temporarily_unavailable" or "bad_gateway" or "rate_limited" }))
            {
                failures.Add(exception);
                ReportBackgroundError(BackgroundOperation.Connect, entry.RelayId, exception);
                continue;
            }
            await SaveRouteAsync(route, cancellationToken).ConfigureAwait(false);
            return route;
        }
        if (failures.Count > 0)
            throw new HttpRequestException("Account route discovery could not complete through the available relays.", new AggregateException(failures));
        if (!attempted)
            throw new InvalidOperationException("No active relay is available for route discovery.");
        return null;
    }

    async Task VerifyRouteRelayAsync(AccountRoute route, CancellationToken cancellationToken)
    {
        var relayClient = await relayClients.GetAsync(route.RelayId, cancellationToken).ConfigureAwait(false);
        var descriptor = await relayClient.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
        if (route.RelaySignature is not { } signature || !RelayIdentity.VerifySignature(descriptor.PublicKey.AsSpan(), route.GetRelaySigningInput(relayClients.Context), signature.AsSpan()))
            throw new InvalidDataException("The account route has no valid signature from its home relay.");
    }

    async Task SaveRouteAsync(AccountRoute route, CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var record = await database.AccountRoutes.SingleOrDefaultAsync(value => value.AccountId == route.Account, cancellationToken).ConfigureAwait(false);
        if (record is not null)
        {
            var previous = ProtocolModel.FromJson<AccountRoute>(record.DocumentJson)!;
            if (route.CompareWith(previous) is RouteComparison.Older or RouteComparison.Conflict)
                throw new InvalidDataException("The resolved route is older than the known route or conflicts at the same revision.");
            record.DocumentJson = route.ToJson();
            record.Revision = route.Revision;
        }
        else
            database.AccountRoutes.Add(new AccountRouteRecord { AccountId = route.Account, DocumentJson = route.ToJson(), Revision = route.Revision });
        var pending = await database.SignedRequests.SingleOrDefaultAsync(value => value.Method == "account.route.publish", cancellationToken).ConfigureAwait(false);
        if (pending is { Pending: true } && ProtocolModel.FromJson<AccountRoute>(pending.DocumentJson) is { } request && request.Account == route.Account
            && route.CompareWith(request) is RouteComparison.Equivalent or RouteComparison.Newer)
            pending.Pending = false;
        await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
