#!/usr/bin/env pwsh
# Hermes dev script: build React + start service on dev port with dev config
# Uses separate config/archive from the installed production service.
# Usage: pwsh scripts/run.ps1

$ErrorActionPreference = "Stop"
$root = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { "c:\work\hermes" }

# Dev environment — separate from production
$env:HERMES_CONFIG_DIR = Join-Path $env:APPDATA "hermes-dev"
$env:HERMES_PORT = "21742"
$devConfigDir = $env:HERMES_CONFIG_DIR

# Ensure dev config dir exists and has a config
if (-not (Test-Path $devConfigDir)) {
    New-Item -ItemType Directory -Path $devConfigDir -Force | Out-Null
    # Copy production config as starting point if it exists
    $prodConfig = Join-Path $env:APPDATA "hermes" "config.yaml"
    if (Test-Path $prodConfig) {
        $content = Get-Content $prodConfig -Raw
        # Change archive dir to Hermes-dev
        $devArchive = Join-Path $env:USERPROFILE "Documents" "Hermes-dev"
        $content = $content -replace '(?m)^archive_dir:.*$', "archive_dir: $devArchive"
        Set-Content (Join-Path $devConfigDir "config.yaml") $content
        Write-Host "Created dev config from production config" -ForegroundColor Yellow
        Write-Host "  Archive: $devArchive" -ForegroundColor Yellow
    }
}

# Kill any running dev instances (but NOT the production scheduled task)
Get-Process -Name "Hermes.Service" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "*Debug*" -or $_.Path -like "*dotnet*" } |
    Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Write-Host "Building React app..." -ForegroundColor Cyan
Push-Location "$root\src\Hermes.Web"
npm run build
if ($LASTEXITCODE -ne 0) { Pop-Location; throw "React build failed" }
Pop-Location

Write-Host "Building .NET service..." -ForegroundColor Cyan
dotnet build "$root\src\Hermes.Service" --no-restore
if ($LASTEXITCODE -ne 0) { throw ".NET build failed" }

Write-Host ""
Write-Host "Starting Hermes DEV on http://localhost:$($env:HERMES_PORT)" -ForegroundColor Green
Write-Host "  Config:  $devConfigDir" -ForegroundColor DarkGray
Write-Host "  Production service is NOT affected." -ForegroundColor DarkGray
Write-Host "  Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

dotnet run --project "$root\src\Hermes.Service" --no-build
