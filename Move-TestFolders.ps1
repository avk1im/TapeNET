<#
.SYNOPSIS
    TapeLibNET.Tests folder layout (mirrors Design-FolderLayout.md): moves test files into layer folders, rewrites
    their namespaces, and injects candidate usings that `dotnet format` then prunes per file.

.DESCRIPTION
    Rule: a test file lives in the folder of the class it EXERCISES (whose methods it calls), not every class it
    touches. Whole-workflow tests across layers go to Scenarios\.

    Phase Move    git mv every file of the layout table into its folder and rewrite its namespace line
                  (file-scoped or block-scoped) to TapeLibNET.Tests.<Folder>. TapeFilePacker\ becomes Packer\.
                  Also reports: root test files the table does not cover, csproj / runsettings references to moved
                  files, and partly qualified names (e.g. "Virtual.X") that the new test namespaces would shadow.

    Phase Usings  Inserts `using TapeLibNET.Tests.<Folder>;` for every new test folder into every .cs file of
                  TapeLibNET.Tests (skipping obj/bin, the file's own namespace, and usings already present).
                  Afterwards run:  dotnet format style <solution> --diagnostics IDE0005 --severity info

    -RepoRoot points at the folder holding TapeLibNET.Tests\. Requires a clean working tree for Move.

.EXAMPLE
    .\Move-TestFolders.ps1 -RepoRoot . -Phase Move -DryRun
    .\Move-TestFolders.ps1 -RepoRoot . -Phase Move
    .\Move-TestFolders.ps1 -RepoRoot . -Phase Usings -DryRun
    .\Move-TestFolders.ps1 -RepoRoot . -Phase Usings
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RepoRoot,
    [Parameter(Mandatory)] [ValidateSet('Move', 'Usings')] [string] $Phase,
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# -- The layout table ------------------------------------------------------------------------------------------
#  Folder name == namespace suffix. Entries are paths RELATIVE TO TapeLibNET.Tests\ (most sit in its root).
#  Files not listed stay where they are.
$Layout = [ordered]@{
    'Format'      = @('FormatCoreTests.cs', 'FormatCoreCoverageTests.cs')
    'Headers'     = @('TapeFileHeaderTests.cs', 'TapeHeaderFormatTests.cs', 'TapeHeaderRoundTripTests.cs',
                      'TapeMediaIdentifyTests.cs', 'TapeSetHeaderTests.cs', 'TapeSetHeaderFactoryTests.cs',
                      'TapeSetHeaderBlockIoTests.cs')
    'Toc'         = @('TapeTOCFormatTests.cs', 'TapeTOCFormatMigrationTests.cs', 'TapeTOCRoundTripTests.cs')
    'Legacy'      = @('LegacyGoldenTests.cs', 'LegacyTimeReaderTests.cs', 'LegacyVirtualGoldenTests.cs',
                      'GoldenGenerator.cs')
    'Drive'       = @('BufferedTapeStreamTests.cs', 'TapeStreamBufferTests.cs')
    'Virtual'     = @('VirtualDriveBasicTests.cs', 'VirtualDriveEarlyWarningTests.cs',
                      'VirtualMediaFaultInjectionTests.cs', 'VirtualMediaStateTests.cs',
                      'VirtualTapeDriveBackendLoadTests.cs', 'VirtualTapeMediaTests.cs',
                      'StrictWritePositionTests.cs')
    'Remote'      = @('RemoteBackendTests.cs')
    'Compression' = @('CompressionRoundTripTests.cs')
    'Media'       = @('TapeNavigatorTests.cs', 'TapeMediaLayoutTests.cs', 'TapeStreamManagerTests.cs',
                      'TapeSetNavigationRecoveryTests.cs')
    'Packer'      = @('TapeFilePacker/PooledTestBuffers.cs', 'TapeFilePacker/TapeFilePipelinedReaderTests.cs',
                      'TapeFilePacker/TapeFileWritePackerTests.cs', 'TapeFilePacker/TapeReadBackendTests.cs',
                      'TapeFilePacker/TapeWriteBackendTests.cs', 'TapeFilePacker/WorkerThreadTapeReadBackendTests.cs')
    'Agents'      = @('TapeBackupAgentTests.cs', 'TapeBackupAgentPackedTests.cs', 'TapeRestoreAgentTests.cs',
                      'TapeRestoreAgentPackedTests.cs', 'TapeRestoreAgentPipelinedTests.cs', 'BackupStreamTests.cs',
                      'TapeHeaderAgentTests.cs', 'TapeSetHeaderAccountingTests.cs', 'TapeSetHeaderCorrectionTests.cs',
                      'TapeSetHeaderVerifyTests.cs', 'TapeSetHeaderWriteTests.cs',
                      'TapeSetHeaderWriteVerificationTests.cs', 'TapeSetNotificationTests.cs',
                      'TapeSetDeleteVerificationTests.cs', 'DeleteSetsTests.cs', 'StatisticsTests.cs',
                      'ErrorHandlingTests.cs')
    'Calibration' = @('CalibrationAndLogicalEwTests.cs', 'CalibrationFormatTests.cs', 'CalibrationResumeTests.cs')
    'Scan'        = @('TapeScannerTests.cs', 'TapeScannerHarvestTests.cs', 'TapeScannerCalibrationTests.cs')
    'Scenarios'   = @('BackupRestoreRoundTripTests.cs', 'IncrementalBackupRestoreTests.cs',
                      'MultiVolumeBackupRestoreTests.cs', 'LargeFileTests.cs', 'FileEdgeCaseTests.cs',
                      'PartitionsVsSetmarksComparisonTests.cs')
}

# Root files that deliberately stay in the test project root.
$StayInRoot = @('GlobalUsings.cs')

# .NET file APIs resolve relative paths against the PROCESS working directory, which Set-Location does not update.
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

$TestProject = 'TapeLibNET.Tests'
$TestRoot = Join-Path $RepoRoot $TestProject
if (-not (Test-Path (Join-Path $TestRoot "$TestProject.csproj"))) {
    throw "$TestProject\$TestProject.csproj not found under $RepoRoot"
}

function Assert-CleanTree {
    $status = git -C $RepoRoot status --porcelain
    if ($status) { throw "Working tree not clean -- commit or stash first:`n$status" }
}

function Get-SourceFiles([string] $dir) {
    Get-ChildItem -Path $dir -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
}

function Test-HasBom([string] $path) {
    $bytes = [IO.File]::ReadAllBytes($path)
    return $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
}

function Write-Preserving([string] $path, [string] $text) {
    [IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new((Test-HasBom $path)))
}

# ═══ Phase: Move ═══════════════════════════════════════════════════════════════════════════════════════════════
function Invoke-Move {
    if (-not $DryRun) { Assert-CleanTree }

    # 1. Every listed file must exist -- fail early, before anything moves.
    $missing = foreach ($folder in $Layout.Keys) {
        foreach ($rel in $Layout[$folder]) {
            if (-not (Test-Path (Join-Path $TestRoot $rel))) { "$folder <- $rel" }
        }
    }
    if ($missing) { throw "Listed but not found in $TestProject\ (renamed? already moved?):`n  $($missing -join "`n  ")" }

    # 2. Root test files the table does not cover -- decide before the commit.
    $coveredRoot = @($StayInRoot) + @($Layout.Values | ForEach-Object { $_ } | Where-Object { $_ -notmatch '[\\/]' })
    $uncovered = Get-ChildItem -Path $TestRoot -Filter *.cs -File | Where-Object { $coveredRoot -notcontains $_.Name }
    foreach ($f in $uncovered) { Write-Warning "Root test file not in the layout table: $($f.Name)" }

    # 3. Move + rewrite the namespace line: TapeLibNET.Tests or TapeLibNET.Tests.<old sub-namespace> -> new.
    $nsPattern = '^(?<lead>\s*namespace\s+)TapeLibNET\.Tests(\.[\w\.]+)?(?<tail>\s*[;{])'
    foreach ($folder in $Layout.Keys) {
        $destDir = Join-Path $TestRoot $folder
        if (-not (Test-Path $destDir) -and -not $DryRun) { New-Item -ItemType Directory -Path $destDir | Out-Null }

        foreach ($rel in $Layout[$folder]) {
            $leaf = Split-Path $rel -Leaf
            $src = "$TestProject/$($rel -replace '\\', '/')"
            $dst = "$TestProject/$folder/$leaf"
            Write-Host "git mv $src -> $dst"
            if ($DryRun) { continue }

            git -C $RepoRoot mv $src $dst
            if ($LASTEXITCODE -ne 0) { throw "git mv failed for $src" }

            $path = Join-Path $RepoRoot $dst
            $text = [IO.File]::ReadAllText($path)
            $newNs = "TapeLibNET.Tests.$folder"
            $rewritten = [regex]::Replace($text, $nsPattern, "`${lead}$newNs`${tail}",
                [Text.RegularExpressions.RegexOptions]::Multiline)
            if ($rewritten -eq $text) {
                Write-Warning "  no 'namespace TapeLibNET.Tests...' line rewritten in $dst -- check it by hand"
            } else {
                Write-Preserving $path $rewritten
            }
        }
    }

    # 4. The old TapeFilePacker folder, once empty.
    $oldPacker = Join-Path $TestRoot 'TapeFilePacker'
    if (-not $DryRun -and (Test-Path $oldPacker) -and -not (Get-ChildItem $oldPacker -Force)) {
        Remove-Item $oldPacker
        Write-Host "removed empty $TestProject\TapeFilePacker\"
    }

    # 5. References that a move can break silently: project files and test filters.
    $moved = @($Layout.Values | ForEach-Object { Split-Path $_ -Leaf }) + 'TapeFilePacker'
    $refFiles = Get-ChildItem -Path $RepoRoot -Recurse -Include *.csproj, *.runsettings, *.props, *.targets -File |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
    foreach ($ref in $refFiles) {
        $content = [IO.File]::ReadAllText($ref.FullName)
        foreach ($name in $moved) {
            if ($content -match [regex]::Escape([IO.Path]::GetFileNameWithoutExtension($name))) {
                Write-Warning "$($ref.Name) mentions $name -- fix the path or test filter"
            }
        }
    }

    # 6. Shadowing: inside TapeLibNET.Tests.*, "Virtual.X" now binds to TapeLibNET.Tests.Virtual first.
    $folders = ($Layout.Keys + 'Services', 'Physical', 'Helpers' | Sort-Object -Unique) -join '|'
    $shadowRx = "(?<![\w\.])(?:$folders)\.[A-Z]\w*"
    foreach ($file in Get-SourceFiles $TestRoot) {
        $n = 0
        foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
            $n++
            if ($line -match '^\s*(using|namespace)\b' -or $line -match '^\s*//') { continue }
            if ($line -match $shadowRx) {
                Write-Warning "Possible shadowed name '$($Matches[0])' in $($file.Name):$n -- qualify as TapeLibNET.<Layer>.… if the build complains"
            }
        }
    }

    Write-Host "`nDone. Next: -Phase Usings, then build." -ForegroundColor Green
}

# ═══ Phase: Usings ═════════════════════════════════════════════════════════════════════════════════════════════
function Invoke-Usings {
    $candidates = @($Layout.Keys | ForEach-Object { "TapeLibNET.Tests.$_" })

    $count = 0
    foreach ($file in Get-SourceFiles $TestRoot) {
        $text = [IO.File]::ReadAllText($file.FullName)

        # The file's own namespace never needs a using; nor do files with global usings only.
        $ownNs = if ($text -match '(?m)^\s*namespace\s+([\w\.]+)') { $Matches[1] } else { '' }
        if (-not $ownNs) { continue }

        $toAdd = $candidates | Where-Object {
            $_ -ne $ownNs -and $text -notmatch "(?m)^\s*(global\s+)?using\s+$([regex]::Escape($_))\s*;"
        }
        if (-not $toAdd) { continue }

        $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
        $block = ($toAdd | ForEach-Object { "using $_;" }) -join $nl
        $lines = [Collections.Generic.List[string]]::new([string[]]($text -split "\r?\n"))

        # Insert before the first using; else before the namespace; else at the top.
        $at = $lines.FindIndex([Predicate[string]] { param($l) $l -match '^\s*using\s+[\w\.]+\s*;' })
        if ($at -lt 0) { $at = $lines.FindIndex([Predicate[string]] { param($l) $l -match '^\s*namespace\s' }) }
        if ($at -lt 0) { $at = 0 }
        $lines.Insert($at, $block)

        if ($DryRun) {
            Write-Host "would inject $($toAdd.Count) using(s): $($file.FullName.Substring($RepoRoot.Length + 1))"
        } else {
            Write-Preserving $file.FullName ($lines -join $nl)
        }
        $count++
    }
    Write-Host "$TestProject : candidates $(if ($DryRun) { 'to inject into' } else { 'injected into' }) $count file(s)"

    $text = @'

Done. Next:
  1. dotnet build                      (fix shadowed names the build reports)
  2. dotnet format style <solution>.sln --diagnostics IDE0005 --severity info
                                       (prunes every unused using, file by file)
  3. .\Sort-Usings.ps1 -RepoRoot . -Path TapeLibNET.Tests
  4. dotnet build ; dotnet test
'@
    Write-Host $text -ForegroundColor Green
}

switch ($Phase) {
    'Move'   { Invoke-Move }
    'Usings' { Invoke-Usings }
}
