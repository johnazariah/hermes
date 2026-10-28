#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-HermesSetupResult {
    param([string]$StandardOutput, [int]$ExitCode, [ValidateSet('install', 'repair', 'uninstall', 'recover')][string]$Operation)
    if ([Text.Encoding]::UTF8.GetByteCount($StandardOutput) -gt 4096) { throw 'Coordinator report exceeds its bound.' }
    $field = '"(?<name>schemaVersion|operation|outcome|ready|msiExitCode)"[ \t]*:[ \t]*(?:"[A-Za-z]+"|true|false|null|0|[1-9][0-9]*)[ \t]*'
    $frame = [regex]::Match($StandardOutput, '\A\{[ \t]*(?:' + $field + ',[ \t]*){4}' + $field + '\}(?:\r?\n)?\z')
    if (-not $frame.Success -or @($frame.Groups['name'].Captures.Value | Sort-Object -Unique).Count -ne 5) {
        throw 'Coordinator report must contain exactly one complete writer record.'
    }
    $report = $StandardOutput | ConvertFrom-Json -ErrorAction Stop
    $names = @($report.PSObject.Properties.Name | Sort-Object)
    if (($names -join ',') -cne 'msiExitCode,operation,outcome,ready,schemaVersion' `
        -or ($report.schemaVersion -isnot [int] -and $report.schemaVersion -isnot [long]) -or $report.schemaVersion -ne 1 `
        -or $report.ready -isnot [bool] -or $report.operation -cne $Operation.Substring(0, 1).ToUpperInvariant() + $Operation.Substring(1) `
        -or $report.outcome -cnotin @('Completed', 'Refused', 'RolledBack', 'RebootRequired', 'RecoveryRequired')) {
        throw 'Coordinator report does not match its public contract.'
    }
    if ($null -ne $report.msiExitCode -and ($report.msiExitCode -isnot [int] -and $report.msiExitCode -isnot [long] `
        -or $report.msiExitCode -lt 0 -or $report.msiExitCode -gt [int]::MaxValue)) { throw 'Invalid native MSI result.' }
    $valid = switch ($report.outcome) {
        'Completed' { $ExitCode -eq 0 -and $report.ready -and $report.msiExitCode -eq 0 }
        'RolledBack' { $ExitCode -eq 1603 -and $report.ready -and $null -ne $report.msiExitCode -and $report.msiExitCode -notin @(0, 1605, 1618, 3010, 1641) }
        'RebootRequired' { $ExitCode -eq 3010 -and -not $report.ready -and $null -ne $report.msiExitCode -and $report.msiExitCode -notin @(1605, 1618) }
        'Refused' { $ExitCode -in @(1603, 1618) -and -not $report.ready -and $null -eq $report.msiExitCode }
        default { $ExitCode -in @(1603, 1618) -and -not $report.ready `
            -and ($ExitCode -ne 1618 -or $null -eq $report.msiExitCode -or $report.msiExitCode -eq 1618) }
    }
    if (-not $valid) { throw 'Coordinator process status and readiness report disagree.' }
    return $report
}

function ConvertFrom-HermesNativePreflightLine {
    param([string]$Line)
    $pattern = '\AHERMES_NATIVE_PREFLIGHT v=1 source=1 observed=(?<observed>[01]) point=(?<point>[0-9]) reason=(?<reason>0|1|2|4|1[0-9]|2[0-2])\z'
    if ($Line.Length -gt 112 -or $Line -cnotmatch $pattern) { return $null }
    $observed = [int]$Matches.observed
    $point = [int]$Matches.point
    $reason = [int]$Matches.reason
    $valid = ($observed -eq 0 -and $point -eq 0 -and $reason -eq 0) `
        -or ($observed -eq 1 -and $point -ge 1 -and $point -le 9 -and ( `
            $reason -in @(0, 1, 4) `
            -or ($reason -eq 2 -and $point -ge 2) `
            -or ($point -eq 2 -and $reason -ge 10 -and $reason -le 22)))
    if (-not $valid) { return $null }
    # source=1 means UNTRUSTED active-MSI-callback data, never native provenance.
    return [pscustomobject]@{
        v = 1; source = 1; observed = $observed; point = $point; reason = $reason
    }
}

function Test-HermesMsiCallContext {
    param($Value)
    if ($Value -isnot [pscustomobject]) { return $false }
    $names = @($Value.PSObject.Properties.Name | Sort-Object)
    if (($names -join ',') -cne 'admin,elevated,elevationType,integrity,linked,linkedElevated,process,sameOwner,thread,v') {
        return $false
    }
    foreach ($name in $names) {
        if ($Value.$name -isnot [int] -and $Value.$name -isnot [long]) { return $false }
    }
    if ($Value.v -ne 1 -or $Value.process -notin @(0, 1, 2) `
        -or $Value.thread -notin @(0, 1, 2, 3) -or $Value.linked -notin @(0, 1, 2, 3)) {
        return $false
    }
    if ($Value.process -eq 1) {
        if ($Value.elevated -notin @(0, 1) -or $Value.elevationType -notin @(1, 2, 3) `
            -or $Value.integrity -lt 0 -or $Value.integrity -gt 65535 -or $Value.admin -notin @(0, 1)) {
            return $false
        }
    } elseif ($Value.elevated -ne -1 -or $Value.elevationType -ne -1 `
        -or $Value.integrity -ne -1 -or $Value.admin -ne -1) { return $false }
    if ($Value.linked -eq 1) {
        if ($Value.sameOwner -notin @(0, 1) -or $Value.linkedElevated -notin @(0, 1)) { return $false }
    } elseif ($Value.sameOwner -ne -1 -or $Value.linkedElevated -ne -1) { return $false }
    if ($Value.process -eq 0) {
        return $Value.thread -eq 0 -and $Value.linked -eq 0
    }
    return $Value.thread -ne 0 -and $Value.linked -ne 0
}

function ConvertTo-HermesMsiContextSuffix {
    param($Value)
    if (-not (Test-HermesMsiCallContext $Value)) { throw 'Invalid coordinator MSI observation.' }
    [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        ' context=1 process={0} elevated={1} elevationType={2} integrity={3} admin={4} thread={5} linked={6} sameOwner={7} linkedElevated={8}',
        [object[]]@($Value.process, $Value.elevated, $Value.elevationType, $Value.integrity,
            $Value.admin, $Value.thread, $Value.linked, $Value.sameOwner, $Value.linkedElevated))
}

function ConvertFrom-HermesMsiObservationLine {
    param([string]$Line)
    $pattern = '\AHERMES_SETUP_MSI v=(?<v>[12]) action=(?<action>0|[1-9][0-9]{0,3}) error=(?<error>0|[1-9][0-9]{0,4}) readFailed=(?<readFailed>[01]) preflight=(?<preflight>[01]) fence=(?<fence>[01])(?<context> context=1 process=(?<process>[0-3]) elevated=(?<elevated>-1|[01]) elevationType=(?<elevationType>-1|[1-3]) integrity=(?<integrity>-1|0|[1-9][0-9]{0,4}) admin=(?<admin>-1|[01]) thread=(?<thread>[0-3]) linked=(?<linked>[0-3]) sameOwner=(?<sameOwner>-1|[01]) linkedElevated=(?<linkedElevated>-1|[01]))?\z'
    if ($Line.Length -gt 320 -or $Line -cnotmatch $pattern) { return $null }
    $hasContext = $Matches.ContainsKey('context')
    if (([int]$Matches.v -eq 2) -ne $hasContext) { return $null }
    $value = [pscustomobject]@{
        v = [int]$Matches.v; action = [int]$Matches.action; error = [int]$Matches.error
        readFailed = [int]$Matches.readFailed
        preflight = [int]$Matches.preflight; fence = [int]$Matches.fence
    }
    if ($hasContext) {
        $context = [pscustomobject]@{
            v = 1; process = [int]$Matches.process; elevated = [int]$Matches.elevated
            elevationType = [int]$Matches.elevationType; integrity = [int]$Matches.integrity
            admin = [int]$Matches.admin; thread = [int]$Matches.thread
            linked = [int]$Matches.linked; sameOwner = [int]$Matches.sameOwner
            linkedElevated = [int]$Matches.linkedElevated
        }
        if (-not (Test-HermesMsiCallContext $context)) { return $null }
        # Non-authoritative caller measurements remain inside the MSI observation.
        $value | Add-Member -NotePropertyName context -NotePropertyValue $context
    }
    return $value
}

function ConvertFrom-HermesSetupProcessOutput {
    param([string]$StandardOutput, [string]$StandardError, [int]$ExitCode,
        [ValidateSet('install', 'repair', 'uninstall', 'recover')][string]$Operation)
    $report = $null
    $protocolError = $null
    try { $report = ConvertFrom-HermesSetupResult -StandardOutput $StandardOutput -ExitCode $ExitCode -Operation $Operation }
    catch { $protocolError = 'InvalidCoordinatorReport' }
    $codes = @()
    $refusalEvidence = $null
    $msiFailureEvidence = $null
    $nativePreflightEvidence = $null
    $diagnosticError = $null
    if ($StandardError.Length -gt 4096) {
        $diagnosticError = 'CoordinatorDiagnosticsTooLong'
    } else {
        $setupCodes = @('None', 'InvalidArguments', 'InvalidEvidence', 'Busy', 'Unavailable', 'Cancelled',
            'OwnershipRefused', 'IncompatibleInstallation', 'RootMismatch', 'Collision', 'MediaInvalid',
            'MediaIdentityMismatch', 'RecoveryMediaUnavailable', 'RollbackNotAllowed', 'UnsupportedPackageChange',
            'NotInstalled', 'RecoveryRequired', 'NoRecoveryPending', 'JournalInvalid', 'ClosedPublicationFailed',
            'JournalPublicationFailed', 'MsiPreparationFailed', 'MsiCallFailed', 'MsiDidNotComplete',
            'TransactionFenceMissing', 'VerificationFailed', 'RebootRequired', 'ReadyPublicationFailed')
        # WriteLine adds one optional final delimiter, not an extra record.
        $lines = @(($StandardError -creplace '\r?\n\z', '') -split '\r?\n')
        $codes = @($lines | Where-Object { $_ -cmatch '^Hermes Setup: [A-Za-z]+\.$' } `
            | ForEach-Object { $_.Substring(14).TrimEnd('.') } | Where-Object { $_ -cin $setupCodes })
        $candidates = @($lines | Where-Object {
            $_.IndexOf('HERMES_SETUP', [StringComparison]::OrdinalIgnoreCase) -ge 0 `
                -and $_.IndexOf('HERMES_SETUP_MSI', [StringComparison]::OrdinalIgnoreCase) -lt 0
        })
        if ($candidates.Count -gt 1) {
            $diagnosticError = 'DuplicateCoordinatorEvidence'
        } elseif ($candidates.Count -eq 1) {
            $pattern = '\AHERMES_SETUP_EVIDENCE v=1 stage=(?<stage>[1-9]|1[0-6]) cause=(?<cause>[1-9]|[1-6][0-9]|7[0-3]) fault=(?<fault>[0-5]) errorKind=(?<errorKind>[0-2]) error=(?<error>0|-?[1-9][0-9]{0,9})\z'
            if ($candidates[0].Length -gt 192 -or $candidates[0] -cnotmatch $pattern) {
                $diagnosticError = 'InvalidCoordinatorEvidence'
            } else {
                $errorCode = [long]$Matches.error
                if ($errorCode -lt [int]::MinValue -or $errorCode -gt [uint32]::MaxValue `
                    -or ([int]$Matches.cause -ge 10 -and ( `
                        ([int]$Matches.cause -le 18 -and [int]$Matches.stage -ne 1) `
                        -or ([int]$Matches.cause -ge 19 -and [int]$Matches.stage -ne 10) `
                        -or [int]$Matches.fault -ne 0 -or [int]$Matches.errorKind -ne 0 -or $errorCode -ne 0))) {
                    $diagnosticError = 'InvalidCoordinatorEvidence'
                } else {
                    $refusalEvidence = [pscustomobject]@{
                        v = 1; stage = [int]$Matches.stage; cause = [int]$Matches.cause
                        fault = [int]$Matches.fault; errorKind = [int]$Matches.errorKind; error = $errorCode
                    }
                }
            }
        }
    }
    if ($StandardError.Length -le 4096) {
        $msiCandidates = @($lines | Where-Object {
            $_.IndexOf('HERMES_SETUP_MSI', [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        if ($msiCandidates.Count -gt 1) {
            if ($null -eq $diagnosticError) { $diagnosticError = 'DuplicateCoordinatorMsiEvidence' }
        } elseif ($msiCandidates.Count -eq 1) {
            $observation = ConvertFrom-HermesMsiObservationLine $msiCandidates[0]
            if ($null -eq $observation) {
                if ($null -eq $diagnosticError) { $diagnosticError = 'InvalidCoordinatorMsiEvidence' }
            } else {
                $allowedActions = @(0, 25, 50, 100, 700, 800, 900, 1000, 1300, 1399, 1400, 1500, 1501, 1502,
                    1600, 1800, 2600, 3200, 3500, 3600, 3700, 3900, 4000, 4500, 5000, 6000, 6100, 6300, 6400, 6600)
                if ($observation.action -notin $allowedActions -or $observation.error -gt 65535 `
                    -or $null -eq $report -or $report.outcome -cne 'RecoveryRequired' -or $report.ready `
                    -or $null -eq $report.msiExitCode -or $ExitCode -ne 1603 `
                    -or $report.msiExitCode -in @(1605, 1618, 3010, 1641) `
                    -or $codes.Count -ne 1 -or $codes[0] -cne 'TransactionFenceMissing' `
                    -or $observation.fence -ne 0) {
                    if ($null -eq $diagnosticError) { $diagnosticError = 'InvalidCoordinatorMsiEvidence' }
                } else {
                    $msiFailureEvidence = $observation
                }
            }
        }
        if ($msiCandidates.Count -gt 0 -and $candidates.Count -gt 0) {
            # Neither family is accepted from a mixed frame, even if one parsed first.
            $refusalEvidence = $null
            $msiFailureEvidence = $null
            if ($null -eq $diagnosticError) { $diagnosticError = 'MixedCoordinatorEvidence' }
        }
    }
    if ($StandardError.Length -le 4096) {
        $nativeCandidates = @($lines | Where-Object {
            $_.IndexOf('HERMES_NATIVE', [StringComparison]::OrdinalIgnoreCase) -ge 0
        })
        if ($nativeCandidates.Count -gt 1) {
            if ($null -eq $diagnosticError) { $diagnosticError = 'DuplicateCoordinatorNativePreflightEvidence' }
        } elseif ($nativeCandidates.Count -eq 1) {
            $value = ConvertFrom-HermesNativePreflightLine $nativeCandidates[0]
            if ($null -eq $value -or $null -eq $msiFailureEvidence `
                -or $msiFailureEvidence.preflight -ne 0 -or $msiFailureEvidence.fence -ne 0 `
                -or $null -ne $refusalEvidence) {
                if ($null -eq $diagnosticError) { $diagnosticError = 'InvalidCoordinatorNativePreflightEvidence' }
            } else {
                $nativePreflightEvidence = $value
            }
        }
        if ($nativeCandidates.Count -gt 0 -and $candidates.Count -gt 0) {
            $refusalEvidence = $null
            $msiFailureEvidence = $null
            $nativePreflightEvidence = $null
            if ($null -eq $diagnosticError) { $diagnosticError = 'MixedCoordinatorEvidence' }
        }
    }
    if ($StandardError.Length -le 4096 -and $null -eq $diagnosticError) {
        $consumed = $codes.Count + $candidates.Count + $msiCandidates.Count + $nativeCandidates.Count
        $ordered = $lines.Count -gt 0 -and $lines[0] -cmatch '\AHermes Setup: [A-Za-z]+\.\z'
        if ($null -ne $nativePreflightEvidence) {
            $ordered = $ordered -and $lines.Count -eq 3 -and $lines[1] -clike 'HERMES_SETUP_MSI *' `
                -and $lines[2] -clike 'HERMES_NATIVE_PREFLIGHT *'
        }
        if ($consumed -ne $lines.Count -or $codes.Count -ne 1 -or -not $ordered `
            -or $codes[0] -cnotin $setupCodes) {
            $diagnosticError = 'InvalidCoordinatorDiagnosticFrame'
        } elseif ($null -ne $refusalEvidence -and ($null -eq $report -or $ExitCode -ne 1603 `
            -or $report.outcome -cne 'Refused' -or $report.ready -or $null -ne $report.msiExitCode `
            -or ($codes[0] -cnotin @('InvalidEvidence', 'Unavailable') -and ( `
                $codes[0] -cne 'MediaInvalid' -or $refusalEvidence.stage -ne 10 -or $refusalEvidence.cause -lt 19)))) {
            # SetupEntryPoint.WriteAsync emits refusal evidence only for these failures.
            $diagnosticError = 'InvalidCoordinatorEvidence'
        } elseif ($null -ne $report) {
            $validCode = switch ($report.outcome) {
                'Completed' { $codes[0] -ceq 'None' }
                'RolledBack' { $codes[0] -ceq 'MsiCallFailed' -and $Operation -cne 'recover' }
                'RebootRequired' { $codes[0] -ceq 'RebootRequired' }
                default {
                    $codes[0] -cnotin @('None', 'InvalidArguments', 'RebootRequired') `
                        -and (($codes[0] -ceq 'Busy') -eq ($ExitCode -eq 1618))
                }
            }
            if (-not $validCode) { $diagnosticError = 'InvalidCoordinatorDiagnosticCode' }
        }
    }
    if ($null -ne $diagnosticError) {
        $refusalEvidence = $null
        $msiFailureEvidence = $null
        $nativePreflightEvidence = $null
        if ($null -eq $protocolError) { $protocolError = 'InvalidCoordinatorDiagnostics' }
    }
    [pscustomobject]@{
        exitCode = $ExitCode; report = $report; diagnosticCodes = $codes; protocolError = $protocolError
        refusalEvidence = $refusalEvidence; diagnosticError = $diagnosticError
        msiFailureEvidence = $msiFailureEvidence
        nativePreflightEvidence = $nativePreflightEvidence
    }
}

function Assert-HermesCoordinatorMsiEvidence {
    param($Result)
    $invalid = 'Invalid coordinator MSI observation.'
    if ($Result -isnot [pscustomobject] -or $null -eq $Result.PSObject.Properties['msiFailureEvidence']) {
        throw $invalid
    }
    $value = $Result.msiFailureEvidence
    if ($null -eq $value) { return }
    if ($value -isnot [pscustomobject]) { throw $invalid }
    $names = @($value.PSObject.Properties.Name | Sort-Object)
    $hasContext = $null -ne $value.PSObject.Properties['context']
    $expectedNames = if ($hasContext) { 'action,context,error,fence,preflight,readFailed,v' } `
        else { 'action,error,fence,preflight,readFailed,v' }
    if (($names -join ',') -cne $expectedNames) { throw $invalid }
    foreach ($name in @('v', 'action', 'error', 'readFailed', 'preflight', 'fence')) {
        if ($value.$name -isnot [int] -and $value.$name -isnot [long]) { throw $invalid }
    }
    if ($hasContext) {
        if ($value.v -ne 2 -or -not (Test-HermesMsiCallContext $value.context)) { throw $invalid }
    } elseif ($value.v -ne 1) { throw $invalid }
    foreach ($name in @('exitCode', 'report', 'diagnosticCodes', 'protocolError', 'diagnosticError', 'refusalEvidence')) {
        if ($null -eq $Result.PSObject.Properties[$name]) { throw $invalid }
    }
    if (($Result.exitCode -isnot [int] -and $Result.exitCode -isnot [long]) `
        -or $Result.exitCode -ne 1603 -or $null -ne $Result.protocolError `
        -or $null -ne $Result.diagnosticError -or $null -ne $Result.refusalEvidence `
        -or $Result.diagnosticCodes -isnot [array] -or $Result.diagnosticCodes.Count -ne 1 `
        -or $Result.diagnosticCodes[0] -cne 'TransactionFenceMissing') { throw $invalid }
    if ($Result.report -isnot [pscustomobject] `
        -or $null -eq $Result.report.PSObject.Properties['operation'] `
        -or $Result.report.operation -isnot [string] `
        -or $Result.report.operation -cnotin @('Install', 'Repair', 'Uninstall', 'Recover')) { throw $invalid }
    $line = [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        'HERMES_SETUP_MSI v={0} action={1} error={2} readFailed={3} preflight={4} fence={5}',
        [object[]]@($value.v, $value.action, $value.error, $value.readFailed, $value.preflight, $value.fence))
    if ($hasContext) { $line += ConvertTo-HermesMsiContextSuffix $value.context }
    $parsed = ConvertFrom-HermesSetupProcessOutput `
        -StandardOutput ($Result.report | ConvertTo-Json -Depth 8 -Compress) `
        -StandardError ("Hermes Setup: TransactionFenceMissing.`n" + $line) `
        -ExitCode $Result.exitCode -Operation ($Result.report.operation.ToLowerInvariant())
    if ($null -ne $parsed.protocolError -or $null -ne $parsed.diagnosticError `
        -or $null -eq $parsed.msiFailureEvidence) { throw $invalid }
}

function Assert-HermesCoordinatorNativePreflightEvidence {
    param($Result)
    $invalid = 'Invalid coordinator native-preflight observation.'
    if ($Result -isnot [pscustomobject] -or $null -eq $Result.PSObject.Properties['nativePreflightEvidence']) {
        throw $invalid
    }
    $value = $Result.nativePreflightEvidence
    if ($null -eq $value) { return }
    if ($value -isnot [pscustomobject]) { throw $invalid }
    $names = @($value.PSObject.Properties.Name | Sort-Object)
    if (($names -join ',') -cne 'observed,point,reason,source,v') { throw $invalid }
    foreach ($name in @('v', 'source', 'observed', 'point', 'reason')) {
        if ($value.$name -isnot [int] -and $value.$name -isnot [long]) { throw $invalid }
    }
    Assert-HermesCoordinatorMsiEvidence $Result
    if ($null -eq $Result.msiFailureEvidence -or $Result.msiFailureEvidence.preflight -ne 0 `
        -or $Result.msiFailureEvidence.fence -ne 0) { throw $invalid }
    $line = [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        'HERMES_NATIVE_PREFLIGHT v={0} source={1} observed={2} point={3} reason={4}',
        [object[]]@($value.v, $value.source, $value.observed, $value.point, $value.reason))
    if ($null -eq (ConvertFrom-HermesNativePreflightLine $line)) { throw $invalid }
}

function Invoke-HermesSetup {
    param(
        [Parameter(Mandatory = $true)][string]$SetupExecutable,
        [Parameter(Mandatory = $true)][ValidateSet('install', 'repair', 'uninstall', 'recover')][string]$Operation,
        [string]$PackagePath,
        [switch]$AllowRollback,
        [switch]$Quiet
    )
    if ($env:OS -ne 'Windows_NT') { throw 'Coordinator execution is Windows-only.' }
    if (($Operation -eq 'install') -ne (-not [string]::IsNullOrEmpty($PackagePath)) `
        -or ($AllowRollback -and $Operation -ne 'install')) { throw 'Invalid coordinator operation arguments.' }
    $executable = (Resolve-Path -LiteralPath $SetupExecutable).Path
    $directory = [IO.Path]::GetDirectoryName($executable)
    foreach ($name in @('Hermes.Setup.exe', 'Hermes.Setup.dll', 'Hermes.Setup.deps.json', 'Hermes.Setup.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $directory $name) -PathType Leaf)) { throw 'Use the complete self-contained Setup bundle outside the application directory.' }
    }
    if ([IO.Path]::GetFileName($executable) -cne 'Hermes.Setup.exe') { throw 'Select the published Hermes.Setup.exe apphost.' }
    $arguments = @($Operation)
    if ($Operation -eq 'install') {
        $package = (Resolve-Path -LiteralPath $PackagePath).Path
        if ([IO.Path]::GetExtension($package) -ine '.msi' -or -not (Test-Path -LiteralPath $package -PathType Leaf)) { throw 'Supply an existing absolute MSI source.' }
        $arguments += @('--package', $package)
    }
    if ($AllowRollback) { $arguments += '--allow-rollback' }
    if ($Quiet) { $arguments += '--quiet' }
    if (@($arguments | Where-Object { $_ -match '["\x00-\x1f]' }).Count -ne 0) { throw 'Invalid coordinator argument characters.' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $executable
    $start.WorkingDirectory = $directory
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Arguments = (($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' ')
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    try {
        if (-not $child.Start()) { throw 'Coordinator process did not start.' }
        $output = $child.StandardOutput.ReadToEndAsync()
        $diagnostics = $child.StandardError.ReadToEndAsync()
        $child.WaitForExit()
        $stdout = $output.GetAwaiter().GetResult()
        $stderr = $diagnostics.GetAwaiter().GetResult()
        ConvertFrom-HermesSetupProcessOutput -StandardOutput $stdout -StandardError $stderr -ExitCode $child.ExitCode -Operation $Operation
    } finally { $child.Dispose() }
}

Export-ModuleMember -Function Invoke-HermesSetup, ConvertFrom-HermesSetupResult, ConvertFrom-HermesSetupProcessOutput, Assert-HermesCoordinatorMsiEvidence, Assert-HermesCoordinatorNativePreflightEvidence
