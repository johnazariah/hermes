#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Native-StartupTrace.psm1') -Force

$targetRow = 'HERMES_STARTUP v=1 stage=1 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 exit=0 parent=0 cleanup=0 fault=0 cleanupFault=0'
$finalRow = 'HERMES_STARTUP v=1 stage=2 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 exit=3221225794 parent=1 cleanup=1 fault=0 cleanupFault=0'
$failureLine = '16:1 @ 00000000 - LdrpCallInitRoutine - ERROR: DLL_PROCESS_ATTACH for dll "C:\Windows\System32\sample.dll" (InitRoutine: 0000000012345678) failed.'

function Assert-Control {
    param([bool]$Value)
    if (-not $Value) { throw 'Bounded startup protocol control failed.' }
}

function New-ControlFixture {
    param(
        [int]$Current = 22, [string]$Event = '16',
        [int]$Flags = 2, [bool]$WithFailure = $true,
        [bool]$WithExit = $true, [bool]$EarlyFailure = $false,
        [string[]]$BeforeExitLines = @()
    )
    $state = New-HermesStartupState
    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
    Add-HermesStartupLine $state "HERMES_CDB v=1 kind=1 pid=$Current"
    Add-HermesStartupLine $state "Last event: $Event.1: Create process 1:$Event"
    Add-HermesStartupLine $state ("Current NtGlobalFlag contents: 0x{0:x8}" -f $Flags)
    Add-HermesStartupLine $state "HERMES_CDB v=1 kind=2 pid=$Current"
    Update-HermesStartupRows $state ($targetRow + "`n")
    if ($EarlyFailure) { Add-HermesStartupLine $state $failureLine }
    if (Test-HermesStartupArm $state) {
        Set-HermesStartupArmAcknowledged $state
    }
    Add-HermesStartupLine $state 'ModLoad: 0000000012340000 0000000012350000 C:\private\not-a-failure.dll'
    if ($WithFailure) { Add-HermesStartupLine $state $failureLine }
    foreach ($line in $BeforeExitLines) {
        Add-HermesStartupLine $state $line
    }
    if ($WithExit) {
        Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=3 pid=22'
        Add-HermesStartupLine $state 'Last event: 16.1: Exit process 1:16, code c0000142'
        Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=4 pid=22'
    }
    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=3 pid=11'
    Add-HermesStartupLine $state 'Last event: b.1: Exit process 0:b, code 0'
    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=4 pid=11'
    Update-HermesStartupRows $state ($targetRow + "`n" + $finalRow + "`n")
    return $state
}

function Get-ControlStreamSnapshot {
    param([hashtable]$Stream)
    @($Stream.Count, $Stream.Prefix, $Stream.Tail, $Stream.DiscardLine,
        [Convert]::ToHexString($Stream.Hash.GetCurrentHash())) |
        ConvertTo-Json -Compress
}

function New-ControlReader {
    param([string]$Text)
    [IO.StreamReader]::new(
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($Text)),
        [Text.Encoding]::UTF8)
}

class StartupControlIdleReader : IO.StreamReader {
    [string]$FirstText
    [int]$Reads = 0
    [Threading.Tasks.TaskCompletionSource[int]]$Idle = [Threading.Tasks.TaskCompletionSource[int]]::new()

    StartupControlIdleReader([string]$text) : base([IO.MemoryStream]::new()) {
        $this.FirstText = $text
    }

    [Threading.Tasks.Task[int]] ReadAsync([char[]]$buffer, [int]$offset, [int]$count) {
        $this.Reads++
        if ($this.Reads -eq 1 -and $this.FirstText.Length -gt 0) {
            $this.FirstText.CopyTo(0, $buffer, $offset, $this.FirstText.Length)
            return [Threading.Tasks.Task]::FromResult($this.FirstText.Length)
        }
        return $this.Idle.Task
    }
}

# Load only the real pump/supervisor functions, never the Windows script body.
# Exercise its actual finally block with an inert debugger and owned memory
# readers; the native Stop-TraceDebugger definition is deliberately not loaded.
$tokens = $null
$parseErrors = $null
$driverAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Invoke-NativeStartupTrace.ps1'),
    [ref]$tokens, [ref]$parseErrors)
