# TypeScript application examples

These functions show how to integrate the SDK into an application. They accept
your network, account signer, relay registry, and secret protector; see
[application integrations](../docs/guides/integrations.md).

| Source | Purpose |
| --- | --- |
| [shared.ts](shared.ts) | Application dependencies and cleanup that attempts every owned resource. |
| [node.ts](node.ts) | Open SQLite and configure Node HTTPS/WSS transport. |
| [browser.ts](browser.ts) | Open IndexedDB and configure browser transport. |
| [expo.ts](expo.ts) | Open native SQLite and configure native randomness, HTTP, and WebSocket. |
| [workflows.ts](workflows.ts) | Establish or resume a client; use profiles, contacts, messages, conversations, channels, groups, and events. |

The platform helpers migrate storage and initialize the client. Establish or
recover account authorization only when needed, then start it. Dispose the
returned session when finished. Business functions represent individual actions:
wait for incoming contact requests or verified group membership before invoking
the next participant's action.

All examples are type-checked, including business calls in both the normal and
Expo TypeScript configurations. They require application integrations and relay
access to run. See the [quick start](../docs/guides/quick-start.md) and
[platform requirements](../docs/platforms.md).
