#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Acceptance-Report.psm1') -Force
$hash = 'a' * 64
$other = 'b' * 64
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'obj/synthetic-report-only'))
$inputs = foreach ($name in @('old.msi', 'new.msi', 'Hermes.Setup.exe', 'guard.dll', 'old/Hermes.Service.dll', 'new/Hermes.Service.dll', 'Hermes.Setup.dll')) {
    [ordered]@{ path = Join-Path $root $name; sha256 = if ($name -eq 'new.msi') { $other } else { $hash } }
}
$owner = foreach ($name in @('config', 'settings', 'marker', 'receipt', 'archive', 'unknown1', 'unknown2', 'unknown3')) {
    [ordered]@{ path = Join-Path $root $name; sha256 = $hash; length = 10 }
}
$expectation = [ordered]@{ ownerFixtureId = '11111111-1111-1111-1111-111111111111'
    job = @{ runId = 'synthetic-run'; attempt = '1'; job = 'installer' }; inputs = @($inputs); ownerUnknown = @($owner) }
$process = @{ processId = 100; creationFileTime = 1000 }

function New-NativeBinding([long]$Cursor) {
    [ordered]@{ cursor = $Cursor; reporter = @{
        providerId = 'c760c1ab-37cb-4c3a-a245-3ea632fc9c5d'; processId = 100; eventFileTime = 2000
    }; process = @{ processId = 100; creationFileTime = 1000 }; observedFileTime = 3000 }
}

function New-Fact([long]$Cursor, [string]$Kind, $Diagnostic = $null, $Outcome = $null) {
    [ordered]@{ cursor = $Cursor; kind = $Kind; diagnostic = $Diagnostic; outcome = $Outcome
        nativeWriter = if ($null -ne $Diagnostic) { New-NativeBinding $Cursor } else { $null } }
}
function New-Diagnostic([string]$Kind, [string]$Run, [string]$Phase = '', [int]$Status = 0, [string]$Action = '') {
    [ordered]@{ schemaVersion = 1; kind = $Kind; runId = $Run; phase = $Phase; status = $Status
        action = $Action; failures = 0; upgradingProductCode = $null
        writer = @{ processId = 100; creationFileTime = 1000 }
        coordinator = @{ processId = 100; creationFileTime = 1000 } }
}
function New-CrashCase([string]$Name) {
    $oldRun = '22222222-2222-2222-2222-222222222222'
    $refused = $Name -in @('interrupted-install', 'interrupted-repair', 'interrupted-uninstall')
    $status = if ($refused) { 1603 } else { 0 }
    $after = if ($Name -in @('interrupted-upgrade', 'interrupted-explicit-rollback')) { 4 } else { $null }
    $facts = @((New-Fact 2 'Begin'), (New-Fact 3 'Diagnostic' (New-Diagnostic 'GuardDecision' $oldRun 'Transaction' $status)))
    if ($null -ne $after) { $facts += New-Fact 4 'Diagnostic' (New-Diagnostic 'ActionStart' $oldRun '' 0 'RemoveExistingProducts') }
    $facts += @((New-Fact 5 'Outcome' $null @{ status = $status }), (New-Fact 6 'End'))
    $fresh = @()
    $receipts = @()
    $events = @()
    $count = if ($Name -eq 'already-absent-bootstrap') { 2 } else { 1 }
    for ($index = 0; $index -lt $count; $index++) {
        $start = 7 + 6 * $index
        $run = if ($index -eq 0) { '33333333-3333-3333-3333-333333333333' } else { '44444444-4444-4444-4444-444444444444' }
        $fresh += [ordered]@{ runId = $run; beginCursor = $start; preflightCursor = $start + 1; transactionCursor = $start + 2
            installEndCursor = $start + 3; outcomeCursor = $start + 4; endCursor = $start + 5 }
        $facts += @((New-Fact $start 'Begin'),
            (New-Fact ($start + 1) 'Diagnostic' (New-Diagnostic 'GuardDecision' $run 'Preflight')),
            (New-Fact ($start + 2) 'Diagnostic' (New-Diagnostic 'GuardDecision' $run 'Transaction')),
            (New-Fact ($start + 3) 'Diagnostic' (New-Diagnostic 'InstallEnd' $run)),
            (New-Fact ($start + 4) 'Outcome' $null @{ status = 0 }), (New-Fact ($start + 5) 'End'))
        $receipt = [ordered]@{ apiEntered = $true; apiReturned = $true; controlFaulted = $false; msiExitCode = 0
            ownPreflightOccurred = $true; ownTransactionFenceOccurred = $true }
        $receipts += $receipt
        $events += [ordered]@{ kind = 'api-returned'; observationFailures = 0; receipt = @{
            runId = $run; apiEntered = $true; apiReturned = $true; controlFaulted = $false; msiExitCode = 0; observationFaults = 0
            fences = @(@{ ownSession = $true; phase = 'Preflight'; runId = $run; caHost = $process },
                @{ ownSession = $true; phase = 'Transaction'; runId = $run; caHost = $process })
        } }
    }
    $independent = [ordered]@{ schemaProfile = 'manifest-application-msi-english-v1'; subscriptionId = '55555555-5555-5555-5555-555555555555'
        readyWriter = (New-NativeBinding 1)
        snapshot = @{ readyCursor = 1; faults = @(); facts = $facts }
        packages = @(@{ engineSha256 = $hash; source = @{ wholeMsiSha256 = $hash; manifestSha256 = $other
            productCode = '66666666-6666-6666-6666-666666666666'; packageCode = '77777777-7777-7777-7777-777777777777' } })
        writers = @(@{ processId = 100; startUtcFileTime = 1000; executableSha256 = $hash })
    }
    $bound = [ordered]@{ independent = $independent; proof = @{ old = @{ beginCursor = 2; decisionCursor = 3
        afterFenceActionCursor = $after; outcomeCursor = 5; endCursor = 6; status = $status }; fresh = $fresh }
        attempts = @(@{ exitCode = 0; events = $events; result = @{ exitCode = 0; diagnosticCode = 'None'
            report = @{ ready = $true; outcome = 'Completed' }; engineReceipts = $receipts } })
    }
    $trace = @{ oldEvents = @(@{ kind = 'paused'; receipt = $null
        fence = @{ caHost = $process; ownSession = $true; phase = 'Transaction' } }) }
    [ordered]@{ name = $Name; scope = 'actual-msi-root-composition'; state = 'Passed'; evidence = @($bound, $trace); missingEvidence = $null }
}
$cases = @([ordered]@{ name = 'windows-kernel-admission'; scope = 'production-component'; state = 'Passed'; missingEvidence = $null; evidence = @(
    @{ boundary = 'thread-death-gc-independent-process-exit' }, @{ boundary = 'exclusive-wins-durable-closed' },
    @{ boundary = 'old-image-mapped-before-generation-publication' }) })
