# Shared SDK tests

This directory belongs to all SDK implementations. Language-specific runners stay
with their SDK; live interoperability is a separate suite requiring both runtimes.

| Directory | Responsibility |
| --- | --- |
| [vectors](vectors/) | Fixed test vectors: a pinned protocol snapshot plus SDK-owned integration and backup fixtures. |
| [scenarios](scenarios/README.md) | Language-neutral SDK behavior cases with stable IDs, operations and independent expected results. |
| [interop](interop/README.md) | Tests that exchange signed or encrypted data between actual SDK implementations. |

The protocol repository remains authoritative for wire-format vectors. Keep a
pinned copy here so a checkout of this SDK repository is sufficient after restore;
do not load a moving sibling protocol checkout or regenerate expected answers with
the implementation being tested. Both language test suites verify the vector
[manifest](vectors/manifest.json), which records the source revision and SHA-256
hashes for eight protocol vector files. Platform acceptance also consumes this same snapshot.

Both SDKs execute the certificate, certificate-tampering and gateway-URL sections
of `push-notifications-v1.json`, and the envelope notification cases in
`message-encryption-v1.json` and `groups-v1.json`. These cover field representation,
fixed signatures, tampering, decryption and canonical retry identity. Component
tests cover gateway creation, updates/removal, renewal and publication recovery,
automatic notification intent by message type, and preservation of signed requests
across restart. Interoperability tests exchange certificates and envelopes between
the actual .NET and TypeScript implementations.

Push eligibility, HTTP callbacks, gateway state, actual delivery and push
deduplication belong to Relay/gateway tests. The SDK's serialization and replay
checks do not exercise those delivery decisions.

SDK-owned fixtures are separate from the pinned protocol manifest. Their expected
values come from independent implementations; each file records its source and
parameters.

| Fixture | Coverage and independent source | Consumers |
| --- | --- | --- |
| [nep6-wallets.json](vectors/nep6-wallets.json) | Wallet accounts, scrypt profiles and Unicode passwords; official Neo wallet implementation. | C# and TypeScript integrations |
| [backup-v1.json](vectors/backup-v1.json) | Passwords, [container records](../docs/backup-format-v1.md), selections, group projections and epoch keys; Python, Tink and pinned protocol vectors. | C# backups |
| [tink-streaming-aead.json](vectors/tink-streaming-aead.json) | Empty input, Unicode/binary input and segment boundaries; official Tink. | C# backups |

Tests consume checked-in fixtures without regenerating expected values. All fixture
passwords and private keys are public test values; never fund these accounts or use
the keys to protect real data.

Native adapters load the common cases and call their own SDK, without consulting
another implementation for expected answers. They must execute every supported
case and fail on an unsupported suite version or operation. Case IDs appear in
runner output. Keep storage-engine, transport and platform-specific tests in their
language directories; shared cases describe observable behavior, not SDK internals.

## Run common conformance tests

From `sdk/dotnet`, after restoring dependencies:

```sh
dotnet build Meshline.Sdk.slnx -c Release --no-restore
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --filter-namespace Meshline.Tests.Conformance --minimum-expected-tests 1 --fail-skips on
```

The ordinary .NET test run also executes all shared scenarios and the existing
protocol vector adapters across protocol, transport and component tests.

From `sdk/typescript`, after `npm ci`:

```sh
npm run build
npm run test:conformance
```

This command needs Node.js only. The complete TypeScript test suite also contains
HTTPS/WSS fixtures which currently use the shared .NET host to generate temporary
TLS certificates; follow the [TypeScript development setup](../typescript/README.md)
for that suite. Cross-language tests have a [separate command](interop/README.md).
