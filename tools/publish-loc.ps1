<#
.SYNOPSIS
	Builds a localized distribution package of TapeWinNET for a target culture.

.DESCRIPTION
	Last-step-before-packaging orchestration for the TapeNET localization
	pipeline (see docs/Design-Master-Loc.md and docs/Design-TapeLoc.md):

	  1. Runs TapeLoc to (re)generate and validate the translated source variant
		 under loc/<lang>/TapeWinNET. If validation fails, the script stops
		 (TapeLoc exit code 1) and no package is produced.
	  2. Publishes TapeWinNET with the LocSourceDir MSBuild property pointed at
		 the generated variant, so the translated sources are compiled instead
		 of the canonical English ones.
	  3. Outputs the package under dist/TapeWinNET-<lang>.

	The canonical English package is produced by a normal publish (no -Lang).

.PARAMETER Lang
	Target culture, e.g. 'de' or 'fr'.

.PARAMETER Configuration
	Build configuration. Defaults to 'Release'.

.PARAMETER Force
	Pass --force to TapeLoc to ignore the translation cache.

.EXAMPLE
	.\tools\publish-loc.ps1 -Lang de

.EXAMPLE
	.\tools\publish-loc.ps1 -Lang fr -Force
#>
[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $Lang,

	[string] $Configuration = 'Release',

	[switch] $Force
)

$ErrorActionPreference = 'Stop'

# Resolve repo root as the parent of this script's 'tools' directory.
$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot
try {
	$locProject = Join-Path $repoRoot 'tools\TapeLoc\TapeLoc.csproj'
	$winProject = Join-Path $repoRoot 'TapeWinNET\TapeWinNET.csproj'
	$variantDir = Join-Path $repoRoot "loc\$Lang\TapeWinNET"
	$outputDir  = Join-Path $repoRoot "dist\TapeWinNET-$Lang"

	# --- Step 1: generate + validate the translated variant ------------------
	Write-Host "==> Generating '$Lang' source variant with TapeLoc..." -ForegroundColor Cyan
	$tapelocArgs = @('run', '--project', $locProject, '-c', $Configuration, '--', '--lang', $Lang, '--report')
	if ($Force) { $tapelocArgs += '--force' }

	& dotnet @tapelocArgs
	if ($LASTEXITCODE -ne 0) {
		throw "TapeLoc failed (exit $LASTEXITCODE). No package produced. " +
			  "Inspect *.reject files and the report under loc\."
	}

	if (-not (Test-Path $variantDir)) {
		throw "Expected variant directory not found: $variantDir"
	}

	# --- Step 2: publish from the translated variant -------------------------
	Write-Host "==> Publishing TapeWinNET ($Lang) -> $outputDir ..." -ForegroundColor Cyan
	& dotnet publish $winProject `
		-c $Configuration `
		-p:LocSourceDir=$variantDir `
		-o $outputDir
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet publish failed (exit $LASTEXITCODE)."
	}

	Write-Host "==> Done. Localized package: $outputDir" -ForegroundColor Green
}
finally {
	Pop-Location
}
