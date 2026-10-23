using Meshline.Components;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.Json;

namespace Meshline.Tests.Components.Messages;

public sealed class MessageSynchronizationTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    static DirectMessageDraft Draft(string text = "hello") => new()
    {
        Body = new()
        {
            ContentType = "text/plain",
            Text = text
        }
    };
    [Theory]
    [InlineData("valid")]
    [InlineData("ciphertext")]
    [InlineData("signature")]
    public async Task Synchronization_deduplicates_valid_messages_and_consumes_rejected_entries(string variation)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);
        MessageSendRequest request;
        await using (var db = fixture.Database.Open())
        {
            request = ProtocolModel.FromJson<MessageSendRequest>((await db.MessageOutbox.FindAsync([queued.MessageId], Token))!.RequestJson)!;
            await db.Messages.ExecuteDeleteAsync(Token);
            await db.MessageOutbox.ExecuteDeleteAsync(Token);
        }

        var envelope = request.Envelope;
        if (variation == "signature")
        {
            var bytes = envelope.DeviceSignature.ToArray();
            bytes[0] ^= 1;
            envelope = envelope with
            {
                DeviceSignature = [.. bytes]
            };
        }

        if (variation == "ciphertext")
        {
            var bytes = envelope.Payload.Ciphertext.ToArray();
            bytes[^1] ^= 1;
            envelope = envelope with
            {
                Payload = envelope.Payload with
                {
                    Ciphertext = [.. bytes]
                }
            };
            envelope = envelope with
            {
                DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(envelope.GetSigningInput(TestNetwork.Context), Token)]
            };
        }

        var entry = new MessageTimelineEntry
        {
            Sequence = 0,
            Envelope = envelope,
            KeyBox = request.RecipientBoxes[0],
            AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds()
        };
        var observed = AsyncTest.Signal();
        fixture.Client.MessageManager.MessageReceived += (_, _) => observed.TrySetResult();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (variation != "valid" && error.Error is InvalidDataException)
                observed.TrySetResult();
            else
                observed.TrySetException(error.Error);
        };
        fixture.Relay.Handler = (http, _) => Task.FromResult(http.Method == "message.timeline.sync" ? OfflineRelay.Json(new MessageTimelinePage
        {
            Items = ParseAfter(http) < 1 ? [entry, entry with { Sequence = 1 }] : [],
            Certificates = [fixture.Client.Device!],
            HasMore = false
        }) : fixture.Relay.Respond(http));
        var routeQueries = fixture.Relay.Requests.Count(request => request.Method == "account.route.resolve");
        var deviceQueries = fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve");
        await fixture.Client.MessageManager.StartAsync(Token);
        await AwaitSignal(observed.Task, fixture);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(routeQueries, fixture.Relay.Requests.Count(request => request.Method == "account.route.resolve"));
        if (variation == "valid")
            Assert.Equal(deviceQueries, fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve"));
        await using var verify = fixture.Database.Open();

        Assert.Equal(1, (await verify.AccountTimelines.SingleAsync(Token)).Sequence);
        Assert.Equal(variation == "valid" ? 1 : 0, await verify.Messages.CountAsync(row => row.MessageId == queued.MessageId, Token));

        if (variation == "valid")
            Assert.Equal("hello", (await fixture.Client.MessageManager.GetMessageAsync(new()
            {
                Sender = fixture.Account.AccountId,
                MessageId = queued.MessageId
            }, Token))!.Body!.Text);
    }

    [Theory]
    [InlineData("contacts", "success")]
    [InlineData("groups", "success")]
    [InlineData("direct", "success")]
    [InlineData("direct", "network")]
    [InlineData("direct", "signature")]
    [InlineData("direct", "missing")]
    [InlineData("direct", "revoked")]
    public async Task New_own_device_payloads_require_current_state_and_failed_lookups_preserve_progress(string kind, string outcome)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        await using var database = new TestDatabase();
        await database.MigrateAsync();
        await using var pool = fixture.Relay.Pool(fixture.Account);
        await using var second = new MeshlineClient(fixture.Relay.Options(fixture.Account), database.Options, pool, fixture.Protector, fixture.Account);
        await second.InitializeAsync(Token);
        await second.DeviceManager.CreateDeviceAsync(TimeSpan.FromDays(2), Token);
        var previous = fixture.Client.DeviceState!;
        await second.DeviceManager.PublishDeviceStateAsync(fixture.Relay.RelayId, [fixture.Client.Device!, second.Device!],
            options: new() { PreviousState = previous }, cancellationToken: Token);
        var newer = second.DeviceState!;
        TypedProtocolModel payload = kind switch
        {
            "contacts" => new AccountContactSync { Records = [] },
            "groups" => new AccountGroupPrivateStateRequest(),
            _ => new DirectMessage { Body = Draft().Body }
        };
        await second.MessageManager.SendPayloadAsync(fixture.Account.AccountId, payload, Token);
        MessageSendRequest request;
        await using (var db = database.Open())
            request = ProtocolModel.FromJson<MessageSendRequest>((await db.MessageOutbox.SingleAsync(Token)).RequestJson)!;
        var entry = new MessageTimelineEntry
        {
            Sequence = 0,
            Envelope = request.Envelope,
            KeyBox = request.RecipientBoxes.Single(box => box.DeviceId == fixture.Client.Device!.GetDeviceId(TestNetwork.Context)),
            AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds()
        };
        var failed = AsyncTest.Signal();
        Exception? error = null;
        fixture.Client.MessageManager.BackgroundError += (_, args) =>
        {
            // A connection diagnostic can arrive before the synchronization pass
            // handles that failure. Wait for the pass before stopping its runtime.
            if ((outcome is "network" or "signature" or "missing")
                && (args.Operation != BackgroundOperation.Synchronize || args.Resource != fixture.Relay.RelayId)) return;
            error = args.Error;
            failed.TrySetResult();
        };
        var queries = 0;
        fixture.Relay.Handler = (http, _) =>
        {
            if (http.Method == "message.timeline.sync")
                return Task.FromResult(OfflineRelay.Json(new MessageTimelinePage
                {
                    Items = ParseAfter(http) < 0 ? [entry] : [],
                    Certificates = [second.Device!],
                    HasMore = false
                }));
            if (http.Method == "device.state.resolve")
            {
                Interlocked.Increment(ref queries);
                if (outcome == "network") throw new HttpRequestException("offline");
                if (outcome == "signature") return Task.FromResult(OfflineRelay.Json(newer with { AccountSignature = [.. new byte[64]] }));
                if (outcome == "missing") return Task.FromResult(OfflineRelay.Error("not_found"));
                if (outcome == "revoked")
                {
                    var revoked = newer with { Revision = newer.Revision + 1, Certificates = [fixture.Client.Device!] };
                    return Task.FromResult(OfflineRelay.Json(revoked with { AccountSignature = [.. fixture.Account.Sign(revoked.GetSigningInput(TestNetwork.Context))] }));
                }
            }
            return Task.FromResult(fixture.Relay.Respond(http));
        };
        if (outcome is "network" or "signature" or "missing")
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                failed = AsyncTest.Signal();
                await fixture.Client.MessageManager.StartAsync(Token);
                await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
                await fixture.Client.MessageManager.StopAsync(Token);
                await using var db = fixture.Database.Open();
                Assert.False(await db.Messages.AnyAsync(row => row.MessageId == request.Envelope.MessageId, Token));
                Assert.True((await db.AccountTimelines.SingleOrDefaultAsync(Token))?.Sequence is null or -1);
                Assert.Equal(1, queries); // Failed necessary reads also observe the one-minute cooldown.
            }
            if (outcome == "signature")
                Assert.IsType<InvalidDataException>(Assert.IsType<InvalidOperationException>(error).InnerException);
            outcome = "success";
            error = null;
            fixture.Relay.Clock.Advance(TimeSpan.FromMinutes(1));
        }
        await fixture.Client.MessageManager.StartAsync(Token);
        await AsyncTest.UntilAsync(async () =>
        {
            await using var db = fixture.Database.Open();
            return (await db.AccountTimelines.SingleOrDefaultAsync(Token))?.Sequence == 0;
        });
        await fixture.Client.MessageManager.StopAsync(Token);
        if (kind == "groups")
        {
            fixture.Client.GroupManager.BackgroundError += (_, args) => error = args.Error;
            await fixture.Client.GroupManager.StartAsync(Token);
            await AsyncTest.UntilAsync(async () =>
            {
                await using var db = fixture.Database.Open();
                var sequence = (await db.Messages.SingleAsync(row => row.MessageId == request.Envelope.MessageId, Token)).LocalSequence;
                return (await db.GroupAccountMessageCursors.SingleOrDefaultAsync(Token))?.LocalSequence >= sequence;
            });
            await fixture.Client.GroupManager.StopAsync(Token);
        }
        await using var verify = fixture.Database.Open();
        Assert.Equal(outcome != "revoked", await verify.Messages.AnyAsync(row => row.MessageId == request.Envelope.MessageId, Token));
        if (outcome == "revoked")
            Assert.IsType<InvalidDataException>(error);
        else
        {
            Assert.Null(error);
            Assert.Equal(newer.Revision, fixture.Client.DeviceState!.Revision);
        }
    }

    [Fact]
    public async Task Storage_failure_leaves_cursor_for_retry_then_commits_message_atomically()
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var queued = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft(), Token);
        MessageSendRequest request;
        await using (var db = fixture.Database.Open())
        {
            request = ProtocolModel.FromJson<MessageSendRequest>((await db.MessageOutbox.FindAsync([queued.MessageId], Token))!.RequestJson)!;
            await db.Messages.ExecuteDeleteAsync(Token);
            await db.MessageOutbox.ExecuteDeleteAsync(Token);
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_message BEFORE INSERT ON Messages BEGIN SELECT RAISE(ABORT, 'injected storage failure'); END;", Token);
        }

        var failed = AsyncTest.Signal();
        var received = AsyncTest.Signal();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Error is DbUpdateException)
                failed.TrySetResult();
            else
                failed.TrySetException(error.Error);
        };
        fixture.Client.MessageManager.MessageReceived += (_, _) => received.TrySetResult();
        var entry = new MessageTimelineEntry
        {
            Sequence = 0,
            Envelope = request.Envelope,
            KeyBox = request.RecipientBoxes[0],
            AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds()
        };
        fixture.Relay.Handler = (http, _) => Task.FromResult(http.Method == "message.timeline.sync" ? OfflineRelay.Json(new MessageTimelinePage
        {
            Items = ParseAfter(http) < 0 ? [entry] : [],
            Certificates = [fixture.Client.Device!],
            HasMore = false
        }) : fixture.Relay.Respond(http));
        await fixture.Client.MessageManager.StartAsync(Token);
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        await using (var db = fixture.Database.Open())
        {
            Assert.Equal(-1, (await db.AccountTimelines.SingleAsync(Token)).Sequence);
            Assert.Empty(await db.Messages.ToListAsync(Token));

            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_message;", Token);
        }

        await fixture.Client.MessageManager.StartAsync(Token);
        await received.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await fixture.Client.MessageManager.StopAsync(Token);
        await using var verify = fixture.Database.Open();

        Assert.Equal(0, (await verify.AccountTimelines.SingleAsync(Token)).Sequence);
        Assert.Single(await verify.Messages.ToListAsync(Token));
    }

    [Theory]
    [InlineData("date_range")]
    [InlineData("max_date")]
    [InlineData("future")]
    public async Task Invalid_contact_timestamp_rejects_the_batch_and_preserves_progress_across_restart(string scenario)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        using var firstContact = new AccountSigner();
        using var secondContact = new AccountSigner();
        await fixture.InitializeAsync();

        var firstRecord = new ContactRecord
        {
            Account = firstContact.AccountId,
            Status = ContactRelationshipState.Deleted,
            UpdatedAt = 0
        };
        var secondRecord = firstRecord with
        {
            Account = secondContact.AccountId,
            UpdatedAt = scenario switch
            {
                "date_range" => 253402300800L,
                "max_date" => DateTimeOffset.MaxValue.ToUnixTimeSeconds(),
                _ => fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds() + 301
            }
        };
        using var records = JsonDocument.Parse("[" + firstRecord.ToJson() + "," + secondRecord.ToJson() + "]");
        // Simulate a remote sender whose payload bypasses this SDK's model validation.
        var payload = new TypedProtocolModel("meshline.account.contacts.sync")
        {
            AdditionalProperties = ImmutableDictionary<string, JsonElement>.Empty.Add("records", records.RootElement)
        };
        await fixture.Client.MessageManager.SendPayloadAsync(fixture.Account.AccountId, payload, Token);
        var goodMessage = await fixture.Client.MessageManager.SendMessageAsync(fixture.Account.AccountId, Draft("after invalid contact"), Token);
        MessageSendRequest[] requests;
        await using (var database = fixture.Database.Open())
        {
            requests = (await database.MessageOutbox.ToListAsync(Token))
                .OrderBy(row => row.MessageId == goodMessage.MessageId)
                .Select(row => ProtocolModel.FromJson<MessageSendRequest>(row.RequestJson)!)
                .ToArray();
            Assert.Equal(2, requests.Length);
            await database.MessageOutbox.ExecuteDeleteAsync(Token);
            await database.Messages.ExecuteDeleteAsync(Token);
        }

        var entries = requests.Select((request, index) => new MessageTimelineEntry
        {
            Sequence = index,
            AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds(),
            Envelope = request.Envelope,
            KeyBox = request.RecipientBoxes[0]
        }).ToArray();
        var rejections = new ConcurrentQueue<Exception>();
        var received = AsyncTest.Signal();
        fixture.Client.MessageManager.BackgroundError += (_, error) =>
        {
            if (error.Error is InvalidDataException)
                rejections.Enqueue(error.Error);
            else
                received.TrySetException(error.Error);
        };
        fixture.Client.MessageManager.MessageReceived += (_, _) => received.TrySetResult();
        fixture.Relay.Handler = (request, _) => Task.FromResult(request.Method == "message.timeline.sync"
            ? OfflineRelay.Json(new MessageTimelinePage
            {
                Items = entries.Where(entry => entry.Sequence > ParseAfter(request)).ToImmutableArray(),
                Certificates = [fixture.Client.Device!],
                HasMore = false
            }) : fixture.Relay.Respond(request));

        await fixture.Client.MessageManager.StartAsync(Token);
        await AwaitSignal(received.Task, fixture);
        await fixture.Client.MessageManager.StopAsync(Token);
        Assert.Equal(scenario == "date_range" ? "The contact update time exceeds the supported date range."
            : "The contact update time is more than five minutes ahead of the local clock.", Assert.Single(rejections).Message);
        await using (var database = fixture.Database.Open())
        {
            Assert.Empty(await database.Contacts.ToListAsync(Token));
            Assert.Equal(goodMessage.MessageId, (await database.Messages.SingleAsync(Token)).MessageId);
            Assert.Equal(1, (await database.AccountTimelines.SingleAsync(Token)).Sequence);
        }

        await fixture.ReopenAsync();
        var resumed = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Relay.Handler = (request, _) =>
        {
            if (request.Method == "message.timeline.sync")
                resumed.TrySetResult(ParseAfter(request));
            return Task.FromResult(fixture.Relay.Respond(request));
        };
        await fixture.Client.MessageManager.StartAsync(Token);
        Assert.Equal(1, await resumed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token));
        await fixture.Client.MessageManager.StopAsync(Token);
    }

    static async Task AwaitSignal(Task task, TestClient fixture)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        }
        catch (TimeoutException exception)
        {
            throw new InvalidOperationException(
                "HTTP: " + string.Join(",", fixture.Relay.Requests.Select(r => r.Method)) + "; sockets=" + fixture.Relay.Sockets.Count + "; socket requests=" + string.Join(",", fixture.Relay.Sockets.SelectMany(s => s.Requests).Select(r => r.Method)),
                exception);
        }
    }

    static long ParseAfter(ObservedRequest request) => long.Parse(
        RequestQuery.Parse(request)["after"],
        System.Globalization.CultureInfo.InvariantCulture);
}
