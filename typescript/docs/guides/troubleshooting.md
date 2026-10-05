# Troubleshooting

[Guide index](../README.md) · [Events and errors](events-and-errors.md)

## Find the failing boundary

| Symptom | Likely cause | What to do |
| --- | --- | --- |
| Client cannot open its database | Missing migration, incorrect binding, inaccessible storage, or lost protector | Migrate explicitly; check the network/account/database and restore access to the original protection keys. Surface corruption instead of opening a new empty store. |
| Existing account fails initial establishment | This database has no authorized device for the existing route | Use the application's explicit account recovery flow with an account signer. |
| Send returns but the peer has no message yet | The return is an outbox status, or background work is stopped | Start the client, inspect `getSendStatus`, and observe `sendStatusChanged`. |
| Page does not show a new arrival | The reader holds a fixed snapshot | Dispose it and open a new reader. |
| Profile edit or migration reports a conflict | An earlier request is uncertain or authoritative state changed | Let the original operation reconcile; inspect the current state before choosing another edit or target. |
| New device cannot decrypt old group messages | Historical secrets are unavailable on that device | Keep another authorized device running for key synchronization; recovery alone cannot recreate missing historical keys. |
| Group approval does not complete a pending request | Approval does not verify the original candidate, or history/key processing failed | Keep the original store/protector, inspect background errors, and verify the correct application's approval. |
| Expo cannot find `MeshlineRelaySocket` | Expo Go or an old native binary lacks the module | Rebuild the native app after installing the adapter. |
| Android reports a released SQLite object or incomplete HTTP body | Pinned Expo native dependencies may be missing compatibility fixes | Apply and check the [Android fixes](../platforms.md#android-compatibility), then rebuild. If already applied, retain the error for diagnosis. |
| Browser relay traffic carries cookies | Ambient WebSocket policy or the known Windows WebKit fetch issue | Use a cookie-free relay origin and review [browser limits](../platforms.md#browser). |
| Authentication stays rejected after a public request succeeds | A public request does not reauthorize the device | Check route/device authorization and establish a newly authenticated session. |
| Synchronization is `blocked` | The latest pass could not complete | Inspect `blockReason` and `error`, correct the cause, and retry a [fresh pass](synchronization.md#run-a-foreground-pass). Missing group keys require key recovery. |
| A request times out | An SDK-owned request deadline expired | Inspect `TimeoutError.operation`, `timeoutMilliseconds`, and `cause`; reconcile pending writes before retrying. See [error contracts](events-and-errors.md#cancellation-and-error-types). |
| Send wait ends without success | The send failed, was canceled, or its record is unavailable | Inspect the result: `failed`/`canceled` end waits; `undefined` means unknown or evicted. See [send waiting](direct-messages.md#wait-for-an-acceptance-milestone). |

## Resume without discarding state

Keep the original database, secret protector, and pending operation IDs. Do not
turn every failure into account recovery or a new send. Await shutdown when
possible and resume against retained cursors and outbox records.

HTTP polling can succeed while WebSocket notifications are unavailable. A connected
socket or completed startup does not establish caught-up history; use
[synchronization status](synchronization.md#interpret-status) and fresh local queries.

When reporting a problem, include SDK and adapter versions, runtime, operation,
structured error, and reproduction steps. Remove private keys, protected-secret
blobs, credentials, and message plaintext from shared logs.

## Validation and recovery limits

The SDK remains alpha. [Platform requirements and limits](../platforms.md) are the
authority for browser, Node, Expo, Android, Linux, and iOS validation. Compiled
examples and offline tests do not establish every live network or device deployment.

Account recovery does not recreate missing historical keys. A caught-up resource
can have retention gaps, a local read position is not a processing ACK, and target
acceptance is not a recipient read receipt. Follow the dedicated
[synchronization](synchronization.md#retention-and-history-limits),
[read-position](conversations.md#track-read-positions), and
[outbox](direct-messages.md#retained-send-history) contracts when diagnosing missing data.

[All guides](../README.md)
