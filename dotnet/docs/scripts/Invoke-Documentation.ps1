#Requires -Version 7.6
param(
    [ValidateSet('Generate', 'Check')]
    [string]$Mode = 'Check'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. (Join-Path $PSScriptRoot 'ApiSurface.ps1')
. (Join-Path $PSScriptRoot 'Markdown.ps1')
$docsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sdkRoot = [IO.Path]::GetFullPath((Join-Path $docsRoot '../..'))
$dotnetRoot = Join-Path $sdkRoot 'dotnet'
$work = Join-Path ([IO.Path]::GetTempPath()) ('meshline-docs-' + [Guid]::NewGuid().ToString('N'))
$project = Join-Path $dotnetRoot 'src/Meshline.Sdk/Meshline.Sdk.csproj'
$assembly = Join-Path $dotnetRoot 'src/Meshline.Sdk/bin/Release/net10.0/Meshline.Sdk.dll'
$previousGlobalization = $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT
$previousCulture = [Globalization.CultureInfo]::CurrentCulture
$previousUICulture = [Globalization.CultureInfo]::CurrentUICulture

Push-Location $docsRoot
try {
    dotnet tool restore
    dotnet build (Join-Path $dotnetRoot 'examples/Meshline.Sdk.Examples/Meshline.Sdk.Examples.csproj') -c Release --nologo
    $references = dotnet msbuild $project -nologo -target:ResolveReferences -property:Configuration=Release -getProperty:MSBuildToolsPath -getItem:ReferencePath | ConvertFrom-Json
    [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::InvariantCulture
    [Globalization.CultureInfo]::CurrentUICulture = [Globalization.CultureInfo]::InvariantCulture
    $surface = Initialize-ApiDocumentation $assembly $docsRoot $work $references
    $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = '1'
    dotnet tool run defaultdocumentation -- -a $assembly -d (Join-Path $work 'Meshline.Sdk.xml') -j (Join-Path $work 'generator.json') -o (Join-Path $work 'api') -l (Join-Path $work 'links.txt') -h Warning
    Complete-Documentation $Mode $docsRoot $sdkRoot $work $surface
}
finally {
    $env:DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = $previousGlobalization
    [Globalization.CultureInfo]::CurrentCulture = $previousCulture
    [Globalization.CultureInfo]::CurrentUICulture = $previousUICulture
    Pop-Location
    # Only the unique directory created by this invocation may be removed.
    $resolvedWork = [IO.Path]::GetFullPath($work)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (!$resolvedWork.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolvedWork) -notmatch '^meshline-docs-[a-f0-9]{32}$') {
        throw "Unexpected documentation work directory: $resolvedWork"
    }
    if (Test-Path -LiteralPath $resolvedWork) { Remove-Item -LiteralPath $resolvedWork -Recurse -Force }
}
