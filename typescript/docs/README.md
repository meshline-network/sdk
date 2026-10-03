# Meshline TypeScript SDK guide

Use `MeshlineClient` to manage accounts, devices, profiles, contacts, messages,
channels, and groups. The SDK stores local state and coordinates relay access;
your application provides identity integrations, content hosting, and the UI.

## Start here

1. [Install the packages and open a session](guides/quick-start.md).
2. [Supply the application integrations](guides/integrations.md).
3. [Understand lifecycle and resource ownership](guides/lifecycle.md).
4. [Send a direct message](guides/direct-messages.md).

Check [platform requirements and limits](platforms.md) for your runtime.

## Guides

| Guide | What you will build or learn |
| --- | --- |
| [Quick start](guides/quick-start.md) | Install the packages, open storage, and start a client. |
| [Application integrations](guides/integrations.md) | Configure a network, account signer, relay registry, and secret protector. |
| [Lifecycle and ownership](guides/lifecycle.md) | Initialize, start, stop, and dispose the client and its dependencies. |
| [Accounts and devices](guides/accounts-and-devices.md) | Establish an account, resolve routes, and manage device authorization. |
| [Recovery and home-relay migration](guides/recovery-and-migration.md) | Restore access deliberately and move authorization to another relay. |
| [Profiles and contacts](guides/profiles-and-contacts.md) | Edit profiles, exchange requests, and manage contacts. |
| [Direct messages and the outbox](guides/direct-messages.md) | Send encrypted content and interpret delivery state. |
| [Conversations and unread state](guides/conversations.md) | Build a conversation list and track local read positions. |
| [Channels](guides/channels.md) | Publish posts, follow channels, and load retained history. |
| [Groups](guides/groups.md) | Admit members, exchange encrypted messages, rotate secrets, and recover keys. |
| [Storage and pagination](guides/storage-and-pagination.md) | Preserve device state and use local snapshots or relay cursors. |
| [Transport](guides/transport.md) | Select HTTP and WebSocket adapters and interpret authentication state. |
| [Events and troubleshooting](guides/events-and-troubleshooting.md) | Subscribe to updates, handle cancellation, and diagnose failures. |

The [examples](../examples/README.md) are functions to incorporate into an
application. Select the optional NEP-6 signer and Neo RPC registry or supply your
own implementations, then provide a secret protector and trusted network
configuration. The examples do not include a funded wallet or a deployed relay service.

[SDK overview and installation](../README.md)
