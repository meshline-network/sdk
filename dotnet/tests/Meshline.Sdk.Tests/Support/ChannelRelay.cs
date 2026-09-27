using Meshline.Models.Protocol;
using System.Net;

namespace Meshline.Tests.Support;

internal sealed class ChannelRelay(TestClient fixture)
{
    public List<ChannelEvent> Events { get; } = [];
    public Dictionary<long, ChannelDescriptor> Descriptors { get; } = [];
    public Func<ChannelReadPage, ChannelReadPage>? TransformPage { get; set; }
    public bool LoseNextResponse { get; set; }

    public void Install()
    {
        fixture.Relay.Handler = (request, _) => Task.FromResult(Respond(request));
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            Assert.Contains(request.Method, new[] { "channel.subscribe", "channel.unsubscribe" });

            socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
    }

    public HttpResponseMessage Respond(ObservedRequest request)
    {
        var query = RequestQuery.Parse(request);
        switch (request.Method)
        {
            case "channel.resolve":
                var revision = query.TryGetValue("revision", out var number) ? long.Parse(number) : Descriptors.Keys.DefaultIfEmpty(-1).Max();
                return Descriptors.TryGetValue(revision, out var descriptor) ? OfflineRelay.Json(new ChannelResolveResult
                {
                    Descriptor = descriptor,
                    SignerCertificate = fixture.Client.Device!
                }) : OfflineRelay.Error("not_found");
            case "channel.create":
            case "channel.update":
                var update = ProtocolModel.FromJson<ChannelDescriptor>(request.Body!)!;
                Descriptors[update.Revision] = update;
                Append(update);
                return Accepted();
            case "channel.close":
                var close = ProtocolModel.FromJson<ChannelCloseRequest>(request.Body!)!;
                var final = Descriptors.Values.MaxBy(value => value.Revision)! with
                {
                    Status = ChannelStatus.Closed,
                    Revision = close.Revision,
                    UpdatedAt = close.UpdatedAt,
                    DeviceSignature = close.DeviceSignature
                };
                Descriptors[final.Revision] = final;
                Append(final);
                return Accepted();
            case "channel.post":
                var post = ProtocolModel.FromJson<ChannelPost>(request.Body!)!;
                var existing = Events.SingleOrDefault(value => value.Payload is ChannelPost previous && previous.MessageId == post.MessageId);
                var entry = existing ?? Append(post);
                return OfflineRelay.Json(new SequenceResult { Sequence = entry.Sequence });
            case "channel.post.edit":
            case "channel.post.delete":
                Append(ProtocolModel.FromJson<TypedProtocolModel>(request.Body!)!);
                return Accepted();
            case "channel.read":
                IEnumerable<ChannelEvent> entries = Events;
                if (query.TryGetValue("after", out var after))
                    entries = entries.Where(value => value.Sequence > long.Parse(after));
                if (query.TryGetValue("before", out var before))
                    entries = entries.Where(value => value.Sequence < long.Parse(before));
                var all = entries.ToArray();
                var limit = query.TryGetValue("limit", out var count) ? int.Parse(count) : 100;
                var page = new ChannelReadPage
                {
                    Events = [.. query.ContainsKey("after") ? all.Take(limit) : all.TakeLast(limit)],
                    Certificates = [fixture.Client.Device!],
                    HasMore = all.Length > limit
                };
                return OfflineRelay.Json(TransformPage?.Invoke(page) ?? page);
            default:
                return fixture.Relay.Respond(request);
        }
    }

    ChannelEvent Append(TypedProtocolModel payload)
    {
        var entry = new ChannelEvent
        {
            Sequence = Events.Count == 0 ? 0 : Events[^1].Sequence + 1,
            DescriptorRev = Descriptors.Keys.Max(),
            Payload = payload,
            AcceptedAt = fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds(),
            SignerDeviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context)
        };
        Events.Add(entry);
        return entry;
    }

    HttpResponseMessage Accepted()
    {
        if (LoseNextResponse)
        {
            LoseNextResponse = false;
            throw new HttpRequestException("Accepted before disconnect");
        }

        return new(HttpStatusCode.NoContent);
    }
}
