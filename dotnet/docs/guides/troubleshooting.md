# Troubleshooting

[Guide index](../README.md) · [Events and errors](events-and-errors.md)

## Find the failing boundary

| Symptom or error | Check and response |
| --- | --- |
| SQLite open, schema, or binding failure | Confirm the directory, permissions, migration, and account/network identity. Preserve the database while correcting the cause. |
| Protected secrets cannot be restored | Reuse the original protection keys and purpose binding. A fresh protector with unrelated keys does not repair persisted secrets. |
| Startup reports missing device authorization | Inspect the route, certificate validity, and published device state; deliberately choose renewal or recovery when needed. |
| `HttpRequestException` or socket failure | Inspect relay endpoints, proxy/handler configuration, cancellation, and network availability. Background polling and reconnect may recover; keep diagnostics visible. |
| `RelayException` | Inspect its structured `Error`, including the protocol code and message. Resolve role, capacity, state conflict, or authorization requirements before retrying. |
| Invalid signature, protocol data, or JSON | Verify network/account binding and the received input. Do not accept invalid data as an empty successful response. |
| `TimeoutException` | An SDK request deadline expired. Inspect `Data["operation"]`, `Data["timeoutSeconds"]`, and `InnerException`; reconcile a submitted write before retrying. |
| `OperationCanceledException` | Inspect caller and component/session cancellation. An unrelated dependency cancellation does not establish an SDK timeout or prove that a remote mutation was rejected. |
| Pending message has an unknown outcome | Query the original outbox operation and let the SDK reconcile it. Sending a new message creates a different operation. |
| List does not show a recent update | Dispose the old snapshot reader and open a new query after the relevant event. |
| Contact synchronization rejects future timestamps | Check the local clock and the five-minute contact-record tolerance. |
| Group message cannot be decrypted | Check local secret availability and group membership, then consider group-key recovery. Account recovery alone does not restore all history. |
| Synchronization is `Blocked` | Inspect `BlockReason` and `Error`, correct the cause, and retry a [fresh pass](synchronization.md#run-a-foreground-pass). Missing group keys require key recovery. |
| Startup returned but a query is stale | Startup activates background work. Await a [synchronization pass](synchronization.md), then open a new local reader. |
| Send wait ends without success | Inspect its actual state. `Failed`/`Canceled` end waits, and `null` means unknown or evicted. See [send waiting](direct-messages.md#wait-for-an-acceptance-milestone). |

## Resume without discarding state

WebSocket notifications accelerate HTTP synchronization. Polling continues during
notification trouble; see [synchronization](synchronization.md#choose-how-to-refresh).

Do not delete storage, establish another account, or silently invoke recovery as a general retry policy. Retained migration progress, outbox operations, and synchronization cursors enable safe resumption. Use explicit application cancellation for work the user abandons and await shutdown so owned work can drain.

When reporting a problem, include the SDK version, operation, structured error, and reproduction steps. Remove private keys, protected-secret blobs, message plaintext, and bearer/session credentials from logs shared outside the application.

## Validation and recovery limits

Offline tests and compiled examples do not establish live-relay interoperability
or full platform support. Live-relay and cross-platform release validation,
including mobile, browser, and NativeAOT, remain pending; see [testing](../../TESTING.md).

Account recovery does not recreate missing historical keys. A caught-up resource
can have retention gaps, a local read position is not a processing ACK, and target
acceptance is not a recipient read receipt. Follow the dedicated
[synchronization](synchronization.md#retention-and-history-limits),
[read-position](conversations.md#track-read-positions), and
[outbox](direct-messages.md#retained-send-history) contracts when diagnosing missing data.
