#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Setup-Process.psm1')
$script:RequiredCases = [ordered]@{
    'windows-kernel-admission' = 'production-component'
    'interrupted-install' = 'actual-msi-root-composition'
    'interrupted-repair' = 'actual-msi-root-composition'
    'interrupted-upgrade' = 'actual-msi-root-composition'
    'interrupted-explicit-rollback' = 'actual-msi-root-composition'
    'interrupted-uninstall' = 'actual-msi-root-composition'
    'already-absent-bootstrap' = 'actual-msi-root-composition'
    'owner-and-unknown-preservation' = 'job-bound-synthetic-fixture'
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function CanonicalPath([string]$Path) { [IO.Path]::GetFullPath($Path).ToLowerInvariant() }

function Require-Fact($Facts, $Cursor, [string]$Kind, [string]$DiagnosticKind = '') {
    $rows = @($Facts | Where-Object cursor -EQ $Cursor)
    Require ($rows.Count -eq 1 -and $rows[0].kind -ceq $Kind) 'A proof cursor is not its declared independent engine event.'
    if ($DiagnosticKind) { Require ($null -ne $rows[0].diagnostic -and $rows[0].diagnostic.kind -ceq $DiagnosticKind) 'A guard decision or engine action was replaced with another diagnostic.' }
    return $rows[0]
}

function Same-Process($Left, $Right) {
    return $null -ne $Left -and $null -ne $Right `
        -and $Left.processId -gt 0 -and $Left.creationFileTime -gt 0 `
        -and $Left.processId -eq $Right.processId `
        -and $Left.creationFileTime -eq $Right.creationFileTime
}

function Assert-NativeBinding($Binding, [long]$Cursor, $Claim, $Writers) {
    Require ($null -ne $Binding -and $null -ne $Claim) 'Native reporter/process binding is missing.'
    Require ($Binding.cursor -eq $Cursor -and $null -ne $Binding.reporter `
        -and [Guid]$Binding.reporter.providerId -eq [Guid]'c760c1ab-37cb-4c3a-a245-3ea632fc9c5d' `
        -and $Binding.reporter.processId -gt 0 `
        -and $Binding.reporter.processId -eq $Claim.processId `
        -and (Same-Process $Binding.process $Claim)) 'Native reporter contradicts the cursor or claimed writer.'
    Require ($Binding.reporter.eventFileTime -gt 0 `
        -and $Binding.reporter.eventFileTime -ge $Binding.process.creationFileTime `
        -and $Binding.observedFileTime -ge $Binding.reporter.eventFileTime) 'Native writer creation/event/observation identity is inconsistent.'
    $matching = @($Writers | Where-Object {
        $_.processId -eq $Binding.process.processId `
            -and $_.startUtcFileTime -eq $Binding.process.creationFileTime
    })
    Require ($matching.Count -eq 1 -and $matching[0].executableSha256 -cmatch '\A[0-9a-f]{64}\z') 'Native reporter has no unique OS-observed image identity.'
}

function Assert-ExpectedGuardWriter($Facts, $Decision, $Host) {
    Require ((Same-Process $Decision.diagnostic.writer $Host) `
        -and (Same-Process $Decision.nativeWriter.process $Host)) 'The native guard reporter is not the independently observed CA host.'
}

function Get-OldGuardHost($Case) {
    $traces = @($Case.evidence | Where-Object { $_.PSObject.Properties['oldEvents'] })
    Require ($traces.Count -eq 1 -and @($traces[0].oldEvents).Count -gt 0) 'Old CA-host observation is absent or ambiguous.'
    $events = @($traces[0].oldEvents)
    $paused = $events[-1]
    if ($null -ne $paused.fence) { return $paused.fence.caHost }
    if ($null -ne $paused.receipt) {
        $fences = @($paused.receipt.fences | Where-Object { $_.ownSession -and $_.phase -ceq 'Transaction' })
    } else {
        $fences = @($events | Where-Object {
            $_.kind -ceq 'CanonicalObservationAccepted' -and $null -ne $_.fence `
                -and $_.fence.ownSession -and $_.fence.phase -ceq 'Transaction'
        } | ForEach-Object fence)
    }
    Require ($fences.Count -eq 1) 'Old CA-host observation is absent or ambiguous.'
    return $fences[0].caHost
}

