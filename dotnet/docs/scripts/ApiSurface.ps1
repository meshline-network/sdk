# Metadata is read without loading or executing SDK implementation code.
function Get-ApiTypeName([Type]$Type) {
    if ($Type.IsByRef) { return (Get-ApiTypeName $Type.GetElementType()) + '@' }
    if ($Type.IsPointer) { return (Get-ApiTypeName $Type.GetElementType()) + '*' }
    if ($Type.IsArray) {
        $suffix = if ($Type.GetArrayRank() -eq 1) { '[]' } else { '[' + ((@('0:') * $Type.GetArrayRank()) -join ',') + ']' }
        return (Get-ApiTypeName $Type.GetElementType()) + $suffix
    }
    if ($Type.IsGenericParameter) {
        $prefix = if ($null -eq $Type.DeclaringMethod) { '`' } else { '``' }
        return $prefix + $Type.GenericParameterPosition
    }
    $name = ($Type.FullName ?? $Type.Name).Replace('+', '.')
    if (!$Type.IsGenericType) { return $name }
    $name = $Type.GetGenericTypeDefinition().FullName.Replace('+', '.')
    return $name.Substring(0, $name.IndexOf('`')) + '{' + (($Type.GetGenericArguments() | ForEach-Object { Get-ApiTypeName $_ }) -join ',') + '}'
}

function Get-ApiParameters($Parameters) {
    if ($Parameters.Count -eq 0) { return '' }
    return '(' + (($Parameters | ForEach-Object { Get-ApiTypeName $_.ParameterType }) -join ',') + ')'
}

function Get-ApiMemberId([Reflection.MemberInfo]$Member) {
    if ($Member -is [Type]) { return 'T:' + $Member.FullName.Replace('+', '.') }
    $name = $Member.DeclaringType.FullName.Replace('+', '.') + '.' + $Member.Name.Replace('.', '#')
    if ($Member -is [Reflection.MethodBase]) {
        if ($Member.IsGenericMethod) { $name += '``' + $Member.GetGenericArguments().Count }
        $name += Get-ApiParameters $Member.GetParameters()
        if ($Member.Name -cin @('op_Implicit', 'op_Explicit')) { $name += '~' + (Get-ApiTypeName $Member.ReturnType) }
        return 'M:' + $name
    }
    if ($Member -is [Reflection.PropertyInfo]) { return 'P:' + $name + (Get-ApiParameters $Member.GetIndexParameters()) }
    if ($Member -is [Reflection.EventInfo]) { return 'E:' + $name }
    if ($Member -is [Reflection.FieldInfo]) { return 'F:' + $name }
    throw "Unsupported API member: $Member"
}

function Test-ApiMethodAccessible([Reflection.MethodBase]$Method) {
    return $Method.IsPublic -or (!$Method.DeclaringType.IsSealed -and ($Method.IsFamily -or $Method.IsFamilyOrAssembly))
}

function Test-ApiAccessible([Reflection.MemberInfo]$Member) {
    if ($Member -is [Type]) { return $Member.IsVisible }
    if ($Member -is [Reflection.PropertyInfo]) {
        return @($Member.GetAccessors($true) | Where-Object { Test-ApiMethodAccessible $_ }).Count -gt 0
    }
    if ($Member -is [Reflection.EventInfo]) {
        return $null -ne $Member.AddMethod -and (Test-ApiMethodAccessible $Member.AddMethod)
    }
    $accessible = $Member.IsPublic -or (!$Member.DeclaringType.IsSealed -and ($Member.IsFamily -or $Member.IsFamilyOrAssembly))
    if ($Member -is [Reflection.MethodBase]) {
        return $accessible -and (!$Member.IsSpecialName -or $Member.IsConstructor -or $Member.Name.StartsWith('op_', [StringComparison]::Ordinal))
    }
    return $Member -is [Reflection.FieldInfo] -and $accessible -and !$Member.IsSpecialName
}

function Test-ApiGenerated([Reflection.MemberInfo]$Member) {
    if ($Member.Name.Contains('<')) { return $true }
    return @($Member.GetCustomAttributesData() | Where-Object {
        $_.AttributeType.FullName -ceq 'System.Runtime.CompilerServices.CompilerGeneratedAttribute'
    }).Count -gt 0
}

