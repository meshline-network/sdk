using Meshline.Components;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Support;

// Scripts only publication outcomes; admission and recovery use the real SQLite rows.
sealed class TestStateUpdateCoordinator(DatabaseOptions options, string method,
    Func<StateUpdateAttempt, CancellationToken, Task<object?>> publish, Action<string, Exception> report, string? accountId = null)
    : StateUpdateCoordinator(options)
{
    protected override string Operation => method;

    public Task<DateTimeOffset?> ReadNextAttemptAsync(string resource, CancellationToken token) =>
        ReadAsync(resource, (_, _, next) => next, token);

    protected override Task<object?> PublishAsync(StateUpdateAttempt attempt, CancellationToken token) => publish(attempt, token);

    protected override void Report(string resource, Exception error) => report(resource, error);

    protected override async Task<IStatePublication?> FindAsync(MeshlineDbContext database, string resource, CancellationToken token) => method switch
    {
        "group.update" => await database.GroupOperations.FindAsync([resource, method], token),
        "channel.update" => await database.ChannelOperations.SingleOrDefaultAsync(value => value.ResourceId == resource && value.Method == method, token),
        _ => await database.SignedRequests.FindAsync([method], token)
    };

    protected override IStatePublication Create(MeshlineDbContext database, string resource, JsonObject intent) => method switch
    {
        "group.update" => database.GroupOperations.Add(new() { GroupId = resource, Method = method, RequestState = PublicationRequestState.None }).Entity,
        "channel.update" => database.ChannelOperations.Add(new() { ResourceId = resource, Method = method, RelayId = intent["relay"]!.GetValue<string>(), RequestState = PublicationRequestState.None }).Entity,
        _ => database.SignedRequests.Add(new() { Method = method, RelayId = intent["relay"]?.GetValue<string>() ?? "", Revision = -1, RequestState = PublicationRequestState.None }).Entity
    };

    protected override async Task<List<StatePublication>> ReadPendingAsync(MeshlineDbContext database, CancellationToken token)
    {
        if (method == "group.update")
            return (await database.GroupOperations.AsNoTracking().Where(value => value.Method == method && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token))
                .Select(value => Publication(value.GroupId, value)).ToList();
        if (method == "channel.update")
            return (await database.ChannelOperations.AsNoTracking().Where(value => value.Method == method && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token))
                .Select(value => Publication(value.ResourceId, value)).ToList();
        return (await database.SignedRequests.AsNoTracking().Where(value => value.Method == method && value.Pending && value.NextAttemptAt != null && value.IntentJson != null).ToListAsync(token))
            .Select(value => Publication(accountId ?? throw new InvalidOperationException("An account publication needs its resource identifier."), value)).ToList();
    }
}
