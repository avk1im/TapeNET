<#
.SYNOPSIS
    Rearranges the top-level using directives of every C# file in the solution into groups separated by blank lines.

.DESCRIPTION
    Group order (empty groups are left out, one blank line between groups):

        1  System, System.*
        2  Microsoft.*, Windows.*, and every other non-solution namespace
        3  the solution's other libraries (FclNET, HelpNET, AiNET, ...)
        4  TapeLibNET.*  -- canonical layer order (Design-FolderLayout.md section 4)
        5  the owning project's own namespaces

    Then one blank line, the namespace line, two blank lines, the code. No comments are added.

    Only existing using lines move. A trailing comment stays on its line; a comment line inside the using block moves
    with the using that follows it. Group-header comments written by an earlier version of this script are removed.
    The script is idempotent: a second run changes nothing.

    Encoding: every file is read as strict UTF-8 and written back as UTF-8 with exactly the BOM state it had, and with
    its own line ending (CRLF or LF) and final-newline state. A file that is not valid UTF-8 is skipped, never rewritten.

    Skipped (and reported): generated files, files with global usings, preprocessor directives or block comments among
    the usings, files without top-level usings. Not scanned: obj\, bin\, .vs\, "Excluded Files\".

    Within a group: plain usings first, then 'using static', then aliases; TapeLibNET.* in layer order, all else
    alphabetical. Exact duplicate usings are dropped.

.EXAMPLE
    .\Sort-Usings.ps1 -RepoRoot . -DryRun
    .\Sort-Usings.ps1 -RepoRoot . -Path TapeWinNET\ViewModels\MainViewModel.cs
    .\Sort-Usings.ps1 -RepoRoot .
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RepoRoot,
    [string] $Path,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# .NET file APIs resolve relative paths against the PROCESS working directory, not PowerShell's location.
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

$GroupCount = 5

# Comments an earlier version of this script inserted; recognized so they are removed, never duplicated.
$ObsoleteHeaders = @(
    '// standard system namespaces',
    '// Microsoft and 3rd party extensions',
    "// our solution's libraries besides TapeLibNET",
    '// TapeLibNET',
    "// this project's namespaces",
    '// this namespace'
)

# --- TapeLibNET canonical layer order (Design-FolderLayout.md section 4, bottom-up). Unlisted ones follow, alphabetical.
$TapeLibRoot = 'TapeLibNET'
$TapeLibOrder = @(
    'TapeLibNET', 'TapeLibNET.Streams', 'TapeLibNET.Format', 'TapeLibNET.Drive', 'TapeLibNET.Virtual',
    'TapeLibNET.Remote', 'TapeLibNET.Compression', 'TapeLibNET.Headers', 'TapeLibNET.Toc', 'TapeLibNET.Legacy',
    'TapeLibNET.Media', 'TapeLibNET.Packer', 'TapeLibNET.Agents', 'TapeLibNET.Calibration', 'TapeLibNET.Scan',
    'TapeLibNET.Services'
)

$ExcludeRx = '[\\/](obj|bin|\.vs|Excluded Files)[\\/]'
$Strict = [Text.UTF8Encoding]::new($false, $true)          # throws on invalid UTF-8
$UsingRx = [regex]'^\s*using\s+(?<static>static\s+)?(?:(?<alias>\w+)\s*=\s*)?(?<target>(?:global::)?[\w\.]+(?:<[^;]*>)?)\s*;\s*(?<trail>//.*)?$'
$NamespaceRx = [regex]'^\s*namespace\s+[\w\.]+\s*(?<scoped>;)?\s*(\{\s*)?(//.*)?$'

# --- Solution projects: directory -> root namespace ----------------------------------------------------------------
#  A folder holding several .csproj files (a stray copy, "Name(1).csproj") is resolved to the one named like the
#  folder; the others are reported, because a stray project there would claim the folder's files.
$csprojs = @(Get-ChildItem -LiteralPath $RepoRoot -Recurse -Filter *.csproj -File |
    Where-Object { $_.FullName -notmatch $ExcludeRx })
$Projects = foreach ($dirGroup in ($csprojs | Group-Object DirectoryName)) {
    $dirName = Split-Path $dirGroup.Name -Leaf
    $chosen = $dirGroup.Group | Where-Object { $_.BaseName -eq $dirName } | Select-Object -First 1
    if (-not $chosen) { $chosen = $dirGroup.Group | Sort-Object { $_.Name.Length } | Select-Object -First 1 }
    foreach ($other in $dirGroup.Group | Where-Object { $_.FullName -ne $chosen.FullName }) {
        Write-Warning "Folder $($dirGroup.Name) holds several projects; using $($chosen.Name), ignoring $($other.Name)"
    }
    $xml = [IO.File]::ReadAllText($chosen.FullName)
    $rootNs = if ($xml -match '<RootNamespace>\s*([^<\s]+)\s*</RootNamespace>') { $Matches[1] } else { $chosen.BaseName }
    [pscustomobject]@{ Dir = $chosen.DirectoryName.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar; RootNs = $rootNs }
}
$Projects = @($Projects)
$SolutionRoots = @($Projects | ForEach-Object { $_.RootNs } | Where-Object { $_ -match '^[\w\.]+$' } | Sort-Object -Unique)
if (-not $SolutionRoots) { throw "No .csproj found under $RepoRoot" }

