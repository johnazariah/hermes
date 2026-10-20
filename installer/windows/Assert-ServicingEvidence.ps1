#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$EvidenceDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Acceptance-Report.psm1') -Force
$ordinaryPath = Join-Path $EvidenceDirectory 'acceptance.json'
$componentPath = Join-Path $EvidenceDirectory 'actual-servicing.json'
if ((Get-Item -LiteralPath $ordinaryPath).Length -gt 8MB -or (Get-Item -LiteralPath $componentPath).Length -gt 32MB) {
    throw 'Owned final evidence exceeds its allowed bound.'
}
$ordinary = Get-Content -LiteralPath $ordinaryPath -Raw | ConvertFrom-Json -Depth 100
$component = Get-Content -LiteralPath $componentPath -Raw | ConvertFrom-Json -Depth 100
if (-not $ordinary.passed -or $null -ne $ordinary.originalFailure -or @($ordinary.cleanupFailures).Count -ne 0) {
    throw 'Ordinary installed acceptance did not complete cleanly.'
}
foreach ($name in @('install', 'repair', 'upgrade', 'rollback', 'corrective-missing', 'corrective-changed')) {
    $rows = @($ordinary.stages | Where-Object { $_.PSObject.Properties['stage'] -and $_.stage -ceq $name -and $_.PSObject.Properties['runtime'] })
    if ($rows.Count -ne 1) { throw 'The component report cannot substitute for every required actual installed-Tray proof.' }
    $proof = $rows[0].runtime
    if (-not $rows[0].sdkPathAbsent -or -not $proof.passed -or -not $proof.cleaned -or -not $proof.installedTrayLaunched `
        -or $proof.trayProcessId -le 0 -or $proof.trayStartUtcTicks -le 0 -or $proof.serviceStartUtcTicks -le 0 `
        -or -not $proof.installedSourceUnchanged -or -not $proof.ownerStateVerified -or -not $proof.ownerFixtureVerified `
        -or [Guid]$proof.ownerFixtureId -ne [Guid]$ordinary.ownerFixtureId `
        -or -not $proof.stop.graceful -or $proof.stop.forced -or -not $proof.stop.exited -or $proof.stop.exitCode -ne 0) {
        throw 'Actual installed native/protected ownership or graceful same-instance proof is incomplete.'
    }
}
Assert-HermesServicingComponentReport $component $ordinary.componentExpectation $ordinary.componentExitCode
Write-Output 'Both independently scoped final evidence sets passed; blocked/unknown native engine evidence cannot authorize publication.'
