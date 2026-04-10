<#
.SYNOPSIS
    Runs TapeLibNET physical tape tests against real hardware.

.DESCRIPTION
    Self-contained script that runs all physical test layers sequentially,
    captures per-test output to timestamped log files, and produces a summary.

    Designed for unattended execution — no terminal timeouts, no orphan processes.

    Partition mode: On drives that support initiator partitions, the script can
    run the full suite twice — once with partition (TOC-in-partition) and once
    without (TOC-in-set) — to exercise both TOC navigator code paths.

    Requirements:
    - Run from an ELEVATED (Administrator) PowerShell
    - Physical tape drive attached with media inserted
    - Solution already built (uses --no-build by default)

.PARAMETER DriveNumber
    Tape drive number (default: 0 = \\.\TAPE0).

.PARAMETER Layers
    Which test layers to run. Default: all three.
    Values: Conformance, Scenario, Error, or any combination.

.PARAMETER PartitionMode
    Controls partition formatting across the entire test run.
    - Both (default): runs the full suite twice — with and without partition.
      On drives that don't support partitions both passes format identically.
    - Partition: runs only with partition (original behavior).
    - NoPartition: runs only without partition.

.PARAMETER Build
    If specified, builds the solution before running tests.

.PARAMETER OutputDir
    Directory for log files. Default: .\TestResults\Physical_<timestamp>

.PARAMETER Filter
    Optional additional dotnet test filter expression (ANDed with layer filter).

.EXAMPLE
    .\Run-PhysicalTests.ps1
    # Runs all layers on drive 0, both partition modes

.EXAMPLE
    .\Run-PhysicalTests.ps1 -PartitionMode Partition
    # Runs all layers with partition only (original behavior)

.EXAMPLE
    .\Run-PhysicalTests.ps1 -Layers Scenario -DriveNumber 1
    # Runs only scenario tests on drive 1, both partition modes

.EXAMPLE
    .\Run-PhysicalTests.ps1 -Layers Conformance,Scenario -PartitionMode NoPartition
    # Runs conformance then scenario tests, without partition
#>
[CmdletBinding()]
param(
    [uint32]$DriveNumber = 0,

    [ValidateSet("Conformance", "Scenario", "Error")]
    [string[]]$Layers = @("Conformance", "Scenario", "Error"),

    [ValidateSet("Both", "Partition", "NoPartition")]
    [string]$PartitionMode = "Both",

    [switch]$Build,

    [string]$OutputDir,

    [string]$Filter
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Check elevation ──────────────────────────────────────────────────────────
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be run as Administrator (tape drive access requires elevation)."
    exit 1
}

# ── Paths ────────────────────────────────────────────────────────────────────
$solutionDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$testProject = "TapeLibNET.Tests"
$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"

if (-not $OutputDir) {
    $OutputDir = Join-Path $solutionDir "TestResults\Physical_$timestamp"
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

# ── Environment ──────────────────────────────────────────────────────────────
$env:TAPELIBNET_PHYSICAL_DRIVES = "$DriveNumber"

# ── Layer → filter mapping ───────────────────────────────────────────────────
$layerMap = @{
    Conformance = "FullyQualifiedName~PhysicalConformanceTests"
    Scenario    = "FullyQualifiedName~PhysicalScenarioTests"
    Error       = "FullyQualifiedName~PhysicalErrorTests"
}

# ── Build (optional) ────────────────────────────────────────────────────────
if ($Build) {
    Write-Host "`n=== Building solution ===" -ForegroundColor Cyan
    Push-Location $solutionDir
    dotnet build $testProject --configuration Debug --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed (exit code $LASTEXITCODE)"
        exit 1
    }
    Pop-Location
    Write-Host "Build succeeded" -ForegroundColor Green
}

# ── Determine partition passes ───────────────────────────────────────────────
$passes = switch ($PartitionMode) {
    "Both"        { @("Partition", "NoPartition") }
    "Partition"   { @("Partition") }
    "NoPartition" { @("NoPartition") }
}
$multiPass = $passes.Count -gt 1

