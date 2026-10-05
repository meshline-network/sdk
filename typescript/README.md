# Meshline TypeScript SDK

Build applications with self-sovereign accounts, encrypted direct and group
messages, public channels, and persistent local conversations. `MeshlineClient`
coordinates these capabilities for one network, account, and local device.
Your application supplies account signing, relay registry access, and secret protection.

The SDK is available on the **alpha** channel as ESM packages with TypeScript declarations.

The optional `RpcRelayRegistry` and `Nep6AccountSigner` adapters provide RPC
registry access and NEP-6 wallet signing. Applications select them explicitly and continue to
provide secret protection. See the [integration guide](docs/guides/integrations.md).

## Choose your packages

Install `@meshline/sdk` and the adapters for your application together.
The core has no Node.js, React, or Expo runtime dependency.

| Application | Adapters | Requirements |
| --- | --- | --- |
| Node.js | `@meshline/storage-node`, `@meshline/transport-node` | Node.js 24 or later |
| Browser | `@meshline/storage-browser` | Secure origin, IndexedDB, Web Locks, cryptographic randomness |
| Native Expo | `@meshline/expo` | Expo 57.0.25, Expo Modules Core 57.0.19, React Native 0.86.x (at least 0.86.3), a native build |

Read [platform requirements and limits](docs/platforms.md) before choosing a
runtime, including browser cookie restrictions and the required Android compatibility fixes.
Linux and iOS runtime validation remains pending.

## Install

For a Node.js application, install the core and both Node adapters:

```sh
npm install @meshline/sdk@alpha @meshline/storage-node@alpha @meshline/transport-node@alpha
```

For browser and Expo installation commands, see the [quick start](docs/guides/quick-start.md).

For source builds and local package tarballs, see [Maintenance](../MAINTENANCE.md#typescript-sdk-checks-and-local-packages).

Guides describe the current checkout. Use the matching release tag for an installed
package; APIs documented on `main` may not yet be published. Install matching core
and adapter versions together.

## Start building

1. [Open a client session](docs/guides/quick-start.md).
2. [Connect your signer, registry, and secret protector](docs/guides/integrations.md).
3. [Establish contacts and send a message](docs/guides/direct-messages.md).
4. [Synchronize data](docs/guides/synchronization.md), [page local history](docs/guides/storage-and-pagination.md), and [track read positions](docs/guides/conversations.md).
5. Use [events and errors](docs/guides/events-and-errors.md) and [troubleshooting](docs/guides/troubleshooting.md) when diagnosing failures.

The [quick start](docs/guides/quick-start.md) provides platform helpers that open,
migrate, and initialize a session. For an already authorized device, the application
lifetime then follows this pattern:

```ts
try {
    await session.client.start();
    await runApplication(session.client);
} finally {
    await session.dispose();
}
```

Here `session` comes from the platform helper and `runApplication` is your own
asynchronous application loop. For initial establishment or recovery, follow the
quick start before starting. Explore all [topic guides](docs/README.md),
[type-checked examples](examples/README.md), and [testing scope](TESTING.md).

Wire formats and interoperability rules are defined by the
[Meshline Protocol 1.0 draft](https://meshline.org/protocol/v1/en/index.html).
