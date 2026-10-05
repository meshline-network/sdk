using Meshline.Components;
using Meshline.Models;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Transport;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

namespace Meshline.Tests.Components.Channels;

public sealed class ChannelTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Refollowing_after_an_unacknowledged_clear_restores_the_subscription()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var host = new ChannelRelay(fixture);
        host.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
        await manager.FollowAsync(channel.Ref, Token);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        manager.BackgroundError += (_, error) => errors.Enqueue(error.Error);
        var clearReceived = AsyncTest.Signal();
        var restored = AsyncTest.Signal();
        var subscriptions = 0;
        var removals = 0;
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            Assert.Equal("channel.subscribe", request.Method);
            if (request.Params!["channel_ids"].GetArrayLength() == 0)
            {
                if (Interlocked.Increment(ref removals) == 1)
                {
                    // The server applied the clear, but its response never arrives.
                    clearReceived.TrySetResult();
                    return Task.CompletedTask;
                }
            }
            else if (Interlocked.Increment(ref subscriptions) == 2)
                restored.TrySetResult();
            socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };

        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => Volatile.Read(ref subscriptions) == 1);
        await manager.UnfollowAsync(channel.Ref, Token);
        await clearReceived.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.FollowAsync(channel.Ref, Token);
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(60));
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.First().ClientStreamDisposed && fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(2)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(2));
        await restored.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        Assert.Equal(2, Volatile.Read(ref subscriptions));
        Assert.Equal(2, fixture.Relay.Sockets.Count);
        Assert.Contains(errors, error => error is TimeoutException);
        Assert.All(errors, error => Assert.True(error is TimeoutException or IOException));
    }

    [Fact]
    public async Task Unfollowing_the_last_channel_retries_a_failed_unsubscription_in_the_background()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
        await manager.FollowAsync(channel.Ref, Token);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        manager.BackgroundError += (_, error) => errors.Enqueue(error.Error);
        var removals = 0;
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            if (request.Params!["channel_ids"].GetArrayLength() == 0 && Interlocked.Increment(ref removals) == 1)
                socket.Push(new RpcFailure
                {
                    Id = request.Id,
                    Error = new() { Code = RelayError.RpcCodes["temporarily_unavailable"], Message = "Retry subscription cleanup." }
                }.ToJson());
            else socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
        await manager.StartAsync(Token);
        await AsyncTest.UntilAsync(() => fixture.Relay.Sockets.Any(socket => socket.Requests.Any(request => request.Method == "channel.subscribe")));

        await manager.UnfollowAsync(channel.Ref, Token);
        await AsyncTest.UntilAsync(() => !errors.IsEmpty && fixture.Relay.Clock.HasTimerDueWithin(TimeSpan.FromSeconds(5)));
        fixture.Relay.Clock.Advance(TimeSpan.FromSeconds(5));
        await AsyncTest.UntilAsync(() => Volatile.Read(ref removals) == 2);
        await manager.StopAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);

        var error = Assert.IsType<RelayException>(Assert.Single(errors));
        Assert.Equal("temporarily_unavailable", error.Error.Code);
        Assert.Single(fixture.Relay.Sockets);
    }

    [Fact]
    public async Task Publish_edit_delete_follow_and_close_persist_across_restart()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var changes = new List<ChannelPostChangeKind>();
        manager.TimelineChanged += (_, value) => changes.AddRange(value.Changes.Select(change => change.ChangeKind));
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", "description", cancellationToken: Token);
        await manager.FollowAsync(channel.Ref, Token);
        await manager.FollowAsync(channel.Ref, Token);
        var updated = await manager.UpdateChannelAsync(channel.Ref, new()
        {
            Name = new("renamed"),
            Description = FieldUpdate<string>.Delete
        }, Token);

        Assert.Equal("renamed", updated.Descriptor!.Name);
        Assert.Null(updated.Descriptor.Description);

        var post = await manager.PublishPostAsync(channel.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "original"
            }
        }, Token);
        var edited = await manager.EditPostAsync(post.Ref, new()
        {
            Body = new(new()
            {
                ContentType = "text/plain",
                Text = "edited"
            })
        }, Token);

        Assert.Equal("edited", edited.Body!.Text);

        await manager.DeletePostAsync(post.Ref, Token);
        await using (var posts = await manager.GetPostsAsync(cancellationToken: Token))
            Assert.Empty(await posts.ReadNextAsync(10, Token));

        Assert.Equal([ChannelPostChangeKind.Added, ChannelPostChangeKind.Edited, ChannelPostChangeKind.Deleted], changes);

        await manager.CloseChannelAsync(channel.Ref, Token);

        await fixture.ReopenAsync();
        var restored = await fixture.Client.ChannelManager.GetChannelAsync(channel.Ref, Token);

        Assert.Equal(ChannelStatus.Closed, restored.Descriptor!.Status);
        Assert.True(restored.IsFollowed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Client.ChannelManager.PublishPostAsync(channel.Ref, new(), Token));

        await fixture.Client.ChannelManager.UnfollowAsync(channel.Ref, Token);
        await using var followed = await fixture.Client.ChannelManager.GetFollowedAsync(cancellationToken: Token);

        Assert.Empty(await followed.ReadNextAsync(10, Token));
    }

    [Fact]
    public async Task Lost_creation_response_is_confirmed_after_restart_without_resubmission()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new ChannelRelay(fixture)
        {
            LoseNextResponse = true
        };
        relay.Install();

        await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Client.ChannelManager.CreateChannelAsync(fixture.Relay.RelayId, "recover", cancellationToken: Token));

        await using (var db = fixture.Database.Open())
            Assert.Single(await db.ChannelOperations.ToListAsync(Token));

        await fixture.ReopenAsync();
        var signal = AsyncTest.Signal();
        fixture.Client.ChannelManager.ChannelChanged += (_, _) => signal.TrySetResult();
        fixture.Client.ChannelManager.BackgroundError += (_, error) => signal.TrySetException(error.Error);
        await fixture.Client.ChannelManager.StartAsync(Token);
        await signal.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.ChannelManager.StopAsync(Token);
        await using var verify = fixture.Database.Open();

        Assert.Empty(await verify.ChannelOperations.ToListAsync(Token));
        Assert.Single(await verify.Channels.ToListAsync(Token));
        Assert.Single(fixture.Relay.Requests, request => request.Method == "channel.create");
    }

    [Theory]
    [InlineData("channel")]
    [InlineData("relay")]
    [InlineData("account")]
    [InlineData("signature")]
    public async Task Resolved_descriptors_still_require_matching_resource_account_and_signature(string defect)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
        using var otherAccount = new AccountSigner();
        var descriptor = channel.Descriptor!;
        var certificate = fixture.Client.Device!;
        switch (defect)
        {
            case "channel":
                descriptor = descriptor with
                {
                    Nonce = [.. new byte[16]]
                };
                break;
            case "relay":
                descriptor = descriptor with
                {
                    RelayId = "0x" + new string('1', 40)
                };
                break;
            case "account":
                certificate = new DeviceSigner(otherAccount, fixture.Relay.Clock).Certificate;
                break;
            case "signature":
                descriptor = descriptor with
                {
                    DeviceSignature = [.. new byte[64]]
                };
                break;
        }

        if (defect is "channel" or "relay")
            descriptor = descriptor with
            {
                ChannelId = Identifiers.DeriveChannelId(descriptor.Creator, descriptor.RelayId, descriptor.Nonce.AsSpan(), TestNetwork.Context)
            };
        var result = new ChannelResolveResult
        {
            Descriptor = descriptor,
            SignerCertificate = certificate
        };

        Assert.Null(result.Validate(TestNetwork.Context));

        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "channel.resolve" ? OfflineRelay.Json(result) : relay.Respond(request));
        if (defect == "signature")
        {
            var error = await Assert.ThrowsAsync<CryptographicException>(() => manager.GetChannelAsync(channel.Ref, Token));

            Assert.Equal("The channel descriptor signature is invalid.", error.Message);
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => manager.GetChannelAsync(channel.Ref, Token));

            Assert.Equal("The channel descriptor or signing certificate belongs to another resource or account.", error.Message);
        }
    }

    [Theory]
    [InlineData("descriptor_metadata")]
    [InlineData("descriptor_signature")]
    [InlineData("signature")]
    [InlineData("order")]
    [InlineData("missing_original")]
    public async Task Invalid_history_is_rejected_without_committing_projection(string defect)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
        var post = await manager.PublishPostAsync(channel.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "hello"
            }
        }, Token);
        await manager.EditPostAsync(post.Ref, new()
        {
            Body = new(new()
            {
                ContentType = "text/plain",
                Text = "edit"
            })
        }, Token);
        await using (var db = fixture.Database.Open())
            await db.ChannelPosts.ExecuteDeleteAsync(Token);
        relay.TransformPage = page => defect switch
        {
            "descriptor_metadata" => page with
            {
                Events = [page.Events[0] with
                {
                    Payload = ((ChannelDescriptor)page.Events[0].Payload)with
                    {
                        Name = " "
                    }
                }
                ]
            },
            "descriptor_signature" => page with
            {
                Events = [page.Events[0] with
                {
                    Payload = ((ChannelDescriptor)page.Events[0].Payload)with
                    {
                        DeviceSignature = [..new byte[64]]
                    }
                }
                ]
            },
            "signature" => page with
            {
                Events = [page.Events[1] with
                {
                    Payload = ((ChannelPost)page.Events[1].Payload)with
                    {
                        DeviceSignature = [..new byte[64]]
                    }
                }
                ]
            },
            "order" => page with
            {
                Events = [.. page.Events.Reverse()]
            },
            _ => page with
            {
                Events = [page.Events[^1]]
            }
        };
        var exception = await Record.ExceptionAsync(() => manager.LoadChannelHistoryAsync(channel.Ref, cancellationToken: Token));

        Assert.True(exception is CryptographicException or InvalidDataException, exception?.ToString());

        if (defect == "descriptor_metadata")
            Assert.Equal("The channel name must contain non-whitespace text and cannot exceed 256 UTF-8 bytes.", Assert.IsType<InvalidDataException>(exception).Message);
        if (defect == "descriptor_signature")
            Assert.Equal("The channel descriptor signature is invalid.", Assert.IsType<CryptographicException>(exception).Message);
        await using var verify = fixture.Database.Open();

        Assert.Empty(await verify.ChannelPosts.ToListAsync(Token));
    }

    [Fact]
    public async Task Sparse_retained_history_preserves_original_sequence()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);
        await manager.PublishPostAsync(channel.Ref, new()
        {
            Body = new()
            {
                ContentType = "text/plain",
                Text = "retained"
            }
        }, Token);
        relay.Events[1] = relay.Events[1] with
        {
            Sequence = 9
        };
        relay.Events.RemoveAt(0);
        await using (var db = fixture.Database.Open())
            await db.ChannelPosts.ExecuteDeleteAsync(Token);
        var page = await manager.LoadChannelHistoryAsync(channel.Ref, cancellationToken: Token);

        Assert.Equal(9, Assert.Single(page.Items).Ref.Sequence);
    }

    [Fact]
    public async Task Canceled_history_read_is_rejected()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => manager.LoadChannelHistoryAsync(channel.Ref, cancellationToken: canceled.Token));
    }

    [Fact]
    public async Task Negative_history_cursor_is_rejected()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, "news", cancellationToken: Token);

        await Assert.ThrowsAsync<ArgumentException>(() => manager.LoadChannelHistoryAsync(channel.Ref, new() { Cursor = "-1" }, Token));
    }
}
