#Requires -Version 7.0
<#
.SYNOPSIS
	Builds the TapeNET Windows installers: the TapeNET suite (tapecon + TapeWin + optional tapesvc)
	and the standalone tapesvc remote service.

.DESCRIPTION
	1. Publishes tapecon, TapeWin, and tapesvc as SELF-CONTAINED win-x64 builds: the .NET runtime
	   ships inside each app folder, so target machines need no .NET installation.
	2. Verifies each publish output: executable, native DLLs (libzstd, ONNX Runtime), and the
	   TapeWin help embedding model.
	3. Sanitizes the tapesvc configuration for shipping: strips the developer TLS endpoint,
	   ships appsettings.Tls.json.example instead.
	4. Stages the shared documents (README, LICENSE, THIRD-PARTY-NOTICES, FCL specification).
	5. Collects the license files and the package inventory of every shipped NuGet package into
	   <app>\licenses\ (input for THIRD-PARTY-NOTICES.md reviews).
	6. Compiles tapesvc.iss first, then tapenet.iss, which embeds the tapesvc installer as an
	   optional component.

	Outputs in packaging\installers\:
		tapesvc-<version>-setup.exe		standalone remote service (servers)
		TapeNET-<version>-setup.exe		tapecon + TapeWin, tapesvc optional (clients, client+server)

	Prerequisites:
		- .NET SDK 8 or newer (dotnet publish)
		- Inno Setup 6 (https://jrsoftware.org/isdl.php) for ISCC.exe
		- The help embedding model folder (see docs\Design-HelpContent.md section 5.2),
		  unless -AllowLexicalHelp

.PARAMETER Configuration
	Build configuration to publish. Defaults to Release (no TEST_HOOKS).

.PARAMETER Version
	Optional explicit version (e.g. 2.0.0.123) for both installers. When omitted, each installer
	takes the FileVersion of its own published executable (Versioning.targets derives it from
	the git commit count).

.PARAMETER SkipPublish
	Skip dotnet publish and reuse the staged dist\ output. For iterating on the .iss scripts.

.PARAMETER AllowLexicalHelp
	Package TapeWin even when the help embedding model (HelpModel\) is missing; help then runs
	without semantic search.

.PARAMETER Runtime
	Target runtime identifier. Defaults to win-x64.

.EXAMPLE
	pwsh packaging\build-packages.ps1

.EXAMPLE
	pwsh packaging\build-packages.ps1 -Version 2.0.0.300

.EXAMPLE
	pwsh packaging\build-packages.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
	[string]$Configuration = 'Release',
	[string]$Version,
	[switch]$SkipPublish,
	[switch]$AllowLexicalHelp,
	[string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# --- Paths -----------------------------------------------------------------
# Script lives in <repo>\packaging; the repo root is its parent.
$packagingDir  = $PSScriptRoot
$repoRoot      = Split-Path -Parent $packagingDir
$distDir       = Join-Path $packagingDir 'dist'
$installersDir = Join-Path $packagingDir 'installers'
$commonDist    = Join-Path $distDir 'common'

# One entry per shipped app. Native = DLLs that MUST sit next to the exe after a RID-specific
#  publish; a missing one fails only at runtime on the tester's machine, so we check here.
$apps = [ordered]@{
	tapecon = @{
		Title   = 'tapecon (CLI)'
		Project = Join-Path $repoRoot 'TapeConNET\TapeConNET.csproj'
		Exe     = 'tapecon.exe'
		Native  = @('libzstd.dll')
	}
	tapewin = @{
		Title   = 'TapeWin (WPF GUI)'
		Project = Join-Path $repoRoot 'TapeWinNET\TapeWinNET.csproj'
		Exe     = 'TapeWin.exe'
		Native  = @('libzstd.dll', 'onnxruntime.dll', 'onnxruntime_providers_shared.dll')
	}
	tapesvc = @{
		Title   = 'tapesvc (remote service)'
		Project = Join-Path $repoRoot 'TapeServiceNET\TapeServiceNET.csproj'
		Exe     = 'tapesvc.exe'
		Native  = @('libzstd.dll')
	}
}

# Shared documents: installed once into the suite root, and into the standalone tapesvc folder.
#  Deliberately an allow-list: docs\ also holds internal design documents that must not ship.
$sharedFiles = @(
	'README.md',
	'LICENSE.txt',
	'THIRD-PARTY-NOTICES.md'
)
$sharedDocs = @(
	'docs\FCL-Specification.md'
)

$svcIss   = Join-Path $packagingDir 'tapesvc.iss'
$suiteIss = Join-Path $packagingDir 'tapenet.iss'

# --- Helpers ---------------------------------------------------------------
function Write-Step([string]$message) {
	Write-Host ''
	Write-Host "==> $message" -ForegroundColor Cyan
}

function Resolve-Iscc {
	$cmd = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
	if ($cmd) { return $cmd.Source }

	foreach ($c in @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "${env:ProgramFiles}\Inno Setup 6\ISCC.exe")) {
		if ($c -and (Test-Path $c)) { return $c }
	}
	throw 'Inno Setup compiler (ISCC.exe) not found. Install Inno Setup 6 from https://jrsoftware.org/isdl.php or add ISCC.exe to PATH.'
}

function Publish-App([string]$key) {
	$app = $apps[$key]
	$out = Join-Path $distDir $key
	Write-Step "Publishing $($app.Title) (self-contained, $Runtime)"

	if (Test-Path $out) { Remove-Item -Path $out -Recurse -Force }

	# PublishDocumentationFile(s)=false: drop the IntelliSense XML files from the payload.
	# SatelliteResourceLanguages=en:     drop third-party satellite resource folders (cs, de, ...).
	dotnet publish $app.Project `
		-c $Configuration `
		-r $Runtime `
		--self-contained true `
		-p:PublishSingleFile=false `
		-p:PublishDocumentationFile=false `
		-p:PublishReferencesDocumentationFiles=false `
		-p:SatelliteResourceLanguages=en `
		-o $out `
		--nologo
	if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($app.Title) (exit code $LASTEXITCODE)." }
}

function Assert-AppLayout([string]$key) {
	$app = $apps[$key]
	$dir = Join-Path $distDir $key
	$missing = @()
	foreach ($f in @($app.Exe) + $app.Native) {
		if (-not (Test-Path (Join-Path $dir $f))) { $missing += $f }
	}
	if ($missing.Count -gt 0) {
		throw "$($app.Title): publish output lacks $($missing -join ', '). Check the -r $Runtime publish and the package references."
	}
}

function Remove-JsonProperty($object, [string]$name) {
	if ($null -ne $object -and $object.PSObject.Properties[$name]) {
		$object.PSObject.Properties.Remove($name)
		return $true
	}
	return $false
}

# tapesvc ships the plaintext endpoint only. The repo appsettings.json also declares a developer
#  TLS endpoint (certs\tapesvc.pfx + password); Kestrel refuses to start when that certificate is
#  missing, so the shipped copy drops it. Users opt in via appsettings.Tls.json.example.
function Optimize-ServiceConfig {
	$dir = Join-Path $distDir 'tapesvc'
	$settingsPath = Join-Path $dir 'appsettings.json'
	$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json

	$endpoints = $null
	if ($settings.PSObject.Properties['Kestrel'] -and $settings.Kestrel.PSObject.Properties['Endpoints']) {
		$endpoints = $settings.Kestrel.Endpoints
	}
	if (Remove-JsonProperty $endpoints 'GrpcTls') {
		Write-Host '    Removed the developer TLS endpoint (GrpcTls) from the shipped appsettings.json.' -ForegroundColor DarkGray
	}
	$settings | ConvertTo-Json -Depth 32 | Set-Content -Path $settingsPath -Encoding utf8NoBOM

	foreach ($junk in @('appsettings.Development.json', 'tapesvc.staticwebassets.endpoints.json', 'certs')) {
		$p = Join-Path $dir $junk
		if (Test-Path $p) { Remove-Item -Path $p -Recurse -Force }
	}

	$tlsExample = Join-Path $repoRoot 'TapeServiceNET\appsettings.Tls.json.example'
	if (Test-Path $tlsExample) { Copy-Item -Path $tlsExample -Destination $dir -Force }
	else { Write-Warning 'TapeServiceNET\appsettings.Tls.json.example not found; the TLS opt-in sample will be missing.' }

	Copy-Item -Path (Join-Path $repoRoot 'TapeServiceNET\README.md') -Destination (Join-Path $dir 'README-tapesvc.md') -Force
}

function Copy-SharedDocs([string]$targetDir) {
	New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
	foreach ($f in $sharedFiles) {
		Copy-Item -Path (Join-Path $repoRoot $f) -Destination $targetDir -Force
	}
	$docsTarget = Join-Path $targetDir 'docs'
	New-Item -ItemType Directory -Path $docsTarget -Force | Out-Null
	foreach ($d in $sharedDocs) {
		Copy-Item -Path (Join-Path $repoRoot $d) -Destination $docsTarget -Force
	}
}

# Reads <project>\obj\project.assets.json (written by the RID-specific restore of the publish) and,
#  for every NuGet package that actually contributes runtime/native files, copies its license and
#  notice files into <app>\licenses\<id>\. Also covers the runtime packs of the self-contained
#  publish. Writes licenses\_packages.csv: Id, Version, License, LicenseFiles.
function Export-PackageLicenses([string]$key) {
	$app = $apps[$key]
	$assetsPath = Join-Path (Split-Path $app.Project -Parent) 'obj\project.assets.json'
	$licensesDir = Join-Path (Join-Path $distDir $key) 'licenses'
	if (-not (Test-Path $assetsPath)) {
		Write-Warning "$($app.Title): $assetsPath not found; skipping license collection."
		return
	}
	if (Test-Path $licensesDir) { Remove-Item -Path $licensesDir -Recurse -Force }
	New-Item -ItemType Directory -Path $licensesDir -Force | Out-Null

	$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json -AsHashtable
	$packageRoot = @($assets['packageFolders'].Keys)[0]
	$targetKey = @($assets['targets'].Keys | Where-Object { $_ -like "*/$Runtime" }) | Select-Object -First 1
	if (-not $targetKey) { throw "$($app.Title): no '$Runtime' target in project.assets.json; run the publish first." }

	$candidates = [System.Collections.Generic.List[object]]::new()

	foreach ($libKey in $assets['targets'][$targetKey].Keys) {
		$entry = $assets['targets'][$targetKey][$libKey]
		if ($entry['type'] -ne 'package') { continue }
		# Ships only when it contributes a real file (placeholders end in '_._').
		$ships = $false
		foreach ($section in 'runtime', 'native', 'runtimeTargets', 'resource') {
			if ($entry.ContainsKey($section) -and @($entry[$section].Keys | Where-Object { $_ -notlike '*_._' }).Count -gt 0) {
				$ships = $true; break
			}
		}
		if (-not $ships) { continue }
		$id, $ver = $libKey -split '/', 2
		$candidates.Add(@{ Id = $id; Version = $ver; Folder = Join-Path $packageRoot $assets['libraries'][$libKey]['path'] })
	}

	# Runtime packs of the self-contained publish (Microsoft.NETCore.App.Runtime.win-x64, ...).
	foreach ($fw in $assets['project']['frameworks'].Values) {
		if (-not $fw.ContainsKey('downloadDependencies')) { continue }
		foreach ($dep in $fw['downloadDependencies']) {
			if ($dep['name'] -notlike '*.Runtime.*') { continue }
			$ver = ($dep['version'] -replace '[\[\]\(\)\s]', '') -split ',' | Select-Object -First 1
			$folder = Join-Path $packageRoot (Join-Path $dep['name'].ToLowerInvariant() $ver)
			if (-not (Test-Path $folder)) { $folder = Join-Path $env:ProgramFiles "dotnet\packs\$($dep['name'])\$ver" }
			$candidates.Add(@{ Id = $dep['name']; Version = $ver; Folder = $folder })
		}
	}

	$rows = foreach ($c in $candidates) {
		$licenseExpr = ''
		$copied = @()
		if (Test-Path $c.Folder) {
			$nuspec = Get-ChildItem -Path $c.Folder -Filter '*.nuspec' -File -ErrorAction SilentlyContinue | Select-Object -First 1
			if ($nuspec) {
				$xml = [xml](Get-Content $nuspec.FullName -Raw)
				$meta = $xml.package.metadata
				if ($meta.PSObject.Properties['license'] -and $meta.license) {
					$licenseExpr = if ($meta.license -is [string]) { $meta.license } else { $meta.license.'#text' }
				}
				elseif ($meta.PSObject.Properties['licenseUrl'] -and $meta.licenseUrl) {
					$licenseExpr = $meta.licenseUrl
				}
			}
			$files = Get-ChildItem -Path $c.Folder -File -ErrorAction SilentlyContinue |
				Where-Object { $_.Name -match '^(license|licence|third[-_]?party[-_]?notices|thirdpartynotices|notice)' }
			if ($files) {
				$dest = Join-Path $licensesDir $c.Id
				New-Item -ItemType Directory -Path $dest -Force | Out-Null
				foreach ($f in $files) {
					Copy-Item -Path $f.FullName -Destination $dest -Force
					$copied += $f.Name
				}
			}
		}
		[pscustomobject]@{
			Id           = $c.Id
			Version      = $c.Version
			License      = $licenseExpr
			LicenseFiles = ($copied -join ';')
		}
	}
	$rows | Sort-Object Id | Export-Csv -Path (Join-Path $licensesDir '_packages.csv') -Encoding utf8NoBOM

	$noFile = @($rows | Where-Object { -not $_.LicenseFiles })
	Write-Host ("    {0} packages inventoried, {1} without a bundled license file (license expression recorded instead)." -f @($rows).Count, $noFile.Count) -ForegroundColor DarkGray
}

function Get-PublishedVersion([string]$exePath) {
	if (-not (Test-Path $exePath)) { return $null }
	$info = (Get-Item $exePath).VersionInfo
	$v = $info.FileVersion
	if ([string]::IsNullOrWhiteSpace($v)) { $v = $info.ProductVersion }
	return $v
}

function Invoke-Iscc([string]$iss, [string[]]$defines) {
	$isccArgs = @($defines | ForEach-Object { "/D$_" }) + @($iss)
	& $script:iscc @isccArgs
	if ($LASTEXITCODE -ne 0) { throw "ISCC failed for $(Split-Path $iss -Leaf) (exit code $LASTEXITCODE)." }
}

# --- Publish & stage ---------------------------------------------------------
New-Item -ItemType Directory -Path $distDir -Force | Out-Null
New-Item -ItemType Directory -Path $installersDir -Force | Out-Null

if (-not $SkipPublish) {
	foreach ($key in $apps.Keys) { Publish-App $key }

	Write-Step 'Sanitizing tapesvc configuration'
	Optimize-ServiceConfig

	Write-Step 'Staging shared documents'
	if (Test-Path $commonDist) { Remove-Item -Path $commonDist -Recurse -Force }
	Copy-SharedDocs $commonDist
	Copy-SharedDocs (Join-Path $distDir 'tapesvc')

	Write-Step 'Collecting third-party licenses'
	foreach ($key in $apps.Keys) {
		Write-Host "  $($apps[$key].Title)"
		Export-PackageLicenses $key
	}
}
else {
	Write-Step 'Skipping publish (-SkipPublish); reusing existing dist\ output.'
}

Write-Step 'Verifying publish output'
foreach ($key in $apps.Keys) { Assert-AppLayout $key }

# Semantic help needs the ONNX model staged by the TapeWinNET build (HelpModel\). Without it the
#  app silently falls back to lexical help, so refuse unless explicitly allowed.
if (-not (Test-Path (Join-Path $distDir 'tapewin\HelpModel\model.onnx'))) {
	$msg = 'TapeWin help embedding model (HelpModel\model.onnx) is missing from the publish output. ' +
		'Provide the model folder or -p:HelpEmbeddingModelDir (docs\Design-HelpContent.md section 5.2), or pass -AllowLexicalHelp.'
	if ($AllowLexicalHelp) { Write-Warning $msg } else { throw $msg }
}
Write-Host '    All executables and native DLLs in place.' -ForegroundColor DarkGray

# --- Versions ----------------------------------------------------------------
# Explicit -Version wins for both installers. Otherwise each installer takes its own exe's
#  version; the suite follows tapecon, and a mismatch with TapeWin is flagged.
$versions = @{}
foreach ($key in $apps.Keys) {
	$versions[$key] = if ($Version) { $Version } else { Get-PublishedVersion (Join-Path (Join-Path $distDir $key) $apps[$key].Exe) }
	if ([string]::IsNullOrWhiteSpace($versions[$key])) { throw "Could not read the version of $($apps[$key].Exe); pass -Version." }
}
if ($versions.tapecon -ne $versions.tapewin) {
	Write-Warning "tapecon ($($versions.tapecon)) and TapeWin ($($versions.tapewin)) carry different versions; the suite installer uses tapecon's. Align VersionBase in both projects."
}
$suiteVersion = $versions.tapecon
$svcVersion   = $versions.tapesvc
Write-Host "Suite version:   $suiteVersion" -ForegroundColor Green
Write-Host "tapesvc version: $svcVersion" -ForegroundColor Green

# --- Compile installers -----------------------------------------------------
$script:iscc = Resolve-Iscc
Write-Host "Using Inno Setup compiler: $script:iscc" -ForegroundColor DarkGray

# tapesvc first: the suite embeds its installer.
Write-Step 'Compiling the tapesvc installer'
Invoke-Iscc $svcIss @("AppVersion=$svcVersion")

Write-Step 'Compiling the TapeNET suite installer'
Invoke-Iscc $suiteIss @("AppVersion=$suiteVersion", "SvcVersion=$svcVersion")

# --- Summary ---------------------------------------------------------------
Write-Step 'Done. Installers produced:'
foreach ($name in @("TapeNET-$suiteVersion-setup.exe", "tapesvc-$svcVersion-setup.exe")) {
	$p = Join-Path $installersDir $name
	$sizeMb = [math]::Round((Get-Item $p).Length / 1MB, 1)
	Write-Host "    $p  ($sizeMb MB)" -ForegroundColor Green
}
