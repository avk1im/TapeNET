<#
.SYNOPSIS
Detects and repairs common mojibake corruption in text files while preserving
the original file encoding.

.DESCRIPTION
Fix-Mojibakes scans one or more text files for signs of character-encoding
damage ("mojibake"), such as:

    façade      -> faÃ§ade
    —           -> â€”
    →           -> â†’
    §           -> Â§

The script uses heuristic scoring to identify suspicious text and attempts
one or more reversible repairs. A repair is only accepted when the resulting
text appears objectively better than the original according to the scoring
rules.

Safety features:

* Preserves the original file encoding whenever possible.
* Refuses to modify files whose encoding cannot be determined with confidence.
* Never applies a repair that scores worse than the original text.
* Supports -DryRun for analysis-only operation.
* Supports -Confirm.
* Can create backup files before modification.
* Can display suspicious source lines via -Verbose.

The script is intended for source repositories and documentation files such
as .cs, .md, .txt, .csproj, .props, .targets, .sln, and similar text files.

.PARAMETER Path
Root directory to search.

Defaults to the current directory.

.PARAMETER Filter
One or more filename patterns to process.

Examples:

    *.cs
    *.md
    *.cs,*.md
    *.cs,*.csproj,*.sln

.PARAMETER Recurse
Recursively process matching files below Path.

.PARAMETER DryRun
Performs analysis only.

No files are modified. The script reports suspicious files, detected
encodings, and predicted repair scores.

.PARAMETER Backup
Creates a ".bak" copy of each file before modification.

Example:

    TapeFileBackupAgent.cs
    TapeFileBackupAgent.cs.bak

.PARAMETER Verbose
Displays the individual source lines containing suspected mojibake.

Each line is emitted at most once, even if multiple suspicious sequences
appear on that line.

.PARAMETER ReplaceFFFD
Replaces U+FFFD replacement characters '�' with '?'.

Carefull! U+FFFD characters may signify some other UTF8 symbols that were lost.
Replacing them with '?' will make finding such occurrences harder. Manual review
is recommended.

.EXAMPLE
Fix-Mojibakes -Path . -Filter *.cs -DryRun

Scans all C# files in the current directory and reports potential mojibake
without modifying any files.

.EXAMPLE
Fix-Mojibakes -Path . -Filter *.cs,*.md -Recurse -DryRun -Verbose

Recursively scans C# and Markdown files.

Displays every suspicious line together with its file name and line number.

.EXAMPLE
Fix-Mojibakes -Path . -Filter *.cs,*.md -Recurse -DryRun

Shows which files would be modified if repairs were applied.

No files are changed because DryRun is enabled.

.EXAMPLE
Fix-Mojibakes -Path . -Filter *.cs,*.md -Recurse -Backup

Recursively repairs files when the script has high confidence that the
repair improves the text.

A .bak file is created before each modification.

