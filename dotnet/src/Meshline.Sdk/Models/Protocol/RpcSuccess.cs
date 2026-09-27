using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Models.Protocol;

/// <summary>
/// Represents a successful JSON-RPC response with a possibly null result.
/// </summary>
public sealed record RpcSuccess : RpcResponse
{
    /// <summary>
    /// The identifier of the request completed by this response.
    /// </summary>
    public required string Id { get; init; }
    /// <summary>
    /// The JSON-RPC result, including an explicitly null result; JSON values are cloned on assignment.
    /// </summary>
    [JsonRequired]
    public JsonElement? Result
    {
        get;
        init => field = value?.Clone();
    }

    /// <summary>
    /// Appends the record's diagnostic fields while omitting sensitive material from its string representation.
    /// </summary>
    /// <param name="builder">The builder to which diagnostic fields are appended.</param>
    /// <returns><see langword="true"/> after appending the record's diagnostic fields.</returns>
    protected override bool PrintMembers(StringBuilder builder)
    {
        if (base.PrintMembers(builder))
            builder.Append(", ");

        builder.Append($"{nameof(Id)} = {Id}");
        return true;
    }
}