foreach ($name in @('interrupted-install', 'interrupted-repair', 'interrupted-upgrade', 'interrupted-explicit-rollback', 'interrupted-uninstall', 'already-absent-bootstrap')) { $cases += New-CrashCase $name }
$cases += [ordered]@{ name = 'owner-and-unknown-preservation'; scope = 'job-bound-synthetic-fixture'; state = 'Passed'; evidence = @(@{ preserved = $true }); missingEvidence = $null }
$captured = @($owner | ForEach-Object { [ordered]@{ path = $_.path; exists = $true; length = $_.length; sha256 = $_.sha256 } })
$valid = [ordered]@{ schemaVersion = 1; kind = 'hermes-a2-actual-component'; passed = $true; complete = $true
    scope = 'component; not complete installed A2 acceptance'; job = $expectation.job; ownerFixtureId = $expectation.ownerFixtureId
    inputs = $inputs; ownerUnknownBefore = $captured; ownerUnknownAfter = $captured; cases = $cases
    cleanup = @{ attempted = $true; completed = $true; finalAdmission = 'ReadyAbsent'; evidence = @(@{ complete = $true }); failure = $null }; failure = $null }
$json = $valid | ConvertTo-Json -Depth 100 -Compress
$validReport = $json | ConvertFrom-Json -Depth 100
Assert-HermesServicingComponentReport $validReport $expectation 0
$controls = @(
    { param($r) $r.complete = $false },
    { param($r) $r.passed = $false },
    { param($r) $r.job.runId = 'another-job' },
    { param($r) $r.inputs[0].sha256 = '0' * 64 },
    { param($r) $r.cases = @($r.cases | Where-Object name -NE 'interrupted-install') },
    { param($r) $r.cases[1].state = 'Blocked' },
    { param($r) $r.cases[1].scope = 'installed-service' },
    { param($r) $r.cleanup.completed = $false },
    { param($r) $r.ownerUnknownAfter[0].sha256 = '0' * 64 },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.faults = @('unsupported-native-schema') },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[0].cursor = 1 },
    { param($r) $r.cases[1].evidence[0].proof.old.endCursor = 999 },
    { param($r) $r.cases[3].evidence[0].proof.old.afterFenceActionCursor = $null },
    { param($r) $r.cases[1].evidence[0].attempts[0].result.engineReceipts[0].ownTransactionFenceOccurred = $false },
    { param($r) $r.cases[1].evidence[0].attempts[0].events[0].receipt.observationFaults = 1 },
    { param($r) $r.cases[1].evidence[0].independent.schemaProfile = 'classic-application-msi-english-v0' },
    { param($r) $r.cases[1].evidence[0].independent.readyWriter = $null },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter = $null },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter.reporter.processId = 0 },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter.reporter.processId = 101 },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter.reporter.providerId = [Guid]::Empty.ToString() },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter.reporter.eventFileTime = 999 },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter.observedFileTime = 1999 },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter.cursor = 2 },
    { param($r) $r.cases[1].evidence[0].independent.snapshot.facts[1].nativeWriter.process.creationFileTime = 1001 },
    { param($r) $r.cases[1].evidence[1].oldEvents[0].fence.caHost.processId = 101 },
    { param($r) $r.cases[1].evidence[0].attempts[0].events[0].receipt.fences[0].caHost.processId = 101 }
)
foreach ($mutate in $controls) {
    $report = $json | ConvertFrom-Json -Depth 100
    & $mutate $report
    $refused = $false
    try { Assert-HermesServicingComponentReport $report $expectation 0 } catch { $refused = $true }
    if (-not $refused) { throw 'A bad/incomplete synthetic report was accepted by the publication parser.' }
}
$refused = $false
try { Assert-HermesServicingComponentReport $validReport $expectation 3 } catch { $refused = $true }
if (-not $refused) { throw 'Exit 3 must never authorize publication even with claimed success flags.' }

