# Groups

Groups exchange encrypted messages under verified membership and epoch keys.
Use `groupManager` with a `GroupRef` containing `groupId` and `relayId`.

## Create and invite

This function from [workflows.ts](../../examples/workflows.ts) creates a group and
an invitation for one account:

```ts
export async function createTeam(client: MeshlineClient, relayId: string, invitee: string, expiresAt: number) {
    const group = await client.groupManager.createGroup(relayId, { name: 'Team', memberCapacity: 20 });
    return client.groupManager.createInvite(group.ref, { expiresAt, invitee });
}
```

`memberCapacity` is required. The invite policy defaults to `administrators`.
Invitation expiry is integer Unix seconds. Omit `invitee` for a shareable
invitation and use `maxUses` when limiting uses.

The joining account previews with `getGroup(invitation)`, then calls
`applyToGroup(invitation)`. An administrator reads `getApplications(group)`
and calls `approveApplications(group, [accountId])` or `rejectApplications`.
Application submission does not grant membership immediately: wait for verified
membership and available keys before sending. The examples split these actions
into `applyToTeam`, `approveTeamMember`, and `sendTeamMessage`.

## Understand invitation authority

Preview and application use `group.resolve` with the invitation ID. Local checks
validate document fields, expiry, and recipient; the hosting relay enforces its
accepted invitation, including revocation and remaining uses. These calls do not
independently verify the supplied invitation document's signature.

`getInvite({ group, inviteId })` reads and verifies the relay's signed invitation
record and requires membership. Owners and administrators can read all invitations;
ordinary members can read only those they created. `getInvites` lists invitations
and `revokeInvite` revokes one, subject to permissions.

Group previews are presentation data. Verified signed history establishes
membership, roles, and epoch authority. Approval and rotation seal secrets only
for verified recipients.

## Messages and administration

`sendMessage(group, draft)` sends group content; replies use `replyToSeq`.
`setNickname(group, nickname)` changes the caller's nickname, with `null` clearing
it. `getMessages({ groupId, sender })` opens local message history.
`getGroups`, `getMembers`, and `getBans` also return disposable local readers.

Use `updateGroup` for properties, `setRole` for non-owner role changes, and
`transferOwnership` for an ownership transfer. `removeMembers`, `ban`, `unban`,
`leaveGroup`, and `closeGroup` enforce the caller's verified authority.
Omitted update fields retain their values; only nullable fields allow deletion.

The owner calls `rotateSecret(group)` to rotate the epoch secret.
`rotateOwnerMemberKey` defaults to false; pass true to rotate the owner's member
key as well. A pending rotation retains its staged choice. Resolve that operation
before attempting a different owner-key choice.

## Recover group access

A newly authorized device can request current member keys and historical epoch
secrets from another running device of the same account. Keep that source device
available during synchronization.

Without such a device, call `requestKeyRecovery(group)`. An administrator uses
`getKeyRecoveryRequests` and `approveKeyRecovery(group, [accountId])` or
`rejectKeyRecovery`. The requester can call `withdrawKeyRecovery`.
The example functions show request and approval as separate actions.

Member-key recovery restores current/future access. It cannot recreate historical
secrets that no available device retains.

## Preserve uncertain operations

If a submission response is lost or malformed, reopen the same store with the
same protector. An offline approval can complete the pending application or key
recovery only when verified history matches the original account, candidate key,
and approval event type. Approval for another device's candidate is insufficient.

Do not delete pending requests, generate replacement candidates as a generic
retry, or infer successful recovery solely from a preview saying the account is
a member. Pending requests retain their exact signed bytes, and group account
synchronization commits keys and its cursor atomically.

`groupChanged` reports committed metadata/membership changes.
`timelineChanged` may describe verified history without a decrypted message;
check its optional `message` before appending a chat item.
See [events](events-and-troubleshooting.md).

[All guides](../README.md)
