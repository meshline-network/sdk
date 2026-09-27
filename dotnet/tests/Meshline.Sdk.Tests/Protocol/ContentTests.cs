using Meshline.Models.Protocol;
using Meshline.Tests.Support;
using Meshline.Validation;
using System.Text.Json;

namespace Meshline.Tests.Protocol;

public sealed class ContentTests
{
    public static IEnumerable<object[]> Bodies() => ProtocolVectors.Read("message-content").GetProperty("body").GetProperty("body_cases").EnumerateArray().Select(value => new object[] { value.GetProperty("body").GetRawText() });
    public static IEnumerable<object[]> InvalidBodies() => ProtocolVectors.Read("message-content").GetProperty("body").GetProperty("invalid_body_cases").EnumerateArray().Select(value => new object[] { value.GetProperty("body").GetRawText() });
    [Theory, MemberData(nameof(Bodies))]
    public void Valid_body_vectors_pass(string json) => Assert.Null(ProtocolModel.FromJson<MessageBody>(json)!.Validate());
    [Theory, MemberData(nameof(InvalidBodies))]
    public void Invalid_body_vectors_fail_parsing_or_validation(string json)
    {
        MessageBody? body;
        try
        {
            body = ProtocolModel.FromJson<MessageBody>(json);
        }
        catch (JsonException)
        {
            return;
        }

        Assert.True(body is null || body.Validate() is not null);
    }

    [Theory]
    [InlineData("https://relay.test", true)]
    [InlineData("wss://relay.test", true)]
    [InlineData("https://relay.test/ bad", false)]
    [InlineData("https://relay.test/#fragment", false)]
    [InlineData("https://user:password@relay.test", false)]
    [InlineData("https://relay.test/?x=1", false)]
    public void Relay_endpoint_constraints_are_enforced(string endpoint, bool valid) => Assert.Equal(valid, RelayEndpointValidator.Validate(endpoint, out _, out _) is null);
    [Fact]
    public void Attachment_reference_resolves_only_matching_hash_and_verifies_plaintext()
    {
        var vector = ProtocolVectors.Read("message-content").GetProperty("attachment_encryption");
        var reference = ProtocolModel.FromJson<ContentReference>(vector.GetProperty("reference").GetRawText())!;
        var plaintext = Convert.FromHexString(vector.GetProperty("plaintext_utf8_hex").GetString()!);

        Assert.Null(reference.Validate());
        Assert.Null(reference.Verify(plaintext));
        Assert.Same(reference, ContentReference.Resolve(reference.HashUri, [reference]));
        Assert.Null(ContentReference.Resolve("https://unrelated.test/", [reference]));

        plaintext[0] ^= 1;

        Assert.NotNull(reference.Verify(plaintext));
        Assert.NotNull(reference.Verify([]));
    }

    [Theory]
    [InlineData("AES-256-GCM", 32, 12, null)]
    [InlineData("AES-128-GCM", 32, 12, ProtocolViolationKind.Unsupported)]
    [InlineData("AES-128-GCM", 0, 0, ProtocolViolationKind.Unsupported)]
    [InlineData("AES-256-GCM", -1, 12, ProtocolViolationKind.Format)]
    [InlineData("AES-256-GCM", 0, 12, ProtocolViolationKind.Format)]
    [InlineData("AES-256-GCM", 31, 12, ProtocolViolationKind.Format)]
    [InlineData("AES-256-GCM", 33, 12, ProtocolViolationKind.Format)]
    [InlineData("AES-256-GCM", 32, -1, ProtocolViolationKind.Format)]
    [InlineData("AES-256-GCM", 32, 0, ProtocolViolationKind.Format)]
    [InlineData("AES-256-GCM", 32, 11, ProtocolViolationKind.Format)]
    [InlineData("AES-256-GCM", 32, 13, ProtocolViolationKind.Format)]
    public void Attachment_encryption_validates_directly_and_through_reference(string algorithm, int keyLength, int nonceLength, ProtocolViolationKind? violationKind)
    {
        var encryption = new ContentEncryption
        {
            Alg = algorithm,
            Key = keyLength < 0 ? default : [.. new byte[keyLength]],
            Nonce = nonceLength < 0 ? default : [.. new byte[nonceLength]]
        };
        var expected = violationKind is { } kind ? new ProtocolViolation(
            kind,
            kind == ProtocolViolationKind.Unsupported ? "Content encryption must use AES-256-GCM." : "Content encryption requires a 32-byte key and a 12-byte nonce.") : null;

        Assert.Equal(expected, encryption.Validate());

        var reference = ProtocolModel.FromJson<ContentReference>(ProtocolVectors.Read("message-content").GetProperty("attachment_encryption").GetProperty("reference").GetRawText())!;

        Assert.Equal(expected, (reference with { Encryption = encryption }).Validate());
    }

    [Fact]
    public void Message_requires_body_or_attachments_and_rejects_duplicate_attachment_hashes()
    {
        Assert.NotNull(new DirectMessage().Validate());

        var reference = ProtocolModel.FromJson<ContentReference>(ProtocolVectors.Read("message-content").GetProperty("attachment_encryption").GetProperty("reference").GetRawText())!;

        Assert.Null(new DirectMessage { Attachments = [reference] }.Validate());
        Assert.NotNull(new DirectMessage { Attachments = [reference, reference] }.Validate());
    }
}
