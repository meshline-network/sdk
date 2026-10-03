using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Meshline;
using Meshline.Identity;
using Meshline.Interactions;
using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Models.Registry;
using Meshline.Storage;
using Meshline.Transport;
using Meshline.Tests.Support;
using ClientGroupRef = Meshline.Models.Client.GroupRef;

// Test-only public-SDK workflow driver. HTTP is forwarded to the TypeScript test relay;
// both SDKs keep their own native storage, signing, authorization and state machines.
static class WorkflowSession
{
    static readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> Responses = new();
    static readonly Dictionary<string, Session> Clients = new(StringComparer.Ordinal);
    static int nextRequest;
    static readonly object OutputGate = new();
    static void Write(object value) { lock (OutputGate) Console.WriteLine(JsonSerializer.Serialize(value)); }

    public static async Task RunAsync()
    {
        var commands = Channel.CreateUnbounded<JsonElement>();
        var input = Task.Run(async () =>
        {
            try
            {
                while (await Console.In.ReadLineAsync() is { } line)
                {
                    using var json = JsonDocument.Parse(line); var value = json.RootElement.Clone();
                    if (value.TryGetProperty("httpResponse", out var response))
                    {
                        if (Responses.TryRemove(response.GetProperty("id").GetInt32(), out var waiting)) waiting.TrySetResult(response.Clone());
                    }
                    else await commands.Writer.WriteAsync(value);
                }
            }
            finally
            {
                commands.Writer.TryComplete();
                foreach (var waiting in Responses.Values) waiting.TrySetException(new EndOfStreamException("Workflow input closed."));
            }
        });
        await foreach (var command in commands.Reader.ReadAllAsync())
        {
            try { Write(new { result = await ExecuteAsync(command) }); }
            catch (Exception error) { Write(new { error = error.GetType().Name, message = error.Message }); }
        }
        foreach (var client in Clients.Values) await client.DisposeAsync();
        await input;
    }

