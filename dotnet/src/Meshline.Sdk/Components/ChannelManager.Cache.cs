using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;

namespace Meshline.Components;

sealed partial class ChannelManager
{
    readonly Lock _descriptorCacheGate = new();
    readonly Dictionary<ChannelRef, MetadataCache<ChannelResolveResult>> _descriptors = [];
    bool _descriptorsStopping;

    MetadataCache<ChannelResolveResult> DescriptorCache(ChannelRef channel)
    {
        lock (_descriptorCacheGate)
        {
            if (_descriptorsStopping) throw new OperationCanceledException("Channel metadata refreshes are stopping.");
            if (!_descriptors.TryGetValue(channel, out var cache)) _descriptors.Add(channel, cache = new());
            return cache;
        }
    }

    async Task LoadDescriptorsAsync(MeshlineDbContext database, CancellationToken cancellationToken)
    {
        foreach (var channel in await database.Channels.AsNoTracking().Where(value => value.DescriptorJson != null).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            var row = await database.ChannelDescriptors.FindAsync([channel.ChannelId, channel.Revision], cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The persisted channel descriptor evidence is missing.");
            var reference = new ChannelRef { ChannelId = channel.ChannelId, RelayId = channel.RelayId };
            var result = new ChannelResolveResult { Descriptor = ProtocolModel.FromJson<ChannelDescriptor>(row.DocumentJson)!, SignerCertificate = ProtocolModel.FromJson<DeviceCertificate>(row.CertificateJson)! };
            ValidateDescriptor(reference, result);
            if (result.Descriptor.Revision != channel.Revision || row.DocumentJson != channel.DescriptorJson) throw new InvalidDataException("The persisted channel revision is inconsistent.");
            _descriptors[reference] = new(result);
        }
    }

    Task<ChannelResolveResult?> ReadDescriptorAsync(ChannelRef channel, bool force, CancellationToken cancellationToken) =>
        DescriptorCache(channel).ReadAsync(_ => true, async token =>
        {
            using var operation = BeginOperation(ref token);
            var relay = await GetHostingRelayAsync(channel.RelayId, token).ConfigureAwait(false);
            var result = await ResolveDescriptorAsync(relay, channel, null, token).ConfigureAwait(false);
            await SaveDescriptorAsync(channel, result, token).ConfigureAwait(false);
            var current = DescriptorCache(channel).Value;
            if (current is not null && result.Descriptor.Revision < current.Descriptor.Revision) throw new InvalidDataException("The channel descriptor is older than the known revision.");
            return current ?? result;
        }, error => ReportBackgroundError(BackgroundOperation.Synchronize, channel.ChannelId, error), cancellationToken, force);

    // Called while holding the storage gate, including after timeline verification and local publication.
    void CacheDescriptor(ChannelRef channel, ChannelResolveResult result, DateTimeOffset? refreshedAt = null)
    {
        var cache = DescriptorCache(channel);
        if (cache.Value is not { } previous || result.Descriptor.Revision >= previous.Descriptor.Revision) cache.Publish(result, refreshedAt);
    }

    /// <summary>Awaits channel descriptor refresh, bypassing the success interval but retaining the one-minute failure cooldown.</summary>
    /// <inheritdoc cref="GetChannelAsync" path="/*[self::param or self::returns or self::exception]"/>
    public async Task<ChannelInfo> RefreshChannelAsync(ChannelRef channel, CancellationToken cancellationToken = default)
    {
        EnsureInitialized(); using var operation = BeginOperation(ref cancellationToken);
        await ReadDescriptorAsync(channel, true, cancellationToken).ConfigureAwait(false);
        return await ReadChannelAsync(channel, cancellationToken).ConfigureAwait(false);
    }

    async Task StopDescriptorsAsync(bool dispose = false)
    {
        Task pending;
        lock (_descriptorCacheGate) { _descriptorsStopping = true; pending = Task.WhenAll(_descriptors.Values.Select(cache => cache.StopAsync(dispose))); }
        try { await pending.ConfigureAwait(false); }
        finally { lock (_descriptorCacheGate) _descriptorsStopping = dispose; }
    }
}
