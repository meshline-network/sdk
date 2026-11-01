using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Collections.Immutable;
using System.Text.Json.Nodes;
using ProtocolUpdate = Meshline.Models.Protocol.GroupUpdate;
using ClientUpdate = Meshline.Models.Client.GroupUpdate;

namespace Meshline.Components;

sealed partial class GroupManager
{
    GroupStateUpdates? _updates;
    GroupStateUpdates Updates => _updates ??= new GroupStateUpdates(this, databaseOptions);

    async Task<GroupInfo> UpdateLatestGroupAsync(GroupRef group, ClientUpdate update, CancellationToken token)
    {
        EnsureInitialized();
        using var operation = BeginOperation(ref token);
        ArgumentNullException.ThrowIfNull(update);
        // Protocol serialization preserves omitted fields and explicit deletions, including enum wire names.
        var draft = new ProtocolUpdate
        {
            GroupId = group.GroupId,
            PrevHash = "sha256:" + new string('A', 43),
            Name = update.Name,
            Description = update.Description,
            MemberCapacity = update.MemberCapacity,
            InvitePolicy = update.InvitePolicy,
            DeviceSignature = new byte[64].ToImmutableArray()
        };
        // Validate field values and required-field deletions before admission;
        // the actual management hash and permissions are checked during execution.
        if (draft.Validate(Context) is { } invalid) throw new ArgumentException(invalid.Message, nameof(update));
        var patch = JsonNode.Parse(draft.ToJson())!.AsObject();
        patch.Remove("prev_hash");
        patch.Remove("device_signature");
        patch["relay"] = group.RelayId;
        return await Updates.SubmitAsync<GroupInfo>(group.GroupId, patch, token).ConfigureAwait(false);
    }

    async Task<object?> PublishLatestGroupAsync(StateUpdateAttempt attempt, CancellationToken token)
    {
        var group = new GroupRef { GroupId = attempt.Intent["group_id"]!.GetValue<string>(), RelayId = attempt.Intent["relay"]!.GetValue<string>() };
        var relay = await GetRelayAsync(group.RelayId, token).ConfigureAwait(false);
        for (var retry = 0; ; retry++)
        {
            await SynchronizeLatestGroupAsync(group, relay, token).ConfigureAwait(false);
            await using var read = new MeshlineDbContext(databaseOptions);
            var state = await read.Groups.AsNoTracking().SingleAsync(value => value.GroupId == group.GroupId, token).ConfigureAwait(false);
            var retained = attempt.RequestJson is { } json ? ProtocolModel.FromJson<ProtocolUpdate>(json)! : null;
            if (retained is not null)
            {
                await using var database = new MeshlineDbContext(databaseOptions);
                if (await database.GroupEvents.AnyAsync(value => value.GroupId == group.GroupId && value.PayloadJson == retained.ToJson() && value.Rejection == null, token).ConfigureAwait(false))
                    return await ReadGroupAsync(group, token).ConfigureAwait(false);
            }
            var request = retained?.PrevHash == state.ManagementHash ? retained : null;
            if (request is null)
            {
                var patch = (JsonObject)attempt.Intent.DeepClone();
                patch.Remove("relay");
                patch["prev_hash"] = state.ManagementHash;
                patch["device_signature"] = "";
                request = (ProtocolUpdate)await SignAsync(ProtocolModel.FromJson<ProtocolUpdate>(patch.ToJsonString())!, token).ConfigureAwait(false);
                if (!await RunAsync(scope => TryCheckManagementAsync(group, request, scope), token).ConfigureAwait(false))
                {
                    if (retry < 2) continue;
                    throw new DbUpdateConcurrencyException("The group management state changed while signing the update.");
                }
                await attempt.SaveRequestAsync(request.ToJson(), token).ConfigureAwait(false);
            }
            try
            {
                await attempt.CheckAsync(token).ConfigureAwait(false);
                await relay.SendHttpAsync(HttpMethod.Patch, "group.update", request, cancellationToken: token).ConfigureAwait(false);
            }
            catch (RelayException error) when (error.Error.Code == "state_conflict" && retry < 2) { continue; }
            await attempt.CheckAsync(token).ConfigureAwait(false);
            await SynchronizeLatestGroupAsync(group, relay, token).ConfigureAwait(false);
            return await ReadGroupAsync(group, token).ConfigureAwait(false);
        }
    }

    async Task SynchronizeLatestGroupAsync(GroupRef group, RelayClient relay, CancellationToken token)
    {
        // Property updates need verified management history, not message keys.
        // Keep network waits outside the manager gate so a superseded read cannot
        // block its successor. The coordinator still owns and drains the old read.
        while (true)
        {
            var after = await ReadGroupSequenceAsync(group, token).ConfigureAwait(false);
            var page = await relay.SendHttpAsync<GroupSyncPage>(HttpMethod.Get, "group.sync",
                new GroupSequenceQuery { GroupId = group.GroupId, After = after }, cancellationToken: token).ConfigureAwait(false);
            if (!await RunAsync(scope => ApplySyncPageAsync(group, after, page, scope), token).ConfigureAwait(false)) continue;
            if (!page.HasMore) return;
        }
    }

    sealed class GroupStateUpdates(GroupManager owner, DatabaseOptions options) : StateUpdateCoordinator(options)
    {
        protected override string Operation => "group.update";

        protected override async Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) =>
            await database.GroupOperations.FindAsync([resource, Operation], token).ConfigureAwait(false);

        protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) =>
            database.GroupOperations.Add(new() { GroupId = resource, Method = Operation, RequestState = PublicationRequestState.None }).Entity;

        protected override async Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token)
        {
            var rows = await database.GroupOperations.AsNoTracking().Where(value => value.Method == Operation && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token).ConfigureAwait(false);
            return rows.Select(value => Publication(value.GroupId, value)).ToList();
        }

        protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => owner.PublishLatestGroupAsync(attempt, token);

        protected override void Report(string resource, Exception error) => owner.ReportBackgroundError(BackgroundOperation.Synchronize, resource, error);
    }
}
