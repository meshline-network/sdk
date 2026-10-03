# Application integrations

`MeshlineClient` accepts application-owned dependencies. The
[platform examples](../../examples/README.md) wire them into the client and relay pool.
The core interfaces are exported by `@meshline/sdk`.

## Network and account

Construct `NetworkContext` from your trusted Neo network reference and registry
contract hash, or parse its canonical `neo:<reference>:<registry>` representation.
The reference is a uint32 and the registry hash is `0x` followed by 40 lowercase
hexadecimal characters. Do not obtain this trust configuration from a remote
message or descriptor being verified.

Use the same context and canonical account identifier for the client, registry,
relay pool, and storage binding. A pool belongs to one network/account/local device.

## Account signing

Use `Nep6AccountSigner` below, or implement `AccountSigner` with `accountId`, `publicKey`, and
`sign(input, signal?)`. Sign the exact supplied bytes using Neo P-256/SHA-256.
The SDK never requests the account private key; keep it in your wallet or signing
integration. Account authority is required for establishment, recovery, and
account-authorized route and device operations. An already-authorized device can
perform its device-authorized work without keeping the account signer online.

Do not mutate `Uint8Array` arguments while an operation is pending. Propagate
signing rejection and cancellation to the caller. A hardware-backed signer
manages its own randomness. If an Expo integration uses the low-level
`signAccount` helper instead, pass `expoRandom` as its third argument.

## Relay registry

Use `RpcRelayRegistry` below, or implement `RelayRegistry.context`, `getRelay(relayId, signal?)`, and the async
iterator `getRelays(signal?)` using the registry for your chosen network.
`RelayEntry` includes the relay ID, discovery endpoint, status, and `updatedAt`.
That registry timestamp is a `bigint` in Unix **milliseconds**; protocol JSON
timestamps normally use integer Unix **seconds**.

Discovery requires an active registry entry and verifies the relay's signed
descriptor against the trusted context and requested identity. Use registry
access for discovery; do not substitute an unverified endpoint for a relay ID.

## Optional Neo implementations

Both implementations are exported by `@meshline/sdk`. The application explicitly
constructs and passes them to its client/pool; no implementation is selected automatically.
See the compiled [Neo example](../../examples/neo-integrations.ts).

```typescript
import { RpcRelayRegistry, Nep6AccountSigner } from '@meshline/sdk';

const registry = new RpcRelayRegistry({ context, rpcUrl });
const signer = await Nep6AccountSigner.fromJson(walletJson, password, { context });
// Pass registry and signer to your session. After disposing dependent clients:
signer.dispose();
```

`RpcRelayRegistry` verifies network magic before querying the configured contract,
preserves inactive entries, and releases iterator sessions on completion,
cancellation, or early exit. It defaults to 100 entries per page and a 15000 ms
timeout per RPC request. An RPC node must support iterator sessions or explicitly
complete inline results. Inject `fetch` for platform transport configuration,
including `createNodeRelayFetch` or `expoRelayFetch`; custom transports must honor
request cancellation. Query errors propagate. Session cleanup errors go to
`onSessionCleanupError` (default: `console.warn`) without replacing the query failure.
The adapter only reads the Registry; it does not submit transactions.

`Nep6AccountSigner.fromJson` accepts Neo N3 NEP-6 version 1.0 JSON loaded by the
application, so it requires no Node filesystem API. Pass `accountIndex` to select
an account; the default is 0 in file order, irrespective of `isDefault`. It never
tries another account when the selected one fails. Only standard single-signature
accounts with matching address, contract, and encrypted key are accepted.
Watch-only, deployed, and multisignature accounts are unsupported.

Decryption uses the wallet's scrypt parameters and NEP-2 NFC password normalization.
Both SDKs limit imported costs to 256 MiB estimated memory and
`n * r * p <= 16777216`; standard NEP-6 parameters fit these limits. Loading never
changes the wallet. Supply passwords at runtime and dispose the signer after use.
On Expo, pass `random: expoRandom`. Disposal clears owned private-key buffers on a
best-effort basis; JavaScript cannot guarantee erasure of every runtime copy.
Keep using a custom `AccountSigner` for external or hardware wallets.

## Secret protection

Implement `SecretProtector.protect(plaintext, purpose, signal?)` and
`unprotect(protectedData, purpose, signal?)` using persistent, authenticated
protection appropriate to the platform. Bind the purpose string to the protected
data so a secret cannot be reused in a different context.

The protector must unprotect existing data after an app restart. Do not use a new
ephemeral protection key on every launch or store device secrets unprotected.
On native platforms, manage protection keys through the application's
Keychain/Keystore integration. In the browser, design persistence and key access
for your authentication model and origin.

Protecting SDK secrets does not encrypt the database or stored message text.
Apply any broader storage protection required by your application separately.

## Persistence and content

Inject a platform store and call `migrate()` before client initialization.
Preserve it across launches; it contains device identity, keys, cursors, and
pending requests. See [storage](storage-and-pagination.md).

Attachment upload, content retrieval, rendering, and user consent belong to the
application. The SDK handles content references and messaging protocol data;
it does not supply a file hosting service or render received content.

[All guides](../README.md)