function Test-UnderNamespace([string] $ns, [string] $root) {
    return $ns -eq $root -or $ns.StartsWith("$root.", [StringComparison]::Ordinal)
}

function Get-OwnRoot([string] $filePath) {
    $owner = $Projects |
        Where-Object { $filePath.StartsWith($_.Dir, [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object { $_.Dir.Length } -Descending |
        Select-Object -First 1
    if ($owner) { return $owner.RootNs } else { return '' }
}

function Get-Group([string] $ns, [string] $ownRoot) {
    if (Test-UnderNamespace $ns 'System') { return 0 }
    if ($ownRoot -and (Test-UnderNamespace $ns $ownRoot)) { return 4 }
    $sol = $SolutionRoots | Where-Object { Test-UnderNamespace $ns $_ } | Sort-Object Length -Descending | Select-Object -First 1
    if ($sol) {
        if (Test-UnderNamespace $sol $TapeLibRoot) { return 3 } else { return 2 }
    }
    return 1
}

function Get-LayerIndex([string] $ns) {
    $i = [Array]::IndexOf($TapeLibOrder, $ns)
    if ($i -ge 0) { return $i } else { return 999 }
}

function Get-UsingEntry([string] $line) {
    $m = $UsingRx.Match($line)
    if (-not $m.Success) { return $null }
    $ns = ($m.Groups['target'].Value -replace '^global::', '') -replace '<.*$', ''
    $kind = if ($m.Groups['alias'].Success) { 2 } elseif ($m.Groups['static'].Success) { 1 } else { 0 }
    $text = $line.Trim()
    [pscustomobject]@{
        Ns       = $ns
        Kind     = $kind                                    # 0 plain, 1 static, 2 alias
        SortName = if ($kind -eq 2) { $m.Groups['alias'].Value } else { $ns }
        Text     = $text
        Key      = (($text -replace '\s*//.*$', '') -replace '\s+', ' ')
        Comments = @()
        Group    = -1
    }
}

# Returns 'changed', 'unchanged', 'none', or 'skip: <reason>'
function Format-File([IO.FileInfo] $file) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    if ($bytes.Length -eq 0) { return 'none' }
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $offset = if ($hasBom) { 3 } else { 0 }
    try { $text = $Strict.GetString($bytes, $offset, $bytes.Length - $offset) }
    catch { return 'skip: not valid UTF-8' }

    if ($text -match '<auto-generated') { return 'skip: auto-generated' }

    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $endsWithNl = $text.EndsWith("`n")
    $lines = [Collections.Generic.List[string]]::new([string[]]($text -split "\r?\n"))
    if ($endsWithNl) { $lines.RemoveAt($lines.Count - 1) }

    # 1. First top-level using. Header comments, blanks and directives such as #nullable may precede it.
    $first = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match '^\s*$' -or $l -match '^\s*//') { continue }
        if ($l -match '^\s*global\s+using\b') { return 'skip: global usings' }
        if (Get-UsingEntry $l) { $first = $i; break }
        if ($l -match '^\s*#') { continue }
        break                                               # namespace or code before any using
    }
    if ($first -lt 0) { return 'none' }

    # 2. The using block: usings, blanks and // comments, up to the first other line.
    $entries = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $pending = [Collections.Generic.List[string]]::new()
    $ownRoot = Get-OwnRoot $file.FullName
    $stop = $lines.Count
    for ($i = $first; $i -lt $lines.Count; $i++) {
        $l = $lines[$i]
        if ($l -match '^\s*$') { continue }
        if ($l -match '^\s*//') {
            if ($ObsoleteHeaders -notcontains $l.Trim()) { $pending.Add($l.Trim()) }
            continue
        }
        if ($l -match '^\s*#') { return 'skip: preprocessor directive among usings' }
        if ($l -match '^\s*/\*') { return 'skip: block comment among usings' }
        if ($l -match '^\s*global\s+using\b') { return 'skip: global usings' }
        $e = Get-UsingEntry $l
        if ($e) {
            $e.Comments = $pending.ToArray()
            $pending.Clear()
            $e.Group = Get-Group $e.Ns $ownRoot
            if ($seen.Add($e.Key)) { $entries.Add($e) }
            continue
        }
        $stop = $i
        break
    }
    $tailComments = $pending.ToArray()                      # comments after the last using

    # 3. Leading region: everything before the first using, minus trailing blanks and obsolete headers.
    $leading = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $first; $i++) { $leading.Add($lines[$i]) }
    while ($leading.Count -gt 0 -and ($leading[$leading.Count - 1] -match '^\s*$' -or
           $ObsoleteHeaders -contains $leading[$leading.Count - 1].Trim())) {
        $leading.RemoveAt($leading.Count - 1)
    }

    # 4. Compose.
    $out = [Collections.Generic.List[string]]::new()
    foreach ($l in $leading) { $out.Add($l) }
    if ($out.Count -gt 0) { $out.Add('') }

    $wroteGroup = $false
    for ($g = 0; $g -lt $GroupCount; $g++) {
        $items = @($entries | Where-Object { $_.Group -eq $g } |
            Sort-Object -Property @{ Expression = { $_.Kind } },
                                  @{ Expression = { Get-LayerIndex $_.Ns } },
                                  @{ Expression = { $_.SortName } })
        if ($items.Count -eq 0) { continue }
        if ($wroteGroup) { $out.Add('') }                   # one blank line between groups
        foreach ($it in $items) {
            foreach ($c in $it.Comments) { $out.Add($c) }
            $out.Add($it.Text)
        }
        $wroteGroup = $true
    }
    $out.Add('')
    if ($tailComments.Count -gt 0) {
        foreach ($c in $tailComments) { $out.Add($c) }
        $out.Add('')
    }

    # The namespace line may be preceded by an obsolete '// this namespace' header in the original: drop it.
    while ($stop -lt $lines.Count -and $ObsoleteHeaders -contains $lines[$stop].Trim()) { $stop++ }

    $nsMatch = if ($stop -lt $lines.Count) { $NamespaceRx.Match($lines[$stop]) } else { $null }
    if ($nsMatch -and $nsMatch.Success -and $nsMatch.Groups['scoped'].Success) {
        # File-scoped namespace: the namespace line, two blank lines, the code.
        $out.Add($lines[$stop].TrimEnd())
        $j = $stop + 1
        while ($j -lt $lines.Count -and $lines[$j] -match '^\s*$') { $j++ }
        if ($j -lt $lines.Count) { $out.Add(''); $out.Add('') }
        for (; $j -lt $lines.Count; $j++) { $out.Add($lines[$j]) }
    }
    else {
        # Block-scoped namespace, top-level statements or assembly attributes: the rest follows unchanged.
        $j = $stop
        while ($j -lt $lines.Count -and $lines[$j] -match '^\s*$') { $j++ }
        if ($j -ge $lines.Count) { $out.RemoveAt($out.Count - 1) }   # nothing follows: no trailing blank
        for (; $j -lt $lines.Count; $j++) { $out.Add($lines[$j]) }
    }

    $newText = [string]::Join($nl, $out)
    if ($endsWithNl) { $newText += $nl }
    if ($newText -ceq $text) { return 'unchanged' }

    if (-not $DryRun) {
        $body = [Text.UTF8Encoding]::new($false).GetBytes($newText)
        if ($hasBom) { $body = [byte[]](0xEF, 0xBB, 0xBF) + $body }
        [IO.File]::WriteAllBytes($file.FullName, $body)
    }
    return 'changed'
}

