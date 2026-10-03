# Meshline TypeScript SDK

Build applications with self-sovereign accounts, encrypted direct and group
messages, public channels, and persistent local conversations. `MeshlineClient`
coordinates these capabilities for one network, account, and local device.
Your application supplies account signing, relay registry access, and secret protection.

**SDK 0.1.0-alpha.1** consists of ESM packages with TypeScript declarations.

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
npm install @meshline/sdk@0.1.0-alpha.1 @meshline/storage-node@0.1.0-alpha.1 @meshline/transport-node@0.1.0-alpha.1
```

For browser and Expo installation commands, see the [quick start](docs/guides/quick-start.md).

To build packages from source, use Node.js 24 or later and run from this
repository's `typescript/` directory:

```sh
npm ci
npm run pack:local
```

The tarballs are written to `artifacts/packages/`. Building local packages does
not require .NET or running the SDK test suite.

## Start building

1. [Open a client session](docs/guides/quick-start.md).
2. [Connect your signer, registry, and secret protector](docs/guides/integrations.md).
3. [Establish contacts and send a message](docs/guides/direct-messages.md).
4. Explore the [topic guides](docs/README.md) and [compilable examples](examples/README.md).

Wire formats and interoperability rules are defined by the
[Meshline Protocol 1.0 draft](https://meshline.org/protocol/v1/en/index.html).
