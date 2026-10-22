#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet(0, 1024, 2048, 4096)][int]$PipeFixture = 0,
    [ValidatePattern('\A(?:[0-9a-f]{32})?\z')][string]$PipeScope = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PipeFixture -ne 0) {
    if ($PipeScope.Length -ne 32) { throw 'Fixed pipe fixture scope required.' }
    $root = Join-Path $PSScriptRoot ('obj/startup-pipe-controls/' + $PipeScope)
    if (-not [IO.Directory]::Exists($root)) { throw 'Owned fixture root required.' }
    $file = [IO.File]::Open((Join-Path $root 'helper.rows'), [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $rows = [IO.StreamWriter]::new($file, [Text.Encoding]::ASCII)
    try {
        $rows.AutoFlush = $true
        $rows.Write("HERMES_STARTUP v=2 stage=1 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 resumed=0 exit=0 parent=0 cleanup=0 fault=0 cleanupFault=0`n")
        if ($PipeFixture -eq 4096) {
            # An inert CDB transport surrogate, never a Windows/native proof.
            # It accepts only the actually published fixed program and g roles.
            $ast = [Management.Automation.Language.Parser]::ParseFile(
                (Join-Path $PSScriptRoot 'Invoke-NativeStartupTrace.ps1'), [ref]$null, [ref]$null)
            foreach ($name in @('Assert-TraceCommandPath', 'New-InventoryEventCommands', 'New-ScopedCreateCommands')) {
                $definition = $ast.Find({
                    param($n)
                    $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name
                }, $true)
                . ([scriptblock]::Create($definition.Extent.Text))
            }
            $extension = Join-Path $root 'selected-sdk/winxp/exts.dll'
            $sources = New-ScopedCreateCommands $extension
            foreach ($entry in $sources.GetEnumerator()) {
                if ([IO.File]::ReadAllText((Join-Path $root $entry.Key)) -cne $entry.Value) {
                    throw 'Surrogate requires exact pinned program.'
                }
            }
            function Read-FixedFixtureInput {
                param([string]$Expected)
                $task = [Console]::In.ReadLineAsync()
                if (-not $task.Wait(10000) -or $task.GetAwaiter().GetResult() -cne $Expected) {
                    throw 'Fixed transport input refused.'
                }
            }
            function Write-FixedFixtureStop {
                param([string]$Text)
                [Console]::Out.Write($Text + '1:000> ')
                [Console]::Out.Flush()
            }
            [Console]::Out.Write("HERMES_CDB v=1 kind=0 pid=11`n")
            foreach ($name in @('cpr', 'epr', 'ibp')) {
                $label = switch ($name) { cpr { 'Create process' }; epr { 'Exit process' }; ibp { 'Initial breakpoint' } }
                $token = if ($name -ceq 'cpr') { '$$<' } else { '$$><' }
                [Console]::Out.Write($name + ' - ' + $label + " - break`nCommand: `"" +
                    $token + (Join-Path $root ('startup-' + $name + '.cdb')).Replace('\', '\\') + "`"`n")
            }
            [Console]::Out.Flush()
            $clock = [Diagnostics.Stopwatch]::StartNew()
            $created = Join-Path $root 'created.row'
            while (-not [IO.File]::Exists($created) -and $clock.ElapsedMilliseconds -lt 10000) {
                [Threading.Thread]::Sleep(20)
            }
            if (-not [IO.File]::Exists($created) -or [IO.File]::ReadAllText($created) -cne
                "HERMES_STARTUP_CREATED v=2 phase=created helper=11 pid=22 born=123`n") {
                throw 'Real controller receipt required.'
            }
            $rows.Write("HERMES_STARTUP v=2 stage=2 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 resumed=1 exit=0 parent=0 cleanup=0 fault=0 cleanupFault=0`n")
            Write-FixedFixtureStop "HERMES_CDB v=3 kind=1 pid=33`nLast event: 21.1: Create process 2:21`nHERMES_CDB v=3 kind=2 pid=33`n"
            Read-FixedFixtureInput 'g'
            Write-FixedFixtureStop "HERMES_CDB v=1 kind=3 pid=33`nLast event: 21.1: Exit process 2:21, code 0`nHERMES_CDB v=1 kind=4 pid=33`n"
            Read-FixedFixtureInput 'g'
            Write-FixedFixtureStop "HERMES_CDB v=3 kind=1 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_CDB v=3 kind=2 pid=22`n"
            Read-FixedFixtureInput ('$$<' + (Join-Path $root 'startup-arm.cdb'))
            Write-FixedFixtureStop ("HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nExtension DLL chain:`n" +
                "    unsupported SDK display`nHERMES_ARM_STAGE v=1 stage=1`n" +
                "Current expression evaluator: MASM - Microsoft Assembler expressions`nHERMES_NTGLOBALFLAG v=1 pid=22 value=00000002`n" +
                "HERMES_ARM_STAGE v=1 stage=2`nHERMES_CDB v=4 kind=8 pid=22`n")
            Read-FixedFixtureInput 'g'
            [Console]::Out.Write("16:1 @ 00000000 - LdrpCallInitRoutine - ERROR: DLL_PROCESS_ATTACH for dll `"C:\Windows\System32\sample.dll`" (InitRoutine: 0000000012345678) failed.`n")
            Write-FixedFixtureStop "HERMES_CDB v=1 kind=3 pid=22`nLast event: 16.1: Exit process 1:16, code c0000142`nHERMES_CDB v=1 kind=4 pid=22`n"
            Read-FixedFixtureInput 'g'
            $rows.Write("HERMES_STARTUP v=2 stage=3 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 resumed=1 exit=3221225794 parent=1 cleanup=1 fault=0 cleanupFault=0`n")
            Write-FixedFixtureStop "HERMES_CDB v=1 kind=3 pid=11`nLast event: b.1: Exit process 0:b, code 0`nHERMES_CDB v=1 kind=4 pid=11`n"
            Read-FixedFixtureInput 'g'
            exit 0
        }
        [Console]::Out.Write(('x' * $PipeFixture))
        [Console]::Out.Flush()
        [Console]::Error.Write(('e' * $PipeFixture))
        [Console]::Error.Flush()
        [Threading.Thread]::Sleep(750)
        [Console]::Out.Write("`nHERMES_CDB v=3 kind=2 pid=22`n")
        [Console]::Out.Flush()
        # A fixed producer deadline bounds even a read initiation that blocks
        # before returning its Task. No input, provider or native API is used.
        [Threading.Thread]::Sleep(6000)
    } finally { $rows.Dispose() }
    exit 0
}
if ($PipeScope -cne '') { throw 'Fixture scope is not a normal control argument.' }
Import-Module (Join-Path $PSScriptRoot 'Native-StartupTrace.psm1') -Force

# The original negative/parser controls remain explicit legacy controls only.
# The real no-argument driver and the v2 protocol cases below use native state.
function New-HermesStartupState {
    Native-StartupTrace\New-HermesStartupState -LegacyParserControls
}

$targetRow = 'HERMES_STARTUP v=1 stage=1 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 exit=0 parent=0 cleanup=0 fault=0 cleanupFault=0'
$finalRow = 'HERMES_STARTUP v=1 stage=2 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 exit=3221225794 parent=1 cleanup=1 fault=0 cleanupFault=0'
$failureLine = '16:1 @ 00000000 - LdrpCallInitRoutine - ERROR: DLL_PROCESS_ATTACH for dll "C:\Windows\System32\sample.dll" (InitRoutine: 0000000012345678) failed.'
$readyV2 = 'HERMES_STARTUP v=2 stage=1 helper=11 pid=22 born=123 image=1 debug=1 context=1 flags=134742020 resumed=0 exit=0 parent=0 cleanup=0 fault=0 cleanupFault=0'
$resumedV2 = $readyV2.Replace('stage=1', 'stage=2').Replace('resumed=0', 'resumed=1')
$finalV2 = $resumedV2.Replace('stage=2', 'stage=3').Replace('exit=0 parent=0 cleanup=0',
    'exit=3221225794 parent=1 cleanup=1')

function New-ControlNativeCreated {
    param(
        [bool]$Acknowledge = $true, [bool]$WithResumed = $true,
        [ValidateSet('AfterClose', 'BeforeClose', 'Absent')][string]$ReadyOrder = 'AfterClose',
        [switch]$DeferClose, [switch]$WithoutCreateCallback,
        [ValidateSet('Trace', 'Inventory')][string]$DiagnosticMode = 'Trace'
    )
    $native = Native-StartupTrace\New-HermesStartupState -DiagnosticMode $DiagnosticMode
    $native.CommandSha256 = 'a' * 64
    $sources = if ($DiagnosticMode -ceq 'Inventory') { New-InventoryEventCommands } else { New-TraceEventCommands }
    foreach ($name in @('Cpr', 'Epr', 'Ibp')) {
        $token = if ($name -ceq 'Cpr') { '$$<' } else { '$$><' }
        $native.FilterExpected[$name] = 'Command: "' + $token + 'D:\\owned\\startup-' + $name.ToLowerInvariant() + '.cdb"'
        $label = switch ($name) { 'Cpr' { 'Create process' }; 'Epr' { 'Exit process' }; 'Ibp' { 'Initial breakpoint' } }
        Add-HermesStartupLine $native ($name.ToLowerInvariant() + ' - ' + $label + ' - break')
        Add-HermesStartupLine $native $native.FilterExpected[$name]
        $field = switch ($name) { 'Cpr' { 'CreateCommandSha256' }; 'Epr' { 'ExitCommandSha256' }; 'Ibp' { 'InitialCommandSha256' } }
        $native[$field] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            [Text.Encoding]::ASCII.GetBytes($sources['startup-' + $name.ToLowerInvariant() + '.cdb']))).ToLowerInvariant()
    }
    if ($ReadyOrder -ceq 'BeforeClose') { Update-HermesStartupRows $native ($readyV2 + "`n") }
    Add-HermesStartupLine $native 'HERMES_CDB v=1 kind=0 pid=11'
    if (-not $WithoutCreateCallback) {
        foreach ($line in @(
            'HERMES_CDB v=3 kind=1 pid=22',
            'Last event: 16.1: Create process 1:16'
        )) { Add-HermesStartupLine $native $line }
        if (-not $DeferClose) { Add-HermesStartupLine $native 'HERMES_CDB v=3 kind=2 pid=22' }
    }
    if ($ReadyOrder -ceq 'AfterClose') { Update-HermesStartupRows $native ($readyV2 + "`n") }
    if ($Acknowledge) {
        Set-HermesStartupCreatedAcknowledged $native
        if ($WithResumed) {
            Update-HermesStartupRows $native ($readyV2 + "`n" + $resumedV2 + "`n")
        }
    }
    return $native
}

function New-ControlNativeStopped {
    param(
        [string]$Flags = '00000002', [bool]$WithResumed = $true,
        [string]$EventLine = 'Last event: 16.1: Break instruction exception - code 80000003 (first chance)',
        [string]$Prompt = '1:000> ', [switch]$ResumeBeforeCreate
    )
    $native = New-ControlNativeCreated -WithResumed $WithResumed -WithoutCreateCallback:$ResumeBeforeCreate
    if ($ResumeBeforeCreate) {
        foreach ($line in @(
            'HERMES_CDB v=3 kind=1 pid=22',
            'Last event: 16.1: Create process 1:16',
            'HERMES_CDB v=3 kind=2 pid=22'
        )) { Add-HermesStartupLine $native $line }
    }
    Add-HermesStartupLine $native 'HERMES_CDB v=3 kind=5 pid=22'
    Add-HermesStartupLine $native $EventLine
    if ($Flags -cne '') {
        Add-HermesStartupLine $native ('HERMES_NTGLOBALFLAG v=1 pid=22 value=' + $Flags)
    }
    Add-HermesStartupLine $native 'HERMES_CDB v=3 kind=6 pid=22'
    Add-HermesStartupText $native $native.Out $Prompt $true
    return $native
}

function Complete-ControlNative {
    param([hashtable]$Native, [string]$Final = $finalV2)
    foreach ($line in @(
        'HERMES_CDB v=1 kind=3 pid=22',
        'Last event: 16.1: Exit process 1:16, code c0000142',
        'HERMES_CDB v=1 kind=4 pid=22',
        'HERMES_CDB v=1 kind=3 pid=11',
        'Last event: b.1: Exit process 0:b, code 0',
        'HERMES_CDB v=1 kind=4 pid=11'
    )) { Add-HermesStartupLine $Native $line }
    Update-HermesStartupRows $Native ($readyV2 + "`n" + $resumedV2 + "`n" + $Final + "`n")
    New-HermesStartupReport $Native $true $true
}

function Get-ControlInventoryEvent {
    param(
        [ValidateSet('Create', 'Exit', 'Initial')][string]$Kind = 'Create',
        [uint32]$Current = 22, [uint32]$Event = 0, [uint32]$EventId = 0,
        [uint32]$Close = 0
    )
    if ($Event -eq 0) { $Event = $Current }
    if ($EventId -eq 0) { $EventId = $Event }
    if ($Close -eq 0) { $Close = $Current }
    $version = if ($Kind -ceq 'Exit') { 1 } else { 3 }
    $open = switch ($Kind) { 'Create' { 1 }; 'Exit' { 3 }; 'Initial' { 5 } }
    $last = switch ($Kind) {
        'Create' { 'Last event: {0:x}.1: Create process 1:{1:x}' -f $Event, $EventId }
        'Exit' { 'Last event: {0:x}.1: Exit process 1:{1:x}, code 25' -f $Event, $EventId }
        'Initial' { 'Last event: {0:x}.1: Break instruction exception - code 80000003 (first chance)' -f $Event }
    }
    @(
        ('HERMES_CDB v={0} kind={1} pid={2}' -f $version, $open, $Current), $last,
        ('HERMES_CDB v={0} kind={1} pid={2}' -f $version, ($open + 1), $Close), ''
    ) -join "`n"
}

function New-ControlInventoryComplete {
    param([string]$Final = $finalV2.Replace('exit=3221225794', 'exit=37'))
    $native = New-ControlNativeCreated -DiagnosticMode Inventory -WithoutCreateCallback
    foreach ($kind in @('Create', 'Initial', 'Exit')) {
        Add-HermesStartupText $native $native.Out (Get-ControlInventoryEvent -Kind $kind) $true
    }
    Add-HermesStartupText $native $native.Out (Get-ControlInventoryEvent -Kind Exit -Current 11) $true
    Update-HermesStartupRows $native ($readyV2 + "`n" + $resumedV2 + "`n" + $Final + "`n")
    $native.Out.End = $true; $native.Err.End = $true; $native.InputClosed = $true
    return $native
}

class StartupControlWriteStream : IO.MemoryStream {
    [int]$Mode
    [int]$Writes = 0
    [int]$Closes = 0
    [bool]$Closed = $false
    [scriptblock]$AfterWrite
    [Threading.Tasks.TaskCompletionSource]$Pending = [Threading.Tasks.TaskCompletionSource]::new()

    StartupControlWriteStream([int]$mode) { $this.Mode = $mode }

    [Threading.Tasks.Task] WriteAsync(
        [byte[]]$bytes, [int]$offset, [int]$count, [Threading.CancellationToken]$token) {
        if ($this.Closed) { throw 'Closed fake physical stream.' }
        $this.Writes++
        if ($this.Mode -eq 1) {
            return [Threading.Tasks.Task]::FromException([IO.IOException]::new('Synthetic write fault.'))
        }
        if ($this.Mode -eq 3) { return $this.Pending.Task }
        $this.Write($bytes, $offset, $count)
        if ($null -ne $this.AfterWrite) { $null = $this.AfterWrite.Invoke() }
        return [Threading.Tasks.Task]::CompletedTask
    }

    [void] Dispose([bool]$disposing) {
        $this.Closes++
        if ($this.Mode -eq 2) { throw 'Synthetic close fault.' }
        $this.Closed = $true
        if ($this.Mode -eq 3) { $null = $this.Pending.TrySetCanceled() }
    }
}

function Assert-Control {
    param([bool]$Value)
    if (-not $Value) { throw "Bounded startup protocol control failed at line $($MyInvocation.ScriptLineNumber)." }
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

function New-ControlTypedBlock {
    $state = New-HermesStartupState
    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
    Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=1 pid=22'
    Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
    Update-HermesStartupRows $state ($targetRow + "`n")
    return $state
}

function New-ControlReader {
    param([string]$Text)
    [IO.StreamReader]::new(
        [IO.MemoryStream]::new([Text.Encoding]::UTF8.GetBytes($Text)),
        [Text.Encoding]::UTF8)
}

function New-ControlPipe {
    $writer = [IO.Pipes.AnonymousPipeServerStream]::new(
        [IO.Pipes.PipeDirection]::Out, [IO.HandleInheritability]::None)
    try {
        $client = [IO.Pipes.AnonymousPipeClientStream]::new(
            [IO.Pipes.PipeDirection]::In, $writer.GetClientHandleAsString())
        @{ Writer = $writer; Reader = [IO.StreamReader]::new($client) }
    } catch { $writer.Dispose(); throw }
}

function Write-ControlPipe {
    param([hashtable]$Pipe, [byte[]]$Bytes)
    Assert-Control ($Pipe.Writer.CanWrite -and $Bytes.Length -le 4096)
    $Pipe.Writer.Write($Bytes, 0, $Bytes.Length)
    $Pipe.Writer.Flush()
}

function Receive-ControlPipe {
    param([hashtable]$Stream, [hashtable]$Pipe, [bool]$Parse)
    # Completion is not the assertion: each caller checks real parser/count
    # progress while the writer remains open. This timeout is only a watchdog.
    Assert-Control ($null -ne $Stream.Pending -and $Stream.Pending.Wait(5000))
    Receive-TraceRead $Stream $Pipe.Reader.BaseStream $Parse
}

function Close-ControlPipe {
    param([hashtable]$Stream, [hashtable]$Pipe)
    $Pipe.Writer.Dispose()
    Close-TraceRead $Stream $Pipe.Reader
}

function New-ControlProcessReader {
    param([IO.StreamReader]$Reader)
    if ($Reader.BaseStream -is [IO.FileStream]) { return $Reader }
    Assert-Control ($Reader.BaseStream -is [IO.Pipes.PipeStream])
    # Unix Process uses PipeStream; Windows Process uses FileStream. Exercise
    # that FileStream strategy over the genuine, retained process pipe handle,
    # without reading its original reader or transferring handle ownership.
    $handle = [Microsoft.Win32.SafeHandles.SafeFileHandle]::new(
        $Reader.BaseStream.SafePipeHandle.DangerousGetHandle(), $false)
    try {
        [IO.StreamReader]::new([IO.FileStream]::new($handle, [IO.FileAccess]::Read, 4096, $false))
    } catch { $handle.Dispose(); throw }
}

class StartupControlIdleStream : IO.MemoryStream {
    [byte[]]$FirstBytes
    [int]$Reads = 0
    [Threading.Tasks.TaskCompletionSource[int]]$Idle = [Threading.Tasks.TaskCompletionSource[int]]::new()

    StartupControlIdleStream([string]$text) {
        $this.FirstBytes = [Text.Encoding]::UTF8.GetBytes($text)
    }

    [Threading.Tasks.Task[int]] ReadAsync([byte[]]$buffer, [int]$offset, [int]$count) {
        $this.Reads++
        if ($this.Reads -eq 1 -and $this.FirstBytes.Length -gt 0) {
            [Array]::Copy($this.FirstBytes, 0, $buffer, $offset, $this.FirstBytes.Length)
            return [Threading.Tasks.Task]::FromResult($this.FirstBytes.Length)
        }
        return $this.Idle.Task
    }
}

class StartupControlContinueStream : IO.MemoryStream {
    [byte[]]$FirstBytes
    [byte[]]$AfterBytes
    [byte[]]$HeldBuffer
    [int]$HeldOffset
    [int]$Reads = 0
    [Threading.Tasks.TaskCompletionSource[int]]$Gate = [Threading.Tasks.TaskCompletionSource[int]]::new()

    StartupControlContinueStream([string]$first, [string]$after) {
        $this.FirstBytes = [Text.Encoding]::UTF8.GetBytes($first)
        $this.AfterBytes = [Text.Encoding]::UTF8.GetBytes($after)
    }

    [Threading.Tasks.Task[int]] ReadAsync([byte[]]$buffer, [int]$offset, [int]$count) {
        $this.Reads++
        if ($this.Reads -eq 1) {
            [Array]::Copy($this.FirstBytes, 0, $buffer, $offset, $this.FirstBytes.Length)
            return [Threading.Tasks.Task]::FromResult($this.FirstBytes.Length)
        }
        if ($this.Reads -eq 2) {
            $this.HeldBuffer = $buffer; $this.HeldOffset = $offset
            return $this.Gate.Task
        }
        return [Threading.Tasks.Task]::FromResult(0)
    }

    [void] Release() {
        [Array]::Copy($this.AfterBytes, 0, $this.HeldBuffer, $this.HeldOffset, $this.AfterBytes.Length)
        $null = $this.Gate.TrySetResult($this.AfterBytes.Length)
    }
}

class StartupControlFaultStream : IO.MemoryStream {
    [Threading.Tasks.Task[int]] ReadAsync([byte[]]$buffer, [int]$offset, [int]$count) {
        return [Threading.Tasks.Task]::FromException[int](
            [IO.IOException]::new('Synthetic owned read fault.'))
    }
}

class StartupControlEmptyReadStream : IO.MemoryStream {
    StartupControlEmptyReadStream([byte[]]$bytes) : base($bytes) {}

    [int] Read([byte[]]$buffer, [int]$offset, [int]$count) { return 0 }
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
        'New-InventoryEventCommands', 'New-ScopedCreateCommands', 'Get-TraceSelectedSources', 'Confirm-TraceCommandSelection',
        'Publish-TraceCommands', 'Close-TraceInventoryInput',
        'Confirm-TracePublication',
        'Start-TraceRead', 'Receive-TraceRead', 'Close-TraceRead', 'Invoke-TraceCollection')) {
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

function Invoke-ControlCreatedPublication {
    # Load the exact publisher, but refuse a path before its first file open.
    # An attempt is deliberately not a persisted receipt or acknowledgement.
    $root = $null
    $definition = @($driverAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
            -and $node.Name -ceq 'Publish-TraceCreated'
    }, $true))
    Assert-Control ($definition.Count -eq 1)
    . ([scriptblock]::Create($definition[0].Extent.Text))
    Publish-TraceCreated
}

function Invoke-ControlRowsRead {
    param([IO.Stream]$Source, [bool]$Exists = $true)
    $definition = @($driverAst.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
            -and $node.Name -ceq 'Read-TraceControl'
    }, $true))
    Assert-Control ($definition.Count -eq 1)
    $open = @($definition[0].FindAll({
        param($node)
        $node -is [Management.Automation.Language.InvokeMemberExpressionAst] `
            -and $node.Expression.Extent.Text -ceq '[IO.File]' -and $node.Member.Extent.Text -ceq 'Open'
    }, $true))
    Assert-Control ($open.Count -eq 1 -and $open[0].Arguments.Count -eq 4 `
        -and $open[0].Arguments[1].Extent.Text -ceq '[IO.FileMode]::Open' `
        -and $open[0].Arguments[2].Extent.Text -ceq '[IO.FileAccess]::Read' `
        -and $open[0].Arguments[3].Extent.Text -ceq '[IO.FileShare]::ReadWrite')
    # Keep the live reader body, substituting only its file-open/existence
    # boundary. No private root, disk fixture, helper or process is involved.
    $text = $definition[0].Extent.Text.Replace(
        '[IO.File]::Exists($Path)', '(Test-ControlRowsExists)').Replace(
        $open[0].Extent.Text, '(Open-ControlRows)')
    function Test-ControlRowsExists { return $Exists }
    function Open-ControlRows { return $Source }
    function Assert-TracePath { param([string]$Path) return $Path }
    . ([scriptblock]::Create($text))
    Read-TraceControl 'owned-control'
}

function Read-TraceControl {
    param([string]$Path)
    $script:controlReads++
}

function Publish-TraceArm { $script:controlArms++ }
function Publish-TraceCreated {}

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
        -or ($state.TracePoint -eq 9 -and $state.ToolRole -eq 6) `
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
        -and $Path -cin @('startup-cpr.cdb', 'startup-ibp.cdb', 'startup-epr.cdb', 'startup.cdb', 'startup-arm.cdb' |
            ForEach-Object { Join-Path $root $_ }) `
        -and -not $script:controlCommandFiles.ContainsKey($Path))
    $script:controlCommandWrites.Add($Path)
    $name = ($Path -split '[\\/]')[-1]
    if ($script:controlCommandFaultName -ceq $name -and $script:controlCommandFault -eq 1) { return }
    $copy = [byte[]]$Bytes.Clone()
    if ($script:controlCommandFaultName -ceq $name) {
        switch ($script:controlCommandFault) {
            2 { $copy[0] = $copy[0] -bxor 1 }
            3 { $copy = [byte[]]($copy + [byte]0) }
            4 { throw 'C:\private-command S-1-5-21-123 --command readonly failure' }
            5 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('sxe -c', 'sxi -c')) }
            6 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('sxe -c', 'sx- -c')) }
            7 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('sxe -c', 'sxz -c')) }
            8 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace("`nsx`n", "`n??`n")) }
            9 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('\\', '\')) }
            10 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('\\', '')) }
            11 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace("`n.chain`n", "`n.echo `n")) }
            12 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('.load "C:\\', '.load "C:\ ')) }
            13 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('sxe -c "$$<', 'sxe -c "$$><')) }
            14 { $copy = [Text.Encoding]::ASCII.GetBytes(
                [Text.Encoding]::ASCII.GetString($copy).Replace('sxe -c "$$<', 'sxe -c "$><')) }
            15 { $copy = [Text.Encoding]::ASCII.GetBytes((New-TraceEventCommands)[$name]) }
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

function New-ControlScopedCreated {
    param([switch]$WithoutCreate, [switch]$WithoutReady, [switch]$WithoutAck, [switch]$WithoutResumed)
    $script:controlCommandFiles = @{}; $script:controlCommandReadOnly = @{}
    $script:controlCommandWrites = [Collections.Generic.List[string]]::new()
    $script:controlCommandPins = [Collections.Generic.List[string]]::new()
    $script:controlCommandHashes = 0
    $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode ScopedCreateV1
    Set-HermesStartupTracePoint $state 24
    $state.DebuggerSha256 = 'd' * 64; $state.HelperSha256 = 'e' * 64
    $sdkPin = [IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes('synthetic signed SDK source'), $false)
    $pins.Add($sdkPin)
    $state.ExtensionSha256 = Get-ControlCommandHash $sdkPin
    $state.ExtensionSource = @{ Path = $extension; Sha256 = $state.ExtensionSha256; Pin = $sdkPin }
    foreach ($name in @('Driver', 'Module')) {
        $bytes = [Text.Encoding]::ASCII.GetBytes('fixed-source-' + $name)
        $pin = [IO.MemoryStream]::new($bytes, $false)
        $pins.Add($pin)
        $state.SourcePins[$name] = $pin
        $state[$name + 'Sha256'] = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
    }
    $null = Publish-TraceCommands $extension
    Confirm-TraceCommandSelection $extension
    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
    foreach ($name in @('Cpr', 'Epr', 'Ibp')) {
        $label = switch ($name) { Cpr { 'Create process' }; Epr { 'Exit process' }; Ibp { 'Initial breakpoint' } }
        Add-HermesStartupLine $state ($name.ToLowerInvariant() + ' - ' + $label + ' - break')
        Add-HermesStartupLine $state $state.FilterExpected[$name]
    }
    if (-not $WithoutReady) {
        Update-HermesStartupRows $state ($readyV2 + "`n")
        if (-not $WithoutAck) {
            Set-HermesStartupCreatedAcknowledged $state
            if (-not $WithoutResumed) { Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n") }
        }
    }
    if (-not $WithoutCreate) {
        Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent) + '1:000> ') $true
    }
    return $state
}

function Get-ControlScopedReadback {
    param([string]$Flags = '00000002', [string]$Chain = "Extension DLL chain:`n    unsupported SDK display`n")
    $text = "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n" +
        $Chain + "HERMES_ARM_STAGE v=1 stage=1`n"
    if ($Flags -cne '') { $text += "HERMES_NTGLOBALFLAG v=1 pid=22 value=$Flags`n" }
    return $text + "HERMES_ARM_STAGE v=1 stage=2`nHERMES_CDB v=4 kind=8 pid=22`n1:000> "
}

function Get-ControlScopedQuerySources {
    param([string]$Extension)
    $query = ((New-ScopedCreateCommands $Extension)['startup-arm.cdb'] -split "`r`n")[6]
    $start = $query.IndexOf('.printf ', [StringComparison]::Ordinal)
    $end = $query.IndexOf(' }; .echo ', [StringComparison]::Ordinal)
    Assert-Control ($start -gt 0 -and $end -gt $start)
    $read = $query.Substring($start, $end - $start)
    Assert-Control ($read -ceq '.printf "HERMES_NTGLOBALFLAG v=1 pid=%u value=%08x\n", @$tpid, @@c++(@$peb->NtGlobalFlag);')
    return @{ Whole = $query; Read = $read }
}

function Invoke-ControlScopedPublisher {
    $definition = $driverAst.Find({
        param($n)
        $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Publish-TraceArm'
    }, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
    Publish-TraceArm
}

function Complete-ControlScoped {
    param([hashtable]$State, [IO.StreamWriter]$Writer)
    Add-HermesStartupLine $State $failureLine
    Add-HermesStartupText $State $State.Out ((Get-ControlInventoryEvent -Kind Exit).Replace('code 25', 'code c0000142') + '1:000> ') $true
    Invoke-HermesStartupScopedInput $State $Writer Side
    Update-HermesStartupRows $State ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`n")
    Add-HermesStartupText $State $State.Out ((Get-ControlInventoryEvent -Kind Exit -Current 11).Replace('code 25', 'code 0') + '1:000> ') $true
    Invoke-HermesStartupScopedInput $State $Writer Side
    $State.Out.Tail = ''; $State.Out.End = $true; $State.Err.End = $true
    Set-HermesStartupDebuggerExit $State ([int]0)
    return New-HermesStartupReport $State $true $true
}

$evaluatorSourceInsertion = @(
    '        # Pin general command parsing after the SDK call; explicit @@c++ stays.',
    '        # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/evaluating-expressions',
    "        '.expr /s masm',", ''
) -join "`n"
$evaluatorMasm = 'Current expression evaluator: MASM - Microsoft Assembler expressions'
$evaluatorCpp = 'Current expression evaluator: C++ - C++ source expressions'
$cases = 1..541 # All 538 prior intents retained, plus final MASM-bound refusal fingerprints.
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
                Assert-Control ($pins.Count -eq 4 -and $script:controlCommandHashes -eq 4 `
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
        if ($case -ge 213) {
            $state.Out.Hash.Dispose()
            $state.Err.Hash.Dispose()
            $state = $null
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
                    Start-TraceRead $state.Out $outReader.BaseStream
                    Start-TraceRead $state.Err $errReader.BaseStream
                    $pending = $state.Err.Pending
                    $outBefore = Get-ControlStreamSnapshot $state.Out
                    $errBefore = Get-ControlStreamSnapshot $state.Err
                    Receive-TraceRead $state.Out $outReader.BaseStream $true
                    Receive-TraceRead $state.Err $errReader.BaseStream $false
                    Start-TraceRead $state.Out $outReader.BaseStream
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
                Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                Start-TraceRead $state.Err $debugger.StandardError.BaseStream
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
                    StandardOutput = [IO.StreamReader]::new([StartupControlIdleStream]::new($outText))
                    StandardError = [IO.StreamReader]::new([StartupControlIdleStream]::new($errText))
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
                    $state.Out.Pending = $debugger.StandardOutput.BaseStream.Idle.Task
                    $state.Err.Pending = $debugger.StandardError.BaseStream.Idle.Task
                } else {
                    Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                    Start-TraceRead $state.Err $debugger.StandardError.BaseStream
                }
                . $controlDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $expectedReads = [int]$lastByte
                Assert-Control ($state.Bounds -eq 1 -and $report.status -eq 7 `
                    -and $report.acceptedUtf8Bytes -eq 8388608 -and $report.completed -eq 0 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                    -and $debugger.StandardOutput.BaseStream.Reads -eq $expectedReads `
                    -and $debugger.StandardError.BaseStream.Reads -eq $expectedReads `
                    -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                    -and $script:controlExitPolls -eq 1 `
                    -and $script:controlReads -eq 0 -and $script:controlArms -eq 0 `
                    -and $script:controlWaits -eq 0 -and $state.Timeout -eq 0 `
                    -and $debuggerEnded -and -not $drainsEnded `
                    -and -not $state.Out.End -and -not $state.Err.End `
                    -and $report.cleanupComplete -eq 0 -and $report.cleanupFault -eq 13 `
                    -and -not $debugger.StandardOutput.BaseStream.CanRead `
                    -and -not $debugger.StandardError.BaseStream.CanRead `
                    -and -not $debugger.StandardOutput.BaseStream.Idle.Task.IsCompleted `
                    -and -not $debugger.StandardError.BaseStream.Idle.Task.IsCompleted `
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
                        -and ($commands -split "`n")[0] -ceq ('.load "' + $expectedPath.Replace('\', '\\') + '"') `
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
                Assert-Control ($report.observations.Count -eq 56)
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
                    'HERMES_CDB v=4 kind=1 pid=22',
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
                    'startup-ibp.cdb' = 'initialCommandSha256'
                }
                Assert-Control ($script:controlCommandWrites.Count -eq 4 `
                    -and $script:controlCommandPins.Count -eq 4 `
                    -and $script:controlCommandHashes -eq 4 -and $script:controlLaunchBindings -eq 1 `
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
                Assert-Control ($arguments.Count -eq 9 -and $arguments[0] -ceq '-ee' -and $arguments[1] -ceq 'masm' `
                    -and $arguments[2] -ceq '-o' -and $arguments[3] -ceq '-cf' `
                    -and $arguments[4] -ceq (Join-Path $root 'startup.cdb') `
                    -and $arguments[5] -ceq '-y' -and $arguments[7] -ceq $helper `
                    -and $arguments[8] -ceq 'startup-debug-probe' `
                    -and $script:controlStartInfo.FileName -ceq (Join-Path $kits 'cdb.exe') `
                    -and $script:controlStartInfo.WorkingDirectory -ceq $kits `
                    -and $script:controlStartInfo.StandardInputEncoding.CodePage -eq 20127 `
                    -and -not $script:controlStartInfo.UseShellExecute `
                    -and $arguments -cnotcontains '-g' -and $arguments -cnotcontains '-G')
                $lines = @($sources['startup.cdb'] -split "`n")
                Assert-Control ($lines.Count -eq 9 -and $lines[0] -ceq ('.load "' + $extension.Replace('\', '\\') + '"') `
                    -and $lines[1] -ceq '.chain' `
                    -and $lines[2] -ceq '.printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid' `
                    -and $lines[3] -ceq ('sxe -c "$$><' + (Join-Path $root 'startup-ibp.cdb').Replace('\', '\\') + '" ibp') `
                    -and $lines[4] -ceq ('sxe -c "$$<' + (Join-Path $root 'startup-cpr.cdb').Replace('\', '\\') + '" cpr') `
                    -and $lines[5] -ceq ('sxe -c "$$><' + (Join-Path $root 'startup-epr.cdb').Replace('\', '\\') + '" epr') `
                    -and $lines[6] -ceq 'sx' `
                    -and $lines[7] -ceq 'g' -and $lines[8] -ceq '')
                foreach ($line in $lines[4..5]) {
                    Assert-Control (@($line.ToCharArray() | Where-Object { $_ -eq '"' }).Count -eq 2 `
                        -and $line -notmatch '\.printf|\.lastevent|!gflag|\\"')
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'command-root|command root|selected.sdk|fixed-published|startup-cpr|startup-epr|\$\$|HERMES_CDB')
            }
            91 {
                $sources = New-TraceEventCommands
                $expectedCreate = @(
                    '.printf "HERMES_CDB v=3 kind=1 pid=%u\n", @$tpid',
                    '.lastevent', '|.',
                    '.printf "HERMES_CDB v=3 kind=2 pid=%u\n", @$tpid', 'g'
                )
                $expectedInitial = @(
                    '.printf "HERMES_CDB v=3 kind=5 pid=%u\n", @$tpid',
                    '.lastevent', '|.',
                    '.printf "HERMES_CDB v=3 kind=6 pid=%u\n", @$tpid'
                )
                $expectedExit = @(
                    '.printf "HERMES_CDB v=1 kind=3 pid=%u\n", @$tpid', '.lastevent',
                    '.printf "HERMES_CDB v=1 kind=4 pid=%u\n", @$tpid', 'g'
                )
                Assert-Control ($sources.Count -eq 3 `
                    -and $sources['startup-cpr.cdb'] -ceq (($expectedCreate -join "`r`n") + "`r`n") `
                    -and $sources['startup-ibp.cdb'] -ceq (($expectedInitial -join "`r`n") + "`r`n") `
                    -and $sources['startup-epr.cdb'] -ceq (($expectedExit -join "`r`n") + "`r`n"))
                foreach ($text in $sources.Values) {
                    Assert-Control (-not $text.Contains('\"', [StringComparison]::Ordinal) `
                        -and $text -notmatch '(?<!\r)\n|sx-|sxe|sxd|\.load|\$\$')
                }
            }
            { $_ -in 92..100 } {
                $script:controlCommandFault = [int][Math]::Floor(($case - 92) / 3) + 1
                $sourceIndex = ($case - 92) % 3
                $script:controlCommandFaultName = @('startup-cpr.cdb', 'startup-epr.cdb', 'startup.cdb')[$sourceIndex]
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $sourceIndex = @(0, 2, 3)[$sourceIndex]
                $expectedPins = $sourceIndex + [int]($script:controlCommandFault -ne 1)
                $expectedHashes = $sourceIndex + [int]($script:controlCommandFault -eq 2)
                Assert-Control ($script:controlCommandWrites.Count -eq 4 `
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
                Assert-Control ($script:controlCommandWrites.Count -eq 3 `
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
                    '1:001> $$<C:\private\startup-cpr.cdb',
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
                    -and ($report | ConvertTo-Json -Depth 4 -Compress) -notmatch 'private|\$\$|printf')
            }
            111 {
                . $controlCommandDriver
                $expectedLines = @(
                    ('.load "' + $extension.Replace('\', '\\') + '"'),
                    '.chain',
                    '.printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid',
                    ('sxe -c "$$><' + (Join-Path $root 'startup-ibp.cdb').Replace('\', '\\') + '" ibp'),
                    ('sxe -c "$$<' + (Join-Path $root 'startup-cpr.cdb').Replace('\', '\\') + '" cpr'),
                    ('sxe -c "$$><' + (Join-Path $root 'startup-epr.cdb').Replace('\', '\\') + '" epr'),
                    'sx', 'g'
                )
                $expected = [Text.Encoding]::ASCII.GetBytes(($expectedLines -join "`n") + "`n")
                $path = Join-Path $root 'startup.cdb'
                $expectedHash = [Convert]::ToHexString(
                    [Security.Cryptography.SHA256]::HashData($expected)).ToLowerInvariant()
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ([Convert]::ToHexString($script:controlCommandFiles[$path]) `
                        -ceq [Convert]::ToHexString($expected) `
                    -and $report.commandSha256 -ceq $expectedHash `
                    -and $script:controlCommandPins -ccontains $path `
                    -and $script:controlStartInfo.ArgumentList[4] -ceq $path `
                    -and $script:controlLaunchBindings -eq 1 -and $script:controlCommandHashes -eq 4 `
                    -and $report.error -eq 0 -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0)
                $events = New-TraceEventCommands
                foreach ($eventSource in $events.GetEnumerator()) {
                    $text = $eventSource.Value
                    $lines = @($text.TrimEnd("`r", "`n") -split "`r`n")
                    $continues = [int]($eventSource.Key -cne 'startup-ibp.cdb')
                    Assert-Control (@($lines | Where-Object { $_ -ceq 'g' }).Count -eq $continues `
                        -and (($continues -eq 1 -and $lines[-1] -ceq 'g') `
                            -or ($continues -eq 0 -and $lines[-1].Contains('kind=6'))) `
                        -and $text -notmatch 'sx[edinx-]|(?<!\r)\n')
                }
            }
            { $_ -in 112..115 } {
                $script:controlCommandFault = $case - 107
                $script:controlCommandFaultName = 'startup.cdb'
                . $controlCommandDriver
                $path = Join-Path $root 'startup.cdb'
                $expectedBytes = [Text.Encoding]::ASCII.GetBytes((New-TraceCommands $extension))
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($script:controlCommandFiles[$path].Length -eq $expectedBytes.Length `
                    -and [Convert]::ToHexString($script:controlCommandFiles[$path]) `
                        -cne [Convert]::ToHexString($expectedBytes) `
                    -and $script:controlCommandWrites.Count -eq 4 -and $script:controlCommandPins.Count -eq 4 `
                    -and $script:controlCommandHashes -eq 4 -and $script:controlLaunchBindings -eq 0 `
                    -and $null -eq $script:controlStartInfo -and $state.TraceFaulted `
                    -and $report.tracePoint -eq 24 -and $report.error -eq 13 -and $report.status -eq 8 `
                    -and $report.commandSha256 -ceq '' -and $report.createCommandSha256 -ceq '' `
                    -and $report.exitCommandSha256 -ceq '' -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0)
            }
            116 {
                . $controlCommandDriver
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Update-HermesStartupRows $state ($targetRow + "`n")
                # Readback-shaped text, including an unknown state, is not
                # parsed as registration proof or a debugger event.
                foreach ($line in @(
                    '1:001> sx',
                    'cpr - Create process - break',
                    '       Command: "$$<C:\private\startup-cpr.cdb"',
                    'epr - Exit process - break',
                    '       Command: "$$><C:\private\startup-epr.cdb"',
                    'cpr - Create process - unknown C:\private\secret S-1-5-21-123 --command'
                )) { Add-HermesStartupLine $state $line }
                Set-HermesStartupTracePoint $state 26
                Set-HermesStartupTraceFault $state
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($report.helperMatched -eq 1 -and $observed.TargetRowPresent -eq 1 `
                    -and $observed.FinalRowPresent -eq 0 -and $observed.Marker0 -eq 1 `
                    -and $observed.Marker1 -eq 0 -and $observed.Marker2 -eq 0 `
                    -and $observed.Marker3 -eq 0 -and $observed.Marker4 -eq 0 `
                    -and $observed.LastEventSeen -eq 0 -and $observed.FlagsSeen -eq 0 `
                    -and $observed.ArmPresent -eq 0 -and $observed.BlockPresent -eq 0 `
                    -and $observed.ParserProtocol -eq 0 -and $observed.ParserUnknown -eq 0 `
                    -and $report.commandSha256 -cmatch '\A[0-9a-f]{64}\z' `
                    -and $report.createCommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
                    -and $report.exitCommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
                    -and -not (Test-HermesStartupArm $state) -and -not $state.Acked `
                    -and $report.tracePoint -eq 26 -and $report.status -eq 8 -and $report.error -eq 13 `
                    -and $report.cleanupFault -eq 0 -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0)
                foreach ($value in $observed.Values) {
                    Assert-Control ($value -is [int])
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|secret|S-1-|--command|Create process|Exit process|\$\$')
            }
            117 {
                . $controlCommandDriver
                $state.Out = New-HermesStartupStream
                $state.Err = New-HermesStartupStream
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Update-HermesStartupRows $state ($targetRow + "`n")
                # The existing prefix cannot retain this readback; the bounded
                # streaming parser must still observe complete, split lines.
                Add-HermesStartupText $state $state.Out (('x' * 4095) + "`n") $true
                $text = " cpr - Create process - break`r`n       " +
                    $state.FilterExpected.Cpr + "`r`n epr - Exit process - break`r`n       " +
                    $state.FilterExpected.Epr + "`r`n"
                for ($index = 0; $index -lt $text.Length; $index += 7) {
                    Add-HermesStartupText $state $state.Out (
                        $text.Substring($index, [Math]::Min(7, $text.Length - $index))) $true
                }
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.CprFilter -eq 9 `
                    -and $report.observations.EprFilter -eq 9 -and $report.outKept -eq 4096 `
                    -and $report.helperMatched -eq 1 -and $report.targetMatched -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.completed -eq 0 -and -not (Test-HermesStartupArm $state) `
                    -and $report.observations.ParserUnknown -eq 0 `
                    -and $report.observations.ParserProtocol -eq 0 `
                    -and $report.observations.Marker1 -eq 0 -and $report.observations.FlagsSeen -eq 0)
                Assert-Control (($report | ConvertTo-Json -Depth 4 -Compress) `
                    -notmatch 'command-root|startup-cpr|startup-epr|\$\$|Command:|xxxxx')
            }
            118 {
                . $controlCommandDriver
                $expected = $state.FilterExpected.Cpr
                foreach ($line in @(
                    $expected.Replace('startup-cpr', 'startup-epr'),
                    ($expected + ' trailing'),
                    $expected.Replace('startup-cpr', 'STARTUP-CPR'),
                    ('0:000> ' + $expected),
                    $expected.Replace('$$<', '$$><'),
                    $expected.Replace('"', '')
                )) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state $line
                    Assert-Control ((New-HermesStartupObservations $state).CprFilter -eq 1)
                }
                $state.FilterExpected.Cpr = ''
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Add-HermesStartupLine $state $expected
                Assert-Control ((New-HermesStartupObservations $state).CprFilter -eq 1 `
                    -and -not (Test-HermesStartupArm $state))
            }
            119 {
                . $controlCommandDriver
                $statuses = @('break', 'second-chance break', 'output', 'ignore', 'unrecognised')
                for ($index = 0; $index -lt $statuses.Count; $index++) {
                    Add-HermesStartupLine $state ('cpr - Create process - ' + $statuses[$index])
                    Add-HermesStartupLine $state ('epr - Exit process - ' + $statuses[$index])
                    Add-HermesStartupLine $state $state.FilterExpected.Epr
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.CprFilter -eq ($index + 1) `
                        -and $observed.EprFilter -eq ($index + 9))
                }
                Assert-Control (-not (Test-HermesStartupArm $state) -and -not $state.Acked)
            }
            120 {
                . $controlCommandDriver
                foreach ($intervening in @('', '       Second command: "g"', '0:000> sx',
                        'av - Access violation - break - not handled')) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state $intervening
                    Add-HermesStartupLine $state $state.FilterExpected.Cpr
                    Assert-Control ((New-HermesStartupObservations $state).CprFilter -eq 1)
                }
                Add-HermesStartupLine $state 'cpr - Exit process - break'
                Add-HermesStartupLine $state $state.FilterExpected.Cpr
                Assert-Control ((New-HermesStartupObservations $state).CprFilter -eq 13)
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Add-HermesStartupLine $state $state.FilterExpected.Cpr
                Add-HermesStartupLine $state 'cpr - Create process - ignore'
                Assert-Control ((New-HermesStartupObservations $state).CprFilter -eq 4 `
                    -and $state.FilterPending -ceq 'Cpr')
            }
            121 {
                . $controlCommandDriver
                $state.Out = New-HermesStartupStream
                $state.Err = New-HermesStartupStream
                $text = "cpr - Create process - break`n" + $state.FilterExpected.Cpr + "`n"
                Add-HermesStartupText $state $state.Err $text $false
                Assert-Control ((New-HermesStartupObservations $state).CprFilter -eq 0 `
                    -and $state.FilterPending -ceq '')
                $state.AcceptedUtf8Bytes = [long]8388607
                $before = (New-HermesStartupObservations $state) | ConvertTo-Json -Compress
                Add-HermesStartupText $state $state.Out "`n" $true
                Add-HermesStartupText $state $state.Out $text $true
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.acceptedUtf8Bytes -eq 8388608 -and $report.status -eq 7 `
                    -and ($report.observations | ConvertTo-Json -Compress) -ceq $before)
            }
            122 {
                foreach ($value in @(6, 7, 8, 14, -1, 'Command: "C:\private\secret"',
                        [long]9, $true, $null, @{ message = 'S-1-5-21-123' })) {
                    $state.Observations.CprFilter = $value
                    $state.Observations.EprFilter = $value
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.CprFilter -is [int] -and $observed.CprFilter -eq 0 `
                        -and $observed.EprFilter -is [int] -and $observed.EprFilter -eq 0)
                }
            }
            123 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture
                $before = New-HermesStartupReport $state $true $true
                $state.FilterExpected.Cpr = 'Command: "$$<C:\private\secret S-1-5-21-123 --command.cdb"'
                Add-HermesStartupLine $state 'cpr - Create process - ignore'
                Add-HermesStartupLine $state $state.FilterExpected.Cpr
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.CprFilter -eq 12 -and $report.completed -eq 1 `
                    -and $report.armingVerified -eq 1 -and $report.firstFailureKind -eq 1)
                foreach ($name in $before.Keys | Where-Object { $_ -cne 'observations' }) {
                    Assert-Control ($report[$name] -ceq $before[$name])
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|secret|S-1-|--command|\$\$|Command:')
            }
            124 {
                $script:controlCommandFault = 2
                $script:controlCommandFaultName = 'startup.cdb'
                . $controlCommandDriver
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Add-HermesStartupLine $state (
                    'Command: "$$<' + (Join-Path $root 'startup-cpr.cdb') + '"')
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($state.FilterExpected.Cpr -ceq '' -and $state.FilterExpected.Epr -ceq '' `
                    -and $report.observations.CprFilter -eq 1 -and $report.status -eq 8 `
                    -and $report.error -eq 13 -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $script:controlLaunchBindings -eq 0)
            }
            125 {
                . $controlCommandDriver
                $quoted = $state.FilterExpected.Cpr.Substring(9)
                $unquoted = $quoted.Substring(1, $quoted.Length - 2)
                $examples = @(
                    @{ Text = ('       Command: ' + $quoted); Shape = 1; Filter = 9 },
                    @{ Text = ('       Command: ' + $unquoted); Shape = 2; Filter = 1 },
                    @{ Text = ('       Command1: ' + $quoted); Shape = 3; Filter = 1 },
                    @{ Text = ('       Command1: ' + $unquoted); Shape = 4; Filter = 1 },
                    @{ Text = '       Command: ""'; Shape = 5; Filter = 1 },
                    @{ Text = '       Command1: '; Shape = 5; Filter = 1 },
                    @{ Text = '       Command: "C:\private\secret S-1-5-21-123"'; Shape = 6; Filter = 1 },
                    @{ Text = 'epr - Exit process - break'; Shape = 7; Filter = 1 },
                    @{ Text = ''; Shape = 8; Filter = 1 },
                    @{ Text = 'unclassified private text --command'; Shape = 9; Filter = 1 }
                )
                foreach ($example in $examples) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state $example.Text
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.CprCommandShape -eq $example.Shape `
                        -and $observed.CprFilter -eq $example.Filter `
                        -and $observed.ArmPresent -eq 0 -and $observed.BlockPresent -eq 0 `
                        -and $observed.ParserUnknown -eq 0 -and $observed.ParserProtocol -eq 0)
                }
                Assert-Control (-not (Test-HermesStartupArm $state) -and -not $state.Acked)
            }
            126 {
                . $controlCommandDriver
                $state.Out = New-HermesStartupStream
                $state.Err = New-HermesStartupStream
                Add-HermesStartupText $state $state.Out (('x' * 4095) + "`n") $true
                $text = "cpr - Create process - break`r`nepr - Exit process - break`r`n" +
                    '       Command: ' + $state.FilterExpected.Epr.Substring(10,
                        $state.FilterExpected.Epr.Length - 11) + "`r`n"
                for ($index = 0; $index -lt $text.Length; $index += 3) {
                    Add-HermesStartupText $state $state.Out (
                        $text.Substring($index, [Math]::Min(3, $text.Length - $index))) $true
                }
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.CprCommandShape -eq 7 `
                    -and $report.observations.EprCommandShape -eq 2 `
                    -and $report.observations.CprFilter -eq 1 -and $report.observations.EprFilter -eq 1 `
                    -and $report.outKept -eq 4096 -and $report.outDropped -gt 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.completed -eq 0)
            }
            127 {
                . $controlCommandDriver
                $quoted = $state.FilterExpected.Cpr.Substring(9)
                foreach ($text in @(
                    ('Command: ' + $quoted.Replace('startup-cpr', 'STARTUP-CPR')),
                    ('Command: ' + $quoted.Replace('startup-cpr', 'startup-epr')),
                    ('Command: ' + $quoted + ';g'),
                    ('Command: ' + $quoted.Replace('$$<', '$$><'))
                )) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state $text
                    Assert-Control ((New-HermesStartupObservations $state).CprCommandShape -eq 6)
                }
                foreach ($text in @(
                    ('Command2: ' + $quoted), ('command: ' + $quoted),
                    ('0:000> Command: ' + $quoted), ('prefix Command: ' + $quoted)
                )) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state $text
                    Add-HermesStartupLine $state $state.FilterExpected.Cpr
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.CprCommandShape -eq 9 -and $observed.CprFilter -eq 1)
                }
            }
            128 {
                . $controlCommandDriver
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Assert-Control ((New-HermesStartupObservations $state).CprCommandShape -eq 0)
                Add-HermesStartupLine $state $state.FilterExpected.Cpr
                Assert-Control ((New-HermesStartupObservations $state).CprCommandShape -eq 1)
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Assert-Control ((New-HermesStartupObservations $state).CprCommandShape -eq 0 `
                    -and (New-HermesStartupObservations $state).CprFilter -eq 1)
                Add-HermesStartupLine $state 'epr - Exit process - break'
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.CprCommandShape -eq 7 -and $observed.EprCommandShape -eq 0)
                Add-HermesStartupLine $state 'ct - Create thread - ignore'
                Assert-Control ((New-HermesStartupObservations $state).EprCommandShape -eq 7)
            }
            129 {
                . $controlCommandDriver
                $state.Out = New-HermesStartupStream
                $state.Err = New-HermesStartupStream
                $text = "cpr - Create process - break`n" + $state.FilterExpected.Cpr + "`n"
                Add-HermesStartupText $state $state.Err $text $false
                Assert-Control ((New-HermesStartupObservations $state).CprCommandShape -eq 0)
                Add-HermesStartupLine $state 'cpr - Create process - break'
                $state.AcceptedUtf8Bytes = [long]8388607
                $before = (New-HermesStartupObservations $state) | ConvertTo-Json -Compress
                Add-HermesStartupText $state $state.Out "`n" $true
                Add-HermesStartupText $state $state.Out ($state.FilterExpected.Cpr + "`n") $true
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and ((New-HermesStartupObservations $state) | ConvertTo-Json -Compress) -ceq $before)
                foreach ($value in @(-1, 10, 8388608, [long]9, $true, $null,
                        'private S-1-5-21-123', @{ text = 'Command: "secret"' })) {
                    $state.Observations.CprCommandShape = $value
                    $state.Observations.EprCommandShape = $value
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.CprCommandShape -is [int] `
                        -and $observed.CprCommandShape -eq 0 `
                        -and $observed.EprCommandShape -is [int] -and $observed.EprCommandShape -eq 0)
                }
            }
            130 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture
                $before = New-HermesStartupReport $state $true $true
                $state.FilterExpected.Cpr = 'Command: "$$<C:\private\secret S-1-5-21-123 --command.cdb"'
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Add-HermesStartupLine $state ('Command1: ' + $state.FilterExpected.Cpr.Substring(9))
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.CprCommandShape -eq 3 `
                    -and $report.observations.CprFilter -eq 1 `
                    -and $report.completed -eq 1 -and $report.armingVerified -eq 1 `
                    -and $report.firstFailureKind -eq 1 -and $report.cleanupComplete -eq 1)
                foreach ($name in $before.Keys | Where-Object { $_ -cne 'observations' }) {
                    Assert-Control ($report[$name] -ceq $before[$name])
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|secret|S-1-|--command|\$\$|Command1:')
            }
            131 {
                $script:controlCommandFault = 2
                $script:controlCommandFaultName = 'startup.cdb'
                . $controlCommandDriver
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Add-HermesStartupLine $state (
                    'Command: "$$<' + (Join-Path $root 'startup-cpr.cdb') + '"')
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($state.FilterExpected.Cpr -ceq '' `
                    -and $report.observations.CprCommandShape -eq 6 `
                    -and $report.observations.CprFilter -eq 1 -and $report.status -eq 8 `
                    -and $report.error -eq 13 -and $report.completed -eq 0 `
                    -and $report.armingVerified -eq 0 -and $script:controlLaunchBindings -eq 0)
            }
            132 {
                . $controlCommandDriver
                $source = '$$<D:\a\_temp\hermes\startup-cpr.cdb'
                $state.FilterExpected.Cpr = 'Command: "' + $source + '"'
                $doubled = '$$<D:\\a\\_temp\\hermes\\startup-cpr.cdb'
                $payloads = @(
                    $source, ('"' + $source + '"'), ('\"' + $source + '\"'), ('""' + $source + '""'),
                    $doubled, ('"' + $doubled + '"'), ('\"' + $doubled + '\"')
                )
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Update-HermesStartupRows $state ($targetRow + "`n")
                for ($index = 0; $index -lt $payloads.Count; $index++) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state ('Command: ' + $payloads[$index])
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.CprCommandTransform -eq ($index + 1) `
                        -and $observed.CprPayloadLength -eq $payloads[$index].Length `
                        -and $observed.CprSourceLength -eq $source.Length `
                        -and $observed.CprFilter -eq $(if ($index -eq 1) { 9 } else { 1 }) `
                        -and $observed.ArmPresent -eq 0 -and $observed.BlockPresent -eq 0 `
                        -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                }
            }
            133 {
                . $controlCommandDriver
                $source = '$$<D:\a\_temp\hermes\startup-cpr.cdb'
                $state.FilterExpected.Cpr = 'Command: "' + $source + '"'
                $removed = '$$<D:a_temphermesstartup-cpr.cdb'
                $known = '$$<D:' + [string][char]7 + '\_temp\hermes\startup-cpr.cdb'
                $all = '$$<D:' + [string][char]7 + '_temphermesstartup-cpr.cdb'
                $payloads = @(
                    $removed, ('"' + $removed + '"'), $known, ('"' + $known + '"'),
                    $all, ('"' + $all + '"')
                )
                for ($index = 0; $index -lt $payloads.Count; $index++) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state ('Command: ' + $payloads[$index])
                    $report = New-HermesStartupReport $state $true $true
                    Assert-Control ($report.observations.CprCommandTransform -eq ($index + 8) `
                        -and $report.observations.CprPayloadLength -eq $payloads[$index].Length `
                        -and $report.observations.CprSourceLength -eq $source.Length `
                        -and $report.observations.CprCommandShape -eq 6 `
                        -and $report.observations.CprFilter -eq 1 `
                        -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                        -and $report.firstFailureKind -eq 0)
                }
                $escaped = '\a\b\f\n\r\t\v\\\"' + "\'"
                $decoded = ConvertFrom-HermesStartupCommandEscapes $escaped $false
                $expected = -join (@(7, 8, 12, 10, 13, 9, 11) | ForEach-Object { [char]$_ })
                Assert-Control ($decoded -ceq ($expected + '\"' + "'"))
            }
            134 {
                . $controlCommandDriver
                $source = '$$><D:\a\_temp\hermes\startup-epr.cdb'
                $state.FilterExpected.Epr = 'Command: "' + $source + '"'
                $single = '$><D:\a\_temp\hermes\startup-epr.cdb'
                $four = '$$$$><D:\a\_temp\hermes\startup-epr.cdb'
                $none = '><D:\a\_temp\hermes\startup-epr.cdb'
                $payloads = @($single, ('"' + $single + '"'), $four, ('"' + $four + '"'),
                    $none, ('"' + $none + '"'))
                for ($index = 0; $index -lt $payloads.Count; $index++) {
                    Add-HermesStartupLine $state 'epr - Exit process - break'
                    Add-HermesStartupLine $state ('Command1: ' + $payloads[$index])
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.EprCommandTransform -eq ($index + 14) `
                        -and $observed.EprPayloadLength -eq $payloads[$index].Length `
                        -and $observed.EprSourceLength -eq $source.Length `
                        -and $observed.EprCommandShape -eq 6 -and $observed.EprFilter -eq 1 `
                        -and $observed.CprCommandTransform -eq 0 `
                        -and -not (Test-HermesStartupArm $state))
                }
            }
            135 {
                . $controlCommandDriver
                $source = '$$<D:\a\_temp\hermes\startup-cpr.cdb'
                $state.FilterExpected.Cpr = 'Command: "' + $source + '"'
                foreach ($payload in @(
                    $source.Replace('startup-cpr', 'startup-epr'),
                    $source.Replace('startup-cpr', 'STARTUP-CPR'),
                    ('"' + $source + '";g'), ('"' + $source.Replace('$$<', '$$><') + '"'),
                    'CN=private S-1-5-21-123 --command', '""', ''
                )) {
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state ('Command: ' + $payload)
                    $report = New-HermesStartupReport $state $true $true
                    Assert-Control ($report.observations.CprCommandTransform -eq 0 `
                        -and $report.observations.CprPayloadLength -eq $payload.Length `
                        -and $report.observations.CprSourceLength -eq $source.Length `
                        -and $report.completed -eq 0 -and $report.armingVerified -eq 0)
                    Assert-Control (($report | ConvertTo-Json -Depth 4 -Compress) `
                        -notmatch 'CN=|private|S-1-|--command|startup|Command:|D:')
                }
                Assert-Control ((Get-HermesStartupCommandTransform 'abc' 'abc') -eq 1 `
                    -and (Get-HermesStartupCommandTransform 'abc' '"abc"') -eq 2 `
                    -and (Get-HermesStartupCommandTransform $source ('x' * 4097)) -eq 0)
            }
            136 {
                . $controlCommandDriver
                $state.Out = New-HermesStartupStream
                $state.Err = New-HermesStartupStream
                $source = '$$<D:\a\_temp\hermes\startup-cpr.cdb'
                $state.FilterExpected.Cpr = 'Command: "' + $source + '"'
                $payload = '"$$<D:\\a\\_temp\\hermes\\startup-cpr.cdb"'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Update-HermesStartupRows $state ($targetRow + "`n")
                Add-HermesStartupText $state $state.Out (('x' * 4095) + "`n") $true
                $text = "cpr - Create process - break`r`nCommand: " + $payload + "`r`n"
                for ($index = 0; $index -lt $text.Length; $index += 3) {
                    Add-HermesStartupText $state $state.Out (
                        $text.Substring($index, [Math]::Min(3, $text.Length - $index))) $true
                }
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.CprCommandTransform -eq 6 `
                    -and $report.observations.CprCommandShape -eq 6 `
                    -and $report.observations.CprPayloadLength -eq $payload.Length `
                    -and $report.observations.CprSourceLength -eq $source.Length `
                    -and $report.outKept -eq 4096 -and $report.outDropped -gt 0 `
                    -and $report.helperMatched -eq 1 -and $report.targetMatched -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                    -and $report.observations.Marker1 -eq 0 -and $report.completed -eq 0)
                Assert-Control (($report | ConvertTo-Json -Depth 4 -Compress) `
                    -notmatch 'startup-cpr|_temp|D:|\$\$|Command:|xxxxx')
            }
            137 {
                . $controlCommandDriver
                $state.Out = New-HermesStartupStream
                $state.Err = New-HermesStartupStream
                $text = "cpr - Create process - break`n" + $state.FilterExpected.Cpr + "`n"
                Add-HermesStartupText $state $state.Err $text $false
                Assert-Control ((New-HermesStartupObservations $state).CprPayloadLength -eq 0)
                Add-HermesStartupLine $state 'cpr - Create process - break'
                $state.AcceptedUtf8Bytes = [long]8388607
                $before = (New-HermesStartupObservations $state) | ConvertTo-Json -Compress
                Add-HermesStartupText $state $state.Out "`n" $true
                Add-HermesStartupText $state $state.Out ($state.FilterExpected.Cpr + "`n") $true
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and ((New-HermesStartupObservations $state) | ConvertTo-Json -Compress) -ceq $before)
                foreach ($name in @('CprCommandTransform', 'EprCommandTransform',
                        'CprPayloadLength', 'EprPayloadLength', 'CprSourceLength', 'EprSourceLength')) {
                    $maximum = if ($name.EndsWith('Transform')) { 19 } else { 4096 }
                    foreach ($value in @(-1, ($maximum + 1), [long]1, 1.5, $true, $null,
                            'private S-1-5-21-123', @{ text = 'secret' })) {
                        $state.Observations[$name] = $value
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($observed[$name] -is [int] -and $observed[$name] -eq 0)
                    }
                    $state.Observations[$name] = $maximum
                    Assert-Control ((New-HermesStartupObservations $state)[$name] -eq $maximum)
                }
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|secret|S-1-' -and $report.status -eq 7)
            }
            138 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture
                $before = New-HermesStartupReport $state $true $true
                $state.FilterExpected.Cpr = 'Command: "$$<D:\a\_temp\private S-1-5-21-123.cdb"'
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Add-HermesStartupLine $state 'Command: "$<D:\a\_temp\private S-1-5-21-123.cdb"'
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.CprCommandTransform -eq 15 `
                    -and $report.observations.CprCommandShape -eq 6 -and $report.observations.CprFilter -eq 1 `
                    -and $report.completed -eq 1 -and $report.armingVerified -eq 1 `
                    -and $report.firstFailureKind -eq 1 -and $report.cleanupComplete -eq 1)
                foreach ($name in $before.Keys | Where-Object { $_ -cne 'observations' }) {
                    Assert-Control ($report[$name] -ceq $before[$name])
                }
                Assert-Control (($report | ConvertTo-Json -Depth 4 -Compress) `
                    -notmatch 'private|S-1-|_temp|\$\$|D:|Command:')
            }
            139 {
                $script:controlCommandFault = 2
                $script:controlCommandFaultName = 'startup.cdb'
                . $controlCommandDriver
                Add-HermesStartupLine $state 'cpr - Create process - break'
                $payload = '"$$<D:\a\_temp\hermes\startup-cpr.cdb"'
                Add-HermesStartupLine $state ('Command: ' + $payload)
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.CprPayloadLength -eq $payload.Length `
                    -and $observed.CprSourceLength -eq 0 -and $observed.CprCommandTransform -eq 0 `
                    -and $state.FilterExpected.Cpr -ceq '' -and -not (Test-HermesStartupArm $state))
                Add-HermesStartupLine $state 'cpr - Create process - break'
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.CprPayloadLength -eq 0 -and $observed.CprSourceLength -eq 0 `
                    -and $observed.CprCommandTransform -eq 0 -and $observed.CprCommandShape -eq 0)
                Add-HermesStartupLine $state ('Command: ' + ('x' * 4096))
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.status -eq 7 -and $report.completed -eq 0 `
                    -and $report.observations.CprPayloadLength -eq 0 `
                    -and $report.observations.CprCommandTransform -eq 0 `
                    -and $report.armingVerified -eq 0 -and $script:controlLaunchBindings -eq 0)
            }
            { $_ -in 140, 141 } {
                $dropUnknown = $case -eq 141
                foreach ($example in @(
                    @{ Text = '\A\a'; Upper = '\A'; Code = 7 },
                    @{ Text = '\B\b'; Upper = '\B'; Code = 8 },
                    @{ Text = '\F\f'; Upper = '\F'; Code = 12 },
                    @{ Text = '\N\n'; Upper = '\N'; Code = 10 },
                    @{ Text = '\R\r'; Upper = '\R'; Code = 13 },
                    @{ Text = '\T\t'; Upper = '\T'; Code = 9 },
                    @{ Text = '\V\v'; Upper = '\V'; Code = 11 }
                )) {
                    $prefix = if ($dropUnknown) { $example.Upper.Substring(1) } else { $example.Upper }
                    $expected = $prefix + [string][char]$example.Code
                    Assert-Control ((ConvertFrom-HermesStartupCommandEscapes `
                        $example.Text $dropUnknown) -ceq $expected)
                }
                $unknown = '\Q\q\Z\z\_'
                $expected = if ($dropUnknown) { 'QqZz_' } else { $unknown }
                Assert-Control ((ConvertFrom-HermesStartupCommandEscapes $unknown $dropUnknown) -ceq $expected)
                foreach ($example in @(
                    @{ Text = '\\'; Expected = '\' },
                    @{ Text = '\"'; Expected = '"' },
                    @{ Text = "\'"; Expected = "'" },
                    @{ Text = '\?'; Expected = '?' },
                    @{ Text = '\'; Expected = '\' }
                )) {
                    Assert-Control ((ConvertFrom-HermesStartupCommandEscapes `
                        $example.Text $dropUnknown) -ceq $example.Expected)
                }
            }
            { $_ -in 142, 143 } {
                $dropUnknown = $case -eq 143
                foreach ($name in @('Cpr', 'Epr')) {
                    $suffix = $name.ToLowerInvariant() + '.cdb'
                    $token = if ($name -ceq 'Cpr') { '$$<' } else { '$$><' }
                    $source = $token + 'C:\Users\Example\AppData\Local\Temp\hermes\trace\startup-' + $suffix
                    $state.FilterExpected[$name] = 'Command: "' + $source + '"'
                    $normalised = if ($dropUnknown) {
                        $token + 'C:UsersExampleAppDataLocalTemphermes' + [string][char]9 + 'racestartup-' + $suffix
                    } else {
                        $token + 'C:\Users\Example\AppData\Local\Temp\hermes' + [string][char]9 + 'race\startup-' + $suffix
                    }
                    $appData = if ($dropUnknown) { 'AppData' } else { '\AppData' }
                    $temp = if ($dropUnknown) { 'Temp' } else { '\Temp' }
                    $misCased = $normalised.Replace($appData, ([string][char]7 + 'ppData')).
                        Replace($temp, ([string][char]9 + 'emp'))
                    Assert-Control ((Get-HermesStartupCommandTransform $source $misCased) -eq 0 `
                        -and (Get-HermesStartupCommandTransform $source ('"' + $misCased + '"')) -eq 0)
                    $header = if ($name -ceq 'Cpr') {
                        'cpr - Create process - break'
                    } else { 'epr - Exit process - break' }
                    $payloads = @($normalised, ('"' + $normalised + '"'))
                    for ($index = 0; $index -lt $payloads.Count; $index++) {
                        Add-HermesStartupLine $state $header
                        Add-HermesStartupLine $state ('Command: ' + $payloads[$index])
                        $observed = New-HermesStartupObservations $state
                        $ordinal = $(if ($dropUnknown) { 12 } else { 10 }) + $index
                        Assert-Control ($observed[$name + 'CommandTransform'] -eq $ordinal `
                            -and $observed[$name + 'PayloadLength'] -eq $payloads[$index].Length `
                            -and $observed[$name + 'SourceLength'] -eq $source.Length `
                            -and $observed[$name + 'CommandShape'] -eq 6 `
                            -and $observed[$name + 'Filter'] -eq 1 `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                    }
                }
                $report = New-HermesStartupReport $state $true $true
                foreach ($field in @('CprCommandTransform', 'EprCommandTransform',
                        'CprPayloadLength', 'EprPayloadLength', 'CprSourceLength', 'EprSourceLength')) {
                    Assert-Control ($report.observations[$field] -is [int])
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($report.observations.Count -eq 56 `
                    -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'Users|Example|AppData|Temp|Command:|\$\$' `
                    -and $report.armingVerified -eq 0 -and $report.completed -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $script:controlLaunchBindings -eq 0)
            }
            { $_ -in (144..147 + 157..164 + 187..190) } {
                & {
                    # Stub only Windows path IO; source generation, publication,
                    # retained pins, hashes and launch/failure bindings stay real.
                    function Join-Path {
                        param([string]$Path, [string]$ChildPath)
                        Assert-Control ($Path -cin @($root, $kits))
                        return $Path + '\' + $ChildPath
                    }
                    function Assert-TraceCommandPath {
                        param([string]$Path)
                        Assert-Control ($Path -cin @($extension,
                            ($root + '\startup.cdb'), ($root + '\startup-cpr.cdb'),
                            ($root + '\startup-ibp.cdb'), ($root + '\startup-epr.cdb')))
                        return $Path
                    }
                    $root = 'D:\a\_temp\hermes-native-startup-trace'
                    $encodedRoot = 'D:\\a\\_temp\\hermes-native-startup-trace'
                    if ($case -eq 145) {
                        $root = 'C:\Users\Example Name\AppData\Local\Temp\hermes\trace'
                        $encodedRoot = 'C:\\Users\\Example Name\\AppData\\Local\\Temp\\hermes\\trace'
                    }
                    $kits = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64'
                    $relative = switch ($case) {
                        188 { '\winext\exts.dll' }; 189 { '\exts.dll' }; default { '\winxp\exts.dll' }
                    }
                    $extension = $kits + $relative
                    $symbols = $root + '\symbols'
                    $script:controlArms = 0
                    if ($case -in 146, 147) {
                        $script:controlCommandFault = $case - 137
                        $script:controlCommandFaultName = 'startup.cdb'
                    }
                    if ($case -eq 190) {
                        $script:controlCommandFault = 12
                        $script:controlCommandFaultName = 'startup.cdb'
                    }
                    . $controlCommandDriver
                    $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                    $expectedLines = @(
                        ('.load "' + $extension.Replace('\', '\\') + '"'),
                        '.chain',
                        '.printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid',
                        ('sxe -c "$$><' + (Join-Path $root 'startup-ibp.cdb').Replace('\', '\\') + '" ibp'),
                        ('sxe -c "$$<' + $encodedRoot + '\\startup-cpr.cdb" cpr'),
                        ('sxe -c "$$><' + $encodedRoot + '\\startup-epr.cdb" epr'),
                        'sx', 'g'
                    )
                    $expectedText = ($expectedLines -join "`n") + "`n"
                    Assert-Control ((New-TraceCommands $extension) -ceq $expectedText `
                        -and $script:controlCommandWrites.Count -eq 4 `
                        -and $script:controlCommandPins.Count -eq 4 `
                        -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                        -and $report.cleanupFault -eq 0 -and $report.completed -eq 0 `
                        -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                        -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                    $path = $root + '\startup.cdb'
                    if ($case -eq 190) {
                        $actual = [Text.Encoding]::ASCII.GetString($script:controlCommandFiles[$path])
                        $actualLines = @($actual -split "`n")
                        Assert-Control ($actual -ceq $expectedText.Replace('.load "C:\\', '.load "C:\ ') `
                            -and $actual.Length -eq $expectedText.Length -and $actual -cne $expectedText `
                            -and ($actualLines[1..7] -join "`n") -ceq ($expectedLines[1..7] -join "`n") `
                            -and $script:controlCommandHashes -eq 4 -and $script:controlLaunchBindings -eq 0 `
                            -and $null -eq $script:controlStartInfo -and $state.TraceFaulted `
                            -and $report.tracePoint -eq 24 -and $report.status -eq 8 -and $report.error -eq 13 `
                            -and $report.commandSha256 -ceq '' -and $report.createCommandSha256 -ceq '' `
                            -and $report.exitCommandSha256 -ceq '' -and $state.ExtensionExpected -ceq '' `
                            -and $state.FilterExpected.Cpr -ceq '' -and $state.FilterExpected.Epr -ceq '' `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                        return
                    }
                    if ($case -in 146, 147) {
                        $unsafeText = if ($case -eq 146) {
                            $expectedText.Replace('\\', '\')
                        } else { $expectedText.Replace('\\', '') }
                        Assert-Control ([Text.Encoding]::ASCII.GetString(
                                $script:controlCommandFiles[$path]) -ceq $unsafeText `
                            -and $unsafeText -cne $expectedText `
                            -and $script:controlCommandHashes -eq 3 `
                            -and $script:controlLaunchBindings -eq 0 `
                            -and $null -eq $script:controlStartInfo -and $state.TraceFaulted `
                            -and $report.tracePoint -eq 24 -and $report.status -eq 8 `
                            -and $report.error -eq 13 -and $report.commandSha256 -ceq '' `
                            -and $report.createCommandSha256 -ceq '' -and $report.exitCommandSha256 -ceq '' `
                            -and $state.FilterExpected.Cpr -ceq '' -and $state.FilterExpected.Epr -ceq '' `
                            -and $state.ExtensionExpected -ceq '' `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                        return
                    }
                    $sources = New-TraceEventCommands
                    $sources['startup.cdb'] = $expectedText
                    $hashFields = @{
                        'startup.cdb' = 'commandSha256'
                        'startup-cpr.cdb' = 'createCommandSha256'
                        'startup-epr.cdb' = 'exitCommandSha256'
                        'startup-ibp.cdb' = 'initialCommandSha256'
                    }
                    foreach ($source in $sources.GetEnumerator()) {
                        $sourcePath = $root + '\' + $source.Key
                        $bytes = [Text.Encoding]::ASCII.GetBytes($source.Value)
                        $expectedHash = [Convert]::ToHexString(
                            [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                        Assert-Control ([Convert]::ToHexString($script:controlCommandFiles[$sourcePath]) `
                                -ceq [Convert]::ToHexString($bytes) `
                            -and $script:controlCommandReadOnly[$sourcePath] `
                            -and $script:controlCommandPins -ccontains $sourcePath `
                            -and $report[$hashFields[$source.Key]] -ceq $expectedHash)
                    }
                    Assert-Control ($script:controlCommandHashes -eq 4 `
                        -and $script:controlLaunchBindings -eq 1 -and $report.error -eq 0 `
                        -and $state.ExtensionExpected -ceq $extension `
                        -and $script:controlStartInfo.ArgumentList[4] -ceq $path)
                    if ($case -in 187..189) {
                        $actualLines = @([Text.Encoding]::ASCII.GetString(
                            $script:controlCommandFiles[$path]) -split "`n")
                        Assert-Control ($actualLines[0] -cmatch '\A\.load "(?<payload>[^"]+)"\z')
                        $payload = $Matches.payload
                        Assert-Control ($payload -ceq $extension.Replace('\', '\\') `
                            -and $payload -cne $extension -and $payload -cmatch '\A(?:[^\\]|\\\\)*\z' `
                            -and $payload.Contains('\\10\\', [StringComparison]::Ordinal) `
                            -and $payload.Contains('\\x64\\', [StringComparison]::Ordinal) `
                            -and $actualLines.Count -eq 9 `
                            -and ($actualLines[1..7] -join "`n") -ceq ($expectedLines[1..7] -join "`n") `
                            -and @($actualLines | Where-Object { $_ -like '.load *' }).Count -eq 1 `
                            -and $state.ExtensionExpected -ceq $extension)
                        foreach ($dropUnknown in @($false, $true)) {
                            # Pure paired-backslash decoding model, NOT native
                            # execution or a claim about octal/hex escape rules.
                            $decoded = ConvertFrom-HermesStartupCommandEscapes $payload $dropUnknown
                            Assert-Control ($decoded -ceq $extension `
                                -and $decoded.Contains('\10\', [StringComparison]::Ordinal) `
                                -and $decoded.Contains('\x64\', [StringComparison]::Ordinal) `
                                -and $decoded -cnotmatch '[\x00-\x1f]')
                        }
                    }
                    $state.Out = New-HermesStartupStream
                    $chain = "Extension DLL search Path:`r`n    $extension`r`n" +
                        "Extension DLL chain:`r`n    ${extension}: image 10.0.1.2, API 1.0.0`r`n"
                    for ($offset = 0; $offset -lt $chain.Length; $offset += 7) {
                        Add-HermesStartupText $state $state.Out (
                            $chain.Substring($offset, [Math]::Min(7, $chain.Length - $offset))) $true
                    }
                    Assert-Control ((New-HermesStartupObservations $state).SdkSelectedExtensionChain -eq 2 `
                        -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                    Update-HermesStartupRows $state ($targetRow + "`n")
                    foreach ($index in 4..5) {
                        $name = if ($index -eq 4) { 'Cpr' } else { 'Epr' }
                        $suffix = 'startup-' + $name.ToLowerInvariant() + '.cdb'
                        $token = if ($name -ceq 'Cpr') { '$$<' } else { '$$><' }
                        $logical = $token + $root + '\' + $suffix
                        $line = $expectedLines[$index]
                        Assert-Control ($line -cmatch '\Asxe -c "(?<payload>[^"]+)" (?:cpr|epr)\z')
                        $encoded = $Matches.payload
                        # Model quoted-command decoding, not native execution:
                        # paired literal backslashes must recover the exact path.
                        foreach ($dropUnknown in @($false, $true)) {
                            Assert-Control ((ConvertFrom-HermesStartupCommandEscapes `
                                $encoded $dropUnknown) -ceq $logical)
                        }
                        Assert-Control ($state.FilterExpected[$name] -ceq ('Command: "' + $logical.Replace('\', '\\') + '"'))
                        $header = if ($name -ceq 'Cpr') {
                            'cpr - Create process - break'
                        } else { 'epr - Exit process - break' }
                        foreach ($payload in @($logical.Replace('\', ''), $encoded, $logical)) {
                            Add-HermesStartupLine $state $header
                            Add-HermesStartupLine $state ('Command: "' + $payload + '"')
                            $observed = New-HermesStartupObservations $state
                            $exact = $payload -ceq $encoded
                            Assert-Control ($observed[$name + 'CommandShape'] -eq $(if ($exact) { 1 } else { 6 }) `
                                -and $observed[$name + 'Filter'] -eq $(if ($exact) { 9 } else { 1 }) `
                                -and $observed[$name + 'SourceLength'] -eq $encoded.Length `
                                -and $observed[$name + 'PayloadLength'] -eq ($payload.Length + 2) `
                                -and $observed[$name + 'CommandTransform'] -eq $(
                                    if ($exact) { 2 } elseif ($payload -ceq $logical) { 11 } else { 9 }) `
                                -and $observed.ArmPresent -eq 0 -and $observed.BlockPresent -eq 0 `
                                -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                        }
                    }
                    if ($case -in 157..164) {
                        $state.Err = New-HermesStartupStream
                        $variants = @($extension, ('"' + $extension + '"'),
                            $extension.Replace('\', ''), ('"' + $extension.Replace('\', '') + '"'),
                            $extension.Replace('\', '\\'), ('"' + $extension.Replace('\', '\\') + '"'),
                            $extension.Replace('exts.dll', 'EXTS.dll'), $extension)
                        $library = $variants[$case - 157]
                        $shape = if ($case -eq 164) { 0 } else { $case - 156 }
                        if ($case -eq 164) { $state.ExtensionExpected = '' }
                        $text = 'The call to LoadLibrary(' + $library + ') failed, Win32 error 0n126' + "`r`n"
                        for ($offset = 0; $offset -lt $text.Length; $offset += 7) {
                            Add-HermesStartupText $state $state.Err (
                                $text.Substring($offset, [Math]::Min(7, $text.Length - $offset))) $false
                        }
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($observed.SdkCommandErrors -eq 1 `
                            -and $observed.SdkLoadLibraryWin32Error -eq 126 `
                            -and $observed.SdkLoadLibraryPathShape -eq $shape)
                        Add-HermesStartupLine $state (
                            'The call to LoadLibrary(C:\private\other.dll) failed, Win32 error 0n123')
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($observed.SdkLoadLibraryWin32Error -eq 126 `
                            -and $observed.SdkLoadLibraryPathShape -eq $shape)
                    }
                    $report = New-HermesStartupReport $state $true $true
                    Assert-Control ($report.armingVerified -eq 0 -and $report.completed -eq 0 `
                        -and $report.firstFailureKind -eq 0 -and $report.nativeAcceptance -eq 0 `
                        -and $script:controlArms -eq 0)
                    $json = $report | ConvertTo-Json -Depth 4 -Compress
                    Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                        -and $json -notmatch 'Program Files|Windows Kits|private|EXTS\.dll|C:|D:')
                }
            }
            { $_ -in 148..151 } {
                $message = @(
                    'The call to LoadLibrary(C:\private SDK (x86)\exts.dll) failed, Win32 error 0n126',
                    'No export gflag found',
                    'Unable to read PEB',
                    '*** ERROR: Symbols could not be loaded for ntdll.dll'
                )[$case - 148]
                $expected = 1 -shl ($case - 148)
                foreach ($parse in @($true, $false)) {
                    $state.Out.Hash.Dispose()
                    $state.Err.Hash.Dispose()
                    $state = New-ControlOpenBlock
                    Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                    $stream = if ($parse) { $state.Out } else { $state.Err }
                    $text = $message + "`r`n"
                    for ($index = 0; $index -lt $text.Length; $index += 3) {
                        Add-HermesStartupText $state $stream (
                            $text.Substring($index, [Math]::Min(3, $text.Length - $index))) $parse
                    }
                    Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=22'
                    $report = New-HermesStartupReport $state $false $false
                    $observed = $report.observations
                    Assert-Control ($observed.SdkCommandErrors -eq $expected `
                        -and $observed.CreateEventAccepted -eq 1 -and $observed.ArmKind -eq 1 `
                        -and $observed.ArmCurrentMatchesTarget -eq 1 -and $observed.ArmEventMatchesTarget -eq 1 `
                        -and $observed.FlagsSeen -eq 0 -and $observed.FlagsAccepted -eq 0 `
                        -and $observed.ArmFlag2 -eq 0 -and $observed.ParserUnknown -eq 1 `
                        -and $observed.ParserProtocol -eq 0 -and -not (Test-HermesStartupArm $state) `
                        -and -not $state.Acked -and $report.armingVerified -eq 0 `
                        -and $report.completed -eq 0 -and $report.firstFailureKind -eq 0 `
                        -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                    $json = $report | ConvertTo-Json -Depth 4 -Compress
                    Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                        -and $json -notmatch 'private|exts|LoadLibrary\(|gflag|PEB|ntdll|Symbols')
                }
            }
            152 {
                foreach ($line in @(
                    'No export other found', 'no export gflag found', 'No export Gflag found',
                    'No export gflag found trailing', '0:000> No export gflag found',
                    '.echo No export gflag found', 'prefix Unable to read PEB',
                    'Unable to read PEB trailing', 'Unable to read other memory',
                    'The call to LoadLibrary(C:\private\exts.dll) succeeded, Win32 error 0n126',
                    'The call to LoadLibrary(C:\private\exts.dll) failed, Win32 error unknown',
                    '*** ERROR: Symbols could not be loaded for private.dll',
                    '*** WARNING: Symbols could not be loaded for ntdll.dll',
                    'CN=private S-1-5-21-123 --command'
                )) {
                    Add-HermesStartupLine $state $line
                    Assert-Control ((New-HermesStartupObservations $state).SdkCommandErrors -eq 0)
                }
                Assert-Control (-not (Test-HermesStartupArm $state) -and -not $state.Acked)
            }
            153 {
                foreach ($line in @(
                    'No export gflag found', 'Unable to read PEB',
                    'The call to LoadLibrary(C:\private\exts.dll) failed, Win32 error 0n2',
                    '*** ERROR: Module load completed but symbols could not be loaded for ntdll.dll'
                )) {
                    Add-HermesStartupLine $state $line
                    Add-HermesStartupLine $state $line
                }
                Assert-Control ((New-HermesStartupObservations $state).SdkCommandErrors -eq 15)
                foreach ($value in @(-1, 16, [long]1, 1.5, $true, $null,
                        'private S-1-5-21-123', @{ text = 'LoadLibrary' })) {
                    $state.Observations.SdkCommandErrors = $value
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.SdkCommandErrors -is [int] -and $observed.SdkCommandErrors -eq 0)
                }
                foreach ($value in 0..15) {
                    $state.Observations.SdkCommandErrors = $value
                    Assert-Control ((New-HermesStartupObservations $state).SdkCommandErrors -eq $value)
                }
            }
            154 {
                Add-HermesStartupText $state $state.Err ('x' * 4096) $false
                Add-HermesStartupText $state $state.Err "No export gflag found`n" $false
                Assert-Control ($state.Bounds -eq 0 `
                    -and (New-HermesStartupObservations $state).SdkCommandErrors -eq 0)
                Add-HermesStartupText $state $state.Err 'No export gflag found' $false
                Assert-Control ((New-HermesStartupObservations $state).SdkCommandErrors -eq 0)
                Add-HermesStartupText $state $state.Err "`n" $false
                Assert-Control ((New-HermesStartupObservations $state).SdkCommandErrors -eq 2)
                $state.AcceptedUtf8Bytes = [long]8388607
                $before = (New-HermesStartupObservations $state) | ConvertTo-Json -Compress
                Add-HermesStartupText $state $state.Err "`n" $false
                Add-HermesStartupText $state $state.Out "Unable to read PEB`n" $true
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and ((New-HermesStartupObservations $state) | ConvertTo-Json -Compress) -ceq $before)
            }
            155 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture
                $before = New-HermesStartupReport $state $true $true
                Add-HermesStartupLine $state 'No export gflag found'
                $report = New-HermesStartupReport $state $true $true
                foreach ($name in $before.Keys | Where-Object { $_ -cne 'observations' }) {
                    Assert-Control ($report[$name] -ceq $before[$name])
                }
                Assert-Control ($report.observations.SdkCommandErrors -eq 2 `
                    -and $report.armingVerified -eq 1 -and $report.completed -eq 1)
            }
            156 {
                $text = "HERMES_CDB v=1 kind=0 pid=11`nHERMES_CDB v=1 kind=1 pid=22`n" +
                    "Last event: 16.1: Create process 1:16`n" +
                    "Current NtGlobalFlag contents: 0x00000002`nHERMES_CDB v=1 kind=2 pid=22`n" +
                    "No export gflag found`n"
                Add-HermesStartupText $state $state.Err $text $false
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.SdkCommandErrors -eq 2 -and $observed.Marker0 -eq 0 `
                    -and $observed.Marker1 -eq 0 -and $observed.FlagsSeen -eq 0 `
                    -and $observed.FlagsAccepted -eq 0 -and $observed.CreateEventAccepted -eq 0 `
                    -and $observed.ArmPresent -eq 0 -and $observed.ParserUnknown -eq 0 `
                    -and $observed.ParserProtocol -eq 0 -and -not (Test-HermesStartupArm $state))
            }
            165 {
                foreach ($code in @('0', '123', '126', '4294967295', '4294967296', '9999999999',
                        '-1', '+1', '1e3', '126 trailing', '12345678901')) {
                    $state.Observations.SdkCommandErrors = 0
                    $state.Observations.SdkLoadLibraryWin32Error = 0
                    $state.Observations.SdkLoadLibraryPathShape = 0
                    Add-HermesStartupLine $state (
                        'The call to LoadLibrary(C:\private\exts.dll) failed, Win32 error 0n' + $code)
                    $valid = $code -cin @('0', '123', '126', '4294967295')
                    $recognised = $valid -or $code -cin @('4294967296', '9999999999')
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.SdkCommandErrors -eq [int]$recognised `
                        -and $observed.SdkLoadLibraryWin32Error -eq $(if ($valid) { [long]$code } else { 0 }) `
                        -and $observed.SdkLoadLibraryPathShape -eq 0 `
                        -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                }
            }
            166 {
                foreach ($value in @(-1, [long]4294967296, [long]::MaxValue, 1.5, $true, $null,
                        '126', @{ path = 'private' })) {
                    $state.Observations.SdkLoadLibraryWin32Error = $value
                    Assert-Control ((New-HermesStartupObservations $state).SdkLoadLibraryWin32Error -eq 0)
                }
                foreach ($value in @(0, 126, [long]4294967295)) {
                    $state.Observations.SdkLoadLibraryWin32Error = $value
                    Assert-Control ((New-HermesStartupObservations $state).SdkLoadLibraryWin32Error -eq $value)
                }
                foreach ($value in @(-1, 8, [long]1, 1.5, $true, $null, '1', @{ path = 'private' })) {
                    $state.Observations.SdkLoadLibraryPathShape = $value
                    Assert-Control ((New-HermesStartupObservations $state).SdkLoadLibraryPathShape -eq 0)
                }
                foreach ($value in 0..7) {
                    $state.Observations.SdkLoadLibraryPathShape = $value
                    Assert-Control ((New-HermesStartupObservations $state).SdkLoadLibraryPathShape -eq $value)
                }
                foreach ($expected in @('', $null, 1, @{ path = 'private' },
                        ('C:\' + ('x' * 240)), 'C:\private";.echo injected', ('C:\private' + [char]128))) {
                    Assert-Control ((Get-HermesStartupLoadLibraryPathShape $expected 'C:\private\exts.dll') -eq 0)
                }
            }
            167 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture
                $state.ExtensionExpected = 'C:\private\exts.dll'
                $before = New-HermesStartupReport $state $true $true
                Add-HermesStartupLine $state (
                    'The call to LoadLibrary("C:\private\exts.dll") failed, Win32 error 0n123')
                $report = New-HermesStartupReport $state $true $true
                foreach ($name in $before.Keys | Where-Object { $_ -cne 'observations' }) {
                    Assert-Control ($report[$name] -ceq $before[$name])
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($report.observations.SdkLoadLibraryWin32Error -eq 123 `
                    -and $report.observations.SdkLoadLibraryPathShape -eq 2 `
                    -and $report.completed -eq 1 -and $report.armingVerified -eq 1 `
                    -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|exts\.dll|LoadLibrary\(|C:')
            }
            168 {
                $state.AcceptedUtf8Bytes = [long]8388607
                $before = (New-HermesStartupObservations $state) | ConvertTo-Json -Compress
                Add-HermesStartupText $state $state.Err "`n" $false
                Add-HermesStartupText $state $state.Err (
                    'The call to LoadLibrary(C:\private\exts.dll) failed, Win32 error 0n126' + "`n") $false
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and ((New-HermesStartupObservations $state) | ConvertTo-Json -Compress) -ceq $before)
            }
            169 {
                $script:controlCommandFault = 11
                $script:controlCommandFaultName = 'startup.cdb'
                . $controlCommandDriver
                $path = Join-Path $root 'startup.cdb'
                $expected = New-TraceCommands $extension
                $actual = [Text.Encoding]::ASCII.GetString($script:controlCommandFiles[$path])
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($actual -ceq $expected.Replace("`n.chain`n", "`n.echo `n") `
                    -and $actual -cne $expected -and $script:controlCommandWrites.Count -eq 4 `
                    -and $script:controlCommandPins.Count -eq 4 -and $script:controlCommandHashes -eq 4 `
                    -and $script:controlLaunchBindings -eq 0 -and $script:controlStops -eq 1 `
                    -and $script:controlDisposed -eq 1 -and $state.TraceFaulted `
                    -and $report.tracePoint -eq 24 -and $report.error -eq 13 `
                    -and $state.ExtensionExpected -ceq '' -and $state.CommandSha256 -ceq '' `
                    -and $state.CreateCommandSha256 -ceq '' -and $state.ExitCommandSha256 -ceq '' `
                    -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.observations.SdkSelectedExtensionChain -eq 0 `
                    -and $report.observations.SdkLoadLibraryRelation -eq 0 `
                    -and $report.observations.SdkLoadLibraryForm -eq 0)
            }
            170 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $paths = @($state.ExtensionExpected, $state.ExtensionExpected.ToUpperInvariant(),
                    $state.ExtensionExpected.Replace('\', '/'),
                    $state.ExtensionExpected.Replace('\', '/').ToUpperInvariant())
                foreach ($parse in @($true, $false)) {
                    for ($variant = 0; $variant -lt $paths.Count; $variant++) {
                        $state.ChainPending = $false
                        $state.Observations.SdkSelectedExtensionChain = 0
                        $text = "Extension DLL search Path:`r`n    $($paths[$variant])`r`n" +
                            "Extension DLL chain:`r`n    $($paths[$variant]): image 10.0.1.2, API 1.0.0, built Sun Nov 24 00:38:52 2019`r`n"
                        $stream = if ($parse) { $state.Out } else { $state.Err }
                        for ($offset = 0; $offset -lt $text.Length; $offset += 11) {
                            Add-HermesStartupText $state $stream (
                                $text.Substring($offset, [Math]::Min(11, $text.Length - $offset))) $parse
                        }
                        $observed = New-HermesStartupObservations $state
                        $expected = if (-not $parse) { 0 } elseif ($variant -eq 0) { 2 } else { 3 }
                        Assert-Control ($observed.SdkSelectedExtensionChain -eq $expected `
                            -and $observed.SdkCommandErrors -eq 0 -and $observed.FlagsAccepted -eq 0 `
                            -and $observed.ArmPresent -eq 0 -and $observed.ParserProtocol -eq 0 `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                    }
                }
            }
            171 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $row = '    ' + $state.ExtensionExpected + ': image 10.0.1.2, API 1.0.0'
                $controls = @(
                    @{ Text = $row; Value = 0 },
                    @{ Text = "Extension DLL search Path:`n$row"; Value = 0 },
                    @{ Text = "extension DLL chain:`n$row"; Value = 0 },
                    @{ Text = "0:000> Extension DLL chain:`n$row"; Value = 0 },
                    @{ Text = "Extension DLL chain:`n`n$row"; Value = 1 },
                    @{ Text = "Extension DLL chain:`nother output`n$row"; Value = 1 },
                    @{ Text = "Extension DLL chain:`n    exts.dll: image 10.0.1.2, API 1.0.0"; Value = 1 },
                    @{ Text = "Extension DLL chain:`n$row trailing"; Value = 1 },
                    @{ Text = "Extension DLL chain:`n" + $row.Replace('winxp', 'winext'); Value = 1 },
                    @{ Text = "Extension DLL chain:`n" + $row.Replace('\winxp\', '\other\..\winxp\'); Value = 1 },
                    @{ Text = "Extension DLL chain:`n    C:\private\exts.dll: image 10.0.1.2, API 1.0.0"; Value = 1 },
                    @{ Text = "Extension DLL chain:`n    `"exts.dll`": image 10.0.1.2, API 1.0.0"; Value = 1 }
                )
                foreach ($control in $controls) {
                    $state.ChainPending = $false
                    $state.Observations.SdkSelectedExtensionChain = 0
                    Add-HermesStartupText $state $state.Out ($control.Text + "`n") $true
                    Assert-Control ((New-HermesStartupObservations $state).SdkSelectedExtensionChain -eq $control.Value)
                }
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Add-HermesStartupText $state $state.Out ("Extension DLL chain:`n$row`n") $true
                Assert-Control ((New-HermesStartupObservations $state).SdkSelectedExtensionChain -eq 1 `
                    -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
            }
            172 {
                $sdk = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64'
                $state.ExtensionExpected = $sdk + '\winxp\exts.dll'
                $selected = $state.ExtensionExpected
                $variants = @($selected, $selected.Replace('\', '/'), $selected.Replace('\', ''),
                    $selected.Replace('\', '\\'), 'C:\Program', ('.load "' + $selected + '"'),
                    ('.load ' + $selected), 'exts', 'exts.dll', 'ext', 'ext.dll',
                    'ntsdexts', 'ntsdexts.dll', 'gflag', 'gflag.dll', 'dbghelp', 'dbghelp.dll',
                    'uext', 'uext.dll', ($sdk + '\winext\ext.dll'), ($sdk + '\winxp\ntsdexts.dll'),
                    ($sdk + '\dbghelp.dll'), ($sdk + '\winext\uext.dll'))
                foreach ($parse in @($true, $false)) {
                    for ($variant = 0; $variant -lt $variants.Count; $variant++) {
                        $source = $variants[$variant]
                        $forms = @($source, ('"' + $source + '"'), ('"' + $source), ($source + '"'),
                            ('\"' + $source + '\"'), (' ' + $source), ($source + ' '))
                        foreach ($upper in @($false, $true)) {
                            for ($form = 0; $form -lt $forms.Count; $form++) {
                                foreach ($name in @('SdkCommandErrors', 'SdkLoadLibraryWin32Error',
                                        'SdkLoadLibraryPathShape', 'SdkLoadLibraryRelation', 'SdkLoadLibraryForm')) {
                                    $state.Observations[$name] = 0
                                }
                                $library = if ($upper) { $forms[$form].ToUpperInvariant() } else { $forms[$form] }
                                $text = 'The call to LoadLibrary(' + $library + ') failed, Win32 error 0n2' + "`r`n"
                                $stream = if ($parse) { $state.Out } else { $state.Err }
                                for ($offset = 0; $offset -lt $text.Length; $offset += 7) {
                                    Add-HermesStartupText $state $stream (
                                        $text.Substring($offset, [Math]::Min(7, $text.Length - $offset))) $parse
                                }
                                $observed = New-HermesStartupObservations $state
                                $expectedForm = $form + 1 + $(if ($upper) { 8 } else { 0 })
                                Assert-Control ($observed.SdkLoadLibraryRelation -eq ($variant + 1) `
                                    -and $observed.SdkLoadLibraryForm -eq $expectedForm `
                                    -and $observed.SdkLoadLibraryWin32Error -eq 2 `
                                    -and $observed.SdkCommandErrors -eq 1 `
                                    -and $observed.FlagsAccepted -eq 0 -and $observed.ParserUnknown -eq 0 `
                                    -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                            }
                        }
                    }
                }
            }
            173 {
                $selected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $state.ExtensionExpected = $selected
                foreach ($library in @(
                    'C:\private\exts.dll', 'other\ext.dll', 'ntsdexts.dll.trailing',
                    'exts.dll;.echo injected', 'C:\PROGRA~2\Windows Kits\10\Debuggers\x64\winxp\exts.dll',
                    $selected.Replace('\winxp\', '\other\..\winxp\'),
                    $selected.Replace('Windows Kits', 'Different Kits'),
                    ('  ' + $selected), ($selected + '  '), ('.loadby ' + $selected),
                    ('C:\private' + [char]128 + '\exts.dll')
                )) {
                    $state.Observations.SdkCommandErrors = 0
                    Add-HermesStartupText $state $state.Err (
                        'The call to LoadLibrary(' + $library + ') failed, Win32 error 0n2' + "`n") $false
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.SdkCommandErrors -eq 1 -and $observed.SdkLoadLibraryWin32Error -eq 2 `
                        -and $observed.SdkLoadLibraryRelation -eq 0 -and $observed.SdkLoadLibraryForm -eq 0 `
                        -and $observed.SdkLoadLibraryPathShape -eq 7)
                }
                foreach ($expected in @('', $null, 1, @{ path = 'private' },
                        ('C:\' + ('x' * 240)), 'C:\private";.echo injected', ('C:\private' + [char]128))) {
                    $match = Get-HermesStartupLoadLibraryRelation $expected 'exts.dll'
                    Assert-Control ($match.Relation -eq 0 -and $match.Form -eq 0)
                }
                $match = Get-HermesStartupLoadLibraryRelation $selected ('x' * 1025)
                Assert-Control ($match.Relation -eq 0 -and $match.Form -eq 0)
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($report.armingVerified -eq 0 -and $report.completed -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $json -notmatch 'private|Program Files|exts\.dll|LoadLibrary\(')
            }
            174 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                foreach ($parse in @($true, $false)) {
                    foreach ($first in @(
                        @{ Path = 'ntsdexts.dll'; Code = '2'; Relation = 13; Shape = 7; Form = 1; Error = 2 },
                        @{ Path = 'C:\private\other.dll'; Code = '2'; Relation = 0; Shape = 7; Form = 0; Error = 2 },
                        @{ Path = 'ntsdexts.dll'; Code = '4294967296'; Relation = 0; Shape = 0; Form = 0; Error = 0 }
                    )) {
                        foreach ($name in @('SdkCommandErrors', 'SdkLoadLibraryWin32Error',
                                'SdkLoadLibraryPathShape', 'SdkLoadLibraryRelation', 'SdkLoadLibraryForm')) {
                            $state.Observations[$name] = 0
                        }
                        $stream = if ($parse) { $state.Out } else { $state.Err }
                        $other = if ($parse) { $state.Err } else { $state.Out }
                        Add-HermesStartupText $state $stream (
                            'The call to LoadLibrary(' + $first.Path + ') failed, Win32 error 0n' + $first.Code + "`n") $parse
                        Add-HermesStartupText $state $other (
                            'The call to LoadLibrary(' + $state.ExtensionExpected + ') failed, Win32 error 0n126' + "`n") (-not $parse)
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($observed.SdkLoadLibraryWin32Error -eq $first.Error `
                            -and $observed.SdkLoadLibraryRelation -eq $first.Relation `
                            -and $observed.SdkLoadLibraryPathShape -eq $first.Shape `
                            -and $observed.SdkLoadLibraryForm -eq $first.Form)
                    }
                }
            }
            175 {
                $limits = @{ SdkSelectedExtensionChain = 3; SdkLoadLibraryRelation = 23; SdkLoadLibraryForm = 15 }
                foreach ($name in $limits.Keys) {
                    $maximum = $limits[$name]
                    foreach ($value in @(-1, ($maximum + 1), [long]1, 1.5, $true, $null,
                            '1', @{ text = 'C:\private\exts.dll' })) {
                        $state.Observations[$name] = $value
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($observed[$name] -is [int] -and $observed[$name] -eq 0)
                    }
                    foreach ($value in 0..$maximum) {
                        $state.Observations[$name] = $value
                        $expected = if ($name -ceq 'SdkLoadLibraryForm' -and $value -eq 8) { 0 } else { $value }
                        Assert-Control ((New-HermesStartupObservations $state)[$name] -eq $expected)
                    }
                }
            }
            176 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $before = New-HermesStartupReport $state $true $true
                Add-HermesStartupLine $state 'Extension DLL chain:'
                Add-HermesStartupLine $state ('    ' + $state.ExtensionExpected + ': image 10.0.1.2, API 1.0.0')
                Add-HermesStartupText $state $state.Err (
                    'The call to LoadLibrary(ntsdexts.dll) failed, Win32 error 0n2' + "`n") $false
                $report = New-HermesStartupReport $state $true $true
                foreach ($name in $before.Keys | Where-Object { $_ -cnotin @(
                        'observations', 'acceptedUtf8Bytes', 'errChars', 'errKept', 'errDropped') }) {
                    Assert-Control ($report[$name] -ceq $before[$name])
                }
                Assert-Control ($report.observations.SdkSelectedExtensionChain -eq 0 `
                    -and $report.observations.SdkLoadLibraryRelation -eq 13 `
                    -and $report.observations.SdkLoadLibraryForm -eq 1 `
                    -and $report.armingVerified -eq 1 -and $report.completed -eq 1)
            }
            177 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                Add-HermesStartupLine $state 'Extension DLL chain:'
                Add-HermesStartupLine $state ('    ' + $state.ExtensionExpected + ': image 10.0.1.2, API 1.0.0')
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=1 pid=22'
                Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                Add-HermesStartupText $state $state.Err (
                    'Current NtGlobalFlag contents: 0x00000002' + "`n" +
                    'The call to LoadLibrary(ntsdexts.dll) failed, Win32 error 0n2' + "`n") $false
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=22'
                Update-HermesStartupRows $state ($targetRow + "`n")
                $report = New-HermesStartupReport $state $false $false
                $observed = $report.observations
                Assert-Control ($observed.SdkSelectedExtensionChain -eq 2 `
                    -and $observed.SdkLoadLibraryRelation -eq 13 -and $observed.SdkLoadLibraryForm -eq 1 `
                    -and $observed.CreateEventAccepted -eq 1 -and $observed.FlagsAccepted -eq 0 `
                    -and $observed.FlagsSeen -eq 0 -and $observed.ArmFlag2 -eq 0 `
                    -and $observed.ParserUnknown -eq 1 -and $observed.ParserProtocol -eq 0 `
                    -and -not (Test-HermesStartupArm $state) -and -not $state.Acked `
                    -and $report.armingVerified -eq 0 -and $report.completed -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0)
            }
            178 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                Add-HermesStartupText $state $state.Err ('x' * 4096) $false
                Add-HermesStartupText $state $state.Err ('x' * 4096) $false
                Add-HermesStartupText $state $state.Err "`n" $false
                Add-HermesStartupText $state $state.Out (
                    "Extension DLL chain:`n    $($state.ExtensionExpected): image 10.0.1.2, API 1.0.0`n") $true
                Add-HermesStartupText $state $state.Err (
                    'The call to LoadLibrary(gflag.dll) failed, Win32 error 0n2' + "`n") $false
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.SdkSelectedExtensionChain -eq 2 -and $observed.SdkLoadLibraryRelation -eq 15 `
                    -and $state.Err.Prefix.Length -eq 4096 -and $state.Err.Tail.Length -eq 0 `
                    -and $state.Bounds -eq 0)
                $before = $observed | ConvertTo-Json -Compress
                $state.AcceptedUtf8Bytes = [long]8388607
                Add-HermesStartupText $state $state.Out "`n" $true
                Add-HermesStartupText $state $state.Err (
                    'The call to LoadLibrary(ext.dll) failed, Win32 error 0n126' + "`n") $false
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and ($report.observations | ConvertTo-Json -Compress) -ceq $before `
                    -and $report.status -eq 7 -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.observations.Count -eq 56 `
                    -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'Program Files|Windows Kits|exts\.dll|gflag\.dll|LoadLibrary\(|C:')
            }
            179 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $names = @('exts', 'ext', 'ntsdexts', 'gflag', 'dbghelp', 'uext',
                    'dbgeng', 'symsrv', 'gflags', 'cdb', 'kext', 'kdexts')
                $suffixes = @('', '.DLL', '.dll.dll', '.exe', '.dll.private-suffix')
                foreach ($parse in @($true, $false)) {
                    for ($index = 0; $index -lt $names.Count; $index++) {
                        for ($suffix = 0; $suffix -lt $suffixes.Count; $suffix++) {
                            $state.Observations.SdkCommandErrors = 0
                            $library = '  \"C:/private\\folder/' + $names[$index].ToUpperInvariant() +
                                $suffixes[$suffix] + "'   "
                            $text = 'The call to LoadLibrary(' + $library + ') failed, Win32 error 0n2' + "`r`n"
                            $stream = if ($parse) { $state.Out } else { $state.Err }
                            for ($offset = 0; $offset -lt $text.Length; $offset += 3) {
                                Add-HermesStartupText $state $stream (
                                    $text.Substring($offset, [Math]::Min(3, $text.Length - $offset))) $parse
                            }
                            $observed = New-HermesStartupObservations $state
                            Assert-Control ($observed.SdkLoadLibraryLexicalModule -eq ($index + 1) `
                                -and $observed.SdkLoadLibraryLexicalSuffix -eq ($suffix + 1) `
                                -and ($observed.SdkLoadLibraryDisplay -band 2) -eq 0 `
                                -and ($observed.SdkLoadLibraryDisplay -band 481) -eq 481 `
                                -and $observed.SdkLoadLibraryRelation -eq 0 -and $observed.SdkLoadLibraryForm -eq 0 `
                                -and $observed.SdkLoadLibraryWin32Error -eq 2 -and $observed.FlagsAccepted -eq 0 `
                                -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                        }
                    }
                }
            }
            180 {
                $selected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $controls = @(
                    @{ Path = $selected; Bits = 3; Full = $true },
                    @{ Path = $selected.ToUpperInvariant(); Bits = 515; Full = $true },
                    @{ Path = $selected.Replace('\Windows Kits\', '/Windows Kits\\'); Bits = 131; Full = $true },
                    @{ Path = ('  \"' + $selected.Replace('\', '\\') + "'   "); Bits = 483; Full = $true },
                    @{ Path = ('.load "' + $selected + '"'); Bits = 51; Full = $true },
                    @{ Path = ('".load \"' + $selected + '\""'); Bits = 115; Full = $true },
                    @{ Path = $selected.Substring(0, 10); Bits = 5; Full = $false },
                    @{ Path = 'winxp/exts.dll'; Bits = 137; Full = $false },
                    @{ Path = $selected.Replace('\winxp\', '\other\..\winxp\'); Bits = 1; Full = $false },
                    @{ Path = 'C:\private\exts.dll'; Bits = 1; Full = $false },
                    @{ Path = ('.load .load "' + $selected + '"'); Bits = 49; Full = $false },
                    @{ Path = 'C:\private\unknown-file.dll'; Bits = 1; Full = $false },
                    @{ Path = 'C:\private\exts-other.dll'; Bits = 1; Full = $false }
                )
                foreach ($control in $controls) {
                    $facts = Get-HermesStartupLibraryLexicalFacts $selected $control.Path
                    Assert-Control (($facts.Display -band $control.Bits) -eq $control.Bits `
                        -and (($facts.Display -band 2) -eq 2) -eq $control.Full `
                        -and $facts.Display -is [int] -and $facts.Module -is [int] -and $facts.Suffix -is [int])
                }
                foreach ($path in @('C:\private\unknown-file.dll', 'C:\private\exts-other.dll',
                        'C:\private\pre-exts.dll', 'C:\private\extsX.dll')) {
                    $facts = Get-HermesStartupLibraryLexicalFacts $selected $path
                    Assert-Control ($facts.Module -eq 0 -and $facts.Suffix -eq 0 -and $facts.Display -eq 1)
                }
            }
            181 {
                foreach ($parse in @($true, $false)) {
                    $state.Observations.SdkSelectedExtensionChain = 0
                    $state.Observations.SdkChainLines = 0
                    $state.Observations.SdkChainSupportedRows = 0
                    $state.Initial = [uint32]0
                    $state.ChainPending = $false
                    $state.ChainCollecting = $false
                    $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                    $text = "Extension DLL search Path:`n    C:\private\search.dll`n" +
                        "Extension DLL chain:`n    other: image 10.0.1.2, API 1.0.0`n" +
                        "        [path: C:\private\other.dll]`n`nunknown unindented chain format`n" +
                        "    C:\private\exts.dll: image 10.0.1.2, API 1.0.0`n" +
                        '0:000> .printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid' + "`n" +
                        "HERMES_CDB v=1 kind=0 pid=11`nafter collection`n"
                    $stream = if ($parse) { $state.Out } else { $state.Err }
                    for ($offset = 0; $offset -lt $text.Length; $offset += 5) {
                        Add-HermesStartupText $state $stream (
                            $text.Substring($offset, [Math]::Min(5, $text.Length - $offset))) $parse
                    }
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.SdkChainLines -eq $(if ($parse) { 4 } else { 0 }) `
                        -and $observed.SdkChainSupportedRows -eq $(if ($parse) { 2 } else { 0 }) `
                        -and $observed.SdkSelectedExtensionChain -eq [int]$parse `
                        -and $observed.FlagsAccepted -eq 0 -and $observed.ParserUnknown -eq 0 `
                        -and $observed.ParserProtocol -eq 0 -and -not $state.ChainCollecting `
                        -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                }
            }
            182 {
                $selected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                foreach ($expected in @('', $null, 1, @{ path = 'private' },
                        ('C:\' + ('x' * 240)), 'C:\private";.echo injected', ('C:\private' + [char]128))) {
                    $facts = Get-HermesStartupLibraryLexicalFacts $expected 'exts.dll'
                    Assert-Control ($facts.Module -eq 0 -and $facts.Suffix -eq 0 -and $facts.Display -eq 0)
                }
                foreach ($path in @('', ('x' * 1025), ("C:\private`nexts.dll"), ("C:\private" + [char]0))) {
                    $facts = Get-HermesStartupLibraryLexicalFacts $selected $path
                    Assert-Control ($facts.Module -eq 0 -and $facts.Suffix -eq 0 -and $facts.Display -eq 0)
                }
                $facts = Get-HermesStartupLibraryLexicalFacts $selected (('x' * 1015) + '\exts.dll')
                Assert-Control ($facts.Module -eq 1 -and $facts.Suffix -eq 2 -and $facts.Display -eq 1)
                foreach ($code in @('2', '4294967296')) {
                    foreach ($name in @('SdkCommandErrors', 'SdkLoadLibraryWin32Error',
                            'SdkLoadLibraryPathShape', 'SdkLoadLibraryRelation', 'SdkLoadLibraryForm',
                            'SdkLoadLibraryLexicalModule', 'SdkLoadLibraryLexicalSuffix', 'SdkLoadLibraryDisplay')) {
                        $state.Observations[$name] = 0
                    }
                    $state.ExtensionExpected = $selected
                    Add-HermesStartupText $state $state.Err (
                        'The call to LoadLibrary( \"C:/private\\exts.dll'' ) failed, Win32 error 0n' + $code + "`n") $false
                    $before = (New-HermesStartupObservations $state) | ConvertTo-Json -Compress
                    Add-HermesStartupText $state $state.Out (
                        'The call to LoadLibrary(' + $selected + ') failed, Win32 error 0n126' + "`n") $true
                    Assert-Control (((New-HermesStartupObservations $state) | ConvertTo-Json -Compress) -ceq $before `
                        -and (New-HermesStartupObservations $state).SdkLoadLibraryWin32Error -eq $(
                            if ($code -ceq '2') { 2 } else { 0 }) `
                        -and (New-HermesStartupObservations $state).SdkLoadLibraryLexicalModule -eq $(
                            if ($code -ceq '2') { 1 } else { 0 }))
                }
            }
            183 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                Add-HermesStartupText $state $state.Out (
                    "Extension DLL chain:`n    $($state.ExtensionExpected): image 10.0.1.2, API 1.0.0`n" +
                    "HERMES_CDB v=1 kind=0 pid=11`nHERMES_CDB v=1 kind=1 pid=22`n" +
                    "Last event: 16.1: Create process 1:16`n") $true
                Add-HermesStartupText $state $state.Err (
                    'The call to LoadLibrary( \"' + $state.ExtensionExpected.Replace('\', '/') +
                    "' ) failed, Win32 error 0n2`nCurrent NtGlobalFlag contents: 0x00000002`n") $false
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=22'
                Update-HermesStartupRows $state ($targetRow + "`n")
                $report = New-HermesStartupReport $state $false $false
                Assert-Control ($report.observations.SdkChainLines -eq 1 `
                    -and $report.observations.SdkChainSupportedRows -eq 1 `
                    -and $report.observations.SdkLoadLibraryLexicalModule -eq 1 `
                    -and ($report.observations.SdkLoadLibraryDisplay -band 2) -eq 2 `
                    -and $report.observations.FlagsAccepted -eq 0 -and $report.observations.ArmFlag2 -eq 0 `
                    -and -not (Test-HermesStartupArm $state) -and -not $state.Acked `
                    -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.firstFailureKind -eq 0 -and $report.nativeAcceptance -eq 0)
            }
            184 {
                $state.Out.Hash.Dispose()
                $state.Err.Hash.Dispose()
                $state = New-ControlFixture
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $before = New-HermesStartupReport $state $true $true
                Add-HermesStartupLine $state (
                    'The call to LoadLibrary( \"C:/private\\ntsdexts.dll'' ) failed, Win32 error 0n2')
                $report = New-HermesStartupReport $state $true $true
                foreach ($name in $before.Keys | Where-Object { $_ -cne 'observations' }) {
                    Assert-Control ($report[$name] -ceq $before[$name])
                }
                Assert-Control ($report.observations.SdkLoadLibraryLexicalModule -eq 3 `
                    -and $report.observations.SdkLoadLibraryLexicalSuffix -eq 2 `
                    -and $report.armingVerified -eq 1 -and $report.completed -eq 1)
            }
            185 {
                $limits = @{
                    SdkLoadLibraryLexicalModule = 12; SdkLoadLibraryLexicalSuffix = 5
                    SdkLoadLibraryDisplay = 1023; SdkChainLines = 8388608; SdkChainSupportedRows = 8388608
                }
                foreach ($name in $limits.Keys) {
                    $maximum = $limits[$name]
                    foreach ($value in @(-1, ($maximum + 1), [long]1, 1.5, $true, $null,
                            '1', @{ text = 'C:\private\exts.dll' })) {
                        $state.Observations[$name] = $value
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($observed[$name] -is [int] -and $observed[$name] -eq 0)
                    }
                    foreach ($value in @(0, 1, $maximum)) {
                        $state.Observations[$name] = $value
                        Assert-Control ((New-HermesStartupObservations $state)[$name] -eq $value)
                    }
                }
                foreach ($name in @('DebuggerSha256', 'HelperSha256', 'CommandSha256',
                        'CreateCommandSha256', 'ExitCommandSha256')) { $state[$name] = 'f' * 64 }
                $state.Out.Fingerprint = 'f' * 64
                $state.Err.Fingerprint = 'f' * 64
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($report.observations.Count -eq 56 `
                    -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|exts\.dll|LoadLibrary\(|C:' `
                    -and $report.completed -eq 0 -and $report.armingVerified -eq 0)
            }
            186 {
                $state.ExtensionExpected = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                $text = "Extension DLL chain:`nunknown unindented format`n" +
                    'The call to LoadLibrary( \"C:/private\\gflag.dll'' ) failed, Win32 error 0n2' + "`n"
                for ($offset = 0; $offset -lt $text.Length; $offset++) {
                    Add-HermesStartupText $state $state.Out $text.Substring($offset, 1) $true
                }
                Add-HermesStartupText $state $state.Err ('x' * 4096) $false
                Add-HermesStartupText $state $state.Err ('x' * 4096) $false
                Add-HermesStartupText $state $state.Err "`n" $false
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.SdkLoadLibraryLexicalModule -eq 4 `
                    -and $observed.SdkLoadLibraryLexicalSuffix -eq 2 `
                    -and $observed.SdkChainLines -eq 2 -and $observed.SdkChainSupportedRows -eq 0 `
                    -and $state.Err.Prefix.Length -eq 4096 -and $state.Err.Tail.Length -eq 0)
                $before = $observed | ConvertTo-Json -Compress
                $state.AcceptedUtf8Bytes = [long]8388607
                Add-HermesStartupText $state $state.Err "`n" $false
                Add-HermesStartupText $state $state.Out (
                    "Extension DLL chain:`n    $($state.ExtensionExpected): image 10.0.1.2, API 1.0.0`n") $true
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and ($report.observations | ConvertTo-Json -Compress) -ceq $before `
                    -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.status -eq 7 -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|Program Files|Windows Kits|exts\.dll|gflag\.dll|LoadLibrary\(|C:')
            }
            191 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Assert-Control ($state.Block.Flags -eq 2 -and -not (Test-HermesStartupArm $state))
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Assert-Control ((Test-HermesStartupArm $state) -and -not $state.Acked)
                Set-HermesStartupArmAcknowledged $state
                Add-HermesStartupLine $state $failureLine
                foreach ($line in @(
                    'HERMES_CDB v=1 kind=3 pid=22',
                    'Last event: 16.1: Exit process 1:16, code c0000142',
                    'HERMES_CDB v=1 kind=4 pid=22',
                    'HERMES_CDB v=1 kind=3 pid=11',
                    'Last event: b.1: Exit process 0:b, code 0',
                    'HERMES_CDB v=1 kind=4 pid=11'
                )) { Add-HermesStartupLine $state $line }
                Update-HermesStartupRows $state ($targetRow + "`n" + $finalRow + "`n")
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.TypedFlagsAvailable -eq 1 `
                    -and $report.observations.TypedFlagsValue -eq 2 `
                    -and $report.observations.FlagsSeen -eq 0 -and $report.observations.FlagsAccepted -eq 0 `
                    -and $report.armingVerified -eq 1 -and $report.completed -eq 1 `
                    -and $report.cleanupComplete -eq 1 -and $report.nativeAcceptance -eq 0 `
                    -and $state.ArmedBinding.Born -eq 123 -and (Test-HermesStartupArmed $state) `
                    -and -not (Test-HermesStartupArm $state))
            }
            { $_ -in 192..194 } {
                $state = New-ControlTypedBlock
                $hex = @('00000000', '00000001', 'ffffffff')[$case - 192]
                $expected = [long][Convert]::ToUInt32($hex, 16)
                Add-HermesStartupLine $state ("HERMES_NTGLOBALFLAG v=1 pid=22 value=" + $hex)
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.TypedFlagsAvailable -eq 1 `
                    -and $observed.TypedFlagsValue -eq $expected `
                    -and (Test-HermesStartupArm $state) -eq ($case -eq 194) `
                    -and $observed.ArmFlag2 -eq [int]($case -eq 194) `
                    -and $observed.ParserUnknown -eq [int]($case -ne 194) -and -not $state.Acked)
            }
            195 {
                foreach ($line in @(
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=2',
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=000000002',
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=100000002',
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=0x00000002',
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=0000000A',
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=????????',
                    'HERMES_NTGLOBALFLAG v=2 pid=22 value=00000002',
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002 trailing',
                    ' HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002',
                    '0:000> HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                )) {
                    $state = New-ControlTypedBlock
                    try {
                        Add-HermesStartupLine $state $line
                        Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($state.Arm.Flags -eq -1 -and $state.Protocol -eq 1 `
                            -and $observed.TypedFlagsAvailable -eq 0 -and $observed.TypedFlagsValue -eq 0 `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            196 {
                foreach ($pidText in @('0', '023', '23', '11', '4294967296', '99999999999', '-1')) {
                    $state = New-ControlTypedBlock
                    try {
                        Add-HermesStartupLine $state ("HERMES_NTGLOBALFLAG v=1 pid=" + $pidText + ' value=00000002')
                        Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                        Assert-Control ($state.Protocol -eq 1 -and $state.Arm.Flags -eq -1 `
                            -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                            -and -not (Test-HermesStartupArm $state))
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            197 {
                foreach ($phase in @('none', 'before-event', 'exit', 'legacy', 'wrong-event', 'helper')) {
                    $state = if ($phase -eq 'exit') { New-ControlOpenBlock -Kind 3 } `
                        elseif ($phase -eq 'legacy') { New-ControlOpenBlock } else { New-HermesStartupState }
                    try {
                        $current = if ($phase -eq 'helper') { 11 } else { 22 }
                        if ($phase -in @('before-event', 'wrong-event', 'helper')) {
                            Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                            Add-HermesStartupLine $state "HERMES_CDB v=2 kind=1 pid=$current"
                            if ($phase -eq 'wrong-event') {
                                Add-HermesStartupLine $state 'Last event: 17.1: Create process 1:17'
                            } elseif ($phase -eq 'helper') {
                                Add-HermesStartupLine $state 'Last event: b.1: Create process 0:b'
                            }
                        }
                        Add-HermesStartupLine $state "HERMES_NTGLOBALFLAG v=1 pid=$current value=00000002"
                        Assert-Control ($state.Protocol -eq 1 `
                            -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            198 {
                $state = New-ControlTypedBlock
                $readback = "HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002`r`n"
                Add-HermesStartupText $state $state.Err $readback $false
                Assert-Control ($state.Block.Flags -eq -1 -and $state.Protocol -eq 0 `
                    -and (Get-HermesStartupTypedFlags $state) -eq -1)
                $text = $readback + "HERMES_CDB v=2 kind=2 pid=22`r`n"
                for ($index = 0; $index -lt $text.Length; $index += 7) {
                    Add-HermesStartupText $state $state.Out (
                        $text.Substring($index, [Math]::Min(7, $text.Length - $index))) $true
                }
                Assert-Control ((Test-HermesStartupArm $state) `
                    -and (Get-HermesStartupTypedFlags $state) -eq 2 `
                    -and $state.Protocol -eq 0 -and $state.Unknown -eq 0)
            }
            199 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state '1:000> .printf "HERMES_NTGLOBALFLAG v=1 pid=%u value=%08x\n", @$tpid, @@c++(@$peb->NtGlobalFlag)'
                Assert-Control ($state.Block.Flags -eq -1 -and $state.Protocol -eq 0)
                Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                $observed = New-HermesStartupObservations $state
                Assert-Control ($state.Arm.Flags -eq -1 -and $state.Unknown -eq 1 `
                    -and $observed.FlagsSeen -eq 1 -and $observed.FlagsAccepted -eq 0 `
                    -and $observed.TypedFlagsAvailable -eq 0 -and -not (Test-HermesStartupArm $state))
            }
            200 {
                foreach ($errorLine in @(
                    '', 'Unable to read PEB', 'Memory access error at private-address',
                    'Type ntdll!_PEB not found', 'Syntax error at NtGlobalFlag',
                    '*** ERROR: Symbols could not be loaded for ntdll.dll',
                    'Couldn''t resolve error at ((ntdll!_PEB*)@$peb)->NtGlobalFlag'
                )) {
                    $state = New-ControlTypedBlock
                    try {
                        Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000002'
                        Add-HermesStartupLine $state $errorLine
                        Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                        $observed = New-HermesStartupObservations $state
                        Assert-Control ($state.Arm.Flags -eq -1 -and $state.Unknown -eq 1 `
                            -and $observed.TypedFlagsAvailable -eq 0 -and $observed.TypedFlagsValue -eq 0 `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            201 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state $failureLine
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Assert-Control ($state.Unknown -eq 1 -and $state.Protocol -eq 1 `
                    -and $null -eq $state.Failure -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                    -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
            }
            202 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Set-HermesStartupArmAcknowledged $state
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=3 pid=22'
                Add-HermesStartupLine $state 'Last event: 16.1: Exit process 1:16, code c0000142'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=4 pid=22'
                Assert-Control ((Test-HermesStartupArmed $state))
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupArmed $state) `
                    -and (Get-HermesStartupTypedFlags $state) -eq -1)
            }
            203 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000000'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Assert-Control ($state.Protocol -eq 1 -and $state.Arm.Flags -eq 2 `
                    -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                    -and -not (Test-HermesStartupArm $state))
            }
            204 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=2 pid=22'
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Arm `
                    -and $null -eq $state.Block -and -not (Test-HermesStartupArm $state))
                Add-HermesStartupMarker $state 0 11 2
                Assert-Control ($state.Protocol -eq 1)
            }
            205 {
                foreach ($row in @(
                    $targetRow.Replace('pid=22', 'pid=23'),
                    $targetRow.Replace('helper=11', 'helper=12'),
                    $targetRow.Replace('born=123', 'born=0'),
                    $targetRow.Replace('context=1', 'context=0')
                )) {
                    $state = New-ControlTypedBlock
                    try {
                        $state.Target = $null
                        $state.TargetText = ''
                        Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                        Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                        Assert-Control ((Get-HermesStartupTypedFlags $state) -eq -1 `
                            -and -not (Test-HermesStartupArm $state))
                        Update-HermesStartupRows $state ($row + "`n")
                        Assert-Control ((Get-HermesStartupTypedFlags $state) -eq -1 `
                            -and -not (Test-HermesStartupArm $state))
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            206 {
                $state = New-ControlTypedBlock
                Update-HermesStartupRows $state ($targetRow + "`n" + $finalRow + "`n")
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Assert-Control ($state.Protocol -eq 1 -and $state.Arm.Flags -eq -1 `
                    -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                    -and -not (Test-HermesStartupArm $state))
            }
            207 {
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                $bytes = $script:controlCommandFiles[(Join-Path $root 'startup-cpr.cdb')]
                $initialBytes = $script:controlCommandFiles[(Join-Path $root 'startup-ibp.cdb')]
                Assert-Control ($bytes.Length -eq 119 `
                    -and $report.createCommandSha256 -ceq '5fb09e6b92ea4344099598c8057ef775745ebf2994662651cabf052d67dc7d7f' `
                    -and $initialBytes.Length -eq 116 `
                    -and $report.initialCommandSha256 -ceq '48714490926d5dbee8117768355f9c6dece7e69840f28220096708eb54587c4e' `
                    -and $script:controlCommandPins.Count -eq 4 -and $script:controlCommandHashes -eq 4 `
                    -and $script:controlLaunchBindings -eq 1 -and $script:controlStops -eq 1 `
                    -and $script:controlDisposed -eq 1 -and $report.armingVerified -eq 0)
            }
            208 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=ffffffff'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Set-HermesStartupArmAcknowledged $state
                foreach ($name in @('DebuggerSha256', 'HelperSha256', 'CommandSha256',
                        'CreateCommandSha256', 'ExitCommandSha256')) { $state[$name] = 'f' * 64 }
                $state.Out.Fingerprint = 'f' * 64
                $state.Err.Fingerprint = 'f' * 64
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                $report = New-HermesStartupReport $state $false $false
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($report.observations.TypedFlagsAvailable -eq 1 `
                    -and $report.observations.TypedFlagsValue -eq [long][uint32]::MaxValue `
                    -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'NtGlobalFlag|ntdll|_PEB|HERMES_NTGLOBALFLAG|0x|private')
            }
            209 {
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Set-HermesStartupArmAcknowledged $state
                Assert-Control ((Test-HermesStartupArmed $state) -and $state.ArmedBinding.Born -eq 123)
                Update-HermesStartupRows $state ($targetRow.Replace('born=123', 'born=124') + "`n")
                Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupArmed $state) `
                    -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                    -and -not (Test-HermesStartupArm $state))
            }
            210 {
                $state = New-ControlTypedBlock
                $readback = "HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002`n"
                Add-HermesStartupText $state $state.Err ('x' * 4096) $false
                $state.AcceptedUtf8Bytes = 8388608 - [Text.Encoding]::UTF8.GetByteCount($readback)
                Add-HermesStartupText $state $state.Out $readback $true
                Assert-Control ($state.AcceptedUtf8Bytes -eq 8388608 -and $state.Bounds -eq 1 `
                    -and $state.Block.Flags -eq -1 -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                    -and -not (Test-HermesStartupArm $state))
                Add-HermesStartupLine $state ($readback.TrimEnd("`n") + ('x' * 4096))
                Assert-Control ($state.Bounds -eq 1 -and $state.Block.Flags -eq -1)
            }
            211 {
                $source = (New-ScopedCreateCommands $extension)['startup-arm.cdb']
                $lines = $source -split "`r`n"
                Assert-Control ($lines.Count -eq 9 -and $lines[4].StartsWith('.catch { !') `
                    -and $lines[4].EndsWith('.gflag +sls; .echo HERMES_ARM_STAGE v=1 stage=1; }') `
                    -and $lines[4] -cnotmatch '"' `
                    -and $lines[4] -cnotmatch '\.printf|\.if|peb' `
                    -and $lines[5] -ceq '.expr /s masm' `
                    -and $lines[6] -ceq ('.catch { .if (@@c++(sizeof(@$peb->NtGlobalFlag)) == 4) { ' +
                        '.printf "HERMES_NTGLOBALFLAG v=1 pid=%u value=%08x\n", @$tpid, @@c++(@$peb->NtGlobalFlag); }; ' +
                        '.echo HERMES_ARM_STAGE v=1 stage=2; }') `
                    -and [regex]::Matches($lines[6], '@\$peb->NtGlobalFlag').Count -eq 2 `
                    -and $lines[6] -notmatch 'ntdll|_PEB|\*|(?<!@)\$peb|0x|(?i:BeingDebugged|Environment)' `
                    -and @($lines | Where-Object { $_ -cmatch '\.catch' }).Count -eq 2 `
                    -and @($lines[4].ToCharArray() | Where-Object { $_ -eq '{' }).Count -eq 1 `
                    -and @($lines[4].ToCharArray() | Where-Object { $_ -eq '}' }).Count -eq 1 `
                    -and @($lines[6].ToCharArray() | Where-Object { $_ -eq '{' }).Count -eq 2 `
                    -and @($lines[6].ToCharArray() | Where-Object { $_ -eq '}' }).Count -eq 2 `
                    -and ($lines[4] + $lines[6]) -notmatch 'HERMES_CDB|\b(?:g|r|ed|ew|eb|eq|ep)\b' `
                    -and $lines[7] -ceq '.printf "HERMES_CDB v=4 kind=8 pid=%u\n", @$tpid' `
                    -and $lines[8] -ceq '' -and $source -notmatch '(?m)^g\r?$')
                $condensed = $source.Replace("`r", ';').Replace("`n", '')
                Assert-Control ($condensed.Contains('stage=1; };.expr /s masm;.catch { .if (') `
                    -and $condensed.Contains('stage=2; };.printf "HERMES_CDB v=4 kind=8 pid=%u\n", @$tpid;'))
            }
            212 {
                # Model .catch returning after a query error, not native CDB
                # execution. The helper's held-thread acknowledgement stays absent.
                $state = New-ControlTypedBlock
                Add-HermesStartupLine $state 'Current NtGlobalFlag contents: 0x00000002'
                Add-HermesStartupLine $state 'Memory access error at private-query-address'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=2 pid=22'
                Assert-Control ($null -eq $state.Block -and $state.Arm.Flags -eq -1 `
                    -and -not (Test-HermesStartupArm $state) -and -not $state.Acked)
                foreach ($line in @(
                    'HERMES_CDB v=1 kind=3 pid=22',
                    'Last event: 16.1: Exit process 1:16, code 0',
                    'HERMES_CDB v=1 kind=4 pid=22',
                    'HERMES_CDB v=1 kind=3 pid=11',
                    'Last event: b.1: Exit process 0:b, code 0',
                    'HERMES_CDB v=1 kind=4 pid=11'
                )) { Add-HermesStartupLine $state $line }
                $unarmedFinal = $finalRow.Replace('exit=3221225794', 'exit=0').Replace(
                    'cleanup=1 fault=0', 'cleanup=1 fault=1460')
                Update-HermesStartupRows $state ($targetRow + "`n" + $unarmedFinal + "`n")
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($report.observations.Marker2 -eq 1 `
                    -and $report.observations.Marker3 -eq 2 -and $report.observations.Marker4 -eq 2 `
                    -and $report.observations.BlockPresent -eq 0 -and $report.observations.FinalRowPresent -eq 1 `
                    -and $report.observations.TypedFlagsAvailable -eq 0 -and $report.observations.TypedFlagsValue -eq 0 `
                    -and $report.observations.ParserUnknown -eq 1 -and $report.observations.ParserProtocol -eq 0 `
                    -and $report.armingVerified -eq 0 -and $report.completed -eq 0 -and $report.status -eq 5 `
                    -and $report.cleanupComplete -eq 1 -and $report.parentUnchanged -eq 1 `
                    -and $report.exitObserved -eq 1 -and $report.exitCode -eq 0 -and $report.powershellExit37 -eq 0 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                    -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|query-address|Memory access|ntdll|_PEB')
            }
            213 {
                $state = New-ControlNativeStopped
                Assert-Control ((Test-HermesStartupArm $state) -and -not $state.Acked `
                    -and $state.CreatedAcked -and $state.Resumed.resumed -eq 1)
                $physical = [StartupControlWriteStream]::new(0)
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new($physical))
                Add-HermesStartupLine $state $failureLine
                $report = Complete-ControlNative $state
                Assert-Control ($report.completed -eq 1 -and $report.armingVerified -eq 1 `
                    -and $report.cleanupComplete -eq 1 -and $report.parentUnchanged -eq 1 `
                    -and $report.firstFailureKind -eq 1 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and $physical.Writes -eq 1 `
                    -and $physical.Closed -and [Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq "g`n")
            }
            214 {
                $state = New-ControlNativeCreated
                Assert-Control ($state.CreatedAcked -and $null -eq $state.Arm `
                    -and -not $state.Acked -and -not (Test-HermesStartupArm $state) `
                    -and (New-HermesStartupReport $state $false $false).armingVerified -eq 0)
            }
            215 {
                $state = Native-StartupTrace\New-HermesStartupState
                Update-HermesStartupRows $state ($targetRow + "`n")
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Target)
            }
            216 {
                $state = Native-StartupTrace\New-HermesStartupState
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=0 pid=11'
                Add-HermesStartupLine $state 'HERMES_CDB v=2 kind=1 pid=22'
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Block `
                    -and -not (Test-HermesStartupCreated $state))
            }
            217 {
                $state = New-ControlNativeCreated -Acknowledge $false
                Assert-Control ((Test-HermesStartupCreated $state) -and $state.Created.Flags -eq -1 `
                    -and $state.Unknown -eq 0 -and -not (Test-HermesStartupArm $state))
            }
            218 {
                $state = New-ControlNativeCreated -Acknowledge $false
                $definition = @($driverAst.FindAll({
                    param($n)
                    $n -is [Management.Automation.Language.FunctionDefinitionAst] `
                        -and $n.Name -ceq 'Publish-TraceCreated'
                }, $true))
                Assert-Control ($definition.Count -eq 1)
                & {
                    . ([scriptblock]::Create($definition[0].Extent.Text))
                    function Open-TracePin {
                        param([string]$Path)
                        $pin = [IO.File]::Open($Path, [IO.FileMode]::Open,
                            [IO.FileAccess]::Read, [IO.FileShare]::Read)
                        $pins.Add($pin)
                        return $pin
                    }
                    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.artifacts/startup-pure-$PID"))
                    Assert-Control (-not [IO.Directory]::Exists($root))
                    $null = [IO.Directory]::CreateDirectory($root)
                    $pins = [Collections.Generic.List[IO.Stream]]::new()
                    try {
                        Publish-TraceCreated
                        $path = Join-Path $root 'created.row'
                        Assert-Control ([IO.File]::ReadAllText($path) -ceq
                            "HERMES_STARTUP_CREATED v=2 phase=created helper=11 pid=22 born=123`n")
                        Assert-Control ($pins.Count -eq 1 -and $pins[0].CanRead `
                            -and -not $pins[0].CanWrite -and $state.CreatedAcked -and -not $state.Acked `
                            -and ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReadOnly) -ne 0 `
                            -and -not [IO.File]::Exists((Join-Path $root 'armed.row')))
                        Publish-TraceCreated
                        Assert-Control ($pins.Count -eq 1)
                    } finally {
                        foreach ($pin in $pins) { $pin.Dispose() }
                        Remove-Item -LiteralPath $root -Recurse -Force
                    }
                }
            }
            219 {
                $state = New-ControlNativeCreated
                Set-HermesStartupCreatedAcknowledged $state
                Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupArm $state))
            }
            { $_ -in 220..229 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $invalid = @(
                    ($readyV2.Replace('v=2', 'v=1')), ($readyV2 + "`r"),
                    ([string][char]0xfeff + $readyV2), ($readyV2 + [char]233),
                    ($readyV2 + ' extra=1'), ($readyV2.Replace('pid=22', 'pid=022')),
                    ($readyV2.Replace('flags=134742020', 'flags=2')),
                    ($readyV2.Replace('resumed=0', 'resumed=1')),
                    ($readyV2.Replace('stage=1', 'stage=4')), ($readyV2 + ('x' * 385))
                )[$case - 220]
                Update-HermesStartupRows $state ($invalid + "`n")
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Target `
                    -and -not (Test-HermesStartupArm $state))
            }
            { $_ -in 230..237 } {
                $state = New-ControlNativeCreated -WithResumed $false
                $invalid = @(
                    ($resumedV2.Replace('helper=11', 'helper=12')),
                    ($resumedV2.Replace('pid=22', 'pid=23')),
                    ($resumedV2.Replace('born=123', 'born=124')),
                    ($resumedV2.Replace('image=1', 'image=0')),
                    ($resumedV2.Replace('debug=1', 'debug=0')),
                    ($resumedV2.Replace('context=1', 'context=0')),
                    ($resumedV2.Replace('resumed=1', 'resumed=0')), $resumedV2
                )[$case - 230]
                if ($case -eq 237) {
                    $state.RowsText = ''; $state.Target = $null
                    Update-HermesStartupRows $state ($invalid + "`n")
                } else {
                    Update-HermesStartupRows $state ($readyV2 + "`n" + $invalid + "`n")
                }
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Resumed `
                    -and -not (Test-HermesStartupArm $state))
            }
            238 {
                $state = New-ControlNativeCreated
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $resumedV2 + "`n")
                Assert-Control ($state.Protocol -eq 1)
            }
            239 {
                $state = New-ControlNativeCreated
                Update-HermesStartupRows $state ($readyV2 + "`n")
                Assert-Control ($state.Protocol -eq 1)
            }
            240 {
                $state = Native-StartupTrace\New-HermesStartupState
                Update-HermesStartupRows $state ('x' * 2049)
                Assert-Control ($state.Protocol -eq 1 -and $state.RowsText -ceq '')
            }
            241 {
                $state = New-ControlNativeCreated -WithResumed $false
                $failedFinal = $finalV2.Replace('resumed=1', 'resumed=0').Replace('fault=0', 'fault=13')
                Update-HermesStartupRows $state ($readyV2 + "`n" + $failedFinal + "`n")
                Assert-Control ($state.Protocol -eq 0 -and $state.Final.resumed -eq 0 `
                    -and $null -eq $state.Resumed -and -not (Test-HermesStartupArm $state))
            }
            242 {
                $state = New-ControlNativeCreated -WithResumed $false
                Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2 + "`n")
                Assert-Control ($state.Protocol -eq 0 -and $state.Final.resumed -eq 1 `
                    -and $null -eq $state.Resumed -and -not (Test-HermesStartupArm $state))
            }
            243 {
                $state = New-ControlNativeCreated
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`n" + $finalV2 + "`n")
                Assert-Control ($state.Protocol -eq 1)
            }
            { $_ -in 244..253 } {
                $state = New-ControlNativeCreated -Acknowledge $false
                $name = if ($case -eq 245) { 'Cpr' } elseif ($case -eq 251) { 'Epr' } else { 'Ibp' }
                $label = switch ($name) { 'Cpr' { 'Create process' }; 'Epr' { 'Exit process' }; 'Ibp' { 'Initial breakpoint' } }
                $header = $name.ToLowerInvariant() + ' - ' + $label + ' - '
                if ($case -in @(244, 245)) {
                    Add-HermesStartupLine $state ($header + 'ignore')
                    Add-HermesStartupLine $state $state.FilterExpected[$name]
                } elseif ($case -eq 250) {
                    $state.InitialCommandSha256 = ''
                } elseif ($case -eq 251) {
                    $state.FilterVerified.Epr = $false
                } elseif ($case -eq 252) {
                    $state.CommandSha256 = 'not-a-pinned-hash'
                } elseif ($case -eq 253) {
                    $state.FilterVerified.Ibp = $false
                    Add-HermesStartupLine $state 'bpe - Breakpoint - break'
                    Add-HermesStartupLine $state $state.FilterExpected.Ibp
                } else {
                    Add-HermesStartupLine $state ($header + 'break')
                    $command = switch ($case) {
                        246 { $state.FilterExpected.Ibp.Replace('startup-ibp', 'startup-epr') }
                        247 { $state.FilterExpected.Ibp.Replace('\\', '\') }
                        248 { $state.FilterExpected.Ibp.Replace('Command:', 'Command1:') }
                        249 { 'unrecognised line' }
                    }
                    Add-HermesStartupLine $state $command
                    if ($case -eq 249) { Add-HermesStartupLine $state $state.FilterExpected.Ibp }
                }
                Assert-Control (-not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                    -and -not (Test-HermesStartupArm $state))
            }
            { $_ -in 254..260 } {
                $eventLine = @(
                    'Last event: 17.1: Break instruction exception - code 80000003 (first chance)',
                    'Last event: 16.1: Break instruction exception - code 80000003 (second chance)',
                    'Last event: 16.1: Exception - code 80000003 (first chance)',
                    'Last event: 16.1: Break instruction exception - code 80000004 (first chance)',
                    'Last event: 16.1: Create process 1:16',
                    'Last event: 16.1: Break instruction exception - code 80000003 (first chance) extra',
                    'Last event: 16.1: Break instruction exception - code 80000003 (first chance)'
                )[$case - 254]
                $state = New-ControlNativeStopped -Flags '' -EventLine $eventLine
                if ($case -eq 260) { Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=5 pid=22' }
                Assert-Control (-not (Test-HermesStartupArm $state) -and -not $state.Acked)
            }
            { $_ -in 261..268 } {
                $prompt = @('', '1:000>', '0:000> arbitrary', '1:000> ;g',
                    '1:000> ', '1:000> ', '1:000> ', '7:123> ')[$case - 261]
                $state = New-ControlNativeStopped -Prompt $prompt
                if ($case -eq 265) {
                    $state.Stopped = $false; $state.Out.End = $true
                    Set-HermesStartupStoppedPrompt $state '1:000> '
                } elseif ($case -eq 266) {
                    $state.Stopped = $false; $state.Out.Tail = ''
                    foreach ($piece in @('1:', '000', '> ')) {
                        Add-HermesStartupText $state $state.Out $piece $true
                    }
                    Assert-Control ((Test-HermesStartupArm $state))
                    Add-HermesStartupText $state $state.Out 'not-a-prompt' $true
                    Assert-Control (-not (Test-HermesStartupArm $state) -and -not $state.Stopped)
                    $state.Out.Tail = ''
                    Add-HermesStartupText $state $state.Out '1:000> ' $true
                } elseif ($case -eq 267) {
                    $state.Stopped = $false; $state.Out.Tail = ''
                    Add-HermesStartupText $state $state.Err '1:000> ' $false
                }
                Assert-Control ((Test-HermesStartupArm $state) -eq ($case -in @(266, 268)) `
                    -and -not $state.Acked)
            }
            { $_ -in 269..274 } {
                $flags = @('00000000', '', '00000001', '00000004', '00000002', 'ffffffff')[$case - 269]
                $state = New-ControlNativeStopped -Flags $flags
                if ($case -eq 273) {
                    Assert-Control ((Get-HermesStartupTypedFlags $state) -eq 2 -and (Test-HermesStartupArm $state))
                } elseif ($case -eq 274) {
                    Assert-Control ((Get-HermesStartupTypedFlags $state) -eq [long][uint32]::MaxValue `
                        -and (Test-HermesStartupArm $state))
                } else {
                    Assert-Control (-not (Test-HermesStartupArm $state) -and $state.Unknown -eq 1)
                    Assert-Control ((Get-HermesStartupTypedFlags $state) -eq $(if ($flags -ceq '') { -1 } else { [long][Convert]::ToUInt32($flags, 16) }))
                }
            }
            { $_ -in 275..279 } {
                $state = New-ControlNativeCreated
                Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=5 pid=22'
                if ($case -ne 275) {
                    Add-HermesStartupLine $state 'Last event: 16.1: Break instruction exception - code 80000003 (first chance)'
                }
                $flagLine = switch ($case) {
                    276 { 'HERMES_NTGLOBALFLAG v=1 pid=23 value=00000002' }
                    277 { 'HERMES_NTGLOBALFLAG v=1 pid=22 value=000000002' }
                    278 { 'HERMES_NTGLOBALFLAG v=1 pid=22 value=0000000A' }
                    default { 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002' }
                }
                Add-HermesStartupLine $state $flagLine
                if ($case -eq 279) { Add-HermesStartupLine $state $flagLine }
                Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupArm $state))
            }
            { $_ -in 280..288 } {
                $state = New-ControlNativeStopped
                $mode = switch ($case) { 285 { 1 }; 286 { 2 }; 287 { 3 }; default { 0 } }
                $physical = [StartupControlWriteStream]::new($mode)
                $writer = [IO.StreamWriter]::new($physical)
                switch ($case) {
                    281 { $state.Stopped = $false }
                    282 { $state.Resumed = $null }
                    283 { $state.CreatedAcked = $false }
                    288 { $state.InputClosed = $true }
                }
                $refused = $false
                try { Invoke-HermesStartupContinue $state $writer } catch { $refused = $true }
                if ($case -in @(281, 282, 283, 288)) {
                    Assert-Control ($refused -and $physical.Writes -eq 0 -and -not $state.Acked)
                    $writer.Dispose()
                } elseif ($case -in @(280, 284)) {
                    Assert-Control (-not $refused -and $physical.Writes -eq 1 -and $state.Continued `
                        -and $state.Acked -and $state.InputClosed -and $physical.Closed `
                        -and $physical.ToArray().Length -eq 2 `
                        -and [Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq "g`n")
                    if ($case -eq 284) {
                        $refused = $false
                        try { Invoke-HermesStartupContinue $state $writer } catch { $refused = $true }
                        Assert-Control ($refused -and $physical.Writes -eq 1)
                    }
                } elseif ($case -eq 286) {
                    Assert-Control ($physical.Writes -eq 1 -and $state.CleanupFault -eq 13 `
                        -and -not $state.InputClosed -and (New-HermesStartupReport $state $true $true).completed -eq 0)
                } else {
                    Assert-Control ($refused -and $physical.Writes -eq 1 -and $state.Acked `
                        -and -not $state.Continued -and $state.Error -eq 13 -and $state.InputClosed `
                        -and $physical.Closed -and $state.InputPending.IsCompleted `
                        -and $state.CleanupFault -eq 0)
                }
            }
            289 {
                $state = New-ControlNativeStopped
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new([StartupControlWriteStream]::new(0)))
                Add-HermesStartupLine $state $failureLine
                Add-HermesStartupLine $state '16:1 @ 00000000 - LdrpInitializeProcess - ERROR: Process initialization failed with status 0xc0000142'
                Assert-Control ($state.Failure.Kind -eq 1 -and $state.Failure.Status -eq 0)
            }
            290 {
                $state = New-ControlNativeCreated
                Add-HermesStartupLine $state $failureLine
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Failure -and -not $state.Acked)
            }
            291 {
                $state = New-ControlNativeStopped
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new([StartupControlWriteStream]::new(0)))
                $null = Complete-ControlNative $state
                Add-HermesStartupLine $state $failureLine
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Failure)
            }
            292 {
                $state = New-ControlNativeStopped
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new([StartupControlWriteStream]::new(0)))
                Add-HermesStartupLine $state $failureLine
                $report = Complete-ControlNative $state ($finalV2.Replace('born=123', 'born=124'))
                Assert-Control ($state.Protocol -eq 1 -and $report.completed -eq 0)
            }
            293 {
                $state = New-ControlNativeStopped
                $state.InitialCommandSha256 = 'f' * 64
                Assert-Control (-not (Test-HermesStartupArm $state))
            }
            294 {
                $state = New-ControlNativeStopped
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new([StartupControlWriteStream]::new(0)))
                Add-HermesStartupLine $state $failureLine
                $report = Complete-ControlNative $state ($finalV2.Replace('parent=1', 'parent=0'))
                Assert-Control ($report.completed -eq 0 -and $report.parentUnchanged -eq 0 `
                    -and $report.cleanupComplete -eq 1)
            }
            { $_ -in 295..300 } {
                $state = New-ControlNativeStopped
                $before = Get-ControlStreamSnapshot $state.Out
                $state.AcceptedUtf8Bytes = [long]8388607
                $chunk = if ($case -eq 296) { 'xx' } else { "`n" }
                $stream = if ($case -eq 297) { $state.Err } else { $state.Out }
                Add-HermesStartupText $state $stream $chunk $true
                Assert-Control ($state.Bounds -eq 1 -and -not (Test-HermesStartupArm $state))
                $hashBefore = [Convert]::ToHexString($state.Out.Hash.GetCurrentHash())
                if ($case -eq 298) { Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=5 pid=22' }
                if ($case -eq 299) { Update-HermesStartupRows $state ('x' * 2049) }
                if ($case -eq 300) { Add-HermesStartupText $state $state.Out ('x' * 4096) $true }
                Assert-Control ($state.Protocol -eq 0 -and $state.Out.Tail -ceq '1:000> ' `
                    -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $hashBefore)
                if ($case -eq 296) {
                    Assert-Control ((Get-ControlStreamSnapshot $state.Out) -ceq $before)
                } else {
                    Assert-Control ($state.AcceptedUtf8Bytes -eq 8388608)
                }
            }
            { $_ -in 301..303 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $script:controlCommandFault = $case - 300
                $script:controlCommandFaultName = 'startup-ibp.cdb'
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($report.completed -eq 0 -and $report.error -eq 13 `
                    -and $report.initialCommandSha256 -ceq '' -and $script:controlLaunchBindings -eq 0 `
                    -and $script:controlCommandWrites.Count -eq 4 `
                    -and $script:controlCommandPins.Count -eq $(if ($case -eq 301) { 1 } else { 2 }))
            }
            304 {
                $state = New-ControlNativeStopped -WithResumed $false
                Assert-Control (-not (Test-HermesStartupArm $state) -and $state.Stopped)
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n")
                Assert-Control ((Test-HermesStartupArm $state))
            }
            305 {
                $state = New-ControlNativeStopped
                $state.Resumed.born = 124
                Assert-Control (-not (Test-HermesStartupArm $state))
            }
            306 {
                $state = New-ControlNativeStopped
                foreach ($line in @(
                    'HERMES_CDB v=1 kind=3 pid=22',
                    'Last event: 16.1: Exit process 1:16, code 25',
                    'HERMES_CDB v=1 kind=4 pid=22'
                )) { Add-HermesStartupLine $state $line }
                Assert-Control (-not (Test-HermesStartupArm $state) `
                    -and (New-HermesStartupReport $state $true $true).powershellExit37 -eq 1 `
                    -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            307 {
                $state = New-ControlNativeStopped
                $state.Out.Tail = ''
                Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=5 pid=22'
                Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupArm $state))
            }
            308 {
                $state = New-ControlNativeStopped
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new([StartupControlWriteStream]::new(0)))
                Add-HermesStartupLine $state '16:1 @ 00000000 - LdrpInitializeProcess - ERROR: Process initialization failed with status 0xc0000142'
                $report = Complete-ControlNative $state
                foreach ($name in @('DebuggerSha256', 'HelperSha256', 'CommandSha256',
                        'CreateCommandSha256', 'ExitCommandSha256', 'InitialCommandSha256')) {
                    $state[$name] = 'f' * 64
                }
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                $json = New-HermesStartupReport $state $true $true | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($report.completed -eq 1 -and $report.firstFailureKind -eq 2 `
                    -and $report.firstFailureStatus -eq 3221225794 -and $report.nativeAcceptance -eq 0 `
                    -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'D:|owned|startup-ibp|80000003|00000000|\.cdb|sample\.dll')
            }
            { $_ -in 309..310 } {
                $state = New-ControlNativeCreated
                & {
                    $armDefinition = @($driverAst.FindAll({
                        param($n)
                        $n -is [Management.Automation.Language.FunctionDefinitionAst] `
                            -and $n.Name -ceq 'Publish-TraceArm'
                    }, $true))
                    . ([scriptblock]::Create($armDefinition[0].Extent.Text))
                    function Start-Sleep { param([int]$Milliseconds) }
                    function Read-TraceControl {
                        param([string]$Path)
                        if ($physical.Closed -or $case -eq 310) {
                            Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`n")
                        }
                    }
                    $first = @(
                        'HERMES_CDB v=3 kind=5 pid=22',
                        'Last event: 16.1: Break instruction exception - code 80000003 (first chance)',
                        'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002',
                        'HERMES_CDB v=3 kind=6 pid=22'
                    ) -join "`r`n"
                    $first += "`r`n1:000> "
                    $after = @(
                        'g', $failureLine,
                        'HERMES_CDB v=1 kind=3 pid=22',
                        'Last event: 16.1: Exit process 1:16, code c0000142',
                        'HERMES_CDB v=1 kind=4 pid=22',
                        'HERMES_CDB v=1 kind=3 pid=11',
                        'Last event: b.1: Exit process 0:b, code 0',
                        'HERMES_CDB v=1 kind=4 pid=11'
                    ) -join "`r`n"
                    $after += "`r`n"
                    $outReader = if ($case -eq 309) {
                        [IO.StreamReader]::new([StartupControlContinueStream]::new($first, $after))
                    } else { New-ControlReader ($first + $after) }
                    $physical = [StartupControlWriteStream]::new(0)
                    if ($case -eq 309) {
                        $physical.AfterWrite = { $outReader.BaseStream.Release() }.GetNewClosure()
                    }
                    $debugger = [pscustomobject]@{
                        StandardInput = [IO.StreamWriter]::new($physical)
                        StandardOutput = $outReader; StandardError = New-ControlReader ''
                        ExitCode = 0
                    }
                    $debugger | Add-Member ScriptProperty HasExited { $case -eq 310 -or $physical.Closed }
                    $debugger | Add-Member ScriptMethod Dispose {}
                    $script:controlStops = 0; $script:controlStopFault = $false
                    $started = $true; $rootCreated = $false
                    $debuggerEnded = $false; $drainsEnded = $false
                    Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                    Start-TraceRead $state.Err $debugger.StandardError.BaseStream
                    . $controlDriver
                    $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                    if ($case -eq 309) {
                        Assert-Control ($physical.Writes -eq 1 -and $physical.Closed `
                            -and $state.InputClosed -and $state.Continued -and $report.completed -eq 1 `
                            -and $report.cleanupComplete -eq 1 -and $report.firstFailureKind -eq 1 `
                            -and $state.Out.End -and $state.Err.End -and $script:controlStops -eq 1)
                    } else {
                        Assert-Control ($physical.Writes -eq 0 -and $physical.Closed `
                            -and -not $state.Acked -and $report.completed -eq 0 `
                            -and $report.firstFailureKind -eq 0 -and $state.Protocol -eq 1)
                    }
                }
            }
            311 {
                $state = New-ControlNativeCreated
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`ntrailing")
                Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupArm $state))
            }
            312 {
                $state = New-ControlNativeCreated
                foreach ($line in @(
                    'HERMES_CDB v=1 kind=3 pid=22',
                    'Last event: 16.1: Exit process 1:16, code 25',
                    'HERMES_CDB v=1 kind=4 pid=22',
                    'HERMES_CDB v=1 kind=3 pid=11',
                    'Last event: b.1: Exit process 0:b, code 0',
                    'HERMES_CDB v=1 kind=4 pid=11'
                )) { Add-HermesStartupLine $state $line }
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" +
                    $finalV2.Replace('exit=3221225794', 'exit=37') + "`n")
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.completed -eq 0 -and $report.status -eq 3 `
                    -and $report.armingVerified -eq 0 -and $report.powershellExit37 -eq 1 `
                    -and $report.firstFailureKind -eq 0 -and $report.cleanupComplete -eq 1 `
                    -and $report.parentUnchanged -eq 1 -and $report.nativeAcceptance -eq 0)
            }
            { $_ -in 313..314 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $pipe = New-ControlPipe
                try {
                    $text = 'x' * $(if ($case -eq 313) { 1024 } else { 2048 })
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Write-ControlPipe $pipe ([Text.Encoding]::UTF8.GetBytes($text))
                    Receive-ControlPipe $state.Out $pipe $false
                    $pending = $state.Out.Pending
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and $state.Out.Count -eq $text.Length -and $state.Bounds -eq 0 `
                        -and $state.AcceptedUtf8Bytes -eq $text.Length `
                        -and $state.Out.Prefix -ceq $text -and $state.Out.Tail -ceq $text `
                        -and [object]::ReferenceEquals($pending, $state.Out.Pending))
                    $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($text)))
                    Assert-Control ([Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $expected)
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $false
                    Assert-Control ($state.Out.End -and $null -eq $state.Out.Pending)
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            { $_ -in 315..316 } {
                $state = New-ControlNativeCreated -Acknowledge $false
                $state.Created = $null; $state.Initial = 0
                $pipe = New-ControlPipe
                try {
                    $opening = "HERMES_CDB v=1 kind=0 pid=11`n"
                    $created = @(
                        'HERMES_CDB v=3 kind=1 pid=22',
                        'Last event: 16.1: Create process 1:16',
                        'HERMES_CDB v=3 kind=2 pid=22'
                    ) -join "`n"
                    $boundary = if ($case -eq 315) { 1024 } else { 2048 }
                    $split = if ($case -eq 315) { 13 } else { 0 }
                    $text = $opening + ('x' * ($boundary - $opening.Length - 1 - $split)) +
                        "`n" + $created + "`n"
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Write-ControlPipe $pipe ([Text.Encoding]::UTF8.GetBytes($text.Substring(0, $boundary)))
                    Receive-ControlPipe $state.Out $pipe $true
                    # Partial CPR output is not trace authority, even where
                    # Ready now permits the fixed diagnostic's native Resume.
                    Assert-Control ($state.Initial -eq 11 -and $null -eq $state.Created `
                        -and $state.Out.Count -eq $boundary -and -not (Test-HermesStartupCreatedBinding $state) `
                        -and -not (Test-HermesStartupArm $state))
                    Write-ControlPipe $pipe ([Text.Encoding]::UTF8.GetBytes($text.Substring($boundary)))
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                        -and $state.Out.Count -eq $text.Length)
                    Set-HermesStartupCreatedAcknowledged $state
                    Assert-Control ($state.CreatedAcked -and $null -ne $state.CreatedBinding `
                        -and -not $state.ContinueAttempted -and -not (Test-HermesStartupArm $state))
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($state.Out.End -and $state.Protocol -eq 0)
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            { $_ -in 317..318 } {
                $state = New-ControlNativeCreated
                $pipe = New-ControlPipe
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    $ibp = (@(
                        'HERMES_CDB v=3 kind=5 pid=22',
                        'Last event: 16.1: Break instruction exception - code 80000003 (first chance)',
                        'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002',
                        'HERMES_CDB v=3 kind=6 pid=22'
                    ) -join "`r`n") + "`r`n"
                    $boundary = if ($case -eq 317) { 1024 } else { 2048 }
                    $split = if ($case -eq 317) { 3 } else { 0 }
                    $first = ('x' * ($boundary - $ibp.Length - 1 - $split)) +
                        "`n" + $ibp + '1:000> '
                    $after = (@(
                        'g', $failureLine,
                        'HERMES_CDB v=1 kind=3 pid=22',
                        'Last event: 16.1: Exit process 1:16, code c0000142',
                        'HERMES_CDB v=1 kind=4 pid=22',
                        'HERMES_CDB v=1 kind=3 pid=11',
                        'Last event: b.1: Exit process 0:b, code 0',
                        'HERMES_CDB v=1 kind=4 pid=11'
                    ) -join "`r`n") + "`r`n"
                    $afterBytes = [Text.Encoding]::UTF8.GetBytes($after)
                    $physical.AfterWrite = {
                        $pipe.Writer.Write($afterBytes, 0, $afterBytes.Length)
                        $pipe.Writer.Flush()
                    }.GetNewClosure()
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Write-ControlPipe $pipe ([Text.Encoding]::UTF8.GetBytes($first.Substring(0, $boundary)))
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($state.Out.Count -eq $boundary -and $null -ne $state.Arm `
                        -and -not $state.Stopped -and -not (Test-HermesStartupArm $state) `
                        -and $physical.Writes -eq 0 -and $null -eq $state.Failure)
                    Write-ControlPipe $pipe ([Text.Encoding]::UTF8.GetBytes($first.Substring($boundary)))
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and $state.Stopped -and (Test-HermesStartupArm $state) `
                        -and (Get-HermesStartupTypedFlags $state) -eq 2 -and $physical.Writes -eq 0)
                    Invoke-HermesStartupContinue $state $writer
                    Assert-Control ($physical.Writes -eq 1 -and $physical.Closed -and $state.InputClosed `
                        -and $state.Continued -and $state.Acked `
                        -and [Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq "g`n")
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and $null -ne $state.Failure -and $state.Failure.Kind -eq 1 `
                        -and $state.Exits.Count -eq 2 -and $state.Protocol -eq 0 `
                        -and $state.Out.Count -eq ($first.Length + $after.Length))
                    Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`n")
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $true
                    $report = New-HermesStartupReport $state $true $state.Out.End
                    Assert-Control ($report.completed -eq 1 -and $report.cleanupComplete -eq 1 `
                        -and $report.advisory -eq 1 -and $report.nativeAcceptance -eq 0 `
                        -and $report.originalGatePassed -eq 0)
                } finally { $writer.Dispose(); Close-ControlPipe $state.Out $pipe }
            }
            319 {
                $state = Native-StartupTrace\New-HermesStartupState
                $pipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Write-ControlPipe $pipe ([byte[]]@(0xc3))
                    Receive-ControlPipe $state.Out $pipe $false
                    Assert-Control ($state.Out.Count -eq 0 -and $state.AcceptedUtf8Bytes -eq 0)
                    Write-ControlPipe $pipe ([byte[]]@(0xa9, 0xe2, 0x82))
                    Receive-ControlPipe $state.Out $pipe $false
                    Assert-Control ($state.Out.Count -eq 1 -and $state.AcceptedUtf8Bytes -eq 2)
                    Write-ControlPipe $pipe ([byte[]]@(0xac, 0xf0, 0x9f, 0x98))
                    Receive-ControlPipe $state.Out $pipe $false
                    Assert-Control ($state.Out.Count -eq 2 -and $state.AcceptedUtf8Bytes -eq 5)
                    Write-ControlPipe $pipe ([byte[]]@(0x80))
                    Receive-ControlPipe $state.Out $pipe $false
                    $text = ([string][char]0xe9) + ([string][char]0x20ac) + [char]::ConvertFromUtf32(0x1f600)
                    $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($text)))
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and $state.Out.Count -eq 4 -and $state.AcceptedUtf8Bytes -eq 9 `
                        -and $state.Out.Prefix -ceq $text `
                        -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $expected)
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $false
                    Assert-Control ($state.Out.End -and $state.Bounds -eq 0 -and $state.Error -eq 0)
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            320 {
                $state = Native-StartupTrace\New-HermesStartupState
                $pipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Write-ControlPipe $pipe ([byte[]]@(0xf0, 0x9f, 0x98))
                    Receive-ControlPipe $state.Out $pipe $false
                    Assert-Control ($state.Out.Count -eq 0)
                    $bytes = [byte[]]::new(4096)
                    [Array]::Fill($bytes, [byte]0x78)
                    $bytes[0] = 0x80
                    Write-ControlPipe $pipe $bytes
                    Receive-ControlPipe $state.Out $pipe $false
                    $text = [char]::ConvertFromUtf32(0x1f600) + ('x' * 4095)
                    $expected = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($text)))
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and $state.Out.Count -eq 4097 -and $state.AcceptedUtf8Bytes -eq 4099 `
                        -and $state.Out.Prefix.Length -eq 4096 -and $state.Out.Tail.Length -le 4096 `
                        -and $state.Bounds -eq 0 -and $state.Error -eq 0 `
                        -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $expected)
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $false
                    Assert-Control ($state.Out.End)
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            { $_ -in 321..323 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $pipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    $bytes = switch ($case) {
                        321 { [byte[]]@(0xe2, 0x82) }
                        322 { [byte[]]@(0xc3, 0x28) }
                        323 { [byte[]]@(0xef, 0xbb, 0xbf, 0x78) }
                    }
                    Write-ControlPipe $pipe $bytes
                    $refused = $false
                    try { Receive-ControlPipe $state.Out $pipe $false } catch { $refused = $true }
                    if ($case -eq 321) {
                        Assert-Control (-not $refused -and $state.Out.Count -eq 0 -and -not $state.Out.End)
                        $pipe.Writer.Dispose()
                        try { Receive-ControlPipe $state.Out $pipe $false } catch { $refused = $true }
                    }
                    if ($case -eq 323) {
                        Assert-Control (-not $refused -and $state.Out.Count -eq 2 `
                            -and $state.AcceptedUtf8Bytes -eq 4 -and $state.Bounds -eq 0 `
                            -and $state.Out.Prefix -ceq (([string][char]0xfeff) + 'x'))
                        $pipe.Writer.Dispose()
                        Receive-ControlPipe $state.Out $pipe $false
                        Assert-Control ($state.Out.End -and -not $state.Out.Faulted)
                    } else {
                        Assert-Control ($refused -and $state.Out.Faulted -and $state.Error -eq 13 `
                            -and $state.Out.Count -eq 0 -and -not $state.Out.End `
                            -and (New-HermesStartupReport $state $true $true).completed -eq 0)
                        Start-TraceRead $state.Out $pipe.Reader.BaseStream
                        Assert-Control ($null -eq $state.Out.Pending)
                    }
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            { $_ -in 324..326 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $state.AcceptedUtf8Bytes = [long]8388607
                $outPipe = New-ControlPipe
                $errPipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $outPipe.Reader.BaseStream
                    Start-TraceRead $state.Err $errPipe.Reader.BaseStream
                    $selected = if ($case -eq 325) { $state.Err } else { $state.Out }
                    $pipe = if ($case -eq 325) { $errPipe } else { $outPipe }
                    $other = if ($case -eq 325) { $state.Out } else { $state.Err }
                    $bytes = if ($case -eq 326) { [byte[]]@(0xc3, 0xa9) } else { [byte[]]@(0x78) }
                    $otherPending = $other.Pending
                    Write-ControlPipe $pipe $bytes
                    Receive-ControlPipe $selected $pipe $true
                    $outBefore = Get-ControlStreamSnapshot $state.Out
                    $errBefore = Get-ControlStreamSnapshot $state.Err
                    Start-TraceRead $state.Out $outPipe.Reader.BaseStream
                    Start-TraceRead $state.Err $errPipe.Reader.BaseStream
                    Receive-TraceRead $state.Out $outPipe.Reader.BaseStream $true
                    Receive-TraceRead $state.Err $errPipe.Reader.BaseStream $false
                    $emptyHash = [Convert]::ToHexString(
                        [Security.Cryptography.SHA256]::HashData([byte[]]::new(0)))
                    Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq $(if ($case -eq 326) { 8388607 } else { 8388608 }) `
                        -and $selected.Count -eq [int]($case -ne 326) -and $other.Count -eq 0 `
                        -and $selected.Tail -ceq '' -and $selected.Prefix -ceq '' `
                        -and [Convert]::ToHexString($selected.Hash.GetCurrentHash()) -ceq $emptyHash `
                        -and $null -eq $selected.Pending -and -not $state.Out.End -and -not $state.Err.End `
                        -and [object]::ReferenceEquals($otherPending, $other.Pending) `
                        -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                        -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore `
                        -and (New-HermesStartupReport $state $true $true).status -eq 7)
                } finally {
                    Close-ControlPipe $state.Out $outPipe
                    Close-ControlPipe $state.Err $errPipe
                }
            }
            327 {
                $state = Native-StartupTrace\New-HermesStartupState
                $pipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    $pending = $state.Out.Pending
                    $handle = $pipe.Reader.BaseStream.SafePipeHandle
                    Assert-Control (-not $pending.IsCompleted -and $pipe.Writer.CanWrite)
                    $watch = [Diagnostics.Stopwatch]::StartNew()
                    Close-TraceRead $state.Out $pipe.Reader
                    Assert-Control ($watch.ElapsedMilliseconds -lt 5000 `
                        -and [object]::ReferenceEquals($pending, $state.Out.Pending) `
                        -and -not $state.Out.End -and $state.Out.Count -eq 0 `
                        -and $state.AcceptedUtf8Bytes -eq 0)
                    # Closure cancellation differs across pipe implementations.
                    # A still-pending task is a cleanup fault, never EOF/success.
                    if (-not $pending.IsCompleted -or $pending.IsFaulted -or $pending.IsCanceled) {
                        Assert-Control ($state.CleanupFault -eq 13)
                    }
                    $pipe.Writer.Dispose()
                    try { $null = $pending.Wait(5000) } catch {}
                    Assert-Control ($pending.IsCompleted -and $handle.IsClosed -and -not $state.Out.End)
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            328 {
                $state = Native-StartupTrace\New-HermesStartupState
                $reader = [IO.StreamReader]::new([StartupControlFaultStream]::new())
                try {
                    Start-TraceRead $state.Out $reader.BaseStream
                    $pending = $state.Out.Pending
                    Assert-Control ($pending.IsCompleted -and $pending.IsFaulted -and $state.CleanupFault -eq 0)
                    Close-TraceRead $state.Out $reader
                    Assert-Control ($state.CleanupFault -eq 13 -and -not $state.Out.End `
                        -and [object]::ReferenceEquals($pending, $state.Out.Pending))
                } finally { $reader.Dispose() }
            }
            329 {
                $state = Native-StartupTrace\New-HermesStartupState
                $pipe = New-ControlPipe
                $other = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    $pending = $state.Out.Pending
                    $refused = $false
                    try { Start-TraceRead $state.Out $other.Reader.BaseStream } catch { $refused = $true }
                    Assert-Control ($refused -and [object]::ReferenceEquals($pending, $state.Out.Pending))
                    Write-ControlPipe $pipe ([Text.Encoding]::UTF8.GetBytes("ok`n"))
                    Assert-Control ($pending.Wait(5000))
                    $refused = $false
                    try { Receive-TraceRead $state.Out $other.Reader.BaseStream $true } catch { $refused = $true }
                    Assert-Control ($refused -and $state.Out.Count -eq 0 `
                        -and [object]::ReferenceEquals($pending, $state.Out.Pending))
                    Receive-TraceRead $state.Out $pipe.Reader.BaseStream $true
                    Assert-Control ($state.Out.Count -eq 3 -and $state.Protocol -eq 0 -and $state.Error -eq 0)
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($state.Out.End)
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Assert-Control ($null -eq $state.Out.Pending)
                } finally {
                    Close-ControlPipe $state.Out $pipe
                    $other.Writer.Dispose(); $other.Reader.Dispose()
                }
            }
            { $_ -in 330..331 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $pipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    $text = if ($case -eq 330) { ('x' * 4095) + "`n" } else { 'x' * 4096 }
                    Write-ControlPipe $pipe ([Text.Encoding]::UTF8.GetBytes($text))
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and $state.Out.Count -eq 4096 -and $state.Out.Prefix.Length -eq 4096 `
                        -and $state.Bounds -eq 0 -and $state.Protocol -eq 0)
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($state.Out.End -and $state.Protocol -eq [int]($case -eq 331) `
                        -and $state.Out.Tail.Length -eq $(if ($case -eq 330) { 0 } else { 4096 }))
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            332 {
                $state = Native-StartupTrace\New-HermesStartupState
                $readCalls = @($driverAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.InvokeMemberExpressionAst] `
                        -and $node.Member.Extent.Text -ceq 'ReadAsync'
                }, $true))
                Assert-Control ($readCalls.Count -eq 1 -and $readCalls[0].Expression.Extent.Text -ceq '$Source' `
                    -and $readCalls[0].Arguments[0].Extent.Text -ceq '$Stream.Bytes')
                $pumpCalls = @($driverAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.CommandAst] `
                        -and $node.GetCommandName() -in @('Start-TraceRead', 'Receive-TraceRead')
                }, $true))
                foreach ($call in $pumpCalls) {
                    $argument = $call.CommandElements[2].Extent.Text
                    Assert-Control ($argument -ceq '$Source' -or $argument -cmatch '\A\$debugger\.Standard(?:Output|Error)\.BaseStream\z')
                }
                Assert-Control ($state.Out.Bytes.Length -eq 4096 -and $state.Out.Buffer.Length -eq 4096 `
                    -and $state.Out.Decoder.Fallback -is [Text.DecoderExceptionFallback] `
                    -and $state.NativeProtocol -and -not $state.Out.Faulted)
            }
            333 {
                $state = Native-StartupTrace\New-HermesStartupState
                $state.AcceptedUtf8Bytes = [long](8388608 - 4098)
                $pipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Write-ControlPipe $pipe ([byte[]]@(0xf0, 0x9f, 0x98))
                    Receive-ControlPipe $state.Out $pipe $false
                    $bytes = [byte[]]::new(4096)
                    [Array]::Fill($bytes, [byte]0x78)
                    $bytes[0] = 0x80
                    Write-ControlPipe $pipe $bytes
                    Receive-ControlPipe $state.Out $pipe $false
                    $before = Get-ControlStreamSnapshot $state.Out
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Receive-TraceRead $state.Out $pipe.Reader.BaseStream $false
                    Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                        -and $state.Out.Count -eq 4096 -and $state.Out.Prefix.Length -eq 0 `
                        -and $state.Out.Tail.Length -eq 0 -and $null -eq $state.Out.Pending `
                        -and -not $state.Out.End -and $state.Error -eq 0 `
                        -and (Get-ControlStreamSnapshot $state.Out) -ceq $before)
                    $emptyHash = [Convert]::ToHexString(
                        [Security.Cryptography.SHA256]::HashData([byte[]]::new(0)))
                    Assert-Control ([Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $emptyHash)
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            334 {
                $state = Native-StartupTrace\New-HermesStartupState
                $state.AcceptedUtf8Bytes = [long]8388607
                $outPipe = New-ControlPipe
                $errPipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $outPipe.Reader.BaseStream
                    Start-TraceRead $state.Err $errPipe.Reader.BaseStream
                    Write-ControlPipe $outPipe ([byte[]]@(0xe2, 0x82))
                    Receive-ControlPipe $state.Out $outPipe $false
                    Assert-Control ($state.Out.Count -eq 0 -and $state.AcceptedUtf8Bytes -eq 8388607)
                    Write-ControlPipe $errPipe ([byte[]]@(0x78))
                    Receive-ControlPipe $state.Err $errPipe $false
                    $pending = $state.Out.Pending
                    $before = Get-ControlStreamSnapshot $state.Out
                    $outPipe.Writer.Dispose()
                    Assert-Control ($pending.Wait(5000))
                    Receive-TraceRead $state.Out $outPipe.Reader.BaseStream $true
                    Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                        -and -not $state.Out.End -and -not $state.Out.Faulted -and $state.Error -eq 0 `
                        -and [object]::ReferenceEquals($pending, $state.Out.Pending) `
                        -and (Get-ControlStreamSnapshot $state.Out) -ceq $before)
                } finally {
                    Close-ControlPipe $state.Out $outPipe
                    Close-ControlPipe $state.Err $errPipe
                }
            }
            335 {
                $state = Native-StartupTrace\New-HermesStartupState
                $reader = [IO.StreamReader]::new([StartupControlFaultStream]::new())
                try {
                    Start-TraceRead $state.Out $reader.BaseStream
                    $pending = $state.Out.Pending
                    Assert-Control ($pending.IsCompleted -and $pending.IsFaulted)
                    $refused = $false
                    try { Receive-TraceRead $state.Out $reader.BaseStream $false } catch { $refused = $true }
                    Start-TraceRead $state.Out $reader.BaseStream
                    Assert-Control ($refused -and $state.Out.Faulted -and $state.Error -eq 13 `
                        -and -not $state.Out.End -and $state.Out.Count -eq 0 `
                        -and [object]::ReferenceEquals($pending, $state.Out.Pending))
                    Close-TraceRead $state.Out $reader
                    Assert-Control ($state.CleanupFault -eq 13 -and $pending.IsFaulted)
                } finally { $reader.Dispose() }
            }
            336 {
                $state = New-ControlNativeCreated -Acknowledge $false
                Assert-Control ($state.Observations.CreateCloseFirst -eq 5 `
                    -and $state.Observations.ReadyFirst -eq 199 -and $state.Observations.CreatedJointFirst -eq 199 `
                    -and $state.Observations.CreatedCheckFirst -eq 0 -and $state.Observations.CreatedCheckSeen -eq 0)
                $refused = $false
                try { Invoke-ControlCreatedPublication } catch { $refused = $true }
                Assert-Control ($refused -and $state.Observations.CreatedCheckSeen -eq 1 `
                    -and $state.Observations.CreatedCheckFirst -eq 199 -and $state.Observations.CreatedPublishAttempted -eq 1 `
                    -and -not $state.CreatedAcked -and $null -eq $state.CreatedBinding -and -not $state.ContinueAttempted)
            }
            337 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder BeforeClose
                Assert-Control ($state.Observations.ReadyFirst -eq 3 -and $state.Observations.CreateCloseFirst -eq 199 `
                    -and $state.Observations.CreatedJointFirst -eq 199)
                Set-HermesStartupCreatedAcknowledged $state
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`n")
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($report.observations.ReadyFirst -eq 3 -and $report.observations.CreateCloseFirst -eq 199 `
                    -and $report.observations.CreatedJointFirst -eq 199 -and $report.observations.CreatedPublishAttempted -eq 0 `
                    -and $report.observations.CreatedAcknowledged -eq 1 -and $report.observations.FinalRowPresent -eq 1)
            }
            338 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.CreateCloseFirst -eq 5 -and $state.Observations.ReadyFirst -eq 0 `
                    -and $state.Observations.CreatedJointFirst -eq 0 -and $state.Observations.CreatedCheckFirst -eq 0 `
                    -and $state.Observations.CreatedCheckSeen -eq 1 -and $state.Observations.CreatedPublishAttempted -eq 0)
                Update-HermesStartupRows $state ($readyV2 + "`n")
                $refused = $false
                try { Invoke-ControlCreatedPublication } catch { $refused = $true }
                Assert-Control ($refused -and $state.Observations.ReadyFirst -eq 199 `
                    -and $state.Observations.CreatedJointFirst -eq 199 -and $state.Observations.CreatedCheckFirst -eq 199 `
                    -and $state.Observations.CreatedPublishAttempted -eq 1 -and -not $state.CreatedAcked)
            }
            339 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2.Replace('resumed=1', 'resumed=0') + "`n")
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.ReadyFirst -eq 199 -and $state.Observations.CreatedJointFirst -eq 199 `
                    -and $state.Observations.CreatedCheckFirst -eq 79 -and $state.Observations.CreatedPublishAttempted -eq 0 `
                    -and $null -ne $state.Final -and -not $state.CreatedAcked -and $state.Protocol -eq 0)
            }
            340 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                foreach ($line in @(
                    'HERMES_CDB v=1 kind=3 pid=22', 'Last event: 16.1: Exit process 1:16, code 0',
                    'HERMES_CDB v=1 kind=4 pid=22', 'HERMES_CDB v=1 kind=3 pid=11',
                    'Last event: b.1: Exit process 0:b, code 0', 'HERMES_CDB v=1 kind=4 pid=11'
                )) { Add-HermesStartupLine $state $line }
                Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2.Replace('resumed=1', 'resumed=0').Replace(
                    'exit=3221225794', 'exit=0') + "`n")
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.CreateCloseFirst -eq 5 -and $state.Observations.ReadyFirst -eq 119 `
                    -and $state.Observations.CreatedJointFirst -eq 119 -and $state.Observations.CreatedCheckFirst -eq 127 `
                    -and $state.Observations.CreatedPublishAttempted -eq 0 -and $state.Exits.Count -eq 2 `
                    -and -not $state.CreatedAcked -and $state.Protocol -eq 0)
            }
            341 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder BeforeClose -DeferClose
                Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2.Replace('resumed=1', 'resumed=0') + "`n")
                Assert-Control ($state.Observations.ReadyFirst -eq 3 -and $state.Observations.CreateCloseFirst -eq 0 `
                    -and $state.Observations.CreatedJointFirst -eq 0)
                Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=2 pid=22'
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.ReadyFirst -eq 3 -and $state.Observations.CreateCloseFirst -eq 79 `
                    -and $state.Observations.CreatedJointFirst -eq 79 -and $state.Observations.CreatedCheckFirst -eq 79 `
                    -and $state.Observations.CreatedPublishAttempted -eq 0 -and $state.Protocol -eq 0)
            }
            342 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                $hash = $state.CommandSha256
                $state.CommandSha256 = ''
                Update-HermesStartupRows $state ($readyV2 + "`n")
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.ReadyFirst -eq 7 -and $state.Observations.CreatedJointFirst -eq 7 `
                    -and $state.Observations.CreatedCheckFirst -eq 7 -and $state.Observations.CreatedPublishAttempted -eq 0)
                $state.CommandSha256 = $hash
                $refused = $false
                try { Invoke-ControlCreatedPublication } catch { $refused = $true }
                Assert-Control ($refused -and $state.Observations.ReadyFirst -eq 7 `
                    -and $state.Observations.CreatedJointFirst -eq 7 -and $state.Observations.CreatedCheckFirst -eq 7 `
                    -and $state.Observations.CreatedPublishAttempted -eq 1 -and -not $state.CreatedAcked)
            }
            { $_ -in 343..344 } {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                $fault = if ($case -eq 343) { 'Protocol' } else { 'Unknown' }
                $state[$fault] = 1
                Update-HermesStartupRows $state ($readyV2 + "`n")
                Invoke-ControlCreatedPublication
                $expected = if ($case -eq 343) { 4103 } else { 8199 }
                Assert-Control ($state.Observations.ReadyFirst -eq $expected `
                    -and $state.Observations.CreatedJointFirst -eq $expected `
                    -and $state.Observations.CreatedCheckFirst -eq $expected `
                    -and $state.Observations.CreatedPublishAttempted -eq 0 -and $state[$fault] -eq 1 `
                    -and -not $state.CreatedAcked -and (New-HermesStartupReport $state $true $true).completed -eq 0)
            }
            345 {
                $state = New-ControlNativeCreated -Acknowledge $false
                Add-HermesStartupLine $state 'HERMES_CDB v=1 kind=3 pid=22'
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.CreatedJointFirst -eq 199 -and $state.Observations.CreatedCheckFirst -eq 327 `
                    -and $state.Observations.CreatedPublishAttempted -eq 0 -and -not $state.CreatedAcked `
                    -and $state.Block.Kind -eq 3 -and $state.Protocol -eq 0)
            }
            346 {
                $state = New-ControlNativeCreated -Acknowledge $false
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n")
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.CreatedCheckFirst -eq 583 -and $state.Observations.CreatedPublishAttempted -eq 0 `
                    -and -not $state.CreatedAcked -and $null -ne $state.Resumed -and $state.Protocol -eq 0)
            }
            347 {
                $state = New-ControlNativeCreated -WithResumed $false
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.CreatedCheckFirst -eq 3143 -and $state.Observations.CreatedPublishAttempted -eq 0 `
                    -and $state.CreatedAcked -and $null -ne $state.CreatedBinding -and $state.Protocol -eq 0)
            }
            348 {
                $state = New-ControlNativeStopped -Flags ffffffff
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new([StartupControlWriteStream]::new(0)))
                Add-HermesStartupLine $state $failureLine
                $null = Complete-ControlNative $state
                $phases = @('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst', 'CreatedCheckFirst')
                foreach ($name in $phases + @('CreatedCheckSeen', 'CreatedPublishAttempted',
                        'RowsReads', 'RowsMissing', 'RowsEmpty', 'RowsFirstDataLength', 'RowsFirstDataRead', 'RowsReadyRead')) {
                    foreach ($bad in @('private S-1-5-21-123 --command', $null, $true, [long]7, -1, 1.5, 8388609)) {
                        $state.Observations[$name] = $bad
                        Assert-Control ((New-HermesStartupReport $state $true $true).observations[$name] -eq 0)
                    }
                }
                foreach ($name in $phases) {
                    $state.Observations[$name] = 2
                    Assert-Control ((New-HermesStartupReport $state $true $true).observations[$name] -eq 0)
                }
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                $maxima = @{
                    SdkCommandErrors = 15; SdkLoadLibraryWin32Error = [long][uint32]::MaxValue
                    SdkLoadLibraryPathShape = 7; SdkSelectedExtensionChain = 3
                    SdkLoadLibraryRelation = 23; SdkLoadLibraryForm = 15
                    SdkLoadLibraryLexicalModule = 12; SdkLoadLibraryLexicalSuffix = 5
                    SdkLoadLibraryDisplay = 1023; MarkerMalformedKind = 2
                    CprFilter = 13; EprFilter = 13; CprCommandShape = 9; EprCommandShape = 9
                    CprCommandTransform = 19; EprCommandTransform = 19
                    CprPayloadLength = 4096; EprPayloadLength = 4096
                    CprSourceLength = 4096; EprSourceLength = 4096
                }
                foreach ($name in $maxima.Keys) { $state.Observations[$name] = $maxima[$name] }
                foreach ($name in $phases) { $state.Observations[$name] = 16383 }
                foreach ($name in @('CreatedCheckSeen', 'CreatedPublishAttempted')) { $state.Observations[$name] = 1 }
                foreach ($name in @('RowsFirstDataLength', 'RowsFirstDataRead', 'RowsReadyRead')) { $state.Observations[$name] = 2049 }
                foreach ($name in @('DebuggerSha256', 'HelperSha256', 'CommandSha256',
                        'CreateCommandSha256', 'ExitCommandSha256', 'InitialCommandSha256')) { $state[$name] = 'f' * 64 }
                foreach ($name in @('Out', 'Err')) {
                    $state[$name].Count = [long]8388608
                    $state[$name].Prefix = 'x' * 4096
                    $state[$name].Fingerprint = 'f' * 64
                }
                $state.AcceptedUtf8Bytes = [long]8388608
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                $script:controlReportBoundBytes = [Text.Encoding]::UTF8.GetByteCount($json + "`n")
                Assert-Control ($script:controlReportBoundBytes -le 4096 `
                    -and $json -notmatch 'private|S-1-|--command|sample\.dll|D:|startup-ibp' `
                    -and $report.advisory -eq 1 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                foreach ($name in $phases) { Assert-Control ($report.observations[$name] -eq 16383) }
            }
            { $_ -in 349..350 } {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                $before = @($state.Observations.ReadyFirst, $state.Observations.CreateCloseFirst,
                    $state.Observations.CreatedJointFirst, $state.Observations.CreatedCheckFirst,
                    $state.Observations.CreatedCheckSeen, $state.Observations.CreatedPublishAttempted) -join ','
                if ($case -eq 349) { $state.AcceptedUtf8Bytes = [long]8388608 } else { $state.Bounds = 1 }
                foreach ($name in @('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst', 'CreatedCheckFirst')) {
                    Set-HermesStartupCreatedPhase $state $name
                }
                if ($case -eq 350) { Invoke-ControlCreatedPublication }
                $after = @($state.Observations.ReadyFirst, $state.Observations.CreateCloseFirst,
                    $state.Observations.CreatedJointFirst, $state.Observations.CreatedCheckFirst,
                    $state.Observations.CreatedCheckSeen, $state.Observations.CreatedPublishAttempted) -join ','
                Assert-Control ($before -ceq $after -and -not $state.CreatedAcked -and $state.Protocol -eq 0)
            }
            351 {
                $state = New-HermesStartupState
                foreach ($name in @('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst', 'CreatedCheckFirst')) {
                    Set-HermesStartupCreatedPhase $state $name
                }
                $report = New-HermesStartupReport $state $true $true
                Assert-Control (-not $state.Observations.ContainsKey('ReadyFirst') `
                    -and -not $report.observations.Contains('ReadyFirst') -and $report.observations.Count -eq 56 `
                    -and -not $state.NativeProtocol -and $report.nativeAcceptance -eq 0)
            }
            352 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder BeforeClose -DeferClose
                Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=2 pid=23'
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Protocol -eq 1 -and $state.Observations.ReadyFirst -eq 3 `
                    -and $state.Observations.CreateCloseFirst -eq 0 -and $state.Observations.CreatedJointFirst -eq 0 `
                    -and $state.Observations.CreatedCheckSeen -eq 1 -and $state.Observations.CreatedCheckFirst -eq 4099 `
                    -and $state.Observations.CreatedPublishAttempted -eq 0 -and $null -eq $state.Created)
            }
            353 {
                $state = New-ControlNativeCreated -Acknowledge $false
                $before = @((Test-HermesStartupCreated $state), (Test-HermesStartupArm $state),
                    $state.Protocol, $state.Unknown, $state.Bounds, $state.AcceptedUtf8Bytes,
                    $state.CreatedAcked, $state.ContinueAttempted, $state.Continued) -join ','
                $facts = Get-HermesStartupCreatedPhase $state
                Set-HermesStartupCreatedPhase $state 'CreatedCheckFirst'
                $after = @((Test-HermesStartupCreated $state), (Test-HermesStartupArm $state),
                    $state.Protocol, $state.Unknown, $state.Bounds, $state.AcceptedUtf8Bytes,
                    $state.CreatedAcked, $state.ContinueAttempted, $state.Continued) -join ','
                Assert-Control ($facts -eq 199 -and $before -ceq $after `
                    -and $state.Observations.CreatedPublishAttempted -eq 0 -and $null -eq $state.InputPending)
            }
            354 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder BeforeClose -DeferClose
                $pipe = New-ControlPipe
                try {
                    Start-TraceRead $state.Out $pipe.Reader.BaseStream
                    Write-ControlPipe $pipe ([Text.Encoding]::ASCII.GetBytes('HERMES_CDB v=3 kind=2'))
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($state.Observations.CreateCloseFirst -eq 0 -and $state.Observations.CreatedJointFirst -eq 0)
                    Write-ControlPipe $pipe ([Text.Encoding]::ASCII.GetBytes(" pid=22`n"))
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($pipe.Writer.CanWrite -and -not $state.Out.End `
                        -and $state.Observations.ReadyFirst -eq 3 -and $state.Observations.CreateCloseFirst -eq 199 `
                        -and $state.Observations.CreatedJointFirst -eq 199 -and (Test-HermesStartupCreated $state))
                    $pipe.Writer.Dispose()
                    Receive-ControlPipe $state.Out $pipe $true
                    Assert-Control ($state.Out.End -and $state.Protocol -eq 0)
                } finally { Close-ControlPipe $state.Out $pipe }
            }
            355 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent -DeferClose
                & {
                    $definition = @($driverAst.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
                            -and $node.Name -ceq 'Publish-TraceCreated'
                    }, $true))
                    . ([scriptblock]::Create($definition[0].Extent.Text))
                    function Read-TraceControl {
                        param([string]$Path)
                        Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2.Replace(
                            'resumed=1', 'resumed=0').Replace('exit=3221225794', 'exit=0') + "`n")
                    }
                    $text = (@(
                        'HERMES_CDB v=3 kind=2 pid=22',
                        'HERMES_CDB v=1 kind=3 pid=22', 'Last event: 16.1: Exit process 1:16, code 0',
                        'HERMES_CDB v=1 kind=4 pid=22', 'HERMES_CDB v=1 kind=3 pid=11',
                        'Last event: b.1: Exit process 0:b, code 0', 'HERMES_CDB v=1 kind=4 pid=11'
                    ) -join "`n") + "`n"
                    $debugger = [pscustomobject]@{
                        StandardOutput = New-ControlReader $text; StandardError = New-ControlReader ''
                        HasExited = $true; ExitCode = 0
                    }
                    try {
                        Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                        Start-TraceRead $state.Err $debugger.StandardError.BaseStream
                        Invoke-TraceCollection
                        Assert-Control ($state.Observations.CreateCloseFirst -eq 5 -and $state.Observations.ReadyFirst -eq 119 `
                            -and $state.Observations.CreatedJointFirst -eq 119 -and $state.Observations.CreatedCheckFirst -eq 127 `
                            -and $state.Observations.CreatedPublishAttempted -eq 0 -and -not $state.CreatedAcked `
                            -and $state.Out.End -and $state.Err.End -and $state.Protocol -eq 0)
                    } finally { $debugger.StandardOutput.Dispose(); $debugger.StandardError.Dispose() }
                }
            }
            356 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                $source = [IO.MemoryStream]::new()
                try { Invoke-ControlRowsRead $source $false } finally { $source.Dispose() }
                Invoke-ControlRowsRead ([IO.MemoryStream]::new())
                Assert-Control ($state.Observations.RowsReads -eq 2 -and $state.Observations.RowsMissing -eq 1 `
                    -and $state.Observations.RowsEmpty -eq 1 -and $state.Observations.RowsFirstDataLength -eq 0 `
                    -and $state.Observations.RowsFirstDataRead -eq 0 -and $state.Observations.RowsReadyRead -eq 0 `
                    -and $state.Observations.ReadyFirst -eq 0 -and $state.Protocol -eq 0)
            }
            { $_ -in 357..358 } {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                $text = $readyV2 + "`n"
                if ($case -eq 358) { $text += $finalV2.Replace('resumed=1', 'resumed=0') + "`n" }
                $bytes = [Text.Encoding]::ASCII.GetBytes($text)
                $source = [IO.MemoryStream]::new($bytes)
                Invoke-ControlRowsRead $source
                Assert-Control (-not $source.CanRead -and $state.Observations.RowsReads -eq 1 `
                    -and $state.Observations.RowsMissing -eq 0 -and $state.Observations.RowsEmpty -eq 0 `
                    -and $state.Observations.RowsFirstDataLength -eq $bytes.Length `
                    -and $state.Observations.RowsFirstDataRead -eq $bytes.Length `
                    -and $state.Observations.RowsReadyRead -eq $bytes.Length -and $state.Observations.ReadyFirst -eq 199)
                if ($case -eq 357) {
                    $later = [Text.Encoding]::ASCII.GetBytes(
                        $text + $finalV2.Replace('resumed=1', 'resumed=0') + "`n")
                    Invoke-ControlRowsRead ([IO.MemoryStream]::new($later))
                }
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.RowsFirstDataLength -eq $bytes.Length `
                    -and $state.Observations.RowsFirstDataRead -eq $bytes.Length `
                    -and $state.Observations.RowsReadyRead -eq $bytes.Length `
                    -and $state.Observations.CreatedCheckFirst -eq 79 -and $state.Observations.CreatedPublishAttempted -eq 0)
            }
            359 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                $bytes = [Text.Encoding]::ASCII.GetBytes($readyV2 + "`n")
                Invoke-ControlRowsRead ([StartupControlEmptyReadStream]::new($bytes))
                Assert-Control ($state.Observations.RowsFirstDataLength -eq $bytes.Length `
                    -and $state.Observations.RowsFirstDataRead -eq 0 -and $state.Observations.RowsEmpty -eq 1 `
                    -and $state.Observations.RowsReadyRead -eq 0 -and $state.Observations.ReadyFirst -eq 0)
                Invoke-ControlRowsRead ([IO.MemoryStream]::new($bytes))
                Assert-Control ($state.Observations.RowsReads -eq 2 -and $state.Observations.RowsReadyRead -eq $bytes.Length `
                    -and $state.Observations.RowsFirstDataLength -eq $bytes.Length `
                    -and $state.Observations.RowsFirstDataRead -eq 0 -and $state.Observations.ReadyFirst -eq 199)
            }
            360 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                $bytes = [Text.Encoding]::ASCII.GetBytes('x' * 3000)
                Invoke-ControlRowsRead ([IO.MemoryStream]::new($bytes))
                Assert-Control ($state.Protocol -eq 1 -and $state.Observations.RowsFirstDataLength -eq 2049 `
                    -and $state.Observations.RowsFirstDataRead -eq 2049 -and $state.Observations.RowsReadyRead -eq 0 `
                    -and $state.Observations.ReadyFirst -eq 0 -and $state.Observations.RowsReads -eq 1)
            }
            361 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent
                Invoke-ControlRowsRead ([IO.MemoryStream]::new())
                $bytes = [Text.Encoding]::ASCII.GetBytes($readyV2 + "`n")
                Invoke-ControlRowsRead ([IO.MemoryStream]::new($bytes))
                $observed = (New-HermesStartupReport $state $true $true).observations
                Assert-Control ($observed.RowsReads -eq 2 -and $observed.RowsMissing -eq 0 -and $observed.RowsEmpty -eq 1 `
                    -and $observed.RowsFirstDataLength -eq $bytes.Length -and $observed.RowsFirstDataRead -eq $bytes.Length `
                    -and $observed.RowsReadyRead -eq $bytes.Length -and $observed.ReadyFirst -eq 199)
            }
            { $_ -in 362..363 } {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent -DeferClose
                & {
                    foreach ($name in @('Assert-TracePath', 'Read-TraceControl')) {
                        $definition = @($driverAst.FindAll({
                            param($node)
                            $node -is [Management.Automation.Language.FunctionDefinitionAst] `
                                -and $node.Name -ceq $name
                        }, $true))
                        Assert-Control ($definition.Count -eq 1)
                        . ([scriptblock]::Create($definition[0].Extent.Text))
                    }
                    function Publish-TraceCreated {
                        Set-HermesStartupCreatedPhase $state 'CreatedCheckFirst'
                        if ((Test-HermesStartupCreated $state) -and $state.Err.Count -ge $size) {
                            $script:controlProcessProgress = $true
                            throw 'Synthetic live progress reached.'
                        }
                    }
                    function Publish-TraceArm { Assert-Control (-not (Test-HermesStartupArm $state)) }
                    function Start-Sleep {
                        param([int]$Milliseconds)
                        if ($watch.ElapsedMilliseconds -ge 5000) { throw 'Synthetic process watchdog expired.' }
                        [Threading.Thread]::Sleep($Milliseconds)
                    }
                    $scope = [Guid]::NewGuid().ToString('N')
                    $root = Join-Path $PSScriptRoot ('obj/startup-pipe-controls/' + $scope)
                    $size = if ($case -eq 362) { 1024 } else { 2048 }
                    $process = [Diagnostics.Process]::new()
                    $null = Assert-TracePath $PSCommandPath
                    $pin = [IO.File]::Open($PSCommandPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                    $hash = Get-ControlCommandHash $pin
                    $script:controlProcessProgress = $false
                    $started = $false
                    $debugger = $null
                    try {
                        Assert-Control (-not [IO.Directory]::Exists($root))
                        $null = [IO.Directory]::CreateDirectory($root)
                        $null = Assert-TracePath $root
                        $start = [Diagnostics.ProcessStartInfo]::new()
                        $start.FileName = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
                        $start.UseShellExecute = $false
                        $start.CreateNoWindow = $true
                        $start.RedirectStandardOutput = $true
                        $start.RedirectStandardError = $true
                        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                                $PSCommandPath, '-PipeFixture', [string]$size, '-PipeScope', $scope)) {
                            $start.ArgumentList.Add($argument)
                        }
                        $process.StartInfo = $start
                        Assert-Control ($process.Start())
                        $started = $true
                        $debugger = [pscustomobject]@{
                            StandardOutput = New-ControlProcessReader $process.StandardOutput
                            StandardError = New-ControlProcessReader $process.StandardError
                            Process = $process; ExitCode = 0
                        }
                        $debugger | Add-Member ScriptProperty HasExited { $this.Process.HasExited }
                        Assert-Control ($debugger.StandardOutput.BaseStream -is [IO.FileStream] `
                            -and $debugger.StandardError.BaseStream -is [IO.FileStream] `
                            -and -not $debugger.StandardOutput.BaseStream.CanSeek `
                            -and -not $debugger.StandardError.BaseStream.CanSeek)
                        $initiate = [Diagnostics.Stopwatch]::StartNew()
                        Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                        Start-TraceRead $state.Err $debugger.StandardError.BaseStream
                        Assert-Control ($initiate.ElapsedMilliseconds -lt 1000 -and -not $debugger.HasExited `
                            -and $null -ne $state.Out.Pending -and $null -ne $state.Err.Pending)
                        $watch = [Diagnostics.Stopwatch]::StartNew()
                        $refused = $false
                        try { Invoke-TraceCollection } catch { $refused = $true }
                        Assert-Control ($refused -and $script:controlProcessProgress -and -not $debugger.HasExited `
                            -and -not $state.Out.End -and -not $state.Err.End `
                            -and $state.Out.Count -ge $size -and $state.Err.Count -eq $size `
                            -and $state.Out.Prefix.StartsWith(('x' * $size), [StringComparison]::Ordinal) `
                            -and $state.Err.Prefix -ceq ('e' * $size) -and $state.Observations.RowsReadyRead -gt 0 `
                            -and (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                            -and -not $state.ContinueAttempted -and $state.Bounds -eq 0 -and $state.Protocol -eq 0)
                        Assert-Control ($state.Observations.OutReadStartMax -in 1..3 `
                            -and $state.Observations.ErrReadStartMax -in 1..3 `
                            -and $state.Observations.CreateReadAge -in 1..6 `
                            -and $state.Observations.CreateParseWork -in 1..6 `
                            -and $state.Observations.CreateReceiveDelay -in 1..6 `
                            -and $state.Observations.CreateRowsWork -in 1..6 `
                            -and $state.Observations.CreateCheckDelay -in 1..6)
                        Assert-Control ($process.WaitForExit(10000) -and $process.ExitCode -eq 0)
                        Assert-Control ((Get-ControlCommandHash $pin) -ceq $hash)
                    } finally {
                        if ($started) {
                            if (-not $process.HasExited) { $process.Kill() }
                            Assert-Control ($process.WaitForExit(5000))
                            if ($null -ne $debugger) {
                                Close-TraceRead $state.Out $debugger.StandardOutput
                                Close-TraceRead $state.Err $debugger.StandardError
                            }
                            $process.StandardOutput.Dispose(); $process.StandardError.Dispose()
                        }
                        $process.Dispose(); $pin.Dispose()
                        $path = Join-Path $root 'helper.rows'
                        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
                        if ([IO.Directory]::Exists($root)) { [IO.Directory]::Delete($root) }
                    }
                }
            }
            364 {
                $state = Native-StartupTrace\New-HermesStartupState
                $samples = @(-1, 0, 10, 11, 100, 101, 1000, 1001, 5000, 5001, 30000, 30001, [long]::MaxValue)
                $expected = @(0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6)
                foreach ($index in 0..($samples.Count - 1)) {
                    $actual = Get-HermesStartupWorkBucket $samples[$index]
                    Assert-Control ($actual -is [int] -and $actual -eq $expected[$index])
                }
            }
            365 {
                $state = Native-StartupTrace\New-HermesStartupState
                foreach ($name in @('OutReadStartMax', 'ErrReadStartMax', 'CreateParseWork', 'CreateRowsWork')) {
                    foreach ($sample in @(0, 11, 101, 1001, 5001, 30001, 0)) {
                        Set-HermesStartupWorkBucket $state $name $sample
                    }
                    Assert-Control ($state.Observations[$name] -eq 6)
                }
                foreach ($name in @('CreateReadAge', 'CreateReceiveDelay', 'CreateCheckDelay')) {
                    Set-HermesStartupWorkBucket $state $name 11 -First
                    Set-HermesStartupWorkBucket $state $name 30001 -First
                    Assert-Control ($state.Observations[$name] -eq 2)
                }
                Assert-Control (-not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                    -and -not $state.ContinueAttempted -and $null -eq $state.InputPending `
                    -and $state.Protocol -eq 0 -and $state.AcceptedUtf8Bytes -eq 0)
            }
            { $_ -in 366..367 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $names = @('OutReadStartMax', 'ErrReadStartMax', 'CreateReadAge', 'CreateParseWork',
                    'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay')
                foreach ($name in $names) { Set-HermesStartupWorkBucket $state $name 0 }
                if ($case -eq 366) { $state.Bounds = 1 } else { $state.AcceptedUtf8Bytes = [long]8388608 }
                foreach ($name in $names) {
                    Set-HermesStartupWorkBucket $state $name 30001
                    Assert-Control ($state.Observations[$name] -eq 1)
                }
                Assert-Control ($state.Protocol -eq 0 -and -not $state.CreatedAcked -and -not $state.ContinueAttempted)
            }
            368 {
                $state = Native-StartupTrace\New-HermesStartupState
                $names = @('OutReadStartMax', 'ErrReadStartMax', 'CreateReadAge', 'CreateParseWork',
                    'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay')
                foreach ($name in $names) {
                    foreach ($bad in @('private --command', $true, $null, [long]1, -1, 7, 8388608)) {
                        $state.Observations[$name] = $bad
                        Assert-Control ((New-HermesStartupReport $state $true $true).observations[$name] -eq 0)
                    }
                    $state.Observations[$name] = 6
                }
                $refused = $false
                try { Set-HermesStartupWorkBucket $state 'arbitrary-field' 0 } catch { $refused = $true }
                $report = New-HermesStartupReport $state $true $true
                foreach ($name in $names) { Assert-Control ($report.observations[$name] -ceq 6) }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ($refused -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|--command|Clock|Timestamp|Elapsed|Stopwatch')
            }
            369 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder BeforeClose -DeferClose
                & {
                    $definition = @($driverAst.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
                            -and $node.Name -ceq 'Publish-TraceCreated'
                    }, $true))
                    . ([scriptblock]::Create($definition[0].Extent.Text))
                    function Read-TraceControl {
                        param([string]$Path)
                        $text = $readyV2 + "`n" + $finalV2.Replace(
                            'resumed=1', 'resumed=0').Replace('exit=3221225794', 'exit=0') + "`n"
                        Invoke-ControlRowsRead ([IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes($text)))
                    }
                    $text = (@(
                        'HERMES_CDB v=3 kind=2 pid=22', 'HERMES_CDB v=1 kind=3 pid=22',
                        'Last event: 16.1: Exit process 1:16, code 0', 'HERMES_CDB v=1 kind=4 pid=22'
                    ) -join "`n") + "`n"
                    $debugger = [pscustomobject]@{
                        StandardOutput = New-ControlReader $text; StandardError = New-ControlReader ''
                        HasExited = $true; ExitCode = 0
                    }
                    try {
                        Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                        Start-TraceRead $state.Err $debugger.StandardError.BaseStream
                        Invoke-TraceCollection
                        Assert-Control ($state.Observations.ReadyFirst -eq 3 -and $state.Observations.CreateCloseFirst -eq 199 `
                            -and $state.Observations.CreatedJointFirst -eq 199 -and $state.Observations.CreatedCheckFirst -eq 95 `
                            -and $state.Observations.CreatedPublishAttempted -eq 0 -and -not $state.CreatedAcked `
                            -and $state.Protocol -eq 0 -and $state.Bounds -eq 0 -and $state.Exits.Count -eq 1)
                        foreach ($name in @('CreateReadAge', 'CreateParseWork', 'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay')) {
                            Assert-Control ($state.Observations[$name] -in 1..6)
                        }
                    } finally { $debugger.StandardOutput.Dispose(); $debugger.StandardError.Dispose() }
                }
            }
            370 {
                $state = Native-StartupTrace\New-HermesStartupState
                . $controlCommandDriver
                $path = Join-Path $root 'startup.cdb'
                $actual = [Text.Encoding]::ASCII.GetString($script:controlCommandFiles[$path])
                $lines = @($actual -split "`n")
                $literal = 'sxe -c "$$<' + (Join-Path $root 'startup-cpr.cdb').Replace('\', '\\') + '" cpr'
                $condensed = $literal.Replace('$$<', '$$><')
                $legacyLines = @(
                    ('.load "' + $extension.Replace('\', '\\') + '"'),
                    '.chain', '.printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid',
                    ('sxe -c "$$><' + (Join-Path $root 'startup-ibp.cdb').Replace('\', '\\') + '" ibp'),
                    $condensed,
                    ('sxe -c "$$><' + (Join-Path $root 'startup-epr.cdb').Replace('\', '\\') + '" epr'),
                    'sx', 'g', ''
                )
                $legacy = $legacyLines -join "`n"
                Assert-Control ($lines.Count -eq 9 -and $lines[4] -ceq $literal `
                    -and $actual.Replace($literal, $condensed) -ceq $legacy `
                    -and $actual.Length -eq ($legacy.Length - 1) `
                    -and @($lines | Where-Object { $_ -ceq 'g' }).Count -eq 1 `
                    -and $state.FilterExpected.Cpr -ceq ('Command: "' + $literal.Substring(8, $literal.Length - 13) + '"'))
                $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::ASCII.GetBytes($actual))).ToLowerInvariant()
                $legacyHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::ASCII.GetBytes($legacy))).ToLowerInvariant()
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($report.commandSha256 -ceq $actualHash -and $actualHash -cne $legacyHash `
                    -and $script:controlCommandWrites.Count -eq 4 -and $script:controlCommandPins.Count -eq 4 `
                    -and $script:controlCommandHashes -eq 4 -and $script:controlLaunchBindings -eq 1 `
                    -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0)
                $fixed = @(
                    @{ File = 'startup-cpr.cdb'; Field = 'createCommandSha256'; Length = 119
                        Hash = '5fb09e6b92ea4344099598c8057ef775745ebf2994662651cabf052d67dc7d7f'; Goes = 1 },
                    @{ File = 'startup-epr.cdb'; Field = 'exitCommandSha256'; Length = 115
                        Hash = 'f3944355a34ef71d377a79075bc802c8be0e233ea02bcbc94c52d27bbfe02138'; Goes = 1 },
                    @{ File = 'startup-ibp.cdb'; Field = 'initialCommandSha256'; Length = 116
                        Hash = '48714490926d5dbee8117768355f9c6dece7e69840f28220096708eb54587c4e'; Goes = 0 }
                )
                foreach ($source in $fixed) {
                    $sourcePath = Join-Path $root $source.File
                    $bytes = $script:controlCommandFiles[$sourcePath]
                    $sourceLines = @([Text.Encoding]::ASCII.GetString($bytes) -split "`r`n")
                    Assert-Control ($bytes.Length -eq $source.Length -and $report[$source.Field] -ceq $source.Hash `
                        -and @($sourceLines | Where-Object { $_ -ceq 'g' }).Count -eq $source.Goes `
                        -and $script:controlCommandReadOnly[$sourcePath] `
                        -and $script:controlCommandPins -ccontains $sourcePath)
                }
            }
            371 {
                $state = New-ControlNativeCreated -Acknowledge $false
                Assert-Control ($state.FilterExpected.Cpr -ceq 'Command: "$$<D:\\owned\\startup-cpr.cdb"' `
                    -and $state.FilterExpected.Epr -ceq 'Command: "$$><D:\\owned\\startup-epr.cdb"' `
                    -and $state.FilterExpected.Ibp -ceq 'Command: "$$><D:\\owned\\startup-ibp.cdb"' `
                    -and (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked)
                $expected = $state.FilterExpected.Cpr
                # The old condensed CPR mode is now a negative control only.
                foreach ($text in @(
                    $expected.Replace('$$<', '$$><'),
                    $expected.Replace('$$<', '$$>a<'),
                    $expected.Replace('$$<', '$><'),
                    $expected.Replace('startup-cpr', 'startup-epr'),
                    $expected.Replace('startup-cpr', 'STARTUP-CPR'),
                    $expected.Replace('\\', '\'),
                    $expected.Replace('.cdb"', '.cdb argument"'),
                    ($expected + ';g')
                )) {
                    Assert-Control ($text -cne $expected)
                    Add-HermesStartupLine $state 'cpr - Create process - break'
                    Add-HermesStartupLine $state $text
                    $observed = New-HermesStartupObservations $state
                    Assert-Control ($observed.CprFilter -eq 1 -and $observed.CprCommandShape -eq 6 `
                        -and -not $state.FilterVerified.Cpr -and -not (Test-HermesStartupCreated $state) `
                        -and -not $state.CreatedAcked -and -not $state.ContinueAttempted `
                        -and $null -eq $state.InputPending -and $state.Protocol -eq 0)
                }
                Add-HermesStartupLine $state 'cpr - Create process - break'
                Add-HermesStartupLine $state $expected
                $observed = New-HermesStartupObservations $state
                Assert-Control ($observed.CprFilter -eq 9 -and $observed.CprCommandShape -eq 1 `
                    -and $observed.CprCommandTransform -eq 2 -and (Test-HermesStartupCreated $state) `
                    -and -not $state.CreatedAcked -and -not (Test-HermesStartupArm $state))
                Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2.Replace(
                    'resumed=1', 'resumed=0').Replace('exit=3221225794', 'exit=0') + "`n")
                Assert-Control (-not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                    -and -not $state.ContinueAttempted -and $null -eq $state.InputPending)
            }
            { $_ -in 372..373 } {
                $state = Native-StartupTrace\New-HermesStartupState
                $script:controlCommandFault = $case - 359
                $script:controlCommandFaultName = 'startup.cdb'
                . $controlCommandDriver
                $path = Join-Path $root 'startup.cdb'
                $expected = New-TraceCommands $extension
                $actual = [Text.Encoding]::ASCII.GetString($script:controlCommandFiles[$path])
                $wrongToken = if ($case -eq 372) { 'sxe -c "$$><' } else { 'sxe -c "$><' }
                $report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
                Assert-Control ($actual -ceq $expected.Replace('sxe -c "$$<', $wrongToken) `
                    -and $actual -cne $expected `
                    -and $actual.Length -eq ($expected.Length + [int]($case -eq 372)) `
                    -and $script:controlCommandWrites.Count -eq 4 -and $script:controlCommandPins.Count -eq 4 `
                    -and $script:controlCommandHashes -eq $(if ($case -eq 372) { 3 } else { 4 }) `
                    -and $script:controlLaunchBindings -eq 0 -and $null -eq $script:controlStartInfo `
                    -and $state.TraceFaulted -and $report.tracePoint -eq 24 `
                    -and $report.status -eq 8 -and $report.error -eq 13 -and $report.cleanupFault -eq 0 `
                    -and $report.commandSha256 -ceq '' -and $report.createCommandSha256 -ceq '' `
                    -and $report.exitCommandSha256 -ceq '' -and $report.initialCommandSha256 -ceq '' `
                    -and $state.FilterExpected.Cpr -ceq '' -and $state.FilterExpected.Epr -ceq '' `
                    -and $state.FilterExpected.Ibp -ceq '' -and $state.ExtensionExpected -ceq '' `
                    -and -not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                    -and -not (Test-HermesStartupArm $state) -and -not $state.ContinueAttempted `
                    -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $script:controlStops -eq 1 -and $script:controlDisposed -eq 1)
            }
            374 {
                $state = New-ControlNativeCreated -Acknowledge $false -ReadyOrder Absent -WithoutCreateCallback
                & {
                    $definition = @($driverAst.FindAll({
                        param($node)
                        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
                            -and $node.Name -ceq 'Publish-TraceCreated'
                    }, $true))
                    . ([scriptblock]::Create($definition[0].Extent.Text))
                    function Read-TraceControl {
                        param([string]$Path)
                        Invoke-ControlRowsRead ([IO.MemoryStream]::new(
                            [Text.Encoding]::ASCII.GetBytes($readyV2 + "`n")))
                    }
                    function Open-TracePin {
                        param([string]$Path)
                        $pin = [IO.File]::Open($Path, [IO.FileMode]::Open,
                            [IO.FileAccess]::Read, [IO.FileShare]::Read)
                        $pins.Add($pin)
                        return $pin
                    }
                    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.artifacts/startup-ready-$PID"))
                    Assert-Control (-not [IO.Directory]::Exists($root))
                    $null = [IO.Directory]::CreateDirectory($root)
                    $debugger = [pscustomobject]@{
                        StandardOutput = New-ControlReader ''; StandardError = New-ControlReader ''
                        HasExited = $true; ExitCode = 0
                    }
                    try {
                        Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                        Start-TraceRead $state.Err $debugger.StandardError.BaseStream
                        Invoke-TraceCollection
                        $path = Join-Path $root 'created.row'
                        Assert-Control ([IO.File]::ReadAllText($path) -ceq
                            "HERMES_STARTUP_CREATED v=2 phase=created helper=11 pid=22 born=123`n")
                        Assert-Control ($pins.Count -eq 1 -and $pins[0].CanRead -and -not $pins[0].CanWrite `
                            -and ([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReadOnly) -ne 0 `
                            -and $state.CreatedAcked -and $null -ne $state.CreatedBinding `
                            -and $null -eq $state.Created -and $null -eq $state.Resumed `
                            -and -not (Test-HermesStartupCreatedBinding $state) -and -not (Test-HermesStartupArm $state) `
                            -and -not $state.ContinueAttempted -and -not [IO.File]::Exists((Join-Path $root 'armed.row')))
                        $observed = (New-HermesStartupReport $state $true $true).observations
                        Assert-Control ($observed.ReadyFirst -eq 131 -and $observed.CreatedCheckFirst -eq 131 `
                            -and $observed.CreatedCheckSeen -eq 1 -and $observed.CreatedPublishAttempted -eq 1 `
                            -and $observed.CreateCloseFirst -eq 0 -and $observed.CreatedJointFirst -eq 0)
                        Publish-TraceCreated
                        Assert-Control ($pins.Count -eq 1 -and $state.Protocol -eq 0)
                    } finally {
                        $debugger.StandardOutput.Dispose(); $debugger.StandardError.Dispose()
                        foreach ($pin in $pins) { $pin.Dispose() }
                        Remove-Item -LiteralPath $root -Recurse -Force
                    }
                }
            }
            375 {
                $state = New-ControlNativeCreated -WithoutCreateCallback
                Assert-Control ($state.CreatedAcked -and $state.Resumed.resumed -eq 1 -and $null -eq $state.Created)
                foreach ($line in @(
                    'HERMES_CDB v=3 kind=5 pid=22',
                    'Last event: 16.1: Break instruction exception - code 80000003 (first chance)',
                    'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002',
                    'HERMES_CDB v=3 kind=6 pid=22'
                )) { Add-HermesStartupLine $state $line }
                Add-HermesStartupText $state $state.Out '1:000> ' $true
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    Assert-Control ($state.Protocol -eq 0 -and $state.Stopped -and $state.Arm.Flags -eq 2 `
                        -and -not (Test-HermesStartupCreatedBinding $state) `
                        -and -not (Test-HermesStartupInitialBinding $state) -and -not (Test-HermesStartupArm $state))
                    $refused = $false
                    try { Invoke-HermesStartupContinue $state $writer } catch { $refused = $true }
                    Assert-Control ($refused -and $physical.Writes -eq 0 -and -not $state.Acked `
                        -and -not (Test-HermesStartupArmed $state))
                    Add-HermesStartupLine $state $failureLine
                    $report = New-HermesStartupReport $state $false $false
                    Assert-Control ($null -eq $state.Failure -and $report.firstFailureKind -eq 0 `
                        -and $report.armingVerified -eq 0 -and $report.completed -eq 0 `
                        -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                } finally { $writer.Dispose() }
            }
            376 {
                $state = New-ControlNativeStopped -ResumeBeforeCreate
                Assert-Control ($state.CreatedAcked -and $state.Resumed.resumed -eq 1 `
                    -and (Test-HermesStartupCreatedBinding $state) -and (Test-HermesStartupInitialBinding $state) `
                    -and (Test-HermesStartupArm $state) -and -not $state.Acked)
                $physical = [StartupControlWriteStream]::new(0)
                $physical.AfterWrite = {
                    Assert-Control ($state.Acked -and $null -ne $state.ArmedBinding `
                        -and (Test-HermesStartupArmed $state) -and $state.ContinueAttempted)
                }
                Invoke-HermesStartupContinue $state ([IO.StreamWriter]::new($physical))
                Add-HermesStartupLine $state $failureLine
                $report = Complete-ControlNative $state
                Assert-Control ($physical.Writes -eq 1 -and $physical.Closed -and $state.InputClosed `
                    -and $state.InputPending.IsCompleted -and [Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq "g`n" `
                    -and $report.completed -eq 1 -and $report.armingVerified -eq 1 `
                    -and $report.firstFailureKind -eq 1 -and $report.cleanupComplete -eq 1 -and $report.parentUnchanged -eq 1 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
            }
            377 {
                $mutations = @(
                    { $state.Initial = 0 }, { $state.Initial = 12 },
                    { $state.Target.helper = 12 }, { $state.Target.pid = 11 },
                    { $state.Target.pid = 0 }, { $state.Target.helper = 0 }, { $state.Target.born = 0 },
                    { $state.Target.image = 0 }, { $state.Target.debug = 0 }, { $state.Target.context = 0 },
                    { $state.Target.version = 1 }, { $state.Target.stage = 3 }, { $state.Target.resumed = 1 }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlNativeCreated -Acknowledge $false -WithoutCreateCallback
                    try {
                        . $mutation
                        Assert-Control (-not (Test-HermesStartupNativeReadinessBinding $state) `
                            -and -not (Test-HermesStartupCreated $state) -and -not (Test-HermesStartupArm $state))
                        Invoke-ControlCreatedPublication
                        Assert-Control ($state.Observations.CreatedPublishAttempted -eq 0 -and -not $state.CreatedAcked)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            378 {
                foreach ($name in @('CommandSha256', 'CreateCommandSha256', 'InitialCommandSha256', 'ExitCommandSha256')) {
                    foreach ($value in @('', ('A' * 64), ('a' * 63), 'D:\owned\startup.cdb')) {
                        $state = New-ControlNativeCreated -Acknowledge $false -WithoutCreateCallback
                        try {
                            $state[$name] = $value
                            Invoke-ControlCreatedPublication
                            Assert-Control (-not (Test-HermesStartupNativeReadinessBinding $state) `
                                -and -not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                                -and $state.Observations.CreatedPublishAttempted -eq 0)
                        } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                    }
                }
            }
            379 {
                $mutations = @(
                    { $state.FilterVerified.Cpr = $false }, { $state.FilterVerified.Epr = $false },
                    { $state.FilterVerified.Ibp = $false }, { $state.IbpBreak = $false },
                    { $state.Observations.CprFilter = 1 }, { $state.Observations.EprFilter = 1 }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlNativeCreated -Acknowledge $false -WithoutCreateCallback
                    try {
                        . $mutation
                        Invoke-ControlCreatedPublication
                        Assert-Control (-not (Test-HermesStartupNativeReadinessBinding $state) `
                            -and -not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                            -and $state.Observations.CreatedPublishAttempted -eq 0)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            380 {
                $mutations = @(
                    { $state.Target = $null },
                    {
                        $state.Target = $null; $state.TargetText = ''; $state.RowsText = ''
                        Update-HermesStartupRows $state ($finalV2.Replace('resumed=1', 'resumed=0') + "`n")
                    },
                    { Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2.Replace('resumed=1', 'resumed=0') + "`n") },
                    { $state.Exits['22'] = 37 }, { $state.Exits['11'] = 0 },
                    { Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=1 pid=22' },
                    { Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n") },
                    { $state.CreatedAcked = $true }, { $state.CreatedBinding = @{ Helper = 11; Pid = 22; Born = 123 } },
                    { $state.Error = 13 }, { $state.Protocol = 1 }, { $state.Unknown = 1 },
                    { $state.Bounds = 1 }, { $state.AcceptedUtf8Bytes = [long]8388608 },
                    { $state.AcceptedUtf8Bytes = [long]8388609 }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlNativeCreated -Acknowledge $false -WithoutCreateCallback
                    try {
                        . $mutation
                        foreach ($name in @('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst')) {
                            $state.Observations[$name] = 199
                        }
                        Assert-Control (-not (Test-HermesStartupCreated $state))
                        Invoke-ControlCreatedPublication
                        Assert-Control ($state.Observations.CreatedPublishAttempted -eq 0 `
                            -and -not $state.ContinueAttempted -and $null -eq $state.InputPending)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            381 {
                $mutations = @(
                    { $state.Created.Kind = 5 }, { $state.Created.Version = 2 },
                    { $state.Created.Event = 0 }, { $state.Created.Pid = 23 }, { $state.Created.EventPid = 23 }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlNativeCreated -Acknowledge $false
                    try {
                        . $mutation
                        Assert-Control ((Test-HermesStartupNativeReadinessBinding $state) `
                            -and -not (Test-HermesStartupCreatedBinding $state) -and -not (Test-HermesStartupCreated $state))
                        Invoke-ControlCreatedPublication
                        Assert-Control (-not $state.CreatedAcked -and $state.Observations.CreatedPublishAttempted -eq 0)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
                $state = New-ControlNativeCreated -Acknowledge $false -WithoutCreateCallback
                foreach ($line in @(
                    'HERMES_CDB v=3 kind=1 pid=23', 'Last event: 17.1: Create process 1:17',
                    'HERMES_CDB v=3 kind=2 pid=23'
                )) { Add-HermesStartupLine $state $line }
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Protocol -eq 0 -and $state.Created.Pid -eq 23 `
                    -and -not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                    -and $state.Observations.CreatedPublishAttempted -eq 0)
            }
            382 {
                $mutations = @(
                    { $state.Target.born = 124; $state.Resumed.born = 124 },
                    { $state.Resumed.born = 124 }, { $state.CreatedAcked = $false }, { $state.CreatedBinding = $null },
                    { $state.CommandSha256 = 'c' * 64 }, { $state.CreateCommandSha256 = 'c' * 64 },
                    { $state.InitialCommandSha256 = 'c' * 64 }, { $state.ExitCommandSha256 = 'c' * 64 }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlNativeStopped -ResumeBeforeCreate
                    try {
                        Assert-Control ((Test-HermesStartupArm $state))
                        . $mutation
                        Assert-Control (-not (Test-HermesStartupInitialBinding $state) -and -not (Test-HermesStartupArm $state) `
                            -and -not (Test-HermesStartupArmed $state) -and -not $state.ContinueAttempted `
                            -and (New-HermesStartupReport $state $false $false).completed -eq 0)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            383 {
                foreach ($duplicate in @('Acknowledgement', 'Resumed')) {
                    $state = New-ControlNativeCreated -WithoutCreateCallback
                    try {
                        if ($duplicate -ceq 'Acknowledgement') { Set-HermesStartupCreatedAcknowledged $state }
                        else { Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $resumedV2 + "`n") }
                        Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupCreated $state) `
                            -and -not (Test-HermesStartupArm $state) -and -not $state.ContinueAttempted)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
                $mutations = @(
                    { $state.Resumed = $null }, { $state.Resumed.resumed = 0 },
                    { $state.Arm.Flags = -1 }, { $state.Arm.Flags = 0 }, { $state.Arm.Flags = 1 },
                    { $state.Arm.Flags = [long][uint32]::MaxValue + 1 },
                    { $state.Stopped = $false }, { $state.Out.Tail = '' }, { $state.Out.End = $true },
                    { $state.Final = @{ stage = 3 } }, { $state.Exits['22'] = 37 }, { $state.Exits['11'] = 0 }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlNativeStopped -ResumeBeforeCreate
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        . $mutation
                        $refused = $false
                        try { Invoke-HermesStartupContinue $state $writer } catch { $refused = $true }
                        Assert-Control ($refused -and -not (Test-HermesStartupArm $state) -and $physical.Writes -eq 0 `
                            -and -not $state.Acked -and -not $state.ContinueAttempted)
                    } finally { $writer.Dispose(); $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            384 {
                foreach ($later in @('Final', 'ChildExit', 'HelperExit')) {
                    $state = New-ControlNativeCreated -Acknowledge $false -WithoutCreateCallback
                    try {
                        Set-HermesStartupCreatedPhase $state 'CreatedCheckFirst'
                        Assert-Control ((Test-HermesStartupCreated $state) -and $state.Observations.CreatedCheckFirst -eq 131)
                        switch ($later) {
                            'Final' { Update-HermesStartupRows $state ($readyV2 + "`n" + $finalV2.Replace('resumed=1', 'resumed=0') + "`n") }
                            'ChildExit' { $state.Exits['22'] = 37 }
                            'HelperExit' { $state.Exits['11'] = 0 }
                        }
                        Invoke-ControlCreatedPublication
                        Assert-Control (-not (Test-HermesStartupCreated $state) -and -not $state.CreatedAcked `
                            -and $state.Observations.CreatedCheckFirst -eq 131 -and $state.Observations.CreatedPublishAttempted -eq 0)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            385 {
                $state = New-ControlNativeCreated -Acknowledge $false -WithoutCreateCallback
                Set-HermesStartupCreatedPhase $state 'CreatedCheckFirst'
                $observed = (New-HermesStartupReport $state $false $false).observations
                Assert-Control ($observed.CreatedCheckFirst -eq 131 -and ($observed.CreatedCheckFirst -band 64) -eq 0 `
                    -and $observed.CreateCloseFirst -eq 0 -and $observed.CreatedJointFirst -eq 0)
                Set-HermesStartupCreatedAcknowledged $state
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n")
                foreach ($line in @(
                    'HERMES_CDB v=3 kind=1 pid=22', 'Last event: 16.1: Create process 1:16',
                    'HERMES_CDB v=3 kind=2 pid=22'
                )) { Add-HermesStartupLine $state $line }
                Assert-Control ($state.Observations.CreateCheckDelay -eq 0)
                Invoke-ControlCreatedPublication
                $report = New-HermesStartupReport $state $false $false
                Assert-Control ($report.observations.CreatedCheckFirst -eq 131 -and $report.observations.ReadyFirst -eq 131 `
                    -and $report.observations.CreateCloseFirst -eq 3655 -and $report.observations.CreatedJointFirst -eq 3655 `
                    -and $report.observations.CreateCheckDelay -in 1..6 -and $report.observations.CreatedPublishAttempted -eq 0 `
                    -and (Test-HermesStartupCreatedBinding $state) -and -not (Test-HermesStartupArm $state))
                $delay = $state.Observations.CreateCheckDelay
                Invoke-ControlCreatedPublication
                Assert-Control ($state.Observations.CreateCheckDelay -eq $delay)
                foreach ($value in @(3, 131)) {
                    $state.Observations.CreatedCheckFirst = $value
                    Assert-Control ((New-HermesStartupReport $state $false $false).observations.CreatedCheckFirst -eq $value)
                }
                foreach ($value in @(1, 4, 5, 16384, -1, 'private', [long]131)) {
                    $state.Observations.CreatedCheckFirst = $value
                    Assert-Control ((New-HermesStartupReport $state $false $false).observations.CreatedCheckFirst -eq 0)
                }
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
            }
            386 {
                $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode Inventory
                $selection = @($driverAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.AssignmentStatementAst] `
                        -and $node.Left.Extent.Text -ceq '$state'
                }, $true))
                Assert-Control ($selection.Count -eq 1 `
                    -and $selection[0].Right.Extent.Text -ceq 'New-HermesStartupState -DiagnosticMode ScopedCreateV1' `
                    -and $driverAst.ParamBlock.Parameters.Count -eq 0)
                . $controlCommandDriver
                $expected = Get-TraceSelectedSources $extension
                Assert-Control ($expected.Count -eq 4 -and $state.PublishedMode -ceq 'Inventory' `
                    -and $script:controlCommandWrites.Count -eq 4 -and $script:controlCommandPins.Count -eq 4 `
                    -and $script:controlCommandHashes -eq 4 -and $script:controlLaunchBindings -eq 1)
                foreach ($entry in $expected.GetEnumerator()) {
                    $path = Join-Path $root $entry.Key
                    $actual = [Text.Encoding]::ASCII.GetString($script:controlCommandFiles[$path])
                    Assert-Control ($actual -ceq $entry.Value -and $script:controlCommandReadOnly[$path] `
                        -and $script:controlCommandPins -ccontains $path `
                        -and $actual -cnotmatch '(?i:gflag|peb|sizeof|@@|\.load|\.chain|\.echo|\|\.|\b(?:ed|ew|eb|eq|ep|r|dt|db|dd|dq|k)\b)')
                    if ($entry.Key -cne 'startup.cdb') {
                        $lines = @($actual -split "`r`n")
                        Assert-Control ($lines.Count -eq 5 -and $lines[1] -ceq '.lastevent' `
                            -and $lines[3] -ceq 'g' -and $lines[4] -ceq '' `
                            -and $lines[0] -cmatch '\A\.printf "HERMES_CDB v=[13] kind=[135] pid=%u\\n", @\$tpid\z' `
                            -and $lines[2] -cmatch '\A\.printf "HERMES_CDB v=[13] kind=[246] pid=%u\\n", @\$tpid\z')
                    }
                }
                Assert-Control ($script:controlStartInfo.ArgumentList.Contains('-o') `
                    -and $script:controlStartInfo.ArgumentList.Contains('startup-debug-probe') `
                    -and -not $script:controlStartInfo.ArgumentList.Contains('-pd'))
            }
            { $_ -in 387..398 } {
                $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode Inventory
                $script:controlCommandFaultName = @(
                    'startup-cpr.cdb', 'startup-ibp.cdb', 'startup-epr.cdb', 'startup.cdb'
                )[[int][Math]::Floor(($case - 387) / 3)]
                $script:controlCommandFault = (($case - 387) % 3) + 1
                . $controlCommandDriver
                $report = New-HermesStartupReport $state $false $false
                Assert-Control ($script:controlCommandWrites.Count -eq 4 -and $script:controlLaunchBindings -eq 0 `
                    -and $null -eq $script:controlStartInfo -and $state.TraceFaulted -and $report.error -eq 13 `
                    -and $state.CommandSha256 -ceq '' -and $state.PublishedMode -ceq '' `
                    -and $report.observationOnly -eq 1 -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
            }
            399 {
                $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode Inventory
                $state.DiagnosticMode = 'arbitrary-mode'
                . $controlCommandDriver
                Assert-Control ($state.Error -eq 13 -and $script:controlCommandWrites.Count -eq 0 `
                    -and $script:controlLaunchBindings -eq 0 -and $null -eq $script:controlStartInfo)
                $refused = $false
                try { $null = Native-StartupTrace\New-HermesStartupState -DiagnosticMode 'arbitrary-mode' } catch { $refused = $true }
                Assert-Control ($refused)
                $refused = $false
                try { $null = Native-StartupTrace\New-HermesStartupState -LegacyParserControls -DiagnosticMode Inventory } catch { $refused = $true }
                Assert-Control ($refused)
            }
            400 {
                $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode Inventory
                Set-HermesStartupTracePoint $state 24
                $null = Publish-TraceCommands $extension
                Confirm-TraceCommandSelection $extension
                $state.DiagnosticMode = 'Trace'
                $refused = $false
                try { Confirm-TraceCommandSelection $extension } catch { $refused = $true }
                Assert-Control ($refused -and $script:controlLaunchBindings -eq 0 -and $null -eq $script:controlStartInfo)
                $state.DiagnosticMode = 'Inventory'
                foreach ($name in @('CommandSha256', 'CreateCommandSha256', 'InitialCommandSha256', 'ExitCommandSha256')) {
                    $saved = $state[$name]; $state[$name] = 'f' * 64
                    $refused = $false
                    try { Confirm-TraceCommandSelection $extension } catch { $refused = $true }
                    Assert-Control ($refused -and $script:controlLaunchBindings -eq 0)
                    $state[$name] = $saved
                }
            }
            401 {
                $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode Inventory
                $script:controlCommandFaultName = 'startup-ibp.cdb'; $script:controlCommandFault = 15
                . $controlCommandDriver
                $actual = [Text.Encoding]::ASCII.GetString($script:controlCommandFiles[(Join-Path $root 'startup-ibp.cdb')])
                Assert-Control ($actual -ceq (New-TraceEventCommands)['startup-ibp.cdb'] `
                    -and $state.Error -eq 13 -and $state.PublishedMode -ceq '' `
                    -and $script:controlLaunchBindings -eq 0 -and $null -eq $script:controlStartInfo)
            }
            402 {
                $state = New-ControlInventoryComplete
                foreach ($kind in @('Create', 'Initial', 'Exit')) {
                    Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Kind $kind -Current 33) $true
                }
                $report = New-HermesStartupReport $state $true $true
                $inventory = $report.inventory
                Assert-Control ($state.Protocol -eq 1 -and $report.observations.ParserProtocol -eq 1 `
                    -and $inventory.complete -eq 1 -and $inventory.status -eq 1 -and $inventory.subjects -eq 3 `
                    -and $inventory.targetCreate -eq 1 -and $inventory.targetInitial -eq 1 -and $inventory.targetExit -eq 1 `
                    -and $inventory.helperExit -eq 1 -and $inventory.otherCreate -eq 1 -and $inventory.otherInitial -eq 1 `
                    -and $inventory.otherExit -eq 1 -and $inventory.otherSubjects -eq 1 `
                    -and $inventory.malformed -eq 0 -and $inventory.mismatch -eq 0 -and $inventory.unclassifiedEvents -eq 0)
                Assert-Control ($report.observationOnly -eq 1 -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                    -and $report.exitObserved -eq 0 -and $report.powershellExit37 -eq 0 `
                    -and $inventory.nativeFinalExit -eq 37 -and $inventory.nativeResumedReceipt -eq 1 `
                    -and $inventory.nativeFinalBound -eq 1 -and $inventory.nativeFinalResumed -eq 1 `
                    -and $inventory.nativeFinalParent -eq 1 -and $inventory.nativeFinalCleanup -eq 1)
            }
            403 {
                $state = New-ControlInventoryComplete
                foreach ($current in @(22, 11, 11, 33, 44, 44)) {
                    Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Current $current) $true
                }
                foreach ($current in @(33, 44)) {
                    Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Kind Exit -Current $current) $true
                }
                $inventory = (New-HermesStartupReport $state $true $true).inventory
                Assert-Control ($inventory.complete -eq 1 -and $inventory.targetCreate -eq 2 -and $inventory.targetCreateRepeat -eq 1 `
                    -and $inventory.helperCreate -eq 2 -and $inventory.helperCreateRepeat -eq 1 `
                    -and $inventory.otherCreate -eq 3 -and $inventory.otherCreateRepeat -eq 1 `
                    -and $inventory.otherSubjects -eq 2 -and $inventory.subjects -eq 4 `
                    -and $inventory.malformed -eq 0 -and $inventory.mismatch -eq 0)
            }
            404 {
                $valid = Get-ControlInventoryEvent
                $texts = @(
                    (Get-ControlInventoryEvent -Event 23), (Get-ControlInventoryEvent -EventId 23),
                    (Get-ControlInventoryEvent -Close 23),
                    $valid.Replace('Create process 1:16', 'Exit process 1:16, code 0'),
                    $valid.Replace("HERMES_CDB v=3 kind=2", "Last event: 16.1: Create process 1:16`nHERMES_CDB v=3 kind=2"),
                    "HERMES_CDB v=3 kind=2 pid=22`n",
                    "Last event: 16.1: Create process 1:16`n",
                    "HERMES_CDB v=3 kind=1 pid=0`n",
                    "HERMES_CDB v=3 kind=1 pid=4294967296`n",
                    "HERMES_CDB v=3 kind=1 pid=22`nHERMES_CDB v=3 kind=1 pid=23`n",
                    "HERMES_CDB v=1 kind=0 pid=11`n"
                )
                foreach ($index in 0..($texts.Count - 1)) {
                    $state = New-ControlNativeCreated -DiagnosticMode Inventory -WithoutCreateCallback
                    try {
                        Add-HermesStartupText $state $state.Out $texts[$index] $true
                        $summary = New-HermesStartupInventorySummary $state $true $true
                        Assert-Control ($state.Inventory.Subjects.Count -eq 0 -and $summary.complete -eq 0)
                        if ($index -le 2) { Assert-Control ($summary.mismatch -eq 1 -and $summary.malformed -eq 0) }
                        else { Assert-Control ($summary.malformed -eq 1 -and $summary.mismatch -eq 0) }
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            405 {
                $mutations = @(
                    { $state.Target = $null }, { $state.Final = $null }, { $state.Resumed = $null },
                    { $state.Inventory.Subjects['22'].Create = 0 }, { $state.Inventory.Subjects['22'].Initial = 0 },
                    { $state.Inventory.Root = 0 }, { $state.Inventory.Root = 12 },
                    { $state.Inventory.Subjects['11'].Exit = 0 }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlInventoryComplete
                    try {
                        . $mutation
                        $report = New-HermesStartupReport $state $true $true
                        Assert-Control ($report.inventory.complete -eq 0 -and $report.inventory.status -eq 2 `
                            -and $report.completed -eq 0 -and $report.armingVerified -eq 0)
                        if (-not (Test-HermesStartupInventoryBinding $state)) {
                            Assert-Control ($report.inventory.nativeFinalBound -eq 0 -and $report.inventory.nativeFinalExit -eq 0 `
                                -and $report.inventory.targetCreate -eq 0 -and $report.inventory.unclassifiedEvents -gt 0)
                        }
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
                $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode Inventory
                Update-HermesStartupRows $state ($finalV2.Replace('resumed=1', 'resumed=0') + "`n")
                $report = New-HermesStartupReport $state $true $true
                Assert-Control ($null -ne $state.Final -and $report.inventory.nativeFinalBound -eq 0 `
                    -and $report.inventory.nativeFinalExit -eq 0 -and $report.parentUnchanged -eq 0)
            }
            406 {
                $state = New-ControlInventoryComplete
                foreach ($current in 33..46) {
                    foreach ($kind in @('Create', 'Exit')) {
                        Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Kind $kind -Current $current) $true
                    }
                }
                $inventory = (New-HermesStartupReport $state $true $true).inventory
                Assert-Control ($inventory.complete -eq 1 -and $inventory.subjects -eq 16 -and $inventory.otherSubjects -eq 14)
                Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Current 47) $true
                Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Kind Exit -Current 47) $true
                $inventory = (New-HermesStartupReport $state $true $true).inventory
                Assert-Control ($inventory.complete -eq 0 -and $inventory.status -eq 4 -and $inventory.limit -eq 1 `
                    -and $inventory.subjects -eq 16 -and $state.Inventory.Subjects.Count -eq 16 `
                    -and $inventory.otherCreate -eq 14 -and $inventory.otherExit -eq 14)
            }
            407 {
                $mutations = @(
                    { $state.Out.End = $false }, { $state.Err.End = $false }, { $state.InputClosed = $false },
                    { $state.CleanupFault = 13 }, { $state.Timeout = 1 },
                    { $state.Out.Tail = 'HERMES_CDB v=3 kind=1 pid=' },
                    { $state.Inventory.Block = @{ Kind = 1; Version = 3; Pid = 22; Event = 0; Valid = $true } }
                )
                foreach ($index in 0..($mutations.Count - 1)) {
                    $state = New-ControlInventoryComplete
                    try {
                        . $mutations[$index]
                        $inventory = (New-HermesStartupReport $state $true $true).inventory
                        $expected = if ($index -le 3) { 6 } elseif ($index -eq 4) { 5 } else { 2 }
                        Assert-Control ($inventory.complete -eq 0 -and $inventory.status -eq $expected)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
                foreach ($fault in @(1460, 13)) {
                    $state = New-ControlInventoryComplete -Final ($finalV2.Replace('fault=0', "fault=$fault"))
                    try {
                        $inventory = (New-HermesStartupReport $state $true $true).inventory
                        Assert-Control ($inventory.complete -eq 0 -and $inventory.nativeFinalFault -eq $fault `
                            -and $inventory.status -eq $(if ($fault -eq 1460) { 5 } else { 8 }))
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
                $state = New-ControlInventoryComplete -Final ($finalV2.Replace('cleanup=1', 'cleanup=0'))
                Assert-Control ((New-HermesStartupReport $state $true $true).inventory.status -eq 6)
                $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                $state = New-ControlInventoryComplete
                $state.Out.End = $false
                $reader = New-ControlReader ((Get-ControlInventoryEvent).TrimEnd("`n"))
                try {
                    Start-TraceRead $state.Out $reader.BaseStream
                    Receive-TraceRead $state.Out $reader.BaseStream $true
                    $inventory = (New-HermesStartupReport $state $true $true).inventory
                    Assert-Control ($state.Out.End -and $inventory.complete -eq 0 -and $inventory.status -eq 2 `
                        -and $inventory.partialLine -eq 1 -and $inventory.openBlock -eq 1)
                } finally { Close-TraceRead $state.Out $reader }
            }
            408 {
                $state = New-ControlInventoryComplete
                $state.InputClosed = $false
                $physical = [StartupControlWriteStream]::new(0)
                $debugger = [pscustomobject]@{ StandardInput = [IO.StreamWriter]::new($physical); HasExited = $false }
                Close-TraceInventoryInput
                $closes = $physical.Closes
                Close-TraceInventoryInput
                $definition = @($driverAst.FindAll({
                    param($node)
                    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Publish-TraceArm'
                }, $true))
                . ([scriptblock]::Create($definition[0].Extent.Text))
                Publish-TraceArm
                Assert-Control ($state.InputClosed -and $physical.Closed -and $physical.Writes -eq 0 `
                    -and $physical.Closes -eq $closes -and $state.CleanupFault -eq 0 -and -not $state.ContinueAttempted)
            }
            409 {
                $state = New-ControlInventoryComplete
                $state.InputClosed = $false
                $physical = [StartupControlWriteStream]::new(2)
                $debugger = [pscustomobject]@{ StandardInput = [IO.StreamWriter]::new($physical) }
                Close-TraceInventoryInput
                $report = New-HermesStartupReport $state $true $true
                Assert-Control (-not $state.InputClosed -and $state.CleanupFault -eq 13 -and $physical.Writes -eq 0 `
                    -and $report.inventory.complete -eq 0 -and $report.inventory.status -eq 6 -and $report.completed -eq 0)
                $refused = $false
                try { Invoke-TraceCollection } catch { $refused = $true }
                Assert-Control ($refused -and $physical.Writes -eq 0 -and -not $state.InputClosed)
            }
            410 {
                $state = New-ControlNativeCreated -DiagnosticMode Inventory -WithoutCreateCallback
                Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent) $true
                $ibp = (Get-ControlInventoryEvent -Kind Initial).Replace(
                    "HERMES_CDB v=3 kind=6", "HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002`nHERMES_CDB v=3 kind=6")
                Add-HermesStartupText $state $state.Out $ibp $true
                Assert-Control ((Test-HermesStartupInventoryBinding $state) -and $state.Arm.Flags -eq 2)
                $state.Stopped = $true; $state.Out.Tail = '1:000> '
                $state.Acked = $true; $state.Continued = $true; $state.InputClosed = $true
                $state.ArmedBinding = @{ Helper = 11; Pid = 22; Born = 123; Flags = 2 }
                $state.Failure = @{ Kind = 1; Status = 3221225794; ModuleCategory = 1; ModuleSha256 = 'f' * 64 }
                Assert-Control (-not (Test-HermesStartupInitialBinding $state) -and -not (Test-HermesStartupArm $state) `
                    -and -not (Test-HermesStartupArmed $state) -and (Get-HermesStartupTypedFlags $state) -eq -1)
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    $refused = $false
                    try { Invoke-HermesStartupContinue $state $writer } catch { $refused = $true }
                    Add-HermesStartupLine $state $failureLine
                    $report = New-HermesStartupReport $state $true $true
                    Assert-Control ($refused -and $physical.Writes -eq 0 -and $report.observationOnly -eq 1 `
                        -and $report.completed -eq 0 -and $report.armingVerified -eq 0 -and $report.firstFailureKind -eq 0 `
                        -and $report.moduleSha256 -ceq '' -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                } finally { $writer.Dispose() }
            }
            411 {
                $mutations = @(
                    { $state.Target.born = 124 }, { $state.Final.born = 124 }, { $state.CreatedBinding.Born = 124 },
                    { $state.CommandSha256 = 'c' * 64 }, { $state.CreateCommandSha256 = 'c' * 64 },
                    { $state.InitialCommandSha256 = 'c' * 64 }, { $state.ExitCommandSha256 = 'c' * 64 },
                    { $state.FilterVerified.Cpr = $false },
                    { Invoke-ControlRowsRead ([IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes('x' * 2049))) },
                    { Invoke-ControlRowsRead ([IO.MemoryStream]::new([byte[]]@(255))) },
                    {
                        Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" +
                            $finalV2.Replace('born=123', 'born=124') + "`n")
                    }
                )
                foreach ($mutation in $mutations) {
                    $state = New-ControlInventoryComplete
                    try {
                        . $mutation
                        $report = New-HermesStartupReport $state $true $true
                        Assert-Control ($report.inventory.nativeFinalBound -eq 0 -and $report.inventory.nativeFinalExit -eq 0 `
                            -and $report.inventory.nativeFinalParent -eq 0 -and $report.parentUnchanged -eq 0 `
                            -and $report.inventory.complete -eq 0 -and $report.completed -eq 0)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            412 {
                $state = New-ControlInventoryComplete -Final ($finalV2.Replace('exit=3221225794', 'exit=4294967295').Replace(
                    'fault=0 cleanupFault=0', 'fault=65535 cleanupFault=65535'))
                foreach ($current in 33..46) {
                    foreach ($kind in @('Create', 'Exit')) {
                        Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Kind $kind -Current $current) $true
                    }
                }
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                foreach ($name in @('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst', 'CreatedCheckFirst')) {
                    $state.Observations[$name] = 16383
                }
                foreach ($subject in $state.Inventory.Subjects.Values) {
                    foreach ($name in @('Create', 'Exit', 'Initial')) { $subject[$name] = 8388608 }
                }
                foreach ($name in @('Malformed', 'Mismatch')) { $state.Inventory[$name] = 8388608 }
                $state.Observations.CprFilter = 9; $state.Observations.EprFilter = 9
                Set-HermesStartupTracePoint $state 26
                foreach ($name in @('DebuggerSha256', 'HelperSha256')) { $state[$name] = 'f' * 64 }
                foreach ($stream in @($state.Out, $state.Err)) {
                    $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                }
                $state.AcceptedUtf8Bytes = [long]8388608
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'private|HERMES_CDB|C:|D:|sample\.dll|S-1-|"Subjects":\{|RootHash|pid' `
                    -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                foreach ($value in $report.inventory.Values) {
                    Assert-Control (($value -is [int] -or $value -is [long]) -and $value -ge 0 -and $value -le [long][uint32]::MaxValue)
                }
                foreach ($bad in @('private --command', $null, $true, -1, 8388609)) {
                    $state.Inventory.Malformed = $bad
                    $summary = New-HermesStartupInventorySummary $state $true $true
                    Assert-Control ($summary.malformed -eq 0)
                }
            }
            413 {
                $state = New-ControlNativeCreated -DiagnosticMode Inventory -WithoutCreateCallback
                $before = $state.Inventory.Subjects.Count
                $state.AcceptedUtf8Bytes = [long]8388607
                Add-HermesStartupText $state $state.Out "`n" $true
                $hash = [Convert]::ToHexString($state.Out.Hash.GetCurrentHash())
                foreach ($line in (Get-ControlInventoryEvent -Current 33) -split "`n") {
                    Add-HermesStartupInventoryLine $state $line
                    Add-HermesStartupLine $state $line
                }
                Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`n")
                Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                    -and $state.Inventory.Subjects.Count -eq $before -and $null -eq $state.Inventory.Block `
                    -and $null -eq $state.Final -and $state.Out.Count -eq 1 `
                    -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $hash)
            }
            414 {
                $state = New-ControlNativeCreated -DiagnosticMode Inventory -WithoutCreateCallback
                & {
                    foreach ($name in @('Publish-TraceCreated', 'Publish-TraceArm')) {
                        $definition = @($driverAst.FindAll({
                            param($node)
                            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
                        }, $true))
                        . ([scriptblock]::Create($definition[0].Extent.Text))
                    }
                    function Read-TraceControl {
                        param([string]$Path)
                        Invoke-ControlRowsRead ([IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes(
                            $readyV2 + "`n" + $resumedV2 + "`n" + $finalV2.Replace('exit=3221225794', 'exit=37') + "`n")))
                    }
                    $text = ''
                    foreach ($current in @(22, 33)) {
                        foreach ($kind in @('Create', 'Initial', 'Exit')) {
                            $text += Get-ControlInventoryEvent -Kind $kind -Current $current
                        }
                    }
                    $text += Get-ControlInventoryEvent -Kind Exit -Current 11
                    $text += "ModLoad: C:\private-inventory\secret.dll S-1-5-21-123`n"
                    $physical = [StartupControlWriteStream]::new(0)
                    $debugger = [pscustomobject]@{
                        StandardInput = [IO.StreamWriter]::new($physical)
                        StandardOutput = New-ControlReader $text; StandardError = New-ControlReader ''
                        HasExited = $true; ExitCode = 0
                    }
                    try {
                        Start-TraceRead $state.Out $debugger.StandardOutput.BaseStream
                        Start-TraceRead $state.Err $debugger.StandardError.BaseStream
                        Invoke-TraceCollection
                        $report = New-HermesStartupReport $state $true $true
                        $json = $report | ConvertTo-Json -Depth 4 -Compress
                        Assert-Control ($report.inventory.complete -eq 1 -and $state.Protocol -eq 1 `
                            -and $state.Out.End -and $state.Err.End -and $null -eq $state.Out.Pending -and $null -eq $state.Err.Pending `
                            -and $physical.Closed -and $physical.Writes -eq 0 -and -not $state.ContinueAttempted `
                            -and $state.Out.Count -eq $text.Length -and $state.AcceptedUtf8Bytes -eq [Text.Encoding]::UTF8.GetByteCount($text) `
                            -and $report.inventory.otherCreate -eq 1 -and $report.inventory.otherExit -eq 1 `
                            -and $report.inventory.targetInitial -eq 1 -and $report.completed -eq 0 `
                            -and $json -notmatch 'private-inventory|secret|S-1-|HERMES_CDB|pid')
                    } finally {
                        Close-TraceRead $state.Out $debugger.StandardOutput
                        Close-TraceRead $state.Err $debugger.StandardError
                        $debugger.StandardInput.Dispose()
                    }
                }
            }
            421 {
                $state = New-ControlScopedCreated
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                try {
                    Assert-Control ((Test-HermesStartupScopedAction $state Arm) -and $null -eq $state.Arm `
                        -and $state.Created.Kind -eq 1 -and $state.Created.EventPid -eq 22)
                    $physical.AfterWrite = {
                        Assert-Control ($state.ArmAttempted -and -not $state.Acked -and -not $state.Continued)
                    }
                    Invoke-ControlScopedPublisher
                    Assert-Control ($physical.Writes -eq 1 -and $state.ArmWritten -and -not $physical.Closed `
                        -and -not (Test-HermesStartupScopedAction $state Arm))
                    $expected = '$$<' + (Join-Path $root 'startup-arm.cdb') + "`n"
                    Assert-Control ([Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq $expected)
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Assert-Control ((Test-HermesStartupScopedAction $state Target) -and $state.Arm.Flags -eq 2 `
                        -and $state.Arm.Kind -eq 7 -and $state.Arm.EventPid -eq 22 -and -not $state.Acked)
                    $physical.AfterWrite = {
                        Assert-Control ($state.Acked -and $state.ContinueAttempted -and $null -ne $state.ArmedBinding `
                            -and -not $state.Continued)
                    }
                    Invoke-ControlScopedPublisher
                    $physical.AfterWrite = $null
                    Assert-Control ($state.Continued -and (Test-HermesStartupArmed $state) -and -not $state.InputClosed)
                    $report = Complete-ControlScoped $state $writer
                    Assert-Control ($report.completed -eq 1 -and $report.armingVerified -eq 1 -and $report.firstFailureKind -eq 1 `
                        -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0 `
                        -and $report.scopedCreateVersion -eq 1 -and $report.debuggerExitAvailable -eq 1 `
                        -and $report.debuggerExitValue -eq 0 -and $report.cleanupComplete -eq 1 -and $report.parentUnchanged -eq 1 `
                        -and $state.InputTasks.Count -eq 4 -and $physical.Writes -eq 4 -and $physical.Closed `
                        -and $state.Inventory.Subjects.Count -eq 2 -and $state.Protocol -eq 0 `
                        -and [Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq ($expected + "g`ng`ng`n"))
                } finally { $writer.Dispose() }
            }
            422 {
                foreach ($arguments in @(@{ WithoutReady = $true }, @{ WithoutAck = $true }, @{ WithoutResumed = $true })) {
                    $state = New-ControlScopedCreated @arguments
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm) -and -not $state.ArmAttempted)
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
                foreach ($change in @(
                    { $state.Target.born = 124 }, { $state.Target.image = 0 }, { $state.Target.context = 0 },
                    { $state.Target.debug = 0 }, { $state.Target.pid = 23 }, { $state.Target.helper = 12 },
                    { $state.TargetText = $readyV2.Replace('born=123', 'born=124') }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm) -and $null -eq $state.ScopedInvocation)
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            423 {
                foreach ($change in @(
                    { $state.Initial = 12 }, { $state.Inventory.Root = 12 },
                    { $state.ScopedPublication.Root += '-wrong' }, { $state.ScopedPublication.Extension += '-wrong' },
                    { $state.CreatedBinding.Scoped.Root += '-wrong' }, { $state.Inventory.RootFault = 1 },
                    { $state.ExtensionSha256 = 'f' * 64 }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            { $_ -in 424..432 } {
                $state = New-ControlScopedCreated
                $name = @('DebuggerSha256', 'HelperSha256', 'DriverSha256', 'ModuleSha256',
                    'CommandSha256', 'CreateCommandSha256', 'InitialCommandSha256', 'ExitCommandSha256',
                    'ArmCommandSha256')[$case - 424]
                $state[$name] = 'f' * 64
                Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                $state.ScopedPublication[$name] = $state[$name]
                Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
            }
            433 {
                foreach ($change in @(
                    { $state.DiagnosticMode = 'Inventory' }, { $state.PublishedMode = 'Trace' },
                    { $state.ScopedPublication.Mode = 'Trace' }, { $state.CreatedBinding.Mode = 'Trace' }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
                $state = New-ControlScopedCreated
                Assert-Control ($driverAst.ParamBlock.Parameters.Count -eq 0)
                $selection = $driverAst.Find({
                    param($n)
                    $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$state'
                }, $true)
                Assert-Control ($selection.Right.Extent.Text -ceq 'New-HermesStartupState -DiagnosticMode ScopedCreateV1')
            }
            434 {
                foreach ($change in @(
                    { $state.CreatedAcked = $false }, { $state.CreatedBinding = $null },
                    { $state.CreatedBinding.Born = 124 }, { $state.Resumed = $null },
                    { $state.CreatedBinding.RootHash = 'f' * 64 }, { $state.NativeRowSnapshots.Resumed = $null },
                    { $state.NativeRowSnapshots.Resumed.Row.born = 124 },
                    { $state.Resumed.resumed = 0 }, { $state.Resumed.born = 124 },
                    { $state.ResumedText = $resumedV2.Replace('resumed=1', 'resumed=0') },
                    { Update-HermesStartupRows $state ($readyV2.Replace('pid=22', 'pid=23') + "`n") }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            435 {
                foreach ($change in @(
                    { $state.Held.Pid = 23 }, { $state.Held.EventPid = 23 }, { $state.Held.Kind = 5 },
                    { $state.Held.Version = 1 }, { $state.Held.Event = 0 }, { $state.Held.Valid = $false },
                    { $state.Held.Released = $true }, { $state.Created.EventPid = 23 }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            436 {
                foreach ($prompt in @('', 'idle', '1:000>', '1:000> x', '22:0000> ', '1000:000> ', '1:00g> ')) {
                    $state = New-ControlScopedCreated
                    $state.Out.Tail = ''
                    Add-HermesStartupText $state $state.Out $prompt $true
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
                foreach ($change in @(
                    { $state.Out.End = $true }, { $state.Out.Faulted = $true },
                    { $state.Err.Faulted = $true }, { $state.Stopped = $false }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
                $state = New-ControlScopedCreated
                $state.Out.Tail = ''
                Add-HermesStartupText $state $state.Out '99:abc> ' $true
                Assert-Control ((Test-HermesStartupScopedAction $state Arm) -and $state.Held.Pid -eq 22)
            }
            437 {
                foreach ($mode in @('Trace', 'Inventory', 'ScopedCreateV1')) {
                    $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode $mode
                    $sources = Get-TraceSelectedSources $extension
                    foreach ($name in @('cpr', 'ibp', 'epr')) {
                        $text = $sources['startup-' + $name + '.cdb']
                        Assert-Control ($text -cnotmatch '(?i:gflag|peb|sizeof|@@|\.load|\.chain|\|#s|\b(?:dt|db|dd|dq|ed|eb|eq|r|k)\b)')
                        if ($mode -ceq 'ScopedCreateV1') { Assert-Control ($text -cnotmatch '(?m)^g\r?$') }
                    }
                    if ($mode -ceq 'ScopedCreateV1') {
                        Assert-Control ($sources.Count -eq 5 -and $sources['startup.cdb'] -cnotmatch 'gflag|peb|\.load|\.chain' `
                            -and $sources['startup-arm.cdb'] -match '\.load "' `
                            -and [regex]::Matches(($sources.Values -join ''), [regex]::Escape(
                                '!' + $extension.Substring(0, $extension.Length - 4) + '.gflag +sls')).Count -eq 1 `
                            -and $sources['startup-arm.cdb'] -cnotmatch '(?m)^g\r?$|\|#s')
                    }
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            438 {
                $state = New-ControlScopedCreated -WithoutCreate
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    foreach ($kind in @('Create', 'Initial', 'Exit')) {
                        Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Kind $kind -Current 33) + '1:000> ') $true
                        Assert-Control (-not (Test-HermesStartupScopedAction $state Arm) -and (Test-HermesStartupScopedAction $state Side))
                        Invoke-HermesStartupScopedInput $state $writer Side
                        Assert-Control ($null -eq $state.Created -and $null -eq $state.Arm -and -not $state.ArmAttempted)
                    }
                    Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent) + '1:000> ') $true
                    Assert-Control ((Test-HermesStartupScopedAction $state Arm) -and $state.Protocol -eq 0 `
                        -and $state.Inventory.Subjects.Count -eq 2 -and $state.SideWrites -eq 3 `
                        -and [Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq "g`ng`ng`n")
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            439 {
                $state = New-ControlScopedCreated -WithoutCreate
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Kind Initial -Current 11) + '1:000> ') $true
                    Invoke-HermesStartupScopedInput $state $writer Side
                    Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Kind Exit -Current 11).Replace('code 25', 'code 0') + '1:000> ') $true
                    Invoke-HermesStartupScopedInput $state $writer Side
                    Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent) + '1:000> ') $true
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm) -and $null -eq $state.Arm `
                        -and -not $state.ArmAttempted -and $state.SideWrites -eq 2)
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            440 {
                foreach ($flags in @('', '00000000', '00000001', '0000000', '000000002', 'unavailable')) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback $flags) $true
                        Assert-Control (-not (Test-HermesStartupScopedAction $state Target) `
                            -and -not $state.Acked -and (Get-HermesStartupTypedFlags $state) -eq -1)
                        if ($flags -ceq '') { Assert-Control ($state.Arm.Flags -eq -1 -and $state.Unknown -eq 1) }
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            441 {
                foreach ($change in @(
                    { param($t) $t.Replace('kind=7 pid=22', 'kind=7 pid=33') },
                    { param($t) $t.Replace('Last event: 16.1:', 'Last event: 21.1:') },
                    { param($t) $t.Replace('Create process 1:16', 'Break instruction exception - code 80000003 (first chance)') },
                    { param($t) $t.Replace('kind=8 pid=22', 'kind=8 pid=33') },
                    { param($t) $t.Replace('v=1 pid=22 value=', 'v=1 pid=33 value=') }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (& $change (Get-ControlScopedReadback)) $true
                        Assert-Control (-not (Test-HermesStartupScopedAction $state Target) -and -not $state.Continued `
                            -and $state.InputTasks.Count -eq 1)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            442 {
                $state = New-ControlScopedCreated
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    $refused = $false
                    try { Invoke-HermesStartupScopedInput $state $writer Arm } catch { $refused = $true }
                    Assert-Control ($refused -and $physical.Writes -eq 1)
                    Invoke-HermesStartupScopedInput $state $writer Target
                    $refused = $false
                    try { Invoke-HermesStartupScopedInput $state $writer Target } catch { $refused = $true }
                    Assert-Control ($refused -and $physical.Writes -eq 2 -and $state.Acked -and $state.Continued)
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Assert-Control ($state.Protocol -eq 1 -and $physical.Writes -eq 2)
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            443 {
                $state = New-ControlScopedCreated
                Add-HermesStartupLine $state $failureLine
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Failure `
                    -and -not (Test-HermesStartupScopedAction $state Arm))
            }
            444 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Add-HermesStartupLine $state $failureLine
                    Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Failure -and -not $state.Acked `
                        -and -not (Test-HermesStartupScopedAction $state Target))
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            445 {
                foreach ($late in @('Final', 'Target', 'Helper')) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        Invoke-HermesStartupScopedInput $state $writer Target
                        switch ($late) {
                            Final { Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n" + $finalV2 + "`n") }
                            Target { Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Kind Exit) $true }
                            Helper { Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Kind Exit -Current 11) $true }
                        }
                        Add-HermesStartupLine $state $failureLine
                        Assert-Control ($null -eq $state.Failure -and $state.Protocol -eq 1)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            446 {
                foreach ($mode in @(1, 3)) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new($mode)
                    $writer = [IO.StreamWriter]::new($physical)
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    try {
                        $refused = $false
                        try { Invoke-HermesStartupScopedInput $state $writer Arm } catch { $refused = $true }
                        Assert-Control ($refused -and $state.Error -eq 13 -and $state.ArmAttempted -and -not $state.ArmWritten `
                            -and $state.InputClosed -and $physical.Closed -and $physical.Writes -eq 1 `
                            -and $state.InputTasks.Count -eq 1 -and $state.InputTasks[0].IsCompleted `
                            -and -not $state.Acked -and $clock.ElapsedMilliseconds -lt 10000)
                    } finally {
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            447 {
                $state = New-ControlScopedCreated
                $pipe = New-ControlPipe
                $handle = $pipe.Writer.SafePipeHandle
                $writer = [IO.StreamWriter]::new($pipe.Writer, [Text.Encoding]::ASCII)
                try {
                    $bytes = [byte[]]::new(1024)
                    $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Assert-Control ($read.Wait(5000))
                    $count = $read.GetAwaiter().GetResult()
                    Assert-Control ([Text.Encoding]::ASCII.GetString($bytes, 0, $count) -ceq
                        ('$$<' + (Join-Path $root 'startup-arm.cdb') + "`n") -and -not $handle.IsClosed -and -not $state.InputClosed)
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                    Invoke-HermesStartupScopedInput $state $writer Target
                    Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 2 `
                        -and $bytes[0] -eq 103 -and $bytes[1] -eq 10 -and $state.Acked -and -not $handle.IsClosed)
                    $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                    Close-HermesStartupScopedInput $state $writer
                    Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 0 -and $handle.IsClosed `
                        -and $state.InputClosed -and $state.CleanupFault -eq 0 `
                        -and @($state.InputTasks | Where-Object { -not $_.IsCompletedSuccessfully }).Count -eq 0)
                } finally { $writer.Dispose(); $pipe.Writer.Dispose(); $pipe.Reader.Dispose() }
            }
            448 {
                $state = New-ControlScopedCreated
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                $otherPhysical = [StartupControlWriteStream]::new(0)
                $other = [IO.StreamWriter]::new($otherPhysical)
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    $refused = $false
                    try { Invoke-HermesStartupScopedInput $state $other Target } catch { $refused = $true }
                    Assert-Control ($refused -and $physical.Writes -eq 1 -and $otherPhysical.Writes -eq 0 -and -not $state.Acked)
                    foreach ($inputRole in @('g;!gflag', '$$><', '$$<', '$$>a<', 'Arm arbitrary-argument')) {
                        $refused = $false
                        try { Invoke-HermesStartupScopedInput $state $writer $inputRole } catch { $refused = $true }
                        Assert-Control ($refused -and $physical.Writes -eq 1)
                    }
                } finally { Close-HermesStartupScopedInput $state $writer; $other.Dispose() }
            }
            449 {
                foreach ($fault in @(1, 2, 3)) {
                    $script:controlCommandFaultName = 'startup-arm.cdb'; $script:controlCommandFault = $fault
                    $refused = $false
                    try { $state = New-ControlScopedCreated } catch { $refused = $true }
                    Assert-Control ($refused -and $script:controlCommandWrites.Count -eq 5 `
                        -and $script:controlLaunchBindings -eq 0)
                }
            }
            450 {
                foreach ($which in @('Command', 'Source')) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $bad = [IO.MemoryStream]::new([byte[]]@(1, 2, 3), $false)
                    $pins.Add($bad)
                    try {
                        if ($which -ceq 'Command') { $state.CommandPins['startup-arm.cdb'] = $bad }
                        else { $state.SourcePins.Driver = $bad }
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        $refused = $false
                        try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                        Assert-Control ($refused -and $physical.Writes -eq 0 -and -not $state.ArmAttempted)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            451 {
                $state = New-ControlScopedCreated
                $before = Get-ControlStreamSnapshot $state.Out
                $hash = [Convert]::ToHexString($state.Out.Hash.GetCurrentHash())
                $count = $state.Out.Count
                $state.AcceptedUtf8Bytes = [long]8388607
                Add-HermesStartupText $state $state.Err 'x' $false
                $stream = [StartupControlIdleStream]::new((Get-ControlScopedReadback))
                try {
                    Start-TraceRead $state.Out $stream
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=7 pid=22'
                    Assert-Control ($state.Bounds -eq 1 -and $state.AcceptedUtf8Bytes -eq 8388608 `
                        -and $stream.Reads -eq 0 -and $null -eq $state.Out.Pending -and $state.Out.Count -eq $count `
                        -and (Get-ControlStreamSnapshot $state.Out) -ceq $before `
                        -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $hash `
                        -and -not (Test-HermesStartupScopedAction $state Arm) -and $null -eq $state.Arm)
                } finally { $stream.Dispose() }
            }
            452 {
                $state = New-ControlScopedCreated
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                foreach ($stream in @($state.Out, $state.Err)) {
                    $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                }
                $state.AcceptedUtf8Bytes = [long]8388608; $state.SideWrites = 48
                $state.DebuggerExitAvailable = 1; $state.DebuggerExitValue = [long][uint32]::MaxValue
                $report = New-HermesStartupReport $state $true $true
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $json -notmatch 'command-root|selected-sdk|fixed-source|HERMES_CDB|pid|S-1-|"Subjects":\{' `
                    -and $report.completed -eq 0 -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0)
            }
            453 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Invoke-HermesStartupScopedInput $state $writer Target
                    $null = Complete-ControlScoped $state $writer
                    foreach ($value in @([int]13, [int]-1073741502, [long]4294967295)) {
                        Set-HermesStartupDebuggerExit $state $value
                        $state.Error = 13
                        $report = New-HermesStartupReport $state $true $true
                        Assert-Control ($report.completed -eq 0 -and $report.status -eq 8 -and $report.error -eq 13 `
                            -and $report.debuggerExitAvailable -eq 1 -and $report.debuggerExitValue -ge 0 `
                            -and $report.firstFailureKind -eq 1 -and $report.armingVerified -eq 1 `
                            -and $report.originalGatePassed -eq 0 -and $report.nativeAcceptance -eq 0)
                    }
                    foreach ($bad in @($null, '0', [long]-2147483649, [long]4294967296, $true)) {
                        $state.DebuggerExitAvailable = 0; $state.DebuggerExitValue = [long]0
                        Set-HermesStartupDebuggerExit $state $bad
                        Assert-Control ($state.DebuggerExitAvailable -eq 0 -and $state.Error -eq 13)
                    }
                } finally { $writer.Dispose() }
            }
            454 {
                $state = New-ControlScopedCreated -WithoutCreate
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    foreach ($current in 33..48) {
                        foreach ($kind in @('Create', 'Initial', 'Exit')) {
                            Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Kind $kind -Current $current) + '1:000> ') $true
                            Invoke-HermesStartupScopedInput $state $writer Side
                        }
                    }
                    Assert-Control ($state.Inventory.Subjects.Count -eq 16 -and $state.SideWrites -eq 48 `
                        -and -not $state.ArmAttempted -and $state.Protocol -eq 0)
                    Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent) + '1:000> ') $true
                    Assert-Control ($state.Protocol -eq 1 -and $state.Inventory.Limit -eq 1 `
                        -and $state.Inventory.Subjects.Count -eq 16 -and -not (Test-HermesStartupScopedAction $state Arm))
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            455 {
                foreach ($bad in @(
                    (Get-ControlInventoryEvent), (Get-ControlInventoryEvent -Event 23),
                    (Get-ControlInventoryEvent -Close 23),
                    "HERMES_CDB v=3 kind=2 pid=22`n", "Last event: 16.1: Create process 1:16`n",
                    "HERMES_CDB v=3 kind=1 pid=0`n"
                )) {
                    $state = New-ControlScopedCreated
                    Add-HermesStartupText $state $state.Out $bad $true
                    Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupScopedAction $state Arm) `
                        -and -not $state.ArmAttempted)
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            456 {
                $state = New-ControlScopedCreated -WithoutCreate
                Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Current 33) + '1:000> ') $true
                Add-HermesStartupLine $state ($failureLine.Replace('16:1', '21:1'))
                Assert-Control ($state.Unknown -eq 0 -and $state.Protocol -eq 0 -and $null -eq $state.Failure `
                    -and -not (Test-HermesStartupScopedAction $state Arm))
                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=33 value=00000002'
                Assert-Control ($state.Protocol -eq 1 -and $null -eq $state.Arm -and -not $state.ArmAttempted)
            }
            457 {
                foreach ($change in @(
                    { $state.Error = 13 }, { $state.Timeout = 1 }, { $state.CleanupFault = 13 },
                    { $state.Protocol = 1 }, { $state.Unknown = 1 }, { $state.Bounds = 1 },
                    { $state.Final = ConvertFrom-HermesStartupRow $finalV2 },
                    { $state.Exits['22'] = 3221225794 }, { $state.Exits['11'] = 0 }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm) -and -not $state.ArmAttempted)
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            458 {
                foreach ($change in @(
                    { param($t) $state.ScopedInvocation.Source.Extension += '.wrong'; $t },
                    { param($t) $t.Replace('Extension DLL chain:', 'No export gflag found') },
                    { param($t) $t.Replace('HERMES_NTGLOBALFLAG', "Unable to read PEB`nHERMES_NTGLOBALFLAG") }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (& $change (Get-ControlScopedReadback)) $true
                        Assert-Control (-not (Test-HermesStartupScopedAction $state Target) -and -not $state.Acked)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            459 {
                & {
                    foreach ($name in @('Assert-TracePath', 'Open-TracePin', 'Get-TraceHash', 'Write-TraceCommandFile',
                            'Read-TraceControl', 'Publish-TraceCreated', 'Publish-TraceArm')) {
                        $definition = $driverAst.Find({
                            param($n)
                            $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name
                        }, $true)
                        . ([scriptblock]::Create($definition.Extent.Text))
                    }
                    function Start-Sleep { param([int]$Milliseconds) [Threading.Thread]::Sleep($Milliseconds) }
                    $scope = [guid]::NewGuid().ToString('N')
                    $root = Join-Path $PSScriptRoot ('obj/startup-pipe-controls/' + $scope)
                    $null = [IO.Directory]::CreateDirectory($root)
                    $extension = Join-Path $root 'selected-sdk/winxp/exts.dll'
                    $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode ScopedCreateV1
                    $state.DebuggerSha256 = 'd' * 64; $state.HelperSha256 = 'e' * 64
                    $sdkPin = [IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes('synthetic signed SDK source'), $false)
                    $pins.Add($sdkPin)
                    $state.ExtensionSha256 = Get-ControlCommandHash $sdkPin
                    $state.ExtensionSource = @{ Path = $extension; Sha256 = $state.ExtensionSha256; Pin = $sdkPin }
                    $owned = [Diagnostics.Process]::new()
                    try {
                        foreach ($entry in @(
                            @('Driver', (Join-Path $PSScriptRoot 'Invoke-NativeStartupTrace.ps1')),
                            @('Module', (Join-Path $PSScriptRoot 'Native-StartupTrace.psm1'))
                        )) {
                            $pin = Open-TracePin $entry[1]
                            $state.SourcePins[$entry[0]] = $pin
                            $state[$entry[0] + 'Sha256'] = Get-TraceHash $pin
                        }
                        Set-HermesStartupTracePoint $state 24
                        $null = Publish-TraceCommands $extension
                        Confirm-TraceCommandSelection $extension
                        Assert-Control ($state.ScopedPublication.QualifiedModule -ceq
                            $extension.Substring(0, $extension.Length - 4) -and
                            [IO.File]::ReadAllText((Join-Path $root 'startup-arm.cdb')).Contains(
                                '.catch { !' + $state.ScopedPublication.QualifiedModule + '.gflag +sls;'))
                        $start = [Diagnostics.ProcessStartInfo]::new()
                        $start.FileName = [Environment]::ProcessPath
                        $start.UseShellExecute = $false
                        $start.RedirectStandardInput = $true; $start.StandardInputEncoding = [Text.Encoding]::ASCII
                        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
                        foreach ($argument in @('-NoLogo', '-NoProfile', '-File', $PSCommandPath,
                                '-PipeFixture', '4096', '-PipeScope', $scope)) { $start.ArgumentList.Add($argument) }
                        $owned.StartInfo = $start
                        Assert-Control ($owned.Start())
                        $handle = $owned.SafeHandle
                        $debugger = $owned
                        Start-TraceRead $state.Out $owned.StandardOutput.BaseStream
                        Start-TraceRead $state.Err $owned.StandardError.BaseStream
                        Set-HermesStartupTracePoint $state 26
                        Invoke-TraceCollection
                        Assert-Control ($owned.WaitForExit(5000) -and $owned.ExitCode -eq 0)
                        $report = New-HermesStartupReport $state $true $true
                        Assert-Control ($report.completed -eq 1 -and $report.armingVerified -eq 1 -and $report.firstFailureKind -eq 1 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                            -and $state.ArmAttempted -and $state.ArmWritten -and $state.Continued -and $state.InputClosed `
                            -and $state.SideWrites -eq 4 -and $state.Inventory.Subjects.Count -eq 3 `
                            -and $state.Inventory.Subjects['22'].Initial -eq 0 -and $state.Protocol -eq 0 `
                            -and $state.InputTasks.Count -eq 6 -and $state.Out.End -and $state.Err.End `
                            -and $state.ScopedStages -eq 3 -and $state.Arm.Stages -eq 3 `
                            -and $report.observations.scopedStages -eq 3 -and $report.observations.scopedEvaluator -eq 1 `
                            -and $report.debuggerExitAvailable -eq 1 -and $report.debuggerExitValue -eq 0 `
                            -and [Text.Encoding]::UTF8.GetByteCount(($report | ConvertTo-Json -Depth 4 -Compress) + "`n") -le 4096)
                    } finally {
                        if (-not $state.InputClosed -and $null -ne $state.InputWriter) {
                            Close-HermesStartupScopedInput $state $owned.StandardInput
                        }
                        if (-not $owned.HasExited) { $owned.Kill() }
                        Assert-Control ($owned.WaitForExit(5000))
                        Close-TraceRead $state.Out $owned.StandardOutput
                        Close-TraceRead $state.Err $owned.StandardError
                        $owned.Dispose()
                        foreach ($pin in $pins) { $pin.Dispose() }
                        $pins.Clear()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        Remove-Item -LiteralPath $root -Recurse -Force
                    }
                }
            }
            460 {
                $state = New-ControlScopedCreated -WithoutCreate
                $output = New-ControlPipe
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    Start-TraceRead $state.Out $output.Reader.BaseStream
                    foreach ($text in @((Get-ControlInventoryEvent), '1:0', '00> ')) {
                        Write-ControlPipe $output ([Text.Encoding]::UTF8.GetBytes($text))
                        Receive-ControlPipe $state.Out $output $true
                        if ($text -cne '00> ') { Assert-Control (-not (Test-HermesStartupScopedAction $state Arm)) }
                    }
                    Assert-Control ((Test-HermesStartupScopedAction $state Arm) -and $physical.Writes -eq 0)
                    $output.Writer.Dispose()
                    Receive-ControlPipe $state.Out $output $true
                    Assert-Control ($state.Out.End -and -not (Test-HermesStartupScopedAction $state Arm) -and $physical.Writes -eq 0)
                } finally { Close-ControlPipe $state.Out $output; $writer.Dispose() }
            }
            461 {
                $state = New-ControlScopedCreated
                $pipe = New-ControlPipe
                $handle = $pipe.Writer.SafePipeHandle
                $encoding = [Text.Encoding]::GetEncoding('us-ascii',
                    [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
                $writer = [IO.StreamWriter]::new($pipe.Writer, $encoding)
                try {
                    $bytes = [byte[]]::new(1024)
                    $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -gt 2)
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                    Invoke-HermesStartupScopedInput $state $writer Target
                    Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 2)
                    $writer.Write([char]0x100)
                    $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                    Close-HermesStartupScopedInput $state $writer
                    Assert-Control ($state.CleanupFault -eq 13 -and -not $state.InputClosed -and $handle.IsClosed `
                        -and $read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 0 `
                        -and $state.Continued -and $state.InputTasks.Count -eq 2)
                } finally { $writer.Dispose(); $pipe.Writer.Dispose(); $pipe.Reader.Dispose() }
            }
            462 {
                foreach ($role in @('Target', 'Side')) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        if ($role -ceq 'Side') {
                            Invoke-HermesStartupScopedInput $state $writer Target
                            Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Current 33) + '1:000> ') $true
                        }
                        $physical.Mode = 3
                        $refused = $false
                        try { Invoke-HermesStartupScopedInput $state $writer $role } catch { $refused = $true }
                        Assert-Control ($refused -and $state.Error -eq 13 -and $state.InputClosed -and $physical.Closed `
                            -and $state.InputPending.IsCompleted -and -not $state.InputPending.IsCompletedSuccessfully `
                            -and (New-HermesStartupReport $state $false $false).completed -eq 0)
                        if ($role -ceq 'Target') { Assert-Control ($state.Acked -and -not $state.Continued) }
                    } finally {
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            463 {
                foreach ($parse in @($false, $true)) {
                    $state = New-ControlScopedCreated
                    $stream = if ($parse) { $state.Out } else { $state.Err }
                    $stream.Tail = ''
                    Add-HermesStartupText $state $stream ('x' * 4096) $parse
                    Assert-Control ($state.Bounds -eq 0 -and $stream.Tail.Length -eq 4096)
                    Add-HermesStartupText $state $stream "x`nHERMES_CDB v=1 kind=0 pid=99`n" $parse
                    $before = Get-ControlStreamSnapshot $stream
                    Add-HermesStartupText $state $stream 'after-bound' $parse
                    Assert-Control ($state.Bounds -eq 1 -and $stream.Prefix.Length -eq 4096 `
                        -and (Get-ControlStreamSnapshot $stream) -ceq $before -and $state.Initial -eq 11 `
                        -and -not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            464 {
                & {
                    # Windows command-string encoding only; no file or SDK load.
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    $sdk = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                    $sources = New-ScopedCreateCommands $sdk
                    $module = $sdk.Substring(0, $sdk.Length - 4)
                    Assert-Control ($sources['startup-arm.cdb'].Contains(('.load "' + $sdk.Replace('\', '\\') + '"')) `
                        -and $sources['startup-arm.cdb'].Contains(('!' + $module + '.gflag +sls')) `
                        -and $sources['startup-arm.cdb'] -cnotmatch '!exts\.gflag|!gflag|\.loadby|\.extpath|\.setdll' `
                        -and $sources['startup-arm.cdb'] -notmatch '\bg\r?\n')
                    $refused = $false
                    try { $null = New-ScopedCreateCommands ('C:\' + ('x\' * 127) + 'exts.dll') } catch { $refused = $true }
                    Assert-Control ($refused)
                }
            }
            465 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Invoke-HermesStartupScopedInput $state $writer Target
                    $null = Complete-ControlScoped $state $writer
                    $state.Final.born = 124
                    $report = New-HermesStartupReport $state $true $true
                    Assert-Control ($report.completed -eq 0 -and $report.parentUnchanged -eq 0 -and $report.cleanupComplete -eq 0)
                    $state.Final.born = 123
                    $state.DebuggerExitAvailable = 0
                    $report = New-HermesStartupReport $state $true $true
                    Assert-Control ($report.completed -eq 0 -and $report.status -eq 8 -and $report.error -eq 13 `
                        -and $report.firstFailureKind -eq 1 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                } finally { $writer.Dispose() }
            }
            466 {
                $state = New-ControlScopedCreated
                $sources = Get-TraceSelectedSources $extension
                foreach ($text in $sources.Values) {
                    foreach ($line in ($text -split '\r?\n')) {
                        if ($line.Contains('.printf', [StringComparison]::Ordinal)) {
                            Add-HermesStartupLine $state ('1:000> ' + $line)
                        }
                    }
                }
                Assert-Control ($state.Protocol -eq 0 -and $null -eq $state.Arm -and -not $state.ArmAttempted)
                Add-HermesStartupLine $state 'HERMES_CDB v=3 kind=1 pid=99 .printf malformed'
                Assert-Control ($state.Protocol -eq 1 -and -not (Test-HermesStartupScopedAction $state Arm))
            }
            467 {
                $state = New-ControlScopedCreated -WithoutCreate
                $reader = New-ControlReader ((Get-ControlInventoryEvent) + '1:000> ')
                try {
                    Start-TraceRead $state.Err $reader.BaseStream
                    Receive-TraceRead $state.Err $reader.BaseStream $false
                    Assert-Control ($state.Err.End -and $null -eq $state.Created -and $null -eq $state.Held `
                        -and -not (Test-HermesStartupScopedAction $state Arm))
                    $state.AcceptedUtf8Bytes = [long]8388608
                    $source = [IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes($readyV2 + "`n"))
                    try {
                        Invoke-ControlRowsRead $source
                        Assert-Control ($source.Position -eq 0 -and $state.Bounds -eq 1)
                    } finally { $source.Dispose() }
                } finally { Close-TraceRead $state.Err $reader }
            }
            468 {
                foreach ($change in @(
                    { $state.NativeRowsFault = 1 }, { Add-HermesStartupSdkError $state 'Unable to read PEB' }
                )) {
                    $state = New-ControlScopedCreated
                    . $change
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    $state.ScopedInvocation.Source.ArmCommandSha256 = 'f' * 64
                    Assert-Control (-not (Test-HermesStartupScopedAction $state Target) -and -not $state.Acked `
                        -and $state.InputTasks.Count -eq 1)
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            469 {
                $state = New-ControlScopedCreated
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Invoke-HermesStartupScopedInput $state $writer Target
                    $created = $state.Created.Clone()
                    foreach ($kind in @('Create', 'Initial', 'Exit')) {
                        Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Kind $kind -Current 33) +
                            $failureLine.Replace('16:1', '21:1') + "`n1:000> ") $true
                        Assert-Control ((Test-HermesStartupArmed $state) -and $state.Protocol -eq 0 -and $null -eq $state.Failure `
                            -and -not (Test-HermesStartupScopedAction $state Arm) -and $state.Created.Pid -eq $created.Pid)
                        Invoke-HermesStartupScopedInput $state $writer Side
                    }
                    $report = Complete-ControlScoped $state $writer
                    $text = [Text.Encoding]::ASCII.GetString($physical.ToArray())
                    Assert-Control ($report.completed -eq 1 -and $report.firstFailureKind -eq 1 -and $state.SideWrites -eq 5 `
                        -and $state.Inventory.Subjects['22'].Create -eq 1 -and $state.Inventory.Subjects['33'].Create -eq 1 `
                        -and $state.Inventory.Subjects['33'].Exit -eq 1 -and $state.InputTasks.Count -eq 7 `
                        -and [regex]::Matches($text, '\$\$<').Count -eq 1 `
                        -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                } finally { $writer.Dispose() }
            }
            470 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                    Invoke-HermesStartupScopedInput $state $writer Target
                    Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent) + '1:000> ') $true
                    Assert-Control ($state.Protocol -eq 1 -and $state.Inventory.Subjects['22'].Create -eq 2 `
                        -and $state.Created.Pid -eq 22 -and $state.Created.Kind -eq 1 `
                        -and -not (Test-HermesStartupScopedAction $state Side) -and $state.InputTasks.Count -eq 2)
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            471 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback -Chain (
                        "Extension DLL chain:`n    " + $extension.ToUpperInvariant() + ": image 10.0.1.1, API 1.0.0`n")) $true
                    Assert-Control ((Test-HermesStartupScopedAction $state Target) `
                        -and $state.Observations.SdkSelectedExtensionChain -eq 3)
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            472 {
                foreach ($earlyCreate in @($true, $false)) {
                    $state = if ($earlyCreate) { New-ControlScopedCreated -WithoutReady } else { New-ControlScopedCreated -WithoutCreate }
                    try {
                        if ($earlyCreate) {
                            Assert-Control ($null -eq $state.Created -and $state.Held.Kind -eq 1 `
                                -and -not (Test-HermesStartupScopedAction $state Arm))
                            Update-HermesStartupRows $state ($readyV2 + "`n")
                            Set-HermesStartupCreatedAcknowledged $state
                            Assert-Control (-not (Test-HermesStartupScopedAction $state Arm))
                            Update-HermesStartupRows $state ($readyV2 + "`n" + $resumedV2 + "`n")
                        } else {
                            Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent) + '1:000> ') $true
                        }
                        Assert-Control ((Test-HermesStartupScopedAction $state Arm) -and $state.Created.Pid -eq 22 `
                            -and $state.Inventory.Subjects['22'].Initial -eq 0 -and $state.Protocol -eq 0)
                    } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                }
            }
            473 {
                foreach ($bound in @(
                    @{ Bounds = 1; Bytes = [long]8388608 },
                    @{ Bounds = 0; Bytes = [long]8388608 },
                    @{ Bounds = 1; Bytes = [long]8388609 },
                    @{ Bounds = 1; Bytes = [long]4097 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    $module = Get-Module Native-StartupTrace
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        Invoke-HermesStartupScopedInput $state $writer Target
                        Assert-Control ((Complete-ControlScoped $state $writer).completed -eq 1 `
                            -and $null -ne $state.NativeRowSnapshots.Final -and $null -ne $state.NativeRowSnapshots.Resumed)
                        $state.Bounds = $bound.Bounds; $state.AcceptedUtf8Bytes = $bound.Bytes
                        $outBefore = Get-ControlStreamSnapshot $state.Out
                        $errBefore = Get-ControlStreamSnapshot $state.Err
                        $outHash = [Convert]::ToHexString($state.Out.Hash.GetCurrentHash())
                        $errHash = [Convert]::ToHexString($state.Err.Hash.GetCurrentHash())
                        & $module {
                            $script:controlOriginalRowParser = (Get-Item Function:ConvertFrom-HermesStartupRow).ScriptBlock
                            $script:controlReportRowParses = 0
                            Set-Item Function:script:ConvertFrom-HermesStartupRow {
                                param([string]$Line)
                                $script:controlReportRowParses++
                                & $script:controlOriginalRowParser $Line
                            }
                        }
                        foreach ($call in 1..2) {
                            $report = New-HermesStartupReport $state $true $true
                            Assert-Control ((& $module { $script:controlReportRowParses }) -eq 0 `
                                -and $report.status -eq 7 -and $report.completed -eq 0 `
                                -and $report.cleanupComplete -eq 1 -and $report.parentUnchanged -eq 1 `
                                -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                        }
                        Assert-Control ((Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                            -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore `
                            -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $outHash `
                            -and [Convert]::ToHexString($state.Err.Hash.GetCurrentHash()) -ceq $errHash)
                    } finally {
                        & $module {
                            if (Test-Path Variable:script:controlOriginalRowParser) {
                                Set-Item Function:script:ConvertFrom-HermesStartupRow $script:controlOriginalRowParser
                                Remove-Variable controlOriginalRowParser, controlReportRowParses -Scope Script
                            }
                        }
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            474 {
                foreach ($chain in @('', ("Extension DLL chain:`n" + ("    unsupported SDK display`n" * 8)))) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        $text = Get-ControlScopedReadback -Chain $chain
                        Add-HermesStartupText $state $state.Out $text $true
                        Assert-Control ((Test-HermesStartupScopedAction $state Target) `
                            -and (Get-HermesStartupTypedFlags $state) -eq 2 `
                            -and $state.Observations.SdkChainSupportedRows -eq 0 `
                            -and $state.Observations.SdkSelectedExtensionChain -eq [int]($chain -cne '') `
                            -and $state.Observations.SdkChainLines -eq $(if ($chain -ceq '') { 0 } else { 11 }) `
                            -and $state.ScopedInvocation.Source.QualifiedModule -ceq
                                $extension.Substring(0, $extension.Length - 4))
                        Invoke-HermesStartupScopedInput $state $writer Target
                        $report = Complete-ControlScoped $state $writer
                        Assert-Control ($report.completed -eq 1 -and $report.armingVerified -eq 1 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                            -and $physical.Writes -eq 4)
                    } finally {
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            475 {
                foreach ($change in @(
                    { $state.Final = $null }, { $state.Resumed = $null },
                    { $state.NativeRowSnapshots.Ready = $null },
                    { $state.NativeRowSnapshots.Resumed = $null }, { $state.NativeRowSnapshots.Final = $null },
                    { $state.NativeRowSnapshots.Final.Remove('Row') },
                    { $state.NativeRowSnapshots.Final.Remove('Text') },
                    { $state.NativeRowSnapshots.Final.Row.born = 124 },
                    { $state.NativeRowSnapshots.Ready.Row.born = 124 },
                    { $state.NativeRowSnapshots.Resumed.Row.resumed = 0 },
                    { $state.NativeRowSnapshots.Final.Text += 'x' },
                    { $state.Final.Remove('fault') }, { $state.Final.cleanup = 0 },
                    { $state.Final.cleanup = '1' }, { $state.NativeRowSnapshots.Final.Row.parent = $true },
                    { $state.Final.born = 124 }, { $state.Resumed.born = 124 }, { $state.Target.born = 124 },
                    { $state.TargetText = $readyV2.Replace('born=123', 'born=124') },
                    { $state.ResumedText = $resumedV2.Replace('resumed=1', 'resumed=0') },
                    { $state.FinalText = $finalV2.Replace('pid=22', 'pid=022') },
                    { $state.RowsText = $state.RowsText.TrimEnd("`n") },
                    { $state.RowsText = $state.RowsText.Substring(0, $state.RowsText.Length - 8) },
                    { $state.RowsText = $state.RowsText.Replace('pid=22', 'pid=022') },
                    { $state.RowsText += 'partial' }, { $state.NativeRowsFault = 1 },
                    { $state.CreatedBinding.Born = 124 }, { $state.CreatedBinding.RootHash = 'f' * 64 },
                    { $state.CreatedBinding.Scoped.ExtensionSha256 = 'f' * 64 },
                    { $state.ScopedPublication.QualifiedModule += '.wrong' },
                    { $state.ExtensionSource.Sha256 = 'f' * 64 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        Invoke-HermesStartupScopedInput $state $writer Target
                        Assert-Control ((Complete-ControlScoped $state $writer).completed -eq 1)
                        . $change
                        $state.AcceptedUtf8Bytes = [long]8388608; $state.Bounds = 1
                        $report = New-HermesStartupReport $state $true $true
                        Assert-Control ($report.status -eq 7 -and $report.completed -eq 0 `
                            -and $report.parentUnchanged -eq 0 -and $report.cleanupComplete -eq 0 `
                            -and $report.firstFailureKind -eq 0 -and $report.armingVerified -eq 0 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                    } finally {
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            476 {
                foreach ($change in @(
                    { $state.ScopedInvocation.Source.Extension += '.wrong' },
                    { $state.ScopedInvocation.Source.QualifiedModule = 'exts' },
                    { $state.ScopedInvocation.Source.QualifiedModule += '.dll' },
                    { $state.ScopedInvocation.Source.QualifiedModule = 'C:\other\exts' },
                    { $state.ScopedInvocation.Source.ExtensionSha256 = 'f' * 64 },
                    { $state.ScopedInvocation.Source.ArmCommandSha256 = 'f' * 64 },
                    { $state.ScopedInvocation.Source = $null },
                    { $state.ScopedPublication.Remove('QualifiedModule') },
                    { $state.ExtensionSha256 = 'f' * 64 },
                    { $state.ExtensionSource.Path += '.wrong' },
                    { $state.ExtensionSource.Sha256 = 'f' * 64 },
                    { $state.ExtensionSource.Pin = $null },
                    { $state.ScopedInvocation.Born = 124 },
                    { $state.ScopedInvocation.EventPid = 23 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        . $change
                        Assert-Control (-not (Test-HermesStartupScopedAction $state Target) `
                            -and (Get-HermesStartupTypedFlags $state) -eq -1 -and -not $state.Acked `
                            -and $state.InputTasks.Count -eq 1)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            477 {
                foreach ($role in @('Arm', 'Target')) {
                    foreach ($fault in @('Changed', 'Closed')) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        try {
                            if ($role -ceq 'Target') {
                                Invoke-HermesStartupScopedInput $state $writer Arm
                                Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                            }
                            $bad = [IO.MemoryStream]::new([byte[]]@(1, 2, 3), $false)
                            $pins.Add($bad)
                            $state.ExtensionSource.Pin = $bad
                            if ($fault -ceq 'Closed') { $bad.Dispose() }
                            $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                            $refused = $false
                            try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                            Assert-Control ($refused -and -not $state.Acked `
                                -and $physical.Writes -eq $(if ($role -ceq 'Arm') { 0 } else { 1 }) `
                                -and $state.ArmAttempted -eq ($role -ceq 'Target'))
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            478 {
                $selectedRow = "Extension DLL chain:`n    " + $extension + ": image 10.0.1.1, API 1.0.0`n"
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Add-HermesStartupText $state $state.Out $selectedRow $true
                    Assert-Control ($null -eq $state.ScopedInvocation -and $null -eq $state.Arm -and -not $state.ArmAttempted)
                    Add-HermesStartupText $state $state.Out '1:000> ' $true
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback -Flags '' -Chain $selectedRow) $true
                    Assert-Control ($state.Observations.SdkSelectedExtensionChain -eq 2 `
                        -and -not (Test-HermesStartupScopedAction $state Target) `
                        -and (Get-HermesStartupTypedFlags $state) -eq -1 -and -not $state.Acked)
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            479 {
                foreach ($replace in @(
                    '!gflag +sls', '!exts.gflag +sls', '!C:\other\exts.gflag +sls',
                    ('!' + $extension + '.gflag +sls'),
                    ('!' + $extension.Substring(0, $extension.Length - 4) + '.gflag -sls'),
                    ('!"' + $extension.Substring(0, $extension.Length - 4).Replace('\', '\\') + '".gflag +sls')
                )) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        $sources = Get-TraceSelectedSources $extension
                        $qualified = '!' + $state.ScopedPublication.QualifiedModule + '.gflag +sls'
                        $bytes = [Text.Encoding]::ASCII.GetBytes($sources['startup-arm.cdb'].Replace($qualified, $replace))
                        $bad = [IO.MemoryStream]::new($bytes, $false)
                        $pins.Add($bad)
                        $hash = Get-ControlCommandHash $bad
                        $state.CommandPins['startup-arm.cdb'] = $bad
                        $state.ArmCommandSha256 = $hash
                        $state.ScopedPublication.ArmCommandSha256 = $hash
                        $state.CreatedBinding.Scoped.ArmCommandSha256 = $hash
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        $refused = $false
                        try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                        Assert-Control ($refused -and $physical.Writes -eq 0 -and -not $state.ArmAttempted)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            480 {
                foreach ($entry in @(
                    @{ Text = '.printf private-path S-1-5-21-secret HERMES_CDB'; Reason = 1 },
                    @{ Text = "Syntax error at '.printf private-path S-1-5-21-secret'"; Reason = 14 },
                    @{ Text = "1:000> Syntax error at '.printf private-path'"; Reason = 14 },
                    @{ Text = "unrecognised Syntax error at '.printf private-path'"; Reason = 1 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        $sources = Get-TraceSelectedSources $extension
                        Add-HermesStartupLine $state (($sources['startup-arm.cdb'] -split "`r`n")[0])
                        Assert-Control ($state.Protocol -eq 0 -and $state.ScopedPhase -eq 0)
                        Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=7 pid=22'
                        Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                        Add-HermesStartupLine $state $entry.Text
                        $first = $state.ScopedFault | ConvertTo-Json -Compress
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        Set-HermesStartupProtocolFault $state 6
                        $report = New-HermesStartupReport $state $false $false
                        $json = $report | ConvertTo-Json -Depth 4 -Compress
                        Assert-Control ($state.Protocol -eq 1 -and $state.Unknown -eq 0 `
                            -and $state.ScopedPhase -eq 1 -and $state.ScopedFault.Reason -eq $entry.Reason `
                            -and $state.ScopedFault.Phase -eq 1 -and $state.ScopedFault.Block -eq 7 `
                            -and $state.ScopedFault.Held -eq 1 -and $report.observations.scopedFirstReason -eq $entry.Reason `
                            -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first `
                            -and $json -notmatch 'private-path|secret|S-1-|HERMES_CDB|Syntax error|pid' `
                            -and $report.observations.SdkCommandErrors -eq 0 -and $report.completed -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            481 {
                $open = "HERMES_CDB v=4 kind=7 pid=22`n"
                $event = "Last event: 16.1: Create process 1:16`n"
                foreach ($entry in @(
                    @{ Text = "HERMES_CDB v=4 kind=7 pid=022`n"; Reason = 2; Phase = 0; Block = 0 },
                    @{ Text = "HERMES_CDB v=3 kind=7 pid=22`n"; Reason = 2; Phase = 0; Block = 0 },
                    @{ Text = "HERMES_CDB v=4 kind=9 pid=22`n"; Reason = 2; Phase = 0; Block = 0 },
                    @{ Text = "HERMES_CDB v=1 kind=0 pid=11`n"; Reason = 9; Phase = 0; Block = 0 },
                    @{ Text = $open + $open; Reason = 3; Phase = 1; Block = 7 },
                    @{ Text = "HERMES_CDB v=4 kind=7 pid=33`n"; Reason = 9; Phase = 1; Block = 0 },
                    @{ Text = "HERMES_CDB v=4 kind=8 pid=22`n"; Reason = 4; Phase = 2; Block = 0 },
                    @{ Text = $open + $event + "HERMES_CDB v=4 kind=8 pid=33`n"; Reason = 4; Phase = 3; Block = 7 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out $entry.Text $true
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $entry.Reason `
                            -and $state.ScopedFault.Phase -eq $entry.Phase -and $state.ScopedFault.Block -eq $entry.Block `
                            -and $state.ScopedFault.Held -eq 1 -and -not $state.Acked -and $null -eq $state.Arm)
                        $first = $state.ScopedFault | ConvertTo-Json -Compress
                        Add-HermesStartupScopedMarker $state 4 8 22
                        Set-HermesStartupProtocolFault $state 1
                        Assert-Control (($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first `
                            -and $state.ScopedPhase -eq $entry.Phase)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            482 {
                $open = "HERMES_CDB v=4 kind=7 pid=22`n"
                $event = "Last event: 16.1: Create process 1:16`n"
                foreach ($entry in @(
                    @{ Text = $event; Reason = 5; Phase = 0; Block = 0 },
                    @{ Text = $open + "Last event: private malformed display`n"; Reason = 5; Phase = 1; Block = 7 },
                    @{ Text = $open + $event + $event; Reason = 5; Phase = 1; Block = 7 },
                    @{ Text = $open + "Last event: 21.1: Create process 1:21`n"; Reason = 6; Phase = 1; Block = 7 },
                    @{ Text = $open + "Last event: 16.1: Create process 1:17`n"; Reason = 6; Phase = 1; Block = 7 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out $entry.Text $true
                        $first = $state.ScopedFault | ConvertTo-Json -Compress
                        Add-HermesStartupLine $state '.printf second private refusal'
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $entry.Reason `
                            -and $state.ScopedFault.Phase -eq $entry.Phase -and $state.ScopedFault.Block -eq $entry.Block `
                            -and $state.ScopedFault.Held -eq 1 -and -not $state.Acked `
                            -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            483 {
                $open = "HERMES_CDB v=4 kind=7 pid=22`n"
                $event = "Last event: 16.1: Create process 1:16`n"
                $typed = "HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002`n"
                foreach ($entry in @(
                    @{ Text = $open + $event + $typed.Replace('00000002', '0000002'); Reason = 7; Phase = 5; Block = 7 },
                    @{ Text = $typed; Reason = 9; Phase = 4; Block = 0 },
                    @{ Text = $open + $typed; Reason = 9; Phase = 5; Block = 7 },
                    @{ Text = $open + $event + $typed.Replace('pid=22', 'pid=33'); Reason = 9; Phase = 5; Block = 7 },
                    @{ Text = $open + $event + $typed + $typed; Reason = 8; Phase = 13; Block = 7 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out $entry.Text $true
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $entry.Reason `
                            -and $state.ScopedFault.Phase -eq $entry.Phase -and $state.ScopedFault.Block -eq $entry.Block `
                            -and $state.ScopedFault.Held -eq 1 -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                            -and -not (Test-HermesStartupScopedAction $state Target))
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
                foreach ($flags in @('', '00000000', '00000002')) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback -Flags $flags) $true
                        Assert-Control ($state.Protocol -eq 0 -and $state.ScopedFault.Reason -eq 0 `
                            -and $state.ScopedPhase -eq $(if ($flags -ceq '') { 3 } else { 15 }) `
                            -and (Test-HermesStartupScopedAction $state Target) -eq ($flags -ceq '00000002'))
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            484 {
                foreach ($reason in @(10, 11, 12, 13)) {
                    $state = New-ControlScopedCreated -WithoutCreate:($reason -eq 10)
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        switch ($reason) {
                            10 {
                                Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Current 33) + '1:000> ') $true
                                Invoke-HermesStartupScopedInput $state $writer Side
                                Add-HermesStartupText $state $state.Out (Get-ControlInventoryEvent -Current 33) $true
                            }
                            11 { Update-HermesStartupRows $state ($state.RowsText + "noncanonical private row`n") }
                            12 { Set-HermesStartupCreatedAcknowledged $state }
                            13 { Add-HermesStartupLine $state $failureLine }
                        }
                        $first = $state.ScopedFault | ConvertTo-Json -Compress
                        Set-HermesStartupProtocolFault $state 1
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $reason `
                            -and $state.ScopedFault.Phase -eq 0 -and $state.ScopedFault.Block -eq 0 `
                            -and $state.ScopedFault.Held -eq 1 `
                            -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            485 {
                foreach ($bounds in @(0, 1)) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=7 pid=22'
                        Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                        $first = $state.ScopedFault | ConvertTo-Json -Compress
                        $observations = $state.Observations | ConvertTo-Json -Compress
                        $outBefore = Get-ControlStreamSnapshot $state.Out
                        $errBefore = Get-ControlStreamSnapshot $state.Err
                        $state.Bounds = $bounds; $state.AcceptedUtf8Bytes = [long]8388608
                        Add-HermesStartupLine $state '.printf private-path S-1-5-21-secret'
                        Add-HermesStartupScopedMarker $state 4 8 22
                        Set-HermesStartupProtocolFault $state 14
                        Update-HermesStartupRows $state 'private malformed row'
                        Add-HermesStartupText $state $state.Err 'No export gflag found' $false
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.Protocol -eq 0 -and $state.ScopedPhase -eq 1 -and $report.status -eq 7 `
                            -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first `
                            -and ($state.Observations | ConvertTo-Json -Compress) -ceq $observations `
                            -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                            -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore)
                        foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                        foreach ($stream in @($state.Out, $state.Err)) {
                            $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                        }
                        $state.Protocol = 1; $state.ScopedPhase = 15
                        $state.ScopedFault = @{ Reason = 14; Phase = 15; Block = 7; Held = 5 }
                        $state.DebuggerKillRequested = 1
                        Set-HermesStartupDebuggerExit $state ([int]-1)
                        $report = New-HermesStartupReport $state $false $false
                        $json = $report | ConvertTo-Json -Depth 4 -Compress
                        Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                            -and $json -notmatch 'private-path|secret|S-1-|HERMES_CDB|pid|command-root|selected-sdk' `
                            -and $report.observations.scopedFirstReason -eq 14 -and $report.observations.scopedFirstPhase -eq 15 `
                            -and $report.observations.scopedFirstBlock -eq 7 -and $report.observations.scopedFirstHeld -eq 5 `
                            -and $report.debuggerKillRequested -eq 1 -and $report.debuggerExitValue -eq 4294967295 `
                            -and $report.status -eq 7 -and $report.completed -eq 0 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            486 {
                & {
                    $definition = $driverAst.Find({
                        param($n)
                        $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Stop-TraceDebugger'
                    }, $true)
                    . ([scriptblock]::Create($definition.Extent.Text))
                    foreach ($entry in @(
                        @{ Started = $false; Exited = $false; Fail = $false; Requested = 0 },
                        @{ Started = $true; Exited = $true; Fail = $false; Requested = 0 },
                        @{ Started = $true; Exited = $false; Fail = $false; Requested = 1 },
                        @{ Started = $true; Exited = $false; Fail = $true; Requested = 1 }
                    )) {
                        $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode ScopedCreateV1
                        $started = $entry.Started
                        $debugger = [pscustomobject]@{
                            HasExited = $entry.Exited; Fail = $entry.Fail; State = $state; Calls = 0; Waits = 0
                        }
                        $debugger | Add-Member ScriptMethod Kill {
                            Assert-Control ($this.State.DebuggerKillRequested -eq 1)
                            $this.Calls++
                            if ($this.Fail) { throw 'Synthetic owned Kill refusal.' }
                            $this.HasExited = $true
                        }
                        $debugger | Add-Member ScriptMethod WaitForExit {
                            param([int]$Milliseconds)
                            Assert-Control ($Milliseconds -eq 5000)
                            $this.Waits++
                            return $true
                        }
                        try {
                            $failed = $false
                            try { Stop-TraceDebugger } catch { $failed = $true }
                            $report = New-HermesStartupReport $state $false $false
                            Assert-Control ($failed -eq $entry.Fail -and $debugger.Calls -eq $entry.Requested `
                                -and $report.debuggerKillRequested -eq $entry.Requested -and $report.debuggerExitAvailable -eq 0 `
                                -and $debugger.Waits -eq [int]($entry.Started -and -not $entry.Fail))
                            foreach ($value in @([int]-1, [int]0, $null)) {
                                Set-HermesStartupDebuggerExit $state $value
                                $report = New-HermesStartupReport $state $true $true
                                Assert-Control ($report.debuggerKillRequested -eq $entry.Requested `
                                    -and $report.debuggerExitAvailable -eq [int]($null -ne $value) -and $report.completed -eq 0)
                                if ($value -eq -1) {
                                    Assert-Control ($report.status -eq 8 -and $report.debuggerExitValue -eq 4294967295)
                                }
                            }
                        } finally { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                    }
                }
            }
            487 {
                & {
                    # Four metadata programs stay frozen. The arm golden includes
                    # the fixed post-SDK evaluator; no filesystem action.
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    function Join-Path { param([string]$Path, [string]$ChildPath) return $Path + '\' + $ChildPath }
                    $root = 'C:\owned\command-root'
                    $sources = New-ScopedCreateCommands 'C:\selected-sdk\winxp\exts.dll'
                    Assert-Control ($sources.Count -eq 5)
                    foreach ($item in @(
                        @('startup-cpr.cdb', '00e4c315299607e64a5cc79d2aafd5a3020fa1686774c24f10d7c672319a8a7f'),
                        @('startup-ibp.cdb', 'c2c6509cc08fab26e327f2aea8abdd950662d2c17534440d6dcca0ab3c374fc7'),
                        @('startup-epr.cdb', 'b99a7cbc74fd6ddd7a03bafa9f021333f62be4455e3e643036a4c8b88601d10f'),
                        @('startup.cdb', '84aa3a9b7176d5b70468db52aa4d0da45c6b7f3cc338a8223a8963457c6c1b23'),
                        @('startup-arm.cdb', 'd5e9001ae7924fea383a2aa3d112ae18d1daa65959243de140a38704b014e55d')
                    )) {
                        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                            [Text.Encoding]::ASCII.GetBytes($sources[$item[0]]))).ToLowerInvariant()
                        Assert-Control ($hash -ceq $item[1] -and $sources[$item[0]] -cnotmatch '\$\{\$arg')
                    }
                }
                foreach ($leaf in @('command-root', 'command root')) {
                    $root = Join-Path $PSScriptRoot $leaf
                    $state = New-ControlScopedCreated
                    $before = @{}
                    foreach ($entry in $state.CommandPins.GetEnumerator()) {
                        $before[$entry.Key] = Get-ControlCommandHash $entry.Value
                    }
                    $source = $state.ScopedPublication.Clone()
                    $pipe = New-ControlPipe
                    $writer = [IO.StreamWriter]::new($pipe.Writer, [Text.Encoding]::ASCII)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        $bytes = [byte[]]::new(1024)
                        $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Invoke-ControlScopedPublisher
                        Assert-Control ($read.Wait(5000))
                        $count = $read.GetAwaiter().GetResult()
                        $actualInput = [Text.Encoding]::ASCII.GetString($bytes, 0, $count)
                        $expected = '$$<' + (Join-Path $root 'startup-arm.cdb') + "`n"
                        Assert-Control ($actualInput -ceq $expected -and $count -eq $expected.Length `
                            -and $actualInput -cmatch '\A\$\$<[^";\x00-\x1f\x7f-\uffff]+\n\z' `
                            -and $state.ArmWritten -and $state.InputTasks.Count -eq 1 `
                            -and -not $state.Acked -and -not $state.InputClosed)
                        $refused = $false
                        try { Invoke-HermesStartupScopedInput $state $writer Arm } catch { $refused = $true }
                        Assert-Control ($refused -and $state.InputTasks.Count -eq 1)
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Invoke-ControlScopedPublisher
                        Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 2 `
                            -and $bytes[0] -eq 103 -and $bytes[1] -eq 10)
                        $report = Complete-ControlScoped $state $writer
                        $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 4 `
                            -and [Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ceq "g`ng`n")
                        $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 0 `
                            -and $state.InputClosed -and $state.CleanupFault -eq 0 -and $state.InputTasks.Count -eq 4 `
                            -and $state.SideWrites -eq 2 -and $report.completed -eq 1 -and $report.armingVerified -eq 1 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                            -and $state.CommandPins.Count -eq 5 -and $state.ScopedInvocation.Source.Count -eq $source.Count)
                        foreach ($entry in $state.CommandPins.GetEnumerator()) {
                            Assert-Control ((Get-ControlCommandHash $entry.Value) -ceq $before[$entry.Key])
                        }
                        foreach ($name in $source.Keys) {
                            Assert-Control ($state.ScopedPublication[$name] -ceq $source[$name] `
                                -and $state.ScopedInvocation.Source[$name] -ceq $source[$name])
                        }
                    } finally {
                        $writer.Dispose(); $pipe.Writer.Dispose(); $pipe.Reader.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
                foreach ($suffix in @('"', ';g', "`n", [string][char]1)) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        $state.ScopedPublication.Root += $suffix
                        $state.CreatedBinding.Scoped.Root = $state.ScopedPublication.Root
                        $refused = $false
                        try { Invoke-HermesStartupScopedInput $state $writer Arm } catch { $refused = $true }
                        Assert-Control ($refused -and -not (Test-HermesStartupScopedAction $state Arm) `
                            -and $physical.Writes -eq 0 -and -not $state.ArmAttempted -and $state.InputTasks.Count -eq 0)
                    } finally {
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            488 {
                $state = New-ControlScopedCreated
                $sources = Get-TraceSelectedSources $extension
                $printf = ($sources['startup-arm.cdb'] -split "`r`n")[0]
                $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                foreach ($entry in @(
                    @{ Text = 'private .printf text'; Mask = 0; Reason = 1 },
                    @{ Text = '1:000> private .printf text'; Mask = 1; Reason = 1 },
                    @{ Text = '.catch { .printf "private" }'; Mask = 2; Reason = 1 },
                    @{ Text = '  .if (1) { .printf "private" }'; Mask = 4; Reason = 1 },
                    @{ Text = '.printf "private"'; Mask = 8; Reason = 1 },
                    @{ Text = "Syntax error at '.printf private'"; Mask = 16; Reason = 14 },
                    @{ Text = "prefix Syntax error at '.printf private'"; Mask = 16; Reason = 1 },
                    @{ Text = '.printf "No export gflag found"'; Mask = 40; Reason = 1 },
                    @{ Text = '.catch { .printf "Couldn''t resolve error at private" }'; Mask = 66; Reason = 1 },
                    @{ Text = '.printf "Type ntdll!_PEB not found"'; Mask = 72; Reason = 1 },
                    @{ Text = '1:000> .Catch { .printf "private" }'; Mask = 1; Reason = 1 },
                    @{ Text = '.catcher { .printf "private" }'; Mask = 0; Reason = 1 },
                    @{ Text = '.printf "no export gflag found"'; Mask = 8; Reason = 1 },
                    @{ Text = "syntax error at '.printf private'"; Mask = 0; Reason = 1 },
                    @{ Text = (" `t" + $printf.Replace(' ', "`t `t") + '  '); Mask = 136; Reason = 1 },
                    @{ Text = ("1:abc> `t" + $printf + '  '); Mask = 137; Reason = 1 },
                    @{ Text = $printf.Replace('HERMES_CDB', 'hermes_cdb'); Mask = 8; Reason = 1 },
                    @{ Text = $printf.Replace('\n', '\N'); Mask = 8; Reason = 1 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=7 pid=22'
                        Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                        Add-HermesStartupLine $state $entry.Text
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $entry.Reason `
                            -and $state.ScopedFault.Echo -eq $entry.Mask -and $state.ScopedFault.Phase -eq 1 `
                            -and $state.ScopedFault.Block -eq 7 -and $state.ScopedFault.Held -eq 1 `
                            -and $report.observations.scopedFirstEchoMask -eq $entry.Mask `
                            -and -not (Test-HermesStartupScopedAction $state Target) -and -not $state.Acked `
                            -and (Get-HermesStartupTypedFlags $state) -eq -1 -and $report.completed -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            489 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                $module = Get-Module Native-StartupTrace
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=7 pid=22'
                    Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                    Add-HermesStartupLine $state '.catch { .printf "Couldn''t resolve error at private-path S-1-5-21-secret" }'
                    $first = $state.ScopedFault | ConvertTo-Json -Compress
                    Assert-Control ($state.ScopedFault.Echo -eq 66)
                    Add-HermesStartupLine $state '1:000> .printf "No export gflag found"'
                    Set-HermesStartupProtocolFault $state 14 255
                    Assert-Control (($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first)
                    $state.Bounds = 1; $state.AcceptedUtf8Bytes = [long]8388608
                    $outBefore = Get-ControlStreamSnapshot $state.Out
                    $errBefore = Get-ControlStreamSnapshot $state.Err
                    & $module {
                        $script:controlOriginalEchoMask = (Get-Item Function:Get-HermesStartupScopedEchoMask).ScriptBlock
                        $script:controlReportEchoMaskCalls = 0
                        Set-Item Function:script:Get-HermesStartupScopedEchoMask {
                            $script:controlReportEchoMaskCalls++
                            throw 'Reporting must retain the earlier private lexical snapshot.'
                        }
                    }
                    Add-HermesStartupLine $state '1:000> .printf "No export gflag found"'
                    Add-HermesStartupText $state $state.Err '.printf private after-bound' $false
                    Set-HermesStartupProtocolFault $state 14 255
                    $state.ScopedEchoes.Clear()
                    $report = New-HermesStartupReport $state $false $false
                    $json = $report | ConvertTo-Json -Depth 4 -Compress
                    Assert-Control ($report.observations.scopedFirstEchoMask -eq 66 `
                        -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first `
                        -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                        -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore `
                        -and $json -notmatch 'private-path|secret|S-1-|HERMES_CDB|printf|resolve|pid' `
                        -and (& $module { $script:controlReportEchoMaskCalls }) -eq 0 `
                        -and $report.status -eq 7 -and $report.completed -eq 0)
                    foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                    foreach ($stream in @($state.Out, $state.Err)) {
                        $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                    }
                    $state.ScopedFault = @{ Reason = 14; Phase = 15; Block = 7; Held = 5; Echo = 255 }
                    $state.ScopedPhase = 15; $state.DebuggerKillRequested = 1
                    Set-HermesStartupDebuggerExit $state ([int]-1)
                    $report = New-HermesStartupReport $state $false $false
                    $json = $report | ConvertTo-Json -Depth 4 -Compress
                    Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                        -and $report.observations.scopedFirstEchoMask -eq 255 `
                        -and $report.debuggerKillRequested -eq 1 -and $report.debuggerExitValue -eq 4294967295 `
                        -and $report.status -eq 7 -and $report.completed -eq 0 `
                        -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                } finally {
                    & $module {
                        if (Test-Path Variable:script:controlOriginalEchoMask) {
                            Set-Item Function:script:Get-HermesStartupScopedEchoMask $script:controlOriginalEchoMask
                            Remove-Variable controlOriginalEchoMask, controlReportEchoMaskCalls -Scope Script
                        }
                    }
                    Close-HermesStartupScopedInput $state $writer
                }
            }
            490 {
                foreach ($diagnostic in @('', "No export gflag found`n", "Syntax error at 'fixed SDK stage'`n")) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        Invoke-ControlScopedPublisher
                        Add-HermesStartupText $state $state.Err (
                            "HERMES_ARM_STAGE v=1 stage=1`nHERMES_ARM_STAGE v=1 stage=2`n") $false
                        Assert-Control ($state.ScopedStages -eq 0 -and $state.Protocol -eq 0)
                        $text = (Get-ControlScopedReadback).Replace("HERMES_ARM_STAGE v=1 stage=1`n", $diagnostic)
                        Add-HermesStartupText $state $state.Out $text $true
                        Invoke-ControlScopedPublisher
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.Protocol -eq 0 -and $state.Unknown -eq 1 -and $state.ScopedFault.Reason -eq 0 `
                            -and $state.Arm.Flags -eq 2 -and $state.Arm.Stages -eq 2 -and $state.ScopedStages -eq 2 `
                            -and $report.observations.scopedStages -eq 2 -and $physical.Writes -eq 1 `
                            -and -not (Test-HermesStartupScopedAction $state Target) -and -not (Test-HermesStartupArmed $state) `
                            -and -not $state.Acked -and -not $state.ContinueAttempted -and $state.InputTasks.Count -eq 1 `
                            -and $report.armingVerified -eq 0 -and $report.completed -eq 0 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                        Assert-Control ($state.Observations.SdkCommandErrors -eq $(if ($diagnostic.StartsWith('No export')) { 2 } else { 0 }))
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            491 {
                foreach ($failure in @('', "Unable to read PEB`n", "Syntax error at '.printf private query'`n")) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        $text = (Get-ControlScopedReadback -Flags '').Replace(
                            "HERMES_ARM_STAGE v=1 stage=2`n", $failure)
                        Add-HermesStartupText $state $state.Out $text $true
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.ScopedStages -eq 1 -and $report.observations.scopedStages -eq 1 `
                            -and $state.Protocol -eq [int]($failure.Contains('.printf')) `
                            -and -not (Test-HermesStartupScopedAction $state Target) -and -not $state.Acked `
                            -and (Get-HermesStartupTypedFlags $state) -eq -1 -and $physical.Writes -eq 1 `
                            -and $report.armingVerified -eq 0 -and $report.completed -eq 0 `
                            -and ($report | ConvertTo-Json -Depth 4 -Compress) -notmatch 'private|query|printf|HERMES_ARM_STAGE')
                        if ($state.Protocol -eq 0) {
                            Assert-Control ($state.Unknown -eq 1 -and $state.Arm.Stages -eq 1 -and $state.Arm.Flags -eq -1)
                        } else {
                            Assert-Control ($null -eq $state.Arm -and $state.Inventory.Block.Stages -eq 1 `
                                -and $state.ScopedFault.Reason -eq 14 -and $state.ScopedFault.Phase -eq 1)
                        }
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out ((Get-ControlScopedReadback).Replace(
                        "HERMES_ARM_STAGE v=1 stage=2`n", '')) $true
                    Assert-Control ($state.Protocol -eq 0 -and $state.Unknown -eq 1 -and $state.Arm.Flags -eq 2 `
                        -and $state.ScopedStages -eq 1 -and -not (Test-HermesStartupScopedAction $state Target))
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            492 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback -Flags '') $true
                    $report = New-HermesStartupReport $state $false $false
                    Assert-Control ($state.Protocol -eq 0 -and $state.ScopedFault.Reason -eq 0 -and $state.Unknown -eq 1 `
                        -and $state.ScopedStages -eq 3 -and $state.Arm.Stages -eq 3 -and $state.Arm.Flags -eq -1 `
                        -and $report.observations.scopedStages -eq 3 -and (Get-HermesStartupTypedFlags $state) -eq -1 `
                        -and -not (Test-HermesStartupScopedAction $state Target) -and $report.armingVerified -eq 0 `
                        -and -not $state.Acked -and $report.completed -eq 0)
                } finally { Close-HermesStartupScopedInput $state $writer }
            }
            493 {
                $state = New-ControlScopedCreated
                $physical = [StartupControlWriteStream]::new(0)
                $writer = [IO.StreamWriter]::new($physical)
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (
                        "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n" +
                        "HERMES_ARM_STAGE v=1 stage=1`n") $true
                    Assert-Control ($state.ScopedStages -eq 1 -and -not (Test-HermesStartupScopedAction $state Target))
                    Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                    Assert-Control ($state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq 2 `
                        -and -not (Test-HermesStartupScopedAction $state Target))
                    Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=2'
                    Assert-Control ($state.ScopedStages -eq 3 -and -not (Test-HermesStartupScopedAction $state Target))
                    Add-HermesStartupText $state $state.Out "HERMES_CDB v=4 kind=8 pid=22`n1:000> " $true
                    Assert-Control ((Test-HermesStartupScopedAction $state Target) -and -not (Test-HermesStartupArmed $state))
                    Invoke-HermesStartupScopedInput $state $writer Target
                    Assert-Control ((Test-HermesStartupArmed $state) -and $state.ArmedBinding.Flags -eq 2)
                    $report = Complete-ControlScoped $state $writer
                    Assert-Control ($report.completed -eq 1 -and $report.armingVerified -eq 1 `
                        -and $report.observations.scopedStages -eq 3 -and $report.observations.armInvocations -eq 1 `
                        -and $report.observations.targetContinuations -eq 1 -and $physical.Writes -eq 4 `
                        -and $state.InputTasks.Count -eq 4 -and $state.InputClosed -and $physical.Closed `
                        -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                } finally { $writer.Dispose() }
            }
            494 {
                foreach ($forgery in @(
                    ' HERMES_ARM_STAGE v=1 stage=1', 'HERMES_ARM_STAGE v=1 stage=1 ',
                    '1:000> HERMES_ARM_STAGE v=1 stage=1', '.echo HERMES_ARM_STAGE v=1 stage=1',
                    'HERMES_ARM_STAGE v=2 stage=1', 'HERMES_ARM_STAGE v=1 stage=3',
                    'HERMES_ARM_STAGE v=1 stage=01', 'HERMES_ARM_STAGE v=1 stage=1 pid=22',
                    'hermes_arm_stage v=1 stage=1', 'Hermes_Arm_Stage v=1 stage=2',
                    "Syntax error at '.echo HERMES_ARM_STAGE v=1 stage=1'",
                    'HERMES_ARM_STAGE v=1 stage=1; HERMES_ARM_STAGE v=1 stage=2'
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n" +
                            $forgery + "`n") $true
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 15 `
                            -and $state.ScopedStages -eq 0 -and $state.Inventory.Block.Stages -eq 0 `
                            -and $state.ScopedFault.Block -eq 7 -and -not (Test-HermesStartupScopedAction $state Target))
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            495 {
                $sdk = "HERMES_ARM_STAGE v=1 stage=1`n"
                $query = "HERMES_ARM_STAGE v=1 stage=2`n"
                $typed = "HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002`n"
                foreach ($sequence in @(
                    @{ Text = $sdk + $sdk; Mask = 1 }, @{ Text = $query + $query; Mask = 2 },
                    @{ Text = $query + $sdk; Mask = 2 }, @{ Text = $typed + $sdk; Mask = 0 },
                    @{ Text = $query + $typed; Mask = 2 }, @{ Text = $sdk + $query + $typed; Mask = 3 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n" + $sequence.Text) $true
                        $first = $state.ScopedFault | ConvertTo-Json -Compress
                        Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=1'
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 15 `
                            -and $state.ScopedStages -eq $sequence.Mask -and $null -eq $state.Arm -and -not $state.Acked `
                            -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first `
                            -and -not (Test-HermesStartupScopedAction $state Target))
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            496 {
                foreach ($change in @(
                    { $state.ScopedInvocation.Source.Root += '-wrong' },
                    { $state.ScopedInvocation.Source.ArmCommandSha256 = 'f' * 64 },
                    { $state.ScopedInvocation.Source.Mode = 'Inventory' },
                    { $state.ScopedInvocation.Pid = 33 }, { $state.ScopedInvocation.EventPid = 33 },
                    { $state.ScopedInvocation.Born = 124 }, { $state.ScopedInvocation.Helper = 12 },
                    { $state.Target.born = 124 }, { $state.Target.image = 0 }, { $state.Target.context = 0 },
                    { $state.TargetText = $readyV2.Replace('born=123', 'born=124') },
                    { $state.Inventory.Root = 12 }, { $state.Initial = 12 }, { $state.NativeRowsFault = 1 },
                    { $state.CreatedAcked = $false }, { $state.CreatedBinding.Scoped.Mode = 'Inventory' },
                    { $state.Held.Kind = 5 }, { $state.Held.EventPid = 33 }, { $state.Held.Released = $true },
                    { $state.Held.Valid = $false }, { $state.Created.EventPid = 33 },
                    { $state.Inventory.Block.Pid = 33 }, { $state.Inventory.Block.EventPid = 33 },
                    { $state.Inventory.Block.Kind = 5 }, { $state.Inventory.Block.Event = 0 },
                    { $state.ArmWritten = $false }, { $state.Acked = $true }, { $state.ContinueAttempted = $true },
                    { $state.Exits['22'] = 37 }, { $state.Exits['11'] = 0 },
                    { $state.Final = @{ stage = 3 } }, { Set-HermesStartupDebuggerExit $state ([int]0) },
                    { $state.InputClosed = $true }, { $state.Out.End = $true }, { $state.Out.Faulted = $true }
                )) {
                    foreach ($stage in @(1, 2)) {
                        $state = New-ControlScopedCreated
                        $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                        try {
                            Invoke-HermesStartupScopedInput $state $writer Arm
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                            . $change
                            Add-HermesStartupLine $state ("HERMES_ARM_STAGE v=1 stage=$stage")
                            Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 15 `
                                -and $state.ScopedStages -eq 0 -and -not (Test-HermesStartupScopedAction $state Target))
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            497 {
                foreach ($replacement in @(
                    @('; .echo HERMES_ARM_STAGE v=1 stage=1; }', '; }; .echo HERMES_ARM_STAGE v=1 stage=1'),
                    @('stage=1; }', 'stage=2; }'),
                    @('; .echo HERMES_ARM_STAGE v=1 stage=1', ''),
                    @('; .echo HERMES_ARM_STAGE v=1 stage=2', ''),
                    @('HERMES_ARM_STAGE v=1 stage=2', 'HERMES_ARM_STAGE v=1 stage=1')
                )) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        $text = (Get-TraceSelectedSources $extension)['startup-arm.cdb']
                        $bytes = [Text.Encoding]::ASCII.GetBytes($text.Replace($replacement[0], $replacement[1]))
                        $pin = [IO.MemoryStream]::new($bytes, $false)
                        $pins.Add($pin)
                        $hash = Get-ControlCommandHash $pin
                        Assert-Control ($hash -cne $state.ArmCommandSha256)
                        $state.CommandPins['startup-arm.cdb'] = $pin
                        $state.ArmCommandSha256 = $hash
                        $state.ScopedPublication.ArmCommandSha256 = $hash
                        $state.CreatedBinding.Scoped.ArmCommandSha256 = $hash
                        $refused = $false
                        try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                        Assert-Control ($refused -and $physical.Writes -eq 0 -and -not $state.ArmAttempted `
                            -and $state.ScopedStages -eq 0 -and $state.InputTasks.Count -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            498 {
                foreach ($errorEcho in @($false, $true)) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        $source = (Get-TraceSelectedSources $extension)['startup-arm.cdb']
                        foreach ($line in ($source -split "`r`n" | Where-Object { $_.Contains('HERMES_ARM_STAGE') })) {
                            foreach ($prefix in @('', '1:000> ')) { Add-HermesStartupLine $state ($prefix + $line) }
                        }
                        Add-HermesStartupLine $state (($source -replace '\r?\n', ';').TrimEnd(';'))
                        Assert-Control ($state.Protocol -eq 0 -and $state.ScopedStages -eq 0 -and $null -eq $state.Arm)
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                        $sdkSource = ($source -split "`r`n")[4]
                        Add-HermesStartupLine $state $(if ($errorEcho) { "Syntax error at '$sdkSource'" } else { $sdkSource })
                        Assert-Control ($state.ScopedStages -eq 0 -and $state.Protocol -eq [int]$errorEcho `
                            -and $state.Inventory.Block.Stages -eq 0 -and -not (Test-HermesStartupScopedAction $state Target))
                        if ($errorEcho) { Assert-Control ($state.ScopedFault.Reason -eq 15) }
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            499 {
                foreach ($bounds in @(0, 1)) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    $module = Get-Module Native-StartupTrace
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n" +
                            "HERMES_ARM_STAGE v=1 stage=1`n") $true
                        $state.Bounds = $bounds; $state.AcceptedUtf8Bytes = [long]8388608
                        $outBefore = Get-ControlStreamSnapshot $state.Out
                        $errBefore = Get-ControlStreamSnapshot $state.Err
                        $outHash = [Convert]::ToHexString($state.Out.Hash.GetCurrentHash())
                        $errHash = [Convert]::ToHexString($state.Err.Hash.GetCurrentHash())
                        & $module {
                            $script:controlOriginalArmFrame = (Get-Item Function:Test-HermesStartupScopedArmFrame).ScriptBlock
                            Set-Item Function:script:Test-HermesStartupScopedArmFrame {
                                throw 'No stage frame may be examined after the collection bound.'
                            }
                        }
                        Add-HermesStartupScopedStage $state 2
                        Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=2'
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.ScopedStages -eq 1 -and $state.Inventory.Block.Stages -eq 1 `
                            -and $state.Protocol -eq 0 -and $state.ScopedPhase -eq 1 -and $report.observations.scopedStages -eq 1 `
                            -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                            -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore `
                            -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $outHash `
                            -and [Convert]::ToHexString($state.Err.Hash.GetCurrentHash()) -ceq $errHash)
                        foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                        foreach ($stream in @($state.Out, $state.Err)) {
                            $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                        }
                        $state.ScopedStages = 3; $state.ScopedPhase = 15
                        $state.ScopedFault = @{ Reason = 15; Phase = 15; Block = 7; Held = 5; Echo = 255 }
                        $state.SideWrites = 48; $state.DebuggerKillRequested = 1
                        foreach ($subject in 100..114) {
                            $state.Inventory.Subjects[[string]$subject] = @{ Create = 1; Initial = 1; Exit = 1; ExitCode = 0 }
                        }
                        while ($state.InputTasks.Count -lt 50) { $state.InputTasks.Add([Threading.Tasks.Task]::CompletedTask) }
                        Set-HermesStartupDebuggerExit $state ([int]-1)
                        $report = New-HermesStartupReport $state $false $false
                        $json = $report | ConvertTo-Json -Depth 4 -Compress
                        Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                            -and $report.observations.scopedStages -eq 3 -and $report.observations.scopedFirstReason -eq 15 `
                            -and $json -notmatch 'HERMES_ARM_STAGE|private|command-root|selected-sdk|pid' `
                            -and $report.status -eq 7 -and $report.completed -eq 0 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                    } finally {
                        & $module {
                            if (Test-Path Variable:script:controlOriginalArmFrame) {
                                Set-Item Function:script:Test-HermesStartupScopedArmFrame $script:controlOriginalArmFrame
                                Remove-Variable controlOriginalArmFrame -Scope Script
                            }
                        }
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            500 {
                foreach ($when in @('BeforeInvocation', 'BeforeOpen', 'BeforeEvent', 'AfterClose', 'AfterTargetExit', 'AfterHelperExit')) {
                    foreach ($stage in @(1, 2)) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        try {
                            if ($when -cne 'BeforeInvocation') { Invoke-HermesStartupScopedInput $state $writer Arm }
                            if ($when -ceq 'BeforeEvent') { Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=7 pid=22' }
                            if ($when.StartsWith('After')) {
                                Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                                if ($when -cne 'AfterClose') {
                                    Invoke-HermesStartupScopedInput $state $writer Target
                                    $subject = if ($when -ceq 'AfterTargetExit') { 22 } else { 11 }
                                    Add-HermesStartupText $state $state.Out ((Get-ControlInventoryEvent -Kind Exit -Current $subject) +
                                        '1:000> ') $true
                                }
                            }
                            $writes = $physical.Writes
                            Add-HermesStartupLine $state ("HERMES_ARM_STAGE v=1 stage=$stage")
                            $report = New-HermesStartupReport $state $false $false
                            Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 15 `
                                -and $state.ScopedStages -eq $(if ($when.StartsWith('After')) { 3 } else { 0 }) `
                                -and $physical.Writes -eq $writes -and -not (Test-HermesStartupScopedAction $state Target) `
                                -and -not (Test-HermesStartupScopedAction $state Arm) -and $report.completed -eq 0)
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            501 {
                $sdk = 'HERMES_ARM_STAGE v=1 stage=1'
                $query = 'HERMES_ARM_STAGE v=1 stage=2'
                foreach ($entry in @(
                    @{ Text = $sdk + ' '; Shape = 257; Original = 15 },
                    @{ Text = " `t" + $sdk + "`t"; Shape = 256; Original = 15 },
                    @{ Text = '1:000> ' + $sdk; Shape = 259; Original = 15 },
                    @{ Text = $query + "`t"; Shape = 513; Original = 15 },
                    @{ Text = 'HERMES_ARM_STAGE v=1 stage=3'; Shape = 1; Original = 15 },
                    @{ Text = 'hermes_arm_stage v=1 stage=1'; Shape = 0; Original = 15 },
                    @{ Text = '.echo ' + $sdk; Shape = 4; Original = 15 },
                    @{ Text = "Syntax error in '.echo $sdk'"; Shape = 12; Original = 15 },
                    @{ Text = "Syntax error at '.echo $sdk'"; Shape = 20; Original = 15 },
                    @{ Text = "1:000> Syntax error in '.echo $sdk'"; Shape = 14; Original = 15 },
                    @{ Text = "  ^ Syntax error at '.echo $sdk'"; Shape = 52; Original = 15 },
                    @{ Text = "^ Numeric expression missing from '.echo $sdk'"; Shape = 100; Original = 15 },
                    @{ Text = "Numeric expression missing from '.echo $sdk'"; Shape = 68; Original = 15 },
                    @{ Text = "Couldn't resolve error at '.echo $sdk'"; Shape = 132; Original = 15 },
                    @{ Text = 'Type ntdll!_PEB not found; .echo ' + $sdk; Shape = 132; Original = 15 },
                    @{ Text = "Syntax error in '.echo $sdk"; Shape = 4; Original = 15 },
                    @{ Text = "syntax error in '.echo $sdk'"; Shape = 4; Original = 15 },
                    @{ Text = "Syntax error IN '.echo $sdk'"; Shape = 4; Original = 15 },
                    @{ Text = '.Echo ' + $sdk; Shape = 0; Original = 15 },
                    @{ Text = $sdk + [char]0xa0; Shape = 1; Original = 15 },
                    @{ Text = $sdk + [char]11; Shape = 1; Original = 15 },
                    @{ Text = "Syntax error at '.printf private; .echo $sdk'"; Shape = 20; Original = 14 },
                    @{ Text = "Syntax error in '.printf private; .echo $sdk'"; Shape = 12; Original = 1 }
                )) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                        Add-HermesStartupLine $state $entry.Text
                        $report = New-HermesStartupReport $state $false $false
                        $json = $report | ConvertTo-Json -Depth 4 -Compress
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $entry.Original `
                            -and $state.ScopedStageFault.Reason -eq 1 -and $state.ScopedStageFault.Shape -eq $entry.Shape `
                            -and $report.observations.scopedStageFirstReason -eq 1 `
                            -and $report.observations.scopedStageFirstShape -eq $entry.Shape `
                            -and $state.ScopedStages -eq 0 -and $physical.Writes -eq 1 -and -not $state.Acked `
                            -and -not (Test-HermesStartupScopedAction $state Target) -and $report.completed -eq 0 `
                            -and $json -notmatch 'HERMES_ARM_STAGE|private|Syntax error|Numeric expression|\.echo')
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            502 {
                foreach ($change in @(
                    { $state.ArmWritten = $false }, { $state.Held.Released = $true },
                    { $state.ScopedInvocation.Source.ArmCommandSha256 = 'f' * 64 },
                    { $state.NativeRowsFault = 1 }, { $state.Inventory.Block.Event = 0 }
                )) {
                    foreach ($stage in @(1, 2)) {
                        $state = New-ControlScopedCreated
                        $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                        try {
                            Invoke-HermesStartupScopedInput $state $writer Arm
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                            . $change
                            Assert-Control (-not (Test-HermesStartupScopedArmFrame $state))
                            Add-HermesStartupLine $state ("HERMES_ARM_STAGE v=1 stage=$stage")
                            $report = New-HermesStartupReport $state $false $false
                            Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 15 `
                                -and $state.ScopedStageFault.Reason -eq 2 -and $state.ScopedStages -eq 0 `
                                -and $report.observations.scopedStageFirstReason -eq 2 `
                                -and $report.observations.scopedStageFirstShape -eq $(if ($stage -eq 1) { 257 } else { 513 }) `
                                -and -not (Test-HermesStartupScopedAction $state Target) -and -not $state.Acked)
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            503 {
                $sdk = 'HERMES_ARM_STAGE v=1 stage=1'
                $query = 'HERMES_ARM_STAGE v=1 stage=2'
                $typed = 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                foreach ($entry in @(
                    @{ Before = @(); Change = { $state.ScopedStages = 1 }; Last = $query; Reason = 3; Shape = 513 },
                    @{ Before = @($sdk); Change = {}; Last = $sdk; Reason = 4; Shape = 257 },
                    @{ Before = @($query); Change = {}; Last = $query; Reason = 4; Shape = 513 },
                    @{ Before = @($query); Change = {}; Last = $sdk; Reason = 5; Shape = 257 },
                    @{ Before = @($typed); Change = {}; Last = $sdk; Reason = 5; Shape = 257 },
                    @{ Before = @($sdk, $query); Change = {}; Last = $typed; Reason = 5; Shape = 0 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                        foreach ($line in $entry.Before) { Add-HermesStartupLine $state $line }
                        . $entry.Change
                        Add-HermesStartupLine $state $entry.Last
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 15 `
                            -and $report.observations.scopedStageFirstReason -eq $entry.Reason `
                            -and $report.observations.scopedStageFirstShape -eq $entry.Shape `
                            -and $null -eq $state.Arm -and -not $state.Acked -and $state.InputTasks.Count -eq 1 `
                            -and -not (Test-HermesStartupScopedAction $state Target))
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            504 {
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                $module = Get-Module Native-StartupTrace
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (
                        "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                    Add-HermesStartupLine $state "Syntax error at '.echo HERMES_ARM_STAGE v=1 stage=1 private-path S-1-5-21-secret'"
                    $first = $state.ScopedStageFault | ConvertTo-Json -Compress
                    $protocolFirst = $state.ScopedFault | ConvertTo-Json -Compress
                    & $module {
                        $script:controlOriginalStageShape = (Get-Item Function:Get-HermesStartupScopedStageShape).ScriptBlock
                        Set-Item Function:script:Get-HermesStartupScopedStageShape {
                            throw 'A frozen first stage refusal must not be reclassified.'
                        }
                    }
                    Set-HermesStartupScopedStageRefusal $state 5 'HERMES_ARM_STAGE v=1 stage=2'
                    Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=2'
                    Add-HermesStartupScopedStage $state 1 'HERMES_ARM_STAGE v=1 stage=1'
                    Set-HermesStartupProtocolFault $state 1
                    $state.ScopedEchoes.Clear()
                    $report = New-HermesStartupReport $state $false $false
                    $json = $report | ConvertTo-Json -Depth 4 -Compress
                    Assert-Control (($state.ScopedStageFault | ConvertTo-Json -Compress) -ceq $first `
                        -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $protocolFirst `
                        -and $report.observations.scopedStageFirstReason -eq 1 `
                        -and $report.observations.scopedStageFirstShape -eq 20 `
                        -and $report.observations.scopedFirstReason -eq 15 -and $state.ScopedStages -eq 0 `
                        -and $json -notmatch 'private-path|secret|S-1-|HERMES_ARM_STAGE|Syntax error|\.echo' `
                        -and $report.completed -eq 0 -and $report.armingVerified -eq 0)
                } finally {
                    & $module {
                        if (Test-Path Variable:script:controlOriginalStageShape) {
                            Set-Item Function:script:Get-HermesStartupScopedStageShape $script:controlOriginalStageShape
                            Remove-Variable controlOriginalStageShape -Scope Script
                        }
                    }
                    Close-HermesStartupScopedInput $state $writer
                }
            }
            505 {
                foreach ($outcome in @('Full', 'SdkMissing', 'QueryMissing', 'FieldUnavailable')) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        $source = (Get-TraceSelectedSources $extension)['startup-arm.cdb']
                        foreach ($line in ($source -split "`r`n" | Where-Object { $_.Contains('HERMES_ARM_STAGE') })) {
                            Add-HermesStartupLine $state ('1:000> ' + $line)
                        }
                        Add-HermesStartupText $state $state.Err "Syntax error at '.echo HERMES_ARM_STAGE v=1 stage=1'`n" $false
                        $text = Get-ControlScopedReadback -Flags $(if ($outcome -ceq 'FieldUnavailable') { '' } else { '00000002' })
                        if ($outcome -ceq 'SdkMissing') { $text = $text.Replace("HERMES_ARM_STAGE v=1 stage=1`n", '') }
                        if ($outcome -ceq 'QueryMissing') { $text = $text.Replace("HERMES_ARM_STAGE v=1 stage=2`n", '') }
                        Add-HermesStartupText $state $state.Out $text $true
                        Assert-Control ($state.Protocol -eq 0 -and $state.ScopedStageFault.Reason -eq 0 `
                            -and $state.ScopedStageFault.Shape -eq 0 `
                            -and (Test-HermesStartupScopedAction $state Target) -eq ($outcome -ceq 'Full'))
                        if ($outcome -ceq 'Full') {
                            Invoke-HermesStartupScopedInput $state $writer Target
                            $report = Complete-ControlScoped $state $writer
                            Assert-Control ($report.completed -eq 1 -and $report.armingVerified -eq 1)
                        } else { $report = New-HermesStartupReport $state $false $false }
                        Assert-Control ($report.observations.scopedStageFirstReason -eq 0 `
                            -and $report.observations.scopedStageFirstShape -eq 0 `
                            -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            506 {
                foreach ($captured in @($false, $true)) {
                    foreach ($bounds in @(0, 1)) {
                        $state = New-ControlScopedCreated
                        $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                        $module = Get-Module Native-StartupTrace
                        try {
                            Invoke-HermesStartupScopedInput $state $writer Arm
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                            if ($captured) { Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=1 ' }
                            $first = $state.ScopedStageFault | ConvertTo-Json -Compress
                            $protocolFirst = $state.ScopedFault | ConvertTo-Json -Compress
                            $state.Bounds = $bounds; $state.AcceptedUtf8Bytes = [long]8388608
                            $outBefore = Get-ControlStreamSnapshot $state.Out
                            $errBefore = Get-ControlStreamSnapshot $state.Err
                            $outHash = [Convert]::ToHexString($state.Out.Hash.GetCurrentHash())
                            $errHash = [Convert]::ToHexString($state.Err.Hash.GetCurrentHash())
                            Assert-Control ((Get-HermesStartupScopedStageShape $state 'HERMES_ARM_STAGE v=1 stage=1 ') -eq 0)
                            & $module {
                                $script:controlOriginalStageShape = (Get-Item Function:Get-HermesStartupScopedStageShape).ScriptBlock
                                $script:controlOriginalArmFrame = (Get-Item Function:Test-HermesStartupScopedArmFrame).ScriptBlock
                                Set-Item Function:script:Get-HermesStartupScopedStageShape { throw 'No post-bound stage lexing.' }
                                Set-Item Function:script:Test-HermesStartupScopedArmFrame { throw 'No post-bound frame parsing.' }
                            }
                            Set-HermesStartupScopedStageRefusal $state 2 'HERMES_ARM_STAGE v=1 stage=2'
                            Add-HermesStartupScopedStage $state 2 'HERMES_ARM_STAGE v=1 stage=2'
                            Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=1 '
                            Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                            Add-HermesStartupText $state $state.Err '.echo HERMES_ARM_STAGE private after-bound' $false
                            $report = New-HermesStartupReport $state $false $false
                            Assert-Control (($state.ScopedStageFault | ConvertTo-Json -Compress) -ceq $first `
                                -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $protocolFirst `
                                -and $report.observations.scopedStageFirstReason -eq [int]$captured `
                                -and $report.observations.scopedStageFirstShape -eq $(if ($captured) { 257 } else { 0 }) `
                                -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                                -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore `
                                -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $outHash `
                                -and [Convert]::ToHexString($state.Err.Hash.GetCurrentHash()) -ceq $errHash `
                                -and $report.status -eq 7 -and $report.completed -eq 0 -and $state.ScopedStages -eq 0)
                        } finally {
                            & $module {
                                if (Test-Path Variable:script:controlOriginalStageShape) {
                                    Set-Item Function:script:Get-HermesStartupScopedStageShape $script:controlOriginalStageShape
                                    Set-Item Function:script:Test-HermesStartupScopedArmFrame $script:controlOriginalArmFrame
                                    Remove-Variable controlOriginalStageShape, controlOriginalArmFrame -Scope Script
                                }
                            }
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            507 {
                $state = New-ControlScopedCreated
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                foreach ($stream in @($state.Out, $state.Err)) {
                    $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                }
                $state.AcceptedUtf8Bytes = [long]8388608; $state.Bounds = 1
                $state.ScopedStages = 3; $state.ScopedPhase = 15; $state.SideWrites = 48
                $state.ScopedFault = @{ Reason = 15; Phase = 15; Block = 7; Held = 5; Echo = 255 }
                $state.ScopedStageFault = @{ Reason = 5; Shape = 1023 }
                $state.DebuggerKillRequested = 1
                foreach ($subject in 100..114) {
                    $state.Inventory.Subjects[[string]$subject] = @{ Create = 1; Initial = 1; Exit = 1; ExitCode = 0 }
                }
                while ($state.InputTasks.Count -lt 50) { $state.InputTasks.Add([Threading.Tasks.Task]::CompletedTask) }
                Set-HermesStartupDebuggerExit $state ([int]-1)
                $report = New-HermesStartupReport $state $false $false
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $report.observations.scopedStageFirstReason -eq 5 -and $report.observations.scopedStageFirstShape -eq 1023 `
                    -and $report.observations.subjects -eq 16 -and $report.observations.inputTasks -eq 50 `
                    -and $report.status -eq 7 -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0 `
                    -and $json -notmatch 'HERMES_ARM_STAGE|private|selected-sdk|command-root|pid|\.echo')
                foreach ($bad in @(-1, 1024, '1023', $true, $null)) {
                    $state.ScopedStageFault.Shape = $bad
                    Assert-Control ((New-HermesStartupReport $state $false $false).observations.scopedStageFirstShape -eq 0)
                }
                foreach ($bad in @(-1, 6, '5', $true, $null)) {
                    $state.ScopedStageFault.Reason = $bad
                    Assert-Control ((New-HermesStartupReport $state $false $false).observations.scopedStageFirstReason -eq 0)
                }
            }
            508 {
                $moduleAst = [Management.Automation.Language.Parser]::ParseFile(
                    (Join-Path $PSScriptRoot 'Native-StartupTrace.psm1'), [ref]$null, [ref]$null)
                $predicate = $moduleAst.Find({
                    param($n)
                    $n -is [Management.Automation.Language.FunctionDefinitionAst] `
                        -and $n.Name -ceq 'Test-HermesStartupScopedArmFrame'
                }, $true)
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                    [Text.Encoding]::UTF8.GetBytes($predicate.Extent.Text))).ToLowerInvariant()
                Assert-Control ($hash -ceq 'fa7409059885a5a52cf6d59f62ea71d2e81f280db26e5f094271857b37f31a92' `
                    -and (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Invoke-NativeStartupTrace.ps1') -Algorithm SHA256).Hash `
                        -ceq '0EAB27CDA608598593B598672E5AE90067AFC2745F8633EC138A00C14148687D')
            }
            509 {
                & {
                    # Serialization control only, not CDB acceptance of spaces.
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    function Join-Path { param([string]$Path, [string]$ChildPath) return $Path + '\' + $ChildPath }
                    $root = 'C:\owned\command-root'
                    $sdk = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                    $module = $sdk.Substring(0, $sdk.Length - 4)
                    $sources = New-ScopedCreateCommands $sdk
                    $arm = $sources['startup-arm.cdb']
                    $lines = $arm -split "`r`n"
                    $statement = '.catch { !' + $module + '.gflag +sls; .echo HERMES_ARM_STAGE v=1 stage=1; }'
                    Assert-Control ($sources.Count -eq 5 -and $lines.Count -eq 9 -and $lines[4] -ceq $statement `
                        -and $lines[5] -ceq '.expr /s masm' `
                        -and $lines[4] -cnotmatch '"|\\\\' `
                        -and [regex]::Matches(($sources.Values -join ''), '\.gflag \+sls').Count -eq 1 `
                        -and $lines[2] -ceq ('.load "' + $sdk.Replace('\', '\\') + '"') `
                        -and $arm -cnotmatch '!exts\.gflag|!gflag|(?m:^g\r?$)')
                    foreach ($item in @(
                        @('startup-cpr.cdb', '00e4c315299607e64a5cc79d2aafd5a3020fa1686774c24f10d7c672319a8a7f'),
                        @('startup-ibp.cdb', 'c2c6509cc08fab26e327f2aea8abdd950662d2c17534440d6dcca0ab3c374fc7'),
                        @('startup-epr.cdb', 'b99a7cbc74fd6ddd7a03bafa9f021333f62be4455e3e643036a4c8b88601d10f'),
                        @('startup.cdb', '84aa3a9b7176d5b70468db52aa4d0da45c6b7f3cc338a8223a8963457c6c1b23'),
                        @('startup-arm.cdb', '86a8929dd22a6e61a3f02ad9db39dbd01245d3b05537a9a225283e385b97d96b')
                    )) {
                        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                            [Text.Encoding]::ASCII.GetBytes($sources[$item[0]]))).ToLowerInvariant()
                        Assert-Control ($hash -ceq $item[1])
                    }
                    $queryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::ASCII.GetBytes($lines[6]))).ToLowerInvariant()
                    $previousStatement = '.catch { !"' + $module.Replace('\', '\\') +
                        '".gflag +sls; .echo HERMES_ARM_STAGE v=1 stage=1; }'
                    $previous = $arm.Replace($statement, $previousStatement).Replace(".expr /s masm`r`n", '')
                    $previousHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::ASCII.GetBytes($previous))).ToLowerInvariant()
                    Assert-Control ($queryHash -ceq '64c052dbbc805338521bc9dc8d60ffaa1b57f2c73fa5fe846df560dbd69d77a8' `
                        -and $previousHash -ceq 'd149256f963417243ed2f527df5a2657e9d945a099ca487eaff627ea8082a204')
                }
            }
            510 {
                & {
                    # Isolate the module's 255-character bound. The real
                    # provisioning path's separate 240-character bound stays.
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    $prefix = 'C:\' + ('x\' * 120)
                    foreach ($length in @(254, 255, 256)) {
                        $module = $prefix + ('a' * ($length - $prefix.Length - 5)) + '\exts'
                        Assert-Control ($module.Length -eq $length -and $module.Replace('\', '\\').Length -gt 255)
                        $refused = $false
                        try {
                            $source = (New-ScopedCreateCommands ($module + '.dll'))['startup-arm.cdb']
                        } catch { $refused = $true }
                        Assert-Control ($refused -eq ($length -eq 256))
                        if (-not $refused) {
                            Assert-Control ($source.Contains(('.catch { !' + $module + '.gflag +sls;')) `
                                -and ($source -split "`r`n")[4] -cnotmatch '"|\\\\')
                        }
                    }
                }
            }
            511 {
                & {
                    $definition = $driverAst.Find({
                        param($n)
                        $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Assert-TraceCommandPath'
                    }, $true)
                    . ([scriptblock]::Create($definition.Extent.Text))
                    $base = Join-Path $PSScriptRoot 'selected-sdk'
                    foreach ($path in @(
                        ($base + '"/exts.dll'), ($base + ';g/exts.dll'),
                        ($base + [char]0 + '/exts.dll'), ($base + "`n/exts.dll"),
                        ($base + [char]127 + '/exts.dll'), ($base + [char]233 + '/exts.dll'),
                        ($base + '/../selected-sdk/winxp/exts.dll'), 'selected-sdk/winxp/exts.dll',
                        ($base + '/' + ('x' * 240) + '/exts.dll'), ($base + '/other.dll')
                    )) {
                        $refused = $false
                        try { $null = New-ScopedCreateCommands $path } catch { $refused = $true }
                        Assert-Control ($refused -and $script:controlCommandWrites.Count -eq 0 `
                            -and $script:controlLaunchBindings -eq 0)
                    }
                }
            }
            512 {
                & {
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    $extension = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                    $module = $extension.Substring(0, $extension.Length - 4)
                    foreach ($bad in @(
                        'exts', 'exts.dll', '!gflag', 'D:\other\exts', ($module + '.dll'),
                        $module.Replace('\', '\\'), $module.Replace('\', '/'),
                        ('"' + $module + '"'), $module.ToUpperInvariant()
                    )) {
                        foreach ($role in @('Arm', 'Target')) {
                            $state = New-ControlScopedCreated
                            $physical = [StartupControlWriteStream]::new(0)
                            $writer = [IO.StreamWriter]::new($physical)
                            $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                            try {
                                Assert-Control ($state.ScopedPublication.QualifiedModule -ceq $module `
                                    -and $state.ExtensionSource.Path -ceq $extension)
                                if ($role -ceq 'Target') {
                                    Invoke-ControlScopedPublisher
                                    Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                                }
                                $state.ScopedPublication.QualifiedModule = $bad
                                $state.CreatedBinding.Scoped.QualifiedModule = $bad
                                if ($null -ne $state.ScopedInvocation) { $state.ScopedInvocation.Source.QualifiedModule = $bad }
                                $refused = $false
                                try { Confirm-TraceCommandSelection $extension } catch { $refused = $true }
                                Invoke-ControlScopedPublisher
                                Assert-Control ($refused -and -not (Test-HermesStartupScopedPublication $state) `
                                    -and -not (Test-HermesStartupScopedAction $state $role) -and -not $state.Acked `
                                    -and $physical.Writes -eq $(if ($role -ceq 'Arm') { 0 } else { 1 }))
                            } finally {
                                Close-HermesStartupScopedInput $state $writer
                                $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                            }
                        }
                    }
                }
            }
            513 {
                & {
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    $extension = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                    $module = $extension.Substring(0, $extension.Length - 4)
                    foreach ($token in @(
                        '!gflag +sls', '!exts.gflag +sls', '!D:\other\exts.gflag +sls',
                        ('!' + $extension + '.gflag +sls'), ('!' + $module + '.gflag -sls'),
                        ('!' + $module.Replace('\', '\\') + '.gflag +sls'),
                        ('!"' + $module + '".gflag +sls'),
                        ('!"' + $module.Replace('\', '\\') + '".gflag +sls')
                    )) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        try {
                            $source = (Get-TraceSelectedSources $extension)['startup-arm.cdb']
                            $bytes = [Text.Encoding]::ASCII.GetBytes($source.Replace(('!' + $module + '.gflag +sls'), $token))
                            $pin = [IO.MemoryStream]::new($bytes, $false)
                            $pins.Add($pin)
                            $hash = Get-ControlCommandHash $pin
                            Assert-Control ($hash -cne $state.ArmCommandSha256)
                            $state.CommandPins['startup-arm.cdb'] = $pin
                            $state.ArmCommandSha256 = $hash
                            $state.ScopedPublication.ArmCommandSha256 = $hash
                            $state.CreatedBinding.Scoped.ArmCommandSha256 = $hash
                            $refused = $false
                            try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                            Assert-Control ($refused -and $physical.Writes -eq 0 -and -not $state.ArmAttempted `
                                -and $state.InputTasks.Count -eq 0 -and $state.ScopedPublication.QualifiedModule -ceq $module)
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
                $moduleAst = [Management.Automation.Language.Parser]::ParseFile(
                    (Join-Path $PSScriptRoot 'Native-StartupTrace.psm1'), [ref]$null, [ref]$null)
                foreach ($item in @(
                    @('Add-HermesStartupScopedStage', '0d5345da659ec19cb74e42d24a23ef2d3c7af88704c0681b91191f51b8fd9b69'),
                    @('Get-HermesStartupScopedStageShape', '33ae33e09a74f9a498a1ec44e9e431676f7b0730105ab3fde92ae1b0cf9fbfeb')
                )) {
                    $definition = $moduleAst.Find({
                        param($n)
                        $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $item[0]
                    }, $true)
                    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($definition.Extent.Text))).ToLowerInvariant()
                    Assert-Control ($hash -ceq $item[1])
                }
            }
            514 {
                $sources = Get-ControlScopedQuerySources $extension
                foreach ($name in @('Whole', 'Read')) {
                    foreach ($entry in @(
                        @{ Prefix = 'Type information missing error at '; Kind = 2; Reason = 1 },
                        @{ Prefix = 'Memory access error at '; Kind = 3; Reason = 1 },
                        @{ Prefix = 'Syntax error in '; Kind = 4; Reason = 1 },
                        @{ Prefix = 'Syntax error at '; Kind = 4; Reason = 14 },
                        @{ Prefix = 'Numeric expression missing from '; Kind = 5; Reason = 1 },
                        @{ Prefix = "Couldn't resolve error at "; Kind = 5; Reason = 1 }
                    )) {
                        foreach ($prompt in @('', '1:000> ')) {
                            $state = New-ControlScopedCreated
                            $physical = [StartupControlWriteStream]::new(0)
                            $writer = [IO.StreamWriter]::new($physical)
                            try {
                                Invoke-HermesStartupScopedInput $state $writer Arm
                                Add-HermesStartupText $state $state.Out (
                                    "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                                $line = $prompt + $entry.Prefix + "'" + $sources[$name] + "'"
                                $echo = Get-HermesStartupScopedEchoMask $state $line
                                $shape = Get-HermesStartupScopedStageShape $state $line
                                $snapshots = $state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress
                                $split = [int]($line.Length / 2)
                                Add-HermesStartupText $state $state.Out ($line.Substring(0, $split)) $true
                                Assert-Control ($state.Protocol -eq 0 -and $state.ScopedQueryFault.Kind -eq 0)
                                Add-HermesStartupText $state $state.Out ($line.Substring($split) + "`n") $true
                                $report = New-HermesStartupReport $state $false $false
                                $json = $report | ConvertTo-Json -Depth 4 -Compress
                                Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $entry.Reason `
                                    -and $state.ScopedFault.Echo -eq $echo -and $state.ScopedStages -eq 1 `
                                    -and $state.ScopedStageFault.Reason -eq $(if ($name -ceq 'Whole') { 1 } else { 0 }) `
                                    -and $state.ScopedStageFault.Shape -eq $(if ($name -ceq 'Whole') { $shape } else { 0 }) `
                                    -and $report.observations.scopedReadFirstKind -eq $entry.Kind `
                                    -and $report.observations.scopedReadFirstSource -eq $(if ($name -ceq 'Whole') { 1 } else { 2 }) `
                                    -and ($state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress) -ceq $snapshots `
                                    -and $physical.Writes -eq 1 -and $state.InputTasks.Count -eq 1 `
                                    -and -not $state.Acked -and -not (Test-HermesStartupScopedAction $state Target) `
                                    -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                                    -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                                    -and $json -notmatch 'Type information|Memory access|Syntax error|Numeric expression|resolve error|NtGlobalFlag|\.printf|\.echo')
                            } finally {
                                Close-HermesStartupScopedInput $state $writer
                                $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                            }
                        }
                    }
                }
            }
            515 {
                $sources = Get-ControlScopedQuerySources $extension
                $read = $sources.Read
                foreach ($entry in @(
                    @{ Text = "prefix Type information missing error at '$read'"; Kind = 1 },
                    @{ Text = "Type information missing error at '$read' suffix"; Kind = 1 },
                    @{ Text = "type information missing error at '$read'"; Kind = 1 },
                    @{ Text = "Type information missing error AT '$read'"; Kind = 1 },
                    @{ Text = "Type information missing error at '$read"; Kind = 1 },
                    @{ Text = "Memory access error in '$read'"; Kind = 1 },
                    @{ Text = "Memory access error at $read"; Kind = 1 },
                    @{ Text = "Memory access error at '$read'`t"; Kind = 1 },
                    @{ Text = "^ Syntax error at '$read'"; Kind = 1 },
                    @{ Text = ".echo Syntax error at '$read'"; Kind = 1 },
                    @{ Text = "unrelated '$read'"; Kind = 1 },
                    @{ Text = ("'$read'"); Kind = 1 },
                    @{ Text = ('Syntax error at "' + $read + '"'); Kind = 1 },
                    @{ Text = "Syntax error at '$read$([char]0xa0)'"; Kind = 1 },
                    @{ Text = "Syntax error at '$read$([char]11)'"; Kind = 1 },
                    @{ Text = "Syntax error at 'private C:\private\sentinel S-1-5-21-secret $read'"; Kind = 4 },
                    @{ Text = "Type information missing error at '$read suffix'"; Kind = 2 },
                    @{ Text = ('.printf "private"; ' + $sources.Whole); Kind = 1 },
                    @{ Text = ('.printf "Type information missing error at"; ' + $read); Kind = 1 }
                )) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                        $originalReason = if ($entry.Text -cmatch "\ASyntax error at '[\x20-\x7e]{1,4096}'\z") { 14 } else { 1 }
                        Add-HermesStartupLine $state $entry.Text
                        $report = New-HermesStartupReport $state $false $false
                        $json = $report | ConvertTo-Json -Depth 4 -Compress
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $originalReason `
                            -and $report.observations.scopedReadFirstKind -eq $entry.Kind `
                            -and $report.observations.scopedReadFirstSource -eq 0 `
                            -and $state.ScopedStages -eq 1 -and $state.InputTasks.Count -eq 1 `
                            -and -not $state.Acked -and $report.completed -eq 0 `
                            -and $json -notmatch 'private|sentinel|S-1-|NtGlobalFlag|Type information|Syntax error|\.printf')
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            516 {
                $sources = Get-ControlScopedQuerySources $extension
                foreach ($entry in @(
                    @{ Text = $sources.Whole; Kind = 0; Source = 0; Fault = 0 },
                    @{ Text = ('1:000> ' + $sources.Whole); Kind = 0; Source = 0; Fault = 0 },
                    @{ Text = (" `t" + $sources.Whole + "`t"); Kind = 6; Source = 1; Fault = 1 },
                    @{ Text = $sources.Read; Kind = 6; Source = 2; Fault = 1 },
                    @{ Text = ('1:000> ' + $sources.Read); Kind = 6; Source = 2; Fault = 1 },
                    @{ Text = ($sources.Read + ';'); Kind = 1; Source = 0; Fault = 1 },
                    @{ Text = ($sources.Whole + ' private'); Kind = 1; Source = 0; Fault = 1 }
                )) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                        Add-HermesStartupLine $state $entry.Text
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.Protocol -eq $entry.Fault `
                            -and $report.observations.scopedReadFirstKind -eq $entry.Kind `
                            -and $report.observations.scopedReadFirstSource -eq $entry.Source `
                            -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1 `
                            -and $physical.Writes -eq 1 -and -not $state.Acked -and $report.completed -eq 0 `
                            -and -not (Test-HermesStartupScopedAction $state Target))
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            517 {
                $sources = Get-ControlScopedQuerySources $extension
                $line = "Type information missing error at '$($sources.Whole)'"
                foreach ($entry in @(
                    @{ Sdk = $false; Change = {} },
                    @{ Sdk = $true; Change = { Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=2' } },
                    @{ Sdk = $true; Change = {
                        Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=2'
                        Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=8 pid=22'
                    } },
                    @{ Sdk = $true; Change = { $state.Held.Released = $true } },
                    @{ Sdk = $true; Change = { $state.ScopedInvocation.Pid = 33 } },
                    @{ Sdk = $true; Change = { $state.ScopedInvocation.Source.ArmCommandSha256 = 'f' * 64 } },
                    @{ Sdk = $true; Change = { $state.NativeRowSnapshots.Resumed.Row.born = 124 } },
                    @{ Sdk = $true; Change = { $state.Final = @{ stage = 3 } } },
                    @{ Sdk = $true; Change = { $state.Exits['22'] = 0 } },
                    @{ Sdk = $true; Change = { $state.Exits['11'] = 0 } },
                    @{ Sdk = $true; Change = { $state.Inventory.Block.Flags = 2 } },
                    @{ Sdk = $true; Change = { $state.ArmWritten = $false } },
                    @{ Sdk = $true; Change = { $state.Error = 13 } },
                    @{ Sdk = $true; Change = { $state.Timeout = 1 } },
                    @{ Sdk = $true; Change = { $state.CleanupFault = 13 } },
                    @{ Sdk = $true; Change = { $state.NativeRowsFault = 1 } },
                    @{ Sdk = $true; Change = { $state.Stopped = $true } },
                    @{ Sdk = $true; Change = { $state.Out.End = $true } },
                    @{ Sdk = $true; Change = { $state.Out.Faulted = $true } },
                    @{ Sdk = $true; Change = { $state.Err.Faulted = $true } },
                    @{ Sdk = $true; Change = { $state.DebuggerExitAvailable = 1 } },
                    @{ Sdk = $true; Change = { $state.DebuggerKillRequested = 1 } },
                    @{ Sdk = $true; Change = { $state.Unknown = 1 } },
                    @{ Sdk = $true; Change = { $state.TraceFaulted = $true } },
                    @{ Sdk = $true; Change = { $state.Failure = @{ Kind = 1 } } },
                    @{ Sdk = $true; Change = { $state.InputClosed = $true } },
                    @{ Sdk = $true; Change = { $state.Inventory.Block.Stages = 0 } },
                    @{ Sdk = $true; Change = { $state.Inventory.Limit = 1 } }
                )) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`n") $true
                        if ($entry.Sdk) { Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=1' }
                        . $entry.Change
                        Add-HermesStartupLine $state $line
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 1 `
                            -and $report.observations.scopedReadFirstKind -eq 0 `
                            -and $report.observations.scopedReadFirstSource -eq 0 `
                            -and $report.observations.scopedReadFirstLexical -eq 0 `
                            -and $report.observations.scopedReadFirstTrimKind -eq 0 `
                            -and $physical.Writes -eq 1 -and -not $state.Acked -and $report.completed -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                try {
                    Invoke-HermesStartupScopedInput $state $writer Arm
                    Add-HermesStartupText $state $state.Out (
                        "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                    Add-HermesStartupText $state $state.Err ($line + "`n") $false
                    foreach ($unrefused in @(
                        "Type information missing error at '@@c++(sizeof(@`$peb->NtGlobalFlag))'",
                        "Memory access error at '@@c++(@`$peb->NtGlobalFlag)'",
                        'Type ntdll!_PEB not found',
                        "Couldn't resolve error at '@@c++(@`$peb->NtGlobalFlag)'"
                    )) { Add-HermesStartupLine $state $unrefused }
                    $report = New-HermesStartupReport $state $false $false
                    Assert-Control ($state.Protocol -eq 0 -and $state.ScopedStageFault.Reason -eq 0 `
                        -and $report.observations.scopedReadFirstKind -eq 0 `
                        -and $report.observations.scopedReadFirstSource -eq 0 `
                        -and $report.observations.scopedReadFirstLexical -eq 0 `
                        -and $report.observations.scopedReadFirstTrimKind -eq 0)
                    Add-HermesStartupLine $state $line
                    Assert-Control ($state.ScopedQueryFault.Kind -eq 2 -and $state.ScopedQueryFault.Source -eq 1)
                } finally {
                    Close-HermesStartupScopedInput $state $writer
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
                foreach ($mode in @('Trace', 'Inventory')) {
                    $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode $mode
                    Add-HermesStartupLine $state $line
                    $report = New-HermesStartupReport $state $false $false
                    Assert-Control ($state.ScopedQueryFault.Kind -eq 0 -and $state.ScopedQueryFault.Source -eq 0 `
                        -and -not $report.observations.Contains('scopedReadFirstKind'))
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            518 {
                $sources = Get-ControlScopedQuerySources $extension
                foreach ($known in @($false, $true)) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    $module = Get-Module Native-StartupTrace
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                        $line = if ($known) { "Memory access error at '$($sources.Whole)'" } `
                            else { '.printf private-path S-1-5-21-secret; .echo HERMES_ARM_STAGE' }
                        Add-HermesStartupLine $state $line
                        $first = $state.ScopedQueryFault | ConvertTo-Json -Compress
                        $protocolFirst = $state.ScopedFault | ConvertTo-Json -Compress
                        $stageFirst = $state.ScopedStageFault | ConvertTo-Json -Compress
                        $snapshots = $state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress
                        & $module {
                            $script:controlOriginalQueryRefusal = (Get-Item Function:Set-HermesStartupScopedQueryRefusal).ScriptBlock
                            $script:controlOriginalArmFrame = (Get-Item Function:Test-HermesStartupScopedArmFrame).ScriptBlock
                            Set-Item Function:script:Set-HermesStartupScopedQueryRefusal { throw 'No frozen query reclassification.' }
                            Set-Item Function:script:Test-HermesStartupScopedArmFrame { throw 'No frozen query frame work.' }
                        }
                        Add-HermesStartupLine $state ("Type information missing error at '$($sources.Read)'")
                        Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=2'
                        Set-HermesStartupProtocolFault $state 14 255 $line
                        $state.ScopedEchoes.Clear()
                        foreach ($iteration in 1..2) {
                            if ($iteration -eq 2) {
                                $state.AcceptedUtf8Bytes = [long]8388608; $state.Bounds = 1
                                $outBefore = Get-ControlStreamSnapshot $state.Out
                                $errBefore = Get-ControlStreamSnapshot $state.Err
                                Add-HermesStartupText $state $state.Out ($line + "`n") $true
                                Add-HermesStartupText $state $state.Err ($line + "`n") $false
                                Assert-Control ((Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                                    -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore)
                            }
                            $report = New-HermesStartupReport $state $false $false
                            $json = $report | ConvertTo-Json -Depth 4 -Compress
                            Assert-Control (($state.ScopedQueryFault | ConvertTo-Json -Compress) -ceq $first `
                                -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $protocolFirst `
                                -and ($state.ScopedStageFault | ConvertTo-Json -Compress) -ceq $stageFirst `
                                -and ($state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress) -ceq $snapshots `
                                -and $report.observations.scopedReadFirstKind -eq $(if ($known) { 3 } else { 1 }) `
                                -and $report.observations.scopedReadFirstSource -eq [int]$known `
                                -and $report.observations.scopedReadFirstLexical -eq $(if ($known) { 196 } else { 0 }) `
                                -and $report.observations.scopedReadFirstTrimKind -eq $(if ($known) { 3 } else { 0 }) `
                                -and $state.ScopedStages -eq 1 -and $state.InputTasks.Count -eq 1 `
                                -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                                -and $json -notmatch 'private-path|secret|S-1-|Type information|Memory access|NtGlobalFlag|\.printf|\.echo')
                        }
                    } finally {
                        & $module {
                            if (Test-Path Variable:script:controlOriginalQueryRefusal) {
                                Set-Item Function:script:Set-HermesStartupScopedQueryRefusal $script:controlOriginalQueryRefusal
                                Set-Item Function:script:Test-HermesStartupScopedArmFrame $script:controlOriginalArmFrame
                                Remove-Variable controlOriginalQueryRefusal, controlOriginalArmFrame -Scope Script
                            }
                        }
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            519 {
                $sources = Get-ControlScopedQuerySources $extension
                foreach ($boundary in @('Bytes', 'Bounds', 'Line', 'Protocol', 'FirstFault', 'Error', 'Timeout', 'Stopped', 'SdkError')) {
                    $state = New-ControlScopedCreated
                    $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                    $module = Get-Module Native-StartupTrace
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                        $line = "Type information missing error at '$($sources.Whole)'"
                        switch ($boundary) {
                            Bytes { $state.AcceptedUtf8Bytes = [long]8388608 }
                            Bounds { $state.Bounds = 1 }
                            Line { $line = $line + ('x' * 4096) }
                            Protocol { $state.Protocol = 1 }
                            FirstFault { $state.ScopedFault.Reason = 1 }
                            Error { $state.Error = 13 }
                            Timeout { $state.Timeout = 1 }
                            Stopped { $state.Stopped = $true }
                            SdkError { $state.Observations.SdkCommandErrors = 1 }
                        }
                        $outBefore = Get-ControlStreamSnapshot $state.Out
                        $errBefore = Get-ControlStreamSnapshot $state.Err
                        $outHash = [Convert]::ToHexString($state.Out.Hash.GetCurrentHash())
                        $errHash = [Convert]::ToHexString($state.Err.Hash.GetCurrentHash())
                        & $module {
                            $script:controlOriginalArmFrame = (Get-Item Function:Test-HermesStartupScopedArmFrame).ScriptBlock
                            Set-Item Function:script:Test-HermesStartupScopedArmFrame { throw 'No unavailable query frame work.' }
                        }
                        Set-HermesStartupScopedQueryRefusal $state $line
                        Set-HermesStartupProtocolFault $state 1 0 $line
                        if ($boundary -cin @('Bytes', 'Bounds')) {
                            Add-HermesStartupLine $state $line
                            Add-HermesStartupText $state $state.Out ($line + "`n") $true
                            Add-HermesStartupText $state $state.Err ($line + "`n") $false
                        }
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($report.observations.scopedReadFirstKind -eq 0 `
                            -and $report.observations.scopedReadFirstSource -eq 0 `
                            -and $report.observations.scopedReadFirstLexical -eq 0 `
                            -and $report.observations.scopedReadFirstTrimKind -eq 0 `
                            -and (Get-ControlStreamSnapshot $state.Out) -ceq $outBefore `
                            -and (Get-ControlStreamSnapshot $state.Err) -ceq $errBefore `
                            -and [Convert]::ToHexString($state.Out.Hash.GetCurrentHash()) -ceq $outHash `
                            -and [Convert]::ToHexString($state.Err.Hash.GetCurrentHash()) -ceq $errHash `
                            -and $state.ScopedStages -eq 1 -and $report.completed -eq 0)
                    } finally {
                        & $module {
                            if (Test-Path Variable:script:controlOriginalArmFrame) {
                                Set-Item Function:script:Test-HermesStartupScopedArmFrame $script:controlOriginalArmFrame
                                Remove-Variable controlOriginalArmFrame -Scope Script
                            }
                        }
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            520 {
                $state = New-ControlScopedCreated
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                foreach ($stream in @($state.Out, $state.Err)) {
                    $stream.Count = [long]8388608; $stream.Prefix = 'private-sentinel' * 256; $stream.Fingerprint = 'f' * 64
                }
                $state.AcceptedUtf8Bytes = [long]8388608; $state.Bounds = 1
                $state.ScopedStages = 3; $state.ScopedPhase = 15; $state.SideWrites = 48
                $state.ScopedFault = @{ Reason = 15; Phase = 15; Block = 7; Held = 5; Echo = 255 }
                $state.ScopedStageFault = @{ Reason = 5; Shape = 1023 }
                $state.ScopedQueryFault = @{ Kind = 6; Source = 2; Lexical = 1023; TrimKind = 5 }
                $state.DebuggerKillRequested = 1
                foreach ($subject in 100..114) {
                    $state.Inventory.Subjects[[string]$subject] = @{ Create = 1; Initial = 1; Exit = 1; ExitCode = 0 }
                }
                while ($state.InputTasks.Count -lt 50) { $state.InputTasks.Add([Threading.Tasks.Task]::CompletedTask) }
                Set-HermesStartupDebuggerExit $state ([int]-1)
                $report = New-HermesStartupReport $state $false $false
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $report.observations.scopedReadFirstKind -eq 6 -and $report.observations.scopedReadFirstSource -eq 2 `
                    -and $report.observations.scopedReadFirstLexical -eq 1023 -and $report.observations.scopedReadFirstTrimKind -eq 5 `
                    -and $report.observations.subjects -eq 16 -and $report.observations.inputTasks -eq 50 `
                    -and $report.status -eq 7 -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 `
                    -and $report.originalGatePassed -eq 0 `
                    -and $json -notmatch 'private|sentinel|selected-sdk|command-root|pid|NtGlobalFlag|\.printf|\.echo')
                foreach ($field in @('Kind', 'Source', 'Lexical', 'TrimKind')) {
                    $maximum = switch ($field) { Kind { 6 }; Source { 2 }; Lexical { 1023 }; TrimKind { 5 } }
                    foreach ($bad in @(-1, ($maximum + 1), ([string]$maximum), $true, $null)) {
                        $state.ScopedQueryFault[$field] = $bad
                        $report = New-HermesStartupReport $state $false $false
                        Assert-Control ($report.observations['scopedReadFirst' + $field] -eq 0)
                    }
                }
                $state.ScopedQueryFault.TrimKind = 1
                Assert-Control ((New-HermesStartupReport $state $false $false).observations.scopedReadFirstTrimKind -eq 0)
            }
            521 {
                & {
                    $module = Get-Module Native-StartupTrace
                    if (-not $IsWindows) {
                        # Windows filename serialization only; controller and BCL pipe remain real.
                        function Join-Path { param([string]$Path, [string]$ChildPath) return $Path + '\' + $ChildPath }
                        function Assert-TraceCommandPath { param([string]$Path) return $Path }
                        & $module {
                            function script:Join-Path {
                                param([string]$Path, [string]$ChildPath)
                                return $Path + '\' + $ChildPath
                            }
                        }
                    }
                    $root = 'C:\owned\command root'
                    $extension = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                    $pipe = New-ControlPipe
                    $writer = [IO.StreamWriter]::new($pipe.Writer, [Text.Encoding]::ASCII)
                    try {
                        $state = New-ControlScopedCreated
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        $bytes = [byte[]]::new(1024)
                        $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Invoke-ControlScopedPublisher
                        Assert-Control ($read.Wait(5000))
                        $count = $read.GetAwaiter().GetResult()
                        $expected = '$$<C:\owned\command root\startup-arm.cdb' + "`n"
                        Assert-Control ($count -eq $expected.Length `
                            -and [Text.Encoding]::ASCII.GetString($bytes, 0, $count) -ceq $expected `
                            -and $state.ArmWritten -and $state.InputTasks.Count -eq 1 `
                            -and [object]::ReferenceEquals($state.InputPending, $state.InputTasks[0]) `
                            -and $state.InputPending.IsCompletedSuccessfully `
                            -and [object]::ReferenceEquals($state.InputWriter, $writer) `
                            -and -not $state.Acked -and -not $state.Held.Released -and -not $state.InputClosed)
                        $refused = $false
                        try { Invoke-HermesStartupScopedInput $state $writer Arm } catch { $refused = $true }
                        Invoke-ControlScopedPublisher
                        Assert-Control ($refused -and $state.InputTasks.Count -eq 1)
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Invoke-ControlScopedPublisher
                        Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 2 `
                            -and $bytes[0] -eq 103 -and $bytes[1] -eq 10 `
                            -and $state.InputTasks.Count -eq 2 -and $state.Continued -and $state.Acked)
                        $read = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Close-HermesStartupScopedInput $state $writer
                        Assert-Control ($read.Wait(5000) -and $read.GetAwaiter().GetResult() -eq 0 `
                            -and $state.InputClosed -and $state.CleanupFault -eq 0)
                    } finally {
                        if (-not $IsWindows) {
                            Assert-Control (& $module {
                                Remove-Item Function:Join-Path
                                (Get-Command Join-Path).CommandType -eq 'Cmdlet'
                            })
                        }
                        $writer.Dispose(); $pipe.Writer.Dispose(); $pipe.Reader.Dispose()
                        if ($null -ne $state) { $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose() }
                    }
                }
            }
            522 {
                foreach ($suffix in @('"', ';g', "`r", "`n", [string][char]0, [string][char]31,
                        [string][char]127, [string][char]233, (';$$<' + $root + '/startup-arm.cdb'))) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        $state.ScopedPublication.Root += $suffix
                        $state.CreatedBinding.Scoped.Root = $state.ScopedPublication.Root
                        $refused = $false
                        try { Invoke-HermesStartupScopedInput $state $writer Arm } catch { $refused = $true }
                        Invoke-ControlScopedPublisher
                        Assert-Control ($refused -and $physical.Writes -eq 0 -and $physical.Length -eq 0 `
                            -and $state.InputTasks.Count -eq 0 -and $null -eq $state.InputPending `
                            -and $null -eq $state.InputWriter -and -not $state.ArmAttempted -and -not $state.Acked)
                    } finally {
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
                foreach ($path in @('startup-arm.cdb', ($root + '/..'), ($root + '/startup-arm.cdb'), ($root + ' replay'))) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        $state.ScopedPublication.Root = $path
                        $state.CreatedBinding.Scoped.Root = $path
                        $refused = $false
                        try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                        Assert-Control ($refused -and $physical.Writes -eq 0 -and $physical.Length -eq 0 `
                            -and $state.InputTasks.Count -eq 0 -and $null -eq $state.InputWriter `
                            -and -not $state.ArmAttempted -and -not $state.Acked)
                    } finally {
                        $writer.Dispose()
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            523 {
                foreach ($prefix in @('', '1:000> ')) {
                    foreach ($missing in @('All', 'Sdk', 'Typed', 'Query', 'None')) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        $lines = (Get-TraceSelectedSources $extension)['startup-arm.cdb'] -split "`r`n"
                        try {
                            Invoke-ControlScopedPublisher
                            foreach ($index in 0..7) {
                                Add-HermesStartupLine $state ($prefix + $lines[$index])
                                Assert-Control ($state.Protocol -eq 0 -and -not $state.Acked `
                                    -and -not $state.Held.Released -and $physical.Writes -eq 1)
                                switch ($index) {
                                    0 {
                                        Assert-Control ($null -eq $state.Inventory.Block)
                                        Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=7 pid=22'
                                    }
                                    1 {
                                        Assert-Control ($state.Inventory.Block.Event -eq 0)
                                        Add-HermesStartupLine $state 'Last event: 16.1: Create process 1:16'
                                    }
                                    4 {
                                        Assert-Control ($state.ScopedStages -eq 0 -and $state.Inventory.Block.Flags -eq -1)
                                        if ($missing -cnotin @('All', 'Sdk')) {
                                            Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=1'
                                        }
                                    }
                                    6 {
                                        Assert-Control ($state.ScopedStages -eq [int]($missing -cnotin @('All', 'Sdk')) `
                                            -and $state.Inventory.Block.Flags -eq -1)
                                        if ($missing -cnotin @('All', 'Typed')) {
                                            Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                                        }
                                        if ($missing -cnotin @('All', 'Query')) {
                                            Add-HermesStartupLine $state 'HERMES_ARM_STAGE v=1 stage=2'
                                        }
                                    }
                                    7 {
                                        Assert-Control ($null -eq $state.Arm -and $state.Inventory.Block.Kind -eq 7)
                                        Add-HermesStartupLine $state 'HERMES_CDB v=4 kind=8 pid=22'
                                    }
                                }
                            }
                            Add-HermesStartupText $state $state.Out '1:000> ' $true
                            $allowed = $missing -ceq 'None'
                            Assert-Control ((Test-HermesStartupScopedAction $state Target) -eq $allowed `
                                -and (Get-HermesStartupTypedFlags $state) -eq $(if ($allowed) { 2 } else { -1 }) `
                                -and $state.Protocol -eq 0 -and $state.InputTasks.Count -eq 1)
                            Invoke-ControlScopedPublisher
                            Assert-Control ($physical.Writes -eq (1 + [int]$allowed) `
                                -and $state.InputTasks.Count -eq (1 + [int]$allowed) `
                                -and $state.Acked -eq $allowed -and $state.Continued -eq $allowed `
                                -and $state.Held.Released -eq $allowed)
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            524 {
                foreach ($index in @(0, 4, 6, 7)) {
                    $line = ((New-ScopedCreateCommands $extension)['startup-arm.cdb'] -split "`r`n")[$index]
                    foreach ($entry in @(
                        @{ Text = (' ' + $line); Reason = $(if ($index -eq 4) { 15 } else { 1 }) },
                        @{ Text = ($line + ' '); Reason = $(if ($index -eq 4) { 15 } else { 1 }) },
                        @{ Text = $line.Replace(' ', "`t"); Reason = $(if ($index -eq 4) { 15 } else { 1 }) },
                        @{ Text = "Syntax error at '$line'"; Reason = $(if ($index -eq 4) { 15 } else { 14 }) },
                        @{ Text = "Type information missing error at '$line'"; Reason = $(if ($index -eq 4) { 15 } else { 1 }) }
                    )) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        try {
                            Invoke-ControlScopedPublisher
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                            Assert-Control (-not $state.ScopedEchoes.Contains($entry.Text))
                            Add-HermesStartupLine $state $entry.Text
                            $first = $state.ScopedFault | ConvertTo-Json -Compress
                            Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                            Invoke-ControlScopedPublisher
                            Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $entry.Reason `
                                -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $first `
                                -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1 `
                                -and (Get-HermesStartupTypedFlags $state) -eq -1 -and $null -eq $state.Arm `
                                -and $physical.Writes -eq 1 -and $state.InputTasks.Count -eq 1 `
                                -and -not $state.Acked -and -not $state.Continued -and -not $state.Held.Released)
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            525 {
                $sources = Get-ControlScopedQuerySources $extension
                foreach ($family in @(
                    @{ Prefix = 'Type information missing error at '; Kind = 2; Bit = 2 },
                    @{ Prefix = 'Memory access error at '; Kind = 3; Bit = 4 },
                    @{ Prefix = 'Syntax error in '; Kind = 4; Bit = 8 },
                    @{ Prefix = 'Syntax error at '; Kind = 4; Bit = 8 },
                    @{ Prefix = 'Numeric expression missing from '; Kind = 5; Bit = 16 },
                    @{ Prefix = "Couldn't resolve error at "; Kind = 5; Bit = 16 }
                )) {
                    foreach ($edge in @(
                        @{ Left = ''; Right = ''; Trim = $true; Bit = 0 },
                        @{ Left = " `t"; Right = ''; Trim = $true; Bit = 512 },
                        @{ Left = ''; Right = "`t "; Trim = $true; Bit = 512 },
                        @{ Left = " `t"; Right = "`t "; Trim = $true; Bit = 512 },
                        @{ Left = [string][char]160; Right = ''; Trim = $false; Bit = 0 },
                        @{ Left = ''; Right = [string][char]11; Trim = $false; Bit = 0 }
                    )) {
                        foreach ($prompt in @('', '1:000> ')) {
                            $state = New-ControlScopedCreated
                            $physical = [StartupControlWriteStream]::new(0)
                            $writer = [IO.StreamWriter]::new($physical)
                            try {
                                Invoke-HermesStartupScopedInput $state $writer Arm
                                Add-HermesStartupText $state $state.Out (
                                    "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                                $line = $prompt + $edge.Left + $family.Prefix + "'" + $sources.Whole + "'" + $edge.Right
                                $shape = Get-HermesStartupScopedStageShape $state $line
                                $echo = Get-HermesStartupScopedEchoMask $state $line
                                Add-HermesStartupLine $state $line
                                $report = New-HermesStartupReport $state $false $false
                                $unchanged = $edge.Left -ceq '' -and $edge.Right -ceq ''
                                $reason = if ($unchanged -and $family.Prefix -ceq 'Syntax error at ') { 14 } else { 1 }
                                Assert-Control ($report.observations.scopedReadFirstKind -eq $(if ($unchanged) { $family.Kind } else { 1 }) `
                                    -and $report.observations.scopedReadFirstSource -eq [int]$unchanged `
                                    -and $report.observations.scopedReadFirstLexical -eq ($family.Bit -bor 192 -bor $edge.Bit) `
                                    -and $report.observations.scopedReadFirstTrimKind -eq $(if ($edge.Trim) { $family.Kind } else { 0 }) `
                                    -and $state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq $reason `
                                    -and $state.ScopedFault.Echo -eq $echo -and $state.ScopedStageFault.Reason -eq 1 `
                                    -and $state.ScopedStageFault.Shape -eq $shape -and $state.ScopedStages -eq 1 `
                                    -and $state.Inventory.Block.Flags -eq -1 -and $physical.Writes -eq 1 `
                                    -and $state.InputTasks.Count -eq 1 -and -not $state.Acked `
                                    -and -not (Test-HermesStartupScopedAction $state Target) -and $report.completed -eq 0)
                            } finally {
                                Close-HermesStartupScopedInput $state $writer
                                $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                            }
                        }
                    }
                }
            }
            526 {
                $sources = Get-ControlScopedQuerySources $extension
                $entries = @(
                    @{ Text = ('.printf private sizeof(@$peb->NtGlobalFlag)'); Mask = 64 },
                    @{ Text = ('.printf private @@c++(@$peb->NtGlobalFlag)'); Mask = 128 },
                    @{ Text = ('.printf private sizeof(@$peb->NtGlobalFlag) @@c++(@$peb->NtGlobalFlag)'); Mask = 192 },
                    @{ Text = ('.printf private SIZEOF(@$PEB->NTGLOBALFLAG) @@C++(@$PEB->NTGLOBALFLAG)'); Mask = 0 },
                    @{ Text = ("prefix Type information missing error at '" + $sources.Read + "'"); Mask = 130 },
                    @{ Text = (" `t^ Bad register TYPE INFORMATION memory access syntax error numeric expression ReSoLvE SyMbOl .printf " +
                        'sizeof(@$peb->NtGlobalFlag) @@c++(@$peb->NtGlobalFlag)' + "`t"); Mask = 1023 }
                )
                foreach ($word in @(
                    @{ Text = 'Bad register'; Bit = 1 }, @{ Text = 'type information'; Bit = 2 },
                    @{ Text = 'memory access'; Bit = 4 }, @{ Text = 'syntax error'; Bit = 8 },
                    @{ Text = 'numeric expression'; Bit = 16 }, @{ Text = 'resolve'; Bit = 16 },
                    @{ Text = 'symbol'; Bit = 32 }
                )) {
                    foreach ($text in @($word.Text.ToLowerInvariant(), $word.Text.ToUpperInvariant())) {
                        $entries += @{ Text = ($text + " private '" + $sources.Read + "'"); Mask = ($word.Bit -bor 128) }
                    }
                }
                foreach ($entry in $entries) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        Invoke-ControlScopedPublisher
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                        Add-HermesStartupLine $state $entry.Text
                        $report = New-HermesStartupReport $state $false $false
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        Invoke-ControlScopedPublisher
                        Assert-Control ($report.observations.scopedReadFirstKind -eq 1 `
                            -and $report.observations.scopedReadFirstSource -eq 0 `
                            -and $report.observations.scopedReadFirstLexical -eq $entry.Mask `
                            -and $report.observations.scopedReadFirstTrimKind -eq 0 `
                            -and $state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 1 `
                            -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1 `
                            -and $physical.Writes -eq 1 -and $state.InputTasks.Count -eq 1 `
                            -and -not $state.Acked -and -not $state.Continued -and -not $state.Held.Released `
                            -and (Get-HermesStartupTypedFlags $state) -eq -1 -and $report.armingVerified -eq 0 `
                            -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            527 {
                $sources = Get-ControlScopedQuerySources $extension
                foreach ($boundary in @('None', 'Bytes', 'Bounds', 'Stopped')) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $module = Get-Module Native-StartupTrace
                    try {
                        Invoke-HermesStartupScopedInput $state $writer Arm
                        Add-HermesStartupText $state $state.Out (
                            "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                        Add-HermesStartupLine $state (" `tMemory access error at '" + $sources.Whole + "'`t")
                        $first = $state.ScopedQueryFault | ConvertTo-Json -Compress
                        $fault = $state.ScopedFault | ConvertTo-Json -Compress
                        $stage = $state.ScopedStageFault | ConvertTo-Json -Compress
                        $rows = $state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress
                        & $module {
                            $script:controlOriginalQueryRefusal = (Get-Item Function:Set-HermesStartupScopedQueryRefusal).ScriptBlock
                            $script:controlOriginalArmFrame = (Get-Item Function:Test-HermesStartupScopedArmFrame).ScriptBlock
                            Set-Item Function:script:Test-HermesStartupScopedArmFrame { throw 'No frozen lexical frame work.' }
                        }
                        switch ($boundary) {
                            Bytes { $state.AcceptedUtf8Bytes = [long]8388608 }
                            Bounds { $state.Bounds = 1 }
                            Stopped { $state.Stopped = $true }
                        }
                        $line = "^ bad register symbol .printf private-path S-1-5-21-secret`n"
                        Set-HermesStartupScopedQueryRefusal $state $line
                        Add-HermesStartupLine $state $line
                        Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                        if ($boundary -cin @('Bytes', 'Bounds')) {
                            $before = Get-ControlStreamSnapshot $state.Out
                            Add-HermesStartupText $state $state.Out $line $true
                            Assert-Control ((Get-ControlStreamSnapshot $state.Out) -ceq $before)
                        }
                        & $module {
                            Set-Item Function:script:Set-HermesStartupScopedQueryRefusal { throw 'Reporting must not reparse lexical observations.' }
                        }
                        $state.ScopedEchoes.Clear()
                        $report = New-HermesStartupReport $state $false $false
                        $json = $report | ConvertTo-Json -Depth 4 -Compress
                        Assert-Control (($state.ScopedQueryFault | ConvertTo-Json -Compress) -ceq $first `
                            -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $fault `
                            -and ($state.ScopedStageFault | ConvertTo-Json -Compress) -ceq $stage `
                            -and ($state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress) -ceq $rows `
                            -and $report.observations.scopedReadFirstKind -eq 1 -and $report.observations.scopedReadFirstSource -eq 0 `
                            -and $report.observations.scopedReadFirstLexical -eq 708 -and $report.observations.scopedReadFirstTrimKind -eq 3 `
                            -and $physical.Writes -eq 1 -and $state.InputTasks.Count -eq 1 -and -not $state.Acked `
                            -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1 `
                            -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                            -and $json -notmatch 'private|secret|S-1-|Memory access|bad register|symbol|NtGlobalFlag|\.printf|\.echo')
                    } finally {
                        & $module {
                            if (Test-Path Variable:script:controlOriginalQueryRefusal) {
                                Set-Item Function:script:Set-HermesStartupScopedQueryRefusal $script:controlOriginalQueryRefusal
                                Set-Item Function:script:Test-HermesStartupScopedArmFrame $script:controlOriginalArmFrame
                                Remove-Variable controlOriginalQueryRefusal, controlOriginalArmFrame -Scope Script
                            }
                        }
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            528 {
                & {
                    # Optional delimiter control, not a missing-semicolon diagnosis:
                    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/-if
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    function Join-Path { param([string]$Path, [string]$ChildPath) return $Path + '\' + $ChildPath }
                    $root = 'C:\owned\command-root'
                    $sdk = 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll'
                    # Factor out the separate interpreter command, retaining
                    # the original one-character arm-program proof.
                    $arm = (New-ScopedCreateCommands $sdk)['startup-arm.cdb'].Replace(".expr /s masm`r`n", '')
                    $expression = '@@c++(@$peb->NtGlobalFlag)'
                    $needle = $expression + '; }; .echo'
                    $position = $arm.IndexOf($needle, [StringComparison]::Ordinal)
                    Assert-Control ($position -gt 0 -and $position -eq $arm.LastIndexOf($needle, [StringComparison]::Ordinal))
                    $delimiter = $position + $expression.Length
                    $prior = $arm.Remove($delimiter, 1)
                    $priorHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::ASCII.GetBytes($prior))).ToLowerInvariant()
                    Assert-Control ($arm[$delimiter] -ceq ';' -and $arm -ceq $prior.Insert($delimiter, ';') `
                        -and $arm.Length -eq ($prior.Length + 1) `
                        -and $priorHash -ceq 'c05cf01b2cce5ad85c74c94f6f2eeb13eac665487d2030b57020e12d200461e9' `
                        -and [regex]::Matches($arm, '\.gflag \+sls').Count -eq 1 `
                        -and [regex]::Matches($arm, [regex]::Escape('@@c++(')).Count -eq 2 `
                        -and [regex]::Matches($arm, 'sizeof\(@\$peb->NtGlobalFlag\)').Count -eq 1 `
                        -and [regex]::Matches($arm, 'HERMES_ARM_STAGE v=1 stage=[12]').Count -eq 2)
                    $driver = [IO.File]::ReadAllText($driverAst.Extent.File).Replace($evaluatorSourceInsertion, '')
                    # Factor out only the separate evaluator-bootstrap delta
                    # before retaining the original one-character driver proof.
                    $driver = $driver.Replace("@('_NT_EXPR_EVAL', '_NT_SYMBOL_PATH',", "@('_NT_SYMBOL_PATH',").
                        Replace("@('-ee', 'masm', '-o', '-cf',", "@('-o', '-cf',")
                    $position = $driver.IndexOf($needle, [StringComparison]::Ordinal)
                    Assert-Control ($position -gt 0 -and $position -eq $driver.LastIndexOf($needle, [StringComparison]::Ordinal))
                    $priorDriver = $driver.Remove(($position + $expression.Length), 1)
                    $driverHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($priorDriver))).ToLowerInvariant()
                    Assert-Control ($driverHash -ceq '0b89a4203522126f41e307b6058bc1bfa3d81bf3b091e85e495a56394d465d08')
                }
            }
            529 {
                $expression = '@@c++(@$peb->NtGlobalFlag)'
                foreach ($replacement in @(
                    @(($expression + '; }; .echo'), ($expression + ' }; .echo')),
                    @(($expression + '; }; .echo'), ($expression + ';; }; .echo')),
                    @(($expression + '; }; .echo'), ($expression + ', }; .echo')),
                    @('sizeof(@$peb->NtGlobalFlag)) == 4', 'sizeof(@$peb->NtGlobalFlag)) == 8')
                )) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        $arm = (Get-TraceSelectedSources $extension)['startup-arm.cdb']
                        $changed = $arm.Replace($replacement[0], $replacement[1])
                        Assert-Control ($changed -cne $arm)
                        $pin = [IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes($changed), $false)
                        $pins.Add($pin)
                        $hash = Get-ControlCommandHash $pin
                        $state.CommandPins['startup-arm.cdb'] = $pin
                        $state.ArmCommandSha256 = $hash
                        $state.ScopedPublication.ArmCommandSha256 = $hash
                        $state.CreatedBinding.Scoped.ArmCommandSha256 = $hash
                        $refused = $false
                        try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                        Assert-Control ($refused -and $physical.Writes -eq 0 -and $state.InputTasks.Count -eq 0 `
                            -and -not $state.ArmAttempted -and -not $state.Acked -and $state.ScopedStages -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
                $sources = Get-ControlScopedQuerySources $extension
                foreach ($region in @('Whole', 'Read')) {
                    $position = $sources[$region].IndexOf(($expression + ';'), [StringComparison]::Ordinal) + $expression.Length
                    Assert-Control ($sources[$region][$position] -ceq ';')
                    foreach ($delimiter in @('', ';;', ',')) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        try {
                            Invoke-ControlScopedPublisher
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                            Add-HermesStartupLine $state $sources.Whole
                            Assert-Control ($state.Protocol -eq 0 -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1)
                            $line = $sources[$region].Remove($position, 1).Insert($position, $delimiter)
                            Add-HermesStartupLine $state $line
                            $report = New-HermesStartupReport $state $false $false
                            Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                            Invoke-ControlScopedPublisher
                            Assert-Control ($state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 1 `
                                -and $report.observations.scopedReadFirstKind -eq 1 -and $report.observations.scopedReadFirstSource -eq 0 `
                                -and $report.observations.scopedReadFirstTrimKind -eq 0 -and $state.ScopedStages -eq 1 `
                                -and $state.Inventory.Block.Flags -eq -1 -and $physical.Writes -eq 1 `
                                -and $state.InputTasks.Count -eq 1 -and -not $state.Acked -and -not $state.Continued)
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            530 {
                foreach ($outcome in @('FieldMissing', 'SdkMissing', 'QueryMissing', 'ZeroFlags', 'OtherFlags', 'Full')) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        Invoke-ControlScopedPublisher
                        Add-HermesStartupLine $state (Get-ControlScopedQuerySources $extension).Whole
                        Assert-Control ($state.ScopedStages -eq 0 -and $null -eq $state.Arm -and $physical.Writes -eq 1)
                        # A guard that emits no typed field remains unarmed even
                        # with both completions; synthetic output, not CDB width proof.
                        $flags = switch ($outcome) { FieldMissing { '' }; ZeroFlags { '00000000' }; OtherFlags { '00000001' }; default { '00000002' } }
                        $text = Get-ControlScopedReadback -Flags $flags
                        if ($outcome -ceq 'SdkMissing') { $text = $text.Replace("HERMES_ARM_STAGE v=1 stage=1`n", '') }
                        if ($outcome -ceq 'QueryMissing') { $text = $text.Replace("HERMES_ARM_STAGE v=1 stage=2`n", '') }
                        Add-HermesStartupText $state $state.Out $text $true
                        $allowed = $outcome -ceq 'Full'
                        Assert-Control ((Test-HermesStartupScopedAction $state Target) -eq $allowed `
                            -and $state.InputTasks.Count -eq 1 -and -not $state.Acked -and -not $state.Held.Released `
                            -and [Text.Encoding]::ASCII.GetString($physical.ToArray()) -ceq
                                ('$$<' + (Join-Path $root 'startup-arm.cdb') + "`n"))
                        Invoke-ControlScopedPublisher
                        Assert-Control ($physical.Writes -eq (1 + [int]$allowed) `
                            -and $state.InputTasks.Count -eq (1 + [int]$allowed) `
                            -and $state.Acked -eq $allowed -and $state.Continued -eq $allowed `
                            -and $state.CommandPins.Count -eq 5 -and $state.SourcePins.Count -eq 2)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            531 {
                & {
                    $driver = [IO.File]::ReadAllText($driverAst.Extent.File).Replace($evaluatorSourceInsertion, '')
                    $prior = $driver
                    foreach ($edit in @(
                        @("@('_NT_EXPR_EVAL', '_NT_SYMBOL_PATH',", "@('_NT_SYMBOL_PATH',"),
                        @("@('-ee', 'masm', '-o', '-cf',", "@('-o', '-cf',")
                    )) {
                        Assert-Control ([regex]::Matches($driver, [regex]::Escape($edit[0])).Count -eq 1 `
                            -and -not $driver.Contains($edit[1], [StringComparison]::Ordinal))
                        $prior = $prior.Replace($edit[0], $edit[1])
                    }
                    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($prior))).ToLowerInvariant()
                    Assert-Control ($hash -ceq '2c8e5c3e07e6f066d251f0b074ea413ff17fc043bef16c4d8d7e82f4fb3c6533' `
                        -and $prior.Replace("@('_NT_SYMBOL_PATH',", "@('_NT_EXPR_EVAL', '_NT_SYMBOL_PATH',").
                            Replace("@('-o', '-cf',", "@('-ee', 'masm', '-o', '-cf',") -ceq $driver)
                }
            }
            { $_ -in 532..533 } {
                & {
                    # Only actual preparation statements, bounded by the unique
                    # constructor and argument loop. No process is constructed or
                    # started; these controls attest configuration, not CDB state.
                    $argumentLoops = @($driverTry.Body.Statements | Where-Object {
                        $_ -is [Management.Automation.Language.ForEachStatementAst] `
                            -and $_.Variable.Extent.Text -ceq '$argument'
                    })
                    Assert-Control ($argumentLoops.Count -eq 1)
                    $preparation = @($driverTry.Body.Statements | Where-Object {
                        $_.Extent.StartOffset -ge $startBinding[0].Extent.StartOffset `
                            -and $_.Extent.EndOffset -le $argumentLoops[0].Extent.EndOffset
                    })
                    Assert-Control ($preparation.Count -eq 11 `
                        -and $preparation[0] -eq $startBinding[0] -and $preparation[-1] -eq $argumentLoops[0] `
                        -and $preparation[0].Extent.Text -ceq '$start = [Diagnostics.ProcessStartInfo]::new()' `
                        -and $argumentLoops[0].Body.Statements.Count -eq 1 `
                        -and $argumentLoops[0].Body.Statements[0].Extent.Text -ceq '$start.ArgumentList.Add($argument)')
                    $text = ($preparation | ForEach-Object { $_.Extent.Text }) -join "`n"
                    $errors = $null
                    $preparationAst = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$errors)
                    $calls = @($preparationAst.FindAll({
                        param($n)
                        $n -is [Management.Automation.Language.CommandAst] `
                            -or $n -is [Management.Automation.Language.InvokeMemberExpressionAst]
                    }, $true) | ForEach-Object { $_.Extent.Text })
                    Assert-Control ($errors.Count -eq 0 -and $calls.Count -eq 4 `
                        -and $calls[0] -ceq '[Diagnostics.ProcessStartInfo]::new()' `
                        -and $calls[1] -ceq "Join-Path `$kits 'cdb.exe'" `
                        -and $calls[2] -ceq '$start.Environment.Remove($name)' `
                        -and $calls[3] -ceq '$start.ArgumentList.Add($argument)')
                    $body = ($preparation[1..10] | ForEach-Object { $_.Extent.Text }) -join "`n"
                    $variants = @($body)
                    if ($case -eq 533) {
                        $variants = @(foreach ($mutation in @(
                            @("'_NT_EXPR_EVAL', ", ''),
                            @("'-ee', 'masm', ", ''),
                            @("'-ee', 'masm', ", "'-ee', 'c++', "),
                            @("'-ee', 'masm', ", "'-ee', 'invalid', "),
                            @("'-ee', 'masm', ", "'-ee', 'masm', '-ee', 'masm', "),
                            @("'_NT_SYMBOL_PATH', ", ''),
                            @('$start.RedirectStandardError = $true', '$start.RedirectStandardError = $false')
                        )) {
                            Assert-Control ([regex]::Matches($body, [regex]::Escape($mutation[0])).Count -eq 1)
                            $body.Replace($mutation[0], $mutation[1])
                        })
                        $variants += $body.Replace("'-ee', 'masm', ", '').
                            Replace("'startup-debug-probe'))", "'startup-debug-probe', '-ee', 'masm'))")
                    }
                    $kits = Join-Path $PSScriptRoot 'selected sdk'
                    $helper = Join-Path $PSScriptRoot 'fixed published helper.exe'
                    $commandPath = Join-Path $root 'startup.cdb'
                    $expected = @('-ee', 'masm', '-o', '-cf', $commandPath, '-y',
                        "srv*$symbols*https://msdl.microsoft.com/download/symbols", $helper, 'startup-debug-probe')
                    foreach ($variant in $variants) {
                        Assert-Control (($variant -ceq $body) -eq ($case -eq 532))
                        foreach ($inherited in @('c++', 'masm', 'invalid')) {
                            . ([scriptblock]::Create($preparation[0].Extent.Text))
                            Assert-Control ($start -is [Diagnostics.ProcessStartInfo])
                            # Seed only this synthetic child dictionary, never
                            # the real host environment or an environment provider.
                            $start.Environment.Clear()
                            $start.Environment['HERMES_STARTUP_CONTROL'] = 'retained'
                            foreach ($name in @('_NT_EXPR_EVAL', '_NT_SYMBOL_PATH', '_NT_ALT_SYMBOL_PATH',
                                    '_NT_DEBUGGER_EXTENSION_PATH', '_NT_SOURCE_PATH', '_NT_EXECUTABLE_IMAGE_PATH')) {
                                $start.Environment[$name] = $inherited
                            }
                            . ([scriptblock]::Create($variant))
                            $configurationMatches = $start.Environment.Count -eq 1 `
                                -and $start.Environment['HERMES_STARTUP_CONTROL'] -ceq 'retained' `
                                -and $start.ArgumentList.Count -eq $expected.Count `
                                -and ($start.ArgumentList -join "`0") -ceq ($expected -join "`0") `
                                -and @($start.ArgumentList | Where-Object { $_ -ceq '-ee' }).Count -eq 1 `
                                -and @($start.ArgumentList | Where-Object { $_ -ceq 'masm' }).Count -eq 1 `
                                -and $start.Arguments -ceq '' `
                                -and $start.FileName -ceq (Join-Path $kits 'cdb.exe') `
                                -and $start.WorkingDirectory -ceq $kits -and -not $start.UseShellExecute `
                                -and $start.CreateNoWindow -and $start.RedirectStandardInput `
                                -and $start.StandardInputEncoding.CodePage -eq 20127 `
                                -and $start.RedirectStandardOutput -and $start.RedirectStandardError
                            Assert-Control ($configurationMatches -eq ($case -eq 532))
                        }
                    }
                }
            }
            534 {
                & {
                    $driver = [IO.File]::ReadAllText($driverAst.Extent.File)
                    Assert-Control ([regex]::Matches($driver, [regex]::Escape($evaluatorSourceInsertion)).Count -eq 1)
                    $priorDriver = $driver.Replace($evaluatorSourceInsertion, '')
                    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::UTF8.GetBytes($priorDriver))).ToLowerInvariant()
                    Assert-Control ($hash -ceq '40954849d631c5c31b0480639001c481b247669a0173a62ed8e86d676567ceb3' `
                        -and $priorDriver.Insert($driver.IndexOf($evaluatorSourceInsertion, [StringComparison]::Ordinal),
                            $evaluatorSourceInsertion) -ceq $driver)
                    function Assert-TraceCommandPath { param([string]$Path) return $Path }
                    function Join-Path { param([string]$Path, [string]$ChildPath) return $Path + '\' + $ChildPath }
                    $root = 'C:\owned\command-root'
                    $arm = (New-ScopedCreateCommands 'C:\Program Files (x86)\Windows Kits\10\Debuggers\x64\winxp\exts.dll')['startup-arm.cdb']
                    $fixed = ".expr /s masm`r`n"
                    Assert-Control ([regex]::Matches($arm, [regex]::Escape($fixed)).Count -eq 1)
                    $prior = $arm.Replace($fixed, '')
                    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                        [Text.Encoding]::ASCII.GetBytes($prior))).ToLowerInvariant()
                    Assert-Control ($hash -ceq '6a9d37c362b28e73a9f7f2061a9c2768f1234cdf7588922c5b06fe4db55e0099' `
                        -and $prior.Insert($arm.IndexOf($fixed, [StringComparison]::Ordinal), $fixed) -ceq $arm `
                        -and ($arm -split "`r`n")[6] -ceq ($prior -split "`r`n")[5])
                }
                foreach ($replacement in @('', ".expr /s c++`r`n", ".expr /s masm`r`n.expr /s masm`r`n")) {
                    $state = New-ControlScopedCreated
                    $physical = [StartupControlWriteStream]::new(0)
                    $writer = [IO.StreamWriter]::new($physical)
                    $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                    try {
                        $arm = (Get-TraceSelectedSources $extension)['startup-arm.cdb']
                        $changed = $arm.Replace(".expr /s masm`r`n", $replacement)
                        $pin = [IO.MemoryStream]::new([Text.Encoding]::ASCII.GetBytes($changed), $false)
                        $pins.Add($pin)
                        $hash = Get-ControlCommandHash $pin
                        $state.CommandPins['startup-arm.cdb'] = $pin
                        $state.ArmCommandSha256 = $hash
                        $state.ScopedPublication.ArmCommandSha256 = $hash
                        $state.CreatedBinding.Scoped.ArmCommandSha256 = $hash
                        $refused = $false
                        try { Invoke-ControlScopedPublisher } catch { $refused = $true }
                        Assert-Control ($changed -cne $arm -and $refused -and $physical.Writes -eq 0 `
                            -and $state.InputTasks.Count -eq 0 -and -not $state.ArmAttempted `
                            -and -not $state.Acked -and $state.ScopedEvaluator -eq 0)
                    } finally {
                        Close-HermesStartupScopedInput $state $writer
                        $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                    }
                }
            }
            535 {
                foreach ($evaluator in @(0, 1, 2)) {
                    foreach ($outcome in @('Full', 'MissingField', 'QueryFault')) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        try {
                            Invoke-ControlScopedPublisher
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                            Add-HermesStartupLine $state '.expr /s masm'
                            Assert-Control ($state.ScopedEvaluator -eq 0 -and -not $state.ScopedEchoes.Contains('.expr /s masm'))
                            if ($evaluator -ne 0) {
                                $line = if ($evaluator -eq 1) { $evaluatorMasm } else { $evaluatorCpp }
                                $split = [int]($line.Length / 2)
                                Add-HermesStartupText $state $state.Out $line.Substring(0, $split) $true
                                Assert-Control ($state.ScopedEvaluator -eq 0)
                                Add-HermesStartupText $state $state.Out ($line.Substring($split) + "`r`n") $true
                                Add-HermesStartupLine $state $(if ($evaluator -eq 1) { $evaluatorCpp } else { $evaluatorMasm })
                            }
                            Assert-Control ($state.ScopedEvaluator -eq $evaluator -and $state.Protocol -eq 0 `
                                -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1 `
                                -and -not (Test-HermesStartupScopedAction $state Target) -and -not $state.Acked `
                                -and $physical.Writes -eq 1 -and $state.InputTasks.Count -eq 1)
                            if ($outcome -ceq 'QueryFault') {
                                Add-HermesStartupLine $state 'unclassified .printf @@c++(@$peb->NtGlobalFlag); .echo HERMES_ARM_STAGE v=1 stage=2;'
                            } elseif ($outcome -ceq 'Full') {
                                Add-HermesStartupLine $state 'HERMES_NTGLOBALFLAG v=1 pid=22 value=00000002'
                            }
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_ARM_STAGE v=1 stage=2`nHERMES_CDB v=4 kind=8 pid=22`n1:000> ") $true
                            $allowed = $outcome -ceq 'Full'
                            Assert-Control ((Test-HermesStartupScopedAction $state Target) -eq $allowed `
                                -and -not $state.Acked -and $physical.Writes -eq 1)
                            Invoke-ControlScopedPublisher
                            $report = if ($allowed) {
                                Complete-ControlScoped $state $writer
                            } else {
                                New-HermesStartupReport $state $false $false
                            }
                            Assert-Control ($report.observations.scopedEvaluator -eq $evaluator `
                                -and $report.completed -eq [int]$allowed -and $report.nativeAcceptance -eq 0 `
                                -and $report.originalGatePassed -eq 0)
                            if ($outcome -ceq 'QueryFault') {
                                Assert-Control ($report.observations.scopedReadFirstKind -eq 1 `
                                    -and $report.observations.scopedReadFirstSource -eq 0 `
                                    -and $report.observations.scopedReadFirstTrimKind -eq 0 `
                                    -and $report.observations.scopedReadFirstLexical -eq 128 `
                                    -and $report.observations.scopedStageFirstReason -eq 1 `
                                    -and $report.observations.scopedStageFirstShape -eq 4 `
                                    -and $state.Protocol -eq 1 -and $state.ScopedStages -eq 1 `
                                    -and $state.Inventory.Block.Flags -eq -1 -and $physical.Writes -eq 1)
                            } elseif (-not $allowed) {
                                Assert-Control ($state.Protocol -eq 0 -and $state.ScopedStages -eq 3 `
                                    -and $state.Arm.Flags -eq -1 -and $physical.Writes -eq 1)
                            }
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            536 {
                foreach ($evaluatorLine in @($evaluatorMasm, $evaluatorCpp)) {
                    foreach ($context in @(
                        'BeforeArm', 'NoBlock', 'BeforeSdk', 'AfterQuery', 'AfterTyped', 'Stopped',
                        'WrongPid', 'WrongEvent', 'WrongVersion', 'Released', 'ChangedSource', 'ChangedReady', 'Stderr'
                    )) {
                        $state = New-ControlScopedCreated
                        $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        try {
                            Invoke-ControlScopedPublisher
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                            switch ($context) {
                                BeforeArm { $state.ArmWritten = $false }
                                NoBlock { $state.Inventory.Block = $null }
                                BeforeSdk { $state.ScopedStages = 0; $state.Inventory.Block.Stages = 0 }
                                AfterQuery { $state.ScopedStages = 3; $state.Inventory.Block.Stages = 3 }
                                AfterTyped { $state.Inventory.Block.Flags = [long]2 }
                                Stopped { $state.Stopped = $true }
                                WrongPid { $state.Inventory.Block.Pid = [uint32]33 }
                                WrongEvent { $state.Inventory.Block.EventPid = [uint32]33 }
                                WrongVersion { $state.Inventory.Block.Version = 3 }
                                Released { $state.Held.Released = $true }
                                ChangedSource { $state.ScopedInvocation.Source.ModuleSha256 = '0' * 64 }
                                ChangedReady { $state.TargetText = $state.TargetText.Replace('born=123', 'born=124') }
                            }
                            $stream = if ($context -ceq 'Stderr') { $state.Err } else { $state.Out }
                            if ($context -ceq 'Stopped') {
                                Add-HermesStartupLine $state $evaluatorLine
                            } else {
                                Add-HermesStartupText $state $stream ($evaluatorLine + "`n") ($context -cne 'Stderr')
                            }
                            Assert-Control ($state.ScopedEvaluator -eq 0 `
                                -and (New-HermesStartupReport $state $false $false).observations.scopedEvaluator -eq 0 `
                                -and -not $state.Acked -and $state.InputTasks.Count -eq 1)
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                try {
                    Invoke-ControlScopedPublisher
                    Add-HermesStartupText $state $state.Out (
                        "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                    foreach ($line in @(
                        '.expr /s masm', '1:000> .expr /s masm', ('1:000> ' + $evaluatorMasm),
                        (' ' + $evaluatorMasm), ($evaluatorMasm + ' '), $evaluatorMasm.ToLowerInvariant(),
                        ($evaluatorMasm + "`t"), ('"' + $evaluatorMasm + '"'), ($evaluatorCpp + ';'),
                        'Current expression evaluator: private C:\secret S-1-5-21', 'Current expression evaluator: MASM'
                    )) {
                        Add-HermesStartupLine $state $line
                        Assert-Control ($state.ScopedEvaluator -eq 0 -and $state.Protocol -eq 0 `
                            -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1)
                    }
                    $json = New-HermesStartupReport $state $false $false | ConvertTo-Json -Depth 4 -Compress
                    Assert-Control ($json -notmatch 'Current expression|private|secret|S-1-|\.expr|Microsoft Assembler|C\+\+ source')
                } finally {
                    Close-HermesStartupScopedInput $state $writer
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
                foreach ($mode in @('Trace', 'Inventory', 'ScopedCreateV1')) {
                    $state = Native-StartupTrace\New-HermesStartupState -DiagnosticMode $mode
                    Add-HermesStartupLine $state $evaluatorMasm
                    Add-HermesStartupLine $state $evaluatorCpp
                    Assert-Control ($state.ScopedEvaluator -eq 0 -and $state.Protocol -eq 0)
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            537 {
                foreach ($captured in @($false, $true)) {
                    foreach ($boundary in @(
                        'Protocol', 'Unknown', 'Error', 'Timeout', 'Cleanup', 'Rows', 'Trace', 'Kill',
                        'StageFault', 'QueryFault', 'SdkError', 'LastByte', 'Bytes', 'Bounds', 'Stopped',
                        'OutEnd', 'OutFault', 'ErrFault', 'InputClosed', 'Frozen'
                    )) {
                        if (-not $captured -and $boundary -ceq 'Frozen') { continue }
                        $state = New-ControlScopedCreated
                        $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        $module = Get-Module Native-StartupTrace
                        try {
                            Invoke-ControlScopedPublisher
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                            if ($captured) { Add-HermesStartupLine $state $evaluatorMasm }
                            switch ($boundary) {
                                Protocol { $state.Protocol = 1 }
                                Unknown { $state.Unknown = 1 }
                                Error { $state.Error = 13 }
                                Timeout { $state.Timeout = 1 }
                                Cleanup { $state.CleanupFault = 13 }
                                Rows { $state.NativeRowsFault = 1 }
                                Trace { $state.TraceFaulted = $true }
                                Kill { $state.DebuggerKillRequested = 1 }
                                StageFault { $state.ScopedStageFault = @{ Reason = 1; Shape = 4 } }
                                QueryFault { $state.ScopedQueryFault = @{ Kind = 1; Source = 0; Lexical = 128; TrimKind = 0 } }
                                SdkError { $state.Observations.SdkCommandErrors = 1 }
                                LastByte { $state.AcceptedUtf8Bytes = 8388608 - [Text.Encoding]::UTF8.GetByteCount($evaluatorCpp + "`n") }
                                Bytes { $state.AcceptedUtf8Bytes = [long]8388608 }
                                Bounds { $state.Bounds = 1 }
                                Stopped { $state.Stopped = $true }
                                OutEnd { $state.Out.End = $true }
                                OutFault { $state.Out.Faulted = $true }
                                ErrFault { $state.Err.Faulted = $true }
                                InputClosed { $state.InputClosed = $true }
                            }
                            $rows = $state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress
                            $fault = $state.ScopedFault | ConvertTo-Json -Compress
                            $query = $state.ScopedQueryFault | ConvertTo-Json -Compress
                            & $module {
                                $script:controlEvaluatorOriginalFrame = (Get-Item Function:Test-HermesStartupScopedArmFrame).ScriptBlock
                                Set-Item Function:script:Test-HermesStartupScopedArmFrame { throw 'No late evaluator frame parsing.' }
                            }
                            if ($boundary -ceq 'LastByte') {
                                Add-HermesStartupText $state $state.Out ($evaluatorCpp + "`n") $true
                                Assert-Control ($state.AcceptedUtf8Bytes -eq 8388608 -and $state.Bounds -eq 1)
                            } else {
                                Add-HermesStartupLine $state $evaluatorCpp
                            }
                            $report = New-HermesStartupReport $state $false $false
                            Assert-Control ($state.ScopedEvaluator -eq [int]$captured `
                                -and $report.observations.scopedEvaluator -eq [int]$captured `
                                -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $fault `
                                -and ($state.ScopedQueryFault | ConvertTo-Json -Compress) -ceq $query `
                                -and ($state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress) -ceq $rows `
                                -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1 `
                                -and -not $state.Acked -and $state.InputTasks.Count -eq 1)
                            if ($boundary -cin @('LastByte', 'Bytes', 'Bounds')) {
                                $before = Get-ControlStreamSnapshot $state.Out
                                Add-HermesStartupText $state $state.Out ($evaluatorMasm + "`n") $true
                                Assert-Control ((Get-ControlStreamSnapshot $state.Out) -ceq $before `
                                    -and $state.ScopedEvaluator -eq [int]$captured)
                            }
                        } finally {
                            & $module {
                                if (Test-Path Variable:script:controlEvaluatorOriginalFrame) {
                                    Set-Item Function:script:Test-HermesStartupScopedArmFrame $script:controlEvaluatorOriginalFrame
                                    Remove-Variable controlEvaluatorOriginalFrame -Scope Script
                                }
                            }
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            538 {
                $state = New-ControlScopedCreated
                foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                foreach ($stream in @($state.Out, $state.Err)) {
                    $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                }
                $state.AcceptedUtf8Bytes = [long]8388608; $state.Bounds = 1
                $state.ScopedStages = 3; $state.ScopedPhase = 15; $state.SideWrites = 48
                $state.ScopedFault = @{ Reason = 15; Phase = 15; Block = 7; Held = 5; Echo = 255 }
                $state.ScopedStageFault = @{ Reason = 5; Shape = 1023 }
                $state.ScopedQueryFault = @{ Kind = 6; Source = 2; Lexical = 1023; TrimKind = 5 }
                $state.ScopedEvaluator = 2; $state.DebuggerKillRequested = 1
                foreach ($subject in 100..114) {
                    $state.Inventory.Subjects[[string]$subject] = @{ Create = 1; Initial = 1; Exit = 1; ExitCode = 0 }
                }
                while ($state.InputTasks.Count -lt 50) { $state.InputTasks.Add([Threading.Tasks.Task]::CompletedTask) }
                Set-HermesStartupDebuggerExit $state ([int]-1)
                $report = New-HermesStartupReport $state $false $false
                $json = $report | ConvertTo-Json -Depth 4 -Compress
                Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                    -and $report.observations.scopedEvaluator -eq 2 -and $report.observations.scopedReadFirstLexical -eq 1023 `
                    -and $report.observations.subjects -eq 16 -and $report.observations.inputTasks -eq 50 `
                    -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                    -and $json -notmatch 'Current expression|_NT_EXPR_EVAL|private|secret|S-1-|\.expr|Microsoft Assembler|C\+\+ source')
                foreach ($bad in @(-1, 3, '1', $true, $null)) {
                    $state.ScopedEvaluator = $bad
                    Assert-Control ((New-HermesStartupReport $state $false $false).observations.scopedEvaluator -eq 0)
                }
            }
            539 {
                $words = @(
                    @{ Text = 'error'; Bit = 1 }, @{ Text = 'not enough arguments'; Bit = 2 },
                    @{ Text = 'extra character'; Bit = 4 }, @{ Text = 'unable to read'; Bit = 8 },
                    @{ Text = 'could not be read'; Bit = 16 }, @{ Text = 'type mismatch'; Bit = 32 },
                    @{ Text = 'format'; Bit = 64 }, @{ Text = 'missing'; Bit = 128 },
                    @{ Text = 'error not enough arguments extra character unable to read could not be read type mismatch format missing'; Bit = 255 }
                )
                foreach ($proof in @('None', 'Echo', 'Cpp', 'Masm')) {
                    foreach ($word in $words) {
                        foreach ($text in @($word.Text.ToLowerInvariant(), $word.Text.ToUpperInvariant())) {
                            $state = New-ControlScopedCreated
                            $physical = [StartupControlWriteStream]::new(0)
                            $writer = [IO.StreamWriter]::new($physical)
                            $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                            try {
                                Invoke-ControlScopedPublisher
                                Add-HermesStartupText $state $state.Out (
                                    "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                                switch ($proof) {
                                    Echo { Add-HermesStartupLine $state '.expr /s masm' }
                                    Cpp { Add-HermesStartupLine $state $evaluatorCpp }
                                    Masm { Add-HermesStartupLine $state $evaluatorMasm }
                                }
                                $expectedEvaluator = switch ($proof) { Cpp { 2 }; Masm { 1 }; default { 0 } }
                                Assert-Control ($state.ScopedEvaluator -eq $expectedEvaluator -and $state.ScopedQueryFault.Kind -eq 0)
                                $line = 'private ' + $text + ' .printf @@c++(@$peb->NtGlobalFlag)'
                                Add-HermesStartupText $state $state.Out ($line + "`n") $true
                                $report = New-HermesStartupReport $state $false $false
                                Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                                Invoke-ControlScopedPublisher
                                $expectedWording = if ($proof -ceq 'Masm') { $word.Bit } else { 0 }
                                Assert-Control ($report.observations.scopedReadFirstWording -eq $expectedWording `
                                    -and $report.observations.scopedReadFirstToken -eq 0 `
                                    -and $report.observations.scopedReadFirstKind -eq 1 `
                                    -and $report.observations.scopedReadFirstSource -eq 0 `
                                    -and $report.observations.scopedReadFirstLexical -eq 128 `
                                    -and $report.observations.scopedReadFirstTrimKind -eq 0 `
                                    -and $report.observations.scopedEvaluator -eq $expectedEvaluator `
                                    -and $state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 1 `
                                    -and $state.ScopedStageFault.Reason -eq 0 -and $state.ScopedStages -eq 1 `
                                    -and $state.Inventory.Block.Flags -eq -1 -and $physical.Writes -eq 1 `
                                    -and $state.InputTasks.Count -eq 1 -and -not $state.Acked -and -not $state.Continued `
                                    -and -not (Test-HermesStartupScopedAction $state Target) `
                                    -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0)
                            } finally {
                                Close-HermesStartupScopedInput $state $writer
                                $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                            }
                        }
                    }
                }
            }
            540 {
                foreach ($entry in @(
                    @{ Prefix = '{'; Token = 1; Word = 0 }, @{ Prefix = '}'; Token = 2; Word = 0 },
                    @{ Prefix = '.printf '; Token = 3; Word = 0 }, @{ Prefix = '.catch{'; Token = 4; Word = 0 },
                    @{ Prefix = '.if('; Token = 5; Word = 0 }, @{ Prefix = 'Syntax '; Token = 6; Word = 0 },
                    @{ Prefix = 'Error:'; Token = 7; Word = 1 }, @{ Prefix = 'Unable '; Token = 8; Word = 0 },
                    @{ Prefix = '.printfOther '; Token = 0; Word = 0 }, @{ Prefix = '.catchOther '; Token = 0; Word = 0 },
                    @{ Prefix = '.ifOther '; Token = 0; Word = 0 }, @{ Prefix = '.PRINTF '; Token = 0; Word = 0 },
                    @{ Prefix = 'syntax '; Token = 0; Word = 0 }, @{ Prefix = 'error '; Token = 0; Word = 1 },
                    @{ Prefix = 'unable '; Token = 0; Word = 0 }, @{ Prefix = 'ErrorOther '; Token = 0; Word = 1 },
                    @{ Prefix = 'SyntaxOther '; Token = 0; Word = 0 }, @{ Prefix = 'UnableOther '; Token = 0; Word = 0 },
                    @{ Prefix = '"'; Token = 0; Word = 0 }, @{ Prefix = "'"; Token = 0; Word = 0 },
                    @{ Prefix = '^ '; Token = 0; Word = 0 }, @{ Prefix = 'prefix { '; Token = 0; Word = 0 },
                    @{ Prefix = ([string][char]0xa0 + '{'); Token = 0; Word = 0 },
                    @{ Prefix = ([string][char]11 + '{'); Token = 0; Word = 0 }
                )) {
                    foreach ($edge in @(
                        @{ Before = ''; After = ''; Lexical = 128 },
                        @{ Before = " `t"; After = "`t"; Lexical = 640 },
                        @{ Before = "1:000>  `t"; After = ' '; Lexical = 640 }
                    )) {
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        try {
                            Invoke-ControlScopedPublisher
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                            Add-HermesStartupLine $state $evaluatorMasm
                            Add-HermesStartupLine $state (Get-ControlScopedQuerySources $extension).Whole
                            Assert-Control ($state.ScopedQueryFault.Kind -eq 0 -and $state.ScopedQueryFault.Wording -eq 0 `
                                -and $state.ScopedQueryFault.Token -eq 0 -and $state.Protocol -eq 0)
                            $line = $edge.Before + $entry.Prefix + 'private C:\secret S-1-5-21 .printf @@c++(@$peb->NtGlobalFlag)' + $edge.After
                            $expectedLexical = $edge.Lexical -bor $(if ($entry.Prefix -ceq '^ ') { 256 } else { 0 })
                            Assert-Control (-not $state.ScopedEchoes.Contains($line))
                            Add-HermesStartupLine $state $line
                            $report = New-HermesStartupReport $state $false $false
                            $json = $report | ConvertTo-Json -Depth 4 -Compress
                            Add-HermesStartupText $state $state.Out (Get-ControlScopedReadback) $true
                            Invoke-ControlScopedPublisher
                            Assert-Control ($report.observations.scopedReadFirstToken -eq $entry.Token `
                                -and $report.observations.scopedReadFirstWording -eq $entry.Word `
                                -and $report.observations.scopedReadFirstKind -eq 1 `
                                -and $report.observations.scopedReadFirstSource -eq 0 `
                                -and $report.observations.scopedReadFirstTrimKind -eq 0 `
                                -and $report.observations.scopedReadFirstLexical -eq $expectedLexical `
                                -and $report.observations.scopedEvaluator -eq 1 `
                                -and $state.Protocol -eq 1 -and $state.ScopedFault.Reason -eq 1 `
                                -and $state.ScopedStages -eq 1 -and $state.Inventory.Block.Flags -eq -1 `
                                -and $physical.Writes -eq 1 -and $state.InputTasks.Count -eq 1 `
                                -and -not $state.Acked -and -not $state.Held.Released `
                                -and $report.completed -eq 0 -and $report.armingVerified -eq 0 `
                                -and $json -notmatch 'private|secret|S-1-|NtGlobalFlag|\.printf|\.catch|\.if|Current expression')
                        } finally {
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
            }
            541 {
                foreach ($captured in @($false, $true)) {
                    foreach ($boundary in @(
                        'Frozen', 'Protocol', 'ScopedFault', 'Unknown', 'Error', 'Timeout', 'Cleanup',
                        'Rows', 'Trace', 'Kill', 'Stopped', 'LastByte', 'Bytes', 'Bounds', 'SdkError', 'NoSdk', 'Flags'
                    )) {
                        if (-not $captured -and $boundary -ceq 'Frozen') { continue }
                        $state = New-ControlScopedCreated
                        $physical = [StartupControlWriteStream]::new(0)
                        $writer = [IO.StreamWriter]::new($physical)
                        $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                        $module = Get-Module Native-StartupTrace
                        try {
                            Invoke-ControlScopedPublisher
                            Add-HermesStartupText $state $state.Out (
                                "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                            Add-HermesStartupLine $state $evaluatorMasm
                            if ($captured) {
                                Add-HermesStartupLine $state 'Unable to read extra character format missing .printf @@c++(@$peb->NtGlobalFlag)'
                                Assert-Control ($state.ScopedQueryFault.Wording -eq 204 -and $state.ScopedQueryFault.Token -eq 8)
                            }
                            $later = '{ error not enough arguments .printf private C:\secret S-1-5-21'
                            switch ($boundary) {
                                Protocol { $state.Protocol = 1 }
                                ScopedFault { $state.ScopedFault.Reason = 1 }
                                Unknown { $state.Unknown = 1 }
                                Error { $state.Error = 13 }
                                Timeout { $state.Timeout = 1 }
                                Cleanup { $state.CleanupFault = 13 }
                                Rows { $state.NativeRowsFault = 1 }
                                Trace { $state.TraceFaulted = $true }
                                Kill { $state.DebuggerKillRequested = 1 }
                                Stopped { $state.Stopped = $true }
                                LastByte { $state.AcceptedUtf8Bytes = 8388608 - [Text.Encoding]::UTF8.GetByteCount($later + "`n") }
                                Bytes { $state.AcceptedUtf8Bytes = [long]8388608 }
                                Bounds { $state.Bounds = 1 }
                                SdkError { $state.Observations.SdkCommandErrors = 1 }
                                NoSdk { $state.ScopedStages = 0; $state.Inventory.Block.Stages = 0 }
                                Flags { $state.Inventory.Block.Flags = [long]2 }
                            }
                            $first = $state.ScopedQueryFault | ConvertTo-Json -Compress
                            $fault = $state.ScopedFault | ConvertTo-Json -Compress
                            $stage = $state.ScopedStageFault | ConvertTo-Json -Compress
                            $rows = $state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress
                            & $module {
                                $script:controlClosureFrame = (Get-Item Function:Test-HermesStartupScopedArmFrame).ScriptBlock
                                $script:controlClosureRefusal = (Get-Item Function:Set-HermesStartupScopedQueryRefusal).ScriptBlock
                                Set-Item Function:script:Test-HermesStartupScopedArmFrame { throw 'No late refusal fingerprint frame work.' }
                            }
                            if ($boundary -ceq 'LastByte') {
                                Add-HermesStartupText $state $state.Out ($later + "`n") $true
                                Assert-Control ($state.AcceptedUtf8Bytes -eq 8388608 -and $state.Bounds -eq 1)
                            }
                            Set-HermesStartupScopedQueryRefusal $state $later
                            if ($boundary -cin @('LastByte', 'Bytes', 'Bounds')) {
                                $before = Get-ControlStreamSnapshot $state.Out
                                Add-HermesStartupText $state $state.Out ($later + "`n") $true
                                Assert-Control ((Get-ControlStreamSnapshot $state.Out) -ceq $before)
                            }
                            & $module {
                                Set-Item Function:script:Set-HermesStartupScopedQueryRefusal { throw 'Report must not reparse refusal fingerprints.' }
                            }
                            $state.ScopedEchoes.Clear()
                            $report = New-HermesStartupReport $state $false $false
                            $json = $report | ConvertTo-Json -Depth 4 -Compress
                            Assert-Control (($state.ScopedQueryFault | ConvertTo-Json -Compress) -ceq $first `
                                -and ($state.ScopedFault | ConvertTo-Json -Compress) -ceq $fault `
                                -and ($state.ScopedStageFault | ConvertTo-Json -Compress) -ceq $stage `
                                -and ($state.NativeRowSnapshots | ConvertTo-Json -Depth 4 -Compress) -ceq $rows `
                                -and $report.observations.scopedReadFirstWording -eq (204 * [int]$captured) `
                                -and $report.observations.scopedReadFirstToken -eq (8 * [int]$captured) `
                                -and $report.observations.scopedEvaluator -eq 1 -and $physical.Writes -eq 1 `
                                -and $state.InputTasks.Count -eq 1 -and -not $state.Acked -and -not $state.Continued `
                                -and $report.completed -eq 0 -and [Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                                -and $json -notmatch 'private|secret|S-1-|not enough arguments|extra character|NtGlobalFlag|\.printf')
                        } finally {
                            & $module {
                                if (Test-Path Variable:script:controlClosureFrame) {
                                    Set-Item Function:script:Test-HermesStartupScopedArmFrame $script:controlClosureFrame
                                    Set-Item Function:script:Set-HermesStartupScopedQueryRefusal $script:controlClosureRefusal
                                    Remove-Variable controlClosureFrame, controlClosureRefusal -Scope Script
                                }
                            }
                            Close-HermesStartupScopedInput $state $writer
                            $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                        }
                    }
                }
                $state = New-ControlScopedCreated
                $writer = [IO.StreamWriter]::new([StartupControlWriteStream]::new(0))
                $debugger = [pscustomobject]@{ HasExited = $false; StandardInput = $writer }
                try {
                    Invoke-ControlScopedPublisher
                    Add-HermesStartupText $state $state.Out (
                        "HERMES_CDB v=4 kind=7 pid=22`nLast event: 16.1: Create process 1:16`nHERMES_ARM_STAGE v=1 stage=1`n") $true
                    Add-HermesStartupLine $state $evaluatorMasm
                    Add-HermesStartupLine $state 'Unable to read error not enough arguments extra character could not be read type mismatch format missing .printf @@c++(@$peb->NtGlobalFlag)'
                    Assert-Control ($state.ScopedQueryFault.Wording -eq 255 -and $state.ScopedQueryFault.Token -eq 8 `
                        -and $state.ScopedQueryFault.Count -eq 6)
                    foreach ($name in @($state.Observations.Keys)) { $state.Observations[$name] = 8388608 }
                    foreach ($stream in @($state.Out, $state.Err)) {
                        $stream.Count = [long]8388608; $stream.Prefix = 'x' * 4096; $stream.Fingerprint = 'f' * 64
                    }
                    $state.AcceptedUtf8Bytes = [long]8388608; $state.Bounds = 1
                    $state.ScopedStages = 3; $state.ScopedPhase = 15; $state.SideWrites = 48
                    $state.ScopedFault = @{ Reason = 15; Phase = 15; Block = 7; Held = 5; Echo = 255 }
                    $state.ScopedStageFault = @{ Reason = 5; Shape = 1023 }
                    $state.ScopedQueryFault.Kind = 6; $state.ScopedQueryFault.Source = 2
                    $state.ScopedQueryFault.Lexical = 1023; $state.ScopedQueryFault.TrimKind = 5
                    $state.DebuggerKillRequested = 1
                    foreach ($subject in 100..114) {
                        $state.Inventory.Subjects[[string]$subject] = @{ Create = 1; Initial = 1; Exit = 1; ExitCode = 0 }
                    }
                    while ($state.InputTasks.Count -lt 50) { $state.InputTasks.Add([Threading.Tasks.Task]::CompletedTask) }
                    Set-HermesStartupDebuggerExit $state ([int]-1)
                    $report = New-HermesStartupReport $state $false $false
                    $json = $report | ConvertTo-Json -Depth 4 -Compress
                    Assert-Control ([Text.Encoding]::UTF8.GetByteCount($json + "`n") -le 4096 `
                        -and $report.observations.scopedReadFirstWording -eq 255 -and $report.observations.scopedReadFirstToken -eq 8 `
                        -and $report.observations.scopedReadFirstLexical -eq 1023 -and $report.observations.scopedEvaluator -eq 1 `
                        -and $report.observations.subjects -eq 16 -and $report.observations.inputTasks -eq 50 `
                        -and $report.completed -eq 0 -and $report.nativeAcceptance -eq 0 -and $report.originalGatePassed -eq 0 `
                        -and $json -notmatch 'Current expression|private|secret|S-1-|not enough arguments|extra character|\.printf')
                    foreach ($bad in @(-1, 256, '255', $true, $null)) {
                        $state.ScopedQueryFault.Wording = $bad
                        Assert-Control ((New-HermesStartupReport $state $false $false).observations.scopedReadFirstWording -eq 0)
                    }
                    foreach ($bad in @(-1, 9, '8', $true, $null)) {
                        $state.ScopedQueryFault.Token = $bad
                        Assert-Control ((New-HermesStartupReport $state $false $false).observations.scopedReadFirstToken -eq 0)
                    }
                } finally {
                    Close-HermesStartupScopedInput $state $writer
                    $state.Out.Hash.Dispose(); $state.Err.Hash.Dispose()
                }
            }
            { $_ -in 415..420 } {
                $closure = ($case - 415) % 3
                $flushFault = $case -ge 418
                $state = if ($closure -eq 1) {
                    New-ControlNativeStopped
                } else {
                    Native-StartupTrace\New-HermesStartupState -DiagnosticMode Inventory
                }
                $initialError = if ($flushFault -and $closure -ne 1) { 1460 } else { 0 }
                $state.Error = $initialError
                $pipe = New-ControlPipe
                $handle = $pipe.Writer.SafePipeHandle
                $encoding = [Text.ASCIIEncoding]::new()
                if ($flushFault) {
                    $encoding = [Text.Encoding]::GetEncoding('us-ascii',
                        [Text.EncoderFallback]::ExceptionFallback, [Text.DecoderFallback]::ExceptionFallback)
                }
                $writer = [IO.StreamWriter]::new($pipe.Writer, $encoding)
                try {
                    if ($flushFault) {
                        # Buffered invalid input makes the real writer's final
                        # flush fail before it can emit any extra pipe bytes.
                        $writer.Write([char]0x100)
                    } else { $writer.AutoFlush = $true }
                    $bytes = [byte[]]::new(3)
                    $pending = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                    Assert-Control (-not $pending.IsCompleted -and -not $handle.IsClosed)
                    if ($closure -eq 1) {
                        Invoke-HermesStartupContinue $state $writer
                    } else {
                        $debugger | Add-Member -NotePropertyMembers @{
                            StandardInput = $writer
                            StandardOutput = New-ControlReader ''
                            StandardError = New-ControlReader ''
                            HasExited = $true; ExitCode = 0
                        }
                        if ($closure -eq 0) {
                            Close-TraceInventoryInput
                        } else {
                            $started = $true
                            $script:controlReads = 0
                            $state.Out.End = $true; $state.Err.End = $true
                            $inputCleanup = [scriptblock]::Create(
                                'try {} finally ' + $driverTry.Finally.Extent.Text)
                            . $inputCleanup
                        }
                    }
                    Assert-Control ($state.InputClosed -eq (-not $flushFault) `
                        -and $state.CleanupFault -eq $(if ($flushFault) { 13 } else { 0 }) `
                        -and $state.Error -eq $initialError -and $handle.IsClosed `
                        -and $pipe.Reader.BaseStream.CanRead)
                    # These waits only watch for the real bytes/EOF. They do
                    # not close the peer, complete a task or provide fallback.
                    Assert-Control ($pending.Wait(5000))
                    $count = $pending.GetAwaiter().GetResult()
                    if ($closure -eq 1) {
                        Assert-Control ($count -eq 2 -and $bytes[0] -eq 103 -and $bytes[1] -eq 10 `
                            -and $state.Acked -and $state.Continued -and $state.ContinueAttempted `
                            -and $null -ne $state.InputPending -and $state.InputPending.IsCompletedSuccessfully)
                        $null = $state.InputPending.GetAwaiter().GetResult()
                        $eof = $pipe.Reader.BaseStream.ReadAsync($bytes, 0, $bytes.Length)
                        Assert-Control ($eof.Wait(5000) -and $eof.GetAwaiter().GetResult() -eq 0)
                    } else {
                        Assert-Control ($count -eq 0 -and -not $state.ContinueAttempted `
                            -and -not $state.Acked -and $null -eq $state.InputPending)
                        if ($closure -eq 2) {
                            Assert-Control ($script:controlStops -eq 1 -and $script:controlDisposed -eq 1 `
                                -and $debuggerEnded -and $drainsEnded)
                        }
                    }
                } finally {
                    $writer.Dispose()
                    $pipe.Writer.Dispose()
                    $pipe.Reader.Dispose()
                    if ($closure -ne 1) {
                        $debugger.StandardOutput.Dispose()
                        $debugger.StandardError.Dispose()
                    }
                }
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
