#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertFrom-HermesSetupResult {
    param([string]$StandardOutput, [int]$ExitCode, [ValidateSet('install', 'repair', 'uninstall', 'recover')][string]$Operation)
    if ([Text.Encoding]::UTF8.GetByteCount($StandardOutput) -gt 4096) { throw 'Coordinator report exceeds its bound.' }
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
        'RebootRequired' { $ExitCode -eq 3010 -and -not $report.ready -and $null -ne $report.msiExitCode }
        'Refused' { $ExitCode -in @(1603, 1618) -and -not $report.ready -and $null -eq $report.msiExitCode }
        default { $ExitCode -in @(1603, 1618) -and -not $report.ready }
    }
    if (-not $valid) { throw 'Coordinator process status and readiness report disagree.' }
    return $report
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
    $diagnosticError = $null
    if ($StandardError.Length -gt 4096) {
        $diagnosticError = 'CoordinatorDiagnosticsTooLong'
    } else {
        $lines = @($StandardError -split '\r?\n')
        $codes = @($lines | Where-Object { $_ -cmatch '^Hermes Setup: [A-Za-z]+\.$' } `
            | ForEach-Object { $_.Substring(14).TrimEnd('.') })
        $candidates = @($lines | Where-Object { $_.IndexOf('HERMES_SETUP', [StringComparison]::OrdinalIgnoreCase) -ge 0 })
        if ($candidates.Count -gt 1) {
            $diagnosticError = 'DuplicateCoordinatorEvidence'
        } elseif ($candidates.Count -eq 1) {
            $pattern = '\AHERMES_SETUP_EVIDENCE v=1 stage=(?<stage>[1-9]|1[0-6]) cause=(?<cause>[1-9]|[1-3][0-9]|4[01]) fault=(?<fault>[0-5]) errorKind=(?<errorKind>[0-2]) error=(?<error>0|-?[1-9][0-9]{0,9})\z'
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
    if ($null -ne $diagnosticError -and $null -eq $protocolError) { $protocolError = 'InvalidCoordinatorDiagnostics' }
    [pscustomobject]@{
        exitCode = $ExitCode; report = $report; diagnosticCodes = $codes; protocolError = $protocolError
        refusalEvidence = $refusalEvidence; diagnosticError = $diagnosticError
    }
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

Export-ModuleMember -Function Invoke-HermesSetup, ConvertFrom-HermesSetupResult, ConvertFrom-HermesSetupProcessOutput
