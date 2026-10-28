#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PackagePath,
    [switch]$Uninstall,
    [switch]$Repair,
    [switch]$Recover,
    [switch]$AllowRollback,
    [switch]$Quiet,
    [string]$SetupExecutable = (Join-Path $PSScriptRoot 'Hermes.Setup.exe')
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'This installer entrypoint is Windows-only.' }
$maintenance = @()
if ($Uninstall) { $maintenance += 'uninstall' }
if ($Repair) { $maintenance += 'repair' }
if ($Recover) { $maintenance += 'recover' }
if ($maintenance.Count -gt 1 -or ($maintenance.Count -ne 0 -and ($PackagePath -or $AllowRollback))) {
    throw 'Select one operation; only install accepts a package and explicit rollback.'
}
$operation = if ($maintenance.Count -eq 1) { $maintenance[0] } else { 'install' }
if ($operation -eq 'install' -and -not $PackagePath) { throw 'Supply -PackagePath for install, or select -Repair, -Uninstall or -Recover.' }
Import-Module (Join-Path $PSScriptRoot 'Setup-Process.psm1') -Force
$result = Invoke-HermesSetup -SetupExecutable $SetupExecutable -Operation $operation `
    -PackagePath $PackagePath -AllowRollback:$AllowRollback -Quiet:$Quiet
if ($null -ne $result.protocolError -or $null -eq $result.report) { throw 'Coordinator did not return a valid bounded readiness report. No raw MSI fallback was attempted.' }
$result.report | ConvertTo-Json -Compress
if ($result.exitCode -eq 3010) { Write-Warning 'A restart is required; Hermes remains not ready. No reboot or application launch was requested.' }
exit $result.exitCode
