<#
.SYNOPSIS
    TapeLibNET folder layout (Design-FolderLayout.md): moves files into layer folders, rewrites their namespaces,
    and injects candidate usings that `dotnet format` then prunes per file.

.DESCRIPTION
    Phase Move    git mv every file of the layout table into its folder and rewrite its namespace line
                  (file-scoped or block-scoped) from TapeLibNET to TapeLibNET.<Folder>. Also reports
                  csproj / include-file references to moved files and any root .cs file the table does not cover.

    Phase Usings  Inserts `using TapeLibNET.<Layer>;` for every NEW layer namespace into every .cs file of the
                  given projects that mentions TapeLibNET (skipping obj/bin and usings already present).
                  Afterwards run:  dotnet format style <solution> --diagnostics IDE0005 --severity info
                  which removes the unused ones — each file keeps exactly the usings it needs.

    Run from anywhere; -RepoRoot points at the folder holding TapeLibNET\. Requires a clean working tree.

.EXAMPLE
    .\Move-TapeLibFolders.ps1 -RepoRoot C:\src\TapeNET -Phase Move -DryRun
    .\Move-TapeLibFolders.ps1 -RepoRoot C:\src\TapeNET -Phase Move
    .\Move-TapeLibFolders.ps1 -RepoRoot C:\src\TapeNET -Phase Usings
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $RepoRoot,
    [Parameter(Mandatory)] [ValidateSet('Move', 'Usings')] [string] $Phase,
    [string[]] $Projects = @('TapeLibNET', 'TapeLibNET.Tests', 'TapeWinNET', 'TapeConNET'),
    [switch] $DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── The layout table (Design-FolderLayout.md §3) ───────────────────────────────────────────────────────────────
#  Folder name == namespace suffix. Files not listed stay where they are.
$Layout = [ordered]@{
    'Streams'     = @('TapeCRC.cs', 'KeyedStreamStore.cs')
    'Drive'       = @('TapeDrive.cs', 'TapeDriveBackend.cs', 'TapeDriveWin32Backend.cs',
                      'TapeDriveWin32Backend.lto-direct.cs', 'TapeDriveWin32Backend.Lto.cs',
                      'TapeEarlyWarning.cs', 'TapeStream.cs', 'TapeStreamBuffer.cs', 'TapeWriteBuffer.cs',
                      'BufferedTapeStream.cs')
    'Compression' = @('TapeCompression.cs', 'TapeCompressionStream.cs')
    'Headers'     = @('TapeHeader.cs', 'TapeMediaHeader.cs', 'TapeSetHeader.cs', 'TapeCalibrationHeader.cs',
                      'TapeHeaderBlock.cs', 'TapeHeaderBlock.Identify.cs', 'TapeFramer.cs', 'TapeFileHeader.cs')
    'Toc'         = @('TapeTOC.cs', 'TapeTOC.Format.cs')
    'Media'       = @('TapeNavigator.cs', 'TapeMediaLayout.cs', 'TapeStreamManager.cs', 'TapeState.cs')
    'Agents'      = @('TapeAgentBase.cs', 'TapeAgentBase.Headers.cs', 'TapeAgentBase.SetVerification.cs',
                      'TapeSetAgent.cs', 'TapeFileBackupAgent.cs', 'TapeFileRestoreAgent.cs',
                      'TapeFileRestoreAgentEx.cs', 'ITapeFileNotifiable.cs', 'TapeBackupStream.cs',
                      'TapeFileFilter.cs', 'FileSizeAggregator.cs')
    'Calibration' = @('TapeCalibration.cs', 'TapeCalibrationCheckpoint.cs', 'TapeCalibrationOptions.cs',
                      'TapeCalibrationStore.cs', 'TapeCalibrator.cs')
}

# Root files that deliberately stay in TapeLibNET (shared vocabulary).
$StayInRoot = @('TapeResult.cs', 'TapeError.cs', 'TapeAddress.cs', 'OnceLatch.cs', 'CsWin32Extras.cs', 'FailureSimulator.cs')

# .NET file APIs resolve relative paths against the PROCESS working directory, which PowerShell's Set-Location does
#  not update (often C:\Windows\system32). Make the root absolute once, so git and [IO.File] agree.
$RepoRoot = (Resolve-Path -LiteralPath $RepoRoot).ProviderPath

$LibRoot = Join-Path $RepoRoot 'TapeLibNET'
if (-not (Test-Path (Join-Path $LibRoot 'TapeLibNET.csproj'))) {
    throw "TapeLibNET\TapeLibNET.csproj not found under $RepoRoot"
}

function Assert-CleanTree {
    $status = git -C $RepoRoot status --porcelain
    if ($status) { throw "Working tree not clean -- commit or stash first:`n$status" }
}

function Get-SourceFiles([string] $projectDir) {
    Get-ChildItem -Path $projectDir -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' }
}

