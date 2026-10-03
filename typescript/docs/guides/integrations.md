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

Implement `AccountSigner` with `accountId`, `publicKey`, and
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

Implement `RelayRegistry.context`, `getRelay(relayId, signal?)`, and the async
iterator `getRelays(signal?)` using the registry for your chosen network.
`RelayEntry` includes the relay ID, discovery endpoint, status, and `updatedAt`.
That registry timestamp is a `bigint` in Unix **milliseconds**; protocol JSON
timestamps normally use integer Unix **seconds**.

Discovery requires an active registry entry and verifies the relay's signed
descriptor against the trusted context and requested identity. Use registry
access for discovery; do not substitute an unverified endpoint for a relay ID.

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
