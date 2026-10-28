#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PackagePath,
    [switch]$Uninstall,
    [switch]$Repair,
    [switch]$Recover,
    [switch]$AllowRollback,
    [switch]$Quiet,
    [string]$SetupExecutable
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot '../installer/windows/Install-Package.ps1') @PSBoundParameters
exit $LASTEXITCODE
