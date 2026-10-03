# Platform requirements and limits

All packages are ESM with TypeScript declarations. Use the core plus the storage
and transport adapters for the target runtime; do not bundle Node adapters into a
browser or native application.

The alpha has been exercised on Windows with Node.js and on Android 11/API 30
with Hermes. Linux execution and iOS native builds/runtime behavior remain
unverified. Browser behavior also depends on the engine and origin policy.
These boundaries do not guarantee every OS version, OEM background policy, or
deployed relay integration.

## Node.js

Use Node.js 24 or later with `@meshline/storage-node` and
`@meshline/transport-node`.

The SQLite adapter uses synchronous `node:sqlite`. Put large workloads in an
application-owned worker if event-loop latency matters. Create the database's
parent directory, migrate explicitly, and preserve the database and secret
protector across restarts.

Node HTTP and WebSocket adapters disable cookies, redirects, and implicit
retries while retaining normal TLS certificate and hostname validation.
See the [Node example](../examples/node.ts).

## Browser

Use `@meshline/storage-browser` on a secure origin with IndexedDB,
Web Locks, and cryptographic randomness. Browser quotas, eviction, user clearing,
and private-mode restrictions apply. An application may request persistent
browser storage; the adapter reports failures rather than falling back to memory.

The core HTTP transport requests credential omission and rejects redirects.
**Windows Playwright WebKit has a known failure to omit HTTP cookies**, including
with a direct native fetch call. Storage working in that engine does not establish
cookie-free transport. This observation does not establish behavior in actual
Safari on macOS or iOS; validate the runtime you deploy.

Browser WebSocket APIs control their own handshake cookie policy. Applications
cannot force credential omission, so use a relay origin without cookies.
Browser APIs also restrict application close codes: the adapter maps requested
1003/1009 to 3003/3009 and retains the requested/sent codes in diagnostics.
The Node and native adapters can send the RFC close codes directly.

For Expo's web build, use the browser store and browser transport instead of
the native Expo adapter. See the [browser example](../examples/browser.ts).

## Expo

The native adapter requires an Expo Modules application with:

| Dependency | Requirement |
| --- | --- |
| `expo` | 57.0.25 |
| `expo-modules-core` | 57.0.19 |
| `react-native` | >=0.86.3 and <0.87.0 |
| `expo-crypto` | ^57.0.3 |
| `expo-file-system` | ^57.0.7 |
| `expo-sqlite` | ^57.0.3 |

Install the matching native dependencies in an Expo 57 app. This command uses
the pinned dependency versions used by the adapter:

```sh
npx expo install expo@57.0.25 expo-modules-core@57.0.19 react-native@0.86.3 expo-crypto@57.0.3 expo-file-system@57.0.7 expo-sqlite@57.0.3
```

Install the core and Expo packages as described in the
[quick start](guides/quick-start.md). Rebuild the native app after installation.
**Expo Go does not contain `MeshlineRelaySocket`**. The adapter reports a missing
module rather than falling back to the default React Native WebSocket.

### Android compatibility

Before building Android, apply the compatibility fixes shipped with
`@meshline/expo`:

```sh
npx meshline-expo-android-compat . --apply
npx meshline-expo-android-compat . --check
npx expo run:android
```

The fixes address concurrent shared-object access and HTTP request startup in
the pinned Expo dependencies. They are required for reliable SQLite and HTTP
operation with these versions. Installation does not modify dependencies automatically.

The command checks exact dependency versions and original or already-fixed source
hashes, is idempotent, and refuses unknown or locally modified sources. Reapply
after a clean dependency installation. Changing either pinned version requires
compatible fixes; do not bypass a mismatch check.

Rebuild the native application after applying the fixes. Reloading JavaScript
cannot change native code. The patches affect Android only.

### Native configuration

Pass `expoRandom` to both `MeshlineClient` and `RelayClientPool`.
It uses native cryptographic randomness with no weak development fallback.
A custom signer using `signAccount` must also pass `expoRandom` as its third
argument; hardware signers manage their own randomness.

Use `expoRelayFetch`, which uses `expo/fetch`, omits cookies, rejects redirects,
and handles response-stream cancellation. Use `createExpoSocketFactory()` for
the included module. Android uses a dedicated OkHttp client; the iOS implementation
uses an ephemeral URLSession. Both disable cookie/credential storage and caches,
reject redirects, and retain normal TLS trust checks.

`ExpoSqliteStore` takes a persistent `databaseName` basename and an optional
app-private `directory` as an absolute path or local file URI. Preserve the store
and persistent protector across launches. See the [Expo example](../examples/expo.ts).

For TypeScript, use Bundler module resolution, the `react-native` condition, and
DOM types for Expo fetch/streams. Expo and React Native dependency declarations
overlap in global types; the native example uses `skipLibCheck` for those
declarations while keeping strict application checks. See its
[configuration](../examples/tsconfig.expo.json).

### Suspension and recovery

Stop the client when intentionally suspending SDK work and start it on resume.
After process termination, reopen the same store and protector and initialize
before starting. Do not assume the OS gives the application time to clean up.

Persistent recovery does not guarantee background delivery while JavaScript is
suspended. Natural suspension, OEM battery policies, and production relay behavior
must be evaluated in the application. iOS implementation and JavaScript bundling
alone do not establish iOS native/runtime support.

[All guides](README.md)
