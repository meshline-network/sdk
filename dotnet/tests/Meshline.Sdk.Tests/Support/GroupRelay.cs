using Meshline.Models.Protocol;
using Org.BouncyCastle.Math.EC.Rfc7748;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Meshline.Tests.Support;

// A scripted single-group host. Its relay key boxes use the platform crypto primitives,
// independently of GroupManager's private sealing and derivation implementation.
internal sealed class GroupRelay(TestClient fixture)
{
    public List<GroupEvent> Events { get; } = [];
    public List<GroupKeyEntry> Keys { get; } = [];
    public List<GroupApplicationEntry> Applications { get; } = [];
    public List<GroupRecoveryEntry> Recoveries { get; } = [];
    public Dictionary<string, GroupInvite> Invites { get; } = [];
    public Func<GroupSyncPage, GroupSyncPage>? TransformPage { get; set; }
    public string? LoseResponseFor { get; set; }
    public bool RejectNextWrite { get; set; }

    GroupSecretBox clientBox = null!;
    GroupRotationPrepareRequest? preparation;
    long epoch;
    string groupId = "";
    long Now => fixture.Relay.Clock.GetUtcNow().ToUnixTimeSeconds();

    public void Install()
    {
        fixture.Relay.Handler = (request, _) => Task.FromResult(Respond(request));
        fixture.Relay.SocketHandler = (request, socket) =>
        {
            Assert.Equal("group.subscribe", request.Method);

            socket.Reply(request.Id!, "null");
            return Task.CompletedTask;
        };
    }

    public HttpResponseMessage Respond(ObservedRequest request)
    {
        var query = RequestQuery.Parse(request);
        if (RejectNextWrite && request.Method.StartsWith("group.", StringComparison.Ordinal) && request.Verb != HttpMethod.Get)
        {
            RejectNextWrite = false;
            return OfflineRelay.Error("forbidden");
        }

        switch (request.Method)
        {
            case "group.create":
                var create = ProtocolModel.FromJson<GroupCreateRequest>(request.Body!)!;
                groupId = create.Create.GroupId;
                clientBox = create.ClientSecretBox;
                Append(create.Create);
                AddKeys();
                return Accepted(request);
            case "group.sync":
                var page = new GroupSyncPage
                {
                    Events = [.. Events.Where(entry => entry.Sequence > long.Parse(query["after"]))],
                    Certificates = [fixture.Client.Device!],
                    HasMore = false
                };
                return OfflineRelay.Json(TransformPage?.Invoke(page) ?? page);
            case "group.key.sync":
                return OfflineRelay.Json(new GroupKeyPage
                {
                    Keys = [.. Keys.Where(entry => entry.Epoch > long.Parse(query["after"]))],
                    HasMore = false
                });
            case "group.message.send":
                var message = ProtocolModel.FromJson<GroupMessageEnvelope>(request.Body!)!;
                var previous = Events.SingleOrDefault(entry => entry.Payload is GroupMessageEnvelope envelope && envelope.MessageId == message.MessageId);
                var messageEvent = previous ?? Append(message);
                return OfflineRelay.Json(new SequenceResult { Sequence = messageEvent.Sequence });
            case "group.secret.rotation.prepare":
                preparation = ProtocolModel.FromJson<GroupRotationPrepareRequest>(request.Body!)!;
                return OfflineRelay.Json(new GroupRotationPrepareResult
                {
                    Prepared = preparation.ClientSecretBoxes.Count,
                    ExpiresAt = Now + 600
                });
            case "group.secret.rotation.commit":
                clientBox = preparation!.ClientSecretBoxes[fixture.Account.AccountId];
                epoch++;
                Append(ProtocolModel.FromJson<GroupSecretRotation>(request.Body!)!);
                AddKeys();
                return Accepted(request);
            case "group.invite.create":
                var invite = ProtocolModel.FromJson<GroupInvite>(request.Body!)!;
                Invites[invite.InviteId] = invite;
                return Accepted(request);
            case "group.invite.resolve":
                return Invites.TryGetValue(query["invite_id"], out var found) ? OfflineRelay.Json(new GroupInviteResolveResult
                {
                    Invite = found,
                    SignerCertificate = fixture.Client.Device!,
                    Uses = 0
                }) : OfflineRelay.Error("not_found");
            case "group.application.list":
                return OfflineRelay.Json(new GroupApplicationPage { Applications = [.. Applications] });
            case "group.application.approve":
                var admission = ProtocolModel.FromJson<GroupApplicationApproveRequest>(request.Body!)!;
                epoch++;
                Append(admission.Approval);
                AddKeys();
                Applications.Clear();
                return Accepted(request);
            case "group.member.recovery.submit":
                var recovery = ProtocolModel.FromJson<GroupMemberRecoveryRequest>(request.Body!)!;
                Recoveries.Add(new()
                {
                    Request = recovery,
                    SignerCertificate = fixture.Client.Device!,
                    AcceptedAt = Now,
                    ExpiresAt = Now + 600
                });
                return OfflineRelay.Json(new GroupRecoverySubmitResult
                {
                    AcceptedAt = Now,
                    ExpiresAt = Now + 600
                });
            case "group.member.recovery.list":
                return OfflineRelay.Json(new GroupRecoveryPage { Requests = [.. Recoveries] });
            case "group.member.recovery.approve":
                var approval = ProtocolModel.FromJson<GroupRecoveryApproveRequest>(request.Body!)!;
                if (approval.ClientSecretBoxes.TryGetValue(fixture.Account.AccountId, out var box))
                    clientBox = box;
                epoch++;
                Append(approval.Approval);
                AddKeys();
                Recoveries.Clear();
                return Accepted(request);
            case "group.member.remove":
                epoch++;
                Append(ProtocolModel.FromJson<GroupMemberRemoval>(request.Body!)!);
                AddKeys();
                return Accepted(request);
            case "group.update":
            case "group.role.update":
            case "group.member.ban":
            case "group.member.unban":
            case "group.close":
                Append(ProtocolModel.FromJson<TypedProtocolModel>(request.Body!)!);
                return Accepted(request);
            default:
                return fixture.Relay.Respond(request);
        }
    }

