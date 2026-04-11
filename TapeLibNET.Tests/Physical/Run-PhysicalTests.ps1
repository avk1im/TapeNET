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
# Wrap in @() so single-branch results stay an array (strict mode blocks .Count on scalars)
$passes = @(switch ($PartitionMode) {
    "Both"        { "Partition"; "NoPartition" }
    "Partition"   { "Partition" }
    "NoPartition" { "NoPartition" }
})
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

        # Run dotnet test with real-time output streaming.
        #  Uses [Process] directly instead of Start-Process so we can read
        #  stdout line-by-line and display test results as they complete.
        $psi = [System.Diagnostics.ProcessStartInfo]@{
            FileName               = "dotnet"
            Arguments              = "test $testProject --filter `"$filterExpr`" --logger `"console;verbosity=detailed`" --no-build"
            WorkingDirectory       = $solutionDir
            UseShellExecute        = $false
            RedirectStandardOutput = $true
            RedirectStandardError  = $true
        }

        $proc = [System.Diagnostics.Process]::new()
        $proc.StartInfo = $psi
        $proc.Start() | Out-Null

        # Capture stderr asynchronously to avoid deadlock
        $stderrTask = $proc.StandardError.ReadToEndAsync()

        # Watchdog: kill the process tree after 120 minutes (2 hours).
        #  Uses taskkill /T to kill the dotnet process AND its children (testhost.exe).
        #  Stop-Process alone would kill only dotnet; testhost inherits the stdout pipe
        #  handle, so ReadLine() would keep blocking until testhost exits on its own.
        $timeoutMs = 2 * 3600000
        $watchdog = Start-Job -ScriptBlock {
            param($procId, $ms)
            Start-Sleep -Milliseconds $ms
            try { & taskkill /T /F /PID $procId 2>$null } catch { }
            # instead of:             
            #  try { Stop-Process -Id $procId -Force -ErrorAction SilentlyContinue } catch { }
            #  which would kill dotnet only, not testhost, as explained above
        } -ArgumentList $proc.Id, $timeoutMs

        # Read stdout line-by-line — test results appear as they complete
        $stdoutLines = [System.Collections.Generic.List[string]]::new()
        # Track whether we're inside a failure detail block to show error context
        $inFailureBlock = $false
        while ($null -ne ($line = $proc.StandardOutput.ReadLine())) {
            $stdoutLines.Add($line)

            # Display individual test results in real-time
            if ($line -match '^\s+(Passed|Failed|Skipped)\s+.+\[') {
                $trimmed = $line.Trim()
                $lineColor = if ($trimmed -match '^Passed') { 'DarkGreen' }
                             elseif ($trimmed -match '^Failed') { 'Red' }
                             else { 'DarkYellow' }
                Write-Host "    $trimmed" -ForegroundColor $lineColor
                # Start capturing failure details after a Failed line
                $inFailureBlock = $trimmed -match '^Failed'
            }
            # Show failure context: Error Message and key Standard Output lines
            elseif ($inFailureBlock) {
                if ($line -match '^\s+Error Message:') {
                    Write-Host "      $($line.Trim())" -ForegroundColor Red
                }
                elseif ($line -match '^\s+Stack Trace:') {
                    # Show first stack frame only (the assert location)
                    $inFailureBlock = $false
                }
                elseif ($line -match '^\s{3,}\S') {
                    # Indented content under Error Message (the actual message text)
                    Write-Host "      $($line.Trim())" -ForegroundColor Red
                }
            }
        }

        # stdout EOF — process has closed its output stream
        $proc.WaitForExit()
        $layerDuration = (Get-Date) - $layerStart

        # Detect timeout: if watchdog job completed, it fired and killed the process
        $timedOut = $watchdog.State -eq 'Completed'
        Stop-Job $watchdog -ErrorAction SilentlyContinue
        Remove-Job $watchdog -Force -ErrorAction SilentlyContinue

        # Capture stderr and write both streams to log files
        $stderrContent = $stderrTask.GetAwaiter().GetResult()
        ($stdoutLines -join [Environment]::NewLine) | Set-Content -Path $logFile -Encoding utf8
        if ($stderrContent) {
            $stderrContent | Set-Content -Path $errFile -Encoding utf8
        }

        if ($timedOut) {
            Write-Host "  TIMEOUT after $($timeoutMs / 60000) minutes — killed" -ForegroundColor Red
            $results += [PSCustomObject]@{
                Pass     = $pass
                Layer    = $layer
                Status   = "TIMEOUT"
                Duration = $layerDuration.ToString("hh\:mm\:ss")
                Passed   = 0; Failed = 0; Skipped = 0
            }
            continue
        }

        # Parse results from captured output (no file re-reads needed)
        $logContent = $stdoutLines -join "`n"
        $errContent = $stderrContent
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
            # Fallback: count individual test-result lines from captured stdout.
            # Real results look like "  Passed TestName [duration]" with a bracket;
            # skip lines like "Failed to initialize..." that lack one.
            $passed  = @($stdoutLines | Select-String -Pattern "^\s+Passed\s+.+\[").Count
            $failed  = @($stdoutLines | Select-String -Pattern "^\s+Failed\s+.+\[").Count
            $skipped = @($stdoutLines | Select-String -Pattern "^\s+Skipped\s+.+\[").Count
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
