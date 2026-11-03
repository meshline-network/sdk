# Profiles and contacts

Use `profileManager` for signed account profiles and `messageManager` for contact
invitations, requests, authorization, and aliases.

## Edit your profile

This function from [workflows.ts](../../examples/workflows.ts) changes a nickname,
removes the bio, and leaves the avatar and discovery setting unchanged:

```ts
export async function updateProfile(client: MeshlineClient, nickname: string) {
    return client.profileManager.updateProfile({ nickname, bio: null });
}
```

Omitted fields retain their values. `null` deletes `nickname`, `bio`, or `avatar`;
`publicDiscovery` accepts a boolean. `getProfile(accountId?)` defaults to the
current account and can return `undefined`. Access to another account's profile
depends on its discovery and contact authorization.

Further edits merge with unfinished changes even while an earlier call is waiting;
see [completion and retry behavior](events-and-errors.md#cancellation-and-error-types).
`profileManager.profile` and `client.profile` contain confirmed values; changes are
reported by `profileChanged`. `publishProfile()` republishes the newest available
profile with unfinished edits. Supplied snapshots use `updatedAt` to select the
newer pending snapshot; equal timestamps retain the existing snapshot.

### Display pending edits and synchronization status

After `initialize()`, call `profileManager.getUpdateState()` to read local state
without network access, including before `start()`. Subscribe with
`on('updateStateChanged', listener)`, then read once and again after notifications.
Notifications have no payload. Serialize asynchronous reads so an older result
cannot replace a newer view; observer failures follow the usual
[event rules](events-and-errors.md#event-behavior).

`pendingUpdate` contains merged field edits: omission means unchanged, a value
means assignment, and `null` clears an optional field. Overlay these edits on the
confirmed profile for an editor. Cached or supplied publication snapshots do not
create field edits; pure republication has an empty `pendingUpdate`. Results are
detached, so changing them does not submit an update. Applications need no separate
pending-data store or access to internal records.

| `status` | Meaning |
| --- | --- |
| `synchronized` | No publication is pending. `pendingUpdate`, `lastError` and `nextAttemptAt` are absent; a confirmed profile may still be unavailable. |
| `pending` | Saved intent is waiting for an attempt, including after restart. |
| `publishing` | The latest update is being resolved, signed or sent. |
| `waitingToRetry` | An attempt failed; `nextAttemptAt` gives the scheduled UTC Unix time in seconds. |
| `failed` | Intent is retained with automatic retry paused. Correct the cause and submit an update or call `publishProfile()`. |

Intent, the latest error (`message`, optional relay `code` and `occurredAt` in UTC
Unix seconds), and the retry deadline survive restart. New intent clears the error;
retries retain it until confirmation or another failure. If saving an outcome fails,
its diagnostics and retry policy remain available in the current process, but only
saved state survives restart. Automatic retries require a running component.
The same query covers initial publication during
[account establishment](account-establishment.md#confirmation-and-protocol-limits).

## Establish a contact

Contact exchange is asynchronous. Run both clients while they exchange the
request and acceptance; network delivery must occur between the following actions.
Contact requests and acceptance messages automatically request offline push notifications.

1. The inviter calls `createInvite(expiresAt)` and shares the signed invitation
   through an application-chosen channel. The expiry is integer Unix seconds.
2. The recipient calls `addContact(invitation)`, optionally with a note.
3. The inviter waits for the incoming request, presents it to the user, and
   calls `acceptContactRequest(peerAccountId)` if accepted.
4. Wait for the contact authorization exchange before sending ordinary messages.

The `inviteContact`, `requestContact`, and `acceptContact` functions in
[workflows.ts](../../examples/workflows.ts) show these separate actions.
`addContact` also accepts an account ID when the account's authorization permits it.

Use `contactRequestChanged` and `contactChanged` to refresh application state.
`getContactRequests({ direction })` returns a local snapshot reader; omit
`direction` to include incoming and outgoing requests. `dismissContactRequest`
dismisses a request, and dismissing an absent request emits no change.

## Manage contacts

`getContact(accountId)` returns an optional local `ContactInfo`.
`getContacts(search?)` returns a disposable reader.
`setContactAlias(accountId, alias)` returns the updated contact snapshot.
Pass `null` to clear the alias; an empty string remains a distinct value.
Repeating the same alias performs no additional write, notification, or sync send.

`removeContact(accountId)` updates the local relationship and its synchronization
state. Render the resulting contact snapshot rather than retaining an assumed
relationship after an operation fails.

`MessageManager` notifies contacts when its own account's verified device-state
revision advances and resumes pending notifications after restart.

For payload details, see [events](events-and-errors.md). For encrypted
content and delivery state, continue to [direct messages](direct-messages.md).

[All guides](../README.md)
