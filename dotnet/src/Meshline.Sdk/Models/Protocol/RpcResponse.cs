using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meshline.Models.Protocol;

/// <summary>
/// Provides the JSON-RPC version shared by successful and failed responses.
/// </summary>
public abstract record RpcResponse : ProtocolModel
{
    /// <summary>
    /// The JSON-RPC protocol version, which must be <c>2.0</c>.
    /// </summary>
    [JsonRequired]
    [SuppressMessage("Performance", "CA1822")]
    public string Jsonrpc
    {
        get => "2.0";
        init
        {
            if (value != "2.0")
                throw new JsonException("The jsonrpc value must be '2.0'.");
        }
    }
}