    static async Task<object> ExecuteAsync(JsonElement request)
    {
        string Text(string field) => request.GetProperty(field).GetString()!;
        var id = Text("id"); var operation = Text("operation");
        if (operation == "open")
        {
            if (Clients.ContainsKey(id)) throw new InvalidOperationException("Workflow client is already open.");
            var context = NetworkContext.Parse(Text("context")); var signer = new Signer(Convert.FromBase64String(Text("privateKey")));
            var options = new ClientOptions { Context = context, AccountId = signer.AccountId };
            var database = new DatabaseOptions { Path = Text("path") }; var now = request.GetProperty("now").GetInt64();
            var entries = request.GetProperty("relays").EnumerateArray().Select(value => new RelayEntry { RelayId = value.GetProperty("relayId").GetString()!, Endpoint = value.GetProperty("endpoint").GetString()!, Status = RelayStatus.Active, UpdatedAt = checked((ulong)now * 1000) }).ToArray();
            var registry = new Registry(context, entries); var http = new HttpClient(new BridgeHttp()) { Timeout = Timeout.InfiniteTimeSpan };
            var pool = new RelayClientPool(options, registry, http); var client = new MeshlineClient(options, database, pool, new Protector(signer.PrivateKey), signer);
            var session = new Session(client, pool, signer, http, new ManualClock(DateTimeOffset.FromUnixTimeSeconds(now)));
            try { using var scope = session.TimeScope(); await MeshlineDatabase.MigrateAsync(database); await client.InitializeAsync(); Clients.Add(id, session); }
            catch { await session.DisposeAsync(); throw; }
            return new { accountId = signer.AccountId };
        }
        var current = Clients[id]; using var time = current.TimeScope(); var sdk = current.Client;
        ClientGroupRef Group() => new() { RelayId = Text("relayId"), GroupId = Text("groupId") };
        ChannelRef Channel() => new() { RelayId = Text("relayId"), ChannelId = Text("channelId") };
        ChannelPostRef Post() => new() { Channel = Channel(), Sequence = request.GetProperty("sequence").GetInt64() };
        string[] Accounts() => request.GetProperty("accounts").EnumerateArray().Select(value => value.GetString()!).ToArray();
        switch (operation)
        {
            case "close": Clients.Remove(id); await current.DisposeAsync(); return new { closed = true };
            case "establish": await sdk.EstablishAccountAsync(new() { RelayId = Text("relayId") }); break;
            case "recover": await sdk.RecoverAccountAsync(new() { RelayId = Text("relayId") }); break;
            case "migrate": await sdk.ChangeHomeRelayAsync(Text("relayId")); break;
            case "start": await sdk.StartAsync(); break;
            case "stop": await sdk.StopAsync(); break;
            case "advance": current.Time.Advance(TimeSpan.FromSeconds(request.GetProperty("seconds").GetInt64())); break;
            case "message-start": await sdk.MessageManager.StartAsync(); break;
            case "group-start": await sdk.GroupManager.StartAsync(); break;
            case "contact-add": await sdk.MessageManager.AddContactAsync(Text("account")); break;
            case "contact-accept": await sdk.MessageManager.AcceptContactRequestAsync(Text("account")); break;
            case "contact-read":
                var contact = await sdk.MessageManager.GetContactAsync(Text("account"));
                return new { present = contact is not null, json = contact is null ? null : JsonSerializer.Serialize(contact) };
            case "message-send":
                var outgoing = await sdk.MessageManager.SendMessageAsync(Text("account"), new() { Body = new() { ContentType = "text/plain", Text = Text("text") } });
                return new { messageId = outgoing.MessageId };
            case "message-read":
                await using (var history = await sdk.MessageManager.GetMessageHistoryAsync()) return new { messages = (await history.ReadNextAsync(100)).Select(value => new { messageId = value.Key.MessageId, text = value.Body?.Text }) };
            case "history-open":
                var readerId = Text("reader");
                if (current.HistoryReaders.ContainsKey(readerId)) throw new InvalidOperationException("History reader already exists.");
                current.HistoryReaders.Add(readerId, await sdk.MessageManager.GetMessageHistoryAsync(request.TryGetProperty("peer", out var peer) ? peer.GetString() : null));
                return new { opened = true };
            case "history-next":
                using (var cancellation = new CancellationTokenSource())
                {
                    if (request.TryGetProperty("canceled", out var canceled) && canceled.GetBoolean()) cancellation.Cancel();
                    var page = await current.HistoryReaders[Text("reader")].ReadNextAsync(request.GetProperty("count").GetInt32(), cancellation.Token);
                    return new { messages = page.Select(value => new { messageId = value.Key.MessageId, sender = value.Key.Sender, recipient = value.Recipient, createdAt = value.CreatedAt.ToUnixTimeSeconds(), text = value.Body?.Text }) };
                }
            case "history-close": await current.HistoryReaders[Text("reader")].DisposeAsync(); return new { closed = true };
            case "channel-create":
                var channel = await sdk.ChannelManager.CreateChannelAsync(Text("relayId"), Text("name")); return new { channelId = channel.Ref.ChannelId, relayId = channel.Ref.RelayId };
            case "channel-follow": await sdk.ChannelManager.FollowAsync(Channel()); break;
            case "channel-send":
                var post = await sdk.ChannelManager.PublishPostAsync(Channel(), new() { Body = new() { ContentType = "text/plain", Text = Text("text") } }); return new { sequence = post.Ref.Sequence, messageId = post.MessageId };
            case "channel-edit": await sdk.ChannelManager.EditPostAsync(Post(), new() { Body = new MessageBody { ContentType = "text/plain", Text = Text("text") } }); break;
            case "channel-delete": await sdk.ChannelManager.DeletePostAsync(Post()); break;
            case "channel-read":
                await sdk.ChannelManager.LoadChannelHistoryAsync(Channel());
                await using (var posts = await sdk.ChannelManager.GetPostsAsync(Text("channelId"))) return new { posts = (await posts.ReadNextAsync(100)).Select(value => new { sequence = value.Ref.Sequence, text = value.Body?.Text, messageId = value.MessageId }) };
            case "profile": await sdk.ProfileManager.UpdateProfileAsync(new() { Nickname = Text("nickname") }); break;
            case "authorize":
                var route = await sdk.AccountManager.GetRouteAsync();
                var previous = route is null ? null : await sdk.DeviceManager.GetOwnDeviceStateAsync(route.RelayId);
                var local = sdk.Device ?? await sdk.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(365));
                if (route is null) await sdk.AccountManager.PublishRouteAsync(Text("relayId"), TimeSpan.FromDays(365));
                await sdk.DeviceManager.PublishDeviceStateAsync(Text("relayId"), [.. previous?.Certificates ?? [], local]);
                break;
            case "group-create":
                var created = await sdk.GroupManager.CreateGroupAsync(Text("relayId"), new() { Name = Text("name"), MemberCapacity = 20 });
                return new { groupId = created.Ref.GroupId, relayId = created.Ref.RelayId };
            case "group-invite":
                var invite = await sdk.GroupManager.CreateInviteAsync(Group(), Text("invitee"), current.Time.GetUtcNow().AddHours(1));
                return new { document = invite.Document.ToJson() };
            case "group-apply": await sdk.GroupManager.ApplyToGroupAsync(new Meshline.Models.Client.GroupInvite(Text("relayId"), ProtocolModel.FromJson<Meshline.Models.Protocol.GroupInvite>(Text("document"))!)); break;
            case "group-approve": await sdk.GroupManager.ApproveApplicationsAsync(Group(), Accounts()); break;
            case "group-recovery": await sdk.GroupManager.RequestKeyRecoveryAsync(Group()); break;
            case "group-recovery-approve": await sdk.GroupManager.ApproveKeyRecoveryAsync(Group(), Accounts()); break;
            case "group-rotate": await sdk.GroupManager.RotateSecretAsync(Group(), request.GetProperty("ownerKey").GetBoolean()); break;
            case "group-nickname": await sdk.GroupManager.SetNicknameAsync(Group(), request.GetProperty("nickname").ValueKind == JsonValueKind.Null ? null : Text("nickname")); break;
            case "group-send":
                var sent = await sdk.GroupManager.SendMessageAsync(Group(), new() { Body = new() { ContentType = "text/plain", Text = Text("text") } });
                return new { messageId = sent.MessageId, sequence = sent.Sequence };
            case "group-read":
                var group = await sdk.GroupManager.GetGroupAsync(Group());
                await using (var messages = await sdk.GroupManager.GetMessagesAsync(Text("groupId")))
                await using (var members = await sdk.GroupManager.GetMembersAsync(Group()))
                {
                    var content = await messages.ReadNextAsync(100); var people = await members.ReadNextAsync(100);
                    return new { membership = group.Membership.ToString(), messages = content.Select(value => new { messageId = value.MessageId, text = value.Body?.Text, sequence = value.Sequence }), members = people.Select(value => new { accountId = value.AccountId, nickname = value.Nickname }) };
                }
            case "conversations":
                var kinds = request.TryGetProperty("kinds", out var kindValues) ? kindValues.EnumerateArray().Aggregate((ConversationKind)0, (all, kind) => all | Enum.Parse<ConversationKind>(kind.GetString()!, ignoreCase: true)) : ConversationKind.All;
                var filter = new ConversationQuery { Kind = kinds, UnreadOnly = request.TryGetProperty("unreadOnly", out var unread) && unread.GetBoolean(), HasMessages = request.TryGetProperty("hasMessages", out var hasMessages) ? hasMessages.GetBoolean() : null };
                await using (var query = await sdk.GetConversationsAsync(filter)) return new { conversations = (await query.ReadNextAsync(100)).Select(value => new { conversationId = value.ConversationId, kind = value.Kind.ToString(), unreadCount = value.UnreadCount, text = value.Latest?.Text, timestamp = value.Latest?.Timestamp.ToUnixTimeSeconds(), hasAttachments = value.Latest?.HasAttachments }) };
            case "mark-read": await sdk.MarkReadAsync(Text("conversationId")); break;
            default: throw new ArgumentException("Unsupported workflow operation.");
        }
        return new { route = sdk.Route?.ToJson(), deviceState = sdk.DeviceState?.ToJson(), device = sdk.Device?.ToJson(), profile = sdk.Profile?.ToJson() };
    }

    sealed class BridgeHttp : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref nextRequest); var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously); Responses[id] = waiting;
            try
            {
                var headers = request.Headers.ToDictionary(value => value.Key, value => string.Join(", ", value.Value));
                Write(new { http = new { id, url = request.RequestUri!.AbsoluteUri, method = request.Method.Method, headers, body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken) } });
                var response = await waiting.Task.WaitAsync(cancellationToken);
                if (response.TryGetProperty("error", out var error)) throw new HttpRequestException(error.GetString());
                var result = new HttpResponseMessage((System.Net.HttpStatusCode)response.GetProperty("status").GetInt32());
                if (response.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null) result.Content = new StringContent(body.GetString()!, Encoding.UTF8, "application/json");
                return result;
            }
            finally { Responses.TryRemove(id, out _); }
        }
    }
    sealed class Session(MeshlineClient client, RelayClientPool pool, Signer signer, HttpClient http, ManualClock time) : IAsyncDisposable
    {
        public MeshlineClient Client => client;
        public ManualClock Time => time;
        public Dictionary<string, QueryReader<MessageInfo>> HistoryReaders { get; } = new(StringComparer.Ordinal);
        public IDisposable TimeScope() => (IDisposable)typeof(MeshlineClient).Assembly.GetType("Meshline.Clock")!.GetMethod("Use", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [time])!;
        public async ValueTask DisposeAsync() { foreach (var reader in HistoryReaders.Values) await reader.DisposeAsync(); await client.DisposeAsync(); await pool.DisposeAsync(); http.Dispose(); signer.Dispose(); }
    }
    sealed class Registry(NetworkContext context, RelayEntry[] entries) : IRelayRegistry
    {
        public NetworkContext Context => context;
        public Task<RelayEntry?> GetRelayAsync(string relayId, CancellationToken cancellationToken = default) => Task.FromResult(entries.SingleOrDefault(value => value.RelayId == relayId));
        public async IAsyncEnumerable<RelayEntry> GetRelaysAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; foreach (var value in entries) { cancellationToken.ThrowIfCancellationRequested(); yield return value; } }
    }
    sealed class Signer(byte[] privateKey) : IAccountSigner, IDisposable
    {
        readonly ECDsa key = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateKey });
        public byte[] PrivateKey => privateKey;
        public ImmutableArray<byte> PublicKey { get { var q = key.ExportParameters(false).Q; return [(byte)(2 + (q.Y![^1] & 1)), .. q.X!]; } }
        public string AccountId => AccountAdapter.GetAccountId("neo:860833102", PublicKey.AsSpan());
        public Task<byte[]> SignAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(key.SignData(data.Span, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)); }
        public void Dispose() { key.Dispose(); CryptographicOperations.ZeroMemory(privateKey); }
    }
    sealed class Protector(byte[] key) : ISecretProtector
    {
        public Task<byte[]> ProtectAsync(ReadOnlyMemory<byte> plaintext, string purpose, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var result = new byte[plaintext.Length + 28]; RandomNumberGenerator.Fill(result.AsSpan(0, 12));
            using var aes = new AesGcm(key, 16); aes.Encrypt(result.AsSpan(0, 12), plaintext.Span, result.AsSpan(12, plaintext.Length), result.AsSpan(12 + plaintext.Length), Encoding.UTF8.GetBytes(purpose)); return Task.FromResult(result);
        }
        public Task<byte[]> UnprotectAsync(ReadOnlyMemory<byte> data, string purpose, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); var result = new byte[data.Length - 28]; using var aes = new AesGcm(key, 16);
            aes.Decrypt(data.Span[..12], data.Span.Slice(12, result.Length), data.Span[^16..], result, Encoding.UTF8.GetBytes(purpose)); return Task.FromResult(result);
        }
    }
}
