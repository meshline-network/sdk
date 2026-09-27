using Meshline.Models.Protocol;
using Meshline.Storage;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;

namespace Meshline.Tests.Components.Groups;

public sealed class GroupEncryptionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("valid")]
    [InlineData("epoch")]
    [InlineData("sender")]
    [InlineData("device")]
    [InlineData("secret")]
    [InlineData("ciphertext")]
    public async Task Restart_recovers_fixed_group_ciphertext_and_rejects_tampered_context(string variation)
    {
        using var time = Clock.Use(new ManualClock());
        await using var fixture = new TestClient();
        await fixture.InitializeAsync();

        var vector = ProtocolVectors.Read("groups").GetProperty("keying");
        var input = vector.GetProperty("input");
        var expected = vector.GetProperty("expected");
        var envelope = ProtocolModel.FromJson<GroupMessageEnvelope>(expected.GetProperty("message").GetRawText())!;
        var secret = ProtocolVectors.Decode(expected.GetProperty("epoch_application_secret").GetString()!);
        var account = input.GetProperty("account").GetString()!;
        var device = input.GetProperty("device_id").GetString()!;
        if (variation == "epoch")
            envelope = envelope with
            {
                Epoch = envelope.Epoch + 1
            };
        if (variation == "sender")
            account = fixture.Account.AccountId;
        if (variation == "device")
            device = "dev_AAAAAAAAAAAAAAAAAAAAAA";
        if (variation == "secret")
            secret[0] ^= 1;
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
        }

        // Seed the crash boundary after event / epoch persistence, before decryption.
        // This crypto vector's synthetic signer ID has no certificate; admission and
        // certificate validation are covered separately by signed group workflows.
        var purpose = $"Meshline/{TestNetwork.Context}/{fixture.Account.AccountId}/{fixture.Client.Device!.GetDeviceId(TestNetwork.Context)}/group/{envelope.GroupId}/epoch/{envelope.Epoch}";
        var protectedSecret = await fixture.Protector.ProtectAsync(secret, purpose, Token);
        await using (var database = fixture.Database.Open())
        {
            database.Groups.Add(new GroupRecord
            {
                GroupId = envelope.GroupId,
                RelayId = fixture.Relay.RelayId,
                Status = GroupStatus.Closed,
                LocallyClosed = true,
                Sequence = 10,
                Epoch = envelope.Epoch
            });
            database.GroupEpochs.Add(new GroupEpochRecord
            {
                GroupId = envelope.GroupId,
                Epoch = envelope.Epoch,
                Commitment = expected.GetProperty("client_secret_commitment").GetString(),
                ProtectedApplicationSecret = protectedSecret
            });
            database.GroupEvents.Add(new GroupEventRecord
            {
                GroupId = envelope.GroupId,
                Sequence = 10,
                PayloadJson = envelope.ToJson(),
                Epoch = envelope.Epoch,
                MessageId = envelope.MessageId,
                Sender = account,
                SenderDeviceId = device,
                CreatedAt = DateTimeOffset.FromUnixTimeSeconds(envelope.CreatedAt)
            });
            await database.SaveChangesAsync(Token);
        }

        await fixture.ReopenAsync();
        var manager = fixture.Client.GroupManager;
        var observed = AsyncTest.Signal();
        Exception? rejection = null;
        manager.TimelineChanged += (_, _) => observed.TrySetResult();
        manager.BackgroundError += (_, error) =>
        {
            rejection = error.Error;
            observed.TrySetResult();
        };
        await manager.StartAsync(Token);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.StopAsync(Token);
        await using var reader = await manager.GetMessagesAsync(envelope.GroupId, cancellationToken: Token);
        var messages = await reader.ReadNextAsync(10, Token);
        await using var verify = fixture.Database.Open();
        var stored = await verify.GroupEvents.SingleAsync(Token);
        if (variation == "valid")
        {
            Assert.Null(rejection);
            Assert.Null(stored.Rejection);

            var actual = Assert.Single(messages);
            using var decoded = JsonDocument.Parse(stored.DecryptedPayloadJson!);

            Assert.True(JsonElement.DeepEquals(input.GetProperty("message_content"), decoded.RootElement));
            Assert.Equal(input.GetProperty("message_content").GetProperty("body").GetProperty("text").GetString(), actual.Body!.Text);
        }
        else
        {
            Assert.IsAssignableFrom<CryptographicException>(rejection);
            Assert.NotNull(stored.Rejection);
            Assert.Null(stored.DecryptedPayloadJson);
            Assert.Empty(messages);
        }
    }
}
