#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Setup-Process.psm1') -Force

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$refusedReport = '{"schemaVersion":1,"operation":"Install","outcome":"Refused","ready":false,"msiExitCode":null}'
$wire = 'HERMES_SETUP_EVIDENCE v=1 stage=8 cause=4 fault=5 errorKind=2 error=4294967295'
$diagnostic = 'Hermes Setup: InvalidEvidence.'

function Read-Output([string]$Text, [string]$Report = $refusedReport, [int]$ExitCode = 1603) {
    ConvertFrom-HermesSetupProcessOutput -StandardOutput $Report -StandardError $Text -ExitCode $ExitCode -Operation install
}

function Require-Refusal($Result) {
    Require ($Result.exitCode -eq 1603 -and $Result.report.schemaVersion -eq 1 `
        -and $Result.report.outcome -ceq 'Refused' -and -not $Result.report.ready `
        -and $null -eq $Result.report.msiExitCode) 'Diagnostics changed the original public refusal.'
}

$positiveCount = 0
foreach ($stage in 1..16) {
    foreach ($cause in 1..9) {
        foreach ($fault in 0..5) {
            foreach ($kind in 0..2) {
                $result = Read-Output "$diagnostic`r`nHERMES_SETUP_EVIDENCE v=1 stage=$stage cause=$cause fault=$fault errorKind=$kind error=0`r`n"
                Require-Refusal $result
                Require ($null -eq $result.protocolError -and $null -eq $result.diagnosticError `
                    -and $result.diagnosticCodes.Count -eq 1 -and $result.diagnosticCodes[0] -ceq 'InvalidEvidence') 'A valid numerical diagnostic changed existing diagnostic handling.'
                $value = $result.refusalEvidence
                Require (($value.PSObject.Properties.Name -join ',') -ceq 'v,stage,cause,fault,errorKind,error') 'Evidence must contain only the six numerical fields.'
                Require ($value.v -eq 1 -and $value.stage -eq $stage -and $value.cause -eq $cause `
                    -and $value.fault -eq $fault -and $value.errorKind -eq $kind -and $value.error -eq 0) 'Numerical evidence did not round-trip.'
                foreach ($property in $value.PSObject.Properties) {
                    Require ($property.Value -is [int] -or $property.Value -is [long]) 'Diagnostic fields must remain numerical, not untrusted strings.'
                }
                $positiveCount++
            }
        }
    }
}
foreach ($code in @(-2147483648L, -1L, 0L, 1L, 2147483647L, 2147483648L, 4294967295L)) {
    $result = Read-Output ($wire.Replace('4294967295', [string]$code))
    Require ($null -eq $result.protocolError -and $result.refusalEvidence.error -eq $code) 'A supported signed/unsigned 32-bit error code was lost.'
    Require-Refusal $result
    $positiveCount++
}
foreach ($text in @('', "$diagnostic`n", "unknown SYNTHETIC_SECRET C:\synthetic\owner token=synthetic`nHermes Setup: Busy.`n")) {
    $result = Read-Output $text
    Require ($null -eq $result.protocolError -and $null -eq $result.diagnosticError `
        -and $null -eq $result.refusalEvidence) 'Absent evidence and unknown stderr must keep existing handling.'
    Require-Refusal $result
    Require (($result | ConvertTo-Json -Depth 8 -Compress) -cnotmatch 'SYNTHETIC_SECRET|synthetic.owner|token=') 'Unknown stderr leaked into the structured result.'
}
$result = Read-Output "$diagnostic`n$wire`nunknown SYNTHETIC_SECRET"
Require ($null -eq $result.protocolError -and $result.refusalEvidence.error -eq 4294967295L) 'Unknown stderr must not invalidate a genuine bounded evidence line.'
Require (($result | ConvertTo-Json -Depth 8 -Compress) -cnotmatch 'SYNTHETIC_SECRET') 'A valid evidence result retained unknown stderr.'
$atLimit = "$diagnostic`n$wire`n"
$result = Read-Output ($atLimit + ('x' * (4096 - $atLimit.Length)))
Require ($null -eq $result.protocolError -and $null -ne $result.refusalEvidence) 'The existing 4096-character stderr bound must remain inclusive.'

$invalid = @(
    $wire.Replace('v=1', 'v=0'), $wire.Replace('v=1', 'v=2'), $wire.Replace('v=1', 'v=01'),
    $wire.Replace('stage=8', 'stage=0'), $wire.Replace('stage=8', 'stage=17'),
    $wire.Replace('stage=8', 'stage=-1'), $wire.Replace('stage=8', 'stage=08'),
    $wire.Replace('stage=8', 'stage=+8'), $wire.Replace('stage=8', 'stage=8.0'),
    $wire.Replace('stage=8', 'stage=8e0'), $wire.Replace('stage=8', 'stage=٨'),
    $wire.Replace('cause=4', 'cause=0'), $wire.Replace('cause=4', 'cause=10'),
    $wire.Replace('cause=4', 'cause=-1'), $wire.Replace('fault=5', 'fault=-1'),
    $wire.Replace('fault=5', 'fault=6'), $wire.Replace('errorKind=2', 'errorKind=-1'),
    $wire.Replace('errorKind=2', 'errorKind=3'), $wire.Replace('errorKind=2', 'errorKind=02'),
    $wire.Replace('error=4294967295', 'error=-2147483649'), $wire.Replace('error=4294967295', 'error=4294967296'),
    $wire.Replace('error=4294967295', 'error=9223372036854775807'), $wire.Replace('error=4294967295', 'error=-9223372036854775809'),
    $wire.Replace('error=4294967295', 'error=+1'), $wire.Replace('error=4294967295', 'error=-0'),
    $wire.Replace('error=4294967295', 'error=01'), $wire.Replace('error=4294967295', 'error=1.0'),
    $wire.Replace('error=4294967295', 'error=1e3'), $wire.Replace('error=4294967295', 'error=0x80070005'),
    $wire.Replace('error=4294967295', 'error=NaN'), $wire.Replace('error=4294967295', 'error=-'),
    $wire.Replace('error=4294967295', 'error='), $wire.Replace(' error=4294967295', ''),
    $wire.Replace(' fault=5', ''), $wire.Replace('cause=4 fault=5', 'fault=5 cause=4'),
    $wire.Replace(' stage=', '  stage='), $wire.Replace(' stage=', "`tstage="),
    $wire.Replace('HERMES_SETUP_EVIDENCE', 'hermes_setup_evidence'),
    $wire.Replace('HERMES_SETUP_EVIDENCE', 'HERMES_SETUP_EVIDENC'),
    'HERMES_SETUP', " $wire", "$wire ", "$wire`r", "$wire`0",
    "$wire extra=1", "$wire error=0", "$wire token=SYNTHETIC_SECRET",
    $wire.Replace('error=4294967295', 'error=C:\synthetic\SYNTHETIC_SECRET'),
    $wire.Replace('stage=8', 'stage=SYNTHETIC_SECRET'),
    "SYNTHETIC_SECRET $wire", "$wire$wire", ($wire + ('x' * 192)),
    "$wire`n$wire", "$wire`r`n$wire`r`n", "$wire`nHERMES_SETUP_EVIDENCE SYNTHETIC_SECRET",
    ($wire + ('x' * 4096)), ('SYNTHETIC_SECRET' + ('x' * 4096))
)
foreach ($text in $invalid) {
    $result = Read-Output "$diagnostic`n$text"
    Require-Refusal $result
    Require ($result.protocolError -ceq 'InvalidCoordinatorDiagnostics' -and $null -ne $result.diagnosticError `
        -and $null -eq $result.refusalEvidence) 'Invalid/duplicate/oversized diagnostics must be explicit, with no partial evidence.'
    if (("$diagnostic`n$text").Length -le 4096) {
        Require ($result.diagnosticCodes.Count -eq 1 -and $result.diagnosticCodes[0] -ceq 'InvalidEvidence') 'Invalid evidence must not erase existing bounded diagnostic codes.'
    } else {
        Require ($result.diagnosticCodes.Count -eq 0 -and $result.diagnosticError -ceq 'CoordinatorDiagnosticsTooLong') 'Oversized stderr must not retain any diagnostic payload.'
    }
    Require (($result | ConvertTo-Json -Depth 8 -Compress) -cnotmatch 'SYNTHETIC_SECRET|C:.synthetic|token=|HERMES_SETUP') 'Invalid evidence leaked untrusted stderr into the structured result.'
}
Require ((Read-Output "$wire`n$wire").diagnosticError -ceq 'DuplicateCoordinatorEvidence') 'Duplicate evidence must be distinguishable from one malformed line.'
foreach ($text in @('not-json', ('x' * 4097), $refusedReport.Replace('"ready":false', '"ready":true'), $refusedReport.Replace('"schemaVersion":1', '"schemaVersion":2'))) {
    $result = Read-Output $wire $text
    Require ($result.exitCode -eq 1603 -and $null -eq $result.report -and $result.protocolError -ceq 'InvalidCoordinatorReport' `
        -and $null -eq $result.diagnosticError -and $null -ne $result.refusalEvidence) 'Evidence must never repair an invalid report or status disagreement.'
    $result = Read-Output "$wire SYNTHETIC_SECRET" $text
    Require ($result.protocolError -ceq 'InvalidCoordinatorReport' -and $result.diagnosticError -ceq 'InvalidCoordinatorEvidence' `
        -and $null -eq $result.report -and $result.exitCode -eq 1603) 'A diagnostic error must not overwrite the original report-validation failure.'
}
$completedReport = $refusedReport.Replace('"Refused"', '"Completed"').Replace('"ready":false', '"ready":true').Replace('"msiExitCode":null', '"msiExitCode":0')
$result = Read-Output $wire $completedReport 1603
Require ($result.protocolError -ceq 'InvalidCoordinatorReport' -and $null -eq $result.report) 'Evidence must not make an unsuccessful exit ready.'
$result = Read-Output '' $completedReport 0
Require ($null -eq $result.protocolError -and $result.report.ready -and $null -eq $result.refusalEvidence) 'Existing completed reports must remain unchanged.'
$result = Read-Output 'Hermes Setup: Busy.' $refusedReport 1618
Require ($null -eq $result.protocolError -and $result.exitCode -eq 1618 -and $result.diagnosticCodes[0] -ceq 'Busy') 'Existing permitted busy exits must remain unchanged.'

$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$start.UseShellExecute = $false
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-Command',
    "[Console]::Out.WriteLine('$refusedReport'); [Console]::Error.WriteLine('$diagnostic'); [Console]::Error.WriteLine('$wire'); exit 1603")) {
    $start.ArgumentList.Add($argument)
}
$child = [Diagnostics.Process]::new()
$child.StartInfo = $start
$started = $false
try {
    $started = $child.Start()
    Require $started 'The synthetic stderr emitter did not start.'
    $stdout = $child.StandardOutput.ReadToEndAsync()
    $stderr = $child.StandardError.ReadToEndAsync()
    Require ($child.WaitForExit(30000)) 'The synthetic stderr emitter exceeded its time bound.'
    $result = Read-Output $stderr.GetAwaiter().GetResult() $stdout.GetAwaiter().GetResult() $child.ExitCode
    Require ($result.exitCode -eq $child.ExitCode -and $null -eq $result.diagnosticError `
        -and $result.diagnosticCodes[0] -ceq 'InvalidEvidence' -and $result.refusalEvidence.stage -eq 8 `
        -and $result.refusalEvidence.error -eq 4294967295L) 'Actual redirected child stderr did not reach the real bounded wrapper parser.'
    if ($IsWindows) {
        Require-Refusal $result
        Require ($null -eq $result.protocolError) 'Windows child status must agree with its synthetic refusal report.'
    } else {
        Require ($child.ExitCode -eq (1603 % 256) -and $result.protocolError -ceq 'InvalidCoordinatorReport' `
            -and $null -eq $result.report) 'POSIX truncation of a Windows exit code must remain an explicit status disagreement, not simulated Windows acceptance.'
    }
} finally {
    if ($started -and -not $child.HasExited) { $child.Kill(); $child.WaitForExit() }
    $child.Dispose()
}

$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Test-Installer.ps1'), [ref]$tokens, [ref]$errors)
Require ($errors.Count -eq 0) 'The real lifecycle source must parse.'
$functions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('Invoke-Coordinator', 'Save-Evidence')
}, $true))
Require ($functions.Count -eq 2) 'Both actual coordinator evidence functions must be exercised.'
& {
    foreach ($definition in $functions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    function Invoke-HermesSetup {
        param($SetupExecutable, $Operation, $PackagePath, [switch]$AllowRollback, [switch]$Quiet)
        $text = if ($Operation -eq 'install') { $wire } else { $wire.Replace('stage=8', 'stage=12').Replace('cause=4', 'cause=3') }
        if ($script:invalidDiagnostic) { $text += ' token=SYNTHETIC_SECRET' }
        ConvertFrom-HermesSetupProcessOutput -StandardOutput $refusedReport.Replace('"Install"', '"' + $Operation.Substring(0, 1).ToUpperInvariant() + $Operation.Substring(1) + '"') `
            -StandardError "$diagnostic`n$text" -ExitCode 1603 -Operation $Operation
    }
    $savedSystemRoot = $env:SystemRoot
    $destination = Join-Path $PSScriptRoot 'obj/synthetic-setup-evidence'
    $root = $destination
    $SetupExecutable = 'SYNTHETIC-never-executed.exe'
    $completed = $false
    $failure = $null
    $stage = 'install'
    $evidence = [Collections.Generic.List[object]]::new()
    $nativeReports = [Collections.Generic.List[string]]::new()
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    $ownerFixtureId = $null
    $GuardSha256 = 'a' * 64
    $componentExpectation = $null
    $componentExitCode = $null
    $script:invalidDiagnostic = $false
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    try {
        $env:SystemRoot = $PSScriptRoot
        try { $null = Invoke-Coordinator install install 'SYNTHETIC-never-executed.msi'; throw 'Synthetic refusal was accepted.' }
        catch { Require ($_.Exception.Message -ceq 'Unexpected coordinator result during install (exit 1603).') 'Original installation refusal must remain the failure.'; $failure = @{ stage = 'install'; message = $_.Exception.Message } }
        $originalFailure = $failure | ConvertTo-Json -Compress
        $null = Invoke-Coordinator recover failure-recover -AcceptedExits @(0, 1603)
        $cleanupFailures.Add('SYNTHETIC cleanup could not establish a completed state.')
        Save-Evidence
        $saved = Get-Content -LiteralPath (Join-Path $destination 'acceptance.json') -Raw | ConvertFrom-Json
        Require (-not $saved.passed -and ($saved.originalFailure | ConvertTo-Json -Compress) -ceq $originalFailure `
            -and $saved.cleanupFailures.Count -eq 1 -and $saved.stages.Count -eq 2) 'Recovery and cleanup must not replace the original failure or turn acceptance into success.'
        Require ($saved.stages[0].action -ceq 'install' -and $saved.stages[0].refusalEvidence.stage -eq 8 `
            -and $saved.stages[1].action -ceq 'failure-recover' -and $saved.stages[1].refusalEvidence.stage -eq 12) 'Install and recovery evidence must remain in separate actual acceptance stages.'
        foreach ($entry in $saved.stages) {
            Require ($entry.exitCode -eq 1603 -and -not $entry.report.ready -and $entry.report.outcome -ceq 'Refused' `
                -and $entry.report.schemaVersion -eq 1 -and $null -eq $entry.report.msiExitCode `
                -and $entry.diagnosticCodes[0] -ceq 'InvalidEvidence') 'Stage evidence changed the original refusal or public report.'
        }
        $script:invalidDiagnostic = $true
        $rejected = $false
        try { $null = Invoke-Coordinator recover invalid-diagnostic -AcceptedExits @(0, 1603) }
        catch { Require ($_.Exception.Message -ceq 'Coordinator report failed validation: invalid-diagnostic') 'Invalid diagnostics must reach the existing protocol failure boundary.'; $rejected = $true }
        Require $rejected 'A permitted exit must not hide an invalid diagnostic.'
        $savedText = Get-Content -LiteralPath (Join-Path $destination 'acceptance.json') -Raw
        $saved = $savedText | ConvertFrom-Json
        Require ($saved.stages.Count -eq 3 -and $saved.stages[2].exitCode -eq 1603 `
            -and $saved.stages[2].report.outcome -ceq 'Refused' -and $null -eq $saved.stages[2].refusalEvidence `
            -and $saved.stages[2].diagnosticError -ceq 'InvalidCoordinatorEvidence' `
            -and ($saved.originalFailure | ConvertTo-Json -Compress) -ceq $originalFailure `
            -and $savedText -cnotmatch 'SYNTHETIC_SECRET|token=|HERMES_SETUP') 'Invalid evidence must persist explicitly without stderr leakage or loss of original failure.'
    } finally {
        $env:SystemRoot = $savedSystemRoot
        Remove-Variable -Name invalidDiagnostic, lastCoordinator -Scope Script -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath (Join-Path $destination 'acceptance.json') -ErrorAction SilentlyContinue
        [IO.Directory]::Delete($destination)
    }
}
Write-Output "Setup evidence controls passed: $positiveCount numerical combinations/boundaries, $($invalid.Count) adverse diagnostics, real synthetic child stderr, report agreement and separate actual coordinator-stage persistence; synthetic only."