    public async Task AddApplicationAsync(DeviceSigner peer, GroupInvite invite, CancellationToken token)
    {
        var application = new GroupApplication
        {
            GroupId = groupId,
            Account = peer.Certificate.Account,
            InviteId = invite.InviteId,
            MemberEncryptionPublicKey = peer.Certificate.EncryptionPublicKey,
            DeviceSignature = []
        };
        application = application with
        {
            DeviceSignature = [.. await peer.SignAsync(application.GetSigningInput(TestNetwork.Context), token)]
        };
        Applications.Add(new()
        {
            Application = application,
            SignerCertificate = peer.Certificate,
            AcceptedAt = Now
        });
    }

    GroupEvent Append(TypedProtocolModel payload)
    {
        var entry = new GroupEvent
        {
            Sequence = Events.Count,
            Epoch = epoch,
            Payload = payload,
            AcceptedAt = Now,
            SignerDeviceId = fixture.Client.Device!.GetDeviceId(TestNetwork.Context)
        };
        Events.Add(entry);
        return entry;
    }

    void AddKeys()
    {
        var certificate = fixture.Client.Device!;
        var aad = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new SortedDictionary<string, object>(StringComparer.Ordinal)
        {
            ["$context"] = TestNetwork.Context.ToString(),
            ["account"] = fixture.Account.AccountId,
            ["device_id"] = certificate.GetDeviceId(TestNetwork.Context),
            ["epoch"] = epoch,
            ["group_id"] = groupId,
            ["$type"] = "meshline.group.relay_secret_box.aad"
        }));
        var privateKey = RandomNumberGenerator.GetBytes(32);
        var enc = new byte[32];
        var shared = new byte[32];
        X25519.GeneratePublicKey(privateKey.AsSpan(), enc.AsSpan());

        Assert.True(X25519.CalculateAgreement(privateKey.AsSpan(), certificate.EncryptionPublicKey.AsSpan(), shared.AsSpan()));

        var secret = RandomNumberGenerator.GetBytes(32);
        var sealedSecret = new byte[60];
        RandomNumberGenerator.Fill(sealedSecret.AsSpan(0, 12));
        var key = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, SHA256.HashData("Meshline/keybox-salt/v1"u8), aad);
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(sealedSecret.AsSpan(0, 12), secret, sealedSecret.AsSpan(12, 32), sealedSecret.AsSpan(44, 16), aad);
        Keys.Add(new()
        {
            Epoch = epoch,
            ClientSecretBox = clientBox,
            RelaySecretBox = new()
            {
                Alg = "X25519-HKDF-SHA256-AES256GCM",
                Enc = [.. enc],
                SealedSecret = [.. sealedSecret]
            }
        });
        CryptographicOperations.ZeroMemory(privateKey);
        CryptographicOperations.ZeroMemory(shared);
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(secret);
    }

    HttpResponseMessage Accepted(ObservedRequest request)
    {
        if (LoseResponseFor == request.Method)
        {
            LoseResponseFor = null;
            throw new HttpRequestException("Accepted before disconnect");
        }

        return new(HttpStatusCode.NoContent);
    }
}
