using Meshline.Models.Protocol;
using Meshline.Tests.Support;

namespace Meshline.Tests.Transport.Responses;

public sealed class MessageDeliveryTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    public static IEnumerable<object[]> DeliveryResponses()
    {
        (string Json, bool Valid)[] responses = [("""{"accepted_at":0,"status":"delivering"}""", true), ("""{"accepted_at":253402300799,"status":"target_accepted"}""", true), ("""{"accepted_at":1,"error":{"code":"not_found","message":"Unknown target."},"status":"failed"}""", true), ("""{"accepted_at":-1,"status":"target_accepted"}""", false), ("""{"accepted_at":253402300800,"status":"target_accepted"}""", false), ("""{"accepted_at":1,"status":"failed"}""", false), ("""{"accepted_at":1,"error":{"code":"not_found","message":"Unknown target."},"status":"target_accepted"}""", false), ("""{"accepted_at":1,"error":{"code":"not_found","message":"Unknown target."},"status":"delivering"}""", false)];
        foreach (var response in responses)
        {
            yield return [false, response.Json, response.Valid];
            yield return [true, response.Json, response.Valid];
        }
    }

    [Theory, MemberData(nameof(DeliveryResponses))]
    public async Task Typed_delivery_responses_are_validated_on_both_transports(bool webSocket, string json, bool valid)
    {
        using var scope = Clock.Use(new ManualClock());
        using var relay = new OfflineRelay();
        using var account = new AccountSigner();
        await using var pool = relay.Pool(account);
        var client = await pool.GetAsync(relay.RelayId, account, Token);
        var response = ResponseTransport.SendAsync<MessageDeliveryStatus>(
            client,
            relay,
            webSocket,
            HttpMethod.Get,
            "message.delivery.status",
            json,
            request => Assert.Equal("message.delivery.status", request.Method),
            Token);
        if (valid)
        {
            var delivery = await response.WaitAsync(TimeSpan.FromSeconds(10), Token);

            Assert.Equal(json, delivery.ToJson());
        }
        else
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() => response.WaitAsync(TimeSpan.FromSeconds(10), Token));

            Assert.Equal("The relay returned an inconsistent delivery result.", error.Message);
        }
    }
}
