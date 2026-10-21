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

function New-ControlOpenBlock {
    param([int]$Current = 22, [int]$Kind = 1)
    $state = New-HermesStartupState
    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
    Add-HermesStartupLine $state "HERMES_CDB v=1 kind=$Kind pid=$Current"
    Update-HermesStartupRows $state ($targetRow + "`n")
    return $state
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
foreach ($name in @('Resolve-TraceExtension', 'Confirm-TraceTools',
        'Assert-TraceCommandPath', 'New-TraceCommands', 'New-TraceEventCommands',
        'Publish-TraceCommands',
        'Confirm-TracePublication',
        'Start-TraceRead', 'Receive-TraceRead', 'Invoke-TraceCollection')) {
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
Assert-Control ($driverTry.CatchClauses.Count -eq 1)
$controlPreflight = [scriptblock]::Create(
    'try { $null = Confirm-TraceTools $root; $null = Confirm-TracePublication $root } ' +
    $driverTry.CatchClauses[0].Extent.Text + ' finally ' + $driverTry.Finally.Extent.Text)
$toolBinding = @($driverTry.Body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] `
        -and $_.Left.Extent.Text -ceq '$extension'
})
$commandBinding = @($driverTry.Body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] `
        -and $_.Left.Extent.Text -ceq '$commandPath'
})
Assert-Control ($toolBinding.Count -eq 1 -and $commandBinding.Count -eq 1)
Assert-Control ($toolBinding[0].Extent.Text -ceq '$extension = Confirm-TraceTools $kits' `
    -and $commandBinding[0].Extent.Text -ceq '$commandPath = Publish-TraceCommands $extension')
# Extension-layout controls do not publish files. The real publication and
# launch-argument bindings are exercised below with retained memory pins.
$controlExtensionDriver = [scriptblock]::Create(
    'try { ' + $toolBinding[0].Extent.Text + '; $commands = New-TraceCommands $extension' +
    ' } ' + $driverTry.CatchClauses[0].Extent.Text + ' finally ' + $driverTry.Finally.Extent.Text)
$hashDefinition = @($driverAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] `
        -and $node.Name -ceq 'Get-TraceHash'
}, $true))
Assert-Control ($hashDefinition.Count -eq 1)
. ([scriptblock]::Create($hashDefinition[0].Extent.Text.Replace(
    'function Get-TraceHash', 'function Get-ControlCommandHash')))
