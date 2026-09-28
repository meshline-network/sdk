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

Source: [Integration.cs](../../samples/Meshline.Sdk.Examples/Integration.cs). Validation checks the identifiers; it does not prove that a network or relay is reachable. The built-in account adapter supports the Neo account namespace. An arbitrary CAIP identifier does not imply support for that chain's signing rules.

## Implement the boundaries

| Interface | Required behavior |
| --- | --- |
| `IAccountSigner` | Return the account identifier and matching public key. Sign the exact supplied bytes using the account namespace's signing rules. Keep the private key in the wallet or application's key service. |
| `IRelayRegistry` | Expose `Context`, look up relay registrations by canonical ID, and enumerate registrations. Preserve registry status, including inactive entries; the SDK evaluates usability. Return `null` for an unknown relay. |
| `ISecretProtector` | Protect and restore secret bytes, enforcing the supplied purpose string. Preserve the ability to decrypt previously stored values after an application restart. |
| `IDeviceSigner` | Supply device signing and key agreement for lower-level transport use. `DeviceManager` implements this interface for normal client applications. |

An account signer is required for establishment, recovery, device authorization, and route publication. An existing authorized device can perform device-authorized operations without retaining an account signer in its client session. This allows an application to request wallet access for account changes instead of every message.

Secret protection applies to selected local keys and secrets. It does **not** encrypt the entire SQLite database, stored message bodies, or arbitrary application files. Apply platform storage protections appropriate to your application and preserve the database together with the means to unprotect its secrets. Do not copy the offline tests' secret protector into a production integration.

## Ownership and failures

The SDK does not dispose the supplied signer, registry, or protector. Keep them usable until all dependent clients and pools are disposed. Honor cancellation in asynchronous implementations, and let wallet rejection, registry failure, and decryption failure reach the caller. Returning fake signatures, empty registry data on error, or newly generated protection keys on every launch prevents correct recovery and diagnosis.

Use the [Registry protocol](https://meshline.org/protocol/v1/en/registry/index.html) and [reference contracts](https://github.com/meshline-network/contracts) to implement network discovery. The SDK interface abstracts registry access; it does not supply a blockchain RPC client.

## API reference

[Interactions](../api/Meshline.Interactions.md) · [NetworkContext](../api/Meshline.Models.NetworkContext.md) · [RelayEntry](../api/Meshline.Models.Registry.RelayEntry.md) · [AccountAdapter](../api/Meshline.Identity.AccountAdapter.md)
