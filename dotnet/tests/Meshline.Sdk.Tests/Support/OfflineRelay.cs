using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Models.Registry;
using Meshline.Transport;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Support;

internal sealed record ObservedRequest(string Method, string? Body, string? Session, HttpMethod Verb, string Query = "");
internal sealed class OfflineRelay : HttpMessageHandler, IRelayRegistry
{
    readonly AccountSigner relayKey = new();
    readonly HttpClient http;
    readonly List<OfflineRelay> neighbors = [];
    public string Host { get; }

    public void LinkTo(OfflineRelay relay)
    {
        neighbors.Add(relay);
        relay.neighbors.Add(this);
    }

    public ManualClock Clock { get; } = (ManualClock)Meshline.Clock.Provider;
    public NetworkContext Context => TestNetwork.Context;
    public string RelayId { get; }
    public RelayDescriptor Descriptor { get; set; }
    public RelayEntry Entry { get; set; }
    public ConcurrentQueue<ObservedRequest> Requests { get; } = new();
    public ConcurrentQueue<MemorySocket> Sockets { get; } = new();
    public ConcurrentDictionary<string, AccountRoute> Routes { get; } = new();
    public ConcurrentDictionary<string, AccountDeviceState> Devices { get; } = new();
    public ConcurrentDictionary<string, ProfileResolveResult> Profiles { get; } = new();
    public Func<ObservedRequest, CancellationToken, Task<HttpResponseMessage>>? Handler { get; set; }
    public Func<RpcRequest, MemorySocket, Task>? SocketHandler { get; set; }
    public HttpClient Http => http;
    public ConcurrentQueue<Uri> Upgrades { get; } = new();
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? UpgradeHandler { get; set; }
    public int SessionSeconds { get; set; } = 300;

    public int RegistryReads;
    int authenticationCount;
    public int AuthenticationCount => Volatile.Read(ref authenticationCount);

    public OfflineRelay(string host = "relay.test")
    {
        Host = host;
        RelayId = RelayIdentity.GetRelayId(relayKey.PublicKey.AsSpan());
        Entry = new()
        {
            RelayId = RelayId,
            Endpoint = "https://" + host,
            Status = RelayStatus.Active,
            UpdatedAt = 1
        };
        var bytes = new byte[34];
        bytes[0] = 0x12;
        bytes[1] = 0x20;
        Array.Fill(bytes, (byte)1, 2, 32);
        var number = new System.Numerics.BigInteger(bytes, true, true);
        var peer = "";
        while (number > 0)
        {
            number = System.Numerics.BigInteger.DivRem(number, 58, out var remainder);
            peer = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"[(int)remainder] + peer;
        }

        Descriptor = SignDescriptor(new()
        {
            RelayId = RelayId,
            PublicKey = relayKey.PublicKey,
            Endpoints = ["https://" + host, "wss://" + host, "/dns4/" + host + "/tcp/4201/p2p/" + peer],
            Capabilities = ["channel.host.v1", "group.host.v1"],
            ExpiresAt = Clock.GetUtcNow().AddDays(5).ToUnixTimeSeconds(),
            RelaySignature = []
        });
        http = new HttpClient(this, disposeHandler: false)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public ClientOptions Options(AccountSigner account) => new()
    {
        Context = Context,
        AccountId = account.AccountId
    };
    public DeviceSigner AddPeer(AccountSigner account)
    {
        var device = new DeviceSigner(account, Clock);
        var state = new AccountDeviceState
        {
            Account = account.AccountId,
            AccountPublicKey = account.PublicKey,
            Revision = 0,
            Certificates = [device.Certificate],
            AccountSignature = []
        };
        Devices[account.AccountId] = state with
        {
            AccountSignature = [.. account.Sign(state.GetSigningInput(Context))]
        };
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();
        var route = new AccountRoute
        {
            Account = account.AccountId,
            AccountPublicKey = account.PublicKey,
            Revision = 0,
            RelayId = RelayId,
            UpdatedAt = now,
            ExpiresAt = now + 86400,
            AccountSignature = []
        };
        route = route with
        {
            AccountSignature = [.. account.Sign(route.GetAccountSigningInput(Context))]
        };
        Routes[account.AccountId] = SignRoute(route);
        return device;
    }

    public RelayClientPool Pool(AccountSigner account) => new(Options(account), this, http);
    public RelayDescriptor SignDescriptor(RelayDescriptor value) => value with
    {
        RelaySignature = [.. relayKey.Sign(value.GetSigningInput(Context))]
    };
    public AccountRoute SignRoute(AccountRoute value) => value with
    {
        RelaySignature = [.. relayKey.Sign(value.GetRelaySigningInput(Context))]
    };
    public Task<RelayEntry?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Interlocked.Increment(ref RegistryReads);
        return Task.FromResult(relayId == RelayId ? Entry : neighbors.SingleOrDefault(relay => relay.RelayId == relayId)?.Entry);
    }