$startBinding = @($driverTry.Body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] `
        -and $_.Left.Extent.Text -ceq '$start'
})
$launchBinding = @($driverTry.Body.Statements | Where-Object {
    $_ -is [Management.Automation.Language.AssignmentStatementAst] `
        -and $_.Left.Extent.Text -ceq '$debugger.StartInfo'
})
Assert-Control ($startBinding.Count -eq 1 -and $launchBinding.Count -eq 1)
$launchSetup = @($driverTry.Body.Statements | Where-Object {
    $_.Extent.StartOffset -ge $startBinding[0].Extent.StartOffset `
        -and $_.Extent.EndOffset -le $launchBinding[0].Extent.EndOffset
} | ForEach-Object { $_.Extent.Text }) -join "`n"
$controlCommandDriver = [scriptblock]::Create(
    'try { Set-HermesStartupTracePoint $state 24; ' + $commandBinding[0].Extent.Text +
    '; Set-HermesStartupTracePoint $state 25; ' + $launchSetup +
    ' } ' + $driverTry.CatchClauses[0].Extent.Text + ' finally ' + $driverTry.Finally.Extent.Text)

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

function Open-TracePin {
    param([string]$Path)
    if ($state.TracePoint -eq 24) {
        Assert-Control ($state.ToolRole -eq 0 -and $script:controlCommandFiles.ContainsKey($Path) `
            -and $script:controlCommandReadOnly[$Path])
        $script:controlCommandPins.Add($Path)
        $pin = [IO.MemoryStream]::new($script:controlCommandFiles[$Path], $false)
        $pins.Add($pin)
        return $pin
    }
    Assert-Control ($state.TracePoint -in @(6, 11, 12, 19))
    Assert-Control (($state.TracePoint -eq 6 -and $state.ToolRole -in 1..6) `
        -or ($state.TracePoint -ne 6 -and $state.ToolRole -eq 0))
    if ($case -eq 38 -and $state.ToolRole -eq 2) {
        throw 'C:\private-preflight S-1-5-21-123 CN=secret --command'
    }
    if ($state.TracePoint -eq 6 -and $state.ToolRole -eq 6) {
        $script:controlExtensionPins.Add($Path)
        if ($case -eq 61) { throw 'C:\private-preflight --command' }
    }
    $text = if ($state.TracePoint -eq 11) { $script:controlManifest } else { '' }
    $pin = [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($text))
    $pins.Add($pin)
    return $pin
}

function Get-AuthenticodeSignature {
    param([string]$LiteralPath)
    Assert-Control ($state.TracePoint -eq 7 -and $state.ToolRole -in 1..6)
    $script:controlToolRoles.Add($state.ToolRole)
    if ($state.ToolRole -eq 6) { $script:controlExtensionSignatures.Add($LiteralPath) }
    if (($case -eq 39 -and $state.ToolRole -eq 3) `
        -or ($case -eq 47 -and $state.ToolRole -eq 2)) {
        throw 'C:\private-preflight CN=secret --command'
    }
    [pscustomobject]@{
        Status = $(if (($case -eq 40 -and $state.ToolRole -eq 4) `
            -or ($case -eq 59 -and $state.ToolRole -eq 6)) {
            [Management.Automation.SignatureStatus]::NotSigned
        } else { [Management.Automation.SignatureStatus]::Valid })
        SignerCertificate = [pscustomobject]@{
            Subject = $(if (($case -eq 41 -and $state.ToolRole -eq 5) `
                -or ($case -eq 60 -and $state.ToolRole -eq 6)) {
                'CN=private-preflight'
            } else { 'CN=private-preflight, O=Microsoft Corporation, C=US' })
        }
    }
}

function Test-TraceExtensionFile {
    param([string]$Path)
    Assert-Control ($state.TracePoint -eq 6 -and $state.ToolRole -eq 6)
    $script:controlExtensionChecks.Add($Path)
    if ($case -eq 62 -and $Path -ceq (Join-Path $kits 'winext/exts.dll')) {
        throw 'C:\private-preflight --command'
    }
    return $script:controlExtensionFiles -ccontains $Path
}

function Get-TraceHash {
    param([IO.Stream]$Stream)
    if ($state.TracePoint -eq 24) {
        Assert-Control ($state.ToolRole -eq 0 -and $Stream.CanRead -and -not $Stream.CanWrite)
        $script:controlCommandHashes++
        return Get-ControlCommandHash $Stream
    }
    Assert-Control (($state.TracePoint -eq 10 -and $state.ToolRole -eq 1) `
        -or ($state.TracePoint -eq 20 -and $state.ToolRole -eq 0))
    if ($case -eq 42 -and $state.TracePoint -eq 10) {
        throw 'C:\private-preflight --command'
    }
    if ($state.TracePoint -eq 10) { return 'a' * 64 }
    return 'b' * 64
}

function Write-TraceCommandFile {
    param([string]$Path, [byte[]]$Bytes)
    Assert-Control ($state.TracePoint -eq 24 -and $state.ToolRole -eq 0 `
        -and $Path -cin @('startup-cpr.cdb', 'startup-epr.cdb', 'startup.cdb' |
            ForEach-Object { Join-Path $root $_ }) `
        -and -not $script:controlCommandFiles.ContainsKey($Path))
    $script:controlCommandWrites.Add($Path)
    $name = [IO.Path]::GetFileName($Path)
    if ($script:controlCommandFaultName -ceq $name -and $script:controlCommandFault -eq 1) { return }
    $copy = [byte[]]$Bytes.Clone()
    if ($script:controlCommandFaultName -ceq $name) {
        switch ($script:controlCommandFault) {
            2 { $copy[0] = $copy[0] -bxor 1 }
            3 { $copy = [byte[]]($copy + [byte]0) }
            4 { throw 'C:\private-command S-1-5-21-123 --command readonly failure' }
        }
    }
    $script:controlCommandFiles[$Path] = $copy
    $script:controlCommandReadOnly[$Path] = $true
}

function Get-HermesSetupPublishFiles {
    param([string]$Directory, [string]$InventoryPath, [switch]$AcceptanceHelpers)
    Assert-Control ($state.TracePoint -eq 17 -and $state.ToolRole -eq 0 `
        -and $AcceptanceHelpers)
    if ($case -eq 45) { throw 'C:\private-preflight --command' }
    foreach ($path in @('Hermes.Servicing.Acceptance.exe', 'one', 'two', 'three')) {
        [pscustomobject]@{
            path = $path
            sha256 = $(if ($case -eq 46) { 'c' * 64 } else { 'b' * 64 })
        }
    }
}

$cases = 1..110
$passed = 0
foreach ($case in $cases) {
    $state = $null
    $script:controlExtensionFiles = @(Join-Path $PSScriptRoot 'winxp/exts.dll')
    $script:controlExtensionChecks = [Collections.Generic.List[string]]::new()
    $script:controlExtensionPins = [Collections.Generic.List[string]]::new()
    $script:controlExtensionSignatures = [Collections.Generic.List[string]]::new()
    try {
        if ($case -ge 89) {
            $state = New-HermesStartupState
            $root = Join-Path $PSScriptRoot $(if ($case -eq 90) { 'command root' } else { 'command-root' })
            $kits = Join-Path $PSScriptRoot $(if ($case -eq 90) { 'selected sdk' } else { 'selected-sdk' })
            $extension = Join-Path $kits 'winxp/exts.dll'
            $helper = Join-Path $PSScriptRoot 'fixed-published-helper.exe'
            $symbols = Join-Path $root 'symbols'
            $script:controlCommandFiles = @{}
            $script:controlCommandReadOnly = @{}
            $script:controlCommandWrites = [Collections.Generic.List[string]]::new()
            $script:controlCommandPins = [Collections.Generic.List[string]]::new()
            $script:controlCommandHashes = 0
            $script:controlCommandFault = 0
            $script:controlCommandFaultName = ''
            $script:controlLaunchBindings = 0
            $script:controlStartInfo = $null
            $script:controlStops = 0
            $script:controlDisposed = 0
            $script:controlStopFault = $false
            $started = $false
            $debuggerEnded = $false
            $drainsEnded = $false
            $rootCreated = $false
            $pins = [Collections.Generic.List[IO.Stream]]::new()
            $debugger = [pscustomobject]@{}
            $debugger | Add-Member ScriptMethod Dispose { $script:controlDisposed++ }
            $debugger | Add-Member ScriptProperty StartInfo { $script:controlStartInfo } {
                param($value)
                Assert-Control ($pins.Count -eq 3 -and $script:controlCommandHashes -eq 3 `
                    -and $state.CommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
                    -and $state.CreateCommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
                    -and $state.ExitCommandSha256 -cmatch '\A[0-9a-f]{64}\z')
                foreach ($pin in $pins) {
                    Assert-Control ($pin.CanRead -and -not $pin.CanWrite -and $pin.Position -eq 0)
                }
                $script:controlLaunchBindings++
                $script:controlStartInfo = $value
            }
        }
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
            { $_ -in (38..47 + 49) } {
                $state = New-HermesStartupState
                $root = $PSScriptRoot
                $expectedHelper = Join-Path $root `
                    '.artifacts/servicing-acceptance/acceptance/Hermes.Servicing.Acceptance.exe'
                $script:controlManifest = if ($case -eq 43) {
                    '{"acceptanceExecutable":'
                } else {
                    @{ acceptanceExecutable = $(if ($case -eq 44) {
                        'C:\private-preflight'
                    } else { $expectedHelper }) } | ConvertTo-Json -Compress
                }
                $script:controlToolRoles = [Collections.Generic.List[int]]::new()
                $script:controlStops = 0
                $script:controlDisposed = 0
                $script:controlStopFault = $case -eq 47
                $debugger = [pscustomobject]@{}
                $debugger | Add-Member ScriptMethod Dispose { $script:controlDisposed++ }
                $started = $false
                $debuggerEnded = $false
                $drainsEnded = $false
                $rootCreated = $false
                $pins = [Collections.Generic.List[IO.Stream]]::new()
                if ($case -eq 47) { $state.Error = 5 }
                . $controlPreflight
                if ($case -eq 47) {
                    # Later diagnostics and cleanup must not replace first-fault origin.
                    Set-HermesStartupTracePoint $state 24
                    Set-HermesStartupTraceFault $state
                }
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $expectedPoint = switch ($case) {
                    38 { 6 }; 39 { 7 }; 40 { 8 }; 41 { 9 }; 42 { 10 }
                    43 { 15 }; 44 { 16 }; 45 { 17 }; 46 { 21 }; 47 { 7 }; 49 { 22 }
                }
                $expectedRole = switch ($case) {
                    38 { 2 }; 39 { 3 }; 40 { 4 }; 41 { 5 }; 42 { 1 }; 47 { 2 }
                    default { 0 }
                }
                Assert-Control ($report.tracePoint -eq $expectedPoint `
                    -and $report.toolRole -eq $expectedRole `
                    -and $report.tracePoint -is [int] -and $report.toolRole -is [int] `
                    -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                    -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and $report.cleanupComplete -eq 0 `
                    -and $report.acceptedUtf8Bytes -eq 0 `
                    -and $report.outChars -eq 0 -and $report.errChars -eq 0 `
                    -and $report.commandSha256 -ceq '')
                if ($case -eq 49) {
                    Assert-Control ($report.status -eq 3 -and $report.error -eq 0 `
                        -and -not $state.TraceFaulted -and $report.cleanupFault -eq 0 `
                        -and $report.debuggerSha256 -ceq ('a' * 64) `
                        -and $report.helperSha256 -ceq ('b' * 64) `
                        -and ($script:controlToolRoles -join ',') -ceq '1,2,3,4,5,6')
                } else {
                    $expectedError = if ($case -eq 47) { 5 } else { 13 }
                    $expectedCleanupFault = if ($case -eq 47) { 13 } else { 0 }
                    Assert-Control ($report.status -eq 8 -and $state.TraceFaulted `
                        -and $report.error -eq $expectedError `
                        -and $report.cleanupFault -eq $expectedCleanupFault `
                        -and $report.helperSha256 -ceq '')
                }
                $json = $report | ConvertTo-Json -Compress
                Assert-Control ($json -notmatch 'private-preflight|S-1-|CN=|--command|Microsoft|gflags|dbgeng')
            }
            48 {
                $state = New-ControlFixture
                Set-HermesStartupTracePoint $state 7 2
                foreach ($invalid in @(
                    @{ Point = -1; Role = 0 }, @{ Point = 27; Role = 0 },
                    @{ Point = '7'; Role = 2 }, @{ Point = 7.0; Role = 2 },
                    @{ Point = 7; Role = 'C:\private-preflight' },
                    @{ Point = 7; Role = -1 }, @{ Point = 7; Role = 7 },
                    @{ Point = 7; Role = 0 }, @{ Point = 11; Role = 2 },
                    @{ Point = 10; Role = 2 }
                )) {
                    $refused = $false
                    try { Set-HermesStartupTracePoint $state $invalid.Point $invalid.Role }
                    catch { $refused = $true }
                    Assert-Control ($refused -and $state.TracePoint -eq 7 -and $state.ToolRole -eq 2)
                }
                $state.TracePoint = 'C:\private-preflight --command'
                $state.ToolRole = @{ Subject = 'CN=secret S-1-5-21-123' }
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.tracePoint -eq 0 -and $report.toolRole -eq 0 `
                    -and $report.status -eq 8 -and $report.error -eq 13 `
                    -and $report.completed -eq 0 `
                    -and ($report | ConvertTo-Json -Compress) -notmatch 'private-preflight|CN=|S-1-|--command')
                Set-HermesStartupTracePoint $state 7 2
                Set-HermesStartupTraceFault $state
                $state.TraceFaultPoint = [long]::MaxValue
                $state.TraceFaultRole = '3'
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.tracePoint -eq 0 -and $report.toolRole -eq 0 `
                    -and $report.status -eq 8 -and $report.error -eq 13 -and $report.completed -eq 0)
            }
            { $_ -in 50..62 } {
                $state = New-HermesStartupState
                $root = $PSScriptRoot
                $kits = $PSScriptRoot
                $candidates = @('winxp/exts.dll', 'winext/exts.dll', 'exts.dll') |
                    ForEach-Object { Join-Path $kits $_ }
                $masks = @{
                    50 = 0; 51 = 1; 52 = 2; 53 = 4; 54 = 3; 55 = 5; 56 = 6
                    57 = 7; 58 = 0; 59 = 1; 60 = 1; 61 = 1; 62 = 1
                }
                $script:controlExtensionFiles = @(for ($index = 0; $index -lt 3; $index++) {
                    if (($masks[$case] -band (1 -shl $index)) -ne 0) { $candidates[$index] }
                })
                if ($case -eq 58) {
                    $script:controlExtensionFiles = @(
                        (Join-Path $kits 'other/exts.dll'), (Join-Path $kits 'winxp/unrelated.dll'))
                }
                $script:controlToolRoles = [Collections.Generic.List[int]]::new()
                $script:controlStops = 0
                $script:controlDisposed = 0
                $script:controlStopFault = $false
                $debugger = [pscustomobject]@{}
                $debugger | Add-Member ScriptMethod Dispose { $script:controlDisposed++ }
                $started = $false
                $debuggerEnded = $false
                $drainsEnded = $false
                $rootCreated = $false
                $pins = [Collections.Generic.List[IO.Stream]]::new()
                $extension = $null
                $commands = $null
                . $controlExtensionDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $checkCount = if ($case -eq 62) { 2 } else { 3 }
                Assert-Control ($script:controlExtensionChecks.Count -eq $checkCount `
                    -and ($script:controlExtensionChecks -join '|') `
                        -ceq ($candidates[0..($checkCount - 1)] -join '|') `
                    -and $report.debuggerSha256 -ceq ('a' * 64) `
                    -and $report.helperSha256 -ceq '' -and $report.commandSha256 -ceq '' `
                    -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                    -and $report.cleanupFault -eq 0 -and $report.toolRole -eq 6 `
                    -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and $report.acceptedUtf8Bytes -eq 0)
                if ($case -in 51..53) {
                    $expectedPath = $candidates[$case - 51]
                    Assert-Control ($extension -ceq $expectedPath `
                        -and $script:controlExtensionPins.Count -eq 1 `
                        -and $script:controlExtensionSignatures.Count -eq 1 `
                        -and $script:controlExtensionPins[0] -ceq $expectedPath `
                        -and $script:controlExtensionSignatures[0] -ceq $expectedPath `
                        -and ($commands -split "`n")[0] -ceq ('.load "' + $expectedPath + '"') `
                        -and @($commands -split "`n" | Where-Object { $_ -like '.load *' }).Count -eq 1 `
                        -and $report.tracePoint -eq 9 -and $report.status -eq 3 `
                        -and $report.error -eq 0 -and -not $state.TraceFaulted)
                } else {
                    $expectedPoint = if ($case -eq 59) { 8 } elseif ($case -eq 60) { 9 } else { 6 }
                    $pinCount = [int]($case -in 59..61)
                    $signatureCount = [int]($case -in 59..60)
                    Assert-Control ($null -eq $extension -and $null -eq $commands `
                        -and $script:controlExtensionPins.Count -eq $pinCount `
                        -and $script:controlExtensionSignatures.Count -eq $signatureCount `
                        -and $report.tracePoint -eq $expectedPoint -and $report.status -eq 8 `
                        -and $report.error -eq 13 -and $state.TraceFaulted)
                    if ($pinCount -eq 1) {
                        Assert-Control ($script:controlExtensionPins[0] -ceq $candidates[0])
                    }
                    if ($signatureCount -eq 1) {
                        Assert-Control ($script:controlExtensionSignatures[0] -ceq $candidates[0])
                    }
                }
                Assert-Control (($report | ConvertTo-Json -Compress) `
                    -notmatch 'private-preflight|CN=|S-1-|--command|exts\.dll|\.load')
            }
            63 {
                $state = New-HermesStartupState
                $report = New-HermesStartupReport $state $false $false
                Assert-Control ($report.observations.Count -eq 33)
                foreach ($value in $report.observations.Values) {
                    Assert-Control ($value -is [int] -and $value -eq 0)
                }
                Assert-Control ($report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.originalGatePassed -eq 0)
            }
            64 {
                $state = New-ControlFixture
                $report = New-HermesStartupReport $state $true $true
                $observed = $report.observations
                Assert-Control ($observed.Marker0 -eq 1 -and $observed.Marker1 -eq 1 `
                    -and $observed.Marker2 -eq 1 -and $observed.Marker3 -eq 2 `
                    -and $observed.Marker4 -eq 2 -and $observed.MarkerMalformed -eq 0 `
                    -and $observed.MarkerEcho -eq 0 -and $observed.LastEventSeen -eq 3 `
                    -and $observed.CreateEventSeen -eq 1 -and $observed.CreateEventAccepted -eq 1 `
                    -and $observed.ExitEventSeen -eq 2 -and $observed.ExitEventAccepted -eq 2 `
                    -and $observed.LastEventUnrecognized -eq 0 `
                    -and $observed.FlagsSeen -eq 1 -and $observed.FlagsAccepted -eq 1 `
                    -and $observed.ArmPresent -eq 1 -and $observed.ArmKind -eq 1 `
                    -and $observed.ArmCurrentMatchesTarget -eq 1 `
                    -and $observed.ArmEventMatchesTarget -eq 1 -and $observed.ArmFlag2 -eq 1 `
                    -and $observed.BlockPresent -eq 0 -and $observed.BlockKind -eq 0 `
                    -and $observed.TargetRowPresent -eq 1 -and $observed.FinalRowPresent -eq 1 `
                    -and $observed.ParserUnknown -eq 0 -and $observed.ParserProtocol -eq 0 `
                    -and $report.completed -eq 1 -and $report.armingVerified -eq 1)
            }
            65 {
                $state = New-HermesStartupState
                Add-HermesStartupLine $state '0:000> .printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Add-HermesStartupLine $state 'ModLoad: 0000000012340000 0000000012350000 C:\private\child.dll'
                $timedFinal = $finalRow.Replace('exit=3221225794', 'exit=0').Replace(
                    'cleanup=1 fault=0 cleanupFault=0', 'cleanup=0 fault=1460 cleanupFault=5')
                Update-HermesStartupRows $state ($targetRow + "`n" + $timedFinal + "`n")
                Set-HermesStartupTracePoint $state 26
                Set-HermesStartupTraceFault $state
                $report = New-HermesStartupReport $state $true $true
                $observed = $report.observations
                Assert-Control ($observed.Marker0 -eq 1 -and $observed.MarkerEcho -eq 1 `
                    -and $observed.Marker1 -eq 0 -and $observed.Marker2 -eq 0 `
                    -and $observed.LastEventSeen -eq 0 -and $observed.FlagsSeen -eq 0 `
                    -and $observed.ArmPresent -eq 0 -and $observed.BlockPresent -eq 0 `
                    -and $observed.TargetRowPresent -eq 1 -and $observed.FinalRowPresent -eq 1 `
                    -and $observed.ParserProtocol -eq 0 -and $observed.ParserUnknown -eq 0 `
                    -and $report.helperMatched -eq 1 -and $report.targetMatched -eq 0 `
                    -and $report.status -eq 5 -and $report.error -eq 13 `
                    -and $report.cleanupFault -eq 5 -and $report.cleanupComplete -eq 0 `
                    -and $report.tracePoint -eq 26 -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0)
            }
            66 {
                $state = New-ControlOpenBlock -Current 23
                Add-HermesStartupLine $state 'Last event: 17.1: Create process 1:17'
                Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=23'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.ArmPresent -eq 1 -and $observed.ArmKind -eq 1 `
                    -and $observed.ArmCurrentMatchesTarget -eq 0 `
                    -and $observed.ArmEventMatchesTarget -eq 0 -and $observed.ArmFlag2 -eq 1 `
                    -and $observed.CreateEventAccepted -eq 1 -and $observed.ParserProtocol -eq 0 `
                    -and $observed.ParserUnknown -eq 0 -and -not (Test-HermesStartupArm $state) `
                    -and $report.armingVerified -eq 0 -and $report.completed -eq 0)
            }
            67 {
                $state = New-ControlOpenBlock
                Add-HermesStartupLine $state 'Last event: 17.1: Create process 1:17'
                Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=22'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.Marker1 -eq 1 -and $observed.Marker2 -eq 1 `
                    -and $observed.CreateEventAccepted -eq 1 -and $observed.ArmPresent -eq 0 `
                    -and $observed.BlockPresent -eq 0 -and $observed.ParserProtocol -eq 1 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.completed -eq 0)
            }
            68 {
                $state = New-ControlOpenBlock -Current 23
                Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000000'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.BlockPresent -eq 1 -and $observed.BlockKind -eq 1 `
                    -and $observed.BlockCurrentMatchesTarget -eq 0 `
                    -and $observed.BlockEventMatchesTarget -eq 1 -and $observed.BlockFlag2 -eq 0 `
                    -and $observed.FlagsSeen -eq 1 -and $observed.FlagsAccepted -eq 1 `
                    -and $observed.ArmPresent -eq 0 -and $observed.ParserProtocol -eq 0 `
                    -and $report.status -eq 4 -and $report.completed -eq 0)
            }
            69 {
                $state = New-ControlOpenBlock
                Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16 C:\private\secret S-1-5-21-123 --command'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.LastEventSeen -eq 1 -and $observed.CreateEventSeen -eq 1 `
                    -and $observed.CreateEventAccepted -eq 0 -and $observed.CreateEventUnrecognized -eq 1 `
                    -and $observed.LastEventUnrecognized -eq 1 -and $observed.ParserUnknown -eq 1 `
                    -and $observed.BlockEventMatchesTarget -eq 0 -and $report.completed -eq 0 `
                    -and $report.firstFailureKind -eq 0 `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) `
                        -notmatch 'private|secret|S-1-|--command|16\.1')
            }
            70 {
                $state = New-ControlOpenBlock
                Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.LastEventSeen -eq 2 -and $observed.CreateEventSeen -eq 2 `
                    -and $observed.CreateEventAccepted -eq 1 -and $observed.CreateEventUnrecognized -eq 1 `
                    -and $observed.LastEventUnrecognized -eq 1 -and $observed.ParserProtocol -eq 1 `
                    -and $observed.ParserUnknown -eq 0 -and $report.completed -eq 0)
            }
            71 {
                $state = New-ControlOpenBlock
                Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:17'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.CreateEventSeen -eq 1 -and $observed.CreateEventAccepted -eq 0 `
                    -and $observed.CreateEventUnrecognized -eq 1 -and $observed.ParserUnknown -eq 1 `
                    -and $observed.BlockEventMatchesTarget -eq 0 -and $report.armingVerified -eq 0)
            }
            72 {
                $state = New-ControlOpenBlock -Kind 3
                Add-HermesStartupLine $state 'Last event: 16.1: Exit process 1:16, code c0000142'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.BlockPresent -eq 1 -and $observed.BlockKind -eq 3 `
                    -and $observed.BlockCurrentMatchesTarget -eq 1 `
                    -and $observed.BlockEventMatchesTarget -eq 1 -and $observed.BlockFlag2 -eq 0 `
                    -and $observed.ExitEventSeen -eq 1 -and $observed.ExitEventAccepted -eq 1 `
                    -and $observed.ExitEventUnrecognized -eq 0 -and $report.exitObserved -eq 0)
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=4 pid=22'
                $report = New-HermesStartupReport $state $false $false
                Assert-Control ($report.exitObserved -eq 1 -and $report.exitCode -eq 3221225794 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.completed -eq 0 -and $report.observations.BlockPresent -eq 0)
            }
            73 {
                $state = New-ControlOpenBlock -Kind 3
                Add-HermesStartupLine $state 'Last event: 16.1: Exit process 1:16, code unknown'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.LastEventSeen -eq 1 -and $observed.ExitEventSeen -eq 1 `
                    -and $observed.ExitEventAccepted -eq 0 -and $observed.ExitEventUnrecognized -eq 1 `
                    -and $observed.LastEventUnrecognized -eq 1 -and $observed.ParserUnknown -eq 1 `
                    -and $report.exitObserved -eq 0 -and $report.completed -eq 0)
            }
            { $_ -in @(74, 75, 79) } {
                $state = New-ControlOpenBlock
                Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                if ($case -eq 75) {
                    Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0xunknown C:\private\secret'
                } elseif ($case -eq 79) {
                    Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000001'
                }
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=22'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.FlagsSeen -eq [int]($case -ne 74) `
                    -and $observed.FlagsAccepted -eq [int]($case -eq 79) `
                    -and $observed.FlagsUnrecognized -eq [int]($case -eq 75) `
                    -and $observed.ArmPresent -eq 1 -and $observed.ArmFlag2 -eq 0 `
                    -and $observed.ParserUnknown -eq 1 -and $observed.ParserProtocol -eq 0 `
                    -and $report.armingVerified -eq 0 -and -not (Test-HermesStartupArm $state) `
                    -and $report.firstFailureKind -eq 0 -and $report.completed -eq 0 `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) -notmatch 'private|secret|0xunknown')
            }
            76 {
                $state = New-HermesStartupState
                Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000002'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.FlagsSeen -eq 1 -and $observed.FlagsAccepted -eq 0 `
                    -and $observed.FlagsUnrecognized -eq 1 -and $observed.ArmFlag2 -eq 0 `
                    -and $observed.BlockFlag2 -eq 0 -and $observed.ParserUnknown -eq 0 `
                    -and $observed.ParserProtocol -eq 0 -and $report.completed -eq 0)
            }
            77 {
                $state = New-HermesStartupState
                foreach ($line in @(
                    'HERMES_CDB v=2 kind=1 pid=22',
                    '0:000> HERMES_CDB v=1 kind=1 pid=22',
                    'HERMES_CDB v=1 kind=9 pid=22 C:\private\secret S-1-5-21-123 --command',
                    '0:000> .printf "HERMES_CDB v=1 kind=1 pid=%u\n", @$tpid'
                )) { Add-HermesStartupLine $state $line }
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.MarkerMalformed -eq 3 -and $observed.MarkerMalformedKind -eq 1 `
                    -and $observed.MarkerEcho -eq 1 -and $observed.Marker1 -eq 0 `
                    -and $observed.ParserProtocol -eq 1 -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) `
                        -notmatch 'private|secret|S-1-|--command|printf|tpid')
            }
            78 {
                $state = New-HermesStartupState
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=1 pid=4294967296'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=1 pid=0'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.Marker1 -eq 1 -and $observed.MarkerMalformed -eq 2 `
                    -and $observed.MarkerMalformedKind -eq 2 -and $observed.ParserProtocol -eq 1 `
                    -and $observed.ArmPresent -eq 0 -and $observed.BlockPresent -eq 0 `
                    -and $report.completed -eq 0 -and $report.armingVerified -eq 0)
            }
            80 {
                $state = New-HermesStartupState
                $text = "0:000> .printf `"HERMES_CDB v=1 kind=0 pid=%u\n`", @`$tpid`r`n" +
                    "HERMES_CDB v=1 kind=0 pid=11`r`nHERMES_CDB v=1 kind=1 pid=22`r`n" +
                    "Last event: 16.1: Create process 1:16`r`n" +
                    "Current NtGlobalFlag contents: 0x00000002`r`nHERMES_CDB v=1 kind=2 pid=22`r`n"
                for ($index = 0; $index -lt $text.Length; $index += 7) {
                    Add-HermesStartupText $state $state.Out (
                        $text.Substring($index, [Math]::Min(7, $text.Length - $index))) $true
                }
                Add-HermesStartupText $state $state.Err (
                    "HERMES_CDB v=1 kind=3 pid=22`nLast event: 16.1: Exit process 1:16, code 0`n" +
                    "Syntax error C:\private\secret S-1-5-21-123 --command`n") $false
                Update-HermesStartupRows $state ($targetRow + "`n")
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.MarkerEcho -eq 1 -and $observed.Marker0 -eq 1 `
                    -and $observed.Marker1 -eq 1 -and $observed.Marker2 -eq 1 `
                    -and $observed.Marker3 -eq 0 -and $observed.ExitEventSeen -eq 0 `
                    -and $observed.CreateEventAccepted -eq 1 -and $observed.FlagsAccepted -eq 1 `
                    -and $observed.ArmCurrentMatchesTarget -eq 1 -and (Test-HermesStartupArm $state) `
                    -and -not $state.Acked -and $report.armingVerified -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.completed -eq 0 `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) `
                        -notmatch 'private|secret|S-1-|--command|Syntax error|printf|tpid')
            }
            81 {
                $state = New-ControlOpenBlock
                foreach ($name in @('Marker0', 'FlagsSeen', 'LastEventSeen', 'CreateEventSeen')) {
                    $state.Observations[$name] = [int]8388607
                }
                foreach ($iteration in 1..3) {
                    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                    Add-HermesStartupLine $state 'Current NtGlobalFlag contents: unknown'
                    Add-HermesStartupLine $state 'Last event: unknown Create process'
                }
                $report = New-HermesStartupReport $state $false $false
                foreach ($name in @('Marker0', 'FlagsSeen', 'LastEventSeen', 'CreateEventSeen')) {
                    Assert-Control ($report.observations[$name] -is [int] `
                        -and $report.observations[$name] -eq 8388608)
                }
                foreach ($value in $report.observations.Values) {
                    Assert-Control ($value -is [int] -and $value -ge 0 -and $value -le 8388608)
                }
                Assert-Control ($report.completed -eq 0 -and $report.armingVerified -eq 0)
            }
            82 {
                $state = New-HermesStartupState
                $state.Protocol = 1
                $state.Unknown = 1
                $badValues = @('C:\private\secret S-1-5-21-123 --command', [long]::MaxValue,
                    -1, 1.5, $true, @{ message = 'CN=secret' }, [int[]]@(1, 2), $null)
                $names = @($state.Observations.Keys)
                for ($index = 0; $index -lt $names.Count; $index++) {
                    $state.Observations[$names[$index]] = $badValues[$index % $badValues.Count]
                }
                $state.Observations.MarkerMalformedKind = 3
                $report = New-HermesStartupReport $state $false $false
                foreach ($name in $names) {
                    Assert-Control ($report.observations[$name] -is [int] `
                        -and $report.observations[$name] -eq 0)
                }
                Assert-Control ($report.observations.ParserUnknown -eq 1 `
                    -and $report.observations.ParserProtocol -eq 1 -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) `
                        -notmatch 'private|secret|S-1-|--command|CN=')
            }
            83 {
                $state = New-ControlOpenBlock
                $before = (New-HermesStartupReport $state $false $false).observations |
                    ConvertTo-Json -Compress
                $state.AcceptedUtf8Bytes = [long]8388607
                $state.Out.Tail = 'HERMES_CDB v=1 kind=2 pid=22'
                Add-HermesStartupText $state $state.Out "`n" $true
                Add-HermesStartupText $state $state.Out "HERMES_CDB v=1 kind=3 pid=22`n" $true
                Add-HermesStartupText $state $state.Err "Current NtGlobalFlag contents: 0x00000002`n" $false
                $report = New-HermesStartupReport $state $false $false
                Assert-Control ($state.Bounds -eq 1 -and $report.acceptedUtf8Bytes -eq 8388608 `
                    -and $report.status -eq 7 -and $report.completed -eq 0 `
                    -and ($report.observations | ConvertTo-Json -Compress) -ceq $before)
            }
            84 {
                $state = New-ControlFixture
                foreach ($name in @($state.Observations.Keys)) {
                    $state.Observations[$name] = [int]8388608
                }
                $state.Observations.MarkerMalformedKind = 2
                foreach ($name in @('DebuggerSha256', 'HelperSha256', 'CommandSha256',
                        'CreateCommandSha256', 'ExitCommandSha256')) {
                    $state[$name] = 'a' * 64
                }
                foreach ($name in @('Out', 'Err')) {
                    $state[$name].Fingerprint = 'b' * 64
                    $state[$name].Count = [long]8388608
                    $state[$name].Prefix = 'x' * 4096
                }
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'xxxxxxxx|sample\.dll' `
                    -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0)
                foreach ($value in $report.observations.Values) {
                    Assert-Control ($value -is [int] -and $value -ge 0 -and $value -le 8388608)
                }
            }
            85 {
                $state = New-ControlOpenBlock
                Add-HermesStartupLine $state 'Last event: 16.1: Exit process 1:16, code c0000142'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.LastEventSeen -eq 1 -and $observed.LastEventUnrecognized -eq 1 `
                    -and $observed.ExitEventSeen -eq 1 -and $observed.ExitEventAccepted -eq 0 `
                    -and $observed.ExitEventUnrecognized -eq 1 -and $observed.CreateEventSeen -eq 0 `
                    -and $observed.ParserUnknown -eq 1 -and $report.exitObserved -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.completed -eq 0)
            }
            86 {
                $state = New-ControlOpenBlock
                Add-HermesStartupLine $state 'Last event: 16.1: Load module C:\private\secret.dll at 0000000012345678'
                Add-HermesStartupLine $state 'ModLoad: 0000000012340000 0000000012350000 C:\private\secret.dll'
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.LastEventSeen -eq 1 -and $observed.LastEventUnrecognized -eq 1 `
                    -and $observed.CreateEventSeen -eq 0 -and $observed.ExitEventSeen -eq 0 `
                    -and $observed.ParserUnknown -eq 1 -and $observed.ArmPresent -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.completed -eq 0 `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) -notmatch 'private|secret|12345678')
            }
            { $_ -in @(87, 88) } {
                $state = New-ControlOpenBlock
                $eventLine = if ($case -eq 87) {
                    '1:001> Last event: 16.1: Create process 1:16'
                } else { 'last event: 16.1: create process 1:16' }
                $flagsLine = if ($case -eq 87) {
                    '1:001> Current NtGlobalFlag contents: 0x00000002'
                } else { 'current ntglobalflag contents: 0x00000002' }
                Add-HermesStartupLine $state $eventLine
                Add-HermesStartupLine $state $flagsLine
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.LastEventSeen -eq 1 -and $observed.LastEventUnrecognized -eq 1 `
                    -and $observed.CreateEventSeen -eq 1 -and $observed.CreateEventAccepted -eq 0 `
                    -and $observed.CreateEventUnrecognized -eq 1 `
                    -and $observed.FlagsSeen -eq 1 -and $observed.FlagsAccepted -eq 0 `
                    -and $observed.FlagsUnrecognized -eq 1 -and $observed.BlockFlag2 -eq 0 `
                    -and $observed.BlockEventMatchesTarget -eq 0 -and $observed.ParserUnknown -eq 0 `
                    -and $observed.ParserProtocol -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.completed -eq 0)
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=22'
                $report = New-HermesStartupReport $state $false $false
                Assert-Control ($report.observations.ParserProtocol -eq 1 `
                    -and $report.observations.ArmPresent -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.completed -eq 0)
            }
            { $_ -in @(89, 90) } {
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $sources = New-TraceEventCommands
                $sources['startup.cdb'] = New-TraceCommands $extension
                $hashFields = @{
                    'startup.cdb' = 'commandSha256'
                    'startup-cpr.cdb' = 'createCommandSha256'
                    'startup-epr.cdb' = 'exitCommandSha256'
                }
                Assert-Control ($script:controlCommandWrites.Count -eq 3 `
                    -and $script:controlCommandPins.Count -eq 3 `
                    -and $script:controlCommandHashes -eq 3 -and $script:controlLaunchBindings -eq 1 `
                    -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                    -and $report.tracePoint -eq 25 -and $report.toolRole -eq 0 `
                    -and $report.error -eq 0 -and $report.cleanupFault -eq 0 `
                    -and -not $state.TraceFaulted -and $report.completed -eq 0 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                foreach ($source in $sources.GetEnumerator()) {
                    $path = Join-Path $root $source.Key
                    $bytes = [Text.Encoding]::ASCII.GetBytes($source.Value)
                    $expected = [Convert]::ToHexString(
                        [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                    Assert-Control ($script:controlCommandWrites -ccontains $path `
                        -and $script:controlCommandPins -ccontains $path `
                        -and $script:controlCommandReadOnly[$path] `
                        -and [Text.Encoding]::ASCII.GetString($script:controlCommandFiles[$path]) `
                            -ceq $source.Value -and $report[$hashFields[$source.Key]] -ceq $expected)
                }
                $arguments = $script:controlStartInfo.ArgumentList
                Assert-Control ($arguments.Count -eq 7 -and $arguments[0] -ceq '-o' `
                    -and $arguments[1] -ceq '-cf' -and $arguments[2] -ceq (Join-Path $root 'startup.cdb') `
                    -and $arguments[3] -ceq '-y' -and $arguments[5] -ceq $helper `
                    -and $arguments[6] -ceq 'startup-debug-probe' `
                    -and $script:controlStartInfo.FileName -ceq (Join-Path $kits 'cdb.exe') `
                    -and $script:controlStartInfo.WorkingDirectory -ceq $kits `
                    -and -not $script:controlStartInfo.UseShellExecute `
                    -and $arguments -cnotcontains '-g' -and $arguments -cnotcontains '-G')
                $lines = @($sources['startup.cdb'] -split "`n")
                Assert-Control ($lines.Count -eq 7 -and $lines[0] -ceq ('.load "' + $extension + '"') `
                    -and $lines[1] -ceq '.printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid' `
                    -and $lines[2] -ceq 'sxd ibp' -and $lines[5] -ceq 'g' -and $lines[6] -ceq '')
                foreach ($line in $lines[3..4]) {
                    Assert-Control ($line -cmatch '\Asx- -c "\$\$><[^";\r\n]+" (?:cpr|epr)\z' `
                        -and @($line.ToCharArray() | Where-Object { $_ -eq '"' }).Count -eq 2 `
                        -and $line -notmatch '\.printf|\.lastevent|!gflag|\\"')
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'command-root|command root|selected.sdk|fixed-published|startup-cpr|startup-epr|\$\$><|HERMES_CDB')
            }
            91 {
                $sources = New-TraceEventCommands
                $expectedCreate = @(
                    '.printf "HERMES_CDB v=1 kind=1 pid=%u\n", @$tpid',
                    '.lastevent', '|.', '!gflag +sls', '!gflag',
                    '.printf "HERMES_CDB v=1 kind=2 pid=%u\n", @$tpid', 'g'
                )
                $expectedExit = @(
                    '.printf "HERMES_CDB v=1 kind=3 pid=%u\n", @$tpid', '.lastevent',
                    '.printf "HERMES_CDB v=1 kind=4 pid=%u\n", @$tpid', 'g'
                )
                Assert-Control ($sources.Count -eq 2 `
                    -and $sources['startup-cpr.cdb'] -ceq (($expectedCreate -join "`r`n") + "`r`n") `
                    -and $sources['startup-epr.cdb'] -ceq (($expectedExit -join "`r`n") + "`r`n"))
                foreach ($text in $sources.Values) {
                    Assert-Control (-not $text.Contains('\"', [StringComparison]::Ordinal) `
                        -and $text -notmatch '(?<!\r)\n|sx-|sxe|sxd|\.load|\$\$><')
                }
            }
            { $_ -in 92..100 } {
                $script:controlCommandFault = [int][Math]::Floor(($case - 92) / 3) + 1
                $sourceIndex = ($case - 92) % 3
                $script:controlCommandFaultName = @('startup-cpr.cdb', 'startup-epr.cdb', 'startup.cdb')[$sourceIndex]
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $expectedPins = $sourceIndex + [int]($script:controlCommandFault -ne 1)
                $expectedHashes = $sourceIndex + [int]($script:controlCommandFault -eq 2)
                Assert-Control ($script:controlCommandWrites.Count -eq 3 `
                    -and $script:controlCommandPins.Count -eq $expectedPins `
                    -and $script:controlCommandHashes -eq $expectedHashes `
                    -and $script:controlLaunchBindings -eq 0 -and $null -eq $script:controlStartInfo `
                    -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                    -and $state.TraceFaulted -and $report.tracePoint -eq 24 -and $report.toolRole -eq 0 `
                    -and $report.status -eq 8 -and $report.error -eq 13 -and $report.cleanupFault -eq 0 `
                    -and $report.commandSha256 -ceq '' -and $report.createCommandSha256 -ceq '' `
                    -and $report.exitCommandSha256 -ceq '' -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0)
                Assert-Control (($report | ConvertTo-Json -Depth 4 -Compress) `
                    -notmatch 'command-root|selected-sdk|fixed-published|startup-cpr|startup-epr|HERMES_CDB')
            }
            101 {
                $script:controlCommandFault = 4
                $script:controlCommandFaultName = 'startup-epr.cdb'
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($script:controlCommandWrites.Count -eq 2 `
                    -and $script:controlCommandPins.Count -eq 0 -and $script:controlCommandHashes -eq 0 `
                    -and $script:controlLaunchBindings -eq 0 -and $report.tracePoint -eq 24 `
                    -and $report.status -eq 8 -and $report.error -eq 13 -and $report.completed -eq 0 `
                    -and $report.commandSha256 -ceq '' -and $report.createCommandSha256 -ceq '' `
                    -and $report.exitCommandSha256 -ceq '' `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) `
                        -notmatch 'private-command|S-1-|--command|readonly failure')
            }
            { $_ -in (102..107 + 110) } {
                switch ($case) {
                    102 { $root += '"' }
                    103 { $root += "`r`n`t" + [char]0 + [char]127 }
                    104 { $root += ';.echo injected' }
                    105 { $root += [char]0x00e9 }
                    106 { $root = Join-Path $root '../other' }
                    107 { $root = 'relative-command-root' }
                    110 { $extension += "`"`r`n;.echo injected" }
                }
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($script:controlCommandWrites.Count -eq 0 `
                    -and $script:controlCommandPins.Count -eq 0 -and $script:controlCommandHashes -eq 0 `
                    -and $script:controlLaunchBindings -eq 0 -and $null -eq $script:controlStartInfo `
                    -and $state.TraceFaulted -and $report.tracePoint -eq 24 -and $report.status -eq 8 `
                    -and $report.error -eq 13 -and $report.completed -eq 0 `
                    -and $report.commandSha256 -ceq '' -and $report.createCommandSha256 -ceq '' `
                    -and $report.exitCommandSha256 -ceq '' `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) `
                        -notmatch 'injected|command-root|selected-sdk|fixed-published')
            }
            108 {
                Assert-Control ($driverAst.ParamBlock.Parameters.Count -eq 0)
                foreach ($name in @('New-TraceCommands', 'Publish-TraceCommands')) {
                    $definitions = @($driverAst.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
                            -and $node.Name -ceq $name
                    }, $true))
                    Assert-Control ($definitions.Count -eq 1 `
                        -and $definitions[0].Body.ParamBlock.Parameters.Count -eq 1 `
                        -and $definitions[0].Body.ParamBlock.Parameters[0].Name.VariablePath.UserPath -ceq 'Extension')
                }
                $writer = @($driverAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] `
                        -and $node.Name -ceq 'Write-TraceCommandFile'
                }, $true))
                $pinDefinition = @($driverAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] `
                        -and $node.Name -ceq 'Open-TracePin'
                }, $true))
                Assert-Control ($writer.Count -eq 1 -and $pinDefinition.Count -eq 1 `
                    -and $writer[0].Extent.Text.Contains('[IO.FileMode]::CreateNew') `
                    -and $writer[0].Extent.Text.Contains('[IO.FileShare]::None') `
                    -and $writer[0].Extent.Text.Contains('[IO.FileAttributes]::ReadOnly') `
                    -and $writer[0].Extent.Text.Contains('$stream.Flush($true)') `
                    -and $pinDefinition[0].Extent.Text.Contains('Assert-TracePath $Path') `
                    -and $pinDefinition[0].Extent.Text.Contains('[IO.FileAccess]::Read, [IO.FileShare]::Read') `
                    -and $pinDefinition[0].Extent.Text.Contains('$pins.Add($pin)'))
                Assert-Control ($commandBinding[0].Extent.EndOffset -lt $launchBinding[0].Extent.StartOffset `
                    -and $launchBinding[0].Extent.EndOffset -lt $entries[0].Extent.StartOffset)
            }
            109 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture -BeforeExitLines @(
                    '1:001> $$><C:\private\startup-cpr.cdb',
                    '1:001> .printf "HERMES_CDB v=1 kind=3 pid=%u\n", @$tpid',
                    '1:001> $$><C:\private\startup-epr.cdb',
                    '1:001> .printf "HERMES_CDB v=1 kind=4 pid=%u\n", @$tpid'
                )
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.MarkerEcho -eq 2 `
                    -and $report.observations.MarkerMalformed -eq 0 `
                    -and $report.observations.ParserProtocol -eq 0 `
                    -and $report.observations.ParserUnknown -eq 0 `
                    -and $report.armingVerified -eq 1 -and $report.completed -eq 1 `
                    -and $report.firstFailureKind -eq 1 `
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) -notmatch 'private|\$\$><|printf')
            }
        }
        $passed++
    } finally {
        if ($case -ge 89) {
            foreach ($pin in $pins) { $pin.Dispose() }
        }
        if ($null -ne $state) {
            $state.Out.Hash.Dispose()
            $state.Err.Hash.Dispose()
        }
    }
}
if ($passed -ne $cases.Count) { throw 'Pure startup protocol control count mismatch.' }
Write-Output ("HERMES_STARTUP_CONTROLS v=1 total={0} passed={1} failed=0" -f $cases.Count, $passed)