function Get-ApiBaseMemberId([Reflection.MemberInfo]$Member) {
    if ($Member -is [Type] -and $null -ne $Member.BaseType) { return Get-ApiMemberId $Member.BaseType }
    if ($Member -isnot [Reflection.MethodInfo] -or !$Member.IsVirtual -or
        ($Member.Attributes -band [Reflection.MethodAttributes]::NewSlot)) { return $null }

    # MetadataLoadContext does not implement GetBaseDefinition. Match the virtual
    # signature up the base chain, stopping at the slot's original declaration.
    $signature = Get-ApiParameters $Member.GetParameters()
    $arity = $Member.GetGenericArguments().Count
    $baseType = $Member.DeclaringType.BaseType
    $source = $null
    while ($null -ne $baseType) {
        $matches = @($baseType.GetMethods([Reflection.BindingFlags]'Public,NonPublic,Instance,DeclaredOnly') | Where-Object {
            $_.Name -ceq $Member.Name -and $_.IsVirtual -and $_.GetGenericArguments().Count -eq $arity -and
            (Get-ApiParameters $_.GetParameters()) -ceq $signature
        })
        if ($matches.Count -gt 1) { throw "Ambiguous inherited API: $Member" }
        if ($matches.Count -eq 1) {
            $source = Get-ApiMemberId $matches[0]
            if ($matches[0].Attributes -band [Reflection.MethodAttributes]::NewSlot) { break }
        }
        $baseType = $baseType.BaseType
    }
    return $source
}

function Merge-ApiComments([Xml.XmlElement]$Target, [Xml.XmlElement]$Source) {
    foreach ($inherited in $Source.ChildNodes) {
        if ($inherited -isnot [Xml.XmlElement] -or $inherited.LocalName -ceq 'inheritdoc') { continue }
        $key = if ($inherited.HasAttribute('name')) { $inherited.GetAttribute('name') } else { $inherited.GetAttribute('cref') }
        $local = @($Target.ChildNodes | Where-Object {
            $_ -is [Xml.XmlElement] -and $_.LocalName -ceq $inherited.LocalName -and
            $(if ($_.HasAttribute('name')) { $_.GetAttribute('name') } else { $_.GetAttribute('cref') }) -ceq $key
        }) | Select-Object -First 1
        if ($null -eq $local) { [void]$Target.AppendChild($Target.OwnerDocument.ImportNode($inherited, $true)) }
        elseif ($inherited.LocalName -ceq 'exception' -and $local.InnerText -cne $inherited.InnerText) {
            [void]$local.AppendChild($Target.OwnerDocument.CreateTextNode(' '))
            foreach ($node in $inherited.ChildNodes) { [void]$local.AppendChild($Target.OwnerDocument.ImportNode($node, $true)) }
        }
    }
}

function Expand-ApiComment([string]$Id, $Comments, $Members, $Visiting) {
    $element = $Comments[$Id]
    $inherit = $element.SelectSingleNode('inheritdoc')
    if ($null -eq $inherit) { return }
    if (!$Visiting.Add($Id)) { throw "Cyclic inheritdoc: $Id" }
    $source = $inherit.GetAttribute('cref')
    if (!$source -and $Members.ContainsKey($Id)) { $source = Get-ApiBaseMemberId $Members[$Id] }
    if ($source -and $Comments.ContainsKey($source)) {
        Expand-ApiComment $source $Comments $Members $Visiting
        Merge-ApiComments $element $Comments[$source]
    }
    elseif ($Id.Contains('Meshline.Storage.Migrations.', [StringComparison]::Ordinal)) {
        $summary = $element.OwnerDocument.CreateElement('summary')
        $summary.InnerText = 'Entity Framework Core migration infrastructure. Applications apply the schema through MeshlineDatabase.MigrateAsync; do not invoke migration operations directly.'
        [void]$element.AppendChild($summary)
    }
    else { throw "Unresolved inheritdoc for ${Id}: $source" }
    [void]$element.RemoveChild($inherit)
    [void]$Visiting.Remove($Id)
}

