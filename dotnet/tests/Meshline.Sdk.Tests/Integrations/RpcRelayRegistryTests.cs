using Meshline.Models;
using Meshline.Models.Registry;

using Meshline.Interactions;

using System.Net;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Integrations;

public sealed class RpcRelayRegistryTests
{
    static readonly NetworkContext Context = new() { Reference = 12345, Registry = "0x0123456789012345678901234567890123456789" };
    const string RelayId = "0x0102030405060708091011121314151617181920";

    [Fact]
    public async Task Get_relay_checks_network_and_decodes_hash_byte_order_and_millisecond_timestamp()
    {
        var methods = new List<string>();
        using var http = Client((method, parameters) =>
        {
            methods.Add(method);
            if (method == "getversion") return new { protocol = new { network = 12345 } };
            Assert.Equal("invokefunction", method);
            Assert.Equal(Context.Registry, parameters[0].GetString());
            Assert.Equal("getRelay", parameters[1].GetString());
            Assert.Equal("Hash160", parameters[2][0].GetProperty("type").GetString());
            Assert.Equal(RelayId, parameters[2][0].GetProperty("value").GetString());
            return Invocation(Entry("active"));
        });

        var result = await Registry(http).GetRelayAsync(RelayId, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(RelayId, result.RelayId);
        Assert.Equal(RelayStatus.Active, result.Status);
        Assert.Equal(1730000000000ul, result.UpdatedAt);
        Assert.Equal(["getversion", "invokefunction"], methods);
    }

    [Fact]
    public async Task Network_mismatch_prevents_contract_calls()
    {
        using var http = Client((method, _) =>
        {
            Assert.Equal("getversion", method);
            return new { protocol = new { network = 54321 } };
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(http).GetRelayAsync(RelayId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Iterator_traverses_all_batches_and_preserves_inactive_members()
    {
        var calls = new List<string>();
        var page = 0;
        using var http = Client((method, parameters) =>
        {
            calls.Add(method);
            return method switch
            {
                "getversion" => new { protocol = new { network = 12345 } },
                "invokefunction" => new { state = "HALT", session = "session-1", stack = new[] { new { type = "InteropInterface", id = "iterator-1" } } },
                "traverseiterator" => ++page == 1 ? new[] { Entry("disabled"), Entry("suspended") } : Array.Empty<object>(),
                "terminatesession" => true,
                _ => throw new InvalidOperationException(method)
            };
        });
        var entries = new List<RelayEntry>();

        await foreach (var entry in Registry(http).GetRelaysAsync(TestContext.Current.CancellationToken))
            entries.Add(entry);

        Assert.Equal([RelayStatus.Disabled, RelayStatus.Suspended], entries.Select(static entry => entry.Status));
        Assert.Equal(["getversion", "invokefunction", "traverseiterator", "traverseiterator", "terminatesession"], calls);
    }

    [Fact]
    public async Task Early_enumeration_exit_releases_iterator_session()
    {
        var released = false;
        using var http = Client((method, _) =>
        {
            if (method == "getversion") return new { protocol = new { network = 12345 } };
            if (method == "invokefunction") return new { state = "HALT", session = "session-1", stack = new[] { new { type = "InteropInterface", id = "iterator-1" } } };
            if (method == "traverseiterator") return new[] { Entry("active") };
            Assert.Equal("terminatesession", method);
            released = true;
            return true;
        });

        await foreach (var entry in Registry(http).GetRelaysAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(RelayId, entry.RelayId);
            break;
        }

        Assert.True(released);
    }

    [Fact]
    public async Task Truncated_inline_iterator_is_not_silently_accepted_as_a_complete_directory()
    {
        using var http = Client((method, _) => method == "getversion"
            ? new { protocol = new { network = 12345 } }
            : Invocation(new { type = "InteropInterface", iterator = new[] { Entry("active") }, truncated = true }));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in Registry(http).GetRelaysAsync(TestContext.Current.CancellationToken)) { }
        });
    }

    [Fact]
    public async Task Unknown_relay_is_null()
    {
        using var http = Client((method, _) => method == "getversion"
            ? new { protocol = new { network = 12345 } }
            : Invocation(new { type = "Any" }));

        Assert.Null(await Registry(http).GetRelayAsync(RelayId, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("fault")]
    [InlineData("wrong-relay")]
    [InlineData("status")]
    [InlineData("shape")]
    public async Task Invalid_registry_data_is_not_accepted(string failure)
    {
        using var http = Client((method, _) =>
        {
            if (method == "getversion") return new { protocol = new { network = 12345 } };
            return failure switch
            {
                "fault" => new { state = "FAULT", stack = Array.Empty<object>() },
                "status" => Invocation(Entry("unknown")),
                "shape" => Invocation(new { type = "Struct", value = Array.Empty<object>() }),
                _ => Invocation(Entry("active"))
            };
        });
        var requested = failure == "wrong-relay" ? "0x0000000000000000000000000000000000000000" : RelayId;

        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(http).GetRelayAsync(requested, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Cancellation_during_enumeration_releases_session()
    {
        using var cancellation = new CancellationTokenSource();
        var released = false;
        using var http = Client((method, _) => method switch
        {
            "getversion" => new { protocol = new { network = 12345 } },
            "invokefunction" => new { state = "HALT", session = "s", stack = new[] { new { type = "InteropInterface", id = "i" } } },
            "traverseiterator" => new[] { Entry("active"), Entry("disabled") },
            "terminatesession" => released = true,
            _ => throw new InvalidOperationException(method)
        });
        await using var enumerator = Registry(http).GetRelaysAsync(cancellation.Token).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await enumerator.MoveNextAsync());
        Assert.True(released);
    }

    [Fact]
    public async Task Complete_inline_iterator_is_supported()
    {
        using var http = Client((method, _) => method == "getversion"
            ? new { protocol = new { network = 12345 } }
            : Invocation(new { type = "InteropInterface", iterator = new[] { Entry("active") }, truncated = false }));
        var entries = new List<RelayEntry>();

        await foreach (var entry in Registry(http).GetRelaysAsync(TestContext.Current.CancellationToken)) entries.Add(entry);

        Assert.Single(entries);
    }

    [Theory]
    [InlineData("rpc")]
    [InlineData("id")]
    [InlineData("missing")]
    public async Task Rpc_failures_propagate(string failure)
    {
        using var http = new HttpClient(new FailureHandler(failure));

        await Assert.ThrowsAsync<InvalidDataException>(() => Registry(http).GetRelayAsync(RelayId, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_iterator_metadata_still_releases_allocated_session()
    {
        var released = false;
        using var http = Client((method, _) => method switch
        {
            "getversion" => new { protocol = new { network = 12345 } },
            "invokefunction" => new { state = "HALT", session = "s", stack = new[] { new { type = "InteropInterface" } } },
            "terminatesession" => released = true,
            _ => throw new InvalidOperationException(method)
        });

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await foreach (var _ in Registry(http).GetRelaysAsync(TestContext.Current.CancellationToken)) { }
        });
        Assert.True(released);
    }

    [Fact]
    public async Task Request_timeout_cancels_rpc_io()
    {
        using var http = new HttpClient(new TimeoutHandler());
        var registry = new RpcRelayRegistry(http, new RpcRelayRegistryOptions
        {
            Context = Context,
            RpcUrl = new Uri("https://neo.test"),
            RequestTimeout = TimeSpan.FromMilliseconds(30)
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => registry.GetRelayAsync(RelayId, TestContext.Current.CancellationToken));
    }

    sealed class FailureHandler(string failure) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(failure switch
                {
                    "rpc" => """{"jsonrpc":"2.0","id":1,"error":{"code":-100,"message":"RPC unavailable"}}""",
                    "id" => """{"jsonrpc":"2.0","id":0,"result":{}}""",
                    _ => "{}"
                })
            });
    }

    sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The request should have been canceled.");
        }
    }

    static RpcRelayRegistry Registry(HttpClient http) => new(http, new RpcRelayRegistryOptions
    {
        Context = Context,
        RpcUrl = new Uri("https://neo.test"),
        IteratorPageSize = 2
    });

    static HttpClient Client(Func<string, JsonElement, object> respond) => new(new RpcHandler(respond));
    static object Invocation(object item) => new { state = "HALT", stack = new[] { item } };
    static object Entry(string status)
    {
        var hash = Convert.FromHexString(RelayId[2..]);
        Array.Reverse(hash);
        return new
        {
            type = "Array",
            value = new object[]
            {
                new { type = "ByteString", value = Convert.ToBase64String(hash) },
                new { type = "ByteString", value = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://relay.test/meshline/v1")) },
                new { type = "ByteString", value = Convert.ToBase64String(Encoding.UTF8.GetBytes(status)) },
                new { type = "Integer", value = "1730000000000" }
            }
        };
    }

    sealed class RpcHandler(Func<string, JsonElement, object> respond) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            var result = respond(root.GetProperty("method").GetString()!, root.GetProperty("params"));
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = root.GetProperty("id").GetInt64(), result }), Encoding.UTF8, "application/json")
            };
        }
    }
}
