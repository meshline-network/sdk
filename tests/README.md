# Shared SDK tests

This directory belongs to all SDK implementations. Language-specific runners stay
with their SDK; live interoperability is a separate suite requiring both runtimes.

| Directory | Responsibility |
| --- | --- |
| [vectors](vectors/manifest.json) | One immutable snapshot of seven protocol vector files, with source revision and SHA-256 hashes. |
| [scenarios](scenarios/README.md) | Language-neutral SDK behavior cases with stable IDs, operations and independent expected results. |
| [interop](interop/README.md) | Tests that exchange signed or encrypted data between actual SDK implementations. |

The protocol repository remains authoritative for wire-format vectors. Keep a
pinned copy here so a checkout of this SDK repository is sufficient after restore;
do not load a moving sibling protocol checkout or regenerate expected answers with
the implementation being tested. Both language test suites verify the vector
manifest. Platform acceptance also consumes this same snapshot.

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
