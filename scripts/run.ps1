#!/usr/bin/env pwsh
# Hermes dev script: build React + start service
# Usage: pwsh scripts/run.ps1

$ErrorActionPreference = "Stop"
$root = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { "c:\work\hermes" }

# Kill any running instances first
Get-Process -Name "Hermes.Service" -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process -Name "Hermes.Tray" -ErrorAction SilentlyContinue | Stop-Process -Force
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
Write-Host "Starting Hermes on http://localhost:21741" -ForegroundColor Green
Write-Host "Press Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

dotnet run --project "$root\src\Hermes.Service" --no-build
