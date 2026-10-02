using Meshline.Models;
using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshline.Tests.Conformance;

public sealed class ChannelPostUpdateTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> Cases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "Scenarios", "channel-post-updates.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var suite = document.RootElement;
        Assert.Equal(1, suite.GetProperty("version").GetInt32());
        var cases = suite.GetProperty("cases").EnumerateArray().ToArray();
        Assert.NotEmpty(cases);
        var ids = new HashSet<string>();
        foreach (var scenario in cases)
        {
            var id = scenario.GetProperty("id").GetString()!;
            Assert.True(ids.Add(id), $"Duplicate scenario: {id}");
            yield return [id, scenario.GetRawText()];
        }
    }

    [Theory, MemberData(nameof(Cases))]
    public async Task Shared_channel_post_updates_survive_restart(string id, string json)
    {
        using var document = JsonDocument.Parse(json);
        var scenario = document.RootElement;
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();
        var relay = new ChannelRelay(fixture);
        relay.Install();
        var manager = fixture.Client.ChannelManager;
        var channel = await manager.CreateChannelAsync(fixture.Relay.RelayId, id, cancellationToken: Token);
        var initial = scenario.GetProperty("initial");
        var original = await manager.PublishPostAsync(channel.Ref, new()
        {
            Body = ReadBody(initial.GetProperty("body")),
            Attachments = ReadAttachments(initial.GetProperty("attachments"))
        }, Token);
        AssertContent(initial, original, id);

        var steps = scenario.GetProperty("steps").EnumerateArray().ToArray();
        Assert.NotEmpty(steps);
        var stepIds = new HashSet<string>();
        foreach (var step in steps)
        {
            var stepId = step.GetProperty("id").GetString()!;
            Assert.True(stepIds.Add(stepId), $"Duplicate step: {stepId}");
            var fields = step.GetProperty("update");
            foreach (var field in fields.EnumerateObject())
                Assert.Contains(field.Name, new[] { "body", "attachments" });
            var update = new ChannelPostUpdate();
            if (fields.TryGetProperty("body", out var body))
                update.Body = body.ValueKind == JsonValueKind.Null ? FieldUpdate<MessageBody>.Delete : new(ReadBody(body)!);
            if (fields.TryGetProperty("attachments", out var attachments))
                update.Attachments = attachments.ValueKind == JsonValueKind.Null ? FieldUpdate<IReadOnlyList<ContentReference>>.Delete : new(ReadAttachments(attachments));
            var edited = await manager.EditPostAsync(original.Ref, update, Token);
            AssertContent(step.GetProperty("expected"), edited, stepId);
            Assert.Equal(original.Ref, edited.Ref);
            Assert.Equal(original.MessageId, edited.MessageId);
            Assert.Equal(original.Author, edited.Author);
        }

        await fixture.ReopenAsync();
        await using var reader = await fixture.Client.ChannelManager.GetPostsAsync(channel.Ref.ChannelId, cancellationToken: Token);
        var restored = Assert.Single(await reader.ReadNextAsync(10, Token));
        AssertContent(steps[^1].GetProperty("expected"), restored, "restart");
        Assert.Equal(original.Ref, restored.Ref);
        Assert.Equal(original.MessageId, restored.MessageId);
    }

    static MessageBody? ReadBody(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : ProtocolModel.FromJson<MessageBody>(value.GetRawText());
    static List<ContentReference> ReadAttachments(JsonElement value) => value.EnumerateArray()
        .Select(item => ProtocolModel.FromJson<ContentReference>(item.GetRawText())!).ToList();

    static void AssertContent(JsonElement expected, ChannelPostInfo actual, string step)
    {
        var content = new JsonObject
        {
            ["body"] = actual.Body is null ? null : JsonNode.Parse(actual.Body.ToJson()),
            ["attachments"] = new JsonArray((actual.Attachments ?? []).Select(item => JsonNode.Parse(item.ToJson())).ToArray())
        };
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), content),
            $"{step}: expected {expected.GetRawText()}, actual {content}");
    }
}
