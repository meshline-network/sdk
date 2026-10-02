# Shared SDK tests

This directory contains language-neutral test data for SDK implementations.
Each implementation executes it through its own native test adapters.

| Directory | Responsibility |
| --- | --- |
| [vectors](vectors/manifest.json) | One immutable snapshot of seven protocol vector files, with source revision and SHA-256 hashes. |
| [scenarios](scenarios/README.md) | Language-neutral SDK behavior cases with stable IDs, operations and independent expected results. |

The protocol repository remains authoritative for wire-format vectors. Keep a
pinned copy here so a checkout of this SDK repository is sufficient after restore;
do not load a moving sibling protocol checkout or regenerate expected answers with
the implementation being tested. The .NET test suite verifies the vector manifest
on every run. Additional SDK adapters should consume this same snapshot.

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
