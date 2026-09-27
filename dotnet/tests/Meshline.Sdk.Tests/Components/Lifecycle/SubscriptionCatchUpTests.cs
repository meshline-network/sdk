using Meshline.Components;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Lifecycle;

public sealed class SubscriptionCatchUpTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Activation_catches_up_changes_before_subscription_without_repeating_confirmed_subscriptions(bool group)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var received = AsyncTest.Signal();
        ClientComponent manager;
        Func<ObservedRequest, HttpResponseMessage> respond;
        Action append;
        var syncMethod = group ? "group.sync" : "channel.read";
        var subscribeMethod = group ? "group.subscribe" : "channel.subscribe";
        var idsField = group ? "group_ids" : "channel_ids";
        if (group)
        {
            var host = new GroupRelay(fixture);
            host.Install();
            var created = await fixture.Client.GroupManager.CreateGroupAsync(fixture.Relay.RelayId, new()
            {
                Name = "before",
                MemberCapacity = 10,
                InvitePolicy = GroupInvitePolicy.Administrators
            }, Token);
            var update = new Meshline.Models.Protocol.GroupUpdate
            {
                GroupId = created.Ref.GroupId,
                PrevHash = "sha256:" + Base64Url.EncodeToString(SHA256.HashData(host.Events[0].Payload.GetSigningInput(TestNetwork.Context))),
                Name = new("after activation"),
                DeviceSignature = []
            };
            update = update with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(update.GetSigningInput(TestNetwork.Context), Token)] };
            append = () => host.Events.Add(new GroupEvent
            {
                Sequence = 1,
                Epoch = 0,
                Payload = update,
                AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds(),
                SignerDeviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context)
            });
            fixture.Client.GroupManager.GroupChanged += (_, change) =>
            {
                if (change.Group.Group.Name == "after activation") received.TrySetResult();
            };
            manager = fixture.Client.GroupManager;
            respond = host.Respond;
        }
        else
        {
            var host = new ChannelRelay(fixture);
            host.Install();
            var created = await fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
            await fixture.Client.ChannelManager.FollowAsync(created.Ref, Token);
            var post = new ChannelPost
            {
                ChannelId = created.Ref.ChannelId,
                MessageId = Identifiers.CreateMessageId(),
                Body = new() { ContentType = "text/plain", Text = "during activation" },
                DeviceSignature = []
            };
            post = post with { DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(post.GetSigningInput(TestNetwork.Context), Token)] };
            append = () => host.Events.Add(new ChannelEvent
            {
                Sequence = 1,
                DescriptorRev = 0,
                Payload = post,
                AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds(),
                SignerDeviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context)
            });
            fixture.Client.ChannelManager.TimelineChanged += (_, change) =>
            {
                if (change.Changes.Count > 0) received.TrySetResult();
            };
            manager = fixture.Client.ChannelManager;
            respond = host.Respond;
        }

        var pending = new ConcurrentQueue<(RpcRequest Request, MemorySocket Socket)>();
        var subscriptionReceived = AsyncTest.Signal();
        var activated = AsyncTest.Signal();
        var reads = 0;
        manager.BackgroundError += (_, error) => received.TrySetException(error.Error);
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            Assert.Equal(subscribeMethod, request.Method);
            if (activated.Task.IsCompleted || request.Params![idsField].GetArrayLength() == 0)
                socket.Reply(request.Id!, "null");
            else
            {
                pending.Enqueue((request, socket));
                subscriptionReceived.TrySetResult();
            }
            return Task.CompletedTask;
        };
        fixture.Relay.Handler = async (request, token) =>
        {
            if (request.Method != syncMethod) return respond(request);
            var number = Interlocked.Increment(ref reads);
            if (number == 1) await subscriptionReceived.Task.WaitAsync(token);
            var response = respond(request);
            if (number == 2)
            {
                // The response is already formed. This change predates activation,
                // so no WebSocket notification is sent for it.
                append();
                activated.TrySetResult();
                while (pending.TryDequeue(out var delayed)) delayed.Socket.Reply(delayed.Request.Id!, "null");
            }
            return response;
        };

        await manager.StartAsync(Token);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.True(Volatile.Read(ref reads) >= 3);

        var beforePoll = Volatile.Read(ref reads);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(30));
        await AsyncTest.UntilAsync(() => Volatile.Read(ref reads) > beforePoll);
        await manager.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Single(fixture.Relay.Sockets.SelectMany(socket => socket.Requests),
            request => request.Method == subscribeMethod && request.Params![idsField].GetArrayLength() > 0);
    }
}
