using Meshline.Identity;
using Meshline.Models;
using Meshline.Models.Protocol;
using Org.BouncyCastle.Math.EC.Rfc8032;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Net;
using System.Reflection;
using System.Collections.Immutable;
using Meshline.Components;
using Org.BouncyCastle.Math.EC.Rfc7748;

// A test-only JSON-lines bridge to the actual .NET SDK and its crypto providers.
// Inputs contain public test fixtures only. No wallet or network integration is used.
Console.InputEncoding = new UTF8Encoding(false, true);
Console.OutputEncoding = new UTF8Encoding(false, true);
if (args.Contains("--workflow")) { await WorkflowSession.RunAsync(); return; }
while (Console.ReadLine() is { } line)
{
    try
    {
        using var input = JsonDocument.Parse(line);
        var request = input.RootElement;
        byte[] Bytes(string name) => Base64Url.DecodeFromChars(request.GetProperty(name).GetString()!);
        object result = request.GetProperty("operation").GetString() switch
        {
            "canonical" => new { json = ProtocolModel.FromJson<Document>(request.GetProperty("json").GetString()!)!.ToJson() },
            "identity" => new { accountId = AccountAdapter.GetAccountId(request.GetProperty("chain").GetString()!, Bytes("publicKey")), relayId = RelayIdentity.GetRelayId(Bytes("publicKey")) },
            "certificate" => Certificate(request),
            "relay-descriptor" => Descriptor(request),
            "profile" => Profile(request),
            "message-open" => OpenMessage(request),
            "message-create" => CreateMessage(request),
            "channel-payload" => ChannelPayload(request),
            "channel-apply" => ApplyChannelEvent(request),
            "group-open" => OpenGroupMessage(request),
            "group-create" => CreateGroupMessage(request),
            "group-secrets" => GroupSecrets(request),
            "tls-material" => TlsMaterial(),
            "device-sign" => new { signature = Base64Url.EncodeToString(SignDevice(Bytes("input"), Bytes("privateKey"))) },
            "device-verify" => new { valid = Ed25519.Verify(Bytes("signature"), Bytes("publicKey"), Bytes("input")) },
            "account-verify" => new { valid = AccountAdapter.Neo.VerifySignature(Bytes("publicKey"), Bytes("input"), Bytes("signature")) },
            "account-sign" => SignAccount(Bytes("input"), Bytes("privateKey")),
            "encrypt" => new { ciphertext = Base64Url.EncodeToString(Encrypt(Bytes("key"), Bytes("nonce"), Bytes("plaintext"), Bytes("aad"))) },
            "decrypt" => new { plaintext = Base64Url.EncodeToString(Decrypt(Bytes("key"), Bytes("nonce"), Bytes("ciphertext"), Bytes("aad"))) },
            _ => throw new ArgumentException("Unknown bridge operation.")
        };
        Console.WriteLine(JsonSerializer.Serialize(new { result }));
    }
    catch (Exception error)
    {
        Console.WriteLine(JsonSerializer.Serialize(new { error = error.GetType().Name, message = error.Message }));
    }
}

static GroupManager CryptoGroupManager(NetworkContext context, string account) =>
    // These actual SDK helpers use only Context; no storage, runtime or other manager is invoked by this fixture.
    new(new Meshline.Models.Client.ClientOptions { Context = context, AccountId = account }, null!, null!, null!, null!, null!);

