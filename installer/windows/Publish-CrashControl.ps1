#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
trap { [Console]::Out.WriteLine('HERMES_CRASH_PUBLICATION v=1 status=1'); exit 1 }

Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -Force -DisableNameChecking
Assert-HermesCrashHost
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($root -cne [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)) { throw '1' }
Assert-HermesCrashReportAbsent $root
$private = Join-Path $root '.artifacts/crash-control-private'
if ([IO.File]::Exists($private) -or [IO.Directory]::Exists($private)) { throw '1' }
New-HermesCrashDirectory $private
$checkout = Get-HermesCrashCheckout $root $env:GITHUB_SHA
$sourcePins = Open-HermesCrashPins $root $checkout.Sources
$sdkPins = $null
$bundlePins = $null
$inventoryPins = $null
try {
    # Existing publishers use their normal intermediate paths. Require an actually fresh build.
    $projects = @('installer/windows/Hermes.Installer.Guard', 'installer/windows/Hermes.Setup',
        'installer/windows/Hermes.Installer.Control', 'src/Hermes.Servicing',
        'tests/Hermes.Servicing.Acceptance', 'tests/Hermes.Servicing.Acceptance.Model',
        'tests/Hermes.Servicing.ProcessProbe')
    foreach ($project in $projects) {
        foreach ($name in @('bin', 'obj')) {
            if (Test-Path -LiteralPath (Join-Path $root "$project/$name")) { throw '1' }
        }
    }
    $servicing = Join-Path $private 'servicing'
    $helpers = Join-Path $private 'helpers'
    & (Join-Path $PSScriptRoot 'Publish-Servicing.ps1') -OutputDirectory $servicing *> (Join-Path $private 'servicing.log')
    & (Join-Path $PSScriptRoot 'Publish-Acceptance.ps1') `
        -ServicingBuildPath (Join-Path $servicing 'servicing-build.json') `
        -OutputDirectory $helpers *> (Join-Path $private 'acceptance.log')
    Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force -DisableNameChecking
    $directory = Join-Path $helpers 'acceptance'
    $inventoryPath = Join-Path $helpers 'acceptance.publish.txt'
    $inventoryEntry = Get-HermesCrashFileEntry $inventoryPath 'helpers/acceptance.publish.txt' 16777216
    $inventoryPins = Open-HermesCrashPins $private @($inventoryEntry)
    $null = Get-HermesSetupPublishFiles -Directory $directory -InventoryPath $inventoryPath -AcceptanceHelpers
    Assert-HermesSdkFreeRuntime -Directory $directory -EntryName 'Hermes.Servicing.Acceptance'
    $sourceRoots = @($projects | ForEach-Object {
        Join-Path $root "$_/bin"
        Join-Path $root "$_/obj"
    })
    $sourceRoots += $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else {
        Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
    })
    if ($env:DOTNET_ROOT) { $sourceRoots += Join-Path $env:DOTNET_ROOT 'packs' }
    $inventory = Read-HermesCrashInventory $inventoryPath $directory $sourceRoots
    $sdkPins = Open-HermesCrashPins $root $inventory.Sources -Absolute
    $bundlePins = Open-HermesCrashPins $directory $inventory.Bundle
    $fresh = Get-HermesCrashCheckout $root $env:GITHUB_SHA
    if ($fresh.Tree -cne $checkout.Tree -or $fresh.SourceSha256 -cne $checkout.SourceSha256) { throw '1' }
    Assert-HermesCrashPins $sourcePins
    Assert-HermesCrashPins $sdkPins
    Assert-HermesCrashPins $bundlePins
    Assert-HermesCrashPins $inventoryPins
    Assert-HermesCrashTree $directory $inventory.Bundle
    $receipt = [ordered]@{
        schemaVersion = 1; revision = $checkout.Revision; tree = $checkout.Tree
        sourceSha256 = $checkout.SourceSha256; bundleSha256 = (Get-HermesCrashEntryDigest $inventory.Bundle)
        sources = $checkout.Sources; bundle = $inventory.Bundle; sdkSources = $inventory.Sources
    }
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(($receipt | ConvertTo-Json -Depth 8 -Compress))
    if ($bytes.Length -gt 16777216) { throw '1' }
    $file = [IO.File]::Open((Join-Path $private 'publication.json'), [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
} finally {
    Close-HermesCrashPins $bundlePins
    Close-HermesCrashPins $inventoryPins
    Close-HermesCrashPins $sdkPins
    Close-HermesCrashPins $sourcePins
}
[Console]::Out.WriteLine('HERMES_CRASH_PUBLICATION v=1 status=0')
exit 0
