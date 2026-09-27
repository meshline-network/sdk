using Meshline.Models;
using Meshline.Models.Protocol;
using Meshline.Storage;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Meshline.Components;

sealed partial class MessageManager
{
    static readonly byte[] KeyBoxSalt = SHA256.HashData("Meshline/keybox-salt/v1"u8);
    static readonly UTF8Encoding Utf8 = new(false, true);

    async Task<MessageSendRequest> EncryptAsync(string messageId, long createdAt, string recipient, TypedProtocolModel payload, IReadOnlyList<DeviceCertificate> recipientDevices, IReadOnlyList<DeviceCertificate> senderDevices, TypedProtocolModel? authorization, CancellationToken cancellationToken)
    {
        var certificate = deviceManager.Local ?? throw new InvalidOperationException("No local device has been created.");
        var plaintext = Utf8.GetBytes(payload.ToJson());
        var contentKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var envelope = new MessageEnvelope
            {
                MessageId = messageId,
                CreatedAt = createdAt,
                From = Options.AccountId,
                FromDeviceId = certificate.GetDeviceId(Context),
                To = recipient,
                Payload = new() { Alg = "AES-256-GCM", Nonce = [.. RandomNumberGenerator.GetBytes(12)], Ciphertext = [] },
                DeviceSignature = []
            };
            var ciphertext = new byte[plaintext.Length + 16];
            using (var cipher = new AesGcm(contentKey, 16))
                cipher.Encrypt(envelope.Payload.Nonce.AsSpan(), plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), MessageAad.Create(envelope).GetSigningInput(Options.Context));
            envelope = envelope with { Payload = envelope.Payload with { Ciphertext = [.. ciphertext] } };
            envelope = envelope with { DeviceSignature = [.. await deviceManager.SignAsync(envelope.GetSigningInput(Options.Context), cancellationToken).ConfigureAwait(false)] };
            if (envelope.Validate(Options.Context) is { } violation) throw new ArgumentException(violation.Message, nameof(payload));
            var recipients = SealKeys(envelope, recipient, recipientDevices, contentKey, Options.Context);
            var senders = recipient == Options.AccountId ? (ImmutableArray<MessageKeyBox>?)null : SealKeys(envelope, Options.AccountId, senderDevices, contentKey, Options.Context);
            return new() { Envelope = envelope, RecipientBoxes = recipients, SenderBoxes = senders, Authorization = recipient == Options.AccountId ? null : authorization };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(contentKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    static ImmutableArray<MessageKeyBox> SealKeys(MessageEnvelope envelope, string accountId, IReadOnlyList<DeviceCertificate> certificates, ReadOnlySpan<byte> contentKey, NetworkContext context)
    {
        if (certificates.Count is < 1 or > 8) throw new InvalidOperationException("Message key boxes require between one and eight authorized devices.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var boxes = ImmutableArray.CreateBuilder<MessageKeyBox>(certificates.Count);
        foreach (var certificate in certificates)
        {
            var id = certificate.GetDeviceId(context);
            if (certificate.Account != accountId || !ids.Add(id)) throw new InvalidDataException("The message device collection contains another account or a duplicate device.");
            var ephemeral = RandomNumberGenerator.GetBytes(32);
            var shared = new byte[32];
            var wrappingKey = new byte[32];
            try
            {
                var enc = new byte[32];
                X25519.GeneratePublicKey(ephemeral.AsSpan(), enc.AsSpan());
                if (!X25519.CalculateAgreement(ephemeral.AsSpan(), certificate.EncryptionPublicKey.AsSpan(), shared.AsSpan()))
                    throw new CryptographicException("The X25519 agreement produced an all-zero shared secret.");
                var box = new MessageKeyBox { DeviceId = id, Alg = "X25519-HKDF-SHA256-AES256GCM", Enc = [.. enc], SealedKey = [] };
                var aad = KeyBoxAad.Create(envelope, box, accountId).GetSigningInput(context);
                HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, wrappingKey, KeyBoxSalt, aad);
                var sealedKey = new byte[60];
                RandomNumberGenerator.Fill(sealedKey.AsSpan(0, 12));
                using (var cipher = new AesGcm(wrappingKey, 16))
                    cipher.Encrypt(sealedKey.AsSpan(0, 12), contentKey, sealedKey.AsSpan(12, 32), sealedKey.AsSpan(44), aad);
                boxes.Add(box with { SealedKey = [.. sealedKey] });
            }
            finally
            {
                CryptographicOperations.ZeroMemory(ephemeral);
                CryptographicOperations.ZeroMemory(shared);
                CryptographicOperations.ZeroMemory(wrappingKey);
            }
        }
        return boxes.MoveToImmutable();
    }

    async Task<TypedProtocolModel> ReadSentPayloadAsync(MessageOutboxRecord record, CancellationToken cancellationToken)
    {
        var request = ProtocolModel.FromJson<MessageSendRequest>(record.RequestJson)!;
        var deviceId = Certificate.GetDeviceId(Context);
        var box = request.SenderBoxes!.Value.Single(value => value.DeviceId == deviceId);
        var shared = await deviceManager.DeriveSharedSecretAsync(box.Enc.AsMemory(), cancellationToken).ConfigureAwait(false);
        try { return OpenPayload(request.Envelope, box, Options.AccountId, shared, Context); }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    async Task<TypedProtocolModel> DecryptAsync(MessageEnvelope envelope, MessageKeyBox keyBox, DeviceCertificate sender, CancellationToken cancellationToken)
    {
        if (envelope.Validate(Options.Context) is { } envelopeViolation) throw new InvalidDataException(envelopeViolation.Message);
        if (keyBox.Validate(Options.Context) is { } boxViolation) throw new InvalidDataException(boxViolation.Message);
        if (sender.Account != envelope.From || sender.GetDeviceId(Context) != envelope.FromDeviceId)
            throw new InvalidDataException("The message signing certificate belongs to another account or device.");
        if (envelope.From != Options.AccountId && envelope.To != Options.AccountId)
            throw new InvalidDataException("The message does not belong to this account.");
        var local = deviceManager.Local ?? throw new InvalidOperationException("No local device has been loaded.");
        if (keyBox.DeviceId != local.GetDeviceId(Context))
            throw new InvalidDataException("The message key box belongs to another device.");
        if (!Ed25519.Verify(envelope.DeviceSignature.AsSpan(), sender.SigningPublicKey.AsSpan(), envelope.GetSigningInput(Options.Context)))
            throw new InvalidDataException("The message envelope signature is invalid.");
        byte[] shared;
        try { shared = await deviceManager.DeriveSharedSecretAsync(keyBox.Enc.AsMemory(), cancellationToken).ConfigureAwait(false); }
        catch (ArgumentException exception) when (exception.ParamName == "peerPublicKey")
        {
            throw new InvalidDataException("The message key box contains an invalid X25519 public key.", exception);
        }
        try
        {
            try { return OpenPayload(envelope, keyBox, Options.AccountId, shared, Options.Context); }
            catch (CryptographicException exception) { throw new InvalidDataException("The encrypted message failed authentication.", exception); }
        }
        finally { CryptographicOperations.ZeroMemory(shared); }
    }

    static TypedProtocolModel OpenPayload(MessageEnvelope envelope, MessageKeyBox keyBox, string accountId, ReadOnlySpan<byte> shared, NetworkContext context)
    {
        var wrappingKey = new byte[32];
        var contentKey = new byte[32];
        var plaintext = new byte[envelope.Payload.Ciphertext.Length - 16];
        try
        {
            var aad = KeyBoxAad.Create(envelope, keyBox, accountId).GetSigningInput(context);
            HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, wrappingKey, KeyBoxSalt, aad);
            using (var cipher = new AesGcm(wrappingKey, 16))
                cipher.Decrypt(keyBox.SealedKey.AsSpan(0, 12), keyBox.SealedKey.AsSpan(12, 32), keyBox.SealedKey.AsSpan(44..), contentKey, aad);
            using (var cipher = new AesGcm(contentKey, 16))
                cipher.Decrypt(envelope.Payload.Nonce.AsSpan(), envelope.Payload.Ciphertext.AsSpan(0, plaintext.Length), envelope.Payload.Ciphertext.AsSpan(plaintext.Length..), plaintext, MessageAad.Create(envelope).GetSigningInput(context));
            return ProtocolModel.FromJson<TypedProtocolModel>(Utf8.GetString(plaintext)) ?? throw new InvalidDataException("A message payload must contain a typed protocol object.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrappingKey);
            CryptographicOperations.ZeroMemory(contentKey);
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    sealed record MessageAad() : TypedProtocolModel("meshline.message.aad")
    {
        public required string MessageId { get; init; }
        public required long CreatedAt { get; init; }
        public required string From { get; init; }
        public required string FromDeviceId { get; init; }
        public required string To { get; init; }

        public static MessageAad Create(MessageEnvelope envelope) => new() { MessageId = envelope.MessageId, CreatedAt = envelope.CreatedAt, From = envelope.From, FromDeviceId = envelope.FromDeviceId, To = envelope.To };
    }

    sealed record KeyBoxAad() : TypedProtocolModel("meshline.message.key_box.aad")
    {
        public required string Alg { get; init; }
        public required string PayloadAlg { get; init; }
        public required string MessageId { get; init; }
        public required long CreatedAt { get; init; }
        public required string From { get; init; }
        public required string FromDeviceId { get; init; }
        public required string Account { get; init; }
        public required string DeviceId { get; init; }
        public required ImmutableArray<byte> Enc { get; init; }

        public static KeyBoxAad Create(MessageEnvelope envelope, MessageKeyBox keyBox, string accountId) => new() { Alg = keyBox.Alg, PayloadAlg = envelope.Payload.Alg, MessageId = envelope.MessageId, CreatedAt = envelope.CreatedAt, From = envelope.From, FromDeviceId = envelope.FromDeviceId, Account = accountId, DeviceId = keyBox.DeviceId, Enc = keyBox.Enc };
    }
}
