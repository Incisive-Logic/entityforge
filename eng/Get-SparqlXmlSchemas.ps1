param([Parameter(Mandatory)][string]$OutputDirectory)

$ErrorActionPreference = 'Stop'
$destination = [System.IO.Path]::GetFullPath($OutputDirectory)
[System.IO.Directory]::CreateDirectory($destination) | Out-Null
$inputs = @(
    @{
        Name = 'sparql-results-official.xsd'
        Uri = 'https://www.w3.org/2007/SPARQL/result.xsd'
        Sha256 = 'EFD767C579F4068AE501BA195E08A5367465AD5F419DE32C6D34E80A90D9B3F8'
    },
    @{
        Name = 'xml-namespace-official.xsd'
        Uri = 'https://www.w3.org/2001/xml.xsd'
        Sha256 = '61960FB3131E38022CAAD5360E2F33A3382578AB3C80CD58BD74320EDE61B20C'
    }
)
foreach ($inputFile in $inputs) {
    $path = Join-Path $destination $inputFile.Name
    if (Test-Path -LiteralPath $path) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $inputFile.Sha256) {
            throw "Existing schema differs from the pinned source: $path"
        }
        continue
    }
    $temporary = Join-Path $destination ('.schema-download-' + [guid]::NewGuid().ToString('N'))
    try {
        Invoke-WebRequest -Uri $inputFile.Uri -OutFile $temporary
        if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $inputFile.Sha256) {
            throw "Upstream schema changed: $($inputFile.Uri). Review it before updating the pin."
        }
        # Keep the original upstream bytes and notices. Never replace an existing file.
        [System.IO.File]::Move($temporary, $path, $false)
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
    }
}
[ordered]@{
    Inputs = $inputs
    Errata = 'https://www.w3.org/2001/sw/DataAccess/XMLres-errata'
    Notice = 'Original W3C inputs retained unchanged. Any test adaptation must be identified separately; these files are not EntityForge-authored schemas.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'schema-provenance.json')
Write-Output "Verified external schemas: $destination"
