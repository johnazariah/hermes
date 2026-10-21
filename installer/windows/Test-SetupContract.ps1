#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Setup-Process.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

foreach ($operation in @('install', 'repair', 'uninstall', 'recover')) {
    $text = [ordered]@{ schemaVersion = 1; operation = $operation.Substring(0, 1).ToUpperInvariant() + $operation.Substring(1); outcome = 'Completed'; ready = $true; msiExitCode = 0 } | ConvertTo-Json -Compress
    $result = ConvertFrom-HermesSetupResult $text 0 $operation
    Require $result.ready 'A completed public report must remain typed and ready.'
}
foreach ($case in @(
    @{ outcome = 'RolledBack'; ready = $true; native = 1603; exit = 1603 },
    @{ outcome = 'RebootRequired'; ready = $false; native = 3010; exit = 3010 },
    @{ outcome = 'Refused'; ready = $false; native = $null; exit = 1618 },
    @{ outcome = 'RecoveryRequired'; ready = $false; native = $null; exit = 1603 }
)) {
    $text = [ordered]@{ schemaVersion = 1; operation = 'Install'; outcome = $case.outcome; ready = $case.ready; msiExitCode = $case.native } | ConvertTo-Json -Compress
    $null = ConvertFrom-HermesSetupResult $text $case.exit 'install'
}
foreach ($case in @(
    @{ patch = @{ ready = $false }; exit = 0 },
    @{ patch = @{ outcome = 'RolledBack'; msiExitCode = 1603 }; exit = 0 },
    @{ patch = @{ outcome = 'RebootRequired' }; exit = 3010 },
    @{ patch = @{ outcome = 'RolledBack'; msiExitCode = 3010 }; exit = 1603 },
    @{ patch = @{ schemaVersion = '1' }; exit = 0 },
    @{ patch = @{ operation = 'Repair' }; exit = 0 },
    @{ patch = @{ msiExitCode = -1 }; exit = 0 },
    @{ patch = @{ privatePath = 'SYNTHETIC forbidden extra' }; exit = 0 }
)) {
    $report = @{ schemaVersion = 1; operation = 'Install'; outcome = 'Completed'; ready = $true; msiExitCode = 0 }
    foreach ($name in $case.patch.Keys) { $report[$name] = $case.patch[$name] }
    $refused = $false
    try { $null = ConvertFrom-HermesSetupResult ($report | ConvertTo-Json -Compress) $case.exit 'install' } catch { $refused = $true }
    Require $refused 'Invalid readiness/process status must not become wrapper success.'
}
$current = 'a' * 64
$previous = 'b' * 64
$trust = ConvertTo-HermesGuardHashList $current $previous
Require ($trust -ceq "$current;$previous") 'Compiled trust input changed its canonical form.'
$refused = $false
try { $null = ConvertTo-HermesGuardHashList ('A' * 64) } catch { $refused = $true }
Require $refused 'Noncanonical build trust input was accepted.'
$refused = $false
try { $null = Get-HermesCompiledGuardTrust ([IO.Path]::Combine($PSHOME, 'System.Runtime.dll')) }
catch {
    Require ($_.Exception.Message -like '*exactly one valid compiled guard trust list*') 'The BCL metadata reader failed before establishing absence of compiled guard trust.'
    $refused = $true
}
Require $refused 'A normal framework assembly must not acquire coordinator trust.'
$escaped = $trust.Replace(';', '%3B')
& dotnet msbuild (Join-Path $PSScriptRoot 'tests/GuardTrustInput.proj') -t:Validate -nologo -v:q "-p:HermesApprovedGuardSha256=$escaped"
if ($LASTEXITCODE -ne 0) { throw 'Canonical build trust input escaping failed.' }
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Hermes.Setup/Hermes.Setup.csproj') -Raw)
Require ($project.SelectSingleNode('/Project/ItemGroup/AssemblyMetadata[@Include="HermesApprovedGuardSha256"]').Value -ceq '$(HermesApprovedGuardSha256)') 'The actual Setup project does not compile the frozen trust metadata.'
Require ($null -ne $project.SelectSingleNode('/Project/Target[@Name="RequireApprovedHermesGuard"]/Error')) 'Setup publication must retain its compile-input validation.'
$wrapper = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Install-Package.ps1') -Raw
Require ($wrapper -notmatch 'msiexec|Get-HermesCachedPackage|Start-Process') 'The public wrapper must have no raw-MSI fallback.'
$lifecycle = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-Installer.ps1') -Raw
Require ($lifecycle -notmatch 'Invoke-Msi' -and $lifecycle.Contains('Assert-RawMsiRefused')) 'Raw MSI paths must be negative acceptance only.'
Require ($lifecycle.Contains('Invoke-Coordinator') -and $lifecycle.Contains("GetFolderPath('LocalApplicationData')")) 'Lifecycle must use the actual coordinator and fixed known-folder root.'
$publisher = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Publish-Servicing.ps1') -Raw
$producerProbe = $publisher.IndexOf('Test-HermesGuardProducerProfile $image', [StringComparison]::Ordinal)
Require ($producerProbe -ge 0 `
    -and $producerProbe -lt $publisher.IndexOf('[Runtime.InteropServices.NativeLibrary]::Load($guard)', [StringComparison]::Ordinal) `
    -and $producerProbe -lt $publisher.IndexOf('$guardHash =', [StringComparison]::Ordinal)) 'The actual producer observation must precede native loading and independent hash selection.'
& (Join-Path $PSScriptRoot 'Test-GuardProducerProfile.ps1')
Require ($publisher.IndexOf('$guardHash =', [StringComparison]::Ordinal) -lt $publisher.IndexOf('& dotnet publish $setupProject', [StringComparison]::Ordinal) `
    -and $publisher.Contains('-p:HermesApprovedGuardSha256=') -and $publisher.Contains('Get-HermesCompiledGuardTrust')) 'The independent guard hash must precede Setup compilation and be verified in its actual assembly metadata.'
$ci = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../../.github/workflows/ci.yml') -Raw
Require ($ci.Contains('Assert-ServicingEvidence.ps1') -and $ci.Contains('Publish-Acceptance.ps1')) 'Publication must execute the actual component and validate both independently scoped final evidence sets.'
$installerJob = [regex]::Match($ci, '(?ms)^  installer:\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)').Groups['body'].Value
Require ($installerJob.Contains('Publish-Servicing.ps1') -and $installerJob.Contains('Publish-Payload.ps1') `
    -and $installerJob -notmatch '(?m)^    needs:' -and -not $installerJob.Contains('download-artifact')) 'Self-contained Windows acceptance must not wait on unrelated runner allocation or consume another job''s inputs.'
$publishJob = [regex]::Match($ci, '(?ms)^  publish:\n(?<body>.*?)(?=^  [a-zA-Z0-9_-]+:|\z)').Groups['body'].Value
Require ($publishJob.Contains('needs: [build-and-test, build-tray, installer]')) 'Publication must retain every supported platform, Tray and complete installer acceptance gate.'
& (Join-Path $PSScriptRoot 'Test-SetupEvidence.ps1')
Write-Output 'Coordinator contract controls passed without executing Setup, a guard DLL, MSI, or a Windows profile operation.'
