[CmdletBinding()]
param (
    [Parameter(Mandatory = $false)]
    [string]$TargetFolder,

    [Parameter(Mandatory = $false)]
    [string]$TotalSize = "400GB",

    [Parameter(Mandatory = $false)]
    [int]$TotalFolders = 200,

    [Parameter(Mandatory = $false)]
    [int]$MaxFolderDepth = 6,

    [Parameter(Mandatory = $false)]
    [string]$MinFileSize = "1KB",

    [Parameter(Mandatory = $false)]
    [string]$MaxFileSize = "50MB",

    [Parameter(Mandatory = $false)]
    [ValidateRange(0, 100)]
    [int]$RandomizationPercent = 50,

    [Parameter(Mandatory = $false)]
    [switch]$ForceLongPaths,

    [Parameter(Mandatory = $false)]
    [switch]$GenerateNTFSStreams,

    [Parameter(Mandatory = $false)]
    [switch]$DryRun,

    # -Verbose is a pre-defined PS script parameter
    #[Parameter(Mandatory = $false)]
    #[switch]$Verbose,

    [Parameter(Mandatory = $false)]
    [switch]$Help
)

function Show-Usage {
    Write-Host ""
    Write-Host "Usage: GenerateTestFiles.ps1 -TargetFolder <path> [options]" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Required:"
    Write-Host "  -TargetFolder <path>     Root folder where test data will be generated."
    Write-Host ""
    Write-Host "Optional:"
    Write-Host "  -TotalSize <size>        Default: 400GB"
    Write-Host "  -TotalFolders <count>    Default: 200"
    Write-Host "  -MaxFolderDepth <depth>  Default: 6"
    Write-Host "  -MinFileSize <size>      Default: 1KB"
    Write-Host "  -MaxFileSize <size>      Default: 50MB"
    Write-Host "  -RandomizationPercent n  Default: 50"
    Write-Host "  -ForceLongPaths"
    Write-Host "  -GenerateNTFSStreams"
    Write-Host "  -DryRun                 Simulate generation without writing files or creating folders."
    Write-Host "  -Verbose                Print one-line details for each folder and file."
    Write-Host ""
    Write-Host "Examples:"
    Write-Host "  .\GenerateTestFiles.ps1 -TargetFolder D:\TestData"
    Write-Host "  .\GenerateTestFiles.ps1 -TargetFolder D:\TestData -TotalSize 50GB -ForceLongPaths"
    Write-Host ""
}

# Support -? without defining an illegal parameter
if ($args -contains '-?') {
    Show-Usage
    return
}

# Mandatory TargetFolder check
if ($Help -or -not $PSBoundParameters.ContainsKey('TargetFolder') -or [string]::IsNullOrWhiteSpace($TargetFolder)) {
    Show-Usage
    return
}

# PS handling of -Verbose paraneter is to set $VerbosePreference to 'Continue' when -Verbose is specified.
#  We check for that to set our own $Verbose variable.
$Verbose = $VerbosePreference -eq 'Continue'

# --- HELPER FUNCTIONS ---

function Format-Size {
    param ([int64]$Bytes)

    if ($Bytes -lt 1KB) { return "$Bytes B" }
    if ($Bytes -lt 1MB) { return ("{0:N2} KB" -f ($Bytes / 1KB)) }
    if ($Bytes -lt 1GB) { return ("{0:N2} MB" -f ($Bytes / 1MB)) }
    if ($Bytes -lt 1TB) { return ("{0:N2} GB" -f ($Bytes / 1GB)) }
    return ("{0:N2} TB" -f ($Bytes / 1TB))
}

function Write-DryRun {
    param ([string]$Message)
    Write-Host "[DRYRUN] $Message" -ForegroundColor DarkGray
}

function Write-VerboseLine {
    param (
        [string]$Path,
        [int64]$SizeBytes = 0,
        [bool]$Ntfs = $false
    )

    $sizeText = if ($SizeBytes -gt 0) { " : " + (Format-Size $SizeBytes) } else { "" }
    $ntfsText = if ($Ntfs) { " : NTFS" } else { "" }

    if ($DryRun) {
        Write-DryRun "$Path$sizeText$ntfsText"
    }
    elseif ($Verbose) {
        Write-Host "$Path$sizeText$ntfsText"
    }
}

