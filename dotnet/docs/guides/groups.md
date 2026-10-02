# Encrypted groups

[Guide index](../README.md) · [Events and troubleshooting](events-and-troubleshooting.md)

## Create a group and send content

Use a running, authorized client and an eligible hosting relay. Select a capacity supported by the relay and an invitation policy suitable for the application. `GroupRef` binds the group ID to its host.

<!-- snippet: group -->
```csharp
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
```
<!-- /snippet -->

Source: [Spaces.cs](../../examples/Meshline.Sdk.Examples/Spaces.cs). Group sending returns a `GroupMessageInfo`; it does not use the direct-message outbox's `MessageSendStatus` contract. Membership, available group secrets, and relay authorization govern whether a send can complete. Use `GetMessagesAsync` for locally stored group messages and `TimelineChanged` to refresh the view.

## Invite and admit members

An invitation and an approved membership are separate states. The inviter needs permission under the group's policy. Pass the returned invitation to the applicant, submit an application, then let an authorized administrator approve it:

<!-- snippet: group-admission -->
```csharp
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
```
<!-- /snippet -->

The methods represent separate user actions, not automatic approval of every request. Observe `ApplicationsChanged` and inspect `GetApplicationsAsync` before selecting accounts to approve or reject. `CreateInviteAsync` also supports account-bound invitations; `GetInvitesAsync`, `GetInviteAsync`, and `RevokeInviteAsync` manage existing invitations. An accepted application is subject to protocol validation, capacity, and current membership state.

## Membership, roles, and closure

`GetMembersAsync` and `GetBansAsync` query local views. Administrative operations include removal, banning, unbanning, role changes, and ownership transfer. `SetNicknameAsync` updates the local account's group nickname. Permission depends on current role and operation; consult the method's documented preconditions and structured relay errors.

`LeaveGroupAsync` changes this account's membership; `CloseGroupAsync` closes the group. Closure is not a promise that the network retains readable ciphertext forever. Locally stored content and relay-retained content have different lifetimes.

## Recover group keys

A valid account/device authorization is not sufficient to decrypt group content when the device lacks the required group secrets. Keep the protected local database across restarts. If key recovery is needed, use the group's dedicated workflow:

<!-- snippet: group-recovery -->
```csharp
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
```
<!-- /snippet -->

An administrator inspects `GetKeyRecoveryRequestsAsync` and approves or rejects particular accounts. `KeyRecoveryChanged` reports recovery changes. `RotateSecretAsync` rotates group secret material and can optionally rotate the owner's member key. Rotation and recovery require the documented role and state; they do not guarantee restoration of every historical message after secrets or relay payloads are lost.

Group synchronization consumes the locally stored account-message stream with a durable cursor and commits group changes with that cursor. Keep `MessageManager` running alongside `GroupManager`; `MeshlineClient` coordinates both. Interrupted group operations retain confirmation state. If an operation reports that an earlier request is awaiting confirmation, let the running component recover it before submitting another operation of that kind.

## API reference

[GroupManager](../api/Meshline.Components.GroupManager.md) · [GroupCreateOptions](../api/Meshline.Models.Client.GroupCreateOptions.md) · [GroupInfo](../api/Meshline.Models.Client.GroupInfo.md) · [GroupInvite](../api/Meshline.Models.Client.GroupInvite.md) · [GroupKeyRecoveryInfo](../api/Meshline.Models.Client.GroupKeyRecoveryInfo.md)