    public async IAsyncEnumerable<RelayEntry> GetRelaysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield return Entry;
        foreach (var relay in neighbors)
            yield return relay.Entry;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri!.Host != Host)
            return await neighbors.Single(relay => relay.Host == request.RequestUri.Host).SendAsync(request, cancellationToken);
        if (request.Headers.Upgrade.Any(value => value.Name == "websocket"))
        {
            Upgrades.Enqueue(request.RequestUri);
            if (UpgradeHandler is not null)
                return await UpgradeHandler(request, cancellationToken);
            var response = MemorySocket.Upgrade(request, OnSocketRequestAsync, out var socket);
            Sockets.Enqueue(socket);
            return response;
        }

        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var observed = new ObservedRequest(
            request.RequestUri!.AbsolutePath.Trim('/').Replace('/', '.'),
            body,
            request.Headers.TryGetValues("X-Meshline-Session", out var tokens) ? tokens.Single() : null,
            request.Method,
            request.RequestUri.Query);
        Requests.Enqueue(observed);
        if (Handler is not null)
            return await Handler(observed, cancellationToken);
        return Respond(observed, request.RequestUri.Query);
    }

    public HttpResponseMessage Respond(ObservedRequest request, string query = "")
    {
        if (query.Length == 0)
            query = request.Query;
        var now = Clock.GetUtcNow().ToUnixTimeSeconds();
        switch (request.Method)
        {
            case "relay.descriptor":
                return Json(Descriptor);
            case "relay.info":
                return Json(new RelayInfo
                {
                    RelayId = RelayId,
                    Name = "Offline relay",
                    ServerTime = now,
                    Limits = new()
                    {
                        MessageRetention = 86400,
                        ChannelTimelineRetention = 86400,
                        MaxChannelSubscriptions = 10,
                        GroupMessageRetention = 86400,
                        MaxGroupMembers = 100,
                        MaxGroupSubscriptions = 10,
                        MaxGroupInviteTtl = 86400
                    }
                });
            case "profile.resolve":
                var profileAccount = Uri.UnescapeDataString(query.TrimStart('?').Split('=', 2).Last());
                return Profiles.TryGetValue(profileAccount, out var profile) ? Json(profile) : Error("not_found");
            case "profile.publish":
                var publishedProfile = ProtocolModel.FromJson<AccountProfile>(request.Body!)!;
                Profiles[publishedProfile.Account] = new()
                {
                    Profile = publishedProfile,
                    SignerCertificate = Devices[publishedProfile.Account].Certificates[0]
                };
                return new(HttpStatusCode.NoContent);
            case "message.timeline.sync":
                return Json(new MessageTimelinePage
                {
                    Items = [],
                    Certificates = [],
                    HasMore = false
                });
            case "message.send":
                return Json(new MessageDeliveryStatus
                {
                    Status = MessageDeliveryState.TargetAccepted,
                    AcceptedAt = now
                });
            case "auth.challenge":
                return Json(new AuthenticationChallenge
                {
                    Nonce = "offline-challenge",
                    CreatedAt = now,
                    ExpiresAt = now + 60
                });
            case "auth.account.verify":
            case "auth.device.verify":
                if (request.Method == "auth.account.verify")
                {
                    var proof = ProtocolModel.FromJson<AccountAuthenticationRequest>(request.Body!)!;
                    var identity = AccountAdapter.GetAccountId("neo:860833102", proof.AccountPublicKey.AsSpan());
                    if (proof.Nonce != "offline-challenge" || !AccountAdapter.VerifySignature(identity, proof.AccountPublicKey.AsSpan(), proof.GetSigningInput(identity, RelayId, "https://" + Host, Context), proof.AccountSignature.AsSpan()))
                        return Error("invalid_signature");
                }
                else
                {
                    var proof = ProtocolModel.FromJson<DeviceAuthenticationRequest>(request.Body!)!;
                    if (proof.SignerCertificate.Validate(Context) is not null || proof.Nonce != "offline-challenge" || !Org.BouncyCastle.Math.EC.Rfc8032.Ed25519.Verify(proof.DeviceSignature.AsSpan(), proof.SignerCertificate.SigningPublicKey.AsSpan(), proof.GetSigningInput(RelayId, "https://" + Host, Context)))
                        return Error("invalid_signature");
                }

                var count = Interlocked.Increment(ref authenticationCount);
                return Json(new SessionCredentials
                {
                    Token = "session-" + count,
                    Mode = request.Method == "auth.account.verify" ? SessionMode.Account : SessionMode.Device,
                    ExpiresAt = now + SessionSeconds
                });
            case "account.route.resolve":
                var account = Uri.UnescapeDataString(query.TrimStart('?').Split('=', 2).Last());
                return Routes.TryGetValue(account, out var route) ? Json(route) : Error("not_found");
            case "account.route.publish":
                var submitted = ProtocolModel.FromJson<AccountRoute>(request.Body!)!;
                Assert.Null(submitted.Validate(Context));
                var published = SignRoute(submitted);
                Routes[published.Account] = published;
                foreach (var relay in neighbors)
                    relay.Routes[published.Account] = published;
                return Json(published);
            case "device.state.publish":
                var state = ProtocolModel.FromJson<AccountDeviceState>(request.Body!)!;
                Assert.Null(state.Validate(Context));
                Devices[state.Account] = state;
                return Json(new DeviceStatePublishResponse
                {
                    Status = Routes.ContainsKey(state.Account) ? DeviceStatePublishStatus.Accepted : DeviceStatePublishStatus.Staged,
                    StagedUntil = Routes.ContainsKey(state.Account) ? null : now + 600
                });
            case "device.state.resolve":
                var requested = request.Body is null ? Uri.UnescapeDataString(query.TrimStart('?').Split('=', 2).Last()) : ProtocolModel.FromJson<SignedDeviceStateQuery>(request.Body)!.Account;
                return Devices.TryGetValue(requested, out var deviceState) ? Json(deviceState) : Error("not_found");
            default:
                throw new InvalidOperationException("Unscripted offline HTTP request: " + request.Method);
        }
    }

    async Task OnSocketRequestAsync(RpcRequest request, MemorySocket socket)
    {
        if (request.Method.StartsWith("auth.", StringComparison.Ordinal))
        {
            using var response = Respond(new(request.Method, JsonSerializer.Serialize(request.Params), null, HttpMethod.Post));
            socket.Reply(request.Id!, await response.Content.ReadAsStringAsync());
        }
        else if (SocketHandler is not null)
            await SocketHandler(request, socket);
        else
            throw new InvalidOperationException("Unscripted offline WebSocket request: " + request.Method);
    }

    public static HttpResponseMessage Json(ProtocolModel model, HttpStatusCode status = HttpStatusCode.OK) => Json(model.ToJson(), status);
    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
    public static HttpResponseMessage Error(string code) => Json(new RelayError
    {
        Code = code,
        Message = "Injected " + code
    }, HttpStatusCode.BadRequest);
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try
            {
                foreach (var socket in Sockets)
                    socket.Dispose();
            }
            finally
            {
                http.Dispose();
                relayKey.Dispose();
            }
        }

        base.Dispose(disposing);
    }
}
