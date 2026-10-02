function ConvertTo-DocumentationText([string]$Text) {
    $lines = foreach ($line in $Text.Replace("`r`n", "`n").Split("`n")) {
        if ($line.EndsWith('  ', [StringComparison]::Ordinal) -and $line.Trim().Length -gt 0) { $line.TrimEnd() + '<br>' }
        else { $line.TrimEnd() }
    }
    return ($lines -join "`n").TrimEnd() + "`n"
}

function Write-DocumentationFile([string]$Path, [string]$Text) {
    [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))
    [IO.File]::WriteAllText($Path, (ConvertTo-DocumentationText $Text), [Text.UTF8Encoding]::new($false))
}

function Get-DocumentationSnippets([string]$Directory) {
    $snippets = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($file in [IO.Directory]::EnumerateFiles($Directory, '*.cs')) {
        $name = $null
        $body = [Collections.Generic.List[string]]::new()
        foreach ($line in [IO.File]::ReadAllLines($file)) {
            if ($line.TrimStart().StartsWith('#region ', [StringComparison]::Ordinal)) {
                if ($null -ne $name) { throw "Nested snippet: $file" }
                $name = $line.Trim().Substring(8)
                $body.Clear()
            }
            elseif ($line.Trim() -ceq '#endregion') {
                if ($null -eq $name) { throw "Unmatched region: $file" }
                $indents = @($body | Where-Object { $_.Trim().Length -gt 0 } | ForEach-Object { $_.Length - $_.TrimStart().Length })
                if ($indents.Count -eq 0) { throw "Empty snippet: $name in $file" }
                $indent = ($indents | Measure-Object -Minimum).Minimum
                $code = ($body | ForEach-Object { if ($_.Length -ge $indent) { $_.Substring($indent) } else { '' } }) -join "`n"
                if (!$snippets.TryAdd($name, $code)) { throw "Duplicate snippet: $name" }
                $name = $null
            }
            elseif ($null -ne $name) { $body.Add($line) }
        }
        if ($null -ne $name) { throw "Unclosed snippet: $file" }
    }
    return ,$snippets
}

function Get-DocumentationMarkdown([string]$Content) {
    # PowerShell supplies the Markdown parser; no separately restored Markdig package.
    $markdown = ConvertFrom-Markdown -InputObject $Content
    $anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($heading in [Markdig.Syntax.MarkdownObjectExtensions]::Descendants[Markdig.Syntax.HeadingBlock]($markdown.Tokens)) {
        $id = [Markdig.Renderers.Html.HtmlAttributesExtensions]::GetAttributes($heading).Id
        if ($null -ne $id) { [void]$anchors.Add($id) }
    }
    foreach ($match in [regex]::Matches($markdown.Html, '<a\s+(?:name|id)=["'']([^"'']+)["'']')) {
        [void]$anchors.Add([Net.WebUtility]::HtmlDecode($match.Groups[1].Value))
    }
    $links = [Collections.Generic.List[string]]::new()
    foreach ($link in [Markdig.Syntax.MarkdownObjectExtensions]::Descendants[Markdig.Syntax.Inlines.LinkInline]($markdown.Tokens)) {
        if ($null -ne $link.Url) { $links.Add($link.Url) }
    }
    return @{ Anchors = $anchors; Links = $links }
}

