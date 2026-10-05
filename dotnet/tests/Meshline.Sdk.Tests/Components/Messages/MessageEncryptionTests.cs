using Meshline.Models.Client;
using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Org.BouncyCastle.Math.EC.Rfc7748;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;

namespace Meshline.Tests.Components.Messages;

public sealed class MessageEncryptionTests
{
    static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("valid")]
    [InlineData("key")]
    [InlineData("key-box")]
    [InlineData("ciphertext")]
    [InlineData("nonce")]
    [InlineData("timestamp")]
    [InlineData("account")]
    [InlineData("network")]
    public async Task Timeline_receives_fixed_sender_copy_and_rejects_tampering(string variation)
    {
        using var time = Clock.Use(new ManualClock(DateTimeOffset.FromUnixTimeSeconds(1730000000)));
        await using var fixture = EncryptionVectorSetup.CreateClient();
        var vector = ProtocolVectors.Read("message-encryption").GetProperty("timeline");
        var request = ProtocolModel.FromJson<MessageSendRequest>(vector.GetProperty("request").GetRawText())!;
        var certificate = ProtocolModel.FromJson<DeviceCertificate>(vector.GetProperty("request").GetProperty("signer_certificate").GetRawText())!;

        Assert.Equal(fixture.Account.AccountId, certificate.Account);
        Assert.Null(certificate.Validate(TestNetwork.Context));

        await EncryptionVectorSetup.SeedDeviceAsync(
            fixture,
            certificate,
            ProtocolVectors.Decode(vector.GetProperty("sender_signing_private_key").GetString()!),
            ProtocolVectors.Decode(vector.GetProperty("sender_encryption_private_key").GetString()!),
            Token);
        await fixture.InitializeAsync();

        var envelope = request.Envelope;
        var box = request.SenderBoxes!.Value[0];

        Assert.Equal(Convert.FromHexString(vector.GetProperty("envelope_signing_input_utf8_hex").GetString()!), envelope.GetSigningInput(TestNetwork.Context));

        if (variation == "key")
        {
            var wrongPublicKey = new byte[32];
            X25519.GeneratePublicKey(new byte[32].AsSpan(), wrongPublicKey.AsSpan());
            box = box with
            {
                Enc = [.. wrongPublicKey]
            };
        }

        if (variation == "key-box")
        {
            var bytes = box.SealedKey.ToArray();
            bytes[^1] ^= 1;
            box = box with
            {
                SealedKey = [.. bytes]
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
        }

        if (variation == "nonce")
            envelope = envelope with
            {
                Payload = envelope.Payload with
                {
                    Nonce = [.. new byte[12]]
                }
            };
        if (variation == "timestamp")
            envelope = envelope with
            {
                CreatedAt = envelope.CreatedAt + 1
            };
        if (variation == "account")
            envelope = envelope with
            {
                To = envelope.From
            };
        if (variation != "valid")
        {
            // Re-sign mutations so ciphertext / AAD cases reach authenticated decryption.
            // The network case deliberately carries a signature for a foreign network.
            var context = variation == "network" ? TestNetwork.Context with
            {
                Reference = 1
            }

            : TestNetwork.Context;
            envelope = envelope with
            {
                DeviceSignature = [.. await fixture.Client.DeviceManager.SignAsync(envelope.GetSigningInput(context), Token)]
            };
        }

        var entry = new MessageTimelineEntry
        {
            Sequence = 0,
            Envelope = envelope,
            KeyBox = box,
            AcceptedAt = 1730000001
        };
        var observed = AsyncTest.Signal();
        Exception? rejection = null;
        IReadOnlyList<MessageInfo>? received = null;
        var manager = fixture.Client.MessageManager;
        manager.MessageReceived += (_, args) =>
        {
            received = args.Messages;
            observed.TrySetResult();
        };
        manager.BackgroundError += (_, error) =>
        {
            rejection = error.Error;
            observed.TrySetResult();
        };
        fixture.Relay.Handler = (http, _) => Task.FromResult(http.Method == "message.timeline.sync" ? OfflineRelay.Json(new MessageTimelinePage
        {
            Items = http.Query.Contains("after=-1", StringComparison.Ordinal) ? [entry] : [],
            Certificates = [certificate],
            HasMore = false
        }) : fixture.Relay.Respond(http));
        await manager.StartAsync(Token);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        await manager.StopAsync(Token);
        var actual = await manager.GetMessageAsync(new()
        {
            Sender = envelope.From,
            MessageId = envelope.MessageId
        }, Token);
        await using var database = fixture.Database.Open();

        Assert.Equal(0, (await database.AccountTimelines.SingleAsync(Token)).Sequence);

        if (variation == "valid")
        {
            Assert.Null(rejection);
            Assert.NotNull(actual);

            var stored = await database.Messages.SingleAsync(row => row.MessageId == envelope.MessageId, Token);

            Assert.True(actual.LocalSequence > 0);
            Assert.Equal(stored.LocalSequence, actual.LocalSequence);
            Assert.Equal(actual.LocalSequence, Assert.Single(received!).LocalSequence);
            await using (var reader = await manager.GetMessageHistoryAsync(cancellationToken: Token))
                Assert.Equal(actual.LocalSequence, Assert.Single(await reader.ReadNextAsync(10, Token)).LocalSequence);
            await fixture.ReopenAsync();
            Assert.Equal(actual.LocalSequence, (await fixture.Client.MessageManager.GetMessageAsync(actual.Key, Token))!.LocalSequence);
            Assert.NotNull(stored.PayloadJson);
            Assert.Equal(Convert.FromHexString(vector.GetProperty("plaintext_utf8_hex").GetString()!), Encoding.UTF8.GetBytes(stored.PayloadJson));

            using var plaintext = JsonDocument.Parse(stored.PayloadJson);

            Assert.Equal(plaintext.RootElement.GetProperty("body").GetProperty("text").GetString(), actual.Body!.Text);
        }
        else
        {
            Assert.Null(actual);

            var error = Assert.IsType<InvalidDataException>(rejection);
            if (variation == "network")
                Assert.Contains("signature", error.Message, StringComparison.Ordinal);
            else
                Assert.IsAssignableFrom<CryptographicException>(error.InnerException);

            Assert.Empty(await database.Messages.Where(row => row.MessageId == envelope.MessageId).ToListAsync(Token));
        }
    }
}