.EXAMPLE
Fix-Mojibakes `
    -Path . `
    -Filter *.cs,*.csproj,*.sln,*.md `
    -Recurse `
    -DryRun `
    -Verbose

Recommended first pass for a Visual Studio solution.

Performs a repository-wide analysis and displays all suspicious occurrences
without changing any files.

.NOTES
Recommended workflow:

Step 1:
    Run with -DryRun -Verbose and review the findings.

Step 2:
    Run with -Backup to perform the actual repairs.

This script intentionally prefers false negatives over false positives.
When the script is uncertain, it skips the file rather than risking
corruption of valid content.

.LINK
about_Comment_Based_Help

.LINK
Get-Help Fix-Mojibakes -Full
#>
[CmdletBinding(
    SupportsShouldProcess = $true,
    ConfirmImpact = 'High'
)]
param(
    [Parameter(Position = 0)]
    [string]$Path = '.',

    [string[]]$Filter = @('*.cs'),

    [switch]$Recurse,

    [switch]$DryRun,

    [switch]$Backup,

    [switch]$ReplaceFFFD
)

Set-StrictMode -Version Latest

function Get-FileEncoding {
    param([byte[]]$Bytes)

    if ($Bytes.Length -ge 3 `
        -and $Bytes[0] -eq 0xEF `
        -and $Bytes[1] -eq 0xBB `
        -and $Bytes[2] -eq 0xBF)
    {
        return @{
            Name     = 'UTF8-BOM'
            Encoding = [Text.UTF8Encoding]::new($true)
            BomBytes = $Bytes[0..2]
            Skip     = 3
        }
    }

    if ($Bytes.Length -ge 2 `
        -and $Bytes[0] -eq 0xFF `
        -and $Bytes[1] -eq 0xFE)
    {
        return @{
            Name     = 'UTF16-LE'
            Encoding = [Text.UnicodeEncoding]::new($false, $true)
            BomBytes = $Bytes[0..1]
            Skip     = 2
        }
    }

    if ($Bytes.Length -ge 2 `
        -and $Bytes[0] -eq 0xFE `
        -and $Bytes[1] -eq 0xFF)
    {
        return @{
            Name     = 'UTF16-BE'
            Encoding = [Text.UnicodeEncoding]::new($true, $true)
            BomBytes = $Bytes[0..1]
            Skip     = 2
        }
    }

    try {
        $utf8 = [Text.UTF8Encoding]::new($false, $true)
        $null = $utf8.GetString($Bytes)

        return @{
            Name     = 'UTF8'
            Encoding = $utf8
            BomBytes = @()
            Skip     = 0
        }
    }
    catch {
        return $null
    }
}

function Get-ReplacementCharCount {
    param([string]$Text)

    $ReplacementChar = [char]0xFFFD

    return ([regex]::Matches(
        $Text,
        [regex]::Escape($ReplacementChar)
    )).Count
}

function Get-MojibakeScore {
    param([string]$Text)

    $score = 0

    $patterns = @(
        'Ã',
        'Â',
        'â€',
        'â€™',
        'â€œ',
        'â€',
        'â€“',
        'â€”'
    )

    foreach ($p in $patterns) {
        $score += ([regex]::Matches(
            $Text,
            [regex]::Escape($p)
        )).Count
    }

    return $score
}

function Repair-Mojibake {
    param(
        [string]$Text,
        [switch]$ReplaceFFFD
    )

    try {

        $placeholder = '__FIXMOJIBAKE_FFFD__'

        if (-not $ReplaceFFFD) {

            $replacementChar = [char]0xFFFD

            $Text = $Text.Replace(
                $replacementChar,
                $placeholder
            )
        }

        $cp1252 = [Text.Encoding]::GetEncoding(1252)

        $bytes = $cp1252.GetBytes($Text)

        $fixed = [Text.Encoding]::UTF8.GetString($bytes)

        if (-not $ReplaceFFFD) {

            $fixed = $fixed.Replace(
                $placeholder,
                [string][char]0xFFFD
            )
        }

        return $fixed
    }
    catch {
        return $null
    }
}

function Show-SuspiciousLines {
    param(
        [string]$OriginalText,
        [string]$FixedText,
        [string]$File
    )

    $patterns = @(
        'Ã',
        'Â',
        'â',
        ([char]0xFFFD)
    )

    $ReplacementChar = [char]0xFFFD

    $oldLines = $OriginalText -split '\r?\n'
    $newLines = $FixedText    -split '\r?\n'

    $count = [Math]::Min($oldLines.Count, $newLines.Count)

    $changedLines = 0

    for ($i = 0; $i -lt $count; $i++) {

        $oldLine = $oldLines[$i]

        $suspicious = $false

        foreach ($p in $patterns) {
            if ($oldLine.Contains($p)) {
                $suspicious = $true
                break
            }
        }

        if (-not $suspicious) {
            continue
        }

        $newLine = $newLines[$i]

        $lineChanged = $oldLine -ne $newLine

        if (
            (-not $lineChanged) -and
            (-not $oldLine.Contains($ReplacementChar))
        ) {
            continue
        }

        $changedLines++

        Write-Verbose ''
        Write-Verbose ('{0}:{1}' -f $File, ($i + 1))

        if ($lineChanged) {
            Write-Verbose ('  OLD: {0}' -f $oldLine.TrimEnd())
            Write-Verbose ('  NEW: {0}' -f $newLine.TrimEnd())
        }
        else {
            if ($i -gt 0) {
                Write-Verbose ('   {0}' -f $oldLines[$i-1].TrimEnd())
            }
            Write-Verbose ('>> {0}' -f $oldLine.TrimEnd())
            if ($i -lt ($oldLines.Count - 1)) {
                Write-Verbose ('   {0}' -f $oldLines[$i+1].TrimEnd())
            }
        }

        if ($oldLine.Contains($ReplacementChar)) {
            Write-Verbose ('  *** Contains U+FFFD (manual review likely required)')
        }
    }

    return $changedLines
}

