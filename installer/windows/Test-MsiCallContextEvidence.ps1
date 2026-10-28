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
$core = 'HERMES_SETUP_MSI v=2 action=1399 error=0 readFailed=0 preflight=0 fence=0'
$high = ' context=1 process=1 elevated=1 elevationType=1 integrity=12288 admin=1 thread=3 linked=3 sameOwner=-1 linkedElevated=-1'
$medium = ' context=1 process=1 elevated=0 elevationType=3 integrity=8192 admin=0 thread=3 linked=1 sameOwner=1 linkedElevated=1'
$failed = ' context=1 process=2 elevated=-1 elevationType=-1 integrity=-1 admin=-1 thread=2 linked=2 sameOwner=-1 linkedElevated=-1'
$missing = ' context=1 process=0 elevated=-1 elevationType=-1 integrity=-1 admin=-1 thread=0 linked=0 sameOwner=-1 linkedElevated=-1'
$hint = 'HERMES_NATIVE_PREFLIGHT v=1 source=1 observed=1 point=2 reason=12'

function Read-Context([string]$Text, [string]$Report = $report, [int]$ExitCode = 1603) {
    ConvertFrom-HermesSetupProcessOutput -StandardOutput $Report `
        -StandardError $Text -ExitCode $ExitCode -Operation install
}

foreach ($suffix in @($high, $medium, $failed, $missing,
    $medium.Replace('sameOwner=1', 'sameOwner=0'),
    $high.Replace('thread=3', 'thread=1'),
    $high.Replace('thread=3', 'thread=2'),
    $high.Replace('linked=3', 'linked=2'))) {
    $result = Read-Context "$code`n$core$suffix`n$hint"
    Require ($null -eq $result.protocolError -and $null -eq $result.diagnosticError `
        -and $result.msiFailureEvidence.v -eq 2 -and $result.msiFailureEvidence.context.v -eq 1 `
        -and $result.report.msiExitCode -eq 1603 -and -not $result.report.ready) `
        'A caller observation changed the closed native result or was lost.'
    foreach ($property in $result.msiFailureEvidence.context.PSObject.Properties) {
        Require ($property.Value -is [int] -or $property.Value -is [long]) `
            'Caller context retained nonnumeric content.'
    }
    Assert-HermesCoordinatorMsiEvidence $result
    Assert-HermesCoordinatorNativePreflightEvidence $result
}

$invalid = @(
    $core,
    ($core.Replace('v=2', 'v=1') + $high),
    ($core.Replace('v=2', 'v=3') + $high),
    ($core + $high.Replace('context=1', 'context=2')),
    ($core + $high.Replace('process=1', 'process=3')),
    ($core + $high.Replace('process=1', 'process=0')),
    ($core + $high.Replace('elevated=1', 'elevated=-1')),
    ($core + $high.Replace('elevated=1', 'elevated=01')),
    ($core + $high.Replace('elevationType=1', 'elevationType=0')),
    ($core + $high.Replace('integrity=12288', 'integrity=65536')),
    ($core + $high.Replace('integrity=12288', 'integrity=012288')),
    ($core + $high.Replace('admin=1', 'admin=2')),
    ($core + $high.Replace('thread=3', 'thread=0')),
    ($core + $high.Replace('thread=3', 'thread=4')),
    ($core + $high.Replace('linked=3', 'linked=1')),
    ($core + $high.Replace('sameOwner=-1', 'sameOwner=0')),
    ($core + $medium.Replace('sameOwner=1', 'sameOwner=-1')),
    ($core + $medium.Replace('linkedElevated=1', 'linkedElevated=-1')),
    ($core + $failed.Replace('elevated=-1', 'elevated=0')),
    ($core + $missing.Replace('thread=0', 'thread=3')),
    ($core + $high.Replace('admin=1', 'admin=true')),
    ($core + $high.Replace('integrity=12288', 'integrity=C:\owner\PRIVATE_SECRET')),
    ("$core$high token=PRIVATE_SECRET"),
    ("$core$high sid=PRIVATE_SECRET"),
    ("$core$high "),
    (" $core$high"),
    ("$core$high`0"),
    ("$core$high`n$core$medium")
)
foreach ($line in $invalid) {
    $result = Read-Context "$code`n$line`n$hint"
    Require ($result.protocolError -ceq 'InvalidCoordinatorDiagnostics' `
        -and $null -ne $result.diagnosticError -and $null -eq $result.msiFailureEvidence `
        -and $null -eq $result.nativePreflightEvidence -and $result.report.msiExitCode -eq 1603 `
        -and -not $result.report.ready) 'Malformed caller context retained partial evidence.'
    Require (($result | ConvertTo-Json -Depth 12 -Compress) -cnotmatch `
        'PRIVATE_SECRET|token=|sid=|C:.owner|HERMES_SETUP|HERMES_NATIVE') `
        'Rejected caller context leaked raw diagnostic content.'
}
foreach ($text in @(
    "unknown PRIVATE_SECRET`n$code`n$core$high`n$hint",
    "$code`n$core$high`n$hint`nunknown PRIVATE_SECRET",
    "$code`n$core$high`n$hint`n$code", "$hint`n$code`n$core$high"
)) {
    $result = Read-Context $text
    Require ($result.protocolError -ceq 'InvalidCoordinatorDiagnostics' `
        -and $null -eq $result.msiFailureEvidence -and $null -eq $result.nativePreflightEvidence `
        -and ($result | ConvertTo-Json -Depth 12 -Compress) -cnotmatch 'PRIVATE_SECRET|HERMES_SETUP|HERMES_NATIVE') `
        'Unconsumed or reordered caller-context records must reject all optional metadata without leaking stderr.'
}

$limit = "$code`n$core$high`n$hint`n"
$result = Read-Context ($limit + ('x' * (4096 - $limit.Length)))
Require ($result.diagnosticError -ceq 'InvalidCoordinatorDiagnosticFrame' `
    -and $null -eq $result.msiFailureEvidence -and $null -eq $result.nativePreflightEvidence) `
    'Trailing noise at the inclusive stderr bound must not retain caller context.'
$result = Read-Context ($limit + ('x' * (4097 - $limit.Length)))
Require ($result.diagnosticError -ceq 'CoordinatorDiagnosticsTooLong' `
    -and $null -eq $result.msiFailureEvidence) 'Oversized stderr retained caller context.'
$result = Read-Context "$code`n$core$high`n$hint" 'not-json'
Require ($result.protocolError -ceq 'InvalidCoordinatorReport' -and $null -eq $result.report `
    -and $null -eq $result.msiFailureEvidence) 'Caller context repaired an invalid public report.'
$mixed = "$code`n$core$high`nHERMES_SETUP_EVIDENCE v=1 stage=8 cause=4 fault=0 errorKind=0 error=0"
$result = Read-Context $mixed
Require ($null -ne $result.diagnosticError -and $null -eq $result.msiFailureEvidence `
    -and $null -eq $result.refusalEvidence) 'Mixed evidence retained partial caller context.'

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
Require ($functions.Count -eq 2) 'Actual invoke/save helpers were not found.'
& {
    foreach ($definition in $functions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    function Invoke-HermesSetup {
        param($SetupExecutable, $Operation, $PackagePath, [switch]$AllowRollback, [switch]$Quiet)
        $operationName = $Operation.Substring(0, 1).ToUpperInvariant() + $Operation.Substring(1)
        $suffix = if ($Operation -eq 'install') { $high } else { $medium }
        ConvertFrom-HermesSetupProcessOutput `
            -StandardOutput $report.Replace('"Install"', '"' + $operationName + '"') `
            -StandardError "$code`n$core$suffix`n$hint`n" `
            -ExitCode 1603 -Operation $Operation
    }
    $destination = Join-Path $PSScriptRoot ('obj/Hermes.Context.' + [Guid]::NewGuid().ToString('N'))
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
                'Caller context replaced the original installation failure.'
            $failure = @{ stage = 'install'; message = $_.Exception.Message }
        }
        Require ($null -ne $failure) 'The failed synthetic installation was accepted.'
        $originalFailure = $failure | ConvertTo-Json -Compress
        $originalStage = $evidence[0] | ConvertTo-Json -Depth 12 -Compress
        $null = Invoke-Coordinator recover failure-recover -AcceptedExits @(0, 1603)
        $cleanupFailures.Add('SYNTHETIC cleanup did not establish a completed state.')
        Save-Evidence
        $path = Join-Path $destination 'acceptance.json'
        $savedText = Get-Content -LiteralPath $path -Raw
        $saved = $savedText | ConvertFrom-Json -Depth 40
        Assert-HermesCoordinatorStages $saved.stages
        Require (-not $saved.passed -and $saved.stages.Count -eq 2 -and $saved.cleanupFailures.Count -eq 1 `
            -and ($saved.originalFailure | ConvertTo-Json -Compress) -ceq $originalFailure `
            -and ($evidence[0] | ConvertTo-Json -Depth 12 -Compress) -ceq $originalStage) `
            'Recovery or saving mutated the original failure or promoted cleanup.'
        Require ($saved.stages[0].report.operation -ceq 'Install' `
            -and $saved.stages[0].msiFailureEvidence.context.elevated -eq 1 `
            -and $saved.stages[0].msiFailureEvidence.context.integrity -eq 12288 `
            -and $saved.stages[1].report.operation -ceq 'Recover' `
            -and $saved.stages[1].msiFailureEvidence.context.elevated -eq 0 `
            -and $saved.stages[1].msiFailureEvidence.context.integrity -eq 8192) `
            'Actual invoke/save helpers lost or combined separate caller contexts.'
        Require ($savedText -cnotmatch 'PRIVATE_SECRET|token=|sid=|HERMES_SETUP|HERMES_NATIVE') `
            'Saved caller context retained raw stderr.'
        [IO.File]::WriteAllText((Join-Path $destination 'actual-servicing.json'), '{}')
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'Assert-ServicingEvidence.ps1') -EvidenceDirectory $destination }
        catch {
            Require ($_.Exception.Message -ceq 'Ordinary installed acceptance did not complete cleanly.') `
                'Final consumer did not validate the bounded failed-stage context.'
            $rejected = $true
        }
        Require $rejected 'Caller measurements authorized final publication.'
        $saved.stages[0].msiFailureEvidence.context.elevated = 2
        [IO.File]::WriteAllText($path, ($saved | ConvertTo-Json -Depth 40))
        $rejected = $false
        try { & (Join-Path $PSScriptRoot 'Assert-ServicingEvidence.ps1') -EvidenceDirectory $destination }
        catch {
            Require ($_.Exception.Message -ceq 'Invalid coordinator MSI observation.') `
                'Final consumer bypassed strict saved-context validation.'
            $rejected = $true
        }
        Require $rejected 'Tampered caller measurements passed the actual final consumer.'
    } finally {
        $env:SystemRoot = $savedSystemRoot
        Remove-Variable -Name lastCoordinator -Scope Script -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $destination -Recurse -Force
    }
}
Write-Output 'Bounded caller-context parser, actual invoke/save and final-consumer controls passed; synthetic context is not native admission or fence proof.'
