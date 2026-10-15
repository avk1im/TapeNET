function Fix-Mojibakes {

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

        [switch]$Backup
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
            $score += ([regex]::Matches($Text, [regex]::Escape($p))).Count
        }

        $score += ([regex]::Matches($Text, [char]0xFFFD)).Count * 5

        return $score
    }

    function Repair-Mojibake {
        param([string]$Text)

        try {

            $cp1252 = [Text.Encoding]::GetEncoding(1252)

            $bytes = $cp1252.GetBytes($Text)

            return [Text.Encoding]::UTF8.GetString($bytes)
        }
        catch {
            return $null
        }
    }

    function Show-SuspiciousLines {
        param(
            [string]$Text,
            [string]$File
        )

        $patterns = @(
            'Ã',
            'Â',
            'â',
            ([char]0xFFFD)
        )

        $shown = [Collections.Generic.HashSet[int]]::new()

        $lineNo = 0

        foreach ($line in ($Text -split '\r?\n')) {

            $lineNo++

            $hit = $false

            foreach ($p in $patterns) {
                if ($line.Contains($p)) {
                    $hit = $true
                    break
                }
            }

            if ($hit -and $shown.Add($lineNo)) {

                Write-Verbose (
                    "{0}:{1}: {2}" -f
                    $File,
                    $lineNo,
                    $line.Trim()
                )
            }
        }
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

            $oldScore = Get-MojibakeScore $text

            if ($oldScore -eq 0) {
                continue
            }

            Show-SuspiciousLines $text $file.FullName

            $fixed = Repair-Mojibake $text

            if (-not $fixed) {
                continue
            }

            $newScore = Get-MojibakeScore $fixed

            Write-Host (
                "[{0}] score {1} -> {2} : {3}" -f
                $encInfo.Name,
                $oldScore,
                $newScore,
                $file.FullName
            )

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
}