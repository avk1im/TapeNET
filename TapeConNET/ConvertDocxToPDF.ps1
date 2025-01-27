# PowerShell script to convert DOCX files to PDF

param (
    [Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)]
    [string[]]$DocxFiles
)

# Function to convert a single DOCX file to PDF
function Convert-DocxToPdf {
    param (
        [string]$DocxFile
    )

    # Ensure the DOCX file exists
    if (-Not (Test-Path -Path $DocxFile)) {
        Write-Error "File not found: $DocxFile"
        return
    }

    # Get the full path of the DOCX file
    $DocxFilePath = (Get-Item -Path $DocxFile).FullName

    # Determine the output PDF file path
    $PdfFilePath = [System.IO.Path]::ChangeExtension($DocxFilePath, ".pdf")

    # Create a new Word application instance
    $Word = New-Object -ComObject Word.Application
    $Word.Visible = $false

    try {
        # Open the DOCX file
        $Document = $Word.Documents.Open($DocxFilePath)

        # Save the document as PDF
        $Document.SaveAs([ref] $PdfFilePath, [ref] 17)  # 17 is the WdSaveFormat for PDF

        Write-Output "Converted: $DocxFilePath -> $PdfFilePath"
    }
    catch {
        Write-Error "Failed to convert $DocxFile : $($_.Exception.Message)"
    }
    finally {
        # Close the document and quit Word
        $Document.Close([ref] $false)
        $Word.Quit()
    }
}

# Convert each DOCX file to PDF
foreach ($DocxFile in $DocxFiles) {
    Convert-DocxToPdf -DocxFile $DocxFile
}
