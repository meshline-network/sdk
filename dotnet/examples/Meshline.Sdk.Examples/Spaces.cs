using Meshline.Models.Client;
using Meshline.Models.Protocol;

namespace Meshline.Examples;

public static class Spaces
{
    #region channel
    public static async Task<ChannelInfo> CreateChannelAsync(
        MeshlineClient client, string relayId, CancellationToken cancellationToken = default)
    {
        var channel = await client.ChannelManager.CreateChannelAsync(
            relayId, "Announcements", cancellationToken: cancellationToken);
        await client.ChannelManager.FollowAsync(channel.Ref, cancellationToken);
        await client.ChannelManager.PublishPostAsync(channel.Ref, new ChannelPostDraft
        {
            Body = new MessageBody { ContentType = "text/plain", Text = "Welcome!" }
        }, cancellationToken);
        return channel;
    }
    #endregion

    #region channel-history
    public static async Task LoadChannelHistoryAsync(
        MeshlineClient client, ChannelRef channel, CancellationToken cancellationToken = default)
    {
        string? cursor = null;
        do
        {
            var page = await client.ChannelManager.LoadChannelHistoryAsync(
                channel, new PageRequest { Limit = 50, Cursor = cursor }, cancellationToken);
            foreach (var post in page.Items)
                Console.WriteLine(post.Body?.Text);
            cursor = page.NextCursor;
        } while (cursor is not null);
    }
    #endregion

    #region group
    public static async Task<GroupInfo> CreateGroupAsync(
        MeshlineClient client, string relayId, CancellationToken cancellationToken = default)
    {
        var group = await client.GroupManager.CreateGroupAsync(relayId, new GroupCreateOptions
        {
            Name = "Project team",
            MemberCapacity = 20,
            InvitePolicy = GroupInvitePolicy.Administrators
        }, cancellationToken);
        await client.GroupManager.SendMessageAsync(group.Ref, new GroupMessageDraft
        {
            Body = new MessageBody { ContentType = "text/plain", Text = "Welcome, team!" }
        }, cancellationToken);
        return group;
    }
    #endregion

    #region group-admission
    public static async Task InviteAndApplyAsync(
        MeshlineClient inviter, MeshlineClient applicant, GroupRef group,
        DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var invite = await inviter.GroupManager.CreateInviteAsync(
            group, expiresAt, maxUses: 1, cancellationToken: cancellationToken);
        // In an application, transfer the returned invitation to the applicant.
        await applicant.GroupManager.ApplyToGroupAsync(invite, cancellationToken);
    }

    public static Task ApproveApplicantAsync(
        MeshlineClient administrator, GroupRef group, string applicantAccountId,
        CancellationToken cancellationToken = default) =>
        administrator.GroupManager.ApproveApplicationsAsync(group, [applicantAccountId], cancellationToken);
    #endregion

    #region group-recovery
    public static async Task RequestGroupKeyRecoveryAsync(
        MeshlineClient member, GroupRef group, CancellationToken cancellationToken = default)
    {
        var request = await member.GroupManager.RequestKeyRecoveryAsync(group, cancellationToken);
        Console.WriteLine(request);
    }

    public static Task ApproveGroupKeyRecoveryAsync(
        MeshlineClient administrator, GroupRef group, string memberAccountId,
        CancellationToken cancellationToken = default) =>
        administrator.GroupManager.ApproveKeyRecoveryAsync(group, [memberAccountId], cancellationToken);
    #endregion
}