# ═══ Phase: Move ═══════════════════════════════════════════════════════════════════════════════════════════════
function Invoke-Move {
    if (-not $DryRun) { Assert-CleanTree }

    # 1. Every listed file must exist in the root — fail early, before anything moves.
    $missing = foreach ($folder in $Layout.Keys) {
        foreach ($file in $Layout[$folder]) {
            if (-not (Test-Path (Join-Path $LibRoot $file))) { "$folder\$file" }
        }
    }
    if ($missing) { throw "Listed but not found in TapeLibNET\ (renamed? already moved?):`n  $($missing -join "`n  ")" }

    # 2. Root files the table does not cover — decide before the commit.
    $covered = @($StayInRoot) + @($Layout.Values | ForEach-Object { $_ })
    $uncovered = Get-ChildItem -Path $LibRoot -Filter *.cs -File | Where-Object { $covered -notcontains $_.Name }
    foreach ($f in $uncovered) { Write-Warning "Root file not in the layout table: $($f.Name)" }

    # 3. Move + rewrite the namespace line.
    $nsPattern = '^(?<lead>\s*namespace\s+)TapeLibNET(?<tail>\s*[;{])'
    foreach ($folder in $Layout.Keys) {
        $destDir = Join-Path $LibRoot $folder
        if (-not (Test-Path $destDir) -and -not $DryRun) { New-Item -ItemType Directory -Path $destDir | Out-Null }

        foreach ($file in $Layout[$folder]) {
            $src = "TapeLibNET/$file"
            $dst = "TapeLibNET/$folder/$file"
            Write-Host "git mv $src -> $dst"
            if ($DryRun) { continue }

            git -C $RepoRoot mv $src $dst
            if ($LASTEXITCODE -ne 0) { throw "git mv failed for $src" }

            $path = Join-Path $RepoRoot $dst
            $text = [IO.File]::ReadAllText($path)
            $newNs = "TapeLibNET.$folder"
            $rewritten = [regex]::Replace($text, $nsPattern, "`${lead}$newNs`${tail}",
                [Text.RegularExpressions.RegexOptions]::Multiline)
            if ($rewritten -eq $text) {
                Write-Warning "  no 'namespace TapeLibNET' line rewritten in $dst — check it by hand"
            } else {
                # Preserve the file's encoding / BOM: write with UTF-8 BOM only if it had one.
                $hadBom = ([IO.File]::ReadAllBytes($path)[0..2] -join ',') -eq '239,187,191'
                [IO.File]::WriteAllText($path, $rewritten, [Text.UTF8Encoding]::new($hadBom))
            }
        }
    }

    # 4. References that a move can break silently.
    $moved = @($Layout.Values | ForEach-Object { $_ })
    foreach ($proj in Get-ChildItem -Path $RepoRoot -Recurse -Filter *.csproj -File) {
        $content = Get-Content $proj.FullName -Raw
        foreach ($name in $moved) {
            if ($content -match [regex]::Escape($name)) {
                Write-Warning "$($proj.Name) mentions $name (DependentUpon / Compile item?) — fix the path"
            }
        }
    }
    $includes = Get-SourceFiles $LibRoot | Select-String -Pattern "include file=" -SimpleMatch
    foreach ($hit in $includes) {
        Write-Warning "XML doc include in $($hit.Path):$($hit.LineNumber) — verify the relative path after the move"
    }

    Write-Host "`nDone. Next: -Phase Usings, then build." -ForegroundColor Green
}

# ═══ Phase: Usings ═════════════════════════════════════════════════════════════════════════════════════════════
function Invoke-Usings {
    $candidates = @($Layout.Keys | ForEach-Object { "TapeLibNET.$_" })

    foreach ($projName in $Projects) {
        $projDir = Join-Path $RepoRoot $projName
        if (-not (Test-Path $projDir)) { Write-Warning "Project folder not found: $projName — skipped"; continue }

        $count = 0
        foreach ($file in Get-SourceFiles $projDir) {
            $text = [IO.File]::ReadAllText($file.FullName)
            if ($text -notmatch 'TapeLibNET') { continue }      # app files unrelated to the library stay untouched

            # The file's own namespace never needs a using.
            $ownNs = if ($text -match '(?m)^\s*namespace\s+([\w\.]+)') { $Matches[1] } else { '' }

            $toAdd = $candidates | Where-Object {
                $_ -ne $ownNs -and $text -notmatch "(?m)^\s*(global\s+)?using\s+$([regex]::Escape($_))\s*;"
            }
            if (-not $toAdd) { continue }

            $block = ($toAdd | ForEach-Object { "using $_;" }) -join "`r`n"
            $lines = [Collections.Generic.List[string]]($text -split "`r?`n")

            # Insert before the first using; else before the namespace; else at the top.
            $at = $lines.FindIndex({ param($l) $l -match '^\s*using\s+[\w\.]+\s*;' })
            if ($at -lt 0) { $at = $lines.FindIndex({ param($l) $l -match '^\s*namespace\s' }) }
            if ($at -lt 0) { $at = 0 }
            $lines.Insert($at, $block)

            if (-not $DryRun) {
                $hadBom = ([IO.File]::ReadAllBytes($file.FullName)[0..2] -join ',') -eq '239,187,191'
                [IO.File]::WriteAllText($file.FullName, ($lines -join "`r`n"), [Text.UTF8Encoding]::new($hadBom))
            }
            $count++
        }
        Write-Host "$projName : candidates injected into $count file(s)"
    }

    $text = @'

Done. Next:
  1. dotnet build                      (fix name clashes the build reports)
  2. dotnet format style <solution>.sln --diagnostics IDE0005 --severity info
                                       (prunes every unused using, file by file)
  3. dotnet build && dotnet test
  If step 2 removes nothing, enable IDE0005 first — see Design-FolderLayout.md §6.
'@
    Write-Host $text -ForegroundColor Green
}

switch ($Phase) {
    'Move'   { Invoke-Move }
    'Usings' { Invoke-Usings }
}
