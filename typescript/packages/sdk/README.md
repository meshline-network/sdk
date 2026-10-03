# @meshline/sdk

Build Meshline applications with accounts, devices, profiles, contacts, encrypted
direct/group messages, public channels, and local conversations. `MeshlineClient`
coordinates six managers. The core has no Node.js, React, or Expo runtime dependency.

## Install

This ESM alpha includes TypeScript declarations. Install the core:

```sh
npm install @meshline/sdk@0.1.0-alpha.1
```

Also install the matching adapters for your application:

| Runtime | Additional packages |
| --- | --- |
| Node.js 24+ | `@meshline/storage-node`, `@meshline/transport-node` |
| Browser | `@meshline/storage-browser` |
| Native Expo | `@meshline/expo` |

See [installation commands](https://github.com/meshline-network/sdk/blob/main/typescript/docs/guides/quick-start.md)
and [platform requirements](https://github.com/meshline-network/sdk/blob/main/typescript/docs/platforms.md).

## Open a client

Supply a trusted `NetworkContext`, `accountId`, `RelayRegistry`,
`SecretProtector`, platform store, and `RelayClientPool`. Account-authorized
operations also require an `AccountSigner`; the SDK does not request account
private keys.

Migrate the store explicitly, initialize the client, establish or recover account
authorization when needed, then start. Ordinary restarts reuse the same database
and protector and do not require account recovery.

The client owns its managers; the application owns the store, pool, and signing/
protection integrations. Dispose the client before the pool and store.
A successful direct `sendMessage` queues a durable request; observe send status
for remote acceptance. Models use camelCase fields and explicit wire codecs.
Operations return promises and accept cancellation signals.

Start with the [application guide](https://github.com/meshline-network/sdk/blob/main/typescript/docs/README.md)
and [platform examples](https://github.com/meshline-network/sdk/blob/main/typescript/examples/README.md).
