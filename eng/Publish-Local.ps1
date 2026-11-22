$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $browser = Join-Path $root 'artifacts/browser'
    $server = Join-Path $root 'artifacts/server'
    & dotnet publish src/Studio/EntityForge.Db.Studio.Browser/EntityForge.Db.Studio.Browser.csproj -c Release -o $browser
    if ($LASTEXITCODE -ne 0) { throw 'Browser publish failed.' }
    & dotnet publish src/Db/EntityForge.Db.Server/EntityForge.Db.Server.csproj -c Release -o $server
    if ($LASTEXITCODE -ne 0) { throw 'Server publish failed.' }
    $assets = Join-Path $browser 'wwwroot'
    if (!(Test-Path -LiteralPath (Join-Path $assets 'index.html'))) { throw "Missing browser assets: $assets" }
    $studio = Join-Path $server 'studio'
    New-Item -ItemType Directory -Force -Path $studio | Out-Null
    Copy-Item -Path (Join-Path $assets '*') -Destination $studio -Recurse -Force
    Write-Host "Server and Studio published to $server"
}
finally { Pop-Location }
