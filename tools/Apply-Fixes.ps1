<#
.SYNOPSIS
Applies manually reviewed line replacements.

.DESCRIPTION
Reads a text file containing entries in the form:

    C:\Some\File.cs:123
    FIXED:     // Corrected text

and replaces the specified source line.

The script intentionally performs no clever matching.
It replaces exactly the requested line number.

.EXAMPLE
.\Apply-Fixes.ps1 `
    -FixesFile My_Great_Fixes.txt `
    -DryRun

.EXAMPLE
.\Apply-ReviewedFixes.ps1 `
    -FixesFile FFFD-Fixes.txt
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FixesFile,

    [switch]$DryRun
)

Set-StrictMode -Version Latest

$entries = [System.Collections.Generic.List[object]]::new()

$lines = Get-Content $FixesFile

for ($i = 0; $i -lt $lines.Count; $i++) {

    $line = $lines[$i].TrimEnd()

    if ([string]::IsNullOrWhiteSpace($line)) {
        continue
    }

    if ($line -match '^(.*?):(\d+)$') {

        $filePath = $matches[1]
        $lineNumber = [int]$matches[2]

        $i++

        if ($i -ge $lines.Count) {
            throw "Missing FIXED: line after '$line'"
        }

        $fixedLine = $lines[$i]

        if (-not $fixedLine.StartsWith('FIXED:')) {
            throw "Expected FIXED: after '$line'"
        }

        $entries.Add([pscustomobject]@{
            FilePath   = $filePath
            LineNumber = $lineNumber
            NewText    = $fixedLine.Substring(6).TrimStart()
        })
    }
}

$grouped =
    $entries |
    Group-Object FilePath

foreach ($group in $grouped) {

    $file = $group.Name

    Write-Host "Processing $file"

    $content =
        [System.Collections.Generic.List[string]]::new()

    if (-not (Test-Path $file)) {
        Write-Warning "File not found: $file"
        continue
    }

    $content.AddRange(
       [string[]](Get-Content $file)
    )

    foreach ($fix in $group.Group) {

        $index = $fix.LineNumber - 1

        if (
            $index -lt 0 -or
            $index -ge $content.Count
        ) {
            Write-Warning (
                "Line {0} does not exist in {1}" -f
                $fix.LineNumber,
                $file
            )
            continue
        }

        $oldText = $content[$index]

        $leadingWhitespace =
            ([regex]::Match(
                $oldText,
                '^\s*'
            )).Value

        $newText = $leadingWhitespace + $fix.NewText.TrimStart()

        $content[$index] = $newText

        Write-Host (
            "  Line {0}" -f
            $fix.LineNumber
        )

        Write-Host (
            "    OLD: {0}" -f
            $oldText
        )

        Write-Host (
            "    NEW: {0}" -f
            $newText
        )

        $content[$index] = $newText
    }

    if (-not $DryRun) {

        [System.IO.File]::WriteAllLines(
            $file,
            $content
        )
    }
}