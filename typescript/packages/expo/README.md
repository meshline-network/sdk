# Meshline Expo native adapters

Native SQLite, cryptographic randomness, HTTP, and WebSocket adapters for an
Expo Modules application. This ESM package includes TypeScript declarations.
Use `@meshline/storage-browser` and browser transport for an Expo web build.

## Install

Install the matching core and adapter together:

```sh
npm install @meshline/sdk@alpha @meshline/expo@alpha
```

Use Expo **57.0.25**, Expo Modules Core **57.0.19**, and React Native
**>=0.86.3 <0.87.0**. The native dependencies are `expo-crypto ^57.0.3`,
`expo-file-system ^57.0.7`, and `expo-sqlite ^57.0.3`.
In an Expo 57 app, install matching versions:

```sh
npx expo install expo@57.0.25 expo-modules-core@57.0.19 react-native@0.86.3 expo-crypto@57.0.3 expo-file-system@57.0.7 expo-sqlite@57.0.3
```

Rebuild the native app after installing this package. **Expo Go does not contain
the included `MeshlineRelaySocket` module.** A missing module produces an error;
the adapter does not fall back to React Native's default WebSocket.

## Android compatibility

Apply the shipped compatibility fixes after installing dependencies and before
building Android:

```sh
npx meshline-expo-android-compat . --apply
npx meshline-expo-android-compat . --check
npx expo run:android
```

The fixes address shared-object registry access and HTTP startup in the pinned
Expo dependencies. Installation does not apply them automatically.
The command checks exact versions and original/already-fixed source hashes,
is idempotent, and refuses unknown or locally modified sources.

Repeat after a clean dependency installation and rebuild the native app.
Reloading JavaScript cannot apply native fixes. Do not bypass version or source
mismatch errors. The patches affect Android only.

## Configure a session

Import `ExpoSqliteStore`, `expoRandom`, `expoRelayFetch`, and
`createExpoSocketFactory` from `@meshline/expo`.

- Construct `ExpoSqliteStore({ databaseName })`, migrate explicitly, then pass it
  to the client before initialization. The name is a basename; optional
  `directory` accepts an absolute app-private path or local file URI.
- Pass `fetch: expoRelayFetch`, `socketFactory: createExpoSocketFactory()`, and
  `random: expoRandom` to `RelayClientPool`.
- Pass `random: expoRandom` to `MeshlineClient` along with the store, pool, and
  application integrations.
- Supply a persistent, purpose-bound `SecretProtector`; manage protection keys
  through your application's Keychain/Keystore integration.

The [Expo session example](https://github.com/meshline-network/sdk/blob/main/typescript/examples/expo.ts)
shows construction and cleanup. If implementing an account signer using
`signAccount`, pass `expoRandom` as its third argument too. A hardware-backed
signer owns its own randomness.

## Runtime behavior

SQLite writers serialize transactions and readers retain independent fixed WAL
snapshots. Dispose query readers promptly. Cancellation before commit rolls back
the transaction; a canceled read does not consume a page. Retain the original
database and protector across restart. Secret protection does not encrypt the
whole database or stored message text.

`expoRelayFetch` uses `expo/fetch`, omits cookies, rejects redirects, and consumes
response streams so cancellation and partial failures settle reads.
It does not use React Native's legacy global fetch.

The native WebSocket module uses dedicated Android OkHttp and iOS ephemeral
URLSession clients without cookie storage, credential storage, or caching.
It rejects redirects and retains platform TLS trust checks.

Use Bundler resolution, the `react-native` condition, and DOM request/stream types
for TypeScript. The
[native example configuration](https://github.com/meshline-network/sdk/blob/main/typescript/examples/tsconfig.expo.json)
uses `skipLibCheck` for overlapping Expo/React Native dependency declarations.

Android execution has been exercised on Android 11/API 30 with Hermes.
iOS native builds and runtime behavior remain unverified. Background execution
depends on OS/OEM policy; persistent recovery does not guarantee background delivery.

[Application guide](https://github.com/meshline-network/sdk/blob/main/typescript/docs/README.md)
· [Platform requirements and limits](https://github.com/meshline-network/sdk/blob/main/typescript/docs/platforms.md)
