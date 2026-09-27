using Meshline.Models.Protocol;
using Meshline.Storage;
using Org.BouncyCastle.Math.EC.Rfc7748;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Buffers.Text;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace Meshline.Components;

sealed partial class GroupManager
{
    static readonly UTF8Encoding Utf8 = new(false, true);

    async Task<T> SignAsync<T>(T value, CancellationToken cancellationToken) where T : TypedProtocolModel
    {
        var signature = (await deviceManager.SignAsync(value.GetSigningInput(Context, "device_signature"), cancellationToken).ConfigureAwait(false)).ToImmutableArray();
        TypedProtocolModel signed = value switch
        {
            GroupManagementOperation item => item with { DeviceSignature = signature },
            GroupCreate item => item with { DeviceSignature = signature },
            GroupInvite item => item with { DeviceSignature = signature },
            GroupApplication item => item with { DeviceSignature = signature },
            GroupMemberRecoveryRequest item => item with { DeviceSignature = signature },
            GroupMessageEnvelope item => item with { DeviceSignature = signature },
            _ => throw new ArgumentException("The object is not a signed group request.", nameof(value))
        };
        if (signed.Validate(Context) is { } violation) throw new ArgumentException(violation.Message, nameof(value));
        return (T)signed;
    }

    void VerifySignature(TypedProtocolModel value, DeviceCertificate certificate)
    {
        if (value.Validate(Context) is { } violation) throw new InvalidDataException(violation.Message);
        var signature = value switch
        {
            GroupManagementOperation item => item.DeviceSignature,
            GroupCreate item => item.DeviceSignature,
            GroupInvite item => item.DeviceSignature,
            GroupApplication item => item.DeviceSignature,
            GroupMemberRecoveryRequest item => item.DeviceSignature,
            GroupMessageEnvelope item => item.DeviceSignature,
            _ => throw new InvalidDataException("The object is not a supported signed group request.")
        };
        if (!Ed25519.Verify(signature.AsSpan(), certificate.SigningPublicKey.AsSpan(), value.GetSigningInput(Context, "device_signature")))
            throw new CryptographicException("The group object has an invalid device signature.");
    }

    string ManagementHash(TypedProtocolModel value) => "sha256:" + Base64Url.EncodeToString(SHA256.HashData(value.GetSigningInput(Context)));
    string Commitment(string groupId, ReadOnlySpan<byte> secret) => ManagementHash(new CommitmentInput { GroupId = groupId, Secret = [.. secret] });
    string SecretPurpose(string groupId, string kind, string key) => $"Meshline/{Context}/{Options.AccountId}/{Certificate.GetDeviceId(Context)}/group/{groupId}/{kind}/{key}";

    async Task<byte[]> SaveMemberKeyAsync(MeshlineDbContext database, string groupId, ReadOnlyMemory<byte> privateKey, CancellationToken cancellationToken)
    {
        var publicKey = new byte[32];
        X25519.GeneratePublicKey(privateKey.Span, publicKey);
        var encoded = Base64Url.EncodeToString(publicKey);
        if (await database.GroupMemberKeys.FindAsync([groupId, encoded], cancellationToken).ConfigureAwait(false) is null)
            database.GroupMemberKeys.Add(new()
            {
                GroupId = groupId,
                PublicKey = encoded,
                ProtectedPrivateKey = await secretProtector.ProtectAsync(privateKey, SecretPurpose(groupId, "member", encoded), cancellationToken).ConfigureAwait(false)
            });
        return publicKey;
    }

    async Task<byte[]?> ReadMemberKeyAsync(MeshlineDbContext database, string groupId, byte[] publicKey, CancellationToken cancellationToken)
    {
        var encoded = Base64Url.EncodeToString(publicKey);
        var record = await database.GroupMemberKeys.FindAsync([groupId, encoded], cancellationToken).ConfigureAwait(false);
        if (record is null) return null;
        return await secretProtector.UnprotectAsync(record.ProtectedPrivateKey, SecretPurpose(groupId, "member", encoded), cancellationToken).ConfigureAwait(false);
    }

    GroupSecretBox SealClientSecret(string groupId, string account, ReadOnlySpan<byte> publicKey, string commitment, ReadOnlySpan<byte> secret)
    {
        var ephemeral = RandomNumberGenerator.GetBytes(32);
        var enc = new byte[32];
        var shared = new byte[32];
        var aad = new ClientBoxInput { GroupId = groupId, Account = account, MemberEncryptionPublicKey = [.. publicKey], ClientSecretCommitment = commitment }.GetSigningInput(Context);
        try
        {
            X25519.GeneratePublicKey(ephemeral, enc);
            if (!X25519.CalculateAgreement(ephemeral, publicKey, shared)) throw new CryptographicException("The member public key produces an all-zero agreement.");
            var key = Derive(shared, "Meshline/keybox-salt/v1", aad);
            try
            {
                var sealedSecret = new byte[60];
                RandomNumberGenerator.Fill(sealedSecret.AsSpan(0, 12));
                using var aes = new AesGcm(key, 16);
                aes.Encrypt(sealedSecret.AsSpan(0, 12), secret, sealedSecret.AsSpan(12, 32), sealedSecret.AsSpan(44, 16), aad);
                return new() { Alg = "X25519-HKDF-SHA256-AES256GCM", Enc = [.. enc], SealedSecret = [.. sealedSecret] };
            }
            finally { CryptographicOperations.ZeroMemory(key); }
        }
        finally { CryptographicOperations.ZeroMemory(ephemeral); CryptographicOperations.ZeroMemory(shared); }
    }

