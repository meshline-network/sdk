using Meshline.Models.Protocol;

namespace Meshline.Tests.Support;

internal static class ProtocolResults
{
    internal static async Task<ProfileResolveResult> CreateProfileAsync(AccountSigner account, CancellationToken cancellationToken)
    {
        var signer = new DeviceSigner(account, Clock.Provider);
        var profile = new AccountProfile
        {
            Account = account.AccountId,
            Nickname = "Alice",
            PublicDiscovery = true,
            UpdatedAt = 0,
            DeviceSignature = []
        };
        profile = profile with
        {
            DeviceSignature = [.. await signer.SignAsync(profile.GetSigningInput(TestNetwork.Context), cancellationToken)]
        };
        return new()
        {
            Profile = profile,
            SignerCertificate = signer.Certificate
        };
    }

    internal static async Task<ChannelResolveResult> CreateChannelAsync(AccountSigner account, OfflineRelay relay, CancellationToken cancellationToken)
    {
        var signer = new DeviceSigner(account, relay.Clock);
        byte[] nonce = new byte[16];
        var descriptor = new ChannelDescriptor
        {
            ChannelId = Identifiers.DeriveChannelId(account.AccountId, relay.RelayId, nonce, TestNetwork.Context),
            Nonce = [.. nonce],
            Creator = account.AccountId,
            RelayId = relay.RelayId,
            Name = "news",
            Revision = 0,
            Status = ChannelStatus.Active,
            CreatedAt = 0,
            UpdatedAt = 0,
            DeviceSignature = []
        };
        descriptor = descriptor with
        {
            DeviceSignature = [.. await signer.SignAsync(descriptor.GetSigningInput(TestNetwork.Context), cancellationToken)]
        };
        return new()
        {
            Descriptor = descriptor,
            SignerCertificate = signer.Certificate
        };
    }

    internal static async Task<GroupInviteResolveResult> CreateGroupInviteAsync(AccountSigner account, CancellationToken cancellationToken)
    {
        var signer = new DeviceSigner(account, Clock.Provider);
        var now = Clock.UtcNow.ToUnixTimeSeconds();
        var invite = new GroupInvite
        {
            InviteId = Identifiers.CreateInviteId(),
            GroupId = "grp_" + new string('A', 22),
            Inviter = account.AccountId,
            CreatedAt = now - 60,
            ExpiresAt = now + 3600,
            DeviceSignature = []
        };
        invite = invite with
        {
            DeviceSignature = [.. await signer.SignAsync(invite.GetSigningInput(TestNetwork.Context), cancellationToken)]
        };
        return new()
        {
            Invite = invite,
            SignerCertificate = signer.Certificate,
            Uses = 0
        };
    }
}
