# SDK maintenance

This guide is for contributors to this repository. Application developers should start with the [SDK guides](README.md#available-sdks).

## Local checks

Install .NET SDK 10.x, then run from `dotnet/`:

```sh
dotnet restore Meshline.Sdk.slnx
dotnet format Meshline.Sdk.slnx --verify-no-changes --no-restore
dotnet build Meshline.Sdk.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --minimum-expected-tests 1 --fail-skips on --report-xunit-trx --results-directory TestResults
```

Every pull request runs the format check and full offline test suite in separate jobs. Tests use xUnit v3 with Microsoft Testing Platform; use the `dotnet run` invocation above. See [TESTING.md](dotnet/TESTING.md) for fixtures, filters, concurrency checks, and protocol-vector provenance.

Keep language-neutral vectors and behavior cases in [shared tests](tests/README.md).
Each SDK executes them through its own native test adapters. Cross-language
interoperability has a separate [test project](tests/interop/README.md).

To consume a local .NET checkout, add a project reference using your application
and checkout paths:

```sh
dotnet add path/to/MyApp.csproj reference path/to/sdk/dotnet/src/Meshline.Sdk/Meshline.Sdk.csproj
```

## Local packages

After a Release build, run from `dotnet/`:

```sh
dotnet pack src/Meshline.Sdk/Meshline.Sdk.csproj -c Release --no-build --no-restore
```

This writes a package under `src/Meshline.Sdk/bin/Release/` without publishing it. The package includes the SDK guide as `README.md`, the MIT license, `assets/icon.png`, and XML API documentation. Changes to these embedded files reach NuGet with a new package version; editing GitHub documentation alone does not update an existing NuGet package.

## TypeScript SDK checks and local packages

Use Node.js 24 and .NET 10, then run from `typescript/`:

```sh
npm ci
npm run restore:interop
npm run check
npm run pack:local
npm run check:packages
npm run check:expo-bundle
npx playwright install --with-deps chromium firefox webkit
npm run test:browser
```

The checks compile the SDK and examples, verify documentation and client method
destinations, and run the offline suite with the actual .NET interoperability
driver. Package checks install the five tarballs into independent consumers;
Expo bundle checks compile JavaScript with Metro and Hermes for Android and iOS.
These commands do not publish packages or establish native runtime acceptance.

See the [TypeScript test guide](typescript/TESTING.md) for test placement, fixture ownership and filtered runs. Public core API tests use the built `@meshline/sdk` package entry point; internal tests consistently import built modules. `npm test` and `npm run test:conformance` build first through npm lifecycle hooks. Direct `npx vitest` commands require an explicit `npm run build` beforehand.

[typescript.yml](.github/workflows/typescript.yml) configures Windows, Linux and
macOS checks. See [platform requirements and limits](typescript/docs/platforms.md)
for the known Windows WebKit cookie issue and unverified runtimes; a workflow
definition alone does not prove a platform passed.

Generated API mappings are review aids. Update `typescript/scripts/api/api-map.json`,
run `npm run inventory:api`, and inspect the resulting inventory when reviewing
the .NET surface. `npm run check:inventory`, included in `npm run check`, rejects
stale inventory output, duplicate/unmatched mappings and missing destination files.

## Database schema changes

Preserve `InitialCreate` as the 1.0.0 database baseline and add incremental migrations for subsequent model changes. Generate migrations and model snapshots with `dotnet-ef`, rather than editing them by hand. Run from `dotnet/`, replacing `DescribeSchemaChange` with a descriptive name:

```sh
dotnet tool restore
dotnet ef migrations add DescribeSchemaChange --project src/Meshline.Sdk --startup-project tests/Meshline.Sdk.Tests --context MeshlineDbContext --output-dir Storage/Migrations
dotnet ef migrations has-pending-model-changes --project src/Meshline.Sdk --startup-project tests/Meshline.Sdk.Tests --context MeshlineDbContext
```

The test project's design-time factory uses an in-memory SQLite database. Verify both fresh database creation and upgrades from the previous schema when introducing a migration.

Account establishment uses the `ResumableAccountEstablishment` migration after
`InitialCreate`. Verify that the direct upgrade preserves device keys, pending
signed publications and history. Intermediate development schemas are not upgrade
baselines.

`DeviceCreationPushGateway` adds nullable creation intent to `AccountEstablishment`
before a device draft exists. Verify upgrades from `ResumableAccountEstablishment`
preserve unfinished setup, protected drafts and exact pending publications. Existing
certificates and drafts remain authoritative for their gateway declaration.

## Account establishment

The [account establishment guide](dotnet/docs/guides/account-establishment.md)
defines the caller contract and recovery limits. Keep implementation details here
and regression ownership in [TESTING.md](dotnet/TESTING.md#regression-ownership).

### Ownership and durable state

`MeshlineClient` owns the workflow. It records intent and the next step before
dispatching to a component, then checkpoints the component's outcome. Components
own idempotent domain execution and persistence. Partial files organize each
owner; they do not introduce another component, forwarding facade or workflow store.

| Owner | Responsibility and durable evidence |
| --- | --- |
| `MeshlineClient` | `AccountEstablishment`: operation ID, relay, lifetimes, initial push gateway, initial profile, next step, replacement intent, original local identity and historical authorization checkpoint. |
| `DeviceManager` | `LocalDevice`: active keys/certificate. `DevicePreparation`: protected draft and last activation receipt. Device-state cache and the `device.state.publish` record in `SignedRequests`. |
| `AccountManager` | Verified `AccountRoutes` and the `account.route.publish` record in `SignedRequests`; route revisions, replay and confirmation. |
| `ProfileManager` | Existing profile resolution, creation-only initialization and exact pending profile publication. |
| `MessageManager` | Contacts, confirmed grants, consent requests and outbox; background authorization supplementation and delivery. |
| `RelayClientPool` | Identity/certificate binding and session retirement; no workflow or database state. |

`DatabaseBinding` identifies the network, account and device. Its local-device
binding provides issuance provenance; an empty contact list does not establish
history for a foreign device. Operation IDs associate resumable work internally.
The device publication's optional `OperationKey` associates execution with an
operation and replacement choice; public standalone publications do not inherit it.

### Workflow and replay

Preflight checks local key access before network I/O, refreshes the route, and
inspects current device authorization. A healthy account with no setup record or
replacement intent returns without signing or creating a workflow. An unrelated
unconfirmed route publication does not invalidate a usable route. When no route
is usable, `GetUnconfirmedRouteAsync` supplies a local, read-only parameter check
before setup saves intent or prepares keys; route execution revalidates it.

The durable steps are:

1. **Authorize device.** `DeviceManager` prepares or reuses keys, reads complete
   state, validates capacity and explicit replacement, and reconciles publication.
   Bootstrap staging returns `AwaitingRoute`, never confirmed authorization.
2. **Ensure route.** `AccountManager.EnsureRouteAsync` establishes or renews the
   original route and reconciles its pending request. Execution returns to device
   authorization afterwards.
3. **Initialize profile and complete.** `ProfileManager` preserves existing values
   or creates the initial profile. The client verifies effective route/device
   authorization and deletes the workflow in a short transaction.

Resume revalidates prerequisites before dispatching the saved step. Expired original
routes need renewal before a device-state query; unconfirmed bootstrap staging may
need replay first. A failed checkpoint repeats the component operation safely.
The historical authorization checkpoint never bypasses current validity checks.
Completion checks validity before deletion, before commit and after commit. Expiry
rolls back deletion or restores the same workflow. These checks use local state;
no extra read, signature or network request runs inside the cleanup transaction.

Inspection interprets the activation receipt and returns the original local ID;
the client saves that ID before authorization. Repeated inspection within one call
reuses its key-access result. Every explicit call reads both protected keys again.
`DeviceManager` derives route readiness from `AccountManager`, without a duplicated
workflow flag. Failures cross this boundary as domain reasons and actionable
details; the client translates them to `AccountEstablishmentException`.

Replacement intent keeps the accepted choice and a proposed new choice separately.
Omitted options resume them. An effective replacement or unresolved proposal cannot
be changed; later revocation can permit another explicit selection. The device
owner validates the proposal and returns accepted, rejected or unresolved
disposition, including on failure. Its saved `OperationKey` distinguishes a submitted
proposal from an older publication when checkpointing failed. Signing refusal
before persistence does not freeze a new proposal. Evidence reads for this failure
path do not add reads to successful or unchanged authorization.

### Device and route publication

Persist the exact certificate draft and both protected keys before account signing.
Commit activation, binding changes and draft consumption together. Clear consumed
draft secrets but retain `ActivatedOperationId`, including while a later renewal
draft is pending. Renewal keeps the original identity and keys. Any key-access
failure preserves all records; partial permanent loss is outside the supported
all-retained-or-all-absent local-data contract.

Before device mutation, read complete verified state directly from the original
relay. Apply only the local add/renew and explicit remove intent, and publish at the
observed revision plus one. Conflicts reread and merge within a bounded retry loop.
Only `Pending` requests are replayable; preserve their exact signed content and
certificate ordering. Reconcile an unknown publication before renewing an expired
certificate. Reconciliation does not consume the conflict retry budget, and an
accepted expired certificate is not successful authorization. If renewal expires
during signing/submission, retain the keys and report failure for explicit retry.

Success requires relay acceptance and effective device authorization. An unchanged
authorization uses its fresh read as confirmation; mutations require a subsequent
read. Retire old device sessions before that read so final authentication reuses
the newly established session. Recovery and relay migration use their own revision
rules but share device preparation/publication and final authorization checks.

`EnsureRouteAsync` derives bootstrap, renewal and replay from verified routes and
signed requests. Discovery runs outside the publication gate; revision selection
and submission share the explicit publication gate. Recheck expiry after acquiring
it and preserve newer state published by discovery callbacks. A valid route without
unfinished work needs no network read or signer.

Unexpired unknown route submissions retain the exact document. After discovery,
an expired request can be superseded on the same relay only with retained verified
original-route evidence. Choose a revision above both route and saved request;
retain that floor even if a successor is rejected. A saved request revision or a
cleared pending flag is not acceptance evidence. Without the original route, an
expired initial request remains a conflict. Definitive rejection may be retried
with the identical unexpired document on a later explicit call after fresh discovery,
but not repeatedly within one call. Never guess a higher bootstrap revision.

Each publication owner classifies unknown outcomes around the actual submission.
Query, signing and session-acquisition failures before submission are unavailable,
not unknown outcomes. Standalone publication APIs preserve their exception types.
Profile initialization resolves first and replays unknown requests exactly; existing
values win over initial settings. Its publication counter prevents delayed reads
from overwriting newer local edits, including edits within the same second.

### Local coordination and contact continuity

Each database has one active client/component graph, including across processes.
Callers await disposal before transferring ownership. Establishment cannot overlap
caller-initiated device mutations, including from signing or event callbacks.
`_accountGate` serializes client account workflows; `DeviceManager`'s private
mutation gate serializes standalone device mutations. `AccountEstablishment`,
`LocalDevice`, `DevicePreparation` and `DatabaseBinding.DeviceId` need no concurrency
versions under this contract. Preserve remote revision checks and concurrency tokens
for records with independent synchronization writers.

Public create/renew rejects an unfinished draft or pending device publication.
Local activation/renewal acquires the SQLite writer before the key gate, matching
message transactions. Account signing holds neither lock. Install cached keys
after commit and retire sessions outside the key gate. Standalone mutation helpers
belong to the device entry points; authorization execution stages remain private
to `DeviceManager`, separate from the client's durable workflow steps.

The relay pool owns certificate comparison and session retirement. Cache canonical
content per immutable certificate object; equal content after deserialization
retains sessions. Public acquisition and owner rebinding share one transition under
the pool lock, with a binding recheck at session acquisition. Public signers cannot
restore a superseded binding; owner rebinding restores committed local state.
Retained binding history is local to the pool and does not order certificates by
validity dates. Account sessions remain available throughout device renewal.

Removal/replacement validates retained grant evidence against the exact remaining
certificates before signing, then repeats continuity and state validation in the
immediate write transaction that saves the pending publication. Wallet and relay
waits remain outside that transaction. Contact authorization writes either commit
before the final check or observe the pending publication and fail promptly,
including after restart. Revalidate prepared grants before contact snapshot flushes.
Messaging never acquires the device mutation gate; each enqueue uses one certificate
snapshot for authorization, encryption and history, rolling back if signing sees
a different identity.

Continuity includes active and previously confirmed grants plus queued/unknown
outgoing consent requests. Only `TargetAccepted` confirms the same current grant
with a valid remaining-device signature. Source acceptance, expired/different grants,
and unknown delivery are insufficient; failed/canceled requests impose no delivery
dependency. Do not remove the last usable signer in anticipation of future delivery.

Background grant maintenance derives work from persisted device/contact/outbox state
at startup. Commit supplemented grants and their encrypted requests together; retain
message IDs and ciphertext on retry. Commit delivery status and confirmed grants
together before completing status waiters. Retry preparation failures, isolate
unavailable peers, and notify successful peers only once per state change.
Definitive delivery failures remain visible rather than generating new requests
on every scan. Account establishment never waits for this background delivery.

### Runtime and shutdown

Startup reserves `_runtimeActive` under the account gate, then releases the gate
before starting children or invoking callbacks. Authorization changes remain
blocked until shutdown finishes. Canceling startup while it is only waiting for
that gate must not stop children or cancel establishment queries. Lifetime
cancellation precedes waiting for lifecycle transitions; concurrent disposal
shares cancellation and retains observable cleanup failures.

The client stops its runtime after verified local-device removal or confirmed
local authentication rejection. Network failures, expiry and peer/recipient errors
are not revocation. Runtime reads never request account signatures. Schedule stop
outside event callbacks, associate it with the originating runtime token, and drain
it during disposal so a delayed stop cannot affect a later runtime.

Shutdown attempts every child in reverse dependency order. Each owner drains tasks,
subscriptions and device sessions even after a failure, then reports all errors
and the stopped transition. Clear drained task references for later stops. Pool
invalidation also joins unfinished retirements removed from active entries;
completed failures remain observable at disposal without blocking future sessions.
Preserve account sessions and verified relay metadata. A storage failure retains
pending messages for normal initialization reconciliation. Managers do not stop
one another or share a cross-component shutdown lock.

## Automated releases

[release.yml](.github/workflows/release.yml) runs on pushes to `main`:

- An explicit, non-empty `Version` in `dotnet/src/Meshline.Sdk/Meshline.Sdk.csproj` selects NuGet and GitHub publication. An existing published `v<Version>` Release skips publication.
- `Version` takes precedence when `VersionPrefix` is also declared. A project with only a non-empty `VersionPrefix` skips this publishing workflow; MyGet publishing is not implemented.
- Missing both properties fails. Release versions must be canonical `major.minor.patch`, optionally with a SemVer prerelease suffix, without build metadata. `PackageVersion` must match `Version`.
- The workflow restores, builds, tests, and packs before publishing. NuGet publication precedes GitHub Release publication.
- If NuGet succeeded but the GitHub step failed, rerun the same workflow run after NuGet indexing completes. Existing package metadata and tags must identify the same source commit. A version already owned by another commit requires a version bump.

For a .NET version bump, change only `Version` in the SDK project. Installation examples select the current stable package; the workflow generates GitHub Release notes. API coverage records symbols rather than the assembly version, so a version-only change does not require regenerating documentation or TypeScript API inventories. Documentation-only changes can retain the current version. Keep the protocol specification's status separate from the SDK's release number.

## NuGet Trusted Publishing

The workflow authenticates with `NuGet/login@v1` and GitHub OIDC. It requires the repository Actions variable `NUGET_USER`, containing the nuget.org login username, and a NuGet Trusted Publishing policy authorizing the package for:

- Repository owner: `meshline-network`
- Repository: `sdk`
- Workflow file: `release.yml`
- Environment: empty

The release job has `id-token: write` for authentication and `contents: write` for GitHub Releases. No long-lived NuGet API key secret is required. The workflow uses the temporary key returned by the login action.

### TypeScript npm releases

[release-typescript.yml](.github/workflows/release-typescript.yml) runs on pushes
to `main` and can be rerun manually on `main`. All five `@meshline/*` packages
share one version. From `typescript/`, run the following command with the desired
version:

```sh
npm run release -- version <version>
```

This local command updates the five package manifests, their internal dependency
versions, and both the workspace and interoperability lockfiles. It does not
stage files, commit, tag, push, or publish. Commit the resulting seven files
together when the release is ready. Android and iOS read the Expo version from
its package manifest. Installation examples use `@alpha`, so they do not change
for each alpha release; update that channel when moving to beta or stable.

The workflow validates the workspace and lockfile, skips an existing published
`typescript-v<version>` Release, and otherwise builds and checks the SDK on
Windows. Its gates include offline and interoperability tests, documentation,
compiled examples, independent packed consumers, Expo Metro/Hermes bundles and
Chromium/Firefox acceptance. WebKit remains in the separate TypeScript checks;
the known Windows cookie failure does not establish Safari behavior. This release
workflow does not perform Android/iOS native acceptance.

The publication job downloads the same five tested tarballs, verifies SHA-256
and npm integrity, then publishes core before its adapters with npm provenance.
`-alpha.*`, `-beta.*` and `-rc.*` versions use their matching npm dist-tags;
stable versions use `latest`. Other prerelease channels and SemVer build metadata
are rejected. The workflow refuses to move a channel back to an older version.

Before the first automated release, configure
[npm Trusted Publishing](https://docs.npmjs.com/trusted-publishers/) separately
for `@meshline/sdk`, `@meshline/storage-node`, `@meshline/storage-browser`,
`@meshline/transport-node` and `@meshline/expo`:

- Provider: GitHub Actions
- Organization: `meshline-network` (the GitHub owner, not the npm scope)
- Repository: `sdk`
- Workflow filename: `release-typescript.yml`
- Environment: empty
- Allowed actions: enable direct `npm publish`

The GitHub-hosted publication job uses `id-token: write`; no `NPM_TOKEN` secret
or interactive npm login is needed. The workflow must be committed before its
authorization can be used. This file does not configure the npm package settings.

A GitHub draft reserves the source commit before npm uploads begin. If a run
fails partway through, rerun that same Actions run: identical npm archives are
skipped, missing packages are published, and the GitHub Release becomes public
only after all five packages are available. After uploading, the workflow polls
for availability every 30 seconds with a shared 20-minute wait budget, allowing
for npm's publish-time scanning. If that budget expires, inspect npm status and
rerun the same workflow once the packages are available. A different archive for an existing
version, a conflicting tag/draft commit, or an unexpected npm dist-tag stops the
run for inspection. npm publication is not atomic across packages; already
published versions are not rolled back. After npm succeeds, the Release attaches
the five tarballs and `SHA256SUMS`, marks prereleases appropriately, and leaves
the repository-wide GitHub `Latest` selection unchanged.

For local release-logic tests without publication, run `npm run test:release`
from `typescript/`. These tests also run within `npm run check`.

## Documentation

Keep README files, website copy, and Release notes focused on installation, integration, capabilities, and limits for SDK consumers. Keep build, testing, publishing, and contributor procedures in this guide or the test guide.

The [.NET guide](dotnet/docs/README.md) and [TypeScript guide](typescript/docs/README.md)
are hand-written English Markdown. Language READMEs cover installation and a minimal
session; guide indexes route readers by task. Keep lifecycle focused on ownership,
synchronization on progress and fresh passes, direct messages on the outbox,
conversations on read state, and storage on query ranges and adapters. Event/error
contracts belong in `events-and-errors.md`; symptom-based diagnosis belongs in
`troubleshooting.md`. Extend the relevant section instead of appending new features
after an API reference or navigation footer. Keep runtime differences explicit and
use matching source-release documentation for package-specific behavior.

The [API reference](dotnet/docs/api/README.md) is generated with the repository-local `DefaultDocumentation.Console` tool, pinned to 1.2.5 in `dotnet/docs/dotnet-tools.json`. The documentation script runs from that directory to resolve its own tool manifest; the EF migration manifest remains at `dotnet/dotnet-tools.json`. Do not edit generated pages. Change the SDK's XML comments to correct API explanations, or the generator configuration to change presentation.

Use PowerShell 7.6 or later and the .NET 10 SDK. PowerShell must run on .NET 10 to load the SDK's metadata inspection library. From the **SDK repository root**, run:

```powershell
pwsh -NoProfile -File dotnet/docs/scripts/Invoke-Documentation.ps1 -Mode Generate
pwsh -NoProfile -File dotnet/docs/scripts/Invoke-Documentation.ps1 -Mode Check
```

Both commands restore the pinned local tool and compile the SDK and examples in Release. [The PowerShell entry point](dotnet/docs/scripts/Invoke-Documentation.ps1) and its helpers, `ApiSurface.ps1` and `Markdown.ps1`, are kept together in `dotnet/docs/scripts/` for API inspection, XML inheritance, snippet synchronization, link checking and output comparison. It uses PowerShell's built-in `ConvertFrom-Markdown` parser and the .NET SDK's `System.Reflection.MetadataLoadContext` library; it does not compile custom C# helpers or restore a separate Markdown package. MSBuild supplies the resolved reference paths so API inspection reads metadata without executing SDK code. Documentation checks run independently of the SDK test project and do not build or invoke it. `Generate` writes the API reference and synchronizes named C# snippets into the guides. `Check` generates into a unique temporary directory and compares the full output, including missing or obsolete files, without rewriting authored or generated documentation. Temporary output is removed after either command, including failures.

The API reference uses the generator's native `FileNameFactory: FullName` layout. All pages are placed directly in `api/`, with fully qualified names such as `Meshline.Components.AccountManager.md`; the assembly index is `api/README.md`. Members stay on their type page. The generator produces the filenames and cross-references directly. `api/coverage.json` maps XML member IDs to the generated URLs; use it when updating links from guides.

Maintain each public namespace's summary in an `internal static class NamespaceDoc` in that namespace's `NamespaceDoc.cs` file. The script preserves these XML comments for DefaultDocumentation to populate the namespace page and assembly index. The helper classes are not part of the public API inventory.

The utility derives a coverage inventory from the public assembly surface and XML member IDs. It includes protected extension points on inheritable types and labels EF Core migrations as infrastructure. Internal types, compiler-generated record helpers, and protected implementation overrides on sealed types are excluded. Inherited documentation is expanded before rendering so local exception descriptions survive. Inheritance paths use Roslyn's standalone member-document context: use `/exception` for exceptions or `/*[self::param or self::returns or self::exception]` for selected member sections. NuGet includes the compiler-generated XML with these paths for IDE resolution. Missing XML summaries, unresolved inheritance and paths selecting no content fail generation; do not silence them by excluding an application API. Compiler-supplied default constructors are not separate documentation entries.

Examples live in [the example project](dotnet/examples/README.md). Add a uniquely named `#region` for a complete method or coherent group of methods, then reference it in a guide with paired `<!-- snippet: name -->` and `<!-- /snippet -->` markers. The generated fenced code comes from that region. The utility rejects missing, duplicate, malformed, and unused snippets. Source imports remain in the linked C# file. Build compilation verifies API use; it does not execute wallet or network operations.

The checker parses Markdown links and heading anchors, verifies generated API coverage, and requires deterministic LF output. It rejects missing, stale or obsolete output, broken links, missing API symbols and snippet drift. PR documentation checks run in a dedicated job on Windows and Linux. The separate unit-test job verifies SDK behavior without documentation-tool dependencies or checks. External website availability is not a CI dependency; review external destinations when adding or changing links. API source and test changes continue through the existing format, Release build, and offline behavioral checks.

Keep the SDK's package README useful on NuGet: retain installation and minimal startup examples, and use absolute GitHub links to the extended guide. Documentation edits do not require a package version bump or an immediate release. New NuGet packages embed the then-current package README; an existing package's embedded README does not update when GitHub documentation changes.