# ── Run passes ───────────────────────────────────────────────────────────────
$results = @()
$overallStart = Get-Date

Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  TapeLibNET Physical Tests — Drive #$DriveNumber                       ║" -ForegroundColor Cyan
Write-Host   "║  Layers: $($Layers -join ', ')$((' ' * (51 - ($Layers -join ', ').Length)))║" -ForegroundColor Cyan
Write-Host   "║  Partition: $PartitionMode$((' ' * (48 - $PartitionMode.Length)))║" -ForegroundColor Cyan
Write-Host   "║  Output: $($OutputDir | Split-Path -Leaf)            ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

foreach ($pass in $passes) {
    # ── Clean up between passes ──────────────────────────────────────────
    #  Kill leftover testhost processes and give the tape driver extra time
    #  to release the device handle before the next pass opens it.
    Get-Process -Name "testhost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    if ($pass -ne $passes[0]) {
        Write-Host "`nWaiting for tape driver to release handle..." -ForegroundColor DarkGray
        Start-Sleep -Seconds 10
    } else {
        Start-Sleep -Seconds 2
    }

    # ── Set/clear the no-partition env var ────────────────────────────────
    if ($pass -eq "NoPartition") {
        $env:TAPELIBNET_PHYSICAL_NO_PARTITION = "1"
    } else {
        Remove-Item Env:\TAPELIBNET_PHYSICAL_NO_PARTITION -ErrorAction SilentlyContinue
    }

    # ── Pass output directory ────────────────────────────────────────────
    $passDir = if ($multiPass) { Join-Path $OutputDir $pass } else { $OutputDir }
    New-Item -ItemType Directory -Path $passDir -Force | Out-Null

    Write-Host "`n═══ Pass: $pass ═══" -ForegroundColor Magenta

    foreach ($layer in $Layers) {
        $filterExpr = $layerMap[$layer]
        if ($Filter) {
            $filterExpr = "($filterExpr) & ($Filter)"
        }

        $logFile = Join-Path $passDir "${layer}.log"
        $errFile = Join-Path $passDir "${layer}_stderr.log"

        Write-Host "`n─── Layer: $layer ($pass) ───" -ForegroundColor Yellow
        Write-Host "Filter: $filterExpr"
        Write-Host "Log:    $logFile"

        $layerStart = Get-Date

        # Run dotnet test as a child process with full output capture
        $proc = Start-Process -PassThru -NoNewWindow `
            -FilePath "dotnet" `
            -ArgumentList "test $testProject --filter `"$filterExpr`" --logger `"console;verbosity=detailed`" --no-build" `
            -WorkingDirectory $solutionDir `
            -RedirectStandardOutput $logFile `
            -RedirectStandardError $errFile

        # Wait up to 60 minutes per layer
        $finished = $proc.WaitForExit(3600000)

        $layerDuration = (Get-Date) - $layerStart

        if (-not $finished) {
            Write-Host "  TIMEOUT after 60 minutes — killing" -ForegroundColor Red
            $proc | Stop-Process -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 5
            $results += [PSCustomObject]@{
                Pass     = $pass
                Layer    = $layer
                Status   = "TIMEOUT"
                Duration = $layerDuration.ToString("hh\:mm\:ss")
                Passed   = 0; Failed = 0; Skipped = 0
            }
            continue
        }

        # Parse results from stdout and stderr (dotnet test writes the summary
        #  line to stderr when stdout is redirected)
        $logContent = Get-Content $logFile -Raw -ErrorAction SilentlyContinue
        $errContent = Get-Content $errFile -Raw -ErrorAction SilentlyContinue
        $passed  = 0; $failed = 0; $skipped = 0

        # Try to parse the summary line: "Passed!  - Failed: 0, Passed: 1, ..."
        $summaryPattern = "Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+)"
        if ($logContent -match $summaryPattern) {
            $failed  = [int]$Matches[1]
            $passed  = [int]$Matches[2]
            $skipped = [int]$Matches[3]
        } elseif ($errContent -match $summaryPattern) {
            $failed  = [int]$Matches[1]
            $passed  = [int]$Matches[2]
            $skipped = [int]$Matches[3]
        } else {
            # Fallback: count individual test-result lines from stdout.
            # Real results look like "  Passed TestName [duration]" with a bracket;
            # skip lines like "Failed to initialize..." that lack one.
            $logLines2 = Get-Content $logFile -ErrorAction SilentlyContinue
            if ($logLines2) {
                $passed  = @($logLines2 | Select-String -Pattern "^\s+Passed\s+.+\[").Count
                $failed  = @($logLines2 | Select-String -Pattern "^\s+Failed\s+.+\[").Count
                $skipped = @($logLines2 | Select-String -Pattern "^\s+Skipped\s+.+\[").Count
            }
        }

        $status = if ($failed -gt 0) { "FAILED" }
                  elseif ($passed -eq 0 -and $skipped -gt 0) { "ALL SKIPPED" }
                  elseif ($passed -gt 0 -and $skipped -gt 0) { "PARTIAL" }
                  elseif ($passed -gt 0) { "PASSED" }
                  else { "UNKNOWN" }

        $color = switch ($status) {
            "PASSED"  { "Green" }
            "FAILED"  { "Red" }
            "PARTIAL" { "Yellow" }
            default   { "Gray" }
        }

        Write-Host "  $status — Passed: $passed, Failed: $failed, Skipped: $skipped ($($layerDuration.ToString('hh\:mm\:ss')))" -ForegroundColor $color

        # Show individual test results
        $logLines = Get-Content $logFile -ErrorAction SilentlyContinue
        $logLines | Select-String -Pattern "^\s+(Passed|Failed|Skipped)\s+" | ForEach-Object {
            $line = $_.Line.Trim()
            $lineColor = if ($line -match "^Passed") { "DarkGreen" }
                         elseif ($line -match "^Failed") { "Red" }
                         else { "DarkYellow" }
            Write-Host "    $line" -ForegroundColor $lineColor
        }

        $results += [PSCustomObject]@{
            Pass     = $pass
            Layer    = $layer
            Status   = $status
            Duration = $layerDuration.ToString("hh\:mm\:ss")
            Passed   = $passed; Failed = $failed; Skipped = $skipped
        }

        # Clean up stale processes between layers
        Get-Process -Name "testhost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 3
    }
}