function Initialize-ApiDocumentation([string]$AssemblyPath, [string]$DocsRoot, [string]$Work, $References) {
    Add-Type -Path (Join-Path $References.Properties.MSBuildToolsPath 'System.Reflection.MetadataLoadContext.dll')
    $resolver = [Reflection.PathAssemblyResolver]::new([string[]]$References.Items.ReferencePath.Identity)
    $context = [Reflection.MetadataLoadContext]::new($resolver, 'System.Runtime')
    try {
        $assembly = $context.LoadFromAssemblyPath($AssemblyPath)
        $xml = [Xml.XmlDocument]::new()
        $xml.Load([IO.Path]::ChangeExtension($AssemblyPath, '.xml'))
        $comments = [Collections.Generic.Dictionary[string, Xml.XmlElement]]::new([StringComparer]::Ordinal)
        foreach ($element in $xml.SelectNodes('/doc/members/member')) { $comments.Add($element.GetAttribute('name'), $element) }
        $members = [Collections.Generic.Dictionary[string, Reflection.MemberInfo]]::new([StringComparer]::Ordinal)
        $allIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($type in $assembly.GetExportedTypes()) {
            $declared = $type.GetMembers([Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly')
            foreach ($member in $declared) { [void]$allIds.Add((Get-ApiMemberId $member)) }
            if (!$type.Namespace.StartsWith('Meshline', [StringComparison]::Ordinal) -or (Test-ApiGenerated $type)) { continue }
            $candidates = @($type) + @($declared | Where-Object { $_ -isnot [Type] })
            foreach ($member in $candidates) {
                if ((Test-ApiAccessible $member) -and !(Test-ApiGenerated $member)) { $members.Add((Get-ApiMemberId $member), $member) }
            }
        }
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($id in $members.Keys) {
            if ($members[$id] -is [Type] -or $comments.ContainsKey($id)) { [void]$expected.Add($id) }
            if (!$comments.ContainsKey($id) -and $members[$id] -isnot [Reflection.ConstructorInfo]) { throw "Public API lacks XML documentation: $id" }
        }
        foreach ($id in $comments.Keys) { [void]$allIds.Add($id) }
        $excluded = [Collections.Generic.List[string]]::new()
        foreach ($id in $allIds) {
            # DefaultDocumentation consumes NamespaceDoc comments as namespace summaries, not public types.
            if ($expected.Contains($id) -or $id -cmatch '^T:Meshline(?:\.[^.]+)*\.NamespaceDoc$') { continue }
            $excluded.Add('^' + [regex]::Escape($id) + '$')
            if ($comments.ContainsKey($id)) {
                [void]$comments[$id].ParentNode.RemoveChild($comments[$id])
                [void]$comments.Remove($id)
            }
        }
        $visiting = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($id in $comments.Keys) { Expand-ApiComment $id $comments $members $visiting }
        foreach ($id in $comments.Keys) {
            if ([string]::IsNullOrWhiteSpace($comments[$id].SelectSingleNode('summary').InnerText)) { throw "Missing API summary: $id" }
        }
        [void][IO.Directory]::CreateDirectory($Work)
        $xml.Save((Join-Path $Work 'Meshline.Sdk.xml'))
        $config = Get-Content -LiteralPath (Join-Path $DocsRoot 'generator.json') -Raw | ConvertFrom-Json -AsHashtable
        $config['Markdown.Exclude'] = $excluded.ToArray()
        Write-DocumentationFile (Join-Path $Work 'generator.json') ($config | ConvertTo-Json -Depth 20)
        $ids = [string[]]@($expected)
        [Array]::Sort($ids, [StringComparer]::Ordinal)
        $typeCount = @($ids | Where-Object { $_.StartsWith('T:', [StringComparison]::Ordinal) }).Count
        Write-Host "Prepared $($ids.Count) documented API symbols in $typeCount public types."
        return @{ Ids = $ids; Assembly = $assembly.GetName().Name }
    }
    finally { $context.Dispose() }
}
