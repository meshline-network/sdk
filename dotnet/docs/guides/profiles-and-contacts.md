# Profiles and contacts

[Guide index](../README.md) · [Direct messages](direct-messages.md)

## Update a profile

Use an initialized, authorized client with access to its home relay. `GetProfileAsync` resolves the current or another account's profile; `client.Profile` exposes the current account's confirmed cached profile. Confirmed profile changes are reported by `ProfileChanged`.

`FieldUpdate<T>` distinguishes omission, assignment and deletion. Omitted fields
retain existing values and unfinished edits; `FieldUpdate<T>.Delete` removes an optional
field. Required fields such as `PublicDiscovery` cannot be deleted. Profile updates
follow the shared [completion and retry rules](events-and-errors.md#cancellation-and-error-types).
`PublishProfileAsync` republishes the newest available profile, including unfinished edits.
When supplying a snapshot, its `UpdatedAt` determines whether it replaces a pending
snapshot: the higher timestamp wins, and equal timestamps retain the pending snapshot.

<!-- snippet: profile -->
```csharp
public static Task<AccountProfile> UpdateProfileAsync(
    MeshlineClient client, CancellationToken cancellationToken = default) =>
    client.ProfileManager.UpdateProfileAsync(new ProfileUpdate
    {
        Nickname = "Alice",
        Bio = FieldUpdate<string>.Delete
        // Omitted Avatar and PublicDiscovery fields remain unchanged.
    }, cancellationToken);
```
<!-- /snippet -->

Source: [Messaging.cs](../../examples/Meshline.Sdk.Examples/Messaging.cs), which imports `Meshline.Models`, `Meshline.Models.Client`, and `Meshline.Models.Protocol`. This changes the nickname, removes the biography, and leaves avatar and public-discovery preference unchanged. A content reference does not upload the avatar; the application provides content hosting and retrieval.

### Display pending edits and synchronization status

After initialization, call `ProfileManager.GetUpdateStateAsync` to read local state
without contacting the relay. Subscribe to `UpdateStateChanged`, then read once and
read again after each notification. Handlers run synchronously and may be called on
a background thread. Marshal UI updates to your dispatcher and serialize reads so
an older asynchronous read cannot replace a newer view. Handler exceptions reach
`BackgroundError` without changing the publication outcome.

`PendingUpdate` contains the latest merged field changes, using the same
`FieldUpdate<T>` distinction between omission, assignment and deletion. Overlay
these fields on `Profile` for an editor or optimistic display. `Profile` itself
continues to contain only confirmed values. The query returns a detached snapshot;
editing it does not submit changes. Republication without field edits has a non-null
`PendingUpdate` with every field unspecified; cached or supplied snapshots do not
create pending field changes. Applications need no separate pending-data store.

| `Status` | Meaning |
| --- | --- |
| `Synchronized` | No local publication is pending. `PendingUpdate`, `LastError`, and `NextAttemptAt` are null; a confirmed profile may still be unavailable. |
| `Pending` | Saved intent is waiting for an attempt, including after restart. |
| `Publishing` | The latest update is being resolved, signed, or sent. |
| `WaitingToRetry` | The latest attempt failed; `NextAttemptAt` gives the scheduled UTC retry time. |
| `Failed` | Intent is retained, but automatic retry is paused. Correct the cause and submit an update or call `PublishProfileAsync`. |

Intent, the latest error (message, optional relay code and failure time), and the
retry deadline survive restart. New intent clears the error; retries retain it
until confirmation or another failure. If saving an outcome fails, its diagnostics
and retry policy remain available in the current process, but only saved state
survives restart. Automatic retries require a running client/component.

The same query and notifications cover the initial publication during
[account establishment](account-establishment.md#keys-and-interrupted-setup).

## Exchange contact requests

`MessageManager` manages contacts as well as direct messages. Start both clients so they can synchronize account messages. On the requesting account:

<!-- snippet: contact-request -->
```csharp
public static Task<ContactRequestInfo> RequestContactAsync(
    MeshlineClient client, string accountId, CancellationToken cancellationToken = default) =>
    client.MessageManager.AddContactAsync(accountId, "Hello from my app", cancellationToken);
```
<!-- /snippet -->

The receiving account lists requests with `GetContactRequestsAsync` or reacts to `ContactRequestChanged`. After the user accepts a specific incoming request:

<!-- snippet: accept-contact -->
```csharp
public static Task<ContactInfo> AcceptContactAsync(
    MeshlineClient receivingClient, string requesterAccountId,
    CancellationToken cancellationToken = default) =>
    receivingClient.MessageManager.AcceptContactRequestAsync(requesterAccountId, cancellationToken);
```
<!-- /snippet -->

Run these operations on the appropriate accounts. Contact requests and acceptance messages automatically request offline push notifications. Sending a request does not immediately establish the authorization required to send ordinary direct messages. Observe contact changes and the contact's grant state before enabling messaging.

For invitation-based discovery, create a time-limited `ContactInvite` using `CreateInviteAsync` and pass it to the `AddContactAsync` overload on the other account. Keep invitations within their intended audience and expiry. `DismissContactRequestAsync` dismisses a request; `SetAliasAsync` changes a contact alias; `RemoveContactAsync` removes the relationship. List and lookup APIs return local contact views, so keep synchronization running and refresh after events.

## Synchronization boundaries

Contact changes and account-message synchronization positions are committed together. `MessageManager` notifies contacts when its own account's verified device-state revision advances and resumes pending notifications after restart.

A direct message received without the required local contact authorization is discarded; adding the contact later does not replay that message. Contact records more than five minutes ahead of the local clock are rejected and reported through `BackgroundError`; check clock accuracy when diagnosing validation failures.

## API reference

[ProfileManager](../api/Meshline.Components.ProfileManager.md) · [ProfileUpdate](../api/Meshline.Models.Client.ProfileUpdate.md) · [ProfileUpdateState](../api/Meshline.Models.Client.ProfileUpdateState.md) · [FieldUpdate](../api/Meshline.Models.FieldUpdate_T_.md) · [MessageManager](../api/Meshline.Components.MessageManager.md) · [ContactInfo](../api/Meshline.Models.Client.ContactInfo.md)
