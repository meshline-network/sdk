# Meshline .NET SDK developer guide

**.NET 10 · NuGet package `Meshline.Sdk` · Namespace `Meshline`**

Build applications with self-sovereign accounts, encrypted direct and group messages, public channels, and local SQLite persistence. Start with `MeshlineClient`, which coordinates the SDK components for one network, account, and device. The application supplies account signing, relay registry access, and local secret protection.

These guides describe the **current source checkout**. For an installed package,
use the documentation at its matching release tag; an API described on `main` may
not yet be available in that package.

## First integration

| Guide | Task |
| --- | --- |
| [Quick start](guides/quick-start.md) | Install the package, migrate storage, establish an account, and run a session. |
| [Application integrations](guides/integrations.md) | Bind the network and account; implement signing, registry access, and secret protection. |
| [Lifecycle and ownership](guides/lifecycle.md) | Initialize, start, stop, and dispose clients and independently composed components. |
| [Accounts and devices](guides/accounts-and-devices.md) | Resolve routes, authorize devices, renew certificates, and publish device state. |

## Everyday application work

| Guide | Task |
| --- | --- |
| [Profiles and contacts](guides/profiles-and-contacts.md) | Update profile fields, exchange contact requests, and manage contact authorization. |
| [Direct messages and the outbox](guides/direct-messages.md) | Send encrypted content, observe progress, and understand retries and cancellation. |
| [Conversations and unread state](guides/conversations.md) | Build a unified conversation list and track local read positions. |
| [Synchronization](guides/synchronization.md) | Await a fresh pass, observe progress, and interpret retention gaps. |
| [Storage and pagination](guides/storage-and-pagination.md) | Preserve local state and distinguish snapshot readers from relay cursors. |
| [Channels](guides/channels.md) | Publish public posts, follow channels, and load retained history. |
| [Groups](guides/groups.md) | Create encrypted groups, admit members, manage roles, and recover group keys. |

## Advanced integration and diagnosis

| Guide | Task |
| --- | --- |
| [Recovery and home-relay migration](guides/recovery-and-migration.md) | Restore account access deliberately and move authorization to another relay. |
| [Transport and dependency injection](guides/transport-and-di.md) | Configure HTTP, use relay sessions, and integrate with Microsoft DI. |
| [Events and errors](guides/events-and-errors.md) | Subscribe to changes and distinguish deadlines, cancellation, and operation failures. |
| [Troubleshooting](guides/troubleshooting.md) | Diagnose symptoms and resume safely from retained state. |

## Reference, examples, and contributions

The [API reference](api/README.md) is generated from the assembly and XML comments.
Inherited members are documented on their declaring base type; follow inheritance
links. Protected extension points are included, and EF migration types are
infrastructure rather than application entry points.

Guide snippets are synchronized from [compilable examples](../examples/README.md).
They are integration methods with application-supplied dependencies, not a wallet
or relay service. Compilation verifies API usage; [offline tests](../TESTING.md)
verify behavior against scripted peers. Neither establishes live-network or full
platform support. See [validation limits](guides/troubleshooting.md#validation-and-recovery-limits).

For source builds, documentation updates, and publishing, use [Maintenance](../../MAINTENANCE.md).
The [Protocol 1.0 draft](https://meshline.org/protocol/v1/en/index.html) defines wire
formats and interoperability rules; these guides describe the .NET implementation.
Content hosting, retrieval, and UI rendering remain application responsibilities.

[SDK overview](../README.md) · [TypeScript guide](../../typescript/docs/README.md)