function Assert-HermesCoordinatorStages {
    param($Stages)
    foreach ($entry in $Stages) {
        Require ($entry -is [pscustomobject]) 'Coordinator stage validation requires structured rows.'
        $coordinator = $null -ne $entry.PSObject.Properties['kind'] -and $entry.kind -ceq 'coordinator'
        if (-not $coordinator) {
            Require ($null -eq $entry.PSObject.Properties['msiFailureEvidence']) 'MSI observations must belong only to coordinator stages.'
            Require ($null -eq $entry.PSObject.Properties['nativePreflightEvidence']) 'Native-preflight observations must belong only to coordinator stages.'
            continue
        }
        $names = @($entry.PSObject.Properties.Name | Sort-Object)
        Require (($names -join ',') -ceq 'action,diagnosticCodes,diagnosticError,exitCode,kind,msiFailureEvidence,nativePreflightEvidence,protocolError,refusalEvidence,report,sdkPathAbsent') 'Unexpected coordinator stage fields.'
        Assert-HermesCoordinatorMsiEvidence $entry
        Assert-HermesCoordinatorNativePreflightEvidence $entry
    }
}

function Assert-HermesComponentEnvelope {
    param($Report, $Expectation)
    $names = @($Report.PSObject.Properties.Name | Sort-Object)
    Require (($names -join ',') -ceq 'cases,cleanup,complete,failure,inputs,job,kind,ownerFixtureId,ownerUnknownAfter,ownerUnknownBefore,passed,schemaVersion,scope') 'Unexpected component report fields.'
    Require ($Report.schemaVersion -eq 1 -and $Report.kind -ceq 'hermes-a2-actual-component' `
        -and $Report.scope -ceq 'component; not complete installed A2 acceptance') 'Unknown component report schema or proof scope.'
    Require ([Guid]$Report.ownerFixtureId -eq [Guid]$Expectation.ownerFixtureId) 'Component report belongs to a different owner fixture.'
    foreach ($field in @('runId', 'attempt', 'job')) {
        Require ($Report.job.$field -ceq $Expectation.job.$field) 'Component report belongs to a different GitHub job.'
    }
    $expectedInputs = @{}
    foreach ($asset in $Expectation.inputs) {
        $key = CanonicalPath $asset.path
        Require (-not $expectedInputs.ContainsKey($key)) 'Duplicate expected source identity.'
        $expectedInputs[$key] = $asset.sha256
    }
    Require (@($Report.inputs).Count -eq $expectedInputs.Count) 'Incomplete component input identity set.'
    $seen = @{}
    foreach ($asset in $Report.inputs) {
        $key = CanonicalPath $asset.path
        Require ($expectedInputs.ContainsKey($key) -and -not $seen.ContainsKey($key) `
            -and $asset.sha256 -cmatch '\A[0-9a-f]{64}\z' -and $asset.sha256 -ceq $expectedInputs[$key]) 'Unbound, duplicate or changed component input.'
        $seen[$key] = $true
    }
    $owner = @{}
    foreach ($file in $Expectation.ownerUnknown) { $owner[(CanonicalPath $file.path)] = $file.sha256 }
    foreach ($set in @('ownerUnknownBefore', 'ownerUnknownAfter')) {
        Require (@($Report.$set).Count -eq $owner.Count) 'Component owner/unknown evidence is incomplete.'
        $seen = @{}
        foreach ($file in $Report.$set) {
            $key = CanonicalPath $file.path
            Require ($owner.ContainsKey($key) -and -not $seen.ContainsKey($key) -and $file.exists -is [bool] `
                -and ($null -eq $file.sha256 -or $file.sha256 -cmatch '\A[0-9a-f]{64}\z')) 'Component owner evidence is outside the synthetic scope.'
            $seen[$key] = $true
        }
    }
}

function Assert-HermesServicingComponentReport {
    param($Report, $Expectation, [int]$ExitCode)
    Assert-HermesComponentEnvelope $Report $Expectation
    Require ($ExitCode -eq 0 -and $Report.passed -is [bool] -and $Report.passed `
        -and $Report.complete -is [bool] -and $Report.complete -and $null -eq $Report.failure) 'Blocked, incomplete, failed or nonzero component execution cannot authorize publication.'
    Require ($Report.cleanup.attempted -and $Report.cleanup.completed -and $Report.cleanup.finalAdmission -ceq 'ReadyAbsent' `
        -and $null -eq $Report.cleanup.failure -and @($Report.cleanup.evidence).Count -gt 0) 'Component cleanup did not prove completed Ready absence.'
    Require (@($Report.cases).Count -eq $script:RequiredCases.Count) 'Missing or unexpected native component cases.'
    foreach ($name in $script:RequiredCases.Keys) {
        $case = @($Report.cases | Where-Object name -CEQ $name)
        Require ($case.Count -eq 1 -and $case[0].state -ceq 'Passed' -and $case[0].scope -ceq $script:RequiredCases[$name] `
            -and $null -eq $case[0].missingEvidence -and @($case[0].evidence).Count -gt 0) 'Every required native case must pass in its declared scope with evidence.'
        if ($script:RequiredCases[$name] -ceq 'actual-msi-root-composition') {
            Assert-IndependentCase $case[0] $Expectation
        }
    }
    foreach ($expected in $Expectation.ownerUnknown) {
        foreach ($set in @('ownerUnknownBefore', 'ownerUnknownAfter')) {
            $file = @($Report.$set | Where-Object { (CanonicalPath $_.path) -ceq (CanonicalPath $expected.path) })[0]
            Require ($file.exists -and $file.sha256 -ceq $expected.sha256 -and $file.length -eq $expected.length) 'Owner or unknown application bytes changed across actual servicing.'
        }
    }
    $kernel = @($Report.cases | Where-Object name -CEQ 'windows-kernel-admission')[0]
    foreach ($boundary in @('thread-death-gc-independent-process-exit', 'exclusive-wins-durable-closed', 'old-image-mapped-before-generation-publication')) {
        Require (@($kernel.evidence | Where-Object { $_.PSObject.Properties['boundary'] -and $_.boundary -ceq $boundary }).Count -eq 1) 'Kernel lifetime, exclusion or loaded-generation evidence is missing.'
    }
}