function Assert-DocumentationLinks($Pages, [string]$DocsRoot, [string]$RepositoryRoot) {
    $documents = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($name in $Pages.Keys) {
        if ($name.EndsWith('.md', [StringComparison]::Ordinal)) { $documents.Add([IO.Path]::GetFullPath((Join-Path $DocsRoot $name)), $Pages[$name]) }
    }
    foreach ($name in @('README.md', 'MAINTENANCE.md', 'dotnet/README.md', 'dotnet/examples/README.md')) {
        $file = Join-Path $RepositoryRoot $name
        if ([IO.File]::Exists($file)) { $documents[$file] = [IO.File]::ReadAllText($file) }
    }
    $parsed = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($file in $documents.Keys) { $parsed[$file] = Get-DocumentationMarkdown $documents[$file] }
    $count = 0
    foreach ($file in $documents.Keys) {
        foreach ($url in $parsed[$file].Links) {
            if ($url -cmatch '^(https?://|mailto:)') { continue }
            if ($url.StartsWith('/') -or $url.Contains('://', [StringComparison]::Ordinal)) { throw "Nonportable link: ${file}: $url" }
            $parts = $url.Split('#', 2)
            $path = if ($parts[0].Length -eq 0) { $file } else {
                [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetDirectoryName($file)) ([Uri]::UnescapeDataString($parts[0]))))
            }
            if (!$path.StartsWith($RepositoryRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw "Link escapes repository: ${file}: $url" }
            $generatedPath = $path.StartsWith((Join-Path $DocsRoot 'api') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)
            if (!$documents.ContainsKey($path) -and ($generatedPath -or (![IO.File]::Exists($path) -and ![IO.Directory]::Exists($path)))) {
                throw "Broken link: ${file}: $url"
            }
            if ($parts.Count -eq 2 -and $parts[1].Length -gt 0 -and $path.EndsWith('.md', [StringComparison]::Ordinal)) {
                if (!$parsed.ContainsKey($path)) { $parsed[$path] = Get-DocumentationMarkdown ([IO.File]::ReadAllText($path)) }
                if (!$parsed[$path].Anchors.Contains([Uri]::UnescapeDataString($parts[1]))) { throw "Broken anchor: ${file}: $url" }
            }
            $count++
        }
    }
    Write-Host "Verified $count local Markdown links and anchors."
}

