# Recovery and home-relay migration

[Guide index](../README.md) · [Accounts and devices](accounts-and-devices.md)

## Choose the workflow deliberately

| Situation | Operation |
| --- | --- |
| First explicit enablement | `EstablishAccountAsync` after initialization. |
| Same authorized device and usable local database | `InitializeAsync`, then `StartAsync`. |
| Existing account needs new-device authorization, renewal, or explicit reauthorization | `EstablishAccountAsync` with the account signer before starting. |
| Restore saved contacts, history and group keys into an initialized device | `ImportBackupAsync`; authorize the device separately. |
| Authorized account moves to a different home relay | `ChangeHomeRelayAsync` with the account signer and existing state. |

All account writes require access to the account signing mechanism. Preserve the existing database and protection keys when they are available; a new empty database cannot reconstruct all local history or protected group secrets.

## Recover account access

Initialize a client constructed with an account signer, then explicitly establish authorization:

<!-- snippet: recovery -->
```csharp
public static async Task RecoverAsync(
    MeshlineClient initializedClient,
    string relayId,
    CancellationToken cancellationToken = default)
{
    // The client was constructed with an account signer and has not started.
    await initializedClient.EstablishAccountAsync(
        new AccountEstablishmentOptions { RelayId = relayId }, cancellationToken);
}
```
<!-- /snippet -->

Source: [Sessions.cs](../../examples/Meshline.Sdk.Examples/Sessions.cs). After setup completes, start the client separately. Read [account establishment](account-establishment.md) for structured failures, explicit full-list replacement, the complete-local-data contract, key-access diagnostics, and protocol limits. New-device authorization preserves existing profile fields and other devices.

`RecoverAccountAsync` supports advanced explicit recovery with revision and previous-state options. The unified establishment entry point does not call it as a fallback. Neither API is a general response to network or storage errors. Device reauthorization does not restore old chat history.

When explicit recovery needs to create a new local device, pass its callback URL
through `AccountRecoveryOptions.PushGateway`. A retained device keeps its current
declaration, including during renewal; this option does not update it. See
[gateway declaration and updates](accounts-and-devices.md#declare-an-offline-push-gateway)
for URL requirements and removal.

<!-- snippet: push-account-recovery -->
```csharp
public static Task RecoverPushEnabledAccountAsync(
    MeshlineClient client, string pushGateway, CancellationToken cancellationToken = default) =>
    client.RecoverAccountAsync(new AccountRecoveryOptions
    {
        PushGateway = pushGateway
    }, cancellationToken);
```
<!-- /snippet -->

## Export and merge a portable backup

Use `ExportBackupAsync` to save local data and `ImportBackupAsync` to merge it into
an initialized database for the same account and network. Create the target's own
local device before restoring group secrets. Account authorization remains a
separate step, described above.

Files always include contacts and grants, device verification material, group
management history and held keys, and channel entries/follow choices. Direct and
group chat history are opt-in. Identity keys, pending operations, read positions,
channel posts and attachment files are excluded; see the
[format and scope](../../../docs/backup-format-v1.md#ownership-and-scope).

Pass a nonempty Unicode password; the SDK applies NFC normalization while
preserving spaces and case. Streams stay caller-owned and open. Import reads from
the current position without requiring seeking.

<!-- snippet: portable-backup -->
```csharp
public static Task<BackupReport> ExportAsync(
    MeshlineClient client, Stream destination, string password,
    CancellationToken cancellationToken = default)
{
    // Always exports base data. Both history selections default to None.
    return client.ExportBackupAsync(destination, password, cancellationToken: cancellationToken);
}

public static async Task<BackupReport> ImportAsync(
    MeshlineClient client, Stream source, string password,
    CancellationToken cancellationToken = default)
{
    // The target has its own local device for protecting restored group secrets.
    var report = await client.ImportBackupAsync(source, password, cancellationToken);
    Console.WriteLine($"Import complete: {report.Applied} applied, {report.Unchanged} unchanged, {report.Conflicts} conflicts.");
    return report;
}
```
<!-- /snippet -->

`BackupOptions.DirectHistory` and `GroupHistory` default to `BackupSelection.None`.
Use `All` for all local history or `Only(...)` for peer account IDs or group IDs.
Direct history includes both directions even if the peer is no longer a contact.
An empty selection includes no history; invalid identifiers or an oversized
selection fail before any file bytes are written. Omitted target history is preserved.

<!-- snippet: selected-backup-history -->
```csharp
public static Task<BackupReport> ExportSelectedHistoryAsync(
    MeshlineClient client, Stream destination, string password,
    string[] peerAccountIds, string[] groupIds,
    CancellationToken cancellationToken = default) =>
    client.ExportBackupAsync(destination, password, new BackupOptions
    {
        DirectHistory = BackupSelection.Only(peerAccountIds),
        GroupHistory = BackupSelection.Only(groupIds)
    }, cancellationToken);
```
<!-- /snippet -->

Source: [Backups.cs](../../examples/Meshline.Sdk.Examples/Backups.cs).

Operations can run alongside a running client. Export uses a consistent snapshot
per component; inconsistent verification material across snapshots raises
`IOException`. Discard partial export output and retry. Import commits validated
records incrementally: later errors or cancellation preserve earlier commits.
A wrong password fails before any record commits. Retry from the file's beginning
to merge remaining data without duplication. Cancellation may wait for an active
password derivation to finish.

Successful import means the complete file validated. `BackupReport` counts applied,
unchanged and conflicting records; failures throw instead of returning a partial
report. The [merge rules](../../../docs/backup-format-v1.md#merge-and-failure-semantics)
preserve newer local data and define record-specific conflicts. Continue using the
same client after a successful or partial import, opening new query readers to see
changes. Updates use the [ordinary component events](events-and-errors.md#choose-events-by-task)
and [local read positions](conversations.md#track-read-positions). Normal contact
authorization and group access checks still apply; import starts no network work.

For custom composition, `DeviceManager`, `MessageManager`, `GroupManager` and
`ChannelManager` also expose plaintext `IAsyncEnumerable<JsonElement>` record
streams. Callers guarantee the account, network, record order and cross-component
consistency. Exported elements remain valid after enumeration; input documents
must remain alive while each record is processed, or be cloned. Use the component
API references for these contracts and `MeshlineClient` for encrypted files.

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

[MeshlineClient](../api/Meshline.MeshlineClient.md) · [BackupOptions](../api/Meshline.Models.Client.BackupOptions.md) · [BackupReport](../api/Meshline.Models.Client.BackupReport.md) · [AccountRecoveryOptions](../api/Meshline.Models.Client.AccountRecoveryOptions.md) · [AccountEstablishmentOptions](../api/Meshline.Models.Client.AccountEstablishmentOptions.md)
