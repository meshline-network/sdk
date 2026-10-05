# Meshline TypeScript SDK guide

Use `MeshlineClient` to manage accounts, devices, profiles, contacts, messages,
channels, and groups. The SDK stores local state and coordinates relay access;
your application provides identity integrations, content hosting, and the UI.

These guides describe the **current source checkout**. For an installed package,
use the documentation at its matching release tag; an API described on `main` may
not yet be available in that package.

Check [platform requirements and limits](platforms.md) before selecting a runtime.

## First integration

| Guide | Task |
| --- | --- |
| [Quick start](guides/quick-start.md) | Install the packages, open storage, and start a client. |
| [Application integrations](guides/integrations.md) | Configure a network, account signer, relay registry, and secret protector. |
| [Lifecycle and ownership](guides/lifecycle.md) | Initialize, start, stop, and dispose the client and its dependencies. |
| [Accounts and devices](guides/accounts-and-devices.md) | Establish an account, resolve routes, and manage device authorization. |

## Everyday application work

| Guide | Task |
| --- | --- |
| [Profiles and contacts](guides/profiles-and-contacts.md) | Edit profiles, exchange requests, and manage contacts. |
| [Direct messages and the outbox](guides/direct-messages.md) | Send encrypted content and interpret delivery state. |
| [Conversations and unread state](guides/conversations.md) | Build a conversation list and track local read positions. |
| [Synchronization](guides/synchronization.md) | Await a fresh pass, observe progress, and interpret retention gaps. |
| [Storage and pagination](guides/storage-and-pagination.md) | Preserve device state and use local snapshots or relay cursors. |
| [Channels](guides/channels.md) | Publish posts, follow channels, and load retained history. |
| [Groups](guides/groups.md) | Admit members, exchange encrypted messages, rotate secrets, and recover keys. |

## Advanced integration and diagnosis

| Guide | Task |
| --- | --- |
| [Recovery and home-relay migration](guides/recovery-and-migration.md) | Restore access deliberately and move authorization to another relay. |
| [Transport](guides/transport.md) | Select HTTP and WebSocket adapters and interpret authentication state. |
| [Events and errors](guides/events-and-errors.md) | Subscribe to changes and distinguish deadlines, cancellation, and operation failures. |
| [Troubleshooting](guides/troubleshooting.md) | Diagnose symptoms and resume safely from retained state. |

## Examples and contributions

The [type-checked examples](../examples/README.md) accept your signer, registry,
secret protector, and trusted network configuration. They do not supply a funded
wallet or deployed relay service. The optional NEP-6 signer and Neo RPC registry
are integrations you select explicitly.

Use [Testing](../TESTING.md) for behavioral coverage and validation scope, and
[Maintenance](../../MAINTENANCE.md) for builds, documentation checks, and publishing.
Compiled examples and offline tests do not establish every live deployment.

[SDK overview and installation](../README.md) · [.NET guide](../../dotnet/docs/README.md)