$gciParams = @{
    Path = $Path
    File = $true
}

if ($Recurse) {
    $gciParams.Recurse = $true
}

$files =
    foreach ($f in $Filter) {
        Get-ChildItem @gciParams -Filter $f
    }

$files = $files | Sort-Object FullName -Unique

foreach ($file in $files) {

#    Write-Host "Scanning $($file.FullName)..."

    try {

        [byte[]]$raw =
            [IO.File]::ReadAllBytes($file.FullName)

        $encInfo = Get-FileEncoding $raw

        if (-not $encInfo) {

            Write-Warning (
                "Skipped unknown encoding: {0}" -f
                $file.FullName
            )

            continue
        }

        $text =
            $encInfo.Encoding.GetString(
                $raw,
                $encInfo.Skip,
                $raw.Length - $encInfo.Skip
            )

        $replacementCount = Get-ReplacementCharCount $text

        $oldScore = Get-MojibakeScore $text

#        Write-Host "Candidate: $($file.Name) Score=$oldScore FFFD=$replacementCount"

        if (
            ($oldScore -eq 0) -and
            ($replacementCount -eq 0)
        ) {
            continue
        }

        $fixed = Repair-Mojibake `
            -Text $text `
            -ReplaceFFFD:$ReplaceFFFD

        if (
            (-not $fixed) -and
            ($replacementCount -eq 0)
        ) {
            continue
        }

        if ($null -eq $fixed) {
            $fixed = $text
        }

        $changedLines = 0

        if ($VerbosePreference -ne 'SilentlyContinue') {

            $changedLines =
                Show-SuspiciousLines `
                    -OriginalText $text `
                    -FixedText $fixed `
                    -File $file.FullName
        }

        $newScore = Get-MojibakeScore $fixed

        $changeDescription =
            if ($DryRun) {
                "would repair $changedLines line(s)"
            }
            else {
                "repairing $changedLines line(s)"
            }

        if ($replacementCount -gt 0) {
            $changeDescription += ", $replacementCount FFFD occurrence(s)"
        }

        Write-Host (
            "[{0}] score {1} -> {2}, {3}: {4}" -f
            $encInfo.Name,
            $oldScore,
            $newScore,
            $changeDescription,
            $file.FullName
        )

        if ($replacementCount -gt 0) {
            Write-Warning (
                "{0}: contains {1} occurrence(s) of U+FFFD. Manual review recommended." -f
                $file.FullName,
                $replacementCount
            )
        }

        if ($newScore -ge $oldScore) {
            continue
        }

        if ($DryRun) {
            continue
        }

        if ($Backup) {

            $backup =
                $file.FullName + '.bak'

            Copy-Item $file.FullName $backup -Force
        }

        if ($PSCmdlet.ShouldProcess(
            $file.FullName,
            'Repair mojibake'
        ))
        {
            $bodyBytes =
                $encInfo.Encoding.GetBytes($fixed)

            $finalBytes =
                @($encInfo.BomBytes) + @($bodyBytes)

            [IO.File]::WriteAllBytes(
                $file.FullName,
                $finalBytes
            )
        }
    }
    catch {

        Write-Warning (
            "{0}: {1}" -f
            $file.FullName,
            $_.Exception.Message
        )
    }
}