function Get-SizeInBytes {
    param ([string]$SizeString)
    
    $SizeString = $SizeString.Trim().ToUpper()
    if ($SizeString -match '^(\d+(?:\.\d+)?)\s*([KMGT]B?|BYTES)?$') {
        $Value = [double]$Matches[1]
        $Unit = $Matches[2]

        switch -regex ($Unit) {
            'K(B)?' { return [int64]($Value * 1KB) }
            'M(B)?' { return [int64]($Value * 1MB) }
            'G(B)?' { return [int64]($Value * 1GB) }
            'T(B)?' { return [int64]($Value * 1TB) }
            default { return [int64]$Value }
        }
    }
    throw "Invalid size format: $SizeString. Use formats like 12K, 512KB, 2MB, 400GB."
}

function Get-NextLogarithmicSize {
    param (
        [System.Random]$Rand,
        [int64]$MinBytes,
        [int64]$MaxBytes
    )

    $LogMin = [Math]::Log($MinBytes)
    $LogMax = [Math]::Log($MaxBytes)
    
    $RandSample = $Rand.NextDouble()
    $LogSize = $LogMin + ($RandSample * ($LogMax - $LogMin))
    
    return [int64][Math]::Exp($LogSize)
}

function New-DirTree {
    param (
        [string]$Root,
        [int]$Count,
        [int]$MaxDepth,
        [bool]$InjectLong
    )
    
    Write-Host "Allocating baseline directory structures..." -ForegroundColor Cyan
    $List = New-Object System.Collections.Generic.List[string]
    $List.Add($Root)

    $FoldersPerLevel = [Math]::Ceiling($Count / $MaxDepth)
    $Counter = 1

    for ($Depth = 1; $Depth -le $MaxDepth; $Depth++) {
        $CurrentParents = $List.ToArray()
        foreach ($Parent in $CurrentParents) {
            for ($i = 0; $i -le $FoldersPerLevel; $i++) {
                if ($Counter -gt $Count) { break }
                $NewFolder = [System.IO.Path]::Combine($Parent, "Folder_$Counter")

                if (-not $DryRun) {
                    [System.IO.Directory]::CreateDirectory($NewFolder) | Out-Null
                }

                Write-VerboseLine -Path $NewFolder
                $List.Add($NewFolder)
                $Counter++
            }
            if ($Counter -gt $Count) { break }
        }
    }

    if ($InjectLong) {
        Write-Host "Injecting deeply nested long paths (> 260 characters)..." -ForegroundColor Cyan
        $DeepParent = $List[$List.Count - 1]
        for ($j = 1; $j -le 12; $j++) {
            $DeepParent = [System.IO.Path]::Combine($DeepParent, "SubFolder_DeepValidationChain_Level_$j")

            if (-not $DryRun) {
                [System.IO.Directory]::CreateDirectory($DeepParent) | Out-Null
            }

            Write-VerboseLine -Path $DeepParent
            $List.Add($DeepParent)
        }
    }

    return $List
}

function Write-TestFile {
    param (
        [string]$Path,
        [int64]$Length,
        [byte[]]$SourceBuffer
    )
    
    $Stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None, $SourceBuffer.Length)
    $BytesWritten = [int64]0
    
    while ($BytesWritten -lt $Length) {
        $BytesToWrite = [Math]::Min($SourceBuffer.Length, ($Length - $BytesWritten))
        $Stream.Write($SourceBuffer, 0, $BytesToWrite)
        $BytesWritten += $BytesToWrite
    }
    $Stream.Close()
    $Stream.Dispose()
}

function Add-NtfsStream {
    param ([string]$ParentFilePath)
    
    $StreamPath = "$ParentFilePath`:Zone.Identifier"
    $StreamContent = "[ZoneTransfer]`r`nZoneId=3`r`nReferrerUrl=http://localhost/test`r`n"
    $StreamBytes = [System.Text.Encoding]::ASCII.GetBytes($StreamContent)
    
    $StreamStream = New-Object System.IO.FileStream($StreamPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write)
    $StreamStream.Write($StreamBytes, 0, $StreamBytes.Length)
    $StreamStream.Close()
    $StreamStream.Dispose()
}

$LastPctReported = 0 # state for function Report-Progress

function Report-Progress {
    param (
        [int64]$CurrentSizeBytes,
        [int64]$TargetSizeBytes,
        [int]$FileCounter
    )

    $Pct = ($CurrentSizeBytes / $TargetSizeBytes) * 100
    $PctInt = [int][Math]::Floor($Pct)

    $Interval = if ($Verbose) { 2 } else { 10 }

    if ($PctInt -ge $script:LastPctReported + $Interval) {
        Write-Host "Progress: $PctInt% complete ($(Format-Size $CurrentSizeBytes) written, $FileCounter files created)..." -ForegroundColor Yellow
        $script:LastPctReported = $PctInt
    }
}

# --- Quantile setup (logarithmic) ---
function SetUp-Quantiles {
    param (
        [int64]$MinFileBytes,
        [int64]$MaxFileBytes
    )

    $LogMin = [Math]::Log($MinFileBytes)
    $LogMax = [Math]::Log($MaxFileBytes)

    $script:QuantileBoundaries = @()
    for ($i = 1; $i -le 10; $i++) {
        $Fraction = $i / 10
        $LogBoundary = $LogMin + ($Fraction * ($LogMax - $LogMin))
        $Boundary = [Math]::Exp($LogBoundary)
        $script:QuantileBoundaries += [int64]$Boundary
    }

    $script:QuantileCounts = @(0,0,0,0,0,0,0,0,0,0)
}

function Update-Quantiles {
    param ([int64]$SizeBytes)

    $LogSize = [Math]::Log($SizeBytes)

    for ($i = 0; $i -lt 10; $i++) {
        if ($LogSize -le [Math]::Log($script:QuantileBoundaries[$i])) {
            $script:QuantileCounts[$i]++
            return
        }
    }

    # Safety fallback (should never happen)
    $script:QuantileCounts[9]++
}

function Write-QuantileSummary {
    Write-Host "`nFile Size Distribution (Logarithmic Quantiles):" -ForegroundColor Cyan

    for ($i = 0; $i -lt 10; $i++) {
        $q = $i + 1
        $count = $script:QuantileCounts[$i]
        $boundary = Format-Size $script:QuantileBoundaries[$i]

        Write-Host ("  Q{0,-2}: {1,10} files  (<= {2})" -f $q, $count, $boundary) -ForegroundColor Gray
    }
}

# --- MAIN EXECUTION PIPELINE ---

$TargetSizeBytes = Get-SizeInBytes $TotalSize
$MinFileBytes = Get-SizeInBytes $MinFileSize
$MaxFileBytes = Get-SizeInBytes $MaxFileSize

if ($MinFileBytes -gt $MaxFileBytes) {
    throw "MinFileSize cannot be larger than MaxFileSize."
}

