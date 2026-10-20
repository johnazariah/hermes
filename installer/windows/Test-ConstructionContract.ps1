#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$shared = Join-Path $repository 'src/Hermes.Servicing/bin/Release/net9.0/Hermes.Servicing.dll'
if ((Get-HermesServicingAbi $shared) -ne 1) { throw 'Actual compiled shared admission ABI differs from the frozen construction contract.' }
$refused = $false
try { $null = Get-HermesServicingAbi ([IO.Path]::Combine($PSHOME, 'System.Runtime.dll')) }
catch { $refused = $true }
if (-not $refused) { throw 'A non-servicing assembly must not establish an admission ABI.' }
$fixture = Join-Path $PSScriptRoot ('obj/construction-controls-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
foreach ($name in @('Hermes.Setup.exe', 'Hermes.Setup.dll', 'Hermes.Setup.deps.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
    [IO.File]::WriteAllText((Join-Path $fixture $name), 'SYNTHETIC metadata-control file; never executed')
}
Copy-Item -LiteralPath $shared -Destination (Join-Path $fixture 'Hermes.Servicing.dll')
[IO.File]::WriteAllText((Join-Path $fixture 'Hermes.Setup.runtimeconfig.json'),
    '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"},"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"9.0.0"}]}}')
$refused = $false
try { Assert-HermesSdkFreeRuntime $fixture 'Hermes.Setup' } catch { $refused = $true }
if (-not $refused) { throw 'A framework-dependent coordinator must not pass construction checks.' }
$lifecycle = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-Installer.ps1') -Raw
foreach ($required in @(
    "Invoke-CorrectiveRepair -Damage missing -RelativePath 'service/Hermes.Service.dll'",
    "Invoke-CorrectiveRepair -Damage changed -RelativePath 'service/wwwroot/index.html'",
    '-SamePackageInstall', 'verifiedPristine', 'fullInventoryRestored', 'actualInstalledNativeProof'
)) {
    if (-not $lifecycle.Contains($required)) { throw 'Corrective repair must retain missing/changed attested-owned-file and real native-restoration acceptance.' }
}
$gate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Validate-PackageConstruction.ps1') -Raw
foreach ($required in @('Assert-HermesNativeGuard', 'Get-HermesCompiledGuardTrust', 'Get-HermesSetupPublishFiles',
    'Assert-HermesSdkFreeRuntime', 'moduleVersionId', 'LocalApplicationData/Hermes/App', 'github-hosted')) {
    if (-not $gate.Contains($required)) { throw 'The replacement construction gate lacks a required actual-artifact check.' }
}
Write-Output 'Construction contract and corrective-repair source controls passed; no coordinator, native guard, MSI or profile operation was executed.'
