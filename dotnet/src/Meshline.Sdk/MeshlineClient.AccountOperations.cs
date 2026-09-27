using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Models.Registry;
using Meshline.Transport;

namespace Meshline;

sealed partial class MeshlineClient
{
    async Task<DeviceCertificate> PrepareLocalDeviceAsync(TimeSpan validity, CancellationToken cancellationToken)
    {
        if (Device is null) return await DeviceManager.CreateDeviceAsync(validity, cancellationToken).ConfigureAwait(false);
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        return Device.NotBefore <= now && Device.ExpiresAt > now ? Device : await DeviceManager.RenewDeviceAsync(validity, cancellationToken).ConfigureAwait(false);
    }

    async Task ConfirmLocalAuthorizationAsync(string relayId, CancellationToken cancellationToken)
    {
        var certificate = Device ?? throw new InvalidOperationException("No local device has been created.");
        if (await DeviceManager.GetOwnDeviceStateAsync(relayId, cancellationToken).ConfigureAwait(false) is null)
            throw new InvalidDataException("The home relay returned no device state after account publication.");
        if (DeviceManager.GetAuthorizationState(certificate.GetDeviceId(Context)) != DeviceAuthorizationState.Authorized)
            throw new InvalidDataException("The home relay has not authorized the local device.");
        await _relayClients.GetAsync(relayId, DeviceManager, cancellationToken).ConfigureAwait(false);
    }

    async Task<AccountDeviceState> ReadMigrationDeviceStateAsync(string sourceRelayId, AccountDeviceState? fallback, CancellationToken cancellationToken)
    {
        try { await DeviceManager.GetOwnDeviceStateAsync(sourceRelayId, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (IsRelayUnavailable(exception, cancellationToken))
        {
            ReportBackgroundError(BackgroundOperation.Connect, sourceRelayId, exception);
        }
        var state = DeviceState is { } current && (fallback is null || current.Revision > fallback.Revision) ? current : fallback;
        if (state is null)
            throw new InvalidOperationException("The complete previous device state is unavailable. Recover the account explicitly instead of discarding its other devices.");
        var certificate = Device ?? throw new InvalidOperationException("A local device is required to migrate the account.");
        if (!state.Certificates.Any(value => value.GetDeviceId(Context) == certificate.GetDeviceId(Context)))
            throw new InvalidOperationException("Authorize the local device or recover the account before migrating.");
        return state;
    }

    async Task<AccountProfile?> ReadMigrationProfileAsync(CancellationToken cancellationToken)
    {
        var previous = Profile;
        try { return await ProfileManager.GetProfileAsync(cancellationToken: cancellationToken).ConfigureAwait(false) ?? previous; }
        catch (Exception exception) when (IsRelayUnavailable(exception, cancellationToken))
        {
            ReportBackgroundError(BackgroundOperation.Connect, Route?.RelayId, exception);
            return previous;
        }
    }

    static bool IsRelayUnavailable(Exception exception, CancellationToken cancellationToken) => !cancellationToken.IsCancellationRequested
        && (exception is HttpRequestException or OperationCanceledException || exception is RelayException { Error.Code: "temporarily_unavailable" or "bad_gateway" or "rate_limited" or "route_stale" or "target_not_local" or "route_not_found" });

    async Task<string> SelectRelayAsync(string? relayId, CancellationToken cancellationToken)
    {
        if (relayId is not null)
        {
            var client = await _relayClients.GetAsync(relayId, cancellationToken).ConfigureAwait(false);
            await client.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
            return relayId;
        }
        List<string> candidates = [];
        await foreach (var entry in _relayClients.Registry.GetRelaysAsync(cancellationToken).ConfigureAwait(false))
            if (entry.Status == RelayStatus.Active) candidates.Add(entry.RelayId);
        var ids = candidates.Distinct(StringComparer.Ordinal).ToArray();
        Random.Shared.Shuffle(ids);
        List<Exception> failures = [];
        foreach (var id in ids)
        {
            try
            {
                var client = await _relayClients.GetAsync(id, cancellationToken).ConfigureAwait(false);
                await client.GetDescriptorAsync(cancellationToken).ConfigureAwait(false);
                return id;
            }
            catch (Exception exception) when (exception is HttpRequestException or InvalidDataException or RelayException || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                failures.Add(exception);
            }
        }
        throw new InvalidOperationException("No verified active relay is available.", failures.Count == 0 ? null : new AggregateException(failures));
    }

}
