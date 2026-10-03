# Node.js relay transport

Cookie-free HTTPS and WebSocket adapters for Meshline on **Node.js 24 or later**.
This ESM package includes TypeScript declarations.

## Install and configure

Install this adapter and the matching core together:

```sh
npm install @meshline/sdk@0.1.0-alpha.1 @meshline/transport-node@0.1.0-alpha.1
```

```ts
import { createNodeRelayFetch, createNodeSocketFactory } from '@meshline/transport-node';

const transport = {
    fetch: createNodeRelayFetch(),
    socketFactory: createNodeSocketFactory(),
};
```

Spread these transport fields into `RelayClientPool` options alongside the
network context and account ID. Pass the application registry as the pool's
second argument. See the
[Node session example](https://github.com/meshline-network/sdk/blob/main/typescript/examples/node.ts)
for SQLite integration and resource ownership.

## Transport behavior

The adapters disable redirects, cookies, and implicit retries. HTTPS and WSS
retain normal TLS certificate and hostname verification. The optional `ca`
configuration trusts an explicit private network root only for this transport;
it does not change process-wide trust settings.

The WebSocket adapter uses `ws`, enforces the protocol's 1 MiB frame limit,
rejects invalid UTF-8, and can send RFC 1003/1009 rejection close codes.
Managers handle durable retries separately from the transport.

[Transport guide](https://github.com/meshline-network/sdk/blob/main/typescript/docs/guides/transport.md)
· [Platform requirements](https://github.com/meshline-network/sdk/blob/main/typescript/docs/platforms.md)
