using Meshline.Models;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Components;

static class StateUpdateJson
{
    public static async Task RejectAsync(DatabaseOptions options, string method, string document)
    {
        await using var database = new MeshlineDbContext(options);
        await database.SignedRequests.Where(value => value.Method == method && value.DocumentJson == document)
            .ExecuteUpdateAsync(update => update.SetProperty(value => value.Pending, value => value.IntentJson != null).SetProperty(value => value.RequestState, PublicationRequestState.Rejected)).ConfigureAwait(false);
    }

    public static void Field<T>(JsonObject patch, string name, FieldUpdate<T> value) where T : notnull
    {
        if (value.IsDeleted) patch[name] = null;
        else if (value.IsSpecified) patch[name] = value.Value is ProtocolModel model
            ? JsonNode.Parse(model.ToJson()) : JsonSerializer.SerializeToNode(value.Value);
    }

    public static JsonObject Apply(ProtocolModel basis, JsonObject patch, params string[] fields)
    {
        var result = JsonNode.Parse(basis.ToJson())!.AsObject();
        foreach (var name in fields)
        {
            if (!patch.TryGetPropertyValue(name, out var value)) continue;
            if (value is null) result.Remove(name);
            else result[name] = value.DeepClone();
        }
        return result;
    }
}