# --- Run ------------------------------------------------------------------------------------------------------------
$scope = if ($Path) { (Resolve-Path -LiteralPath (Join-Path $RepoRoot $Path)).ProviderPath } else { $RepoRoot }
#$files = if (Test-Path -LiteralPath $scope -PathType Leaf) { @(Get-Item -LiteralPath $scope) }
#         else { @(Get-ChildItem -LiteralPath $scope -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch $ExcludeRx }) }
# @( ) around the whole if: PowerShell unwraps a branch's result, so a single file would otherwise be a bare FileInfo.
$files = @(
    if (Test-Path -LiteralPath $scope -PathType Leaf) { Get-Item -LiteralPath $scope }
    else { Get-ChildItem -LiteralPath $scope -Recurse -Filter *.cs -File | Where-Object { $_.FullName -notmatch $ExcludeRx } }
)

$stats = [ordered]@{ changed = 0; unchanged = 0; none = 0; skipped = 0 }
foreach ($f in $files) {
    $rel = $f.FullName.Substring($RepoRoot.Length).TrimStart('\', '/')
    $result = Format-File $f
    switch -Wildcard ($result) {
        'changed'   { $stats.changed++;   Write-Host "$(if ($DryRun) { 'would change' } else { 'changed' }): $rel" }
        'unchanged' { $stats.unchanged++ }
        'none'      { $stats.none++ }
        'skip:*'    { $stats.skipped++;   Write-Warning "$rel -- $($result.Substring(6))" }
    }
}

Write-Host ''
Write-Host ("{0} file(s): {1} {2}, {3} already in order, {4} without top-level usings, {5} skipped" -f
    $files.Count, $stats.changed, $(if ($DryRun) { 'to change' } else { 'changed' }),
    $stats.unchanged, $stats.none, $stats.skipped) -ForegroundColor Green
if (-not $DryRun -and $stats.changed -gt 0) {
    Write-Host 'Review with: git diff --stat ; git diff -- <file>   then build.' -ForegroundColor Green
}
