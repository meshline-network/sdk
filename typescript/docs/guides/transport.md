# Transport

A `RelayClientPool` discovers relays and maintains account/device sessions.
Managers use the application-supplied pool; the application disposes it after
the client. Use the [platform examples](../../examples/README.md) to configure it.

## Select adapters

| Runtime | HTTP | WebSocket |
| --- | --- | --- |
| Node.js | `createNodeRelayFetch()` from `@meshline/transport-node` | `createNodeSocketFactory()` from the same package |
| Browser | Native fetch through the core transport | `browserSocketFactory` from the core |
| Expo | `expoRelayFetch` from `@meshline/expo` | `createExpoSocketFactory()` from the same package |

Pass `fetch` and `socketFactory` in pool options together with the network context
and account ID. Expo also needs `random: expoRandom` on the pool and client.
The browser helper uses the core's default fetch implementation.

The Node adapters disable cookies, redirects, and implicit retries. HTTPS/WSS
retain normal certificate and hostname verification. Their `ca` option adds an
explicit private root for that transport; it does not alter process-wide trust.

Expo HTTP uses `expo/fetch` with credential omission and redirect rejection.
Its native socket has dedicated cookie-free clients and normal TLS trust checks.
The custom module must be in the native build.

Browser WebSocket handshakes use the browser's ambient cookie policy: use a relay
origin without cookies. Browser application close-code restrictions also apply.
Read the known WebKit issue and runtime details in [platform limits](../platforms.md).

## Discovery and authentication

Relay discovery starts from an active registry entry and validates the signed
descriptor for the trusted network and relay ID. The descriptor authorizes
endpoint selection. A cached URL alone is insufficient authority.

`relay.state.authentication` distinguishes `rejected` from `none` and `expired`.
Relay errors `unauthorized`, `device_unknown`, and `invalid_signature` mark
rejection. Permission errors and application callback failures do not.

A successful public HTTP request or cached token does not clear rejection; a
newly authenticated session does. A WebSocket renewal failure other than
`unauthorized` retains the previously accepted socket session until expiry while
still reporting the error.

HTTP sessions refresh when their remaining conservative lifetime reaches the
smaller of five seconds and 20% of the original lifetime. Concurrent callers share
the refresh. A refresh failure is returned rather than falling back to the
nearly expired token.

## Requests, subscriptions, and errors

Failed business requests return to the caller without automatic transport replay.
Managers that persist operations implement their own retry/reconciliation rules.
A lost response is not evidence that the remote operation was rejected.

Running managers coordinate subscriptions, polling, and catch-up. A connected
socket alone does not prove all local history has synchronized. Retain the same
store and observe domain updates and background failures when reconnecting.

Relay and pool subscriptions do not await callbacks. Callback errors use
`errorOccurred`, with diagnostics in `relay.state.lastError` or `pool.lastError`.
A callback failure does not retire a healthy connection or change an acknowledged
request into failure. Actual transport failures keep their recovery behavior.

[All guides](../README.md)
