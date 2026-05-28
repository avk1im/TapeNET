<#
.SYNOPSIS
	Builds precomputed embeddings for the HelpNET sample content corpus.

.DESCRIPTION
	Invokes HelpIndexBuilder against the SampleContent directory using the
	all-MiniLM-L6-v2 ONNX model and writes the three output files
	(embeddings.bin, embeddings.meta.json, embeddings.index.json) into
	SampleContent\Embeddings\.

	Run this script whenever the sample Markdown content changes or when you
	want to regenerate embeddings with a different model.

.PARAMETER Configuration
	Build configuration for HelpIndexBuilder. Defaults to "Release".

.PARAMETER DryRun
	If specified, passes --dry-run to HelpIndexBuilder so no files are written.

.EXAMPLE
	.\Build-SampleEmbeddings.ps1

.EXAMPLE
	.\Build-SampleEmbeddings.ps1 -DryRun

.NOTES
	Prerequisites:
	  - .NET 8 SDK  (dotnet.exe on PATH)
	  - ONNX model at the path defined by $ModelDir below
		(all-MiniLM-L6-v2 — model.onnx + vocab.txt)
#>
[CmdletBinding()]
param(
	[string] $Configuration = "Release",
	[switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# ── Paths ─────────────────────────────────────────────────────────────────────

$ScriptDir   = $PSScriptRoot
$SolutionDir = Resolve-Path (Join-Path $ScriptDir "..\..\..")
$ProjectPath = Join-Path $ScriptDir "HelpIndexBuilder\HelpIndexBuilder.csproj"
$ContentDir  = Join-Path $ScriptDir "SampleContent"
$OutputDir   = Join-Path $ContentDir "Embeddings"

# ONNX model — adjust this path if your model is in a different location.
$ModelDir    = "C:\Users\$env:USERNAME\ONNX\Models\all-MiniLM-L6-v2"
$ModelPath   = Join-Path $ModelDir "model.onnx"
$VocabPath   = Join-Path $ModelDir "vocab.txt"

# Model parameters for all-MiniLM-L6-v2.
$ModelId     = "all-MiniLM-L6-v2"
$Dimension   = 384
$MaxTokens   = 512

# ── Pre-flight checks ─────────────────────────────────────────────────────────

if (-not (Test-Path $ModelPath)) {
	Write-Error "ONNX model not found: $ModelPath`nDownload all-MiniLM-L6-v2 and place model.onnx + vocab.txt at:`n  $ModelDir"
}

if (-not (Test-Path $VocabPath)) {
	Write-Error "Vocab file not found: $VocabPath"
}

if (-not (Test-Path $ContentDir)) {
	Write-Error "Sample content directory not found: $ContentDir"
}

# ── Build HelpIndexBuilder ────────────────────────────────────────────────────

Write-Host "[Build-SampleEmbeddings] Building HelpIndexBuilder ($Configuration)…" -ForegroundColor Cyan
dotnet build $ProjectPath -c $Configuration --nologo -v minimal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ── Run HelpIndexBuilder ──────────────────────────────────────────────────────

$RunArgs = @(
	"run", "--project", $ProjectPath,
	"-c", $Configuration,
	"--no-build",
	"--",
	"--content",   $ContentDir,
	"--model",     $ModelPath,
	"--vocab",     $VocabPath,
	"--model-id",  $ModelId,
	"--dim",       $Dimension,
	"--max-tokens", $MaxTokens,
	"--output",    $OutputDir
)

if ($DryRun) {
	$RunArgs += "--dry-run"
	Write-Host "[Build-SampleEmbeddings] *** DRY RUN — no files will be written ***" -ForegroundColor Yellow
}

Write-Host "[Build-SampleEmbeddings] Running HelpIndexBuilder…" -ForegroundColor Cyan
dotnet @RunArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# ── Summary ───────────────────────────────────────────────────────────────────

if (-not $DryRun) {
	$Files = @("embeddings.bin", "embeddings.meta.json", "embeddings.index.json")
	Write-Host "`n[Build-SampleEmbeddings] Output files:" -ForegroundColor Green
	foreach ($f in $Files) {
		$p = Join-Path $OutputDir $f
		if (Test-Path $p) {
			$size = (Get-Item $p).Length
			Write-Host ("  {0,-30}  {1,8:N0} bytes" -f $f, $size) -ForegroundColor Green
		} else {
			Write-Warning "  $f -- NOT FOUND (HelpIndexBuilder may have returned an error)"
		}
	}
}

Write-Host "`n[Build-SampleEmbeddings] Done." -ForegroundColor Green
