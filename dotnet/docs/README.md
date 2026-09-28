# Meshline .NET SDK developer guide

**SDK 1.0.0 · .NET 10 · NuGet package `Meshline.Sdk` · Namespace `Meshline`**

Build applications with self-sovereign accounts, encrypted direct and group messages, public channels, and local SQLite persistence. Start with `MeshlineClient`, which coordinates the SDK components for one network, account, and device. The application supplies account signing, relay registry access, and local secret protection.

## Start here

1. [Install the SDK and open your first session](guides/quick-start.md).
2. [Implement the application integrations](guides/integrations.md).
3. [Understand initialization, startup, shutdown, and ownership](guides/lifecycle.md).
4. [Establish contacts and send a direct message](guides/direct-messages.md).

The [API reference](api/README.md) documents public types and members, including protected extension points on inheritable types. It is generated from the SDK assembly and XML comments. Inherited members are documented on their declaring base type; follow each type's inheritance links. Compiler-generated implementation details are excluded. EF Core migration types are included as infrastructure, not as application entry points.

## Guides

| Guide | What you will build or learn |
| --- | --- |
| [Quick start](guides/quick-start.md) | Install the package, migrate storage, establish an account, and run a session. |
| [Application integrations](guides/integrations.md) | Bind the network and account; implement signing, registry access, and secret protection. |
| [Lifecycle and ownership](guides/lifecycle.md) | Initialize, start, stop, and dispose clients and independently composed components. |
| [Accounts and devices](guides/accounts-and-devices.md) | Resolve routes, authorize devices, renew certificates, and publish device state. |
| [Recovery and home-relay migration](guides/recovery-and-migration.md) | Restore account access deliberately and move authorization to another relay. |
| [Profiles and contacts](guides/profiles-and-contacts.md) | Update profile fields, exchange contact requests, and manage contact authorization. |
| [Direct messages and the outbox](guides/direct-messages.md) | Send encrypted content, observe progress, and understand retries and cancellation. |
| [Conversations and unread state](guides/conversations.md) | Build a unified conversation list and track local read positions. |
| [Channels](guides/channels.md) | Publish public posts, follow channels, and load retained history. |
| [Groups](guides/groups.md) | Create encrypted groups, admit members, manage roles, and recover group keys. |
| [Storage and pagination](guides/storage-and-pagination.md) | Preserve local state and distinguish snapshot readers from relay cursors. |
| [Transport and dependency injection](guides/transport-and-di.md) | Configure HTTP, use relay sessions, and integrate with Microsoft DI. |
| [Events and troubleshooting](guides/events-and-troubleshooting.md) | React to updates, diagnose background failures, and recover without losing state. |

## Working with the examples

Code blocks marked as snippets are synchronized from the [compilable examples](../samples/README.md). They are methods to incorporate into an application, with dependencies supplied as arguments. Use the linked source files for their namespace imports. They are not a ready-to-run wallet, registry client, or Relay server.

There are three distinct validation levels:

- **Example compilation:** verifies that the documented calls match this checkout's public API.
- **Offline behavioral tests:** exercise protocol vectors, transport, persistence, and component workflows against scripted peers. See the [test guide](../TESTING.md).
- **Live network validation:** requires your chosen network, eligible relays, wallet integration, and platform-specific storage. Compilation and offline tests do not establish live-relay interoperability or mobile, browser, and NativeAOT support.

The SDK release number does not change the [Protocol 1.0 specification's draft status](https://meshline.org/protocol/v1/en/index.html). Wire formats and interoperability rules belong to the specification; these guides explain the .NET implementation. Content hosting, attachment retrieval, and UI rendering remain application responsibilities. TypeScript is planned and has no API documentation here.

[SDK overview](../README.md) · [API reference](api/README.md) · [Maintenance](../../MAINTENANCE.md)
