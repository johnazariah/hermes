#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$InventoryDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$PackageVersion,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$GuardPath,
    [Parameter(Mandatory = $true)][string]$ServicingBuildPath,
    [Parameter(Mandatory = $true)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$ExpectedGuardSha256
)
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'MSI builds and ICE validation require Windows.' }
Import-Module (Join-Path $PSScriptRoot 'Installer-Identity.psm1') -Force
Assert-HermesPackageVersion $PackageVersion
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ((Test-Path -LiteralPath $output) -and (-not (Test-Path -LiteralPath $output -PathType Container) `
    -or @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0)) {
    throw 'MSI publication requires a fresh empty output directory; existing files are never overwritten or deleted.'
}
$intermediate = Join-Path $output 'obj'
$project = Join-Path $PSScriptRoot 'Hermes.Installer.wixproj'
$guard = (Resolve-Path -LiteralPath $GuardPath).Path
if ($ExpectedGuardSha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'The independently approved guard hash must be canonical lowercase.' }
$guardPin = [IO.File]::Open($guard, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    if ((Get-FileHash -LiteralPath $guard -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedGuardSha256) {
        throw 'Native guard changed after independent publication; no MSI build was started.'
    }
    $productCode = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    $packageCode = [Guid]::NewGuid().ToString('B').ToUpperInvariant()
    & dotnet build $project -c Release --nologo -v minimal `
        "-p:HermesPayloadDir=$payload" "-p:HermesPackageVersion=$PackageVersion" `
        "-p:HermesInventoryDir=$([IO.Path]::GetFullPath($InventoryDirectory))" `
        "-p:HermesProductCode=$productCode" "-p:HermesPackageCode=$packageCode" `
        "-p:HermesGuardPath=$guard" `
        "-p:HermesExpectedGuardSha256=$ExpectedGuardSha256" "-p:HermesServicingBuildPath=$([IO.Path]::GetFullPath($ServicingBuildPath))" `
        "-p:OutputPath=$output/" "-p:IntermediateOutputPath=$intermediate/" "-p:HermesGeneratedDir=$output/generated"
    if ($LASTEXITCODE -ne 0) { throw 'Windows installer build failed.' }
    $package = Join-Path $output 'Hermes.Installer.msi'
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
        throw "Installer output is missing: $package"
    }
    $receipt = Get-Content -LiteralPath (Join-Path $output 'generated/ice-validation.json') -Raw | ConvertFrom-Json
    if ($receipt.schemaVersion -ne 1 -or $receipt.nativeExitCode -ne 0 `
        -or $receipt.version -cne $PackageVersion -or [Guid]$receipt.productCode -ne [Guid]$productCode `
        -or [Guid]$receipt.packageCode -ne [Guid]$packageCode) {
        throw 'Published MSI must retain the selected identity and successful complete native ICE receipt.'
    }
    & node (Join-Path $PSScriptRoot 'package-materialization.mjs') `
        (Join-Path $intermediate 'Hermes.Installer.msi') $package $receipt.packageSha256 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Published MSI materialisation differs from the validated independent package.' }
    Copy-Item -LiteralPath (Join-Path $output 'generated/payload-inventory.json') `
        -Destination (Join-Path $output 'payload-inventory.json')
    Write-Output $package
} finally { $guardPin.Dispose() }
