using Meshline.Identity;
using Meshline.Models;
using Meshline.Models.Registry;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Meshline.Interactions;

/// <summary>Read-only implementation of the protocol Registry ABI over Neo N3 JSON-RPC.</summary>
/// <remarks>Iterator cleanup failures are reported through <see cref="Trace"/> without replacing the primary query failure.</remarks>
public sealed class RpcRelayRegistry : IRelayRegistry
{
    readonly HttpClient http;
    readonly RpcRelayRegistryOptions options;

    /// <summary>Creates a Registry adapter without registering any DI services or taking ownership of the HTTP client.</summary>
    /// <param name="http">An application-owned HTTP client. Its headers and transport settings apply to RPC calls.</param>
    /// <param name="options">Explicit RPC and trusted network configuration.</param>
    public RpcRelayRegistry(HttpClient http, RpcRelayRegistryOptions options)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.http = http;
        this.options = options;
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    long requestId;
    /// <inheritdoc cref="IRelayRegistry.Context"/>
    public NetworkContext Context => options.Context;

    /// <inheritdoc cref="IRelayRegistry.GetRelayAsync"/>
    public async Task<RelayEntry?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        if (RelayIdentity.ValidateRelayId(relayId) is not null)
            throw new ArgumentException("Expected a canonical relay ID.", nameof(relayId));
        await VerifyNetworkAsync(cancellationToken);
        var result = await CallAsync("invokefunction", [Context.Registry, "getRelay", new[] { new { type = "Hash160", value = relayId } }], cancellationToken);
        var item = GetSingleStackItem(result);
        if (item.GetProperty("type").GetString() == "Any" && (!item.TryGetProperty("value", out var value) || value.ValueKind == JsonValueKind.Null))
            return null;
        var entry = ReadEntry(item);
        if (entry.RelayId != relayId)
            throw new InvalidDataException("Registry getRelay returned another relay's record.");
        return entry;
    }

    /// <inheritdoc cref="IRelayRegistry.GetRelaysAsync"/>
    public async IAsyncEnumerable<RelayEntry> GetRelaysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await VerifyNetworkAsync(cancellationToken);
        var result = await CallAsync("invokefunction", [Context.Registry, "listRelays", Array.Empty<object>()], cancellationToken);
        var session = result.TryGetProperty("session", out var sessionValue) ? sessionValue.GetString() : null;
        try
        {
            var iterator = GetSingleStackItem(result);
            if (iterator.GetProperty("type").GetString() != "InteropInterface")
                throw new InvalidDataException("Registry listRelays did not return an iterator.");
            if (session is not null)
            {
                if (!iterator.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException("Registry iterator session has no iterator ID.");
                var iteratorId = idValue.GetString()!;
                while (true)
                {
                    var batch = await CallAsync("traverseiterator", [session, iteratorId, options.IteratorPageSize], cancellationToken);
                    if (batch.ValueKind != JsonValueKind.Array || batch.GetArrayLength() > options.IteratorPageSize)
                        throw new InvalidDataException("Registry iterator returned an invalid batch.");
                    foreach (var item in batch.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return ReadEntry(item);
                    }
                    if (batch.GetArrayLength() == 0)
                        break;
                }
                yield break;
            }

            if (!iterator.TryGetProperty("iterator", out var inline) || inline.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Neo RPC returned neither a usable iterator session nor inline results.");
            if (!iterator.TryGetProperty("truncated", out var truncated) || truncated.ValueKind != JsonValueKind.False)
                throw new InvalidDataException("Neo RPC inline iterator results may be truncated. Enable iterator sessions on the RPC node.");
            foreach (var item in inline.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ReadEntry(item);
            }
        }
        finally
        {
            if (session is not null)
            {
                // Cancellation or early enumeration must still release the server session.
                using var cleanup = new CancellationTokenSource(options.RequestTimeout);
                try
                {
                    var released = await CallAsync("terminatesession", [session], cleanup.Token);
                    if (released.ValueKind != JsonValueKind.True)
                        Trace.TraceWarning("Neo RPC did not confirm Registry iterator session termination.");
                }
                catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException or OperationCanceledException)
                {
                    Trace.TraceWarning("Could not release Neo Registry iterator session: {0}", exception);
                }
            }
        }
    }

    async Task VerifyNetworkAsync(CancellationToken cancellationToken)
    {
        var version = await CallAsync("getversion", [], cancellationToken);
        if (!version.TryGetProperty("protocol", out var protocol) || !protocol.TryGetProperty("network", out var network)
            || !network.TryGetUInt32(out var magic) || magic != Context.Reference)
            throw new InvalidDataException("Neo RPC network magic differs from the configured Meshline network.");
    }

    async Task<JsonElement> CallAsync(string method, object[] parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref requestId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, options.RpcUrl)
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024, timeout.Token);
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token), new JsonDocumentOptions { MaxDepth = 32 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("jsonrpc", out var version)
            || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0"
            || !root.TryGetProperty("id", out var responseId) || responseId.ValueKind != JsonValueKind.Number
            || !responseId.TryGetInt64(out var actualId) || actualId != id
            || root.TryGetProperty("result", out _) == root.TryGetProperty("error", out _))
            throw new InvalidDataException("Neo RPC returned a mismatched JSON-RPC response.");
        if (root.TryGetProperty("error", out var error))
            throw new InvalidDataException($"Neo RPC {method} failed: {error.GetRawText()}");
        return root.GetProperty("result").Clone();
    }

    static JsonElement GetSingleStackItem(JsonElement result)
    {
        if (result.GetProperty("state").GetString() != "HALT")
            throw new InvalidDataException("Registry invocation did not halt successfully.");
        var stack = result.GetProperty("stack");
        if (stack.ValueKind != JsonValueKind.Array || stack.GetArrayLength() != 1)
            throw new InvalidDataException("Registry invocation must return exactly one stack item.");
        return stack[0];
    }

    static RelayEntry ReadEntry(JsonElement item)
    {
        if (item.GetProperty("type").GetString() is not ("Array" or "Struct"))
            throw new InvalidDataException("Expected a Registry RelayEntry array.");
        var fields = item.GetProperty("value");
        if (fields.ValueKind != JsonValueKind.Array || fields.GetArrayLength() != 4)
            throw new InvalidDataException("Registry RelayEntry must have exactly four fields.");
        var hash = ReadBytes(fields[0]);
        if (hash.Length != 20)
            throw new InvalidDataException("Registry relay hash must contain 20 bytes.");
        Array.Reverse(hash);
        var endpoint = StrictUtf8.GetString(ReadBytes(fields[1]));
        if (!endpoint.StartsWith("https://", StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(endpoint) > 512
            || endpoint.Any(static character => char.IsWhiteSpace(character) || char.IsControl(character))
            || endpoint.IndexOfAny(['?', '#', '\\']) >= 0
            || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.HostNameType == UriHostNameType.Unknown || uri.UserInfo.Length != 0)
            throw new InvalidDataException("Registry RelayEntry has an invalid HTTPS endpoint.");
        var status = StrictUtf8.GetString(ReadBytes(fields[2])) switch
        {
            "active" => RelayStatus.Active,
            "disabled" => RelayStatus.Disabled,
            "suspended" => RelayStatus.Suspended,
            _ => throw new InvalidDataException("Registry RelayEntry has an unknown status.")
        };
        if (fields[3].GetProperty("type").GetString() != "Integer"
            || !ulong.TryParse(fields[3].GetProperty("value").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var updatedAt))
            throw new InvalidDataException("Registry updated_at must be an unsigned integer in milliseconds.");
        return new() { RelayId = "0x" + Convert.ToHexStringLower(hash), Endpoint = endpoint, Status = status, UpdatedAt = updatedAt };
    }

    static byte[] ReadBytes(JsonElement item)
    {
        if (item.GetProperty("type").GetString() is not ("ByteString" or "Buffer"))
            throw new InvalidDataException("Expected a Neo VM byte string.");
        return Convert.FromBase64String(item.GetProperty("value").GetString()!);
    }
}
