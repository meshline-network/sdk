# Application integrations

[Guide index](../README.md) · [Quick start](quick-start.md)

## Bind a session to its network and account

Every client and component uses `ClientOptions`. `Context` identifies the Neo chain reference and Registry script hash; `AccountId` is the account's CAIP-10 identifier. Take both from the actual integrations rather than hard-coding unrelated example values. The registry and options must describe the same network, and the signer must match the account.

<!-- snippet: options -->
```csharp
public static ClientOptions CreateOptions(IRelayRegistry registry, IAccountSigner signer)
{
    var options = new ClientOptions { Context = registry.Context, AccountId = signer.AccountId };
    options.Validate();
    return options;
}
```
<!-- /snippet -->

Source: [Integration.cs](../../examples/Meshline.Sdk.Examples/Integration.cs). Validation checks the identifiers; it does not prove that a network or relay is reachable. The built-in account adapter supports the Neo account namespace. An arbitrary CAIP identifier does not imply support for that chain's signing rules.

## Choose the boundary implementations

| Interface | Required behavior |
| --- | --- |
| `IAccountSigner` | Return the account identifier and matching public key. Sign the exact supplied bytes using the account namespace's signing rules. Keep the private key in the wallet or application's key service. |
| `IRelayRegistry` | Expose `Context`, look up relay registrations by canonical ID, and enumerate registrations. Preserve registry status, including inactive entries; the SDK evaluates usability. Return `null` for an unknown relay. |
| `ISecretProtector` | Protect and restore secret bytes, enforcing the supplied purpose string. Preserve the ability to decrypt previously stored values after an application restart. |
| `IDeviceSigner` | Supply a device certificate and signatures for lower-level transport use. `DeviceManager` implements this interface for normal client applications. |

An account signer is required for establishment, recovery, device authorization, and route publication. An existing authorized device can perform device-authorized operations without retaining an account signer in its client session. This allows an application to request wallet access for account changes instead of every message.

Secret protection applies to selected local keys and secrets. It does **not** encrypt the entire SQLite database, stored message bodies, or arbitrary application files. Apply platform storage protections appropriate to your application and preserve the database together with the means to unprotect its secrets. Do not copy the offline tests' secret protector into a production integration.

## Optional Neo implementations

`Meshline.Interactions` supplies `RpcRelayRegistry` and `Nep6AccountSigner`. Neither is registered automatically. Construct them directly or register them in your application's DI container; custom implementations remain supported.

`RpcRelayRegistry` takes an application-owned `HttpClient` and explicit options. It verifies the RPC network magic before invoking the configured contract, preserves inactive entries, and releases iterator sessions on completion, cancellation, or early exit. Configure the RPC node to support iterator sessions; inline results are accepted only when explicitly complete. Defaults are 100 entries per page and a 15-second deadline per request. Query errors reach the caller; session cleanup failures are reported through `System.Diagnostics.Trace` without hiding the original failure. The application controls HTTP handlers, headers, and proxy settings.

<!-- snippet: neo-registry -->
```csharp
public static void RegisterRpcRegistry(
    IServiceCollection services, HttpClient rpcHttp, NetworkContext context, Uri rpcUrl)
{
    // This is application registration code; the SDK never adds this service automatically.
    services.AddSingleton<IRelayRegistry>(_ => new RpcRelayRegistry(rpcHttp,
        new RpcRelayRegistryOptions { Context = context, RpcUrl = rpcUrl }));
}
```
<!-- /snippet -->

`Nep6AccountSigner` reads a Neo N3 NEP-6 version 1.0 wallet and decrypts its selected NEP-2 key. It defaults to account index 0 in file order, ignoring `isDefault`; pass another index explicitly when needed. It rejects watch-only, deployed, multisignature, mismatched-address, and mismatched-contract accounts without trying a later account. `Parse` accepts JSON already loaded by the application. The wallet is never modified. Supply the password at runtime, keep the signer available while it is in use, and dispose it afterward.

<!-- snippet: nep6-signer -->
```csharp
public static Nep6AccountSigner OpenAccount(
    string walletPath, string walletPassword, NetworkContext context, int accountIndex = 0)
{
    // The application chooses the path/password and disposes the returned signer.
    return Nep6AccountSigner.Load(walletPath, walletPassword, context, accountIndex);
}
```
<!-- /snippet -->

Source: [NeoIntegrations.cs](../../examples/Meshline.Sdk.Examples/NeoIntegrations.cs). Wallet loading honors the file's scrypt parameters and NEP-2 NFC password normalization. Imported scrypt costs are limited to 256 MiB estimated memory and `n * r * p <= 16777216`; standard NEP-6 parameters fit these limits. Both SDKs use the same limits. This software signer holds decrypted key material in memory; use your own `IAccountSigner` for external or hardware wallets.

## Ownership and failures

The SDK does not dispose the supplied signer, registry, or protector. Keep them usable until all dependent clients and pools are disposed. Honor cancellation in asynchronous implementations, and let wallet rejection, registry failure, and decryption failure reach the caller. Returning fake signatures, empty registry data on error, or newly generated protection keys on every launch prevents correct recovery and diagnosis.

Use the [Registry protocol](https://meshline.org/protocol/v1/en/registry/index.html) and [reference contracts](https://github.com/meshline-network/contracts) when implementing another registry adapter. The supplied RPC adapter is read-only; it does not register relays or submit transactions.

## API reference

[Interactions](../api/Meshline.Interactions.md) · [NetworkContext](../api/Meshline.Models.NetworkContext.md) · [RelayEntry](../api/Meshline.Models.Registry.RelayEntry.md) · [AccountAdapter](../api/Meshline.Identity.AccountAdapter.md)
