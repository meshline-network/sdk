using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Meshline.Components;

sealed partial class ChannelManager
{
    ChannelStateUpdates? _updates;
    ChannelStateUpdates Updates => _updates ??= new ChannelStateUpdates(this, databaseOptions);

    async Task<ChannelInfo> UpdateLatestChannelAsync(ChannelRef channel, ChannelUpdate update, CancellationToken token)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref token);
        ArgumentNullException.ThrowIfNull(update);
        if (update.Name.IsDeleted) throw new ArgumentException("A channel name cannot be deleted.", nameof(update));
        if (Identifiers.ValidateChannelId(channel.ChannelId) is { } invalidId) throw new ArgumentException(invalidId.Message, nameof(channel));
        var patch = new JsonObject { ["relay"] = channel.RelayId, ["channel"] = channel.ChannelId };
        StateUpdateJson.Field(patch, "name", update.Name);
        StateUpdateJson.Field(patch, "description", update.Description);
        StateUpdateJson.Field(patch, "moderators", update.Moderators);
        // Validate supplied properties without requiring a locally cached descriptor.
        var nonce = new byte[16];
        var draft = new ChannelDescriptor
        {
            ChannelId = Identifiers.DeriveChannelId(Options.AccountId, channel.RelayId, nonce, Context),
            Nonce = [.. nonce],
            Creator = Options.AccountId,
            RelayId = channel.RelayId,
            Name = "channel",
            Revision = 0,
            CreatedAt = 0,
            UpdatedAt = 0,
            Status = ChannelStatus.Active,
            DeviceSignature = [.. new byte[64]]
        };
        var candidate = ProtocolModel.FromJson<ChannelDescriptor>(StateUpdateJson.Apply(draft, patch, "name", "description", "moderators").ToJsonString())!;
        if (candidate.Validate(Context) is { } invalid) throw new ArgumentException(invalid.Message, nameof(update));
        return await Updates.SubmitAsync<ChannelInfo>(channel.ChannelId, patch, token).ConfigureAwait(false);
    }

    async Task<object?> PublishLatestChannelAsync(StateUpdateAttempt attempt, CancellationToken token)
    {
        var channel = new ChannelRef { ChannelId = attempt.Intent["channel"]!.GetValue<string>(), RelayId = attempt.Intent["relay"]!.GetValue<string>() };
        var relay = await GetHostingRelayAsync(channel.RelayId, token).ConfigureAwait(false);
        for (var retry = 0; ; retry++)
        {
            var resolved = await ResolveDescriptorAsync(relay, channel, null, token).ConfigureAwait(false);
            var current = resolved.Descriptor;
            var retained = attempt.RequestJson is { } json ? ProtocolModel.FromJson<ChannelDescriptor>(json)! : null;
            if (retained is not null && current.Revision >= retained.Revision)
            {
                var accepted = await ResolveDescriptorAsync(relay, channel, retained.Revision, token).ConfigureAwait(false);
                if (accepted.Descriptor.ToJson() == retained.ToJson())
                {
                    return await SaveLatestChannelAsync(attempt, channel, resolved, token).ConfigureAwait(false);
                }
            }
            EnsureCanWrite(current, ownerOnly: true);
            var request = retained is not null && retained.Revision == current.Revision + 1 ? retained : null;
            if (request is null)
            {
                var next = StateUpdateJson.Apply(current, attempt.Intent, "name", "description", "moderators");
                next["revision"] = checked(current.Revision + 1);
                next["updated_at"] = Clock.UtcNow.ToUnixTimeSeconds();
                request = await SignAsync(ProtocolModel.FromJson<ChannelDescriptor>(next.ToJsonString())!, token).ConfigureAwait(false);
                if (request.Validate(Context) is { } invalid) throw new ArgumentException(invalid.Message);
                await attempt.SaveRequestAsync(request.ToJson(), token).ConfigureAwait(false);
            }
            try
            {
                await attempt.CheckAsync(token).ConfigureAwait(false);
                await relay.SendHttpAsync(HttpMethod.Put, "channel.update", request, cancellationToken: token).ConfigureAwait(false);
            }
            catch (RelayException error) when (error.Error.Code == "state_conflict" && retry < 2) { continue; }
            catch (RelayException error) when (error.Error.Code == "clock_skew")
            {
                // This request was definitively rejected; refresh its timestamp on retry.
                await attempt.InvalidateRequestAsync(request.ToJson()).ConfigureAwait(false);
                throw;
            }
            QueueRefresh();
            return await SaveLatestChannelAsync(attempt, channel, new() { Descriptor = request, SignerCertificate = Certificate }, token).ConfigureAwait(false);
        }
    }

    async Task<ChannelInfo> SaveLatestChannelAsync(StateUpdateAttempt attempt, ChannelRef channel, ChannelResolveResult result, CancellationToken token)
    {
        await using var database = new MeshlineDbContext(databaseOptions);
        var previous = await database.Channels.Where(value => value.ChannelId == channel.ChannelId).Select(value => value.DescriptorJson).SingleOrDefaultAsync(token).ConfigureAwait(false);
        await attempt.CurrentAsync(() => SaveDescriptorAsync(channel, result, token, notify: false, published: true), token).ConfigureAwait(false);
        var info = await ReadChannelAsync(channel, token).ConfigureAwait(false);
        if (previous != info.Descriptor?.ToJson()) ChannelChanged?.Invoke(this, info);
        return info;
    }

    sealed class ChannelStateUpdates(ChannelManager owner, DatabaseOptions options) : StateUpdateCoordinator(options)
    {
        protected override string Operation => "channel.update";

        protected override async Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) =>
            await database.ChannelOperations.SingleOrDefaultAsync(value => value.ResourceId == resource && value.Method == Operation, token).ConfigureAwait(false);

        protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) =>
            database.ChannelOperations.Add(new() { ResourceId = resource, Method = Operation, RelayId = intent["relay"]!.GetValue<string>(), RequestState = PublicationRequestState.None }).Entity;

        protected override async Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token)
        {
            var rows = await database.ChannelOperations.AsNoTracking().Where(value => value.Method == Operation && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token).ConfigureAwait(false);
            return rows.Select(value => Publication(value.ResourceId, value)).ToList();
        }

        protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => owner.PublishLatestChannelAsync(attempt, token);

        protected override void Report(string resource, Exception error) => owner.ReportBackgroundError(BackgroundOperation.Synchronize, resource, error);
    }
}
