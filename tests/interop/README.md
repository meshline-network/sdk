# Cross-language interoperability

Shared vectors and scenarios verify each implementation against independent
expected results. This suite additionally exchanges messages between the actual
.NET and TypeScript SDKs, including signatures, encryption, client workflows,
persistent-state recovery and process termination.

- [dotnet](dotnet/Meshline.Interop.csproj) is the JSON-lines host for the actual
  .NET SDK. Its project references the .NET SDK and reuses its manual test clock.
- [typescript](typescript/package.json) is an independent Node.js/Vitest test
  project with its own lockfile and type-check configuration. It reuses the
  TypeScript SDK's relay fixtures and portable native-acceptance runners.

Run from `sdk/typescript`, with Node.js 24 and .NET 10 installed:

```sh
npm ci
npm run build
npm run restore:interop
npm run build:interop
npm run test:interop
```

`restore:interop` restores the .NET project and runs `npm ci` for the independent
TypeScript test project. `test:interop` type-checks and runs that project. The full
`npm run check` command includes both the language-local and interoperability
suites. `npm test` runs only the language-local tests.

The .NET host also generates short-lived test certificates for existing TLS test
peers. Browser/Expo test hosts resolve it here. After moving or changing host
inputs, rerun the documented native peer preparation commands; old build receipts
intentionally do not authenticate a new source layout.
