#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Setup-Process.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Acceptance-Report.psm1') -Force

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

$report = '{"schemaVersion":1,"operation":"Install","outcome":"RecoveryRequired","ready":false,"msiExitCode":1603}'
$code = 'Hermes Setup: TransactionFenceMissing.'
$msi = 'HERMES_SETUP_MSI v=1 action=1399 error=0 readFailed=0 preflight=0 fence=0'
$hint = 'HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=1 point=2 reason=11'

function Read-Hint([string]$Text, [string]$Report = $report, [int]$ExitCode = 1603) {
    ConvertFrom-HermesSetupProcessOutput -StandardOutput $Report `
        -StandardError $Text -ExitCode $ExitCode -Operation install
}

$validLines = @(
    'HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=0 point=0 reason=0'
    foreach ($point in 1..9) {
        foreach ($reason in @(0, 1, 4)) {
            "HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=1 point=$point reason=$reason"
        }
    }
    foreach ($point in 2..9) {
        "HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=1 point=$point reason=2"
    }
    foreach ($reason in 10..22) {
        "HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=1 point=2 reason=$reason"
    }
)
foreach ($line in $validLines) {
    foreach ($text in @("$code`n$msi`n$line", "$code`r`n$msi`r`n$line`r`n")) {
        $result = Read-Hint $text
        Require ($null -eq $result.protocolError -and $null -eq $result.diagnosticError `
            -and $null -eq $result.refusalEvidence -and $null -ne $result.nativePreflightEvidence `
            -and $result.msiFailureEvidence.error -eq 0 -and $result.report.msiExitCode -eq 1603 `
            -and -not $result.report.ready) 'A closed native hint changed the original result or was lost.'
        $value = $result.nativePreflightEvidence
        Require (($value.PSObject.Properties.Name -join ',') -ceq 'v,source,observed,point,reason' `
            -and $value.source -eq 1) 'Native hints must have only five numeric fields and an untrusted source.'
        foreach ($property in $value.PSObject.Properties) {
            Require ($property.Value -is [int] -or $property.Value -is [long]) 'Native hint retained a nonnumeric field.'
        }
        Assert-HermesCoordinatorNativePreflightEvidence $result
    }
}
$invalidLines = @(
    $hint.Replace('v=1', 'v=2'),
    $hint.Replace('source=1', 'source=0'), $hint.Replace('source=1', 'source=2'),
    $hint.Replace('observed=1', 'observed=0'), $hint.Replace('observed=1', 'observed=true'),
    $hint.Replace('point=2', 'point=02'), $hint.Replace('point=2', 'point=10'),
    $hint.Replace('point=2', 'point=-1'), $hint.Replace('point=2', 'point=6'),
    $hint.Replace('reason=11', 'reason=3'), $hint.Replace('reason=11', 'reason=23'),
    $hint.Replace('reason=11', 'reason=011'), $hint.Replace('reason=11', 'reason=1e1'),
    $hint.Replace('reason=11', 'reason=-1'), $hint.Replace('reason=11', 'reason=Scheduled'),
    $hint.Replace('HERMES_NATIVE_PREFLIGHT', 'hermes_native_preflight'),
    " $hint", "$hint ", "$hint`0", "$hint token=SYNTHETIC_SECRET",
    "$hint`n$hint", "$hint`nHERMES_NATIVE_PREFLIGHT SYNTHETIC_SECRET",
    $hint.Replace('reason=11', 'reason=C:\owner\SYNTHETIC_SECRET'),
    'HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=0 point=0 reason=1'
)
foreach ($line in $invalidLines) {
    $result = Read-Hint "$code`n$msi`n$line"
    Require ($result.protocolError -ceq 'InvalidCoordinatorDiagnostics' `
        -and $null -ne $result.diagnosticError -and $null -eq $result.nativePreflightEvidence `
        -and $null -eq $result.msiFailureEvidence -and $result.report.msiExitCode -eq 1603 `
        -and -not $result.report.ready) 'Malformed hints yielded partial evidence or changed the native result.'
    Require (($result | ConvertTo-Json -Depth 8 -Compress) -cnotmatch `
        'SYNTHETIC_SECRET|C:.owner|token=|HERMES_NATIVE') 'Rejected hints leaked raw stderr.'
}
foreach ($text in @(
    "$code`n$hint",
    "$hint`n$code`n$msi", "$code`n$hint`n$msi",
    "unknown SYNTHETIC_SECRET`n$code`n$msi`n$hint",
    "$code`n$msi`n$hint`nunknown SYNTHETIC_SECRET",
    "$code`n$msi`n$hint`n$code", "$code`n$msi`n$hint`n`n",
    "$code`n$($msi.Replace('preflight=0', 'preflight=1'))`n$hint",
    "$code`n$msi`n$hint`nHERMES_SETUP_EVIDENCE v=1 stage=8 cause=4 fault=0 errorKind=0 error=0"
)) {
    $result = Read-Hint $text
    Require ($null -ne $result.diagnosticError -and $null -eq $result.nativePreflightEvidence `
        -and $null -eq $result.msiFailureEvidence -and $null -eq $result.refusalEvidence `
        -and $result.report.msiExitCode -eq 1603) 'Unbound or mixed hints must not become accepted evidence.'
}
$result = Read-Hint "$code`n$msi`n$hint" 'not-json'
Require ($result.protocolError -ceq 'InvalidCoordinatorReport' -and $null -eq $result.report `
    -and $null -eq $result.nativePreflightEvidence) 'A hint repaired an invalid public report.'
$limit = "$code`n$msi`n$hint`n"
$result = Read-Hint ($limit + ('x' * (4096 - $limit.Length)))
Require ($result.diagnosticError -ceq 'InvalidCoordinatorDiagnosticFrame' `
    -and $null -eq $result.nativePreflightEvidence -and $null -eq $result.msiFailureEvidence) 'Trailing noise at the inclusive stderr bound must not retain a hint.'
$result = Read-Hint ($limit + ('x' * (4097 - $limit.Length)))
Require ($result.diagnosticError -ceq 'CoordinatorDiagnosticsTooLong' `
    -and $null -eq $result.nativePreflightEvidence) 'Oversized stderr retained a hint.'

$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Test-Installer.ps1'), [ref]$tokens, [ref]$errors)
Require ($errors.Count -eq 0) 'Actual coordinator lifecycle source did not parse.'
$functions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] `
        -and $node.Name -in @('Invoke-Coordinator', 'Save-Evidence')
}, $true))
Require ($functions.Count -eq 2) 'Actual coordinator invoke/save functions were not found.'
& {
    foreach ($definition in $functions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    function Invoke-HermesSetup {
        param($SetupExecutable, $Operation, $PackagePath, [switch]$AllowRollback, [switch]$Quiet)
        $operationName = $Operation.Substring(0, 1).ToUpperInvariant() + $Operation.Substring(1)
        $nativeHint = if ($Operation -eq 'install') { $hint } `
            else { $hint.Replace('point=2 reason=11', 'point=8 reason=2') }
        ConvertFrom-HermesSetupProcessOutput `
            -StandardOutput $report.Replace('"Install"', '"' + $operationName + '"') `
            -StandardError "$code`n$msi`n$nativeHint`n" `
            -ExitCode 1603 -Operation $Operation
    }
    $destination = Join-Path $PSScriptRoot ('obj/Hermes.NativeHint.' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $savedSystemRoot = $env:SystemRoot
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
    try {
        $env:SystemRoot = $destination
        try { $null = Invoke-Coordinator install install 'SYNTHETIC-never-executed.msi' }
        catch {
            Require ($_.Exception.Message -ceq 'Unexpected coordinator result during install (exit 1603).') `
                'The original installation failure was replaced.'
            $failure = @{ stage = 'install'; message = $_.Exception.Message }
        }
        Require ($null -ne $failure) 'The synthetic failed installation was accepted.'
        $originalFailure = $failure | ConvertTo-Json -Compress
        $originalStage = $evidence[0] | ConvertTo-Json -Depth 8 -Compress
        $null = Invoke-Coordinator recover failure-recover -AcceptedExits @(0, 1603)
        $cleanupFailures.Add('SYNTHETIC cleanup did not establish a completed state.')
        Save-Evidence
        $path = Join-Path $destination 'acceptance.json'
        $savedText = Get-Content -LiteralPath $path -Raw
        $saved = $savedText | ConvertFrom-Json
        Assert-HermesCoordinatorStages $saved.stages
        Require (-not $saved.passed -and $saved.stages.Count -eq 2 -and $saved.cleanupFailures.Count -eq 1 `
            -and ($saved.originalFailure | ConvertTo-Json -Compress) -ceq $originalFailure `
            -and ($evidence[0] | ConvertTo-Json -Depth 8 -Compress) -ceq $originalStage) `
            'Recovery or saving mutated the original install or promoted cleanup.'
        Require ($saved.stages[0].report.operation -ceq 'Install' `
            -and $saved.stages[0].nativePreflightEvidence.point -eq 2 `
            -and $saved.stages[0].nativePreflightEvidence.reason -eq 11 `
            -and $saved.stages[1].report.operation -ceq 'Recover' `
            -and $saved.stages[1].nativePreflightEvidence.point -eq 8 `
            -and $saved.stages[1].nativePreflightEvidence.reason -eq 2) `
            'Actual invoke/save helpers lost or combined the separate native hints.'
        Require ($savedText -cnotmatch 'SYNTHETIC_SECRET|token=|HERMES_NATIVE|HERMES_SETUP') `
            'The saved evidence retained raw stderr.'
        [IO.File]::WriteAllText((Join-Path $destination 'actual-servicing.json'), '{}')
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'Assert-ServicingEvidence.ps1') -EvidenceDirectory $destination }
        catch {
            Require ($_.Exception.Message -ceq 'Ordinary installed acceptance did not complete cleanly.') `
                'Final publication did not consume the valid bounded failed-stage data.'
            $rejected = $true
        }
        Require $rejected 'An untrusted hint authorized final publication.'
        $saved.stages[0].nativePreflightEvidence.reason = 99
        [IO.File]::WriteAllText($path, ($saved | ConvertTo-Json -Depth 40))
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'Assert-ServicingEvidence.ps1') -EvidenceDirectory $destination }
        catch {
            Require ($_.Exception.Message -ceq 'Invalid coordinator native-preflight observation.') `
                'Final publication bypassed strict native-hint validation.'
            $rejected = $true
        }
        Require $rejected 'Tampered saved native hints passed the actual final consumer.'
    } finally {
        $env:SystemRoot = $savedSystemRoot
        Remove-Variable -Name lastCoordinator -Scope Script -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
}
Write-Output 'Closed native-preflight parser, actual coordinator save and final-consumer controls passed; synthetic controls are not native ownership or fence evidence.'
