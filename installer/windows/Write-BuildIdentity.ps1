#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][Guid]$ProductCode,
    [Parameter(Mandatory = $true)][Guid]$PackageCode,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Installer-Identity.psm1') -Force
Assert-HermesPackageVersion $Version
$upgrade = [Guid]'7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A'
if ($ProductCode -eq [Guid]::Empty -or $PackageCode -eq [Guid]::Empty `
    -or $ProductCode -eq $PackageCode -or $ProductCode -eq $upgrade -or $PackageCode -eq $upgrade) {
    throw 'Build product/package identity must be explicit, nonempty and distinct.'
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))) | Out-Null
[ordered]@{ productCode = $ProductCode.ToString('D').ToUpperInvariant(); packageCode = $PackageCode.ToString('D').ToUpperInvariant(); version = $Version } `
    | ConvertTo-Json | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
