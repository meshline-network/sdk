# Quick start

Choose the packages for your runtime in the [SDK overview](../../README.md).
These commands install SDK 0.1.0-alpha.1 from npm.

## Install in your application

Install the core and the adapters for your runtime together.

Node.js:

```sh
npm install @meshline/sdk@0.1.0-alpha.1 @meshline/storage-node@0.1.0-alpha.1 @meshline/transport-node@0.1.0-alpha.1
```

Browser:

```sh
npm install @meshline/sdk@0.1.0-alpha.1 @meshline/storage-browser@0.1.0-alpha.1
```

Native Expo:

```sh
npm install @meshline/sdk@0.1.0-alpha.1 @meshline/expo@0.1.0-alpha.1
```

Expo also needs matching native dependencies, the Android compatibility command,
and a rebuilt native application. Follow [Expo setup](../platforms.md#expo).

## Open a session

Supply [ApplicationDependencies](../../examples/shared.ts): a trusted
`NetworkContext`, `accountId`, `RelayRegistry`, persistent `SecretProtector`,
and an `AccountSigner` for operations requiring account authority. Read
[application integrations](integrations.md) before implementing these interfaces.

Use the helper for your runtime:

| Runtime | Function | Source |
| --- | --- | --- |
| Node.js | `migrateAndOpenNodeClient(path, application)` | [node.ts](../../examples/node.ts) |
| Browser | `migrateAndOpenBrowserClient(databaseName, application)` | [browser.ts](../../examples/browser.ts) |
| Expo | `migrateAndOpenExpoClient(databaseName, application)` | [expo.ts](../../examples/expo.ts) |

Each helper opens one database, migrates it, and initializes `MeshlineClient`.
It returns `{ client, dispose }`. Use a distinct database for each network,
account, and local device; retain the same database and protector on restart.

## Establish or resume

For initial account establishment, use this function from
[workflows.ts](../../examples/workflows.ts) on the initialized client.
Types in these excerpts are imported from `@meshline/sdk`; the source
file includes the imports.

```ts
export async function establishAndStart(client: MeshlineClient, relayId: string) {
    await client.establishAccount({ relayId });
    await client.start();
}
```

Omit `relayId` from the establishment options if the SDK should select a verified
active relay. On an ordinary restart, call `client.start()` after initialization.
An existing account route with no authorized local device requires deliberate
[recovery](recovery-and-migration.md), not a second automatic establishment.

Starting activates synchronization, outgoing delivery, subscriptions, and polling.
Use the managers on `client` for application actions. Start with
[profiles and contacts](profiles-and-contacts.md), then [direct messages](direct-messages.md).

## Close the session

Use `try`/`finally` around the application's session lifetime and call
`await session.dispose()` in the `finally` block. The example session disposes
the client, pool, and store in order and attempts all three even if cleanup fails.
Track and finish application-owned event work separately.

See [lifecycle and ownership](lifecycle.md) for suspension and resource management.

[All guides](../README.md)