static object OpenGroupMessage(JsonElement request)
{
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var envelope = ProtocolModel.FromJson<GroupMessageEnvelope>(request.GetProperty("envelope").GetString()!)!;
    var sender = ProtocolModel.FromJson<DeviceCertificate>(request.GetProperty("sender").GetString()!)!;
    var deviceId = request.GetProperty("deviceId").GetString()!;
    if (envelope.Validate(context) is not null || sender.Validate(context) is not null || sender.GetDeviceId(context) != deviceId
        || !Ed25519.Verify(envelope.DeviceSignature.AsSpan(), sender.SigningPublicKey.AsSpan(), envelope.GetSigningInput(context)))
        throw new InvalidDataException("Invalid group message proof.");
    var manager = CryptoGroupManager(context, sender.Account);
    var secret = Base64Url.DecodeFromChars(request.GetProperty("applicationSecret").GetString()!);
    try
    {
        var open = typeof(GroupManager).GetMethod("DecryptMessage", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return new { json = ((TypedProtocolModel)open.Invoke(manager, [envelope, sender.Account, deviceId, secret])!).ToJson() };
    }
    finally { CryptographicOperations.ZeroMemory(secret); }
}

static object CreateGroupMessage(JsonElement request)
{
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var sender = ProtocolModel.FromJson<DeviceCertificate>(request.GetProperty("sender").GetString()!)!;
    var payload = ProtocolModel.FromJson<TypedProtocolModel>(request.GetProperty("payload").GetString()!)!;
    if (sender.Validate(context) is not null || payload.Validate(context) is not null) throw new InvalidDataException("Invalid group content or sender.");
    var manager = CryptoGroupManager(context, sender.Account);
    var envelope = new GroupMessageEnvelope { GroupId = request.GetProperty("groupId").GetString()!, Epoch = request.GetProperty("epoch").GetInt64(),
        MessageId = request.GetProperty("messageId").GetString()!, CreatedAt = request.GetProperty("createdAt").GetInt64(),
        Payload = new() { Alg = "AES-256-GCM", Nonce = [.. RandomNumberGenerator.GetBytes(12)], Ciphertext = [] }, DeviceSignature = [] };
    // Reuse the SDK's real AAD projection and HKDF helper, then the independent .NET AES/Ed25519 providers.
    var aad = (byte[])typeof(GroupManager).GetMethod("MessageAad", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(manager, [envelope, sender.Account, sender.GetDeviceId(context)])!;
    var derive = typeof(GroupManager).GetMethod("Derive", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<GroupDeriveDelegate>();
    var secret = Base64Url.DecodeFromChars(request.GetProperty("applicationSecret").GetString()!);
    var key = derive(secret, "Meshline/group-message-salt/v1", aad);
    var signingKey = Base64Url.DecodeFromChars(request.GetProperty("privateKey").GetString()!);
    var plaintext = Encoding.UTF8.GetBytes(payload.ToJson());
    try
    {
        envelope = envelope with { Payload = envelope.Payload with { Ciphertext = [.. Encrypt(key, envelope.Payload.Nonce.ToArray(), plaintext, aad)] } };
        envelope = envelope with { DeviceSignature = [.. SignDevice(envelope.GetSigningInput(context), signingKey)] };
        return new { json = envelope.ToJson() };
    }
    finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(signingKey); CryptographicOperations.ZeroMemory(plaintext); }
}

static object GroupSecrets(JsonElement request)
{
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var account = request.GetProperty("account").GetString()!;
    var manager = CryptoGroupManager(context, account);
    var groupId = request.GetProperty("groupId").GetString()!;
    var secret = Base64Url.DecodeFromChars(request.GetProperty("secret").GetString()!);
    var privateKey = Base64Url.DecodeFromChars(request.GetProperty("privateKey").GetString()!);
    var publicKey = new byte[32]; var shared = new byte[32];
    byte[]? opened = null;
    try
    {
        X25519.GeneratePublicKey(privateKey, publicKey);
        var commitment = typeof(GroupManager).GetMethod("Commitment", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<GroupCommitmentDelegate>(manager)(groupId, secret);
        var aadModel = (TypedProtocolModel)Activator.CreateInstance(typeof(GroupManager).GetNestedType("ClientBoxInput", BindingFlags.NonPublic)!)!;
        var aadType = aadModel.GetType();
        aadType.GetProperty("GroupId")!.SetValue(aadModel, groupId); aadType.GetProperty("Account")!.SetValue(aadModel, account);
        aadType.GetProperty("MemberEncryptionPublicKey")!.SetValue(aadModel, publicKey.ToImmutableArray()); aadType.GetProperty("ClientSecretCommitment")!.SetValue(aadModel, commitment);
        var aad = (byte[])typeof(TypedProtocolModel).GetMethod("GetSigningInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(aadModel, [context, Array.Empty<string>()])!;
        var box = ProtocolModel.FromJson<GroupSecretBox>(request.GetProperty("box").GetString()!)!;
        if (!X25519.CalculateAgreement(privateKey, box.Enc.AsSpan(), shared)) throw new CryptographicException("Invalid group key agreement.");
        opened = typeof(GroupManager).GetMethod("OpenSecret", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<GroupOpenSecretDelegate>()(box, shared, aad);
        var sealedBox = typeof(GroupManager).GetMethod("SealClientSecret", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate<GroupSealSecretDelegate>(manager)(groupId, account, publicKey, commitment, secret);
        return new { commitment, opened = Base64Url.EncodeToString(opened), box = sealedBox.ToJson() };
    }
    finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(privateKey); CryptographicOperations.ZeroMemory(shared); if (opened is not null) CryptographicOperations.ZeroMemory(opened); }
}

static object ChannelPayload(JsonElement request)
{
    var value = ProtocolModel.FromJson<TypedProtocolModel>(request.GetProperty("json").GetString()!)!;
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var input = value switch { ChannelDescriptor descriptor => descriptor.GetSigningInput(context), ChannelPost post => post.GetSigningInput(context), ChannelPostEdit edit => edit.GetSigningInput(context), ChannelPostDelete deleted => deleted.GetSigningInput(context), _ => throw new ArgumentException("Unexpected channel payload") };
    var signature = value switch { ChannelDescriptor descriptor => descriptor.DeviceSignature, ChannelPost post => post.DeviceSignature, ChannelPostEdit edit => edit.DeviceSignature, ChannelPostDelete deleted => deleted.DeviceSignature, _ => throw new ArgumentException() };
    var valid = value.Validate(context) is null && Ed25519.Verify(signature.AsSpan(), Base64Url.DecodeFromChars(request.GetProperty("publicKey").GetString()!), input);
    if (request.TryGetProperty("privateKey", out var privateKey))
    {
        ImmutableArray<byte> signed = [.. SignDevice(input, Base64Url.DecodeFromChars(privateKey.GetString()!))];
        value = value switch { ChannelDescriptor descriptor => descriptor with { DeviceSignature = signed }, ChannelPost post => post with { DeviceSignature = signed }, ChannelPostEdit edit => edit with { DeviceSignature = signed }, ChannelPostDelete deleted => deleted with { DeviceSignature = signed }, _ => throw new ArgumentException() };
    }
    return new { valid, json = value.ToJson(), signingInput = Base64Url.EncodeToString(input) };
}

static object ApplyChannelEvent(JsonElement request)
{
    var post = ProtocolModel.FromJson<ChannelPost>(request.GetProperty("post").GetString()!)!;
    var entry = ProtocolModel.FromJson<ChannelEvent>(request.GetProperty("event").GetString()!)!;
    var certificate = ProtocolModel.FromJson<DeviceCertificate>(request.GetProperty("certificate").GetString()!)!;
    var recordType = typeof(ChannelManager).Assembly.GetType("Meshline.Storage.ChannelPostRecord", throwOnError: true)!;
    var record = Activator.CreateInstance(recordType)!;
    recordType.GetProperty("ChannelId")!.SetValue(record, post.ChannelId);
    recordType.GetProperty("Sequence")!.SetValue(record, 1L);
    recordType.GetProperty("AppliedThrough")!.SetValue(record, 1L);
    recordType.GetProperty("MessageId")!.SetValue(record, post.MessageId);
    recordType.GetProperty("Author")!.SetValue(record, certificate.Account);
    recordType.GetProperty("PostJson")!.SetValue(record, post.ToJson());
    var apply = typeof(ChannelManager).GetMethod("ApplyPostEvent", BindingFlags.Static | BindingFlags.NonPublic)!;
    apply.Invoke(null, [record, entry, certificate]);
    return new { json = recordType.GetProperty("PostJson")!.GetValue(record), deleted = recordType.GetProperty("IsDeleted")!.GetValue(record) };
}

static object Certificate(JsonElement request)
{
    var certificate = ProtocolModel.FromJson<DeviceCertificate>(request.GetProperty("json").GetString()!)!;
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var violation = certificate.Validate(context);
    return new { valid = violation is null, deviceId = certificate.GetDeviceId(context), json = certificate.ToJson() };
}

static object Descriptor(JsonElement request)
{
    var descriptor = ProtocolModel.FromJson<RelayDescriptor>(request.GetProperty("json").GetString()!)!;
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var violation = descriptor.Validate(context);
    return new { valid = violation is null, message = violation?.Message, json = descriptor.ToJson() };
}

static object Profile(JsonElement request)
{
    var result = ProtocolModel.FromJson<ProfileResolveResult>(request.GetProperty("json").GetString()!)!;
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var violation = result.Validate(context);
    var profile = result.Profile;
    var valid = violation is null && profile.Account == result.SignerCertificate.Account
        && Ed25519.Verify(profile.DeviceSignature.AsSpan(), result.SignerCertificate.SigningPublicKey.AsSpan(), profile.GetSigningInput(context));
    if (request.TryGetProperty("privateKey", out var privateKey))
        result = result with { Profile = profile with { DeviceSignature = [.. SignDevice(profile.GetSigningInput(context), Base64Url.DecodeFromChars(privateKey.GetString()!))] } };
    return new { valid, json = result.ToJson(), signingInput = Base64Url.EncodeToString(profile.GetSigningInput(context)) };
}

static object OpenMessage(JsonElement request)
{
    var envelope = ProtocolModel.FromJson<MessageEnvelope>(request.GetProperty("envelope").GetString()!)!;
    var box = ProtocolModel.FromJson<MessageKeyBox>(request.GetProperty("keyBox").GetString()!)!;
    var certificate = ProtocolModel.FromJson<DeviceCertificate>(request.GetProperty("sender").GetString()!)!;
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    if (envelope.Validate(context) is not null || box.Validate(context) is not null || certificate.Validate(context) is not null
        || certificate.Account != envelope.From || certificate.GetDeviceId(context) != envelope.FromDeviceId
        || !Ed25519.Verify(envelope.DeviceSignature.AsSpan(), certificate.SigningPublicKey.AsSpan(), envelope.GetSigningInput(context)))
        throw new InvalidDataException("Invalid message proof.");
    var secret = new byte[32];
    var key = Base64Url.DecodeFromChars(request.GetProperty("privateKey").GetString()!);
    try
    {
        if (!X25519.CalculateAgreement(key.AsSpan(), box.Enc.AsSpan(), secret.AsSpan())) throw new CryptographicException("Invalid X25519 agreement.");
        // Call the actual SDK implementation, including its own AAD projections and JSON parser.
        var open = typeof(MessageManager).GetMethod("OpenPayload", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<OpenPayloadDelegate>();
        return new { json = open(envelope, box, request.GetProperty("account").GetString()!, secret, context).ToJson() };
    }
    finally { CryptographicOperations.ZeroMemory(secret); CryptographicOperations.ZeroMemory(key); }
}

static object CreateMessage(JsonElement request)
{
    var context = NetworkContext.Parse(request.GetProperty("context").GetString()!);
    var sender = ProtocolModel.FromJson<DeviceCertificate>(request.GetProperty("sender").GetString()!)!;
    var recipient = ProtocolModel.FromJson<DeviceCertificate>(request.GetProperty("recipient").GetString()!)!;
    if (sender.Validate(context) is not null || recipient.Validate(context) is not null) throw new InvalidDataException("Invalid encryption certificate.");
    var plaintext = Encoding.UTF8.GetBytes(request.GetProperty("payload").GetString()!);
    var contentKey = RandomNumberGenerator.GetBytes(32);
    var signingKey = Base64Url.DecodeFromChars(request.GetProperty("privateKey").GetString()!);
    try
    {
        var envelope = new MessageEnvelope { MessageId = request.GetProperty("messageId").GetString()!, CreatedAt = request.GetProperty("createdAt").GetInt64(),
            From = sender.Account, FromDeviceId = sender.GetDeviceId(context), To = recipient.Account,
            Payload = new() { Alg = "AES-256-GCM", Nonce = [.. RandomNumberGenerator.GetBytes(12)], Ciphertext = [] }, DeviceSignature = [] };
        var aadType = typeof(MessageManager).GetNestedType("MessageAad", BindingFlags.NonPublic)!;
        var aad = (TypedProtocolModel)aadType.GetMethod("Create", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [envelope])!;
        var aadBytes = (byte[])typeof(TypedProtocolModel).GetMethod("GetSigningInput", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(aad, [context, Array.Empty<string>()])!;
        envelope = envelope with { Payload = envelope.Payload with { Ciphertext = [.. Encrypt(contentKey, envelope.Payload.Nonce.ToArray(), plaintext, aadBytes)] } };
        envelope = envelope with { DeviceSignature = [.. SignDevice(envelope.GetSigningInput(context), signingKey)] };
        // Delegate avoids reflection boxing of ReadOnlySpan and runs the real SDK key-box constructor.
        var seal = typeof(MessageManager).GetMethod("SealKeys", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<SealKeysDelegate>();
        var message = new MessageSendRequest { Envelope = envelope, RecipientBoxes = seal(envelope, recipient.Account, [recipient], contentKey, context),
            SenderBoxes = sender.Account == recipient.Account ? null : seal(envelope, sender.Account, [sender], contentKey, context) };
        return new { json = message.ToJson() };
    }
    finally { CryptographicOperations.ZeroMemory(contentKey); CryptographicOperations.ZeroMemory(signingKey); CryptographicOperations.ZeroMemory(plaintext); }
}

static object TlsMaterial()
{
    // Ephemeral test CA and localhost certificate; never exported outside local test processes.
    using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var rootRequest = new CertificateRequest("CN=Meshline SDK Test CA", rootKey, HashAlgorithmName.SHA256);
    rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
    rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
    using var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(7));
    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
    request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
    request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
    var names = new SubjectAlternativeNameBuilder();
    names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); names.AddIpAddress(IPAddress.IPv6Loopback);
    request.CertificateExtensions.Add(names.Build());
    using var certificate = request.Create(root, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
    return new { ca = root.ExportCertificatePem(), certificate = certificate.ExportCertificatePem(), privateKey = key.ExportPkcs8PrivateKeyPem() };
}

static byte[] SignDevice(byte[] input, byte[] privateKey)
{
    var signature = new byte[64];
    Ed25519.Sign(privateKey, input, signature);
    return signature;
}

static object SignAccount(byte[] input, byte[] privateKey)
{
    using var signer = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, D = privateKey });
    var key = signer.ExportParameters(false);
    byte[] compressed = [(byte)((key.Q.Y![^1] & 1) == 0 ? 2 : 3), .. key.Q.X!];
    return new { publicKey = Base64Url.EncodeToString(compressed), signature = Base64Url.EncodeToString(signer.SignData(input, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
}

static byte[] Encrypt(byte[] key, byte[] nonce, byte[] plaintext, byte[] aad)
{
    var result = new byte[plaintext.Length + 16];
    using var cipher = new AesGcm(key, 16);
    cipher.Encrypt(nonce, plaintext, result.AsSpan(0, plaintext.Length), result.AsSpan(plaintext.Length), aad);
    return result;
}

static byte[] Decrypt(byte[] key, byte[] nonce, byte[] ciphertext, byte[] aad)
{
    var result = new byte[ciphertext.Length - 16];
    using var cipher = new AesGcm(key, 16);
    cipher.Decrypt(nonce, ciphertext.AsSpan(0, result.Length), ciphertext.AsSpan(result.Length), result, aad);
    return result;
}

sealed record Document : ProtocolModel;
delegate TypedProtocolModel OpenPayloadDelegate(MessageEnvelope envelope, MessageKeyBox keyBox, string accountId, ReadOnlySpan<byte> shared, NetworkContext context);
delegate ImmutableArray<MessageKeyBox> SealKeysDelegate(MessageEnvelope envelope, string accountId, IReadOnlyList<DeviceCertificate> certificates, ReadOnlySpan<byte> contentKey, NetworkContext context);
delegate byte[] GroupDeriveDelegate(ReadOnlySpan<byte> material, string salt, ReadOnlySpan<byte> info);
delegate string GroupCommitmentDelegate(string groupId, ReadOnlySpan<byte> secret);
delegate byte[] GroupOpenSecretDelegate(GroupSecretBox box, ReadOnlySpan<byte> shared, byte[] aad);
delegate GroupSecretBox GroupSealSecretDelegate(string groupId, string account, ReadOnlySpan<byte> publicKey, string commitment, ReadOnlySpan<byte> secret);
