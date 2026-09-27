using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models.Protocol;
using Meshline.Models;
using Meshline.Tests.Support;
using Meshline.Transport;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text;

namespace Meshline.Tests.Transport;

public sealed class AuthenticationVectorTests
{
    public static IEnumerable<object[]> Origins() => Rows(ProtocolVectors.Read("identity-auth").GetProperty("relay_origin").GetProperty("normalization_cases"));
    public static IEnumerable<object[]> Authentication() => Rows(ProtocolVectors.Read("identity-auth").GetProperty("session_auth").GetProperty("verification_cases"));
    static IEnumerable<object[]> Rows(JsonElement value) => value.EnumerateArray().Select(row => new object[] { row.GetRawText() });
    [Theory, MemberData(nameof(Origins))]
    public async Task Relay_authentication_origin_matches_normalization_vector(string json)
    {
        using var document = JsonDocument.Parse(json);
        var vector = document.RootElement;
        var origin = await CaptureAuthenticationOriginAsync(vector.GetProperty("endpoint").GetString()!);

        Assert.Equal(vector.GetProperty("expected_origin").GetString(), origin);
        Assert.Equal(Convert.FromHexString(vector.GetProperty("expected_origin_utf8_hex").GetString()!), Encoding.UTF8.GetBytes(origin));
    }

    [Theory, MemberData(nameof(Authentication))]
    public async Task Account_and_device_authentication_bind_trusted_target_and_network(string json)
    {
        using var document = JsonDocument.Parse(json);
        var test = document.RootElement;
        var vector = ProtocolVectors.Read("identity-auth").GetProperty("session_auth");
        var context = NetworkContext.Parse(test.GetProperty("network_context").GetString()!);
        var relay = test.GetProperty("trusted_relay_id").GetString()!;
        var origin = await CaptureAuthenticationOriginAsync(test.GetProperty("endpoint").GetString()!);
        var device = ProtocolModel.FromJson<DeviceAuthenticationRequest>(vector.GetProperty("device_auth").GetProperty("request").GetRawText())!;
        var account = ProtocolModel.FromJson<AccountAuthenticationRequest>(vector.GetProperty("account_auth").GetProperty("request").GetRawText())!;
        if (test.TryGetProperty("nonce_override", out var nonce))
        {
            device = device with
            {
                Nonce = nonce.GetString()!
            };
            account = account with
            {
                Nonce = nonce.GetString()!
            };
        }

        var deviceInput = device.GetSigningInput(relay, origin, context);
        var accountInput = account.GetSigningInput(device.SignerCertificate.Account, relay, origin, context);
        var signatureField = test.GetProperty("signature_field").GetString()!;

        Assert.Equal(test.GetProperty("expected_signature_valid").GetBoolean(), Ed25519.Verify(
            ProtocolVectors.Decode(vector.GetProperty("device_auth").GetProperty(signatureField).GetString()!),
            device.SignerCertificate.SigningPublicKey.AsSpan(),
            deviceInput));
        Assert.Equal(test.GetProperty("expected_signature_valid").GetBoolean(), AccountAdapter.VerifySignature(
            device.SignerCertificate.Account,
            account.AccountPublicKey.AsSpan(),
            accountInput,
            ProtocolVectors.Decode(vector.GetProperty("account_auth").GetProperty(signatureField).GetString()!)));

        if (test.GetProperty("name").GetString() == "original_target")
        {
            Assert.Equal(Convert.FromHexString(vector.GetProperty("device_auth").GetProperty("signing_input_utf8_hex").GetString()!), deviceInput);
            Assert.Equal(Convert.FromHexString(vector.GetProperty("account_auth").GetProperty("signing_input_utf8_hex").GetString()!), accountInput);
        }
    }

    // Observe the public signer boundary during real HTTP / WebSocket authentication.
    // Transport is entirely in memory; neither endpoint normalization nor private
    // RelayClient helpers are reproduced in this fixture.
    static async Task<string> CaptureAuthenticationOriginAsync(string endpoint)
    {
        using var time = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        var signer = new RecordingSigner(account);
        var socketEndpoint = endpoint.StartsWith("wss://", StringComparison.Ordinal);
        relay.Descriptor = relay.SignDescriptor(relay.Descriptor with { Endpoints = [socketEndpoint ? "https://relay.test" : endpoint, socketEndpoint ? endpoint : "wss://relay.test", relay.Descriptor.Endpoints[2]] });
        var now = relay.Clock.GetUtcNow().ToUnixTimeSeconds();
        ProtocolModel Respond(string method, string? body)
        {
            if (method == "relay.descriptor")
                return relay.Descriptor;
            if (method == "auth.challenge")
                return new AuthenticationChallenge
                {
                    Nonce = "vector-origin",
                    CreatedAt = now,
                    ExpiresAt = now + 60
                };
            if (method == "auth.account.verify")
            {
                var proof = ProtocolModel.FromJson<AccountAuthenticationRequest>(body!)!;

                Assert.True(AccountAdapter.VerifySignature(account.AccountId, account.PublicKey.AsSpan(), signer.Input!, proof.AccountSignature.AsSpan()));

                return new SessionCredentials
                {
                    Mode = SessionMode.Account,
                    Token = "vector-session",
                    ExpiresAt = now + 300
                };
            }

            if (method == "vector.probe")
                return new SequenceResult
                {
                    Sequence = 0
                };
            throw new InvalidOperationException("Unscripted origin fixture request: " + method);
        }

        using var handler = new OriginHandler(async (request, token) =>
        {
            if (request.Headers.Upgrade.Any(value => value.Name == "websocket"))
            {
                Assert.Equal(new Uri(endpoint), request.RequestUri);

                var response = MemorySocket.Upgrade(request, (rpc, socket) =>
                {
                    socket.Reply(rpc.Id!, Respond(rpc.Method, JsonSerializer.Serialize(rpc.Params)).ToJson());
                    return Task.CompletedTask;
                }, out var peer);
                relay.Sockets.Enqueue(peer);
                return response;
            }

            var path = request.RequestUri!.AbsolutePath;
            var method = new[]
            {
                "relay.descriptor",
                "auth.challenge",
                "auth.account.verify"
            }.Single(name => path.EndsWith("/" + name.Replace('.', '/'), StringComparison.Ordinal));
            return OfflineRelay.Json(Respond(method, request.Content is null ? null : await request.Content.ReadAsStringAsync(token)));
        });
        using var http = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        await using var pool = new RelayClientPool(relay.Options(account), relay, http);
        var client = await pool.GetAsync(relay.RelayId, signer, TestContext.Current.CancellationToken);
        if (socketEndpoint)
            await client.SendWebSocketAsync<SequenceResult>("vector.probe", cancellationToken: TestContext.Current.CancellationToken);
        using var input = JsonDocument.Parse(signer.Input!);
        return input.RootElement.GetProperty("origin").GetString()!;
    }

    sealed class RecordingSigner(AccountSigner inner) : IAccountSigner
    {
        public string AccountId => inner.AccountId;
        public ImmutableArray<byte> PublicKey => inner.PublicKey;
        public byte[]? Input { get; private set; }

        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            Input = data.ToArray();
            return inner.SignAsync(data, cancellationToken);
        }
    }

    sealed class OriginHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
