# Compilable integration examples

These examples support the [developer guide](../docs/README.md). They compile against this checkout's SDK and demonstrate real public API calls. They are a class library, not an executable application or an offline network simulation.

| Source | Examples |
| --- | --- |
| [Sessions.cs](Meshline.Sdk.Examples/Sessions.cs) | New accounts, existing-device startup, recovery, migration, and device renewal. |
| [Synchronization.cs](Meshline.Sdk.Examples/Synchronization.cs) | Foreground account and group refresh with a caller deadline. |
| [Messaging.cs](Meshline.Sdk.Examples/Messaging.cs) | Profiles, contacts, direct messages, outbox, conversations, and local history. |
| [Spaces.cs](Meshline.Sdk.Examples/Spaces.cs) | Channels, history cursors, groups, admission, and key recovery. |
| [Integration.cs](Meshline.Sdk.Examples/Integration.cs) | Account/network binding, event handlers, HTTP configuration, and DI. |

Copy methods into your application with the imports from their source file. The examples live inside the `Meshline.Examples` namespace; another application namespace also needs `using Meshline;`. Select `IAccountSigner` and `IRelayRegistry` implementations (the optional Neo implementations are shown in [NeoIntegrations.cs](Meshline.Sdk.Examples/NeoIntegrations.cs)), and supply `ISecretProtector`, a database path, and an application lifetime callback. Values such as account IDs, relay IDs, and invitation expiries come from your application and network. Methods performing writes are separate actions to call deliberately, not a script to run sequentially without user intent.

Named `#region` blocks are the source of corresponding documentation code blocks. Edit the C# first, then use the [documentation commands](../../MAINTENANCE.md#documentation) to synchronize and verify. Compilation checks API compatibility; the [offline tests](../TESTING.md) check behavior. Neither substitutes for live testing with your integrations.
