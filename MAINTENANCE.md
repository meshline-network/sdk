# SDK maintenance

This guide is for contributors to this repository. Application developers should start with the [.NET SDK guide](dotnet/README.md).

## Local checks

Install .NET SDK 10.x, then run from `dotnet/`:

```sh
dotnet restore Meshline.Sdk.slnx
dotnet format Meshline.Sdk.slnx --verify-no-changes --no-restore
dotnet build Meshline.Sdk.slnx -c Release --no-restore -p:ContinuousIntegrationBuild=true
dotnet run --project tests/Meshline.Sdk.Tests -c Release --no-build -- --minimum-expected-tests 1 --fail-skips on --report-xunit-trx --results-directory TestResults
```

Every pull request runs the format check and full offline test suite in separate jobs. Tests use xUnit v3 with Microsoft Testing Platform; use the `dotnet run` invocation above. See [TESTING.md](dotnet/TESTING.md) for fixtures, filters, concurrency checks, and protocol-vector provenance.

## Local packages

After a Release build, run from `dotnet/`:

```sh
dotnet pack src/Meshline.Sdk/Meshline.Sdk.csproj -c Release --no-build --no-restore
```

This writes a package under `src/Meshline.Sdk/bin/Release/` without publishing it. The package includes the SDK guide as `README.md`, the MIT license, `assets/icon.png`, and XML API documentation. Changes to these embedded files reach NuGet with a new package version; editing GitHub documentation alone does not update an existing NuGet package.

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

Update `Version` and the user-facing installation examples and release notes together for a new SDK release. Documentation-only changes can retain the current version. Keep the protocol specification's status separate from the SDK's release number.

## NuGet Trusted Publishing

The workflow authenticates with `NuGet/login@v1` and GitHub OIDC. It requires the repository Actions variable `NUGET_USER`, containing the nuget.org login username, and a NuGet Trusted Publishing policy authorizing the package for:

- Repository owner: `meshline-network`
- Repository: `sdk`
- Workflow file: `release.yml`
- Environment: empty

The release job has `id-token: write` for authentication and `contents: write` for GitHub Releases. No long-lived NuGet API key secret is required. The workflow uses the temporary key returned by the login action.

## Documentation

Keep README files, website copy, and Release notes focused on installation, integration, capabilities, and limits for SDK consumers. Keep build, testing, publishing, and contributor procedures in this guide or the test guide.