$nativeRow = [pscustomobject][ordered]@{
    action = 'install'; kind = 'coordinator'; exitCode = 1603
    report = [pscustomobject]@{ schemaVersion = 1; operation = 'Install'; outcome = 'RecoveryRequired'; ready = $false; msiExitCode = 1603 }
    diagnosticCodes = @('TransactionFenceMissing'); protocolError = $null
    refusalEvidence = $null; diagnosticError = $null; sdkPathAbsent = $true
    msiFailureEvidence = [pscustomobject]@{ v = 1; action = 1399; error = 1723; readFailed = 0; preflight = 1; fence = 0 }
    nativePreflightEvidence = $null
}
$nativeJson = $nativeRow | ConvertTo-Json -Depth 8 -Compress
Assert-HermesCoordinatorStages @($nativeRow)
$nativeControls = @(
    { param($r) $r.msiFailureEvidence.fence = 1 },
    { param($r) $r.msiFailureEvidence.preflight = 0; $r.msiFailureEvidence.fence = 1 },
    { param($r) $r.msiFailureEvidence.action = 9999 },
    { param($r) $r.msiFailureEvidence.error = 65536 },
    { param($r) $r.msiFailureEvidence.error = '1723' },
    { param($r) $r.msiFailureEvidence.readFailed = $true },
    { param($r) $r.msiFailureEvidence | Add-Member -NotePropertyName source -NotePropertyValue 'SYNTHETIC_SECRET' },
    { param($r) $r.msiFailureEvidence.PSObject.Properties.Remove('error') },
    { param($r) $r.PSObject.Properties.Remove('msiFailureEvidence') },
    { param($r) $r | Add-Member -NotePropertyName source -NotePropertyValue 'SYNTHETIC_SECRET' },
    { param($r) $r.kind = 'raw-msi-negative' },
    { param($r) $r.refusalEvidence = [pscustomobject]@{ v = 1; stage = 8; cause = 4; fault = 0; errorKind = 0; error = 0 } },
    { param($r) $r.protocolError = 'InvalidCoordinatorDiagnostics' },
    { param($r) $r.diagnosticError = 'MixedCoordinatorEvidence' },
    { param($r) $r.diagnosticCodes = @('TransactionFenceMissing', 'None') },
    { param($r) $r.report.msiExitCode = $null },
    { param($r) $r.report.outcome = 'Refused'; $r.report.msiExitCode = $null },
    { param($r) $r.report.outcome = 'Completed'; $r.report.ready = $true; $r.report.msiExitCode = 0; $r.exitCode = 0 },
    { param($r) $r.report.outcome = 'RolledBack'; $r.report.ready = $true },
    { param($r) $r.report.outcome = 'RebootRequired'; $r.report.msiExitCode = 3010; $r.exitCode = 3010 },
    { param($r) $r.exitCode = 1618 }
)
foreach ($mutate in $nativeControls) {
    $row = $nativeJson | ConvertFrom-Json
    & $mutate $row
    $beforeValidation = $row | ConvertTo-Json -Depth 8 -Compress
    $refused = $false
    try { Assert-HermesCoordinatorStages @($row) } catch { $refused = $true }
    if (-not $refused) { throw 'A contradictory or unbound coordinator MSI observation passed the publication consumer.' }
    if (($row | ConvertTo-Json -Depth 8 -Compress) -cne $beforeValidation) {
        throw 'Coordinator MSI validation mutated the original report, exit or evidence.'
    }
}
$row = $nativeJson | ConvertFrom-Json
$row.msiFailureEvidence = $null
$row.report.outcome = 'Completed'
$row.report.ready = $true
$row.report.msiExitCode = 0
$row.exitCode = 0
$row.diagnosticCodes = @('None')
Assert-HermesCoordinatorStages @($row)

