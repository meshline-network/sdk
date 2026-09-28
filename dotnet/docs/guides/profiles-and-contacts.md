# Profiles and contacts

[Guide index](../README.md) · [Direct messages](direct-messages.md)

## Update a profile

Use an initialized, authorized client with access to its home relay. `GetProfileAsync` resolves the current or another account's profile; `client.Profile` exposes the current account's cached profile. Profile changes are reported by `ProfileChanged`.

`FieldUpdate<T>` distinguishes an omitted field, an assigned value, and explicit deletion. An omitted update leaves the existing value unchanged; assigning `FieldUpdate<T>.Delete` removes an optional field. Required fields such as `PublicDiscovery` cannot be deleted.

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

Source: [Messaging.cs](../../samples/Meshline.Sdk.Examples/Messaging.cs), which imports `Meshline.Models`, `Meshline.Models.Client`, and `Meshline.Models.Protocol`. This changes the nickname, removes the biography, and leaves avatar and public-discovery preference unchanged. A content reference does not upload the avatar; the application provides content hosting and retrieval.

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

Run these operations on the appropriate accounts. Sending a request does not immediately establish the authorization required to send ordinary direct messages. Observe contact changes and the contact's grant state before enabling messaging.

For invitation-based discovery, create a time-limited `ContactInvite` using `CreateInviteAsync` and pass it to the `AddContactAsync` overload on the other account. Keep invitations within their intended audience and expiry. `DismissContactRequestAsync` dismisses a request; `SetAliasAsync` changes a contact alias; `RemoveContactAsync` removes the relationship. List and lookup APIs return local contact views, so keep synchronization running and refresh after events.

## Synchronization boundaries

Contact changes and account-message synchronization positions are committed together. A direct message received without the required local contact authorization is discarded; adding the contact later does not replay that message. Contact records more than five minutes ahead of the local clock are rejected and reported through `BackgroundError`; check clock accuracy when diagnosing validation failures.

## API reference

[ProfileManager](../api/Meshline.Components.ProfileManager.md) · [ProfileUpdate](../api/Meshline.Models.Client.ProfileUpdate.md) · [FieldUpdate](../api/Meshline.Models.FieldUpdate_T_.md) · [MessageManager](../api/Meshline.Components.MessageManager.md) · [ContactInfo](../api/Meshline.Models.Client.ContactInfo.md)