if (-not $TargetFolder.StartsWith("\\?\")) {
    $Resolved = Resolve-Path $TargetFolder -ErrorAction SilentlyContinue
    $TargetFolder = "\\?\" + $(if ($Resolved) { $Resolved.Path } else { $TargetFolder.Replace("/", "\") })
    # For PS 7.x, either of the following would also work:
    #  $TargetFolder = "\\?\" + ($Resolved ? $Resolved.Path : $TargetFolder.Replace("/", "\"))
    #  $TargetFolder = "\\?\" + ($Resolved.Path ?? $TargetFolder.Replace("/", "\"))
}

if ($Verbose) {
    Write-Host "Verbose Mode ON" -ForegroundColor Green
    Write-Host "Target Folder: $TargetFolder" -ForegroundColor Gray
    Write-Host "Total Size: $(Format-Size $TargetSizeBytes)" -ForegroundColor Gray
    Write-Host "Total Folders: $TotalFolders" -ForegroundColor Gray
    Write-Host "Max Folder Depth: $MaxFolderDepth" -ForegroundColor Gray
    Write-Host "Min File Size: $(Format-Size $MinFileBytes)" -ForegroundColor Gray
    Write-Host "Max File Size: $(Format-Size $MaxFileBytes)" -ForegroundColor Gray
    Write-Host "Randomization Percent: $RandomizationPercent%" -ForegroundColor Gray
    Write-Host "Force Long Paths: $ForceLongPaths" -ForegroundColor Gray
    Write-Host "Generate NTFS Streams: $GenerateNTFSStreams" -ForegroundColor Gray
}
if ($DryRun) {
    Write-Host "Dry Run Mode ON: No files or folders will be created." -ForegroundColor Yellow
}

if (-not (Test-Path $TargetFolder)) {
    Write-Host "Creating target folder..." -ForegroundColor Cyan
    if (-not $DryRun) {
        [System.IO.Directory]::CreateDirectory($TargetFolder) | Out-Null
    }
    Write-VerboseLine -Path $TargetFolder
}
else {
    Write-Host "Target folder $TargetFolder already exists." -ForegroundColor Yellow
}

$FolderList = New-DirTree -Root $TargetFolder -Count $TotalFolders -MaxDepth $MaxFolderDepth -InjectLong $ForceLongPaths

# Build chunk buffer
$ChunkSize = if ($MaxFileBytes -lt 1MB) { $MaxFileBytes } else { 1MB }
$Buffer = New-Object byte[] $ChunkSize

if ($RandomizationPercent -gt 0) {
    $Rand = New-Object System.Random
    $RandomBytesCount = [int]($ChunkSize * ($RandomizationPercent / 100))
    $RandomBytes = New-Object byte[] $RandomBytesCount
    $Rand.NextBytes($RandomBytes)
    [System.Buffer]::BlockCopy($RandomBytes, 0, $Buffer, 0, $RandomBytesCount)
}

SetUp-Quantiles -MinFileBytes $MinFileBytes -MaxFileBytes $MaxFileBytes

$CurrentSizeBytes = [int64]0
$FileCounter = 1
$RandEngine = New-Object System.Random

Write-Host "Streaming log-distributed dataset files..." -ForegroundColor Green
$Stopwatch = [System.Diagnostics.Stopwatch]::StartNew()

while ($CurrentSizeBytes -lt $TargetSizeBytes) {
    $SelectedFolder = $FolderList[$RandEngine.Next(0, $FolderList.Count)]
    
    $TargetFileLengthBytes = Get-NextLogarithmicSize -Rand $RandEngine -MinBytes $MinFileBytes -MaxBytes $MaxFileBytes
    
    if (($CurrentSizeBytes + $TargetFileLengthBytes) -gt $TargetSizeBytes) {
        $TargetFileLengthBytes = $TargetSizeBytes - $CurrentSizeBytes
    }

    if ($TargetFileLengthBytes -le 0) { break }

    $FolderIndex = [System.IO.Path]::GetFileName($SelectedFolder)
    $FilePath = [System.IO.Path]::Combine($SelectedFolder, "TestFile_${FolderIndex}_${FileCounter}.dat")

    try {
        if (-not $DryRun) {
            Write-TestFile -Path $FilePath -Length $TargetFileLengthBytes -SourceBuffer $Buffer
        }

        if ($GenerateNTFSStreams -and ($RandEngine.Next(1, 6) -eq 5)) {
            if (-not $DryRun) {
                Add-NtfsStream -ParentFilePath $FilePath
            }
            Write-VerboseLine -Path $FilePath -SizeBytes $TargetFileLengthBytes -Ntfs $true
        }
        else {
            Write-VerboseLine -Path $FilePath -SizeBytes $TargetFileLengthBytes
        }

        $CurrentSizeBytes += $TargetFileLengthBytes
        $FileCounter++

        Update-Quantiles -SizeBytes $TargetFileLengthBytes

        Report-Progress -CurrentSizeBytes $CurrentSizeBytes -TargetSizeBytes $TargetSizeBytes -FileCounter $FileCounter
    }
    catch {
        Write-Warning "Failed writing file $FilePath : $_"
        break
    }
}

$Stopwatch.Stop()
Write-Host "`nDataset file generation complete!" -ForegroundColor Green
Write-Host "Total Folders : $($FolderList.Count - 1)" -ForegroundColor Gray
Write-Host "Total Files : $FileCounter" -ForegroundColor Gray
Write-Host "Total Data Size: $(Format-Size $CurrentSizeBytes)" -ForegroundColor Gray
Write-Host "Execution Time : $($Stopwatch.Elapsed.TotalMinutes.ToString('F2')) minutes" -ForegroundColor Gray
Write-Host "Average Write Rate : $(Format-Size ([int64]($CurrentSizeBytes / $Stopwatch.Elapsed.TotalSeconds))) / s" -ForegroundColor Gray
Write-QuantileSummary
