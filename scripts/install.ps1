#Requires -Version 5.1
<#
.SYNOPSIS
    Install, upgrade, or uninstall the Hermes service.

.DESCRIPTION
    Publishes Hermes as a self-contained deployment and registers it
    as a Windows Task Scheduler task that runs at user logon.

.PARAMETER Uninstall
    Remove the scheduled task and optionally the install directory.

.PARAMETER SkipBuild
    Skip the npm + dotnet publish steps (use existing binaries).

.EXAMPLE
    .\install.ps1              # Build, publish, register
    .\install.ps1 -Uninstall   # Remove task, keep data
    .\install.ps1 -SkipBuild   # Re-register without rebuilding
#>
[CmdletBinding()]
param(
    [switch]$Uninstall,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$TaskName = 'Hermes'
$InstallDir = Join-Path $env:LOCALAPPDATA 'Hermes'
$ExePath = Join-Path $InstallDir 'Hermes.Service.exe'
$RepoRoot = Split-Path -Parent $PSScriptRoot
$WebDir = Join-Path $RepoRoot 'src' 'Hermes.Web'
$ServiceProj = Join-Path $RepoRoot 'src' 'Hermes.Service'
$LogDir = Join-Path $env:APPDATA 'hermes' 'logs'

function Write-Step($msg) { Write-Host "  → $msg" -ForegroundColor Cyan }

# ── Uninstall ─────────────────────────────────────────────────────

if ($Uninstall) {
    Write-Host '🗑️  Uninstalling Hermes...' -ForegroundColor Yellow

    # Stop and remove scheduled task
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($task) {
        if ($task.State -eq 'Running') {
            Write-Step 'Stopping running task...'
            Stop-ScheduledTask -TaskName $TaskName
        }
        Write-Step 'Removing scheduled task...'
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
    }
    else {
        Write-Step 'No scheduled task found'
    }

    # Kill any running process
    Get-Process -Name 'Hermes.Service' -ErrorAction SilentlyContinue | Stop-Process -Force

    # Optionally remove install directory
    if (Test-Path $InstallDir) {
        $remove = Read-Host "Remove install directory ($InstallDir)? [y/N]"
        if ($remove -eq 'y') {
            Remove-Item $InstallDir -Recurse -Force
            Write-Step 'Install directory removed'
        }
        else {
            Write-Step 'Install directory preserved'
        }
    }

    Write-Host '✅ Hermes uninstalled. Config and archive preserved.' -ForegroundColor Green
    Write-Host "   Config: $env:APPDATA\hermes\"
    Write-Host "   Archive: $env:USERPROFILE\Documents\Hermes\"
    exit 0
}

# ── Install / Upgrade ─────────────────────────────────────────────

Write-Host '⚡ Installing Hermes...' -ForegroundColor Cyan

# Stop existing task if running
$existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($existingTask -and $existingTask.State -eq 'Running') {
    Write-Step 'Stopping existing Hermes task...'
    Stop-ScheduledTask -TaskName $TaskName
    Start-Sleep -Seconds 2
}

# Kill any running process
Get-Process -Name 'Hermes.Service' -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1

if (-not $SkipBuild) {
    # Build Blazor UI CSS (Tailwind)
    Write-Step 'Building Blazor UI CSS...'
    Push-Location $WebDir
    try {
        npm run build:blazor 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Blazor CSS build failed' }
    }
    finally { Pop-Location }

    # Publish .NET service
    Write-Step "Publishing to $InstallDir..."
    dotnet publish $ServiceProj -c Release -r win-x64 --self-contained -o $InstallDir 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
}

# Verify the executable exists
if (-not (Test-Path $ExePath)) {
    throw "Publish failed — $ExePath not found"
}

# Create log directory
if (-not (Test-Path $LogDir)) {
    New-Item -ItemType Directory -Path $LogDir -Force | Out-Null
}

# Register scheduled task
Write-Step 'Registering scheduled task...'
$action = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Days 365)

# Remove existing task if present (idempotent)
if ($existingTask) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Description 'Hermes Document Intelligence Service' `
    -RunLevel Limited | Out-Null

# Start the task
Write-Step 'Starting Hermes...'
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 3

# Verify
$running = Get-Process -Name 'Hermes.Service' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host '✅ Hermes installed and running!' -ForegroundColor Green
}
else {
    Write-Host '⚠️  Hermes installed but may not be running yet. Check Task Scheduler.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host "   Install:  $InstallDir"
Write-Host "   Config:   $env:APPDATA\hermes\"
Write-Host "   Archive:  $env:USERPROFILE\Documents\Hermes\"
Write-Host "   Logs:     $LogDir"
Write-Host "   Web UI:   http://localhost:21741"
Write-Host ''
Write-Host '   To upgrade: run this script again'
Write-Host '   To stop:    Stop-ScheduledTask -TaskName Hermes'
Write-Host '   To remove:  .\install.ps1 -Uninstall'