# ── Clean up env var ─────────────────────────────────────────────────────────
Remove-Item Env:\TAPELIBNET_PHYSICAL_NO_PARTITION -ErrorAction SilentlyContinue

# ── Summary ──────────────────────────────────────────────────────────────────
$overallDuration = (Get-Date) - $overallStart
$totalPassed  = ($results | Measure-Object -Property Passed  -Sum).Sum
$totalFailed  = ($results | Measure-Object -Property Failed  -Sum).Sum
$totalSkipped = ($results | Measure-Object -Property Skipped -Sum).Sum

Write-Host "`n╔══════════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  SUMMARY                                                    ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════════════════╝" -ForegroundColor Cyan

$results | Format-Table -AutoSize

$overallStatus = if ($totalFailed -gt 0) { "FAILED" } elseif ($totalPassed -gt 0) { "PASSED" } else { "NO RESULTS" }
$overallColor  = if ($totalFailed -gt 0) { "Red" } elseif ($totalPassed -gt 0) { "Green" } else { "Yellow" }

Write-Host "Overall: $overallStatus — $totalPassed passed, $totalFailed failed, $totalSkipped skipped" -ForegroundColor $overallColor
Write-Host "Duration: $($overallDuration.ToString('hh\:mm\:ss'))" -ForegroundColor Cyan
Write-Host "Logs: $OutputDir" -ForegroundColor Cyan

# Write summary to file
$summaryFile = Join-Path $OutputDir "SUMMARY.txt"
@"
TapeLibNET Physical Test Run
=============================
Date:    $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Drive:   #$DriveNumber (\\.\TAPE$DriveNumber)
Layers:  $($Layers -join ', ')
Partition: $PartitionMode (passes: $($passes -join ', '))
Duration: $($overallDuration.ToString('hh\:mm\:ss'))
Result:  $overallStatus — $totalPassed passed, $totalFailed failed, $totalSkipped skipped

Per-Pass/Layer Results:
$($results | Format-Table -AutoSize | Out-String)
"@ | Out-File -Encoding utf8 $summaryFile

Write-Host "`nSummary written to: $summaryFile" -ForegroundColor DarkGray
