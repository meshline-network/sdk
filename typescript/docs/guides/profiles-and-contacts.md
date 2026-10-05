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

A lost publication response leaves the original signed profile request pending.
Resolve it before making a conflicting edit. A different profile with the same
timestamp does not prove acceptance of the pending write.

## Establish a contact

Contact exchange is asynchronous. Run both clients while they exchange the
request and acceptance; network delivery must occur between the following actions.

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

For payload details, see [events](events-and-errors.md). For encrypted
content and delivery state, continue to [direct messages](direct-messages.md).

[All guides](../README.md)