    static byte[] OpenSecret(GroupSecretBox box, ReadOnlySpan<byte> shared, byte[] aad)
    {
        if (box.Validate() is { } violation) throw new InvalidDataException(violation.Message);
        var key = Derive(shared, "Meshline/keybox-salt/v1", aad);
        var plaintext = new byte[32];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(box.SealedSecret.AsSpan(0, 12), box.SealedSecret.AsSpan(12, 32), box.SealedSecret.AsSpan(44, 16), plaintext, aad);
            return plaintext;
        }
        catch { CryptographicOperations.ZeroMemory(plaintext); throw; }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    static byte[] Derive(ReadOnlySpan<byte> material, string salt, ReadOnlySpan<byte> info)
    {
        var result = new byte[32];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, material, result, SHA256.HashData(Encoding.UTF8.GetBytes(salt)), info);
        return result;
    }

    byte[] MessageAad(GroupMessageEnvelope envelope, string account, string deviceId) => new MessageInput
    {
        GroupId = envelope.GroupId,
        Epoch = envelope.Epoch,
        MessageId = envelope.MessageId,
        From = account,
        FromDeviceId = deviceId,
        CreatedAt = envelope.CreatedAt
    }.GetSigningInput(Context);

    async Task<GroupMessageEnvelope> EncryptMessageAsync(string groupId, long epoch, TypedProtocolModel payload, byte[] applicationSecret, CancellationToken cancellationToken)
    {
        if (payload.Validate(Context) is { } violation) throw new ArgumentException(violation.Message, nameof(payload));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var envelope = new GroupMessageEnvelope
        {
            GroupId = groupId,
            Epoch = epoch,
            MessageId = Identifiers.CreateMessageId(),
            CreatedAt = Clock.UtcNow.ToUnixTimeSeconds(),
            Payload = new() { Alg = "AES-256-GCM", Nonce = [.. nonce], Ciphertext = [] },
            DeviceSignature = []
        };
        var aad = MessageAad(envelope, Options.AccountId, Certificate.GetDeviceId(Context));
        var key = Derive(applicationSecret, "Meshline/group-message-salt/v1", aad);
        var plaintext = Encoding.UTF8.GetBytes(payload.ToJson());
        try
        {
            var ciphertext = new byte[plaintext.Length + 16];
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, plaintext, ciphertext.AsSpan(0, plaintext.Length), ciphertext.AsSpan(plaintext.Length), aad);
            return await SignAsync(envelope with { Payload = envelope.Payload with { Ciphertext = [.. ciphertext] } }, cancellationToken).ConfigureAwait(false);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    TypedProtocolModel DecryptMessage(GroupMessageEnvelope envelope, string account, string deviceId, byte[] applicationSecret)
    {
        var aad = MessageAad(envelope, account, deviceId);
        var key = Derive(applicationSecret, "Meshline/group-message-salt/v1", aad);
        var ciphertext = envelope.Payload.Ciphertext.AsSpan();
        var plaintext = new byte[ciphertext.Length - 16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(envelope.Payload.Nonce.AsSpan(), ciphertext[..^16], ciphertext[^16..], plaintext, aad);
            return ProtocolModel.FromJson<TypedProtocolModel>(Utf8.GetString(plaintext))
                ?? throw new InvalidDataException("A group message payload must contain a typed protocol object.");
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    sealed record CommitmentInput() : TypedProtocolModel("meshline.group.client_secret.commitment")
    {
        public required string GroupId { get; init; }
        public required ImmutableArray<byte> Secret { get; init; }
    }

    sealed record ClientBoxInput() : TypedProtocolModel("meshline.group.client_secret_box.aad")
    {
        public required string GroupId { get; init; }
        public required string Account { get; init; }
        public required ImmutableArray<byte> MemberEncryptionPublicKey { get; init; }
        public required string ClientSecretCommitment { get; init; }
    }

    sealed record RelayBoxInput() : TypedProtocolModel("meshline.group.relay_secret_box.aad")
    {
        public required string GroupId { get; init; }
        public required string Account { get; init; }
        public required string DeviceId { get; init; }
        public required long Epoch { get; init; }
    }

    sealed record EpochInput() : TypedProtocolModel("meshline.group.epoch_secret")
    {
        public required string GroupId { get; init; }
        public required long Epoch { get; init; }
        public required string ClientSecretCommitment { get; init; }
    }

    sealed record MessageInput() : TypedProtocolModel("meshline.group.message.aad")
    {
        public required string GroupId { get; init; }
        public required long Epoch { get; init; }
        public required string MessageId { get; init; }
        public required string From { get; init; }
        public required string FromDeviceId { get; init; }
        public required long CreatedAt { get; init; }
    }
}
