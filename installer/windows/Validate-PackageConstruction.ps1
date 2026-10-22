#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServicingBuildPath,
    [Parameter(Mandatory = $true)][string]$GuardPath,
    [Parameter(Mandatory = $true)][string]$ExpectedGuardSha256,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$PayloadDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' `
    -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:GITHUB_SERVER_URL -cne 'https://github.com' `
    -or $env:GITHUB_REPOSITORY -cne 'johnazariah/hermes') {
    throw 'MSI construction requires the dedicated public GitHub-hosted Windows stage; no build opt-out exists.'
}
Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force
$build = Get-Content -LiteralPath $ServicingBuildPath -Raw | ConvertFrom-Json
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$guard = (Resolve-Path -LiteralPath $GuardPath).Path
if ($build.schemaVersion -ne 1 -or $build.admissionAbi -ne 1 -or $manifest.schemaVersion -ne 1 -or $manifest.admissionAbi -ne 1 `
    -or $manifest.root -cne 'LocalApplicationData/Hermes/App' -or $manifest.scope -cne 'perUser' `
    -or $build.guardSha256 -cne $ExpectedGuardSha256 -or $guard -ine (Resolve-Path -LiteralPath $build.guardPath).Path) {
    throw 'Actual construction identity, fixed root or admission ABI does not match the frozen package contract.'
}
Assert-HermesNativeGuard -Path $guard -ExpectedSha256 $ExpectedGuardSha256
$setup = (Resolve-Path -LiteralPath $build.setupExecutable).Path
if ([IO.Path]::GetFileName($setup) -cne 'Hermes.Setup.exe') { throw 'The construction record does not identify the actual coordinator apphost.' }
$setupDirectory = [IO.Path]::GetDirectoryName($setup)
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path.TrimEnd('\')
$appRoot = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Hermes/App'
if ($setup.StartsWith($payload + '\', [StringComparison]::OrdinalIgnoreCase) `
    -or $setup.StartsWith([IO.Path]::GetFullPath($appRoot).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The distributed coordinator must remain outside both AppRoot and its MSI payload.'
}
Assert-HermesSdkFreeRuntime -Directory $setupDirectory -EntryName 'Hermes.Setup'
$compiled = Get-HermesCompiledGuardTrust (Join-Path $setupDirectory 'Hermes.Setup.dll')
if ($compiled -cne $build.approvedGuardSha256 -or $compiled.Split(';') -cnotcontains $ExpectedGuardSha256) {
    throw 'The coordinator compiled trust is not the independent guard identity selected before candidate construction.'
}
$actualSetupFiles = @(Get-HermesSetupPublishFiles -Directory $setupDirectory `
    -InventoryPath (Join-Path ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ServicingBuildPath))) 'setup.publish.txt') -WithPackagingWrappers)
if ($actualSetupFiles.Count -ne @($build.setupFiles).Count) { throw 'Published coordinator inventory changed after independent preparation.' }
foreach ($file in $actualSetupFiles) {
    $expected = @($build.setupFiles | Where-Object path -CEQ $file.path)
    if ($expected.Count -ne 1 -or $expected[0].sha256 -cne $file.sha256 -or $expected[0].length -ne $file.length) { throw 'Coordinator inventory does not match its prepared build record.' }
}
foreach ($part in @(@('service', 'Hermes.Service'), @('tray', 'Hermes.Tray'))) {
    Assert-HermesSdkFreeRuntime -Directory (Join-Path $payload $part[0]) -EntryName $part[1]
    $shared = @($manifest.files | Where-Object path -CEQ "$($part[0])/Hermes.Servicing.dll")
    if ($shared.Count -ne 1 -or $null -eq $shared[0].moduleVersionId) { throw 'Both published entrypoints require the actual managed servicing ABI image in the complete inventory.' }
}
Write-Output 'Actual guard, compiled trust, self-contained coordinator/entry payloads and A2 construction identities verified; lifecycle acceptance remains separate.'