Assert-Control ($parseErrors.Count -eq 0)
foreach ($name in @('Start-TraceRead', 'Receive-TraceRead', 'Invoke-TraceCollection')) {
    $definitions = @($driverAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
            -and $node.Name -ceq $name
    }, $true))
    Assert-Control ($definitions.Count -eq 1)
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$entries = @($driverAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] `
        -and $node.GetCommandName() -ceq 'Invoke-TraceCollection'
}, $true))
Assert-Control ($entries.Count -eq 1)
$driverTry = $entries[0].Parent.Parent.Parent
Assert-Control ($driverTry -is [Management.Automation.Language.TryStatementAst] `
    -and $null -ne $driverTry.Finally)
$stopCalls = @($driverTry.Finally.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CommandAst] `
        -and $node.GetCommandName() -ceq 'Stop-TraceDebugger'
}, $true))
Assert-Control ($stopCalls.Count -eq 1)
$controlDriver = [scriptblock]::Create(
    'try { ' + $entries[0].Parent.Extent.Text + ' } finally ' +
    $driverTry.Finally.Extent.Text)

function Read-TraceControl {
    param([string]$Path)
    $script:controlReads++
}

function Publish-TraceArm { $script:controlArms++ }

function Start-Sleep {
    param([int]$Milliseconds)
    $script:controlWaits++
    throw 'Collection must reach owned cleanup without another wait.'
}

function Stop-TraceDebugger {
    $script:controlStops++
    if ($script:controlStopFault) { throw 'Synthetic owned cleanup failure.' }
}

$cases = 1..37
$passed = 0
foreach ($case in $cases) {
    $state = $null
    try {
        switch ($case) {
            1 {
                $state = New-ControlFixture
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.completed -eq 1 -and $report.firstFailureKind -eq 1 `
                    -and $report.firstFailureStatus -eq 0 -and $report.nativeAcceptance -eq 0)
                Assert-Control ($report.armingVerified -eq 1 -and $report.cleanupComplete -eq 1 `
                    -and $report.exitObserved -eq 1 -and $report.exitCode -eq 3221225794)
                Assert-Control ((Test-HermesStartupArmed $state) `
                    -and -not (Test-HermesStartupArm $state) `
                    -and $state.ArmedBinding.Born -eq 123)
            }
            2 {
                $state = New-ControlFixture -WithFailure $false
                Assert-Control ((New-HermesStartupReport $state $true $true).status -eq 2)
            }
            3 {
                $state = New-ControlFixture -Current 23
                Assert-Control (-not (Test-HermesStartupArm $state))
                Assert-Control (-not (Test-HermesStartupArmed $state) `
                    -and $null -eq $state.ArmedBinding `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            4 {
                $state = New-ControlFixture -Event '17'
                Assert-Control (-not (Test-HermesStartupArm $state))
                Assert-Control (-not (Test-HermesStartupArmed $state) `
                    -and $null -eq $state.ArmedBinding `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            5 {
                $state = New-ControlFixture
                $state.Initial = 12
                Assert-Control ((New-HermesStartupReport $state $true $true).completed -eq 0)
                $state.Initial = 11
                $state.Target.born = 124
                Assert-Control (-not (Test-HermesStartupArmed $state) `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            6 {
                $state = New-ControlFixture -Flags 0
                Assert-Control (-not (Test-HermesStartupArm $state))
            }
            7 {
                $state = New-ControlFixture
                $state.Arm.Flags = -1
                Assert-Control ((New-HermesStartupReport $state $true $true).completed -eq 0)
                Assert-Control (-not (Test-HermesStartupArmed $state))
            }
            8 {
                $state = New-ControlFixture -EarlyFailure $true
                Assert-Control ($state.Protocol -eq 1)
                Assert-Control (-not $state.Acked -and $null -eq $state.ArmedBinding `
                    -and $null -eq $state.Failure `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            9 {
                $state = New-ControlFixture -WithFailure $false
                Add-HermesStartupLine $state ($failureLine.Replace('16:1', '17:1'))
                Assert-Control ($state.Unknown -eq 1 -and $null -eq $state.Failure)
            }
            10 {
                $state = New-ControlFixture -WithFailure $false -BeforeExitLines @(
                    '16:1 @ 00000000 - LdrpUnknown - ERROR: unrecognised initialization failure'
                )
                Add-HermesStartupLine $state '16:1 @ 00000000 - LdrpUnknown - ERROR: unrecognised initialization failure'
                Assert-Control ($state.Unknown -eq 1 -and $null -eq $state.Failure)
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.status -eq 3 -and $report.completed -eq 0 `
                    -and $report.firstFailureKind -eq 0)
                Add-HermesStartupLine $state $failureLine
                Assert-Control ($null -eq $state.Failure `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            11 {
                $state = New-ControlFixture -WithExit $false
                Assert-Control ((New-HermesStartupReport $state $true $true).cleanupComplete -eq 0)
                Assert-Control ((New-HermesStartupReport $state $true $true).completed -eq 0)
                $state.Acked = $false
                $state.ArmedBinding = $null
                $state.Final = $null
                Assert-Control (-not (Test-HermesStartupArm $state))
                Set-HermesStartupArmAcknowledged $state
                Assert-Control ($state.Protocol -eq 1 -and -not $state.Acked `
                    -and $null -eq $state.ArmedBinding)
            }
            12 {
                $state = New-ControlFixture
                $state.Exits['22'] = 37
                Assert-Control ((New-HermesStartupReport $state $true $true).status -eq 4)
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.completed -eq 0 -and $report.cleanupComplete -eq 0 `
                    -and $report.armingVerified -eq 1)
            }
            13 {
                $state = New-ControlFixture
                $state.Timeout = 1
                Assert-Control ((New-HermesStartupReport $state $true $true).status -eq 5)
            }
            14 {
                $state = New-ControlFixture
                $report = New-HermesStartupReport $state $false $true
                Assert-Control ($report.completed -eq 0 -and $report.cleanupComplete -eq 0)
                $report = New-HermesStartupReport $state $true $false
                Assert-Control ($report.completed -eq 0 -and $report.cleanupComplete -eq 0)
                $state.Final.cleanup = 0
                Assert-Control ((New-HermesStartupReport $state $true $true).cleanupComplete -eq 0)
                Assert-Control ((New-HermesStartupReport $state $true $true).completed -eq 0)
                $state.Final.cleanup = 1
                $state.Final.parent = 0
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.completed -eq 0 -and $report.parentUnchanged -eq 0)
            }
            15 {
                $state = New-ControlFixture
                $state.Final.fault = 1460
                $state.Final.cleanupFault = 6
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.error -eq 1460 -and $report.cleanupFault -eq 6 `
                    -and $report.completed -eq 0)
                Assert-Control ($report.cleanupComplete -eq 0)
            }
            16 {
                $state = New-ControlFixture
                Add-HermesStartupText $state $state.Out ('x' * 4096) $true
                Add-HermesStartupText $state $state.Out "x`n" $true
                Assert-Control ($state.Bounds -eq 1 -and $state.Out.Prefix.Length -eq 4096)
            }
            17 {
                $state = New-ControlFixture
                $state.AcceptedUtf8Bytes = [long]8388608
                Add-HermesStartupText $state $state.Out 'x' $true
                Assert-Control ($state.Bounds -eq 1 -and $state.Out.Count -eq 0 `
                    -and $state.AcceptedUtf8Bytes -eq 8388608)
            }
            18 {
                $state = New-ControlFixture
                Assert-Control ($null -eq (ConvertFrom-HermesStartupRow (
                    $targetRow.Replace('pid=22', 'pid=022'))))
            }
            19 {
                $state = New-ControlFixture
                Assert-Control ($null -eq (ConvertFrom-HermesStartupRow (
                    $targetRow.Replace('pid=22', 'pid=4294967296'))))
            }
            20 {
                $state = New-ControlFixture
                Assert-Control ($null -eq (ConvertFrom-HermesStartupRow (
                    $targetRow + ' command=1')))
            }
            21 {
                $state = New-ControlFixture
                Update-HermesStartupRows $state ($targetRow + "`n" + $finalRow + "`n" + $finalRow + "`n")
                Assert-Control ($state.Protocol -eq 1)
                Assert-Control ((New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            22 {
                $state = New-ControlFixture -BeforeExitLines @(
                    ($failureLine.Replace('sample.dll', 'second.dll')),
                    '16:1 @ 00000000 - LdrpInitializeProcess - ERROR: Process initialization failed with status 0xc0000001.'
                )
                $first = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes('sample.dll'))).ToLowerInvariant()
                Assert-Control ($state.Failure.ModuleSha256 -ceq $first `
                    -and $state.Failure.Kind -eq 1 -and $state.Failure.Status -eq 0 `
                    -and $state.Protocol -eq 0 -and $state.Unknown -eq 0 `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 1)
                Add-HermesStartupLine $state ($failureLine.Replace('sample.dll', 'second.dll'))
                Assert-Control ($state.Failure.ModuleSha256 -ceq $first)
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($state.Protocol -eq 1 -and $report.completed -eq 0 `
                    -and $report.firstFailureKind -eq 0)
            }
            23 {
                $state = New-ControlFixture
                Add-HermesStartupText $state $state.Err `
                    'C:\private\secret S-1-5-21-123 --command arbitrary 0000000012345678' $false
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Compress
                Assert-Control ($json -notmatch 'private|secret|S-1-|--command|12345678|sample\.dll' `
                    -and $report.moduleSha256 -cmatch '\A[0-9a-f]{64}\z')
            }
            24 {
                $state = New-ControlFixture -WithFailure $false
                $state.Exits['22'] = 37
                $state.Final.exit = 37
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.powershellExit37 -eq 1 `
                    -and $report.debuggerChangedFailure -eq 1 -and $report.completed -eq 0 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                $state.ArmedBinding = $null
                Assert-Control ($state.Acked -and -not (Test-HermesStartupArmed $state))
                $state.Acked = $false
                $state.Final = $null
                Assert-Control (-not (Test-HermesStartupArm $state))
                Set-HermesStartupArmAcknowledged $state
                Assert-Control ($state.Protocol -eq 1 -and -not $state.Acked `
                    -and $null -eq $state.ArmedBinding `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            25 {
                $state = New-ControlFixture
                $chunk = 'a' * 4096
                for ($index = 0; $index -lt 2048; $index++) {
                    $stream = if ($index % 2 -eq 0) { $state.Out } else { $state.Err }
                    Add-HermesStartupText $state $stream $chunk $false
                }
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and $state.Out.Count -eq 4194304 -and $state.Err.Count -eq 4194304)
                $outBefore = Get-ControlStreamSnapshot $state.Out
                $errBefore = Get-ControlStreamSnapshot $state.Err
                Add-HermesStartupText $state $state.Err 'a' $false
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                    -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore `
                    -and (New-HermesStartupReport $state $true $true).status -eq 7)
            }
            26 {
                $state = New-ControlFixture
                $state.AcceptedUtf8Bytes = [long]8388604
                $emoji = [char]::ConvertFromUtf32(0x1f600)
                Add-HermesStartupText $state $state.Out $emoji $false
                $expected = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($emoji)))
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($state.Bounds -eq 1 -and $report.acceptedUtf8Bytes -eq 8388608 `
                    -and $report.outputBudgetBytes -eq 8388608 -and $report.outChars -eq 2 `
                    -and $report.outKept -eq 2 -and $report.outDropped -eq 0 `
                    -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $expected)
                Add-HermesStartupText $state $state.Err ([string][char]0x00e9) $false
                Assert-Control ($state.Bounds -eq 1 -and $state.Err.Count -eq 0 `
                    -and $state.AcceptedUtf8Bytes -eq 8388608)
            }
            27 {
                $state = New-ControlFixture
                $state.AcceptedUtf8Bytes = [long]8388605
                $before = Get-ControlStreamSnapshot $state.Out
                # Two UTF-16 code units fit the remaining three bytes; their
                # four UTF-8 bytes do not. Reject the whole chunk.
                Add-HermesStartupText $state $state.Out (([string][char]0x00e9) * 2) $true
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388605 `
                    -and (Get-ControlStreamSnapshot $state.Out) -ceq $before)
            }
            28 {
                $state = New-ControlFixture
                $state.AcceptedUtf8Bytes = [long]8388603
                Add-HermesStartupText $state $state.Out ([string][char]0xd83d) $false
                $before = Get-ControlStreamSnapshot $state.Out
                Add-HermesStartupText $state $state.Out ([string][char]0xde00) $false
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388606 `
                    -and $state.Out.Count -eq 1 `
                    -and (Get-ControlStreamSnapshot $state.Out) -ceq $before)
            }
            29 {
                $state = New-ControlFixture
                $state.AcceptedUtf8Bytes = [long]8388604
                Add-HermesStartupText $state $state.Out 'left' $true
                $outBefore = Get-ControlStreamSnapshot $state.Out
                $errBefore = Get-ControlStreamSnapshot $state.Err
                $first = $state.Failure.ModuleSha256
                Add-HermesStartupText $state $state.Out "HERMES_CDB v=1 kind=0 pid=99`n" $true
                Add-HermesStartupText $state $state.Out ($failureLine + "`n") $true
                Add-HermesStartupText $state $state.Err 'do not hash or retain' $false
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and $state.Protocol -eq 0 -and $state.Unknown -eq 0 -and $state.Initial -eq 11 `
                    -and $state.Failure.ModuleSha256 -ceq $first `
                    -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                    -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore)
            }
            30 {
                $state = New-ControlFixture
                $state.AcceptedUtf8Bytes = [long]8388607
                $outReader = New-ControlReader 'xy'
                $errReader = New-ControlReader ''
                try {
                    Start-TraceRead $state.Out $outReader
                    Start-TraceRead $state.Err $errReader
                    $pending = $state.Err.Pending
                    $outBefore = Get-ControlStreamSnapshot $state.Out
                    $errBefore = Get-ControlStreamSnapshot $state.Err
                    Receive-TraceRead $state.Out $outReader $true
                    Receive-TraceRead $state.Err $errReader $false
                    Start-TraceRead $state.Out $outReader
                    Assert-Control ($state.Bounds -eq 1 -and $null -eq $state.Out.Pending `
                        -and [object]::ReferenceEquals($pending, $state.Err.Pending) `
                        -and -not $state.Out.End -and -not $state.Err.End `
                        -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                        -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore)
                } finally { $outReader.Dispose(); $errReader.Dispose() }
            }
            { $_ -in @(31, 32, 33) } {
                $state = New-ControlFixture
                $state.AcceptedUtf8Bytes = if ($case -eq 33) { [long]0 } else { [long]8388607 }
                $outText = if ($case -eq 31) { 'xy' } elseif ($case -eq 33) { "ok`n" } else { '' }
                $errText = if ($case -eq 32) { 'xy' } else { '' }
                $debugger = [pscustomobject]@{
                    StandardOutput = (New-ControlReader $outText)
                    StandardError = (New-ControlReader $errText)
                    ExitCode = 0
                }
                $script:controlStops = 0
                $script:controlReads = 0
                $script:controlArms = 0
                $script:controlWaits = 0
                $script:controlDisposed = 0
                $script:controlStopFault = $case -eq 32
                $script:controlAlreadyExited = $case -eq 33
                $debugger | Add-Member ScriptProperty HasExited {
                    $script:controlAlreadyExited -or $script:controlStops -gt 0
                }
                $debugger | Add-Member ScriptMethod Dispose { $script:controlDisposed++ }
                $started = $true
                $debuggerEnded = $false
                $drainsEnded = $false
                $rootCreated = $false
                $root = $PSScriptRoot
                $pins = @()
                if ($case -eq 32) {
                    $state.Error = 5
                    $state.Final.fault = 1460
                }
                Start-TraceRead $state.Out $debugger.StandardOutput
                Start-TraceRead $state.Err $debugger.StandardError
                . $controlDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($script:controlStops -eq 1 -and $script:controlWaits -eq 0 `
                    -and $script:controlDisposed -eq 1 -and $state.Timeout -eq 0)
                if ($case -eq 33) {
                    Assert-Control ($report.completed -eq 1 -and $report.cleanupComplete -eq 1 `
                        -and $script:controlReads -eq 2 -and $script:controlArms -eq 1 `
                        -and $state.Out.End -and $state.Err.End -and $drainsEnded `
                        -and $report.outChars -eq 3 -and $report.acceptedUtf8Bytes -eq 3)
                } else {
                    $emptyHash = [Convert]::ToHexString(
                        [Security.Cryptography.SHA256]::HashData([byte[]]::new(0))).ToLowerInvariant()
                    Assert-Control ($report.status -eq 7 -and $report.completed -eq 0 `
                        -and $report.cleanupComplete -eq 0 -and $report.cleanupFault -eq 13 `
                        -and -not $drainsEnded -and -not $state.Err.End `
                        -and $script:controlReads -eq 0 -and $script:controlArms -eq 0 `
                        -and $report.acceptedUtf8Bytes -eq 8388607 `
                        -and $report.outChars -eq 0 -and $report.errChars -eq 0 `
                        -and $report.outSha256 -ceq $emptyHash -and $report.errSha256 -ceq $emptyHash)
                    if ($case -eq 32) {
                        Assert-Control ($report.error -eq 5)
                        $state.Error = 0
                        $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                        Assert-Control ($report.status -eq 7 -and $report.error -eq 1460 `
                            -and $report.cleanupFault -eq 13)
                    } else {
                        Assert-Control ($report.error -eq 0 -and $debuggerEnded)
                    }
                }
            }
            { $_ -in @(34, 35, 36, 37) } {
                $state = New-ControlFixture
                $lastByte = $case -in @(34, 35)
                if ($lastByte) {
                    $padding = if ($case -eq 34) { $state.Err } else { $state.Out }
                    for ($index = 0; $index -lt 2047; $index++) {
                        Add-HermesStartupText $state $padding ('a' * 4096) $false
                    }
                    Add-HermesStartupText $state $padding ('a' * 4095) $false
                    Assert-Control ($state.AcceptedUtf8Bytes -eq 8388607 -and $state.Bounds -eq 0)
                } else {
                    # Defend an exhausted counter even when its flag is not set.
                    $state.AcceptedUtf8Bytes = [long]8388608
                }
                $state.Out.Tail = 'HERMES_CDB v=1 kind=0 pid=99'
                $outText = if ($case -eq 34) { "`n" } else { '' }
                $errText = if ($case -eq 35) { "`n" } else { '' }
                $debugger = [pscustomobject]@{
                    StandardOutput = [StartupControlIdleReader]::new($outText)
                    StandardError = [StartupControlIdleReader]::new($errText)
                    ExitCode = 0
                }
                $script:controlStops = 0
                $script:controlReads = 0
                $script:controlArms = 0
                $script:controlWaits = 0
                $script:controlDisposed = 0
                $script:controlStopFault = $false
                $script:controlExitPolls = 0
                $debugger | Add-Member ScriptProperty HasExited {
                    $script:controlExitPolls++
                    $script:controlStops -gt 0
                }
                $debugger | Add-Member ScriptMethod Dispose { $script:controlDisposed++ }
                $started = $true
                $debuggerEnded = $false
                $drainsEnded = $false
                $rootCreated = $false
                $root = $PSScriptRoot
                $pins = @()
                if ($case -eq 37) {
                    $state.Out.Pending = $debugger.StandardOutput.Idle.Task
                    $state.Err.Pending = $debugger.StandardError.Idle.Task
                } else {
                    Start-TraceRead $state.Out $debugger.StandardOutput
                    Start-TraceRead $state.Err $debugger.StandardError
                }
                . $controlDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $expectedReads = [int]$lastByte
                Assert-Control ($state.Bounds -eq 1 -and $report.status -eq 7 `
                    -and $report.acceptedUtf8Bytes -eq 8388608 -and $report.completed -eq 0 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                    -and $debugger.StandardOutput.Reads -eq $expectedReads `
                    -and $debugger.StandardError.Reads -eq $expectedReads `
                    -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                    -and $script:controlExitPolls -eq 1 `
                    -and $script:controlReads -eq 0 -and $script:controlArms -eq 0 `
                    -and $script:controlWaits -eq 0 -and $state.Timeout -eq 0 `
                    -and $debuggerEnded -and -not $drainsEnded `
                    -and -not $state.Out.End -and -not $state.Err.End `
                    -and $report.cleanupComplete -eq 0 -and $report.cleanupFault -eq 13 `
                    -and -not $debugger.StandardOutput.BaseStream.CanRead `
                    -and -not $debugger.StandardError.BaseStream.CanRead `
                    -and -not $debugger.StandardOutput.Idle.Task.IsCompleted `
                    -and -not $debugger.StandardError.Idle.Task.IsCompleted `
                    -and $state.Protocol -eq 0 -and $state.Initial -eq 11 `
                    -and $state.Out.Tail -ceq 'HERMES_CDB v=1 kind=0 pid=99')
                foreach ($name in @('Out', 'Err')) {
                    $text = if ($lastByte) {
                        if (($name -ceq 'Out') -eq ($case -eq 34)) { "`n" } else { 'a' * 8388607 }
                    } else { '' }
                    $expectedHash = [Convert]::ToHexString(
                        [Security.Cryptography.SHA256]::HashData(
                            [Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
                    Assert-Control ($state[$name].Count -eq $text.Length `
                        -and $state[$name].Prefix.Length -eq [Math]::Min(4096, $text.Length) `
                        -and $state[$name].Fingerprint -ceq $expectedHash)
                }
            }
        }
        $passed++
    } finally {
        if ($null -ne $state) {
            $state.Out.Hash.Dispose()
            $state.Err.Hash.Dispose()
        }
    }
}
if ($passed -ne $cases.Count) { throw 'Pure startup protocol control count mismatch.' }
Write-Output ("HERMES_STARTUP_CONTROLS v=1 total={0} passed={1} failed=0" -f $cases.Count, $passed)