function Complete-Documentation([string]$Mode, [string]$DocsRoot, [string]$RepositoryRoot, [string]$Work, $Surface) {
    $pages = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($file in [IO.Directory]::EnumerateFiles((Join-Path $Work 'api'), '*.md')) {
        $pages.Add('api/' + [IO.Path]::GetFileName($file), [IO.File]::ReadAllText($file))
    }
    $links = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($row in [IO.File]::ReadAllLines((Join-Path $Work 'links.txt'))) {
        if (!$row.Contains('|')) { continue }
        $parts = $row.Split('|', 3)
        if ($parts.Count -ne 3) { throw "Invalid generated API link: $row" }
        $links.Add($parts[0], @{ Url = $parts[1]; Label = $parts[2] })
    }
    $expected = [Collections.Generic.HashSet[string]]::new([string[]]$Surface.Ids, [StringComparer]::Ordinal)
    foreach ($id in $Surface.Ids) {
        if (!$links.ContainsKey($id)) { throw "Missing generated API symbol: $id" }
    }
    foreach ($id in $links.Keys) {
        if ($id -cmatch '^[TMPFE]:' -and !$expected.Contains($id)) { throw "Unexpected generated API symbol: $id" }
    }
    $orderedLinks = [string[]]@($links.Keys)
    [Array]::Sort($orderedLinks, [StringComparer]::Ordinal)
    foreach ($name in @($pages.Keys)) {
        $notice = "<!-- Generated by dotnet/docs/scripts/Invoke-Documentation.ps1. Edit source XML comments, not this file. -->`n`n[Developer guide](../README.md) · [API index](README.md)`n`n"
        if ($name.Contains('Storage.Migrations', [StringComparison]::Ordinal)) {
            $notice += '> Infrastructure API: managed by Entity Framework Core. Use `MeshlineDatabase.MigrateAsync` to apply the database schema.' + "`n`n"
        }
        $prefix = $name.Substring(4) + '#'
        $entries = @($orderedLinks | Where-Object { $_ -cmatch '^[MPFE]:' -and $links[$_].Url.StartsWith($prefix, [StringComparison]::Ordinal) })
        if ($entries.Count -gt 0) {
            $notice += "<details>`n<summary>Members on this page</summary>`n`n"
            foreach ($id in $entries) {
                $notice += '- [`' + $links[$id].Label + '`](#' + [Uri]::EscapeDataString($links[$id].Url.Substring($prefix.Length)) + ")`n"
            }
            $notice += "`n</details>`n`n"
        }
        $pages[$name] = ConvertTo-DocumentationText ($notice + $pages[$name])
    }
    $symbols = [ordered]@{}
    foreach ($id in $Surface.Ids) { $symbols.Add($id, $links[$id].Url) }
    $coverage = [ordered]@{ assembly = $Surface.Assembly; version = $Surface.Version; generator = 'DefaultDocumentation.Console 1.2.5'; symbols = $symbols }
    # Match the existing JSON escaping and indentation across PowerShell platforms.
    $json = [Text.Json.Nodes.JsonNode]::Parse(($coverage | ConvertTo-Json -Depth 10))
    $options = [Text.Json.JsonSerializerOptions]::new()
    $options.WriteIndented = $true
    $pages['api/coverage.json'] = ConvertTo-DocumentationText $json.ToJsonString($options)

    $snippets = Get-DocumentationSnippets (Join-Path $RepositoryRoot 'dotnet/examples/Meshline.Sdk.Examples')
    $used = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pattern = [regex]::new('<!-- snippet: ([a-z0-9-]+) -->.*?<!-- /snippet -->', [Text.RegularExpressions.RegexOptions]::Singleline)
    foreach ($file in [IO.Directory]::EnumerateFiles($DocsRoot, '*.md', [IO.SearchOption]::AllDirectories)) {
        if ($file.StartsWith((Join-Path $DocsRoot 'api') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { continue }
        $source = [IO.File]::ReadAllText($file)
        if ([regex]::Matches($source, '<!-- snippet:').Count -ne $pattern.Matches($source).Count) { throw "Malformed snippet markers: $file" }
        $updated = $pattern.Replace($source, [Text.RegularExpressions.MatchEvaluator]{
            param($match)
            $name = $match.Groups[1].Value
            if (!$snippets.ContainsKey($name)) { throw "Unknown snippet $name in $file" }
            [void]$used.Add($name)
            return "<!-- snippet: $name -->`n" + '```csharp' + "`n" + $snippets[$name] + "`n" + '```' + "`n<!-- /snippet -->"
        })
        $pages[[IO.Path]::GetRelativePath($DocsRoot, $file).Replace('\', '/')] = ConvertTo-DocumentationText $updated
    }
    foreach ($name in $snippets.Keys) {
        if (!$used.Contains($name)) { throw "Undocumented snippet: $name" }
    }
    Assert-DocumentationLinks $pages $DocsRoot $RepositoryRoot
    $differences = [Collections.Generic.List[string]]::new()
    foreach ($name in $pages.Keys) {
        $target = Join-Path $DocsRoot $name
        if (![IO.File]::Exists($target) -or [IO.File]::ReadAllText($target) -cne $pages[$name]) {
            $differences.Add($name)
            if ($Mode -ceq 'Generate') { Write-DocumentationFile $target $pages[$name] }
        }
    }
    $output = Join-Path $DocsRoot 'api'
    if ([IO.Directory]::Exists($output)) {
        foreach ($file in [IO.Directory]::EnumerateFiles($output, '*', [IO.SearchOption]::AllDirectories)) {
            $name = [IO.Path]::GetRelativePath($DocsRoot, $file).Replace('\', '/')
            if ($pages.ContainsKey($name)) { continue }
            $differences.Add('obsolete: ' + $name)
            if ($Mode -ceq 'Generate') { [IO.File]::Delete($file) }
        }
    }
    if ($Mode -ceq 'Check' -and $differences.Count -ne 0) { throw "Documentation is stale; run Generate:`n$($differences -join "`n")" }
    $verb = if ($Mode -ceq 'Generate') { 'Generated' } else { 'Verified' }
    $pageCount = @($pages.Keys | Where-Object { $_.EndsWith('.md', [StringComparison]::Ordinal) }).Count
    Write-Host "$verb $pageCount Markdown pages, $($expected.Count) API symbols and $($used.Count) compiled snippets. $($differences.Count) changed files."
}
