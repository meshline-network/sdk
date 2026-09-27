using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline;

sealed partial class MeshlineClient
{
    /// <summary>
    /// Migrates the current account's device state, route, and profile to another eligible home relay.
    /// </summary>
    /// <param name="relayId">The relay's canonical lowercase Neo script-hash identifier.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>A task that completes when the operation finishes.</returns>
    /// <remarks>
    /// Requires a known route, complete device state, and a local device. Progress is persisted for retry or startup resumption at the same target relay. This transfers authorization and profile state; it does not copy relay message history.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The operation is canceled through <paramref name="cancellationToken"/>, a component or relay lifetime ends, or a relay request times out.</exception>
    /// <exception cref="InvalidOperationException">This component or a required component has not completed initialization. A verified active relay, account signer, complete device state, or local-device authorization required by the operation is unavailable. The route changed concurrently, a pending migration targets another relay, the device is not yet valid, or staging leaves too little time to publish the route.</exception>
    /// <exception cref="ObjectDisposedException">This component, a required component, or the shared relay pool has been disposed.</exception>
    /// <exception cref="HttpRequestException">Relay discovery, authentication, or the HTTP request fails at the transport layer.</exception>
    /// <exception cref="RelayException">The relay rejects the operation with a structured protocol error that is not handled by this method.</exception>
    /// <exception cref="InvalidDataException">Relay evidence or returned state is missing, inconsistent, or fails protocol validation.</exception>
    /// <exception cref="JsonException">A stored or received protocol document cannot be serialized or deserialized.</exception>
    /// <exception cref="DecoderFallbackException">A relay response contains bytes that are not valid UTF-8.</exception>
    /// <exception cref="SqliteException">The SQLite database cannot be opened or a database command fails, for example because the schema is not migrated or the file is locked.</exception>
    /// <exception cref="DbUpdateException">Persisting local changes fails, including database constraint or optimistic-concurrency failures.</exception>
    /// <exception cref="CryptographicException">The local device key cannot be validated against its certificate, or cryptographic signing or verification fails.</exception>
    /// <exception cref="ArgumentException">The target relay, previous device state, or publication input is invalid or belongs to another account.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A certificate or route validity duration, or a selected publication revision, is outside its supported range.</exception>
    public async Task ChangeHomeRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref cancellationToken);
        await _accountGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var database = new MeshlineDbContext(_databaseOptions);
            var migration = await database.HomeRelayMigrations.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (migration is not null && migration.TargetRelayId != relayId)
                throw new InvalidOperationException("Resume the pending migration to its original target, or recover the account before choosing another target.");
            var route = await ReadMigrationRouteAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("No previous account route is available. Establish or recover the account instead.");
            if (migration is null && route.RelayId == relayId && route.ExpiresAt > Clock.UtcNow.ToUnixTimeSeconds()) return;
            await SelectRelayAsync(relayId, cancellationToken).ConfigureAwait(false);
            if (migration is null)
            {
                var state = await ReadMigrationDeviceStateAsync(route.RelayId, null, cancellationToken).ConfigureAwait(false);
                var profile = await ReadMigrationProfileAsync(cancellationToken).ConfigureAwait(false);
                if (Route is { } latest && latest.RelayId != route.RelayId)
                    throw new InvalidOperationException("The account route changed while preparing the migration. Retry using the newly resolved route.");
                migration = new()
                {
                    SourceRelayId = route.RelayId,
                    TargetRelayId = relayId,
                    DeviceStateJson = state.ToJson(),
                    ProfileJson = profile?.ToJson(),
                    RouteValiditySeconds = route.ExpiresAt - route.UpdatedAt
                };
                database.HomeRelayMigrations.Add(migration);
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            if (route.RelayId != migration.SourceRelayId && route.RelayId != migration.TargetRelayId)
                throw new InvalidOperationException("The account has moved to another relay since this migration began. Resolve the concurrent change or recover the account explicitly.");
            if (route.RelayId != relayId || route.ExpiresAt <= Clock.UtcNow.ToUnixTimeSeconds())
            {
                var state = ProtocolModel.FromJson<AccountDeviceState>(migration.DeviceStateJson)!;
                state = await ReadMigrationDeviceStateAsync(migration.SourceRelayId, state, cancellationToken).ConfigureAwait(false);
                var certificate = Device!;
                if (certificate.NotBefore > Clock.UtcNow.ToUnixTimeSeconds())
                    throw new InvalidOperationException("The local device certificate is not yet valid.");
                await PrepareLocalDeviceAsync(TimeSpan.FromSeconds(certificate.ExpiresAt - certificate.NotBefore), cancellationToken).ConfigureAwait(false);
                var publication = await DeviceManager.PublishDeviceStateAsync(relayId, options: new() { PreviousState = state }, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (publication.Status == DeviceStatePublishStatus.Staged)
                {
                    if (publication.StagedUntil!.Value - Clock.UtcNow < TimeSpan.FromMinutes(1))
                        throw new InvalidOperationException("The staged device state must remain available for at least one minute to publish the migration route.");
                }
                using var deadline = new CancellationTokenSource(publication.Status == DeviceStatePublishStatus.Staged ? TimeSpan.FromMinutes(1) : Timeout.InfiniteTimeSpan, Clock.Provider);
                using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                await AccountManager.PublishRouteAsync(relayId, TimeSpan.FromSeconds(migration.RouteValiditySeconds), cancellationToken: window.Token).ConfigureAwait(false);
            }
            await ConfirmLocalAuthorizationAsync(relayId, cancellationToken).ConfigureAwait(false);
            var restoredProfile = migration.ProfileJson is null ? null : ProtocolModel.FromJson<AccountProfile>(migration.ProfileJson);
            await ProfileManager.PublishProfileAsync(restoredProfile, cancellationToken).ConfigureAwait(false);
            database.HomeRelayMigrations.Remove(migration);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { _accountGate.Release(); }
    }

    async Task ResumeHomeRelayMigrationAsync(CancellationToken cancellationToken)
    {
        await using var database = new MeshlineDbContext(_databaseOptions);
        var relayId = await database.HomeRelayMigrations.Select(value => value.TargetRelayId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (relayId is not null) await ChangeHomeRelayAsync(relayId, cancellationToken).ConfigureAwait(false);
    }

    async Task<AccountRoute?> ReadMigrationRouteAsync(CancellationToken cancellationToken)
    {
        var previous = Route;
        try { return await AccountManager.GetRouteAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? previous; }
        catch (Exception exception) when (previous is not null && IsRelayUnavailable(exception, cancellationToken))
        {
            ReportBackgroundError(BackgroundOperation.Connect, previous.RelayId, exception);
            return previous;
        }
    }
}