function Assert-IndependentCase($Case, $Expectation) {
    $bound = @($Case.evidence | Where-Object { $_.PSObject.Properties['independent'] -and $_.PSObject.Properties['proof'] })
    Require ($bound.Count -eq 1) 'The case has no unique independent engine/fresh-recovery proof.'
    $observer = $bound[0].independent
    $proof = $bound[0].proof
    Require ($observer.schemaProfile -ceq 'manifest-application-msi-english-v1' -and [Guid]$observer.subscriptionId -ne [Guid]::Empty `
        -and $observer.snapshot.readyCursor -gt 0 -and @($observer.snapshot.faults).Count -eq 0) 'Unknown, unavailable or faulted independent observer.'
    Require (@($observer.writers).Count -gt 0 `
        -and @($observer.writers.processId | Select-Object -Unique).Count -eq @($observer.writers).Count) 'Writer PID reuse or duplicate writer identity is unsupported.'
    Assert-NativeBinding $observer.readyWriter $observer.snapshot.readyCursor $observer.readyWriter.process $observer.writers
    $facts = @($observer.snapshot.facts)
    Require ($facts.Count -gt 0) 'Independent engine facts are absent.'
    $previous = [long]$observer.snapshot.readyCursor
    foreach ($fact in $facts) {
        Require (($fact.cursor -is [long] -or $fact.cursor -is [int]) -and $fact.cursor -gt $previous) 'Replayed, reordered or missing independent cursor.'
        $previous = [long]$fact.cursor
        if ($null -ne $fact.diagnostic) {
            Require ($fact.diagnostic.schemaVersion -eq 1 `
                -and $fact.diagnostic.failures -eq 0 -and $fact.diagnostic.kind -cne 'ObserverFault') 'Unverified writer or lost guard/engine observation.'
            Assert-NativeBinding $fact.nativeWriter $fact.cursor $fact.diagnostic.writer $observer.writers
            if ($fact.diagnostic.kind -cin @('InstallStart', 'InstallEnd', 'ActionStart')) {
                Require (Same-Process $fact.diagnostic.writer $fact.diagnostic.coordinator) 'An MSI callback was not reported by its own coordinator process.'
            }
        }
    }
    Require (@($observer.packages).Count -gt 0 -and @($observer.writers).Count -gt 0) 'Engine package or writer identities are absent.'
    $msiHashes = @($Expectation.inputs | Where-Object { $_.path.EndsWith('.msi', [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object sha256)
    foreach ($package in $observer.packages) {
        Require ($package.engineSha256 -cmatch '\A[0-9a-f]{64}\z' -and $package.source.wholeMsiSha256 -cin $msiHashes `
            -and $package.source.manifestSha256 -cmatch '\A[0-9a-f]{64}\z' -and [Guid]$package.source.productCode -ne [Guid]::Empty `
            -and [Guid]$package.source.packageCode -ne [Guid]::Empty) 'An engine record is not bound to complete validated source media.'
    }
    foreach ($writer in $observer.writers) {
        Require ($writer.processId -gt 0 -and $writer.startUtcFileTime -gt 0 -and $writer.executableSha256 -cmatch '\A[0-9a-f]{64}\z') 'Engine/guard OS process identity is incomplete.'
    }
    $cursorSet = @{}
    foreach ($fact in $facts) { $cursorSet[[string]$fact.cursor] = $true }
    $old = $proof.old
    Require ($old.beginCursor -gt $observer.snapshot.readyCursor -and $old.beginCursor -lt $old.decisionCursor `
        -and $old.decisionCursor -lt $old.outcomeCursor -and $old.outcomeCursor -lt $old.endCursor `
        -and $old.status -in @(0, 1602, 1603)) 'Old MSI terminal/transaction-end proof is incomplete.'
    foreach ($cursor in @($old.beginCursor, $old.decisionCursor, $old.outcomeCursor, $old.endCursor)) {
        Require ($cursorSet.ContainsKey([string]$cursor)) 'A claimed old-engine proof cursor is absent from the independent subscription.'
    }
    $null = Require-Fact $facts $old.beginCursor 'Begin'
    $decision = Require-Fact $facts $old.decisionCursor 'Diagnostic' 'GuardDecision'
    Assert-ExpectedGuardWriter $facts $decision (Get-OldGuardHost $Case)
    $outcome = Require-Fact $facts $old.outcomeCursor 'Outcome'
    $null = Require-Fact $facts $old.endCursor 'End'
    Require ($null -ne $outcome.outcome -and $outcome.outcome.status -eq $old.status) 'Old terminal outcome does not match its bound event.'
    $refusal = $Case.name -cin @('interrupted-install', 'interrupted-repair', 'interrupted-uninstall')
    $status = if ($refusal) { 1603 } else { 0 }
    Require ($decision.diagnostic.status -eq $status -and $null -eq $decision.diagnostic.upgradingProductCode) 'Old session guard permission/refusal evidence is inconsistent.'
    if ($Case.name -cin @('interrupted-upgrade', 'interrupted-explicit-rollback')) {
        Require ($null -ne $old.afterFenceActionCursor -and $old.decisionCursor -lt $old.afterFenceActionCursor `
            -and $old.afterFenceActionCursor -lt $old.outcomeCursor) 'Parent guard acceptance without an actual subsequent engine action is not a passed fence.'
        Require ($cursorSet.ContainsKey([string]$old.afterFenceActionCursor)) 'The claimed after-fence engine action is not independently observed.'
        $action = Require-Fact $facts $old.afterFenceActionCursor 'Diagnostic' 'ActionStart'
        Require ($action.diagnostic.action -ceq 'RemoveExistingProducts') 'An unrelated action cannot prove passage beyond the parent transaction fence.'
    }
    $fresh = @($proof.fresh)
    Require ($fresh.Count -gt 0 -and ($Case.name -cne 'already-absent-bootstrap' -or $fresh.Count -eq 2)) 'Completed fresh recovery fence evidence is missing.'
    $runs = @{}
    foreach ($call in $fresh) {
        $run = ([Guid]$call.runId).ToString('D')
        Require ($run -ne ([Guid]::Empty).ToString('D') -and -not $runs.ContainsKey($run) `
            -and $call.beginCursor -lt $call.preflightCursor -and $old.endCursor -lt $call.preflightCursor `
            -and $call.preflightCursor -lt $call.transactionCursor -and $call.transactionCursor -lt $call.installEndCursor `
            -and $call.preflightCursor -lt $call.outcomeCursor -and $call.outcomeCursor -lt $call.endCursor) 'Fresh recovery did not traverse its own complete observed fence/session.'
        $runs[$run] = $true
        foreach ($cursor in @($call.beginCursor, $call.preflightCursor, $call.transactionCursor, $call.installEndCursor, $call.outcomeCursor, $call.endCursor)) {
            Require ($cursorSet.ContainsKey([string]$cursor)) 'A claimed fresh-recovery proof cursor is absent from the independent subscription.'
        }
        $null = Require-Fact $facts $call.beginCursor 'Begin'
        foreach ($pair in @(@($call.preflightCursor, 'Preflight'), @($call.transactionCursor, 'Transaction'))) {
            $decision = Require-Fact $facts $pair[0] 'Diagnostic' 'GuardDecision'
            Require ([Guid]$decision.diagnostic.runId -eq [Guid]$call.runId -and $decision.diagnostic.phase -ceq $pair[1] `
                -and $decision.diagnostic.status -eq 0 -and $null -eq $decision.diagnostic.upgradingProductCode) 'Fresh proof did not bind its own exact guard run and phase.'
        }
        $end = Require-Fact $facts $call.installEndCursor 'Diagnostic' 'InstallEnd'
        Require ($end.diagnostic.status -eq 0 -and [Guid]$end.diagnostic.runId -eq [Guid]$call.runId) 'Fresh synchronous completion is not bound to its own run.'
        $outcome = Require-Fact $facts $call.outcomeCursor 'Outcome'
        Require ($null -ne $outcome.outcome -and $outcome.outcome.status -eq 0) 'Fresh engine outcome did not succeed.'
        $null = Require-Fact $facts $call.endCursor 'End'
    }
    $attempts = @($bound[0].attempts)
    Require ($attempts.Count -gt 0) 'Actual synchronous recovery receipts are absent.'
    $last = $attempts[-1]
    Require ($last.exitCode -eq 0 -and $last.result.exitCode -eq 0 -and $last.result.diagnosticCode -ceq 'None' `
        -and $last.result.report.ready -and $last.result.report.outcome -ceq 'Completed') 'Fresh recovery full verification did not complete.'
    foreach ($receipt in $last.result.engineReceipts) {
        Require ($receipt.apiEntered -and $receipt.apiReturned -and -not $receipt.controlFaulted -and $receipt.msiExitCode -eq 0 `
            -and $receipt.ownPreflightOccurred -and $receipt.ownTransactionFenceOccurred) 'A snapshot, busy result or pre-execution return cannot substitute for fresh own recovery.'
    }
    Require (@($last.result.engineReceipts).Count -eq $fresh.Count) 'Fresh proof and synchronous receipt membership differ.'
    $raw = @($last.events | Where-Object kind -CEQ 'api-returned')
    Require ($raw.Count -eq $fresh.Count) 'Actual own synchronous MSI call receipts are incomplete.'
    foreach ($event in $raw) {
        $receipt = $event.receipt
        Require ($event.observationFailures -eq 0 -and $receipt.apiEntered -and $receipt.apiReturned `
            -and $receipt.msiExitCode -eq 0 -and -not $receipt.controlFaulted -and $receipt.observationFaults -eq 0) 'Raw recovery callback/receipt integrity failed.'
        foreach ($phase in @('Preflight', 'Transaction')) {
            $fences = @($receipt.fences | Where-Object { $_.ownSession -and $_.phase -ceq $phase -and $_.runId -eq $receipt.runId })
            Require ($fences.Count -eq 1) 'Nested or missing fences cannot substitute for the own recovery transaction.'
            $decisions = @($facts | Where-Object {
                $null -ne $_.diagnostic -and $_.diagnostic.kind -ceq 'GuardDecision' `
                    -and $_.diagnostic.runId -eq $receipt.runId `
                    -and $_.diagnostic.phase -ceq $phase `
                    -and $null -eq $_.diagnostic.upgradingProductCode
            })
            Require ($decisions.Count -eq 1) 'The recovery CA-host observation has no unique independent guard decision.'
            Assert-ExpectedGuardWriter $facts $decisions[0] $fences[0].caHost
        }
    }
}

Export-ModuleMember -Function Assert-HermesComponentEnvelope, Assert-HermesServicingComponentReport, Assert-HermesCoordinatorStages
