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
            Assert.True(fixture.Relay.Requests.Count(request => request.Method == "device.state.resolve") > deviceQueries);
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
