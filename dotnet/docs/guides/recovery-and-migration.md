# Recovery and home-relay migration

[Guide index](../README.md) · [Accounts and devices](accounts-and-devices.md)

## Choose the workflow deliberately

| Situation | Operation |
| --- | --- |
| Account has no established route | `EstablishAccountAsync` after initialization. |
| Same authorized device and usable local database | `InitializeAsync`, then `StartAsync`. |
| Existing account needs restored device authorization | `RecoverAccountAsync` with the account signer before starting. |
| Authorized account moves to a different home relay | `ChangeHomeRelayAsync` with the account signer and existing state. |

All account writes require access to the account signing mechanism. Preserve the existing database and protection keys when they are available; a new empty database cannot reconstruct all local history or protected group secrets.

## Recover account access

Initialize a client constructed with an account signer, then explicitly select recovery:

<!-- snippet: recovery -->
```csharp
public static async Task RecoverAsync(
    MeshlineClient initializedClient,
    string relayId,
    CancellationToken cancellationToken = default)
{
    // The client was constructed with an account signer and has not started.
    await initializedClient.RecoverAccountAsync(
        new AccountRecoveryOptions { RelayId = relayId }, cancellationToken);
}
```
<!-- /snippet -->

Source: [Sessions.cs](../../samples/Meshline.Sdk.Examples/Sessions.cs). After recovery completes, start the client and observe synchronization. `AccountRecoveryOptions` can carry verified `PreviousDeviceState` and explicit device-state and route revisions when the application needs them. Otherwise the SDK selects recovery revisions. A supplied previous state can preserve devices; do not invent previous state or assume every old device is preserved when that state is unavailable.

Recovery publishes device state and an account route. It is not a read-only probe and must not run automatically whenever initialization, a request, or socket connection fails. Diagnose storage access, network binding, secret decryption, and relay availability first.

## Move to another home relay

Prerequisites: initialized client, known route, complete device state, local device, account signer, and an eligible target relay from the same registry.

<!-- snippet: migration -->
```csharp
public static Task MigrateHomeRelayAsync(
    MeshlineClient client,
    string targetRelayId,
    CancellationToken cancellationToken = default) =>
    client.ChangeHomeRelayAsync(targetRelayId, cancellationToken);
```
<!-- /snippet -->

The SDK persists migration progress so an interrupted move can retry or resume during startup. Retry the original target while that migration is pending. Selecting another target or encountering a concurrently changed route requires resolving the conflict rather than resetting the database.

Migration transfers device authorization, route, and profile state. It does not copy message history from the old relay to the new one. Retained local messages remain in the database; future history availability is governed by relay retention and the protocol. Account recovery and group-key recovery are separate workflows: use the [group recovery APIs](groups.md#recover-group-keys) when the device lacks required group secrets.

## API reference

[MeshlineClient](../api/Meshline.MeshlineClient.md) · [AccountRecoveryOptions](../api/Meshline.Models.Client.AccountRecoveryOptions.md) · [AccountEstablishmentOptions](../api/Meshline.Models.Client.AccountEstablishmentOptions.md)