$hintRow = $nativeJson | ConvertFrom-Json
$hintRow.msiFailureEvidence.preflight = 0
$hintRow.nativePreflightEvidence = [pscustomobject]@{
    v = 1; source = 1; observed = 1; point = 2; reason = 11
}
$hintJson = $hintRow | ConvertTo-Json -Depth 8 -Compress
Assert-HermesCoordinatorStages @($hintRow)
$hintControls = @(
    { param($r) $r.nativePreflightEvidence.v = 2 },
    { param($r) $r.nativePreflightEvidence.source = 2 },
    { param($r) $r.nativePreflightEvidence.observed = 0 },
    { param($r) $r.nativePreflightEvidence.observed = $true },
    { param($r) $r.nativePreflightEvidence.point = '2' },
    { param($r) $r.nativePreflightEvidence.point = 0 },
    { param($r) $r.nativePreflightEvidence.point = 10 },
    { param($r) $r.nativePreflightEvidence.reason = 3 },
    { param($r) $r.nativePreflightEvidence.reason = 23 },
    { param($r) $r.nativePreflightEvidence.point = 6 },
    { param($r) $r.nativePreflightEvidence | Add-Member -NotePropertyName token -NotePropertyValue 'SYNTHETIC_SECRET' },
    { param($r) $r.nativePreflightEvidence.PSObject.Properties.Remove('reason') },
    { param($r) $r.PSObject.Properties.Remove('nativePreflightEvidence') },
    { param($r) $r.msiFailureEvidence = $null },
    { param($r) $r.msiFailureEvidence.preflight = 1 },
    { param($r) $r.report.ready = $true },
    { param($r) $r.diagnosticError = 'InvalidCoordinatorDiagnostics' }
)
foreach ($mutate in $hintControls) {
    $row = $hintJson | ConvertFrom-Json
    & $mutate $row
    $beforeValidation = $row | ConvertTo-Json -Depth 8 -Compress
    $rejected = $false
    try { Assert-HermesCoordinatorStages @($row) } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid or unbound native-preflight data reached the publication consumer.' }
    if (($row | ConvertTo-Json -Depth 8 -Compress) -cne $beforeValidation) {
        throw 'Native-preflight validation mutated coordinator evidence.'
    }
}
Write-Output "Synthetic report-parser controls passed: valid shape plus $($controls.Count + 1) adverse/incomplete/blocked controls. These are not Windows evidence."
