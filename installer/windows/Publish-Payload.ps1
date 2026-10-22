#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$InventoryDirectory,
    [string]$RuntimeVersion = '0.1.0'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'The supported Windows payload must be published on Windows.' }
Import-Module (Join-Path $PSScriptRoot 'Installer-Identity.psm1') -Force
Assert-HermesPackageVersion $RuntimeVersion
$payload = [IO.Path]::GetFullPath($PayloadDirectory)
$inventory = [IO.Path]::GetFullPath($InventoryDirectory)
foreach ($directory in @($payload, $inventory)) {
    if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -LiteralPath $directory -Force).Count -ne 0) {
        throw 'Publish requires fresh empty output and inventory directories; existing files are never deleted.'
    }
    [IO.Directory]::CreateDirectory($directory) | Out-Null
}
$targets = Join-Path $PSScriptRoot 'Publish-Inventory.targets'
$versionParts = $RuntimeVersion.Split('.')
$assemblyVersion = "$($versionParts[0]).$($versionParts[1]).0.0"
foreach ($part in @('Service', 'Tray')) {
    $directory = $part.ToLowerInvariant()
    $project = Join-Path $PSScriptRoot "../../src/Hermes.$part/Hermes.$part.$(if ($part -eq 'Service') { 'fsproj' } else { 'csproj' })"
    & dotnet publish $project -c Release -r win-x64 --self-contained true -v q `
        -o (Join-Path $payload $directory) "-p:Version=$RuntimeVersion" `
        "-p:AssemblyVersion=$assemblyVersion" "-p:FileVersion=$RuntimeVersion.0" `
        -p:IsTransformWebConfigDisabled=true -p:PublishSingleFile=false `
        "-p:CustomAfterMicrosoftCommonTargets=$targets" `
        "-p:HermesPublishInventoryPath=$(Join-Path $inventory "$directory.publish.txt")"
    if ($LASTEXITCODE -ne 0) { throw "Windows $part publish failed." }
}
& node (Join-Path $PSScriptRoot 'payload.mjs') verify $payload $inventory
if ($LASTEXITCODE -ne 0) { throw 'Windows publish inventory verification failed.' }
