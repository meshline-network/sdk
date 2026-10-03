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

The [developer guide](dotnet/docs/README.md) is hand-written English Markdown. The [API reference](dotnet/docs/api/README.md) is generated with the repository-local `DefaultDocumentation.Console` tool, pinned to 1.2.5 in `dotnet/docs/dotnet-tools.json`. The documentation script runs from that directory to resolve its own tool manifest; the EF migration manifest remains at `dotnet/dotnet-tools.json`. Do not edit generated pages. Change the SDK's XML comments to correct API explanations, or the generator configuration to change presentation.

Use PowerShell 7.6 or later and the .NET 10 SDK. PowerShell must run on .NET 10 to load the SDK's metadata inspection library. From the **SDK repository root**, run:

```powershell
pwsh -NoProfile -File dotnet/docs/scripts/Invoke-Documentation.ps1 -Mode Generate
pwsh -NoProfile -File dotnet/docs/scripts/Invoke-Documentation.ps1 -Mode Check
```

Both commands restore the pinned local tool and compile the SDK and examples in Release. [The PowerShell entry point](dotnet/docs/scripts/Invoke-Documentation.ps1) and its helpers, `ApiSurface.ps1` and `Markdown.ps1`, are kept together in `dotnet/docs/scripts/` for API inspection, XML inheritance, snippet synchronization, link checking and output comparison. It uses PowerShell's built-in `ConvertFrom-Markdown` parser and the .NET SDK's `System.Reflection.MetadataLoadContext` library; it does not compile custom C# helpers or restore a separate Markdown package. MSBuild supplies the resolved reference paths so API inspection reads metadata without executing SDK code. Documentation checks run independently of the SDK test project and do not build or invoke it. `Generate` writes the API reference and synchronizes named C# snippets into the guides. `Check` generates into a unique temporary directory and compares the full output, including missing or obsolete files, without rewriting authored or generated documentation. Temporary output is removed after either command, including failures.

The API reference uses the generator's native `FileNameFactory: FullName` layout. All pages are placed directly in `api/`, with fully qualified names such as `Meshline.Components.AccountManager.md`; the assembly index is `api/README.md`. Members stay on their type page. The generator produces the filenames and cross-references directly. `api/coverage.json` maps XML member IDs to the generated URLs; use it when updating links from guides.

Maintain each public namespace's summary in an `internal static class NamespaceDoc` in that namespace's `NamespaceDoc.cs` file. The script preserves these XML comments for DefaultDocumentation to populate the namespace page and assembly index. The helper classes are not part of the public API inventory.

The utility derives a coverage inventory from the public assembly surface and XML member IDs. It includes protected extension points on inheritable types and labels EF Core migrations as infrastructure. Internal types, compiler-generated record helpers, and protected implementation overrides on sealed types are excluded. Inherited documentation is expanded before rendering so local exception descriptions survive. Missing XML summaries and unresolved inheritance fail generation; do not silence them by excluding an application API. Compiler-supplied default constructors are not separate documentation entries.

Examples live in [the example project](dotnet/examples/README.md). Add a uniquely named `#region` for a complete method or coherent group of methods, then reference it in a guide with paired `<!-- snippet: name -->` and `<!-- /snippet -->` markers. The generated fenced code comes from that region. The utility rejects missing, duplicate, malformed, and unused snippets. Source imports remain in the linked C# file. Build compilation verifies API use; it does not execute wallet or network operations.

The checker parses Markdown links and heading anchors, verifies generated API coverage, and requires deterministic LF output. It rejects missing, stale or obsolete output, broken links, missing API symbols and snippet drift. PR documentation checks run in a dedicated job on Windows and Linux. The separate unit-test job verifies SDK behavior without documentation-tool dependencies or checks. External website availability is not a CI dependency; review external destinations when adding or changing links. API source and test changes continue through the existing format, Release build, and offline behavioral checks.

Keep the SDK's package README useful on NuGet: retain installation and minimal startup examples, and use absolute GitHub links to the extended guide. Documentation edits do not require a package version bump or an immediate release. New NuGet packages embed the then-current package README; an existing package's embedded README does not update when GitHub documentation changes.
