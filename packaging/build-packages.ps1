<#
.SYNOPSIS
	Builds deployable installers for the TapeNET apps (tapecon CLI and TapeWin WPF GUI).

.DESCRIPTION
	1. Publishes both apps as framework-dependent win-x64 builds.
	2. Stages README, LICENSE, and markdown docs alongside each app.
	3. Compiles the Inno Setup scripts into self-contained installer executables.

	The resulting installers are written to packaging\installers\:
		tapecon-<version>-setup.exe
		TapeWin-<version>-setup.exe

	Prerequisites:
		- .NET 8 SDK (for dotnet publish)
		- Inno Setup 6 (https://jrsoftware.org/isdl.php) for ISCC.exe

.PARAMETER Configuration
	Build configuration to publish. Defaults to Release.

.PARAMETER Version
	Optional explicit version (e.g. 2.0.0.123) stamped into the installer file names
	and metadata. When omitted, the version is read from the published tapecon.exe
	FileVersion (which Versioning.targets derives from the git commit count).

.PARAMETER SkipPublish
	Skip the dotnet publish step and reuse whatever is already staged under dist\.
	Useful for iterating on the .iss scripts only.

.EXAMPLE
	pwsh packaging\build-packages.ps1

.EXAMPLE
	pwsh packaging\build-packages.ps1 -Version 2.0.0.123
#>
[CmdletBinding()]
param(
	[string]$Configuration = 'Release',
	[string]$Version,
	[switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'

# --- Paths -----------------------------------------------------------------
# Script lives in <repo>\packaging; the repo root is its parent.
$packagingDir = $PSScriptRoot
$repoRoot     = Split-Path -Parent $packagingDir
$distDir      = Join-Path $packagingDir 'dist'
$installersDir = Join-Path $packagingDir 'installers'

$conProject = Join-Path $repoRoot 'TapeConNET\TapeConNET.csproj'
$winProject = Join-Path $repoRoot 'TapeWinNET\TapeWinNET.csproj'
$conDist    = Join-Path $distDir 'tapecon'
$winDist    = Join-Path $distDir 'tapewin'

$conIss = Join-Path $packagingDir 'tapecon.iss'
$winIss = Join-Path $packagingDir 'tapewin.iss'

# Shared documents staged into every package.
$rootDocs = @(
	(Join-Path $repoRoot 'README.md'),
	(Join-Path $repoRoot 'LICENSE.txt'),
	(Join-Path $repoRoot 'THIRD-PARTY-NOTICES.md')
)
$docsFolder = Join-Path $repoRoot 'docs'

# --- Helpers ---------------------------------------------------------------
function Write-Step($message) {
	Write-Host ''
	Write-Host "==> $message" -ForegroundColor Cyan
}

function Resolve-Iscc {
	# 1) Already on PATH?
	$cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
	if ($cmd) { return $cmd.Source }

	# 2) Default Inno Setup 6 install locations.
	$candidates = @(
		"${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
		"${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
	)
	foreach ($c in $candidates) {
		if ($c -and (Test-Path $c)) { return $c }
	}

	throw "Inno Setup compiler (ISCC.exe) not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or add ISCC.exe to PATH."
}

function Copy-Docs($targetDir) {
	foreach ($doc in $rootDocs) {
		if (Test-Path $doc) {
			Copy-Item -Path $doc -Destination $targetDir -Force
		}
	}
	if (Test-Path $docsFolder) {
		$targetDocs = Join-Path $targetDir 'docs'
		New-Item -ItemType Directory -Path $targetDocs -Force | Out-Null
		Copy-Item -Path (Join-Path $docsFolder '*.md') -Destination $targetDocs -Force -ErrorAction SilentlyContinue
	}
}

function Publish-App($name, $project, $output) {
	Write-Step "Publishing $name (framework-dependent, win-x64)"
	if (Test-Path $output) {
		Remove-Item -Path $output -Recurse -Force
	}
	dotnet publish $project `
		-c $Configuration `
		-r win-x64 `
		--self-contained false `
		-p:PublishSingleFile=false `
		-o $output
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet publish failed for $name (exit code $LASTEXITCODE)."
	}
	Copy-Docs $output
}

function Get-PublishedVersion($exePath) {
	if (-not (Test-Path $exePath)) { return $null }
	$info = (Get-Item $exePath).VersionInfo
	# Prefer the 4-part FileVersion; fall back to ProductVersion.
	$v = $info.FileVersion
	if ([string]::IsNullOrWhiteSpace($v)) { $v = $info.ProductVersion }
	return $v
}

# --- Build -----------------------------------------------------------------
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
New-Item -ItemType Directory -Path $installersDir -Force | Out-Null

if (-not $SkipPublish) {
	Publish-App 'tapecon (CLI)' $conProject $conDist
	Publish-App 'TapeWin (WPF)' $winProject $winDist
}
else {
	Write-Step 'Skipping publish (-SkipPublish); reusing existing dist\ output.'
}

# Resolve the installer version: explicit -Version wins, otherwise read the exe.
if ([string]::IsNullOrWhiteSpace($Version)) {
	$Version = Get-PublishedVersion (Join-Path $conDist 'tapecon.exe')
	if ([string]::IsNullOrWhiteSpace($Version)) {
		$Version = '1.0.0.0'
		Write-Warning "Could not determine version from published exe; defaulting to $Version."
	}
}
Write-Host "Installer version: $Version" -ForegroundColor Green

# --- Compile installers ----------------------------------------------------
$iscc = Resolve-Iscc
Write-Host "Using Inno Setup compiler: $iscc" -ForegroundColor DarkGray

Write-Step 'Compiling tapecon installer'
& $iscc "/DAppVersion=$Version" $conIss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed for tapecon.iss (exit code $LASTEXITCODE)." }

Write-Step 'Compiling TapeWin installer'
& $iscc "/DAppVersion=$Version" $winIss
if ($LASTEXITCODE -ne 0) { throw "ISCC failed for tapewin.iss (exit code $LASTEXITCODE)." }

# --- Summary ---------------------------------------------------------------
Write-Step 'Done. Installers produced:'
Get-ChildItem -Path $installersDir -Filter '*-setup.exe' |
	ForEach-Object { Write-Host "    $($_.FullName)" -ForegroundColor Green }
