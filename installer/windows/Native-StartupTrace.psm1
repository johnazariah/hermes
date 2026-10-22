#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-HermesStartupStream {
    @{
        Count = [long]0; Prefix = ''; Tail = ''; DiscardLine = $false
        End = $false; Pending = $null; Source = $null; Faulted = $false; ReadClock = $null
        Bytes = [byte[]]::new(4096); Buffer = [char[]]::new(4096)
        Decoder = [Text.UTF8Encoding]::new($false, $true).GetDecoder()
        Hash = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        Fingerprint = ''
    }
}

function New-HermesStartupState {
    param(
        [switch]$LegacyParserControls,
        [ValidateSet('Trace', 'Inventory', 'ScopedCreateV1')][string]$DiagnosticMode = 'Trace'
    )
    if ($LegacyParserControls -and $DiagnosticMode -cne 'Trace') { throw 'Legacy inventory refused.' }
    $state = @{
        DiagnosticMode = $DiagnosticMode; PublishedMode = ''; NativeRowsFault = 0
        NativeRowSnapshots = @{ Ready = $null; Resumed = $null; Final = $null }
        CommandPins = @{}; SourcePins = @{}
        ScopedEchoes = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        ScopedPublication = $null; ScopedInvocation = $null; ExtensionSource = $null
        ScopedPhase = 0; ScopedStages = 0; ScopedEvaluator = 0
        ScopedFault = @{ Reason = 0; Phase = 0; Block = 0; Held = 0; Echo = 0 }
        ScopedStageFault = @{ Reason = 0; Shape = 0 }
        ScopedQueryFault = @{ Kind = 0; Source = 0; Lexical = 0; TrimKind = 0 }
        Held = $null; ArmAttempted = $false; ArmWritten = $false; SideWrites = 0
        InputWriter = $null; InputTasks = [Collections.Generic.List[Threading.Tasks.Task]]::new()
        DriverSha256 = ''; ModuleSha256 = ''; ArmCommandSha256 = ''; ExtensionSha256 = ''
        DebuggerExitAvailable = 0; DebuggerExitValue = [long]0; DebuggerKillRequested = 0
        Inventory = @{
            Root = [uint32]0; RootFault = 0; Block = $null; Subjects = @{}
            Malformed = 0; Mismatch = 0; Limit = 0
        }
        NativeProtocol = -not $LegacyParserControls
        Created = $null; CreatedBinding = $null; CreatedAcked = $false; CreateCloseClock = $null
        Resumed = $null; ResumedText = ''; RowsText = ''
        Stopped = $false; ContinueAttempted = $false; Continued = $false
        InputClosed = $false; InputPending = $null
        Initial = [uint32]0; Arm = $null; Block = $null; Exits = @{}
        Target = $null; Final = $null; TargetText = ''; FinalText = ''
        Acked = $false; ArmedBinding = $null
        Failure = $null; Unknown = 0; Protocol = 0
        Bounds = 0; AcceptedUtf8Bytes = [long]0
        Timeout = 0; Error = 0; CleanupFault = 0
        TracePoint = 0; ToolRole = 0; TraceFaulted = $false
        TraceFaultPoint = 0; TraceFaultRole = 0
        FilterPending = ''; FilterExpected = @{ Cpr = ''; Epr = ''; Ibp = '' }; ExtensionExpected = ''
        FilterVerified = @{ Cpr = $false; Epr = $false; Ibp = $false }
        IbpBreak = $false
        ChainPending = $false; ChainCollecting = $false
        Observations = @{
            Marker0 = 0; Marker1 = 0; Marker2 = 0; Marker3 = 0; Marker4 = 0
            MarkerMalformed = 0; MarkerMalformedKind = 0; MarkerEcho = 0
            LastEventSeen = 0; CreateEventSeen = 0; CreateEventAccepted = 0
            ExitEventSeen = 0; ExitEventAccepted = 0
            FlagsSeen = 0; FlagsAccepted = 0; SdkCommandErrors = 0
            SdkLoadLibraryWin32Error = 0; SdkLoadLibraryPathShape = 0
            SdkSelectedExtensionChain = 0; SdkLoadLibraryRelation = 0; SdkLoadLibraryForm = 0
            SdkLoadLibraryLexicalModule = 0; SdkLoadLibraryLexicalSuffix = 0
            SdkLoadLibraryDisplay = 0; SdkChainLines = 0; SdkChainSupportedRows = 0
            CprFilter = 0; EprFilter = 0
            CprCommandShape = 0; EprCommandShape = 0
            CprCommandTransform = 0; EprCommandTransform = 0
            CprPayloadLength = 0; EprPayloadLength = 0
            CprSourceLength = 0; EprSourceLength = 0
        }
        Out = (New-HermesStartupStream); Err = (New-HermesStartupStream)
        DebuggerSha256 = ''; HelperSha256 = ''; CommandSha256 = ''
        CreateCommandSha256 = ''; ExitCommandSha256 = ''; InitialCommandSha256 = ''
    }
    if ($state.NativeProtocol) {
        foreach ($name in @('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst',
                'CreatedCheckFirst', 'CreatedCheckSeen', 'CreatedPublishAttempted',
                'RowsReads', 'RowsMissing', 'RowsEmpty', 'RowsFirstDataLength',
                'RowsFirstDataRead', 'RowsReadyRead',
                'OutReadStartMax', 'ErrReadStartMax', 'CreateReadAge', 'CreateParseWork',
                'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay')) {
            $state.Observations[$name] = 0
        }
    }
    return $state
}

function Get-HermesStartupObservation {
    param(
        [hashtable]$State,
        [ValidateSet('Marker0', 'Marker1', 'Marker2', 'Marker3', 'Marker4',
            'MarkerMalformed', 'MarkerMalformedKind', 'MarkerEcho',
            'LastEventSeen', 'CreateEventSeen', 'CreateEventAccepted',
            'ExitEventSeen', 'ExitEventAccepted', 'FlagsSeen', 'FlagsAccepted', 'SdkCommandErrors',
            'SdkLoadLibraryWin32Error', 'SdkLoadLibraryPathShape',
            'SdkSelectedExtensionChain', 'SdkLoadLibraryRelation', 'SdkLoadLibraryForm',
            'SdkLoadLibraryLexicalModule', 'SdkLoadLibraryLexicalSuffix',
            'SdkLoadLibraryDisplay', 'SdkChainLines', 'SdkChainSupportedRows',
            'CprFilter', 'EprFilter', 'CprCommandShape', 'EprCommandShape',
            'CprCommandTransform', 'EprCommandTransform',
            'CprPayloadLength', 'EprPayloadLength', 'CprSourceLength', 'EprSourceLength',
            'ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst',
            'CreatedCheckFirst', 'CreatedCheckSeen', 'CreatedPublishAttempted',
            'RowsReads', 'RowsMissing', 'RowsEmpty', 'RowsFirstDataLength',
            'RowsFirstDataRead', 'RowsReadyRead',
            'OutReadStartMax', 'ErrReadStartMax', 'CreateReadAge', 'CreateParseWork',
            'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay')]
        [string]$Name,
        [long]$Maximum = 8388608
    )
    $value = $State.Observations[$Name]
    if (($value -is [int] -or ($Name -ceq 'SdkLoadLibraryWin32Error' -and $value -is [long])) `
        -and $value -ge 0 -and $value -le $Maximum) {
        return $value
    }
    return 0
}

function Add-HermesStartupObservation {
    param([hashtable]$State, [string]$Name)
    $State.Observations[$Name] = [Math]::Min(
        8388608, (Get-HermesStartupObservation $State $Name) + 1)
}

function Get-HermesStartupWorkBucket {
    param([long]$Milliseconds)
    # Monotonic duration classes only, never timestamps or reported durations:
    # 0 unavailable; 1 <=10 ms; 2 <=100 ms; 3 <=1 s; 4 <=5 s; 5 <=30 s; 6 >30 s.
    if ($Milliseconds -lt 0) { return 0 }
    $limits = @(10, 100, 1000, 5000, 30000)
    for ($index = 0; $index -lt $limits.Count; $index++) {
        if ($Milliseconds -le $limits[$index]) { return ($index + 1) }
    }
    return 6
}

function Set-HermesStartupWorkBucket {
    param(
        [hashtable]$State,
        [ValidateSet('OutReadStartMax', 'ErrReadStartMax', 'CreateReadAge', 'CreateParseWork',
            'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay')][string]$Name,
        [long]$Milliseconds,
        [switch]$First
    )
    if (-not $State.NativeProtocol -or $State.Bounds -ne 0 `
        -or $State.AcceptedUtf8Bytes -ge 8388608) { return }
    $previous = Get-HermesStartupObservation $State $Name 6
    if ($First -and $previous -ne 0) { return }
    $State.Observations[$Name] = [Math]::Max($previous, (Get-HermesStartupWorkBucket $Milliseconds))
}

function Test-HermesStartupTracePoint {
    param($Point, $ToolRole)
    return $Point -is [int] -and $ToolRole -is [int] `
        -and $Point -ge 0 -and $Point -le 26 `
        -and (($Point -ge 6 -and $Point -le 10 `
            -and $ToolRole -ge 1 -and $ToolRole -le 6 `
            -and ($Point -ne 10 -or $ToolRole -eq 1)) `
        -or (($Point -lt 6 -or $Point -gt 10) -and $ToolRole -eq 0))
}

function Set-HermesStartupTracePoint {
    param([hashtable]$State, $Point, $ToolRole = 0)
    if (-not (Test-HermesStartupTracePoint $Point $ToolRole)) {
        throw 'Trace point refused.'
    }
    $State.TracePoint = $Point
    $State.ToolRole = $ToolRole
}

function Set-HermesStartupTraceFault {
    param([hashtable]$State)
    if (-not $State.TraceFaulted) {
        $State.TraceFaultPoint = $State.TracePoint
        $State.TraceFaultRole = $State.ToolRole
        $State.TraceFaulted = $true
    }
    if ($State.Error -eq 0) { $State.Error = 13 }
}

function ConvertFrom-HermesStartupRow {
    param([string]$Line)
    $pattern = '\AHERMES_STARTUP v=(?<version>[12]) stage=(?<stage>[123]) helper=(?<helper>0|[1-9][0-9]{0,9}) pid=(?<pid>0|[1-9][0-9]{0,9}) born=(?<born>0|[1-9][0-9]{0,18}) image=(?<image>[01]) debug=(?<debug>[01]) context=(?<context>[01]) flags=134742020(?: resumed=(?<resumed>[01]))? exit=(?<exit>0|[1-9][0-9]{0,9}) parent=(?<parent>[01]) cleanup=(?<cleanup>[01]) fault=(?<fault>0|[1-9][0-9]{0,4}) cleanupFault=(?<cleanupFault>0|[1-9][0-9]{0,4})\z'
    if ($Line.Length -gt 384 -or $Line -cnotmatch $pattern) { return $null }
    $captures = $Matches
    $row = @{}
    try {
        foreach ($name in @('version', 'stage', 'helper', 'pid', 'born', 'image', 'debug',
                'context', 'exit', 'parent', 'cleanup', 'fault', 'cleanupFault')) {
            $row[$name] = [long]::Parse($captures[$name],
                [Globalization.CultureInfo]::InvariantCulture)
        }
    } catch { return $null }
    if ($row.version -eq 2) {
        if (-not $captures.ContainsKey('resumed')) { return $null }
        $row.resumed = [int]$captures.resumed
    } elseif ($captures.ContainsKey('resumed') -or $row.stage -gt 2) { return $null }
    if ($row.helper -le 0 -or $row.helper -gt [uint32]::MaxValue `
        -or $row.pid -gt [uint32]::MaxValue -or $row.exit -gt [uint32]::MaxValue `
        -or $row.fault -gt 65535 -or $row.cleanupFault -gt 65535) { return $null }
    if (($row.stage -eq 1 -or ($row.version -eq 2 -and $row.stage -eq 2)) `
        -and ($row.pid -eq 0 -or $row.born -eq 0 `
        -or $row.image -ne 1 -or $row.debug -ne 1 -or $row.context -ne 1 `
        -or $row.exit -ne 0 -or $row.parent -ne 0 -or $row.cleanup -ne 0 `
        -or $row.fault -ne 0 -or $row.cleanupFault -ne 0)) { return $null }
    if ($row.version -eq 2 -and (($row.stage -eq 1 -and $row.resumed -ne 0) `
        -or ($row.stage -eq 2 -and $row.resumed -ne 1))) { return $null }
    return $row
}

function Update-HermesStartupNativeRows {
    param([hashtable]$State, [string]$Text)
    if ($State.AcceptedUtf8Bytes -ge 8388608) { $State.Bounds = 1 }
    if ($State.Bounds -ne 0) { return }
    if ($Text.Length -gt 2048 -or $Text -cmatch '[^\x20-\x7e\n]' `
        -or -not $Text.StartsWith($State.RowsText, [StringComparison]::Ordinal)) {
        Set-HermesStartupProtocolFault $State 11; $State.NativeRowsFault = 1
        return
    }
    $State.RowsText = $Text
    $lines = @($Text -split "`n")
    $complete = @($lines | Select-Object -SkipLast 1)
    if ($complete.Count -gt 3 -or $lines[-1].Length -gt 384) {
        Set-HermesStartupProtocolFault $State 11; $State.NativeRowsFault = 1
        return
    }
    $previous = 0
    foreach ($line in $complete) {
        $row = ConvertFrom-HermesStartupRow $line
        if ($null -eq $row -or $row.version -ne 2 -or $row.stage -le $previous `
            -or ($row.stage -eq 2 -and $previous -ne 1)) {
            Set-HermesStartupProtocolFault $State 11; $State.NativeRowsFault = 1
            return
        }
        if ($null -ne $State.Target) {
            foreach ($name in @('helper', 'pid', 'born', 'image', 'debug', 'context')) {
                if ($row[$name] -ne $State.Target[$name]) {
                    Set-HermesStartupProtocolFault $State 11; $State.NativeRowsFault = 1; return
                }
            }
        }
        switch ($row.stage) {
            1 {
                $State.Target = $row; $State.TargetText = $line
                $State.NativeRowSnapshots.Ready = @{ Row = $row.Clone(); Text = $line }
                if ($State.DiagnosticMode -ceq 'ScopedCreateV1') { Set-HermesStartupScopedCreated $State }
                Set-HermesStartupCreatedPhase $State 'ReadyFirst'
                Set-HermesStartupCreatedPhase $State 'CreatedJointFirst'
            }
            2 {
                $State.Resumed = $row; $State.ResumedText = $line
                $State.NativeRowSnapshots.Resumed = @{ Row = $row.Clone(); Text = $line }
            }
            3 {
                if (($previous -eq 2 -and $row.resumed -ne 1) `
                    -or ($previous -eq 0 -and $row.resumed -ne 0)) {
                    Set-HermesStartupProtocolFault $State 11; $State.NativeRowsFault = 1
                    return
                }
                $State.Final = $row; $State.FinalText = $line
                $State.NativeRowSnapshots.Final = @{ Row = $row.Clone(); Text = $line }
            }
        }
        $previous = $row.stage
    }
    if ($previous -eq 3 -and $lines[-1].Length -ne 0) {
        Set-HermesStartupProtocolFault $State 11; $State.NativeRowsFault = 1
    }
}

function Test-HermesStartupNativeReadinessBinding {
    param([hashtable]$State, [switch]$Historical)
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1' `
        -and -not (Test-HermesStartupScopedPublication $State)) { return $false }
    $target = $State.Target
    return $State.NativeProtocol -and $State.Protocol -eq 0 -and $State.Unknown -eq 0 `
        -and $State.DiagnosticMode -cin @('Trace', 'Inventory', 'ScopedCreateV1') `
        -and ($State.Error -eq 0 -or ($Historical -and $State.DiagnosticMode -ceq 'ScopedCreateV1')) `
        -and (($Historical -and $State.DiagnosticMode -ceq 'ScopedCreateV1') `
            -or ($State.Bounds -eq 0 -and $State.AcceptedUtf8Bytes -lt 8388608)) `
        -and $null -ne $target -and $target.version -eq 2 -and $target.stage -eq 1 `
        -and $target.resumed -eq 0 -and $target.helper -gt 0 -and $target.pid -gt 0 `
        -and $State.Initial -eq $target.helper -and $target.pid -ne $target.helper `
        -and $target.born -gt 0 -and $target.image -eq 1 -and $target.debug -eq 1 `
        -and $target.context -eq 1 `
        -and $State.FilterVerified.Cpr -and $State.FilterVerified.Ibp -and $State.IbpBreak `
        -and $State.FilterVerified.Epr -and $State.Observations.CprFilter -eq 9 `
        -and $State.Observations.EprFilter -eq 9 `
        -and $State.CommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
        -and $State.CreateCommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
        -and $State.InitialCommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
        -and $State.ExitCommandSha256 -cmatch '\A[0-9a-f]{64}\z'
}

function Test-HermesStartupCreatedBinding {
    param([hashtable]$State)
    $created = $State.Created
    return (Test-HermesStartupNativeReadinessBinding $State) `
        -and $null -ne $created -and $created.Kind -eq 1 -and $created.Version -eq 3 `
        -and $created.Event -eq 1 -and $created.Pid -eq $State.Target.pid `
        -and $created.EventPid -eq $State.Target.pid
}

function Test-HermesStartupCreated {
    param([hashtable]$State)
    # Permission is only for the existing fixed diagnostic. Trace authority
    # still needs the real matching CPR, even when it arrives after Resume.
    return (Test-HermesStartupNativeReadinessBinding $State) `
        -and ($null -eq $State.Created -or (Test-HermesStartupCreatedBinding $State)) `
        -and -not $State.CreatedAcked -and $null -eq $State.CreatedBinding `
        -and $null -eq $State.Block -and $null -eq $State.Final `
        -and $null -eq $State.Resumed `
        -and -not $State.Exits.ContainsKey([string]$State.Target.pid) `
        -and -not $State.Exits.ContainsKey([string]$State.Target.helper)
}

function Get-HermesStartupCreatedPhase {
    param([hashtable]$State)
    # Observation bits, not authority: 1 observed; 2 Ready; 4 create closed;
    # 8 Final; 16/32 target/helper exit; 64 actual CPR binding; 128 readiness eligibility
    # predicate; 256 open callback; 512 Resumed; 1024 acknowledged;
    # 2048 retained created binding; 4096 protocol fault; 8192 unknown.
    $facts = 1
    if ($null -ne $State.Target) {
        $facts = $facts -bor 2
        if ($State.Exits.ContainsKey([string]$State.Target.pid)) { $facts = $facts -bor 16 }
        if ($State.Exits.ContainsKey([string]$State.Target.helper)) { $facts = $facts -bor 32 }
    }
    if ($null -ne $State.Created) { $facts = $facts -bor 4 }
    if ($null -ne $State.Final) { $facts = $facts -bor 8 }
    if (Test-HermesStartupCreatedBinding $State) { $facts = $facts -bor 64 }
    if (Test-HermesStartupCreated $State) { $facts = $facts -bor 128 }
    if ($null -ne $State.Block) { $facts = $facts -bor 256 }
    if ($null -ne $State.Resumed) { $facts = $facts -bor 512 }
    if ($State.CreatedAcked) { $facts = $facts -bor 1024 }
    if ($null -ne $State.CreatedBinding) { $facts = $facts -bor 2048 }
    if ($State.Protocol -ne 0) { $facts = $facts -bor 4096 }
    if ($State.Unknown -ne 0) { $facts = $facts -bor 8192 }
    return $facts
}

function Set-HermesStartupCreatedPhase {
    param(
        [hashtable]$State,
        [ValidateSet('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst', 'CreatedCheckFirst')]
        [string]$Name
    )
    if (-not $State.NativeProtocol -or $State.Bounds -ne 0 `
        -or $State.AcceptedUtf8Bytes -ge 8388608) { return }
    if ($Name -ceq 'CreatedCheckFirst') { $State.Observations.CreatedCheckSeen = 1 }
    if ($Name -in @('CreatedJointFirst', 'CreatedCheckFirst') -and $null -eq $State.Target) { return }
    if ($Name -ceq 'CreatedJointFirst' -and $null -eq $State.Created) { return }
    # Create-close timing remains independent of an earlier Ready-only check.
    if ($Name -ceq 'CreatedCheckFirst' -and $null -ne $State.CreateCloseClock) {
        Set-HermesStartupWorkBucket $State 'CreateCheckDelay' $State.CreateCloseClock.ElapsedMilliseconds -First
    }
    if ($State.Observations[$Name] -ne 0) { return }
    $State.Observations[$Name] = Get-HermesStartupCreatedPhase $State
}

function Set-HermesStartupCreatedAcknowledged {
    param([hashtable]$State)
    if (-not (Test-HermesStartupCreated $State)) {
        Set-HermesStartupProtocolFault $State 12
        return
    }
    # Snapshot Ready and pinned sources, not a claim that CPR has occurred.
    $State.CreatedBinding = @{
        Mode = $State.DiagnosticMode
        Helper = $State.Target.helper; Pid = $State.Target.pid; Born = $State.Target.born
        RootHash = $State.CommandSha256; CreateHash = $State.CreateCommandSha256
        InitialHash = $State.InitialCommandSha256; ExitHash = $State.ExitCommandSha256
    }
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        $State.CreatedBinding.Scoped = $State.ScopedPublication.Clone()
    }
    $State.CreatedAcked = $true
}

function Test-HermesStartupInitialBinding {
    param([hashtable]$State)
    if ($State.DiagnosticMode -cne 'Trace') { return $false }
    $bound = $State.CreatedBinding
    $arm = $State.Arm
    if ($null -ne $State.Target -and $null -ne $State.Resumed) {
        foreach ($name in @('helper', 'pid', 'born', 'image', 'debug', 'context')) {
            if ($State.Resumed[$name] -ne $State.Target[$name]) { return $false }
        }
    }
    return (Test-HermesStartupCreatedBinding $State) `
        -and $State.CreatedAcked -and $null -ne $bound -and $null -ne $State.Resumed `
        -and $bound.Mode -ceq 'Trace' `
        -and $State.Resumed.resumed -eq 1 `
        -and $bound.Helper -eq $State.Target.helper -and $bound.Pid -eq $State.Target.pid `
        -and $bound.Born -eq $State.Target.born `
        -and $bound.RootHash -ceq $State.CommandSha256 `
        -and $bound.CreateHash -ceq $State.CreateCommandSha256 `
        -and $bound.InitialHash -ceq $State.InitialCommandSha256 `
        -and $bound.ExitHash -ceq $State.ExitCommandSha256 `
        -and $null -ne $arm -and $arm.Kind -eq 5 -and $arm.Version -eq 3 `
        -and $arm.Event -eq 1 -and $arm.Pid -eq $bound.Pid -and $arm.EventPid -eq $bound.Pid `
        -and $arm.Flags -ge 0 -and $arm.Flags -le [uint32]::MaxValue `
        -and ($arm.Flags -band 2) -eq 2
}

function Add-HermesStartupNativeMarker {
    param([hashtable]$State, [int]$Kind, [uint32]$Pid, [int]$Version)
    if ($Pid -eq 0 -or $State.Bounds -ne 0 -or $State.Protocol -ne 0) {
        $State.Protocol = 1
        return
    }
    if ($Kind -eq 0 -and $Version -eq 1) {
        if ($State.Initial -ne 0 -or $null -ne $State.Block) { $State.Protocol = 1 }
        else { $State.Initial = $Pid }
        return
    }
    $valid = ($Kind -in @(1, 2, 5, 6) -and $Version -eq 3) `
        -or ($Kind -in @(3, 4) -and $Version -eq 1)
    if (-not $valid -or $State.Initial -eq 0) { $State.Protocol = 1; return }
    if ($Kind -in @(1, 3, 5)) {
        if ($null -ne $State.Block -or ($Kind -eq 1 -and $null -ne $State.Created) `
            -or ($Kind -eq 5 -and ($null -ne $State.Arm -or -not $State.CreatedAcked)) `
            -or $State.Exits.ContainsKey([string]$Pid)) { $State.Protocol = 1; return }
        $State.Stopped = $false
        $State.Block = @{
            Kind = $Kind; Version = $Version; Pid = $Pid; EventPid = [uint32]0
            Flags = [long]-1; Code = [long]-1; Event = 0
        }
        return
    }
    $block = $State.Block
    if ($null -eq $block -or $block.Pid -ne $Pid -or $block.Kind -ne $Kind - 1 `
        -or $block.Version -ne $Version -or $block.Event -ne 1 -or $block.EventPid -ne $Pid) {
        $State.Protocol = 1; $State.Block = $null
        return
    }
    switch ($Kind) {
        2 { $State.Created = $block }
        6 {
            $State.Arm = $block
            if ($block.Flags -lt 0 -or ($block.Flags -band 2) -ne 2) { $State.Unknown = 1 }
        }
        4 {
            if ($block.Code -lt 0) { $State.Protocol = 1 }
            else { $State.Exits[[string]$Pid] = $block.Code }
        }
    }
    $State.Block = $null
    if ($Kind -eq 2) {
        $State.CreateCloseClock = [Diagnostics.Stopwatch]::StartNew()
        Set-HermesStartupCreatedPhase $State 'CreateCloseFirst'
        Set-HermesStartupCreatedPhase $State 'CreatedJointFirst'
    }
}

function Add-HermesStartupNativeLastEvent {
    param([hashtable]$State, [string]$Line)
    $block = $State.Block
    if ($null -eq $block -or $block.Event -ne 0) { $State.Protocol = 1; return }
    $prefix = '\ALast event: (?<pid>[0-9a-fA-F]{1,8})\.[0-9a-fA-F]{1,8}: '
    $pattern = switch ($block.Kind) {
        1 { $prefix + 'Create process (?:[0-9]+:)?(?<id>[0-9a-fA-F]{1,8})\z' }
        3 { $prefix + 'Exit process (?:[0-9]+:)?(?<id>[0-9a-fA-F]{1,8}), code (?:0x)?(?<code>[0-9a-fA-F]{1,8})\z' }
        # The exception descriptor qualifies only inside this registered ibp
        # callback. A bpe callback or bare exception code cannot open the block.
        5 { $prefix + '(?:Initial breakpoint|Break instruction exception) - code 80000003 \(first chance\)\z' }
    }
    if ($Line -cnotmatch $pattern) { $State.Unknown = 1; return }
    $pidValue = [Convert]::ToUInt32($Matches.pid, 16)
    if ($block.Kind -ne 5 -and $pidValue -ne [Convert]::ToUInt32($Matches.id, 16)) {
        $State.Unknown = 1
        return
    }
    $block.EventPid = $pidValue
    $block.Event = 1
    if ($block.Kind -eq 1) { Add-HermesStartupObservation $State 'CreateEventAccepted' }
    if ($block.Kind -eq 3) {
        Add-HermesStartupObservation $State 'ExitEventAccepted'
        $block.Code = [long][Convert]::ToUInt32($Matches.code, 16)
    }
}

function Set-HermesStartupStoppedPrompt {
    param([hashtable]$State, [string]$Text)
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        $State.Stopped = $State.Bounds -eq 0 -and $State.Protocol -eq 0 -and $State.Unknown -eq 0 `
            -and -not $State.Out.End -and -not $State.Out.Faulted -and $null -ne $State.Held `
            -and -not $State.Held.Released -and $null -eq $State.Inventory.Block `
            -and $Text -cmatch '\A[0-9]{1,3}:[0-9a-f]{3}> \z'
        return
    }
    if ($State.DiagnosticMode -cne 'Trace') { return }
    if ($State.NativeProtocol -and -not $State.ContinueAttempted) { $State.Stopped = $false }
    if ($State.NativeProtocol -and $State.Bounds -eq 0 -and $State.Protocol -eq 0 `
        -and $State.Unknown -eq 0 -and -not $State.Out.End `
        -and $null -ne $State.Arm -and $State.Arm.Kind -eq 5 `
        -and $null -eq $State.Block -and $null -eq $State.Final `
        -and -not $State.ContinueAttempted -and $Text -cmatch '\A[0-9]{1,3}:[0-9a-f]{3}> \z' `
        -and -not $State.Exits.ContainsKey([string]$State.Arm.Pid) `
        -and -not $State.Exits.ContainsKey([string]$State.Initial)) {
        $State.Stopped = $true
    }
}

function Invoke-HermesStartupContinue {
    param([hashtable]$State, [IO.StreamWriter]$Writer)
    if (-not (Test-HermesStartupArm $State)) { throw 'Startup continuation refused.' }
    # Record historical ARMED before the only possible continuation write.
    Set-HermesStartupArmAcknowledged $State
    $State.ContinueAttempted = $true
    $State.Stopped = $false
    $cancellation = [Threading.CancellationTokenSource]::new(2000)
    try {
        $State.InputPending = $Writer.BaseStream.WriteAsync(
            [byte[]]@(103, 10), 0, 2, $cancellation.Token)
        if (-not $State.InputPending.Wait(2000)) { throw 'Startup continuation timed out.' }
        $null = $State.InputPending.GetAwaiter().GetResult()
        $State.Continued = $true
    } catch {
        $State.Error = 13
        throw 'Startup continuation failed.'
    } finally {
        if ($null -ne $State.InputPending -and -not $State.InputPending.IsCompleted) {
            try { $cancellation.Cancel() } catch { $State.CleanupFault = 13 }
        }
        $closed = $true
        foreach ($owned in @($Writer, $Writer.BaseStream)) {
            try { $owned.Dispose() }
            catch { $closed = $false; $State.CleanupFault = 13 }
        }
        $State.InputClosed = $closed
        if ($null -ne $State.InputPending) {
            try {
                if (-not $State.InputPending.Wait(5000)) { $State.CleanupFault = 13 }
            } catch {
                if (-not $State.InputPending.IsCompleted) { $State.CleanupFault = 13 }
            }
        }
        $cancellation.Dispose()
    }
}

function Update-HermesStartupRows {
    param([hashtable]$State, [string]$Text)
    if ($State.NativeProtocol) {
        Update-HermesStartupNativeRows $State $Text
        return
    }
    if ($Text.Length -gt 2048 -or $Text.IndexOf([char]0) -ge 0) {
        $State.Protocol = 1
        return
    }
    $lines = @($Text -split "`n")
    # A writer may still be finishing its last row; only complete rows count.
    $complete = @($lines | Select-Object -SkipLast 1)
    if ($complete.Count -gt 2) { $State.Protocol = 1; return }
    if ($complete.Count -eq 0) { return }
    $firstText = $complete[0].TrimEnd("`r")
    $first = ConvertFrom-HermesStartupRow $firstText
    if ($null -eq $first -or $first.stage -ne 1) {
        $State.Protocol = 1
        return
    }
    if ($State.TargetText -ne '' -and $State.TargetText -cne $firstText) {
        $State.Protocol = 1
        return
    }
    $State.Target = $first
    $State.TargetText = $firstText
    if ($complete.Count -ne 2) { return }
    $lastText = $complete[1].TrimEnd("`r")
    $last = ConvertFrom-HermesStartupRow $lastText
    if ($null -eq $last -or $last.stage -ne 2 `
        -or ($State.FinalText -ne '' -and $State.FinalText -cne $lastText)) {
        $State.Protocol = 1
        return
    }
    foreach ($name in @('helper', 'pid', 'born', 'image', 'debug', 'context')) {
        if ($last[$name] -ne $first[$name]) { $State.Protocol = 1; return }
    }
    $State.Final = $last
    $State.FinalText = $lastText
}

function Test-HermesStartupBinding {
    param([hashtable]$State)
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') { return Test-HermesStartupScopedBinding $State }
    if ($State.NativeProtocol) { return Test-HermesStartupInitialBinding $State }
    return $null -ne $State.Target -and $null -ne $State.Arm `
        -and $State.Protocol -eq 0 -and $State.Unknown -eq 0 `
        -and $State.Initial -eq $State.Target.helper `
        -and $State.Target.pid -ne $State.Target.helper `
        -and $State.Target.born -gt 0 `
        -and $State.Target.image -eq 1 -and $State.Target.debug -eq 1 `
        -and $State.Target.context -eq 1 `
        -and $State.Arm.Kind -eq 1 -and $State.Arm.Event -eq 1 `
        -and $State.Arm.Pid -eq $State.Target.pid `
        -and $State.Arm.EventPid -eq $State.Target.pid `
        -and $State.Arm.Flags -ge 0 -and $State.Arm.Flags -le [uint32]::MaxValue `
        -and ($State.Arm.Flags -band 2) -eq 2
}

function Test-HermesStartupArm {
    param([hashtable]$State)
    if ($State.DiagnosticMode -cne 'Trace') { return $false }
    if ($State.NativeProtocol) {
        return (Test-HermesStartupInitialBinding $State) `
            -and $State.Stopped -and -not $State.ContinueAttempted `
            -and -not $State.Out.End -and $State.Out.Tail -cmatch '\A[0-9]{1,3}:[0-9a-f]{3}> \z' `
            -and -not $State.InputClosed -and -not $State.Acked `
            -and $null -eq $State.ArmedBinding -and $null -eq $State.Block `
            -and $null -eq $State.Final `
            -and -not $State.Exits.ContainsKey([string]$State.Target.pid) `
            -and -not $State.Exits.ContainsKey([string]$State.Target.helper)
    }
    # Eligibility NOW, before publishing the acknowledgement. Never use this
    # predicate to invalidate an earlier acknowledgement after normal exit.
    return (Test-HermesStartupBinding $State) `
        -and -not $State.Acked -and $null -eq $State.ArmedBinding `
        -and $null -eq $State.Block -and $null -eq $State.Final `
        -and -not $State.Exits.ContainsKey([string]$State.Target.helper) `
        -and -not $State.Exits.ContainsKey([string]$State.Target.pid)
}

function Set-HermesStartupArmAcknowledged {
    param([hashtable]$State)
    if (-not (Test-HermesStartupArm $State)) {
        $State.Protocol = 1
        return
    }
    # Copy values, not the mutable parser/control-row hashtables.
    $State.ArmedBinding = @{
        Helper = [long]$State.Target.helper
        Pid = [long]$State.Target.pid
        Born = [long]$State.Target.born
        Flags = [long]$State.Arm.Flags
    }
    $State.Acked = $true
}

function Test-HermesStartupArmed {
    param([hashtable]$State)
    if ($State.DiagnosticMode -cnotin @('Trace', 'ScopedCreateV1')) { return $false }
    # Historical acknowledgement survives exit, but not a changed binding or
    # a subsequently tainted protocol. Acked alone cannot manufacture history.
    return $State.Acked -and $null -ne $State.ArmedBinding `
        -and (Test-HermesStartupBinding $State) `
        -and $State.ArmedBinding.Helper -eq $State.Target.helper `
        -and $State.ArmedBinding.Pid -eq $State.Target.pid `
        -and $State.ArmedBinding.Born -eq $State.Target.born `
        -and $State.ArmedBinding.Flags -eq $State.Arm.Flags
}

function Add-HermesStartupMarker {
    param([hashtable]$State, [int]$Kind, [uint32]$Pid, [int]$Version = 1)
    if ($State.NativeProtocol) {
        Add-HermesStartupNativeMarker $State $Kind $Pid $Version
        return
    }
    if ($Pid -eq 0 -or $Version -notin @(1, 2) `
        -or ($Version -eq 2 -and $Kind -notin @(1, 2))) {
        $State.Protocol = 1
        return
    }
    switch ($Kind) {
        0 {
            if ($State.Initial -ne 0 -or $null -ne $State.Block) {
                $State.Protocol = 1
            } else { $State.Initial = $Pid }
        }
        { $_ -in @(1, 3) } {
            if ($State.Initial -eq 0 -or $null -ne $State.Block `
                -or ($Kind -eq 1 -and $null -ne $State.Arm)) {
                $State.Protocol = 1
                return
            }
            $State.Block = @{
                Kind = $Kind; Version = $Version; Pid = $Pid; EventPid = [uint32]0
                Flags = [long]-1; Code = [long]-1; Event = 0
            }
        }
        { $_ -in @(2, 4) } {
            $block = $State.Block
            if ($null -eq $block -or $block.Pid -ne $Pid `
                -or $block.Version -ne $Version `
                -or $block.Kind -ne $Kind - 1 -or $block.Event -ne 1 `
                -or $block.EventPid -ne $Pid) {
                $State.Protocol = 1
                $State.Block = $null
                return
            }
            if ($Kind -eq 2) {
                if ($block.Flags -lt 0 -or ($block.Flags -band 2) -ne 2) {
                    $State.Unknown = 1
                }
                $State.Arm = $block
            } elseif ($block.Code -lt 0 -or $State.Exits.ContainsKey([string]$Pid)) {
                $State.Protocol = 1
            } else { $State.Exits[[string]$Pid] = $block.Code }
            $State.Block = $null
        }
        default { $State.Protocol = 1 }
    }
}

function Add-HermesStartupLastEvent {
    param([hashtable]$State, [string]$Line)
    if ($State.NativeProtocol) {
        Add-HermesStartupNativeLastEvent $State $Line
        return
    }
    $block = $State.Block
    if ($null -eq $block -or $block.Event -ne 0) {
        $State.Protocol = 1
        return
    }
    $prefix = '\ALast event:\s+(?<pid>[0-9a-fA-F]{1,8})\.[0-9a-fA-F]{1,8}:\s+'
    $create = $prefix + 'Create process (?:[0-9]+:)?(?<id>[0-9a-fA-F]{1,8})\s*\z'
    $exit = $prefix + 'Exit process (?:[0-9]+:)?(?<id>[0-9a-fA-F]{1,8}), code (?:0x)?(?<code>[0-9a-fA-F]{1,8})\s*\z'
    $pattern = if ($block.Kind -eq 1) { $create } else { $exit }
    if ($Line -cnotmatch $pattern) { $State.Unknown = 1; return }
    $pidValue = [Convert]::ToUInt32($Matches.pid, 16)
    $idValue = [Convert]::ToUInt32($Matches.id, 16)
    if ($pidValue -ne $idValue) { $State.Unknown = 1; return }
    $block.EventPid = $pidValue
    $block.Event = 1
    $accepted = if ($block.Kind -eq 1) { 'CreateEventAccepted' } else { 'ExitEventAccepted' }
    Add-HermesStartupObservation $State $accepted
    if ($block.Kind -eq 3) { $block.Code = [Convert]::ToUInt32($Matches.code, 16) }
}

function Add-HermesStartupFailure {
    param([hashtable]$State, [string]$Line)
    if ($State.DiagnosticMode -cnotin @('Trace', 'ScopedCreateV1')) { return }
    # Loader text is not a stable API. Only these explicit failure grammars
    # qualify; unfamiliar failure-looking text is unavailable, not a root cause.
    $candidate = $Line -match 'Ldrp\S+\s+-\s+ERROR:|DLL_PROCESS_ATTACH.*failed|initialization.*failed.*status'
    if (-not $candidate) { return }
    $prefix = '\A(?<pid>[0-9a-fA-F]{1,8})[.:][0-9a-fA-F]{1,8}\s+@\s+[0-9a-fA-F]{8,16}\s+-\s+(?<body>.+)\z'
    if ($Line -cnotmatch $prefix) { $State.Unknown = 1; return }
    $pidValue = [Convert]::ToUInt32($Matches.pid, 16)
    $body = $Matches.body
    if ($pidValue -eq $State.Initial) { return }
    if ($null -eq $State.Target -or $pidValue -ne $State.Target.pid) {
        if ($State.DiagnosticMode -ceq 'ScopedCreateV1' `
            -and $State.Inventory.Subjects.ContainsKey([string]$pidValue)) { return }
        $State.Unknown = 1
        return
    }
    $dll = '\ALdrpCallInitRoutine - ERROR: DLL_PROCESS_ATTACH for dll "(?<module>[^"\r\n]{1,512})" \(InitRoutine: (?:0x)?[0-9a-fA-F`]{1,18}\) failed\.?\z'
    $status = '\ALdrpInitializeProcess - ERROR: Process initialization failed with status (?:0x)?(?<status>[89a-fA-F][0-9a-fA-F]{7})\.?\z'
    $kind = 0
    $code = [uint32]0
    $moduleHash = ''
    $category = 0
    if ($body -cmatch $dll) {
        $kind = 1
        $basename = (($Matches.module -split '[\\/]')[-1]).ToLowerInvariant()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try {
            $moduleHash = [Convert]::ToHexString(
                $hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes($basename))).ToLowerInvariant()
        } finally { $hasher.Dispose() }
        $category = switch ($basename) {
            'ntdll.dll' { 1 }
            'kernel32.dll' { 2 }
            'kernelbase.dll' { 3 }
            { $_ -in @('coreclr.dll', 'clr.dll', 'hostfxr.dll', 'hostpolicy.dll') } { 4 }
            default { 0 }
        }
    } elseif ($body -cmatch $status) {
        $kind = 2
        $code = [Convert]::ToUInt32($Matches.status, 16)
    } else { $State.Unknown = 1; return }
    # Recognising a grammar does not establish its event ordering. In
    # particular, no new failure evidence may be accepted after either exit.
    if (-not (Test-HermesStartupArmed $State) `
        -or ($State.NativeProtocol -and -not $State.Continued) -or $null -ne $State.Block `
        -or ($State.DiagnosticMode -ceq 'ScopedCreateV1' `
            -and ($null -ne $State.Final -or $null -ne $State.Inventory.Block)) `
        -or $State.Exits.ContainsKey([string]$State.Target.pid) `
        -or $State.Exits.ContainsKey([string]$State.Target.helper)) {
        Set-HermesStartupProtocolFault $State 13
        return
    }
    if ($null -eq $State.Failure) {
        $State.Failure = @{
            Kind = $kind; Status = $code; ModuleCategory = $category
            ModuleSha256 = $moduleHash
        }
    }
}

function ConvertFrom-HermesStartupCommandEscapes {
    param([string]$Source, [bool]$DropUnknown)
    # Source-only comparison hypotheses, not an sx parser or command repair.
    # bp documents C controls; sx does not specify its unescaping algorithm:
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/bp--bu--bm--set-breakpoint-
    $escapes = [hashtable]::new(@{
        'a' = [string][char]7; 'b' = [string][char]8; 'f' = [string][char]12
        'n' = [string][char]10; 'r' = [string][char]13
        't' = [string][char]9; 'v' = [string][char]11
        '\' = '\'; '"' = '"'; "'" = "'"; '?' = '?'
    }, [StringComparer]::Ordinal)
    return [regex]::Replace($Source, '\\(.)', {
        param($capture)
        $escape = $capture.Groups[1].Value
        if ($escapes.ContainsKey($escape)) { $escapes[$escape] }
        elseif ($DropUnknown) { $escape } else { $capture.Value }
    })
}

function Get-HermesStartupCommandTransform {
    param([string]$Source, [string]$Payload)
    if ($Source.Length -gt 4096 -or $Payload.Length -gt 4096) { return 0 }
    $doubled = $Source.Replace('\', '\\')
    $removed = $Source.Replace('\', '')
    $knownEscapes = ConvertFrom-HermesStartupCommandEscapes $Source $false
    $allEscapes = ConvertFrom-HermesStartupCommandEscapes $Source $true
    # Fixed ordinals only. Never decode, execute, hash, or retain the payload.
    # First exact ordinal wins when a source has no character to transform.
    $variants = @(
        $Source, ('"' + $Source + '"'), ('\"' + $Source + '\"'), ('""' + $Source + '""'),
        $doubled, ('"' + $doubled + '"'), ('\"' + $doubled + '\"'),
        $removed, ('"' + $removed + '"'),
        $knownEscapes, ('"' + $knownEscapes + '"'),
        $allEscapes, ('"' + $allEscapes + '"'),
        $Source.Replace('$$', '$'), ('"' + $Source.Replace('$$', '$') + '"'),
        $Source.Replace('$$', '$$$$'), ('"' + $Source.Replace('$$', '$$$$') + '"'),
        $Source.Replace('$$', ''), ('"' + $Source.Replace('$$', '') + '"')
    )
    for ($index = 0; $index -lt $variants.Count; $index++) {
        if ($Payload -ceq $variants[$index]) { return ($index + 1) }
    }
    return 0
}

function Set-HermesStartupCommandDerivation {
    param([hashtable]$State, [string]$Name, [string]$Payload)
    if ($Payload.Length -gt 4096) { return }
    $State.Observations[$Name + 'PayloadLength'] = $Payload.Length
    $expected = $State.FilterExpected[$Name]
    if ($expected -isnot [string] -or $expected.Length -le 11 -or $expected.Length -gt 4096 `
        -or -not $expected.StartsWith('Command: "', [StringComparison]::Ordinal) `
        -or -not $expected.EndsWith('"', [StringComparison]::Ordinal)) { return }
    $source = $expected.Substring(10, $expected.Length - 11)
    $State.Observations[$Name + 'SourceLength'] = $source.Length
    $State.Observations[$Name + 'CommandTransform'] =
        Get-HermesStartupCommandTransform $source $Payload
}

function Get-HermesStartupFilterCommandShape {
    param([hashtable]$State, [string]$Name, [string]$Line)
    $text = $Line.Trim()
    if ($text.Length -eq 0) { return 8 }
    # Only Command: "..." is documented by sx. Other shapes are observations,
    # not accepted spellings, registration proof, or debugger event evidence.
    if ($text -cmatch '\ACommand(?<suffix>1)?:[ \t]*(?<payload>[^\r\n]*)\z') {
        $offset = if ($Matches['suffix'] -ceq '1') { 2 } else { 0 }
        $payload = $Matches.payload
        Set-HermesStartupCommandDerivation $State $Name $payload
        if ($payload -cin @('', '""')) { return 5 }
        $expected = $State.FilterExpected[$Name]
        if ($expected -is [string] -and $expected.Length -gt 11 `
            -and $expected.StartsWith('Command: "', [StringComparison]::Ordinal) `
            -and $expected.EndsWith('"', [StringComparison]::Ordinal)) {
            $quoted = $expected.Substring(9)
            if ($payload -ceq $quoted) { return (1 + $offset) }
            if ($payload -ceq $quoted.Substring(1, $quoted.Length - 2)) {
                return (2 + $offset)
            }
        }
        return 6
    }
    if ($text -cmatch '\A[a-z0-9]{1,8} - [^\r\n]+ - [^\r\n]+\z') { return 7 }
    return 9
}

function Add-HermesStartupFilterReadback {
    param([hashtable]$State, [string]$Line)
    # Registration is necessary, but never substitutes for a runtime callback.
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/sx--sxd--sxe--sxi--sxn--sxr--sx---set-exceptions-
    $pending = $State.FilterPending
    $State.FilterPending = ''
    if ($pending -cin @('Cpr', 'Epr', 'Ibp')) {
        $expected = $State.FilterExpected[$pending]
        $State.FilterVerified[$pending] = $expected -is [string] `
            -and $expected.Length -gt 11 -and $Line.Trim() -ceq $expected
    }
    if ($pending -cin @('Cpr', 'Epr')) {
        $State.Observations[$pending + 'CommandShape'] =
            Get-HermesStartupFilterCommandShape $State $pending $Line
    }
    if ($Line -cmatch '\A[ \t]*(?<event>cpr|epr|ibp) - (?<label>Create process|Exit process|Initial breakpoint) - (?<status>[^\r\n]*)\z') {
        $name = switch -CaseSensitive ($Matches.event) { 'cpr' { 'Cpr' }; 'epr' { 'Epr' }; 'ibp' { 'Ibp' } }
        $label = switch ($name) { 'Cpr' { 'Create process' }; 'Epr' { 'Exit process' }; 'Ibp' { 'Initial breakpoint' } }
        $status = switch -CaseSensitive ($Matches.status.TrimEnd()) {
            'break' { 1 }; 'second-chance break' { 2 }
            'output' { 3 }; 'ignore' { 4 }; default { 5 }
        }
        if ($Matches.label -cne $label) { $status = 5 }
        $State.FilterVerified[$name] = $false
        if ($name -ceq 'Ibp') {
            $State.IbpBreak = $status -eq 1
            $State.FilterPending = $name
            return
        }
        $State.Observations[$name + 'Filter'] = $status
        $State.Observations[$name + 'CommandShape'] = 0
        foreach ($field in @('CommandTransform', 'PayloadLength', 'SourceLength')) {
            $State.Observations[$name + $field] = 0
        }
        $State.FilterPending = $name
    } elseif ($pending -cin @('Cpr', 'Epr') `
        -and $State.FilterExpected[$pending] -is [string] `
        -and $State.FilterExpected[$pending].Length -gt 0 `
        -and $Line.Trim() -ceq $State.FilterExpected[$pending]) {
        $status = Get-HermesStartupObservation $State ($pending + 'Filter') 5
        if ($status -ge 1) { $State.Observations[$pending + 'Filter'] = $status + 8 }
    }
}

function Get-HermesStartupLoadLibraryPathShape {
    param($Expected, [string]$Path)
    if ($Expected -isnot [string] -or $Expected.Length -eq 0 -or $Expected.Length -gt 240 `
        -or $Expected -cmatch '[";\x00-\x1f\x7f-\uffff]') { return 0 }
    $stripped = $Expected.Replace('\', '')
    $doubled = $Expected.Replace('\', '\\')
    $variants = @($Expected, ('"' + $Expected + '"'),
        $stripped, ('"' + $stripped + '"'), $doubled, ('"' + $doubled + '"'))
    for ($index = 0; $index -lt $variants.Count; $index++) {
        if ($Path -ceq $variants[$index]) { return ($index + 1) }
    }
    return 7
}

function Get-HermesStartupLoadLibraryRelation {
    param($Expected, [string]$Path)
    $unknown = @{ Relation = 0; Form = 0 }
    if ($Expected -isnot [string] -or $Expected.Length -eq 0 -or $Expected.Length -gt 240 `
        -or $Expected -cmatch '[";\x00-\x1f\x7f-\uffff]' -or $Path.Length -gt 1024) { return $unknown }
    # Fixed source variants only. No trimming, path resolution, decoding, or
    # payload-derived names; even a match is diagnostic, never load authority.
    # Default user-mode modules:
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/using-debugger-extension-commands
    # gflag is the fixed callback command, not a documented default module.
    $variants = @(
        $Expected, $Expected.Replace('\', '/'), $Expected.Replace('\', ''),
        $Expected.Replace('\', '\\'), ($Expected -split '[ \t]', 2)[0],
        ('.load "' + $Expected + '"'), ('.load ' + $Expected),
        'exts', 'exts.dll', 'ext', 'ext.dll', 'ntsdexts', 'ntsdexts.dll',
        'gflag', 'gflag.dll', 'dbghelp', 'dbghelp.dll', 'uext', 'uext.dll'
    )
    if ($Expected -cmatch '\A(?<sdk>[A-Za-z]:\\.+?)\\(?:(?:winxp|winext)\\)?exts\.dll\z') {
        $sdk = $Matches.sdk
        $variants += @(($sdk + '\winext\ext.dll'), ($sdk + '\winxp\ntsdexts.dll'),
            ($sdk + '\dbghelp.dll'), ($sdk + '\winext\uext.dll'))
    }
    for ($index = 0; $index -lt $variants.Count; $index++) {
        $source = $variants[$index]
        $forms = @($source, ('"' + $source + '"'), ('"' + $source), ($source + '"'),
            ('\"' + $source + '\"'), (' ' + $source), ($source + ' '))
        for ($form = 0; $form -lt $forms.Count; $form++) {
            if ([string]::Equals($Path, $forms[$form], [StringComparison]::Ordinal)) {
                return @{ Relation = $index + 1; Form = $form + 1 }
            }
            if ([string]::Equals($Path, $forms[$form], [StringComparison]::OrdinalIgnoreCase)) {
                return @{ Relation = $index + 1; Form = $form + 9 }
            }
        }
    }
    return $unknown
}

function Get-HermesStartupLibraryLexicalFacts {
    param($Expected, [string]$Path)
    $unknown = @{ Module = 0; Suffix = 0; Display = 0 }
    if ($Expected -isnot [string] -or $Expected.Length -eq 0 -or $Expected.Length -gt 240 `
        -or $Expected -cmatch '[";\x00-\x1f\x7f-\uffff]' -or $Path.Length -eq 0 `
        -or $Path.Length -gt 1024 -or $Path -cmatch '[\x00-\x1f]') { return $unknown }
    # Private lexical display comparison, NOT a Windows path parser. No file
    # resolution, general escape decoding, command execution, or path authority.
    $display = $Path
    $flags = 1
    for ($round = 0; $round -lt 2; $round++) {
        $padded = $display.Trim([char[]]@(' ', [char]9))
        if ($padded -cne $display) { $flags = $flags -bor 256 }
        $escaped = [regex]::Replace($padded, '\\+(?=["''])', '')
        if ($escaped -cne $padded) { $flags = $flags -bor 64 }
        $display = $escaped.Trim([char[]]@(' ', [char]9, '"', "'"))
        if ($display -cne $escaped) { $flags = $flags -bor 32 }
        if ($round -eq 0 -and $display -cmatch '\A(?i:\.load)[ \t]+(?<rest>.+)\z') {
            $flags = $flags -bor 16
            $display = $Matches.rest
        } else { break }
    }
    $slashes = [regex]::Replace($display, '[\\/]+', '\')
    if ($slashes -cne $display) { $flags = $flags -bor 128 }
    $source = [regex]::Replace($Expected, '[\\/]+', '\')
    if ([string]::Equals($slashes, $source, [StringComparison]::OrdinalIgnoreCase)) {
        $flags = $flags -bor 2
        if ($slashes -cne $source) { $flags = $flags -bor 512 }
    } elseif ($slashes.Length -ge 3 -and $slashes.Length -lt $source.Length) {
        if ($source.StartsWith($slashes, [StringComparison]::OrdinalIgnoreCase)) { $flags = $flags -bor 4 }
        if ($source.EndsWith($slashes, [StringComparison]::OrdinalIgnoreCase)) { $flags = $flags -bor 8 }
    }
    $leaf = ($slashes -split '\\')[-1]
    $dot = $leaf.IndexOf('.')
    $stem = if ($dot -lt 0) { $leaf } else { $leaf.Substring(0, $dot) }
    $suffix = if ($dot -lt 0) { '' } else { $leaf.Substring($dot) }
    # Fixed SDK/default-search names plus the fixed callback stem. A known
    # leaf does not identify its directory, provenance, dependency, or load.
    $names = @('exts', 'ext', 'ntsdexts', 'gflag', 'dbghelp', 'uext',
        'dbgeng', 'symsrv', 'gflags', 'cdb', 'kext', 'kdexts')
    for ($index = 0; $index -lt $names.Count; $index++) {
        if (-not [string]::Equals($stem, $names[$index], [StringComparison]::OrdinalIgnoreCase)) { continue }
        $kind = switch ($suffix.ToLowerInvariant()) {
            '' { 1 }; '.dll' { 2 }; '.dll.dll' { 3 }; '.exe' { 4 }; default { 5 }
        }
        return @{ Module = $index + 1; Suffix = $kind; Display = $flags }
    }
    return @{ Module = 0; Suffix = 0; Display = $flags }
}

function Add-HermesStartupChainReadback {
    param([hashtable]$State, [string]$Line)
    # Read-only root/held-arm stdout advisory, never selection or input
    # authority. Unsupported layouts stay unknown, including a header alone.
    # Documented full-path row example:
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/-load---loadby--load-extension-dll-
    $scopedArm = $State.DiagnosticMode -ceq 'ScopedCreateV1' -and $State.ArmWritten `
        -and $null -ne $State.Inventory.Block -and $State.Inventory.Block.Kind -eq 7
    if (($State.Initial -ne 0 -and -not $scopedArm) `
        -or $Line.Contains('HERMES_CDB', [StringComparison]::Ordinal)) {
        $State.ChainCollecting = $false
        $State.ChainPending = $false
        return
    }
    if ($Line -ceq 'Extension DLL chain:') {
        $State.ChainPending = $true
        $State.ChainCollecting = $true
        if ((Get-HermesStartupObservation $State 'SdkSelectedExtensionChain' 3) -eq 0) {
            $State.Observations.SdkSelectedExtensionChain = 1
        }
        return
    }
    $row = '\A[ \t]+(?<path>[^\x00-\x1f]{1,1024}): image [0-9]{1,5}(?:\.[0-9]{1,5}){1,3}, API [0-9]{1,5}(?:\.[0-9]{1,5}){1,3}(?:, built [^\x00-\x1f]{1,128})?\z'
    # Count the entire bounded root readback, even after an unsupported row
    # closes the strict matcher. Counts do not widen its accepted grammar.
    if ($State.ChainCollecting -and $Line.Trim([char[]]@(' ', [char]9)).Length -gt 0) {
        Add-HermesStartupObservation $State 'SdkChainLines'
        if ($Line -cmatch $row) { Add-HermesStartupObservation $State 'SdkChainSupportedRows' }
    }
    if (-not $State.ChainPending) { return }
    if ($Line -cnotmatch '\A[ \t]+') { $State.ChainPending = $false; return }
    if ($Line -cnotmatch $row) { return }
    $match = Get-HermesStartupLoadLibraryRelation $State.ExtensionExpected $Matches.path
    if ($match.Relation -notin @(1, 2) -or $match.Form -notin @(1, 9)) { return }
    $value = if ($match.Relation -eq 1 -and $match.Form -eq 1) { 2 } else { 3 }
    if ((Get-HermesStartupObservation $State 'SdkSelectedExtensionChain' 3) -ne 2) {
        $State.Observations.SdkSelectedExtensionChain = $value
    }
}

function Set-HermesStartupLoadLibraryError {
    param([hashtable]$State, [string]$Path, [string]$Code)
    $errorCode = [long]0
    if (-not [long]::TryParse($Code, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$errorCode) `
        -or $errorCode -gt [uint32]::MaxValue) { return }
    $State.Observations.SdkLoadLibraryWin32Error = $errorCode
    $State.Observations.SdkLoadLibraryPathShape =
        Get-HermesStartupLoadLibraryPathShape $State.ExtensionExpected $Path
    $relation = Get-HermesStartupLoadLibraryRelation $State.ExtensionExpected $Path
    $State.Observations.SdkLoadLibraryRelation = $relation.Relation
    $State.Observations.SdkLoadLibraryForm = $relation.Form
    $lexical = Get-HermesStartupLibraryLexicalFacts $State.ExtensionExpected $Path
    $State.Observations.SdkLoadLibraryLexicalModule = $lexical.Module
    $State.Observations.SdkLoadLibraryLexicalSuffix = $lexical.Suffix
    $State.Observations.SdkLoadLibraryDisplay = $lexical.Display
}

function Add-HermesStartupSdkError {
    param([hashtable]$State, [string]$Line)
    if ($Line.Length -gt 4096) { return }
    # Closed SDK message shapes only, not command success or target evidence.
    # !gflag belongs to Exts.dll; a missing export is not a LoadLibrary diagnosis.
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/-gflag
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/using-debugger-extension-commands
    # https://learn.microsoft.com/windows-hardware/drivers/debugger/verifying-symbols
    $kind = switch -CaseSensitive -Regex ($Line) {
        '\AThe call to LoadLibrary\((?<library>[^\x00-\x1f]{1,1024})\) failed, Win32 error 0n(?<error>[0-9]{1,10})\z' {
            if (((Get-HermesStartupObservation $State 'SdkCommandErrors' 15) -band 1) -eq 0) {
                Set-HermesStartupLoadLibraryError $State $Matches.library $Matches.error
            }
            1; break
        }
        '\ANo export gflag found\z' { 2; break }
        '\AUnable to read PEB\z' { 4; break }
        '\A\*\*\* ERROR: (?:Symbols|Module load completed but symbols) could not be loaded for ntdll\.dll\z' { 8; break }
        default { 0 }
    }
    $State.Observations.SdkCommandErrors =
        (Get-HermesStartupObservation $State 'SdkCommandErrors' 15) -bor $kind
}

function Add-HermesStartupTypedFlags {
    param([hashtable]$State, [string]$Line)
    $block = $State.Block
    $pattern = '\AHERMES_NTGLOBALFLAG v=1 pid=(?<pid>[1-9][0-9]{0,9}) value=(?<flags>[0-9a-f]{8})\z'
    if ($Line -cnotmatch $pattern -or $null -eq $block `
        -or (($State.NativeProtocol -and ($block.Kind -ne 5 -or $block.Version -ne 3)) `
            -or (-not $State.NativeProtocol -and ($block.Kind -ne 1 -or $block.Version -ne 2))) `
        -or $block.Event -ne 1 -or $block.EventPid -ne $block.Pid `
        -or [long]$Matches.pid -gt [uint32]::MaxValue `
        -or [long]$Matches.pid -ne $block.Pid -or $block.Pid -eq $State.Initial `
        -or $block.Flags -ne -1 -or $State.Protocol -ne 0 -or $State.Unknown -ne 0 `
        -or $State.Acked -or $null -ne $State.Final `
        -or $State.Exits.ContainsKey([string]$block.Pid)) {
        $State.Protocol = 1
        return
    }
    $block.Flags = [long][Convert]::ToUInt32($Matches.flags, 16)
}

function Get-HermesStartupTypedFlags {
    param([hashtable]$State)
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        if (Test-HermesStartupScopedBinding $State) { return [long]$State.Arm.Flags }
        return [long]-1
    }
    if ($State.DiagnosticMode -cne 'Trace') { return [long]-1 }
    $block = if ($null -ne $State.Arm) { $State.Arm } else { $State.Block }
    $target = $State.Target
    if ($State.Protocol -ne 0 -or $null -eq $target -or $null -eq $block `
        -or (($State.NativeProtocol -and ($block.Kind -ne 5 -or $block.Version -ne 3)) `
            -or (-not $State.NativeProtocol -and ($block.Kind -ne 1 -or $block.Version -ne 2))) `
        -or $block.Event -ne 1 `
        -or $State.Initial -ne $target.helper -or $target.pid -eq $target.helper `
        -or $target.born -le 0 -or $target.image -ne 1 -or $target.debug -ne 1 `
        -or $target.context -ne 1 -or $block.Pid -ne $target.pid `
        -or $block.EventPid -ne $target.pid `
        -or $block.Flags -lt 0 -or $block.Flags -gt [uint32]::MaxValue) { return [long]-1 }
    return [long]$block.Flags
}

function Add-HermesStartupInventoryFault {
    param([hashtable]$Inventory, [ValidateSet('Malformed', 'Mismatch')][string]$Name)
    $Inventory[$Name] = [Math]::Min(8388608, $Inventory[$Name] + 1)
}

function Complete-HermesStartupInventoryBlock {
    param([hashtable]$Inventory, [int]$Kind, [uint32]$Pid, [int]$Version)
    $block = $Inventory.Block
    $Inventory.Block = $null
    if ($null -eq $block -or $block.Kind -ne $Kind - 1 -or $block.Version -ne $Version) {
        Add-HermesStartupInventoryFault $Inventory 'Malformed'
        return
    }
    if ($block.Pid -ne $Pid) {
        Add-HermesStartupInventoryFault $Inventory 'Mismatch'
        return
    }
    if ($block.Event -ne 1) { Add-HermesStartupInventoryFault $Inventory 'Malformed'; return }
    if (-not $block.Valid) { return }
    $key = [string]$Pid
    if (-not $Inventory.Subjects.ContainsKey($key)) {
        if ($Inventory.Subjects.Count -ge 16) { $Inventory.Limit = 1; return }
        $Inventory.Subjects[$key] = @{ Create = 0; Exit = 0; Initial = 0; ExitCode = [long]0 }
    }
    $subject = $Inventory.Subjects[$key]
    $name = switch ($Kind) { 2 { 'Create' }; 4 { 'Exit' }; 6 { 'Initial' } }
    $subject[$name] = [Math]::Min(8388608, $subject[$name] + 1)
    if ($Kind -eq 4) { $subject.ExitCode = $block.Code }
}

function Add-HermesStartupInventoryLastEvent {
    param([hashtable]$Inventory, [string]$Line)
    $block = $Inventory.Block
    if ($null -eq $block) { Add-HermesStartupInventoryFault $Inventory 'Malformed'; return }
    if ($block.Event -ne 0) {
        $block.Valid = $false
        Add-HermesStartupInventoryFault $Inventory 'Malformed'
        return
    }
    $prefix = '\ALast event: (?<pid>[0-9a-fA-F]{1,8})\.[0-9a-fA-F]{1,8}: '
    $pattern = switch ($block.Kind) {
        1 { $prefix + 'Create process (?:[0-9]+:)?(?<id>[0-9a-fA-F]{1,8})\z' }
        7 { $prefix + 'Create process (?:[0-9]+:)?(?<id>[0-9a-fA-F]{1,8})\z' }
        3 { $prefix + 'Exit process (?:[0-9]+:)?(?<id>[0-9a-fA-F]{1,8}), code (?:0x)?(?<code>[0-9a-fA-F]{1,8})\z' }
        5 { $prefix + '(?:Initial breakpoint|Break instruction exception) - code 80000003 \(first chance\)\z' }
    }
    if ($Line -cnotmatch $pattern) {
        $block.Valid = $false; $block.Event = 1
        Add-HermesStartupInventoryFault $Inventory 'Malformed'
        return
    }
    $eventPid = [Convert]::ToUInt32($Matches.pid, 16)
    $block.EventPid = $eventPid
    $block.Event = 1
    if ($eventPid -ne $block.Pid -or ($block.Kind -ne 5 `
        -and [Convert]::ToUInt32($Matches.id, 16) -ne $eventPid)) {
        $block.Valid = $false
        Add-HermesStartupInventoryFault $Inventory 'Mismatch'
    }
    if ($block.Kind -eq 3) { $block.Code = [long][Convert]::ToUInt32($Matches.code, 16) }
}

function Add-HermesStartupInventoryLine {
    param([hashtable]$State, [string]$Line)
    if ($State.DiagnosticMode -cne 'Inventory' -or $State.Bounds -ne 0 `
        -or $State.AcceptedUtf8Bytes -ge 8388608) { return }
    $inventory = $State.Inventory
    if ($inventory.Limit -ne 0) { return }
    if ($Line.Contains('.printf', [StringComparison]::Ordinal)) { return }
    if ($Line -cmatch '\AHERMES_CDB v=(?<version>[13]) kind=(?<kind>[0-6]) pid=(?<pid>[1-9][0-9]{0,9})\z' `
        -and [long]$Matches.pid -le [uint32]::MaxValue) {
        $version = [int]$Matches.version; $kind = [int]$Matches.kind; $pidValue = [uint32]$Matches.pid
        if ($kind -eq 0 -and $version -eq 1) {
            if ($inventory.Root -ne 0 -or $null -ne $inventory.Block) {
                $inventory.RootFault = 1
                Add-HermesStartupInventoryFault $inventory 'Malformed'
            } else { $inventory.Root = $pidValue }
            return
        }
        if (-not (($kind -in @(1, 2, 5, 6) -and $version -eq 3) `
            -or ($kind -in @(3, 4) -and $version -eq 1))) {
            Add-HermesStartupInventoryFault $inventory 'Malformed'
            return
        }
        if ($kind -in @(2, 4, 6)) {
            Complete-HermesStartupInventoryBlock $inventory $kind $pidValue $version
            return
        }
        if ($null -ne $inventory.Block) { Add-HermesStartupInventoryFault $inventory 'Malformed' }
        $inventory.Block = @{
            Kind = $kind; Version = $version; Pid = $pidValue
            Event = 0; Valid = $true; Code = [long]0
        }
        return
    }
    if ($Line.Contains('HERMES_CDB', [StringComparison]::Ordinal)) {
        Add-HermesStartupInventoryFault $inventory 'Malformed'
        if ($null -ne $inventory.Block) { $inventory.Block.Valid = $false }
        return
    }
    if ($Line.Contains('Last event:', [StringComparison]::OrdinalIgnoreCase)) {
        Add-HermesStartupInventoryLastEvent $inventory $Line
    }
}

function Test-HermesStartupInventoryBinding {
    param([hashtable]$State)
    $target = $State.Target; $bound = $State.CreatedBinding
    if ($State.DiagnosticMode -cne 'Inventory' -or -not $State.CreatedAcked `
        -or $State.NativeRowsFault -ne 0 -or $null -eq $target -or $null -eq $bound `
        -or $bound.Mode -cne 'Inventory' -or $State.Inventory.RootFault -ne 0 `
        -or $State.Inventory.Root -ne $target.helper -or $State.Initial -ne $target.helper `
        -or $target.version -ne 2 -or $target.stage -ne 1 -or $target.resumed -ne 0 `
        -or $target.pid -eq $target.helper -or $target.pid -le 0 -or $target.helper -le 0 `
        -or $target.born -le 0 -or $target.image -ne 1 -or $target.debug -ne 1 -or $target.context -ne 1 `
        -or $bound.Helper -ne $target.helper -or $bound.Pid -ne $target.pid -or $bound.Born -ne $target.born `
        -or -not $State.FilterVerified.Cpr -or -not $State.FilterVerified.Epr `
        -or -not $State.FilterVerified.Ibp -or -not $State.IbpBreak `
        -or $State.Observations.CprFilter -ne 9 -or $State.Observations.EprFilter -ne 9) { return $false }
    foreach ($item in @(
        @('RootHash', 'CommandSha256'), @('CreateHash', 'CreateCommandSha256'),
        @('InitialHash', 'InitialCommandSha256'), @('ExitHash', 'ExitCommandSha256')
    )) {
        if ($State[$item[1]] -cnotmatch '\A[0-9a-f]{64}\z' `
            -or $bound[$item[0]] -cne $State[$item[1]]) { return $false }
    }
    return $true
}

function Test-HermesStartupScopedPublication {
    param([hashtable]$State)
    $source = $State.ScopedPublication
    if ($source -isnot [hashtable] -or $State.ExtensionSource -isnot [hashtable]) { return $false }
    foreach ($name in @('Root', 'Extension', 'Mode', 'QualifiedModule')) {
        if (-not $source.ContainsKey($name) -or $source[$name] -isnot [string]) { return $false }
    }
    foreach ($name in @('Path', 'Sha256', 'Pin')) {
        if (-not $State.ExtensionSource.ContainsKey($name)) { return $false }
    }
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or $State.PublishedMode -cne 'ScopedCreateV1' `
        -or $null -eq $source -or $source.Mode -cne 'ScopedCreateV1' `
        -or $source.Root -ceq '' -or $source.Root -cmatch '[";\x00-\x1f\x7f-\uffff]' `
        -or $source.Extension -cne $State.ExtensionExpected `
        -or $null -eq $State.ExtensionSource -or $State.ExtensionSource.Path -cne $source.Extension `
        -or $State.ExtensionSource.Sha256 -cne $State.ExtensionSha256 `
        -or $null -eq $State.ExtensionSource.Pin `
        -or $State.CommandPins.Count -ne 5 -or $State.SourcePins.Count -ne 2) { return $false }
    if ($source.Extension.Length -lt 9 `
        -or (-not $source.Extension.EndsWith('/exts.dll', [StringComparison]::OrdinalIgnoreCase) `
            -and -not $source.Extension.EndsWith('\exts.dll', [StringComparison]::OrdinalIgnoreCase)) `
        -or $source.QualifiedModule -cne $source.Extension.Substring(0, $source.Extension.Length - 4) `
        -or $source.QualifiedModule.Length -gt 255) { return $false }
    foreach ($name in @('DebuggerSha256', 'ExtensionSha256', 'HelperSha256', 'DriverSha256', 'ModuleSha256',
            'CommandSha256', 'CreateCommandSha256', 'InitialCommandSha256', 'ExitCommandSha256',
            'ArmCommandSha256')) {
        if ($State[$name] -cnotmatch '\A[0-9a-f]{64}\z' -or $source[$name] -cne $State[$name]) {
            return $false
        }
    }
    return $true
}

function Test-HermesStartupScopedReceipt {
    param([hashtable]$State, [switch]$Historical)
    if (-not (Test-HermesStartupNativeReadinessBinding $State -Historical:$Historical) -or -not $State.CreatedAcked `
        -or $null -eq $State.CreatedBinding -or $null -eq $State.Resumed `
        -or $State.Inventory.Root -ne $State.Target.helper -or $State.Inventory.RootFault -ne 0 `
        -or $State.Inventory.Malformed -ne 0 -or $State.Inventory.Mismatch -ne 0 `
        -or $State.Inventory.Limit -ne 0) { return $false }
    $bound = $State.CreatedBinding
    if ($bound.Mode -cne 'ScopedCreateV1' -or $bound.Scoped -isnot [hashtable]) { return $false }
    foreach ($name in $State.ScopedPublication.Keys) {
        if ($bound.Scoped[$name] -cne $State.ScopedPublication[$name]) { return $false }
    }
    foreach ($item in @(
        @('RootHash', 'CommandSha256'), @('CreateHash', 'CreateCommandSha256'),
        @('InitialHash', 'InitialCommandSha256'), @('ExitHash', 'ExitCommandSha256')
    )) {
        if ($bound[$item[0]] -cne $State[$item[1]]) { return $false }
    }
    $ready = Get-HermesStartupInventoryNativeRow $State Ready
    $resumed = Get-HermesStartupInventoryNativeRow $State Resumed
    if ($null -eq $ready -or $null -eq $resumed -or $resumed.stage -ne 2 -or $resumed.resumed -ne 1 `
        -or $bound.Helper -ne $ready.helper -or $bound.Pid -ne $ready.pid -or $bound.Born -ne $ready.born) {
        return $false
    }
    # Actions retain the live canonical-row check. Historical reporting uses
    # only the parser's earlier scalar snapshots, never another input parse.
    if (-not $Historical) {
        foreach ($pair in @(@($ready, $State.TargetText), @($resumed, $State.ResumedText))) {
            $parsed = ConvertFrom-HermesStartupRow $pair[1]
            if ($null -eq $parsed -or $parsed.Count -ne $pair[0].Count) { return $false }
            foreach ($name in $parsed.Keys) {
                if ($pair[0][$name] -ne $parsed[$name]) { return $false }
            }
        }
    }
    foreach ($name in @('helper', 'pid', 'born', 'image', 'debug', 'context')) {
        if ($resumed[$name] -ne $ready[$name]) { return $false }
    }
    return $true
}

function Set-HermesStartupScopedCreated {
    param([hashtable]$State)
    $held = $State.Held
    if ($null -ne $State.Target -and $null -ne $held -and $held.Kind -eq 1 `
        -and $held.Pid -eq $State.Target.pid -and $null -eq $State.Created) {
        $State.Created = $held.Clone()
        $State.CreateCloseClock = [Diagnostics.Stopwatch]::StartNew()
        Set-HermesStartupCreatedPhase $State 'CreateCloseFirst'
        Set-HermesStartupCreatedPhase $State 'CreatedJointFirst'
    }
}

function Test-HermesStartupScopedBinding {
    param([hashtable]$State)
    if ($State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608 `
        -or -not (Test-HermesStartupScopedReceipt $State -Historical) -or $null -eq $State.ScopedInvocation `
        -or -not $State.ArmAttempted -or -not $State.ArmWritten) { return $false }
    $created = $State.Created
    if ($null -eq $created -or $created.Kind -ne 1 -or $created.Version -ne 3 -or $created.Event -ne 1 `
        -or $created.Pid -ne $State.Target.pid -or $created.EventPid -ne $State.Target.pid) { return $false }
    $invocation = $State.ScopedInvocation
    $arm = $State.Arm
    if ($invocation.Source -isnot [hashtable]) { return $false }
    foreach ($name in $State.ScopedPublication.Keys) {
        if ($invocation.Source[$name] -cne $State.ScopedPublication[$name]) { return $false }
    }
    # The exact pinned program qualified gflag with the selected full module
    # path. Only its held-target typed readback supplies flag evidence; .chain
    # remains an advisory display, not source selection or loading authority.
    return $invocation.Pid -eq $State.Target.pid -and $invocation.Born -eq $State.Target.born `
        -and $invocation.Helper -eq $State.Target.helper -and $invocation.EventPid -eq $State.Target.pid `
        -and $null -ne $arm -and $arm.Kind -eq 7 -and $arm.Version -eq 4 -and $arm.Event -eq 1 `
        -and $arm.Pid -eq $invocation.Pid -and $arm.EventPid -eq $invocation.Pid `
        -and $State.ScopedStages -eq 3 -and $arm.Stages -eq 3 `
        -and $arm.Flags -ge 0 -and $arm.Flags -le [uint32]::MaxValue -and ($arm.Flags -band 2) -eq 2 `
        -and (Get-HermesStartupObservation $State 'SdkCommandErrors' 15) -eq 0
}

function Test-HermesStartupScopedAction {
    param([hashtable]$State, [ValidateSet('Arm', 'Target', 'Side')][string]$Role)
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or -not $State.Stopped `
        -or -not (Test-HermesStartupNativeReadinessBinding $State) -or $State.Timeout -ne 0 `
        -or $State.CleanupFault -ne 0 -or $State.NativeRowsFault -ne 0 `
        -or (Get-HermesStartupObservation $State 'SdkCommandErrors' 15) -ne 0 `
        -or $State.InputClosed -or $State.Out.End -or $State.Out.Faulted `
        -or $State.Err.Faulted -or $State.Out.Tail -cnotmatch '\A[0-9]{1,3}:[0-9a-f]{3}> \z' `
        -or $null -ne $State.Inventory.Block -or $null -eq $State.Held -or $State.Held.Released `
        -or $State.InputTasks.Count -ge 50 `
        -or ($null -ne $State.InputPending -and -not $State.InputPending.IsCompletedSuccessfully)) { return $false }
    $held = $State.Held
    if ($held.Event -ne 1 -or $held.EventPid -ne $held.Pid -or -not $held.Valid `
        -or $State.Inventory.Limit -ne 0 -or $State.Inventory.Malformed -ne 0 `
        -or $State.Inventory.Mismatch -ne 0 -or $State.Inventory.Root -ne $State.Target.helper) { return $false }
    if ($Role -ceq 'Side') {
        return $State.SideWrites -lt 48 -and $State.Inventory.Subjects.ContainsKey([string]$held.Pid) `
            -and -not ($held.Pid -eq $State.Target.pid -and $held.Kind -eq 1) `
            -and ($held.Pid -ne $State.Target.pid -or $State.Continued) `
            -and $held.Kind -in @(1, 3, 5)
    }
    # CREATE_PROCESS is the pre-user-mode stop, not a surrogate breakpoint:
    # https://learn.microsoft.com/windows/win32/debug/debugging-events
    if (-not (Test-HermesStartupScopedReceipt $State) -or -not (Test-HermesStartupCreatedBinding $State) `
        -or $held.Kind -ne 1 -or $held.Version -ne 3 `
        -or $held.Pid -ne $State.Target.pid -or $null -ne $State.Final `
        -or $State.Exits.ContainsKey([string]$State.Target.pid) `
        -or $State.Exits.ContainsKey([string]$State.Target.helper)) { return $false }
    if ($Role -ceq 'Arm') {
        return -not $State.ArmAttempted -and $null -eq $State.ScopedInvocation -and $null -eq $State.Arm `
            -and -not $State.Acked -and -not $State.ContinueAttempted
    }
    return (Test-HermesStartupScopedBinding $State) -and -not $State.Acked `
        -and -not $State.ContinueAttempted -and $null -eq $State.ArmedBinding
}

function Close-HermesStartupScopedInput {
    param([hashtable]$State, [IO.StreamWriter]$Writer)
    $closed = $true
    # The writer owns the live handle: dispose it before its BaseStream.
    foreach ($owned in @($Writer, $Writer.BaseStream)) {
        try { $owned.Dispose() } catch { $closed = $false; $State.CleanupFault = 13 }
    }
    $State.InputClosed = $closed
    foreach ($pending in $State.InputTasks) {
        try {
            if (-not $pending.Wait(5000)) { $State.CleanupFault = 13 }
        } catch {
            if (-not $pending.IsCompleted) { $State.CleanupFault = 13 }
        }
    }
}

function Invoke-HermesStartupScopedInput {
    param([hashtable]$State, [IO.StreamWriter]$Writer,
        [ValidateSet('Arm', 'Target', 'Side')][string]$Role)
    if (-not (Test-HermesStartupScopedAction $State $Role) `
        -or ($null -ne $State.InputWriter -and -not [object]::ReferenceEquals($State.InputWriter, $Writer))) {
        throw 'Scoped input refused.'
    }
    $State.InputWriter = $Writer
    # Closed capability: one fixed script invocation or one literal g. No
    # caller-supplied command, path, PID, selector, or second writer is accepted.
    $command = "g`n"
    switch ($Role) {
        Arm {
            $State.ArmAttempted = $true
            $State.ScopedInvocation = @{
                Pid = $State.Target.pid; EventPid = $State.Held.EventPid
                Born = $State.Target.born; Helper = $State.Target.helper
                Source = $State.ScopedPublication.Clone()
            }
            # Execute the same pinned program as literal lines, with command
            # echo. The remaining line is one fixed filename, without arguments.
            # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/-----------------------a---run-script-file-
            $command = '$$<' + (Join-Path $State.ScopedPublication.Root 'startup-arm.cdb') + "`n"
        }
        Target {
            $State.ArmedBinding = @{ Helper = $State.Target.helper; Pid = $State.Target.pid
                Born = $State.Target.born; Flags = $State.Arm.Flags }
            $State.Acked = $true
            $State.ContinueAttempted = $true
            $State.Held.Released = $true
        }
        Side { $State.SideWrites++; $State.Held.Released = $true }
    }
    $State.Stopped = $false
    $State.Out.Tail = '' # Consume only the already-accounted-for stopped prompt.
    $bytes = [Text.Encoding]::ASCII.GetBytes($command)
    $cancellation = [Threading.CancellationTokenSource]::new(2000)
    $failed = $false
    try {
        $State.InputPending = $Writer.BaseStream.WriteAsync($bytes, 0, $bytes.Length, $cancellation.Token)
        $State.InputTasks.Add($State.InputPending)
        if (-not $State.InputPending.Wait(2000)) { throw 'Scoped input deadline.' }
        $null = $State.InputPending.GetAwaiter().GetResult()
        if ($Role -ceq 'Arm') { $State.ArmWritten = $true }
        if ($Role -ceq 'Target') { $State.Continued = $true }
    } catch {
        $failed = $true
        $State.Error = 13
        try { $cancellation.Cancel() } catch { $State.CleanupFault = 13 }
        throw 'Scoped input failed.'
    } finally {
        if ($failed -or ($Role -ceq 'Side' -and $State.Exits.ContainsKey([string]$State.Target.pid) `
            -and $State.Exits.ContainsKey([string]$State.Target.helper))) {
            Close-HermesStartupScopedInput $State $Writer
        }
        $cancellation.Dispose()
    }
}

function Set-HermesStartupProtocolFault {
    param([hashtable]$State, [ValidateRange(1, 15)][int]$Reason,
        [ValidateRange(0, 255)][int]$EchoMask = 0, [string]$RefusedLine = '')
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        if ($State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608) { return }
        if ($State.Protocol -eq 0 -and $State.ScopedFault.Reason -eq 0) {
            if ($Reason -in @(1, 14) -and $RefusedLine.Length -gt 0) {
                Set-HermesStartupScopedQueryRefusal $State $RefusedLine
            }
            $block = $State.Inventory.Block; $held = $State.Held
            $State.ScopedFault = @{
                Reason = $Reason; Phase = $State.ScopedPhase; Echo = $EchoMask
                Block = $(if ($null -ne $block -and $block.Kind -in @(1, 3, 5, 7)) { $block.Kind } else { 0 })
                Held = $(if ($null -ne $held -and $held.Kind -in @(1, 3, 5)) { $held.Kind } else { 0 })
            }
        }
    }
    $State.Protocol = 1
}

function Add-HermesStartupScopedMarker {
    param([hashtable]$State, [int]$Version, [int]$Kind, [uint32]$Pid)
    if ($State.Protocol -ne 0 -or $State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608) { return }
    # Seen, not accepted: 1 arm-open, 2 arm-close, 4 typed candidate,
    # 8 parsed/scoped DWORD. These bits never authorise an action.
    if ($Version -eq 4 -and $Kind -eq 7) { $State.ScopedPhase = $State.ScopedPhase -bor 1 }
    if ($Version -eq 4 -and $Kind -eq 8) { $State.ScopedPhase = $State.ScopedPhase -bor 2 }
    $inventory = $State.Inventory
    if ($Kind -eq 0 -and $Version -eq 1) {
        if ($State.Initial -ne 0 -or $inventory.Root -ne 0 -or $null -ne $inventory.Block) {
            Set-HermesStartupProtocolFault $State 9; return
        }
        $State.Initial = $Pid; $inventory.Root = $Pid
        return
    }
    $valid = ($Kind -in @(1, 2, 5, 6) -and $Version -eq 3) `
        -or ($Kind -in @(3, 4) -and $Version -eq 1) -or ($Kind -in @(7, 8) -and $Version -eq 4)
    if (-not $valid) { Set-HermesStartupProtocolFault $State 2; return }
    if ($State.Initial -eq 0) { Set-HermesStartupProtocolFault $State 9; return }
    if ($Kind -in @(1, 3, 5, 7)) {
        $arming = $Kind -eq 7 -and $State.ArmWritten -and $null -eq $State.Arm `
            -and $null -ne $State.ScopedInvocation -and $Pid -eq $State.ScopedInvocation.Pid `
            -and $null -ne $State.Held -and -not $State.Held.Released -and $State.Held.Kind -eq 1
        if ($null -ne $inventory.Block `
            -or ($Kind -ne 7 -and $null -ne $State.Held -and -not $State.Held.Released)) {
            Set-HermesStartupProtocolFault $State 3; return
        }
        if (($Kind -eq 7 -and -not $arming) -or $State.Exits.ContainsKey([string]$Pid)) {
            Set-HermesStartupProtocolFault $State 9; return
        }
        $inventory.Block = @{ Kind = $Kind; Version = $Version; Pid = $Pid
            EventPid = [uint32]0; Event = 0; Valid = $true; Code = [long]0; Flags = [long]-1; Released = $false }
        if ($Kind -eq 7) { $inventory.Block.Stages = 0 }
        $State.Stopped = $false
        return
    }
    $block = $inventory.Block
    if ($null -eq $block -or $block.Kind -ne $Kind - 1 -or $block.Pid -ne $Pid `
        -or $block.Version -ne $Version -or $block.Event -ne 1 -or -not $block.Valid `
        -or $block.EventPid -ne $Pid) { Set-HermesStartupProtocolFault $State 4; return }
    if ($Kind -eq 8) {
        $inventory.Block = $null
        $State.Arm = $block
        if ($block.Stages -ne 3 -or $State.ScopedStages -ne 3 `
            -or $block.Flags -lt 0 -or ($block.Flags -band 2) -ne 2) { $State.Unknown = 1 }
        return
    }
    Complete-HermesStartupInventoryBlock $inventory $Kind $Pid $Version
    if ($inventory.Limit -ne 0) { Set-HermesStartupProtocolFault $State 10; return }
    $subject = $inventory.Subjects[[string]$Pid]
    if ($subject.Create -gt 1 -or $subject.Exit -gt 1 -or $subject.Initial -gt 1 `
        -or ($Pid -ne $State.Initial -and $block.Kind -ne 1 -and $subject.Create -ne 1)) {
        Set-HermesStartupProtocolFault $State 10; return
    }
    if ($Kind -eq 4) { $State.Exits[[string]$Pid] = $block.Code }
    $State.Held = $block
    $State.Stopped = $false
    Set-HermesStartupScopedCreated $State
}

function Test-HermesStartupScopedArmFrame {
    param([hashtable]$State)
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or $State.Bounds -ne 0 `
        -or $State.AcceptedUtf8Bytes -ge 8388608 -or -not $State.ArmAttempted -or -not $State.ArmWritten `
        -or -not (Test-HermesStartupScopedReceipt $State) -or -not (Test-HermesStartupCreatedBinding $State) `
        -or $State.Acked -or $State.ContinueAttempted -or $null -ne $State.Arm -or $null -ne $State.Final `
        -or $State.DebuggerExitAvailable -ne 0 -or $State.InputClosed -or $State.Out.End `
        -or $State.Out.Faulted -or $State.Err.Faulted) { return $false }
    $block = $State.Inventory.Block; $held = $State.Held; $invocation = $State.ScopedInvocation
    if ($null -eq $block -or $block.Kind -ne 7 -or $block.Version -ne 4 -or -not $block.Valid `
        -or $block.Event -ne 1 -or $block.Pid -ne $State.Target.pid -or $block.EventPid -ne $block.Pid `
        -or $null -eq $held -or $held.Kind -ne 1 -or $held.Version -ne 3 -or -not $held.Valid `
        -or $held.Released -or $held.Event -ne 1 -or $held.Pid -ne $block.Pid -or $held.EventPid -ne $block.Pid `
        -or $null -eq $invocation -or $invocation.Source -isnot [hashtable] `
        -or $invocation.Pid -ne $block.Pid -or $invocation.EventPid -ne $block.Pid `
        -or $invocation.Born -ne $State.Target.born -or $invocation.Helper -ne $State.Target.helper `
        -or $State.Exits.ContainsKey([string]$State.Target.pid) `
        -or $State.Exits.ContainsKey([string]$State.Target.helper)) { return $false }
    foreach ($name in $State.ScopedPublication.Keys) {
        if ($invocation.Source[$name] -cne $State.ScopedPublication[$name]) { return $false }
    }
    return $true
}

function Set-HermesStartupScopedQueryRefusal {
    param([hashtable]$State, [string]$Line)
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or $State.Protocol -ne 0 `
        -or $State.ScopedFault.Reason -ne 0 -or $State.ScopedQueryFault.Kind -ne 0 `
        -or $State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608 -or $Line.Length -gt 4096 `
        -or $State.Unknown -ne 0 -or $State.Error -ne 0 -or $State.Timeout -ne 0 `
        -or $State.CleanupFault -ne 0 -or $State.NativeRowsFault -ne 0 -or $State.TraceFaulted `
        -or $State.DebuggerKillRequested -ne 0 -or $State.Stopped -or $null -ne $State.Failure `
        -or $State.ScopedStages -ne 1 -or $null -eq $State.Inventory.Block `
        -or $State.Inventory.Block.Kind -ne 7 `
        -or $State.Inventory.Block.Stages -ne 1 -or $State.Inventory.Block.Flags -ne -1) { return }
    if ((Get-HermesStartupObservation $State 'SdkCommandErrors' 15) -ne 0 `
        -or -not (Test-HermesStartupScopedArmFrame $State)) { return }
    # Fixed-source comparisons describe this refusal only. Neither a family
    # label nor an exact displayed source supplies query or flag authority.
    $query = '.catch { .if (@@c++(sizeof(@$peb->NtGlobalFlag)) == 4) { .printf "HERMES_NTGLOBALFLAG v=1 pid=%u value=%08x\n", @$tpid, @@c++(@$peb->NtGlobalFlag); }; .echo HERMES_ARM_STAGE v=1 stage=2; }'
    # The exact read region includes this fixed program's statement terminator.
    $read = '.printf "HERMES_NTGLOBALFLAG v=1 pid=%u value=%08x\n", @$tpid, @@c++(@$peb->NtGlobalFlag);'
    $display = $Line -creplace '\A[0-9]{1,3}:[0-9a-f]{3}> ', ''
    # HorizontalTrim removes edge ASCII spaces/tabs only, after prompt removal.
    $horizontalTrim = $display.Trim([char[]]@(' ', [char]9))
    $source = $horizontalTrim
    $rules = @(
        @("\AType information missing error at '(?<source>[\x20-\x7e]{1,4096})'\z", 2),
        @("\AMemory access error at '(?<source>[\x20-\x7e]{1,4096})'\z", 3),
        @("\ASyntax error (?:in|at) '(?<source>[\x20-\x7e]{1,4096})'\z", 4),
        @("\ANumeric expression missing from '(?<source>[\x20-\x7e]{1,4096})'\z", 5),
        @("\ACouldn't resolve error at '(?<source>[\x20-\x7e]{1,4096})'\z", 5)
    )
    $kind = 1
    # HorizontalTrim is observation-only, as for the existing stage shape.
    if ($source -ceq $query -or $source -ceq $read) { $kind = 6 }
    else {
        $source = ''
        foreach ($rule in $rules) {
            if ($display -cmatch $rule[0]) { $kind = $rule[1]; $source = $Matches.source; break }
        }
    }
    $region = if ($source -ceq $query) { 1 } elseif ($source -ceq $read) { 2 } else { 0 }
    # Separate display facts: neither trimming nor lexical presence relabels
    # Kind/Source or supplies SDK, typed-field, query or continuation authority.
    $trimKind = 0
    foreach ($rule in $rules) {
        if ($horizontalTrim -cmatch $rule[0]) { $trimKind = $rule[1]; break }
    }
    $lexical = 0
    foreach ($token in @(
        @('bad register', 1), @('type information', 2), @('memory access', 4),
        @('syntax error', 8), @('numeric expression', 16), @('resolve', 16), @('symbol', 32)
    )) {
        if ($display.Contains($token[0], [StringComparison]::OrdinalIgnoreCase)) {
            $lexical = $lexical -bor $token[1]
        }
    }
    foreach ($token in @(
        @('sizeof(@$peb->NtGlobalFlag)', 64), @('@@c++(@$peb->NtGlobalFlag)', 128)
    )) {
        if ($display.Contains($token[0], [StringComparison]::Ordinal)) { $lexical = $lexical -bor $token[1] }
    }
    if ($horizontalTrim -cmatch '\A\^(?:[ \t]|\z)') { $lexical = $lexical -bor 256 }
    if ($display -cne $horizontalTrim) { $lexical = $lexical -bor 512 }
    $State.ScopedQueryFault = @{ Kind = $kind; Source = $region; Lexical = $lexical; TrimKind = $trimKind }
}

function Get-HermesStartupScopedStageShape {
    param([hashtable]$State, [string]$Line)
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or $State.Protocol -ne 0 `
        -or $State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608 -or $Line.Length -gt 4096) { return 0 }
    $display = $Line -creplace '\A[0-9]{1,3}:[0-9a-f]{3}> ', ''
    $mask = if ($display -cne $Line) { 2 } else { 0 }
    if ($display -cmatch '\AHERMES_ARM_STAGE(?:[ \t]|\z)') { $mask = $mask -bor 1 }
    if ($display.Contains('.echo', [StringComparison]::Ordinal)) { $mask = $mask -bor 4 }
    foreach ($rule in @(
        @("(?:\A|[ \t;])Syntax error in '[\x20-\x7e]{1,4096}'(?:\z|[ \t;])", 8),
        @("(?:\A|[ \t;])Syntax error at '[\x20-\x7e]{1,4096}'(?:\z|[ \t;])", 16),
        @('\A[ \t]*\^(?:[ \t]|\z)', 32),
        @("(?:\A|[ \t;])Numeric expression missing from '[\x20-\x7e]{1,4096}'(?:\z|[ \t;])", 64),
        @("(?:\A|[ \t;])Couldn't resolve error at '[\x20-\x7e]{1,4096}'(?:\z|[ \t;])", 128),
        @('(?:\A|[ \t;])Type ntdll!_PEB not found(?:\z|[ \t;])', 128)
    )) {
        if ($display -cmatch $rule[0]) { $mask = $mask -bor $rule[1] }
    }
    # HorizontalTrim: remove edge ASCII spaces/tabs from the prompt-stripped
    # display only. No collapse, case or escape changes; never acceptance.
    $horizontalTrim = $display.Trim([char[]]@(' ', [char]9))
    if ($horizontalTrim -ceq 'HERMES_ARM_STAGE v=1 stage=1') { $mask = $mask -bor 256 }
    if ($horizontalTrim -ceq 'HERMES_ARM_STAGE v=1 stage=2') { $mask = $mask -bor 512 }
    return $mask
}

function Set-HermesStartupScopedStageRefusal {
    param([hashtable]$State, [ValidateRange(1, 5)][int]$Reason, [string]$Line = '')
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or $State.Protocol -ne 0 `
        -or $State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608 `
        -or $State.ScopedStageFault.Reason -ne 0) { return }
    $State.ScopedStageFault = @{ Reason = $Reason; Shape = (Get-HermesStartupScopedStageShape $State $Line) }
}

function Add-HermesStartupScopedStage {
    param([hashtable]$State, [ValidateSet(1, 2)][int]$Stage, [string]$Line = '')
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or $State.Protocol -ne 0 `
        -or $State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608) { return }
    if (-not (Test-HermesStartupScopedArmFrame $State)) {
        Set-HermesStartupScopedStageRefusal $State 2 $Line
        Set-HermesStartupProtocolFault $State 15; return
    }
    $block = $State.Inventory.Block
    # Missing completion is a contained-stage outcome, not a forged record.
    # SDK cannot return after the query; query can return without a field.
    if ($block.Stages -ne $State.ScopedStages -or ($State.ScopedStages -band $Stage) -ne 0 `
        -or ($Stage -eq 1 -and ($State.ScopedStages -ne 0 -or $block.Flags -ne -1))) {
        $reason = if ($block.Stages -ne $State.ScopedStages) { 3 } `
            elseif (($State.ScopedStages -band $Stage) -ne 0) { 4 } else { 5 }
        Set-HermesStartupScopedStageRefusal $State $reason $Line
        Set-HermesStartupProtocolFault $State 15; return
    }
    $State.ScopedStages = $State.ScopedStages -bor $Stage
    $block.Stages = $State.ScopedStages
}

function Get-HermesStartupScopedEchoMask {
    param([hashtable]$State, [string]$Line)
    if ($State.DiagnosticMode -cne 'ScopedCreateV1' -or $State.Protocol -ne 0 `
        -or $State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608 -or $Line.Length -gt 4096) { return 0 }
    $echo = $Line -creplace '\A[0-9]{1,3}:[0-9a-f]{3}> ', ''
    $mask = if ($echo -cne $Line) { 1 } else { 0 }
    $lexical = $echo.TrimStart([char[]]@(' ', [char]9))
    foreach ($rule in @(
        @('\A\.catch(?:[ \t{]|\z)', 2), @('\A\.if(?:[ \t(]|\z)', 4), @('\A\.printf(?:[ \t]|\z)', 8),
        @("(?:\A|[ \t;])Syntax error at '[\x20-\x7e]{1,4096}'(?:\z|[ \t;])", 16)
    )) {
        if ($lexical -cmatch $rule[0]) { $mask = $mask -bor $rule[1] }
    }
    if ($echo.Contains('No export gflag found', [StringComparison]::Ordinal)) { $mask = $mask -bor 32 }
    if ($echo.Contains("Couldn't resolve error at ", [StringComparison]::Ordinal) `
        -or $echo.Contains('Type ntdll!_PEB not found', [StringComparison]::Ordinal)) { $mask = $mask -bor 64 }
    # Named observation-only transform: HorizontalWhitespaceCollapse replaces
    # every ASCII space/tab run with one space, then trims edge spaces/tabs.
    # It changes neither case nor escapes and never expands accepted echoes.
    $display = [regex]::Replace($echo, '[ \t]+', ' ').Trim([char[]]@(' ', [char]9))
    if ($State.ScopedEchoes.Count -le 32) {
        foreach ($source in $State.ScopedEchoes) {
            if ($source.Length -gt 4096) { continue }
            $collapsed = [regex]::Replace($source, '[ \t]+', ' ').Trim([char[]]@(' ', [char]9))
            if ([string]::Equals($display, $collapsed, [StringComparison]::Ordinal)) {
                $mask = $mask -bor 128; break
            }
        }
    }
    return $mask
}

function Add-HermesStartupScopedLine {
    param([hashtable]$State, [string]$Line)
    if ($State.Protocol -ne 0 -or $State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608) { return }
    $wasStopped = $State.Stopped
    Set-HermesStartupStoppedPrompt $State $Line
    Add-HermesStartupSdkError $State $Line
    Add-HermesStartupChainReadback $State $Line
    Add-HermesStartupFilterReadback $State $Line
    if ($Line -ceq 'Current expression evaluator: MASM - Microsoft Assembler expressions' `
        -or $Line -ceq 'Current expression evaluator: C++ - C++ source expressions') {
        # First exact stdout fact in the bound post-SDK arm frame only.
        # Never a typed value, stage completion, evaluator-cause or action gate.
        # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/-expr--choose-expression-evaluator-
        if ($State.DiagnosticMode -ceq 'ScopedCreateV1' -and $State.ScopedEvaluator -eq 0 `
            -and $State.ScopedFault.Reason -eq 0 -and $State.ScopedStageFault.Reason -eq 0 `
            -and $State.ScopedQueryFault.Kind -eq 0 -and $State.Unknown -eq 0 `
            -and $State.Error -eq 0 -and $State.Timeout -eq 0 -and $State.CleanupFault -eq 0 `
            -and $State.NativeRowsFault -eq 0 -and -not $State.TraceFaulted `
            -and $State.DebuggerKillRequested -eq 0 -and -not $wasStopped -and -not $State.Stopped `
            -and -not $State.InputClosed -and -not $State.Out.End -and -not $State.Out.Faulted -and -not $State.Err.Faulted `
            -and -not $State.Acked -and -not $State.ContinueAttempted -and $null -eq $State.Final `
            -and $State.DebuggerExitAvailable -eq 0 `
            -and $null -eq $State.Failure -and $State.ScopedStages -eq 1 `
            -and $null -ne $State.Inventory.Block -and $State.Inventory.Block.Kind -eq 7 `
            -and $State.Inventory.Block.Stages -eq 1 -and $State.Inventory.Block.Flags -eq -1 `
            -and (Get-HermesStartupObservation $State 'SdkCommandErrors' 15) -eq 0 `
            -and (Test-HermesStartupScopedArmFrame $State)) {
            $State.ScopedEvaluator = if ($Line -ceq 'Current expression evaluator: MASM - Microsoft Assembler expressions') { 1 } else { 2 }
        }
        return
    }
    if ($Line.Contains('.printf', [StringComparison]::Ordinal)) {
        $echo = $Line -creplace '\A[0-9]{1,3}:[0-9a-f]{3}> ', ''
        if (-not $State.ScopedEchoes.Contains($echo)) {
            # A closed display shape, not a diagnosis or a new SDK-success
            # rule. Both shapes retain exactly the same source-echo refusal.
            $reason = if ($echo -cmatch "\ASyntax error at '[\x20-\x7e]{1,4096}'\z") { 14 } else { 1 }
            if ($Line.Contains('HERMES_ARM_STAGE', [StringComparison]::OrdinalIgnoreCase)) {
                Set-HermesStartupScopedStageRefusal $State 1 $Line
            }
            $refusedLine = if ($wasStopped) { '' } else { $Line }
            Set-HermesStartupProtocolFault $State $reason (Get-HermesStartupScopedEchoMask $State $Line) $refusedLine
        }
        return
    }
    if ($Line.Contains('HERMES_ARM_STAGE', [StringComparison]::OrdinalIgnoreCase)) {
        $echo = $Line -creplace '\A[0-9]{1,3}:[0-9a-f]{3}> ', ''
        if ($State.ScopedEchoes.Contains($echo)) { return }
        if ($Line -cmatch '\AHERMES_ARM_STAGE v=1 stage=(?<s>[12])\z') {
            Add-HermesStartupScopedStage $State ([int]$Matches.s) $Line
        } else {
            Set-HermesStartupScopedStageRefusal $State 1 $Line
            Set-HermesStartupProtocolFault $State 15
        }
        return
    }
    if ($Line -cmatch '\AHERMES_CDB v=(?<v>[134]) kind=(?<k>[0-8]) pid=(?<p>[1-9][0-9]{0,9})\z' `
        -and [long]$Matches.p -le [uint32]::MaxValue) {
        Add-HermesStartupScopedMarker $State ([int]$Matches.v) ([int]$Matches.k) ([uint32]$Matches.p)
        return
    }
    if ($Line.Contains('HERMES_CDB', [StringComparison]::Ordinal)) { Set-HermesStartupProtocolFault $State 2; return }
    if ($Line.Contains('Last event:', [StringComparison]::OrdinalIgnoreCase)) {
        Add-HermesStartupInventoryLastEvent $State.Inventory $Line
        if ($State.Inventory.Malformed -ne 0 -or $State.Inventory.Mismatch -ne 0) {
            $reason = if ($State.Inventory.Mismatch -ne 0) { 6 } else { 5 }
            Set-HermesStartupProtocolFault $State $reason
        }
        return
    }
    if ($Line.Contains('HERMES_NTGLOBALFLAG', [StringComparison]::Ordinal)) {
        $State.ScopedPhase = $State.ScopedPhase -bor 4
        $block = $State.Inventory.Block
        if ($Line -cnotmatch '\AHERMES_NTGLOBALFLAG v=1 pid=(?<p>[1-9][0-9]{0,9}) value=(?<f>[0-9a-f]{8})\z') {
            Set-HermesStartupProtocolFault $State 7; return
        }
        if ($null -eq $block -or $block.Kind -ne 7 -or $block.Version -ne 4 `
            -or -not $State.ArmWritten -or $null -eq $State.ScopedInvocation -or $null -eq $State.Target `
            -or [long]$Matches.p -ne $State.Target.pid -or [long]$Matches.p -ne $block.Pid `
            -or $block.Event -ne 1 -or $block.EventPid -ne $block.Pid `
            -or $State.Acked -or $null -ne $State.Final `
            -or $State.Exits.ContainsKey([string]$State.Target.pid) `
            -or $State.Exits.ContainsKey([string]$State.Target.helper)) { Set-HermesStartupProtocolFault $State 9; return }
        if ($block.Flags -ne -1) { Set-HermesStartupProtocolFault $State 8; return }
        if (($State.ScopedStages -band 2) -ne 0) {
            Set-HermesStartupScopedStageRefusal $State 5 $Line
            Set-HermesStartupProtocolFault $State 15; return
        }
        $block.Flags = [long][Convert]::ToUInt32($Matches.f, 16)
        $State.ScopedPhase = $State.ScopedPhase -bor 8
        return
    }
    Add-HermesStartupFailure $State $Line
}

function Set-HermesStartupDebuggerExit {
    param([hashtable]$State, [object]$Value)
    if (($Value -isnot [int] -and $Value -isnot [long]) -or [long]$Value -lt [int]::MinValue `
        -or [long]$Value -gt [uint32]::MaxValue) {
        $State.DebuggerExitAvailable = 0; $State.DebuggerExitValue = [long]0
        $State.Error = 13
        return
    }
    $State.DebuggerExitAvailable = 1
    $State.DebuggerExitValue = if ([long]$Value -lt 0) { [long]$Value + 4294967296 } else { [long]$Value }
}

function Test-HermesStartupScopedFinal {
    param([hashtable]$State)
    if (-not (Test-HermesStartupScopedReceipt $State -Historical)) { return $false }
    $row = Get-HermesStartupInventoryNativeRow $State Final
    if ($null -eq $row -or $row.resumed -ne 1) { return $false }
    return $State.RowsText -ceq ($State.NativeRowSnapshots.Ready.Text + "`n" `
        + $State.NativeRowSnapshots.Resumed.Text + "`n" + $State.NativeRowSnapshots.Final.Text + "`n")
}

function Add-HermesStartupLine {
    param([hashtable]$State, [string]$Line)
    if ($State.NativeProtocol -and $State.Bounds -ne 0) { return }
    if ($Line.Length -gt 4096) { $State.Bounds = 1; return }
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        Add-HermesStartupScopedLine $State $Line
        return
    }
    if ($State.DiagnosticMode -ceq 'Inventory') { Add-HermesStartupInventoryLine $State $Line }
    Set-HermesStartupStoppedPrompt $State $Line
    Add-HermesStartupSdkError $State $Line
    Add-HermesStartupChainReadback $State $Line
    Add-HermesStartupFilterReadback $State $Line
    if ($Line.Contains('HERMES_NTGLOBALFLAG', [StringComparison]::Ordinal)) {
        if (-not $Line.Contains('.printf', [StringComparison]::Ordinal)) {
            Add-HermesStartupTypedFlags $State $Line
        }
        return
    }
    if ($Line -cmatch '\AHERMES_CDB v=(?<version>[123]) kind=(?<kind>[0-6]) pid=(?<pid>[1-9][0-9]{0,9})\z') {
        $version = [int]$Matches.version
        $kind = [int]$Matches.kind
        $pidValue = [long]$Matches.pid
        if ($kind -le 4) { Add-HermesStartupObservation $State "Marker$kind" }
        if ($pidValue -gt [uint32]::MaxValue) {
            Add-HermesStartupObservation $State 'MarkerMalformed'
            if ($State.Observations.MarkerMalformedKind -eq 0) {
                $State.Observations.MarkerMalformedKind = 2
            }
            $State.Protocol = 1
            return
        }
        Add-HermesStartupMarker $State $kind ([uint32]$pidValue) $version
        return
    }
    if ($Line.Contains('HERMES_CDB', [StringComparison]::Ordinal) `
        -and $Line.Contains('.printf', [StringComparison]::Ordinal)) {
        Add-HermesStartupObservation $State 'MarkerEcho'
    }
    if ($Line.Contains('HERMES_CDB', [StringComparison]::Ordinal) `
        -and -not $Line.Contains('.printf', [StringComparison]::Ordinal)) {
        Add-HermesStartupObservation $State 'MarkerMalformed'
        if ($State.Observations.MarkerMalformedKind -eq 0) {
            $State.Observations.MarkerMalformedKind = 1
        }
        $State.Protocol = 1
        return
    }
    # Candidate labels are observations only; the strict parsers below still
    # decide acceptance, including their original case and prefix requirements.
    if ($Line.Contains('Last event:', [StringComparison]::OrdinalIgnoreCase)) {
        Add-HermesStartupObservation $State 'LastEventSeen'
        if ($Line.Contains('Create process', [StringComparison]::OrdinalIgnoreCase)) {
            Add-HermesStartupObservation $State 'CreateEventSeen'
        }
        if ($Line.Contains('Exit process', [StringComparison]::OrdinalIgnoreCase)) {
            Add-HermesStartupObservation $State 'ExitEventSeen'
        }
    }
    if ($Line.StartsWith('Last event:', [StringComparison]::Ordinal)) {
        Add-HermesStartupLastEvent $State $Line
        return
    }
    if ($Line.Contains(
            'Current NtGlobalFlag contents:', [StringComparison]::OrdinalIgnoreCase)) {
        Add-HermesStartupObservation $State 'FlagsSeen'
    }
    if ($null -ne $State.Block -and $State.Block.Kind -eq 1 `
        -and $State.Block.Version -eq 1 `
        -and $Line -cmatch '\A\s*Current NtGlobalFlag contents:\s*(?:0x)?(?<flags>[0-9a-fA-F]{1,8})\s*\z') {
        $State.Block.Flags = [Convert]::ToUInt32($Matches.flags, 16)
        Add-HermesStartupObservation $State 'FlagsAccepted'
        return
    }
    if ($Line -match '\AModLoad:') { return }
    Add-HermesStartupFailure $State $Line
}

function Add-HermesStartupText {
    param([hashtable]$State, [hashtable]$Stream, [string]$Chunk, [bool]$Parse)
    if ($State.Bounds -ne 0) { return }
    if ($State.AcceptedUtf8Bytes -ge 8388608) { $State.Bounds = 1; return }
    if ($Chunk.Length -gt 4096) { $State.Bounds = 1; return }
    # One 8 MiB budget for accepted UTF-8 bytes across stdout AND stderr.
    # Encode chunks independently, as for hashing: split surrogates are charged
    # conservatively for their replacement bytes, never as a cheaper pair.
    $byteCount = [Text.Encoding]::UTF8.GetByteCount($Chunk)
    if ($byteCount -gt (8388608 - $State.AcceptedUtf8Bytes)) {
        $State.Bounds = 1
        return
    }
    $State.AcceptedUtf8Bytes += $byteCount
    $Stream.Count += $Chunk.Length
    if ($State.NativeProtocol -and $State.AcceptedUtf8Bytes -ge 8388608) {
        $State.Bounds = 1
        return
    }
    $Stream.Hash.AppendData([Text.Encoding]::UTF8.GetBytes($Chunk))
    $keep = [Math]::Min(4096 - $Stream.Prefix.Length, $Chunk.Length)
    if ($keep -gt 0) { $Stream.Prefix += $Chunk.Substring(0, $keep) }
    # The last admitted byte is accounted for, but exhaustion ends collection.
    if ($State.AcceptedUtf8Bytes -ge 8388608) { $State.Bounds = 1; return }
    $pieces = @($Chunk -split "`n")
    for ($index = 0; $index -lt $pieces.Count; $index++) {
        $last = $index -eq $pieces.Count - 1
        if (-not $Stream.DiscardLine) {
            $Stream.Tail += $pieces[$index]
            if ($Stream.Tail.Length -gt 4096) {
                if ($Parse -or $State.DiagnosticMode -ceq 'ScopedCreateV1') { $State.Bounds = 1 }
                $Stream.Tail = ''
                $Stream.DiscardLine = $true
                if ($State.Bounds -ne 0) { return }
            }
        }
        if ($Parse -and -not $Stream.DiscardLine) {
            Set-HermesStartupStoppedPrompt $State $Stream.Tail
        }
        if (-not $last) {
            if (-not $Stream.DiscardLine) {
                $line = $Stream.Tail.TrimEnd("`r")
                if ($Parse) { Add-HermesStartupLine $State $line }
                else { Add-HermesStartupSdkError $State $line }
            }
            $Stream.Tail = ''
            $Stream.DiscardLine = $false
        }
    }
}

function New-HermesStartupObservations {
    param([hashtable]$State)
    $observations = [ordered]@{}
    foreach ($name in @('Marker0', 'Marker1', 'Marker2', 'Marker3', 'Marker4',
            'MarkerMalformed', 'MarkerEcho', 'LastEventSeen', 'CreateEventSeen',
            'CreateEventAccepted', 'ExitEventSeen', 'ExitEventAccepted',
            'FlagsSeen', 'FlagsAccepted')) {
        $observations[$name] = Get-HermesStartupObservation $State $name
    }
    # Message-presence bits only: 1 = LoadLibrary failure, 2 = missing gflag
    # export, 4 = unreadable PEB, 8 = ntdll symbol load failure. Zero means
    # none recognised, not success. Stdout/stderr never supply new authority.
    $observations.SdkCommandErrors = Get-HermesStartupObservation $State 'SdkCommandErrors' 15
    # First recognised LoadLibrary failure only; later errors cannot replace it.
    # Win32 error is uint32-bounded; zero also means unavailable. Path shape:
    # 0 = unbound/unavailable, 1/2 = exact selected path bare/literal quotes,
    # 3/4 = stripped backslashes bare/quoted, 5/6 = doubled bare/quoted, 7 = other.
    # These exact source comparisons do not normalise or authorise any path.
    $observations.SdkLoadLibraryWin32Error =
        Get-HermesStartupObservation $State 'SdkLoadLibraryWin32Error' ([long][uint32]::MaxValue)
    $observations.SdkLoadLibraryPathShape = Get-HermesStartupObservation $State 'SdkLoadLibraryPathShape' 7
    # Root .chain: 0 = unavailable; 1 = header only/no supported selected row;
    # 2 = exact selected full path; 3 = source case/forward-separator display.
    # A listed module is not flag-2, target-binding, or arming evidence.
    $observations.SdkSelectedExtensionChain = Get-HermesStartupObservation $State 'SdkSelectedExtensionChain' 3
    # First LoadLibrary failure relation: 0 = unknown/unbound; 1..7 = selected
    # full path, forward slashes, stripped/doubled backslashes, first whitespace
    # token, unencoded quoted/unquoted .load text. 8..19 = stem/.dll pairs:
    # exts, ext, ntsdexts, gflag, dbghelp, uext. 20..23 = source SDK-relative
    # winext/ext.dll, winxp/ntsdexts.dll, dbghelp.dll, winext/uext.dll.
    # Form 1..7 = bare, quotes both/leading/trailing, escaped quotes both,
    # one leading/trailing space; add 8 for case-only display differences.
    $observations.SdkLoadLibraryRelation = Get-HermesStartupObservation $State 'SdkLoadLibraryRelation' 23
    $form = Get-HermesStartupObservation $State 'SdkLoadLibraryForm' 15
    $observations.SdkLoadLibraryForm = if ($form -eq 8) { 0 } else { $form }
    # Untrusted first-failure lexical facts only. Module 0 = other/unavailable;
    # 1..12 = exts, ext, ntsdexts, gflag, dbghelp, uext, dbgeng, symsrv,
    # gflags, cdb, kext, kdexts. Suffix 1..5 = stem, .dll, .dll.dll, .exe,
    # other dot suffix; 0 = no recognised stem. No private leaf is published.
    $observations.SdkLoadLibraryLexicalModule =
        Get-HermesStartupObservation $State 'SdkLoadLibraryLexicalModule' 12
    $observations.SdkLoadLibraryLexicalSuffix =
        Get-HermesStartupObservation $State 'SdkLoadLibraryLexicalSuffix' 5
    # Display bits: 1 = compared with verified source; 2 = lexical full match;
    # 4/8 = proper source prefix/suffix (at least 3 characters); 16 = .load
    # prefix removed; 32 = boundary quotes/padding removed; 64 = backslashes
    # before quotes removed; 128 = mixed/repeated slash display collapsed;
    # 256 = exterior padding removed; 512 = full match differs in case.
    # These transforms do NOT establish valid serialization or a loaded module.
    $observations.SdkLoadLibraryDisplay = Get-HermesStartupObservation $State 'SdkLoadLibraryDisplay' 1023
    # Nonblank root .chain lines vs rows supported by the original metadata
    # grammar. Collection ends at marker-0 output/echo, never reads stderr.
    foreach ($name in @('SdkChainLines', 'SdkChainSupportedRows')) {
        $observations[$name] = Get-HermesStartupObservation $State $name
    }
    # 0 = no recognised header; 1..5 = break/second-chance/output/ignore/unknown.
    # Add 8 only for the immediately following, exact pinned callback command.
    # Neither the filter nor callback readback proves that an event occurred.
    foreach ($name in @('CprFilter', 'EprFilter')) {
        $value = Get-HermesStartupObservation $State $name 13
        $observations[$name] = if ($value -in @(0, 1, 2, 3, 4, 5, 9, 10, 11, 12, 13)) {
            $value
        } else { 0 }
    }
    # Immediate next line only: 0 = unavailable; 1/2 = exact Command payload
    # quoted/unquoted; 3/4 = same with Command1; 5 = empty command; 6 = different
    # payload; 7 = next filter-shaped header; 8 = blank; 9 = other line.
    foreach ($name in @('CprCommandShape', 'EprCommandShape')) {
        $observations[$name] = Get-HermesStartupObservation $State $name 9
    }
    # Transform 0 = unmatched/unavailable; 1..4 = literal raw/quoted/escaped
    # quotes/doubled quotes; 5..7 = doubled backslashes raw/quoted/escaped
    # quotes; 8/9 = removed backslashes raw/quoted; 10/11 = known one-character
    # C escapes raw/quoted; 12/13 = also drop unknown escape backslashes;
    # 14/15 = single dollar raw/quoted; 16/17 = four dollars raw/quoted;
    # 18/19 = no dollars raw/quoted. These do not accept transformed commands.
    foreach ($name in @('CprCommandTransform', 'EprCommandTransform')) {
        $observations[$name] = Get-HermesStartupObservation $State $name 19
    }
    # UTF-16 code units: trimmed readback payload (including displayed quotes)
    # versus the exact unquoted source command from the verified publication.
    foreach ($name in @('CprPayloadLength', 'EprPayloadLength', 'CprSourceLength', 'EprSourceLength')) {
        $observations[$name] = Get-HermesStartupObservation $State $name 4096
    }
    # First malformed marker: 0 = none/unknown, 1 = syntax, 2 = PID range.
    $observations.MarkerMalformedKind = Get-HermesStartupObservation $State 'MarkerMalformedKind' 2
    $observations.LastEventUnrecognized = [Math]::Max(0,
        $observations.LastEventSeen - $observations.CreateEventAccepted - $observations.ExitEventAccepted)
    foreach ($name in @('CreateEvent', 'ExitEvent', 'Flags')) {
        $observations[$name + 'Unrecognized'] = [Math]::Max(0,
            $observations[$name + 'Seen'] - $observations[$name + 'Accepted'])
    }
    # Parser acceptance is not target binding. These are current snapshots,
    # not a reconstructed context for a previously rejected/cleared block.
    foreach ($name in @('Arm', 'Block')) {
        $binding = $State[$name]
        $observations[$name + 'Present'] = [int]($null -ne $binding)
        $observations[$name + 'Kind'] = if ($null -ne $binding `
            -and $binding.Kind -is [int] -and $binding.Kind -in @(1, 3, 5)) { $binding.Kind } else { 0 }
        $observations[$name + 'CurrentMatchesTarget'] = [int]($null -ne $binding `
            -and $null -ne $State.Target -and $binding.Pid -eq $State.Target.pid)
        $observations[$name + 'EventMatchesTarget'] = [int]($null -ne $binding `
            -and $null -ne $State.Target -and $binding.EventPid -eq $State.Target.pid)
        $observations[$name + 'Flag2'] = [int]($null -ne $binding `
            -and $binding.Flags -ge 0 -and $binding.Flags -le [uint32]::MaxValue `
            -and ($binding.Flags -band 2) -eq 2)
    }
    $observations.TargetRowPresent = [int]($null -ne $State.Target)
    $observations.FinalRowPresent = [int]($null -ne $State.Final)
    if ($State.NativeProtocol) {
        # Latch parser transitions and the actual driver's first Ready check
        # separately: a single row snapshot can contain both Ready and Final.
        foreach ($name in @('ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst', 'CreatedCheckFirst')) {
            $facts = Get-HermesStartupObservation $State $name 16383
            $required = switch ($name) {
                'CreateCloseFirst' { 5 }; 'CreatedJointFirst' { 7 }; default { 3 }
            }
            $observations[$name] = if (($facts -band $required) -eq $required) { $facts } else { 0 }
        }
        foreach ($name in @('CreatedCheckSeen', 'CreatedPublishAttempted')) {
            $observations[$name] = Get-HermesStartupObservation $State $name 1
        }
        foreach ($name in @('RowsReads', 'RowsMissing', 'RowsEmpty')) {
            $observations[$name] = Get-HermesStartupObservation $State $name
        }
        foreach ($name in @('RowsFirstDataLength', 'RowsFirstDataRead', 'RowsReadyRead')) {
            $observations[$name] = Get-HermesStartupObservation $State $name 2049
        }
        foreach ($name in @('OutReadStartMax', 'ErrReadStartMax', 'CreateReadAge', 'CreateParseWork',
                'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay')) {
            $observations[$name] = Get-HermesStartupObservation $State $name 6
        }
        $observations.CreatedAcknowledged = [int]$State.CreatedAcked
        $observations.ResumedRowPresent = [int]($null -ne $State.Resumed)
        $observations.InitialCallback = [int]($null -ne $State.Arm -and $State.Arm.Kind -eq 5)
        $observations.InitialStopped = [int]$State.Stopped
        $observations.Continued = [int]$State.Continued
        $observations.InputClosed = [int]$State.InputClosed
        $observations.IbpBreak = [int]$State.IbpBreak
        $observations.IbpCommandVerified = [int]$State.FilterVerified.Ibp
        $observations.LastEventUnrecognized = [Math]::Max(0,
            $observations.LastEventUnrecognized - $observations.InitialCallback)
    }
    # Actual DWORD only after current/event/helper/held-child row binding.
    # Availability distinguishes a measured zero from no field read at all.
    $typedFlags = Get-HermesStartupTypedFlags $State
    $observations.TypedFlagsAvailable = [int]($typedFlags -ge 0)
    $observations.TypedFlagsValue = if ($typedFlags -ge 0) { $typedFlags } else { 0 }
    $observations.ParserUnknown = [int]($State.Unknown -ne 0)
    $observations.ParserProtocol = [int]($State.Protocol -ne 0)
    return $observations
}

function Get-HermesStartupInventoryNumber {
    param($Value, [long]$Maximum = 8388608)
    if (($Value -is [int] -or $Value -is [long]) -and $Value -ge 0 -and $Value -le $Maximum) {
        return $Value
    }
    return 0
}

function Get-HermesStartupInventoryNativeRow {
    param([hashtable]$State, [ValidateSet('Ready', 'Final', 'Resumed')][string]$Name)
    $scoped = $State.DiagnosticMode -ceq 'ScopedCreateV1'
    if ($scoped) {
        if (-not (Test-HermesStartupScopedPublication $State) -or $State.NativeRowsFault -ne 0) { return $null }
    } elseif ($Name -ceq 'Ready' -or -not (Test-HermesStartupInventoryBinding $State)) { return $null }
    $field = if ($Name -ceq 'Ready') { 'Target' } else { $Name }
    $live = $State[$field]
    if ($live -isnot [hashtable]) { return $null }
    $snapshot = $State.NativeRowSnapshots[$Name]
    if ($snapshot -isnot [hashtable] -or $snapshot['Text'] -isnot [string] `
        -or $snapshot['Text'] -cne $State[$field + 'Text']) { return $null }
    $text = $snapshot['Text']
    if ($State.RowsText -isnot [string] `
        -or -not $State.RowsText.Contains($text + "`n", [StringComparison]::Ordinal)) { return $null }
    # These scalar copies were made by the actual bounded native-row parser.
    # Reporting never re-parses output, including after budget exhaustion.
    $row = $snapshot['Row']
    $stage = if ($Name -ceq 'Final') { 3 } elseif ($Name -ceq 'Ready') { 1 } else { 2 }
    if ($row -isnot [hashtable] -or $row['version'] -ne 2 -or $row['stage'] -ne $stage) { return $null }
    $fields = $row.Keys
    if ($scoped) {
        $fields = @('version', 'stage', 'helper', 'pid', 'born', 'image', 'debug', 'context',
            'exit', 'parent', 'cleanup', 'fault', 'cleanupFault', 'resumed')
        if ($row.Count -ne $fields.Count -or $live.Count -ne $fields.Count) { return $null }
    }
    foreach ($field in $fields) {
        if (-not $row.ContainsKey($field) -or -not $live.ContainsKey($field)) { return $null }
        if ($scoped -and (($row[$field] -isnot [int] -and $row[$field] -isnot [long]) `
            -or ($live[$field] -isnot [int] -and $live[$field] -isnot [long]))) { return $null }
        if ($live[$field] -ne $row[$field]) { return $null }
    }
    foreach ($field in @('helper', 'pid', 'born', 'image', 'debug', 'context')) {
        if ($row[$field] -ne $State.Target[$field]) { return $null }
    }
    return $row
}

function New-HermesStartupInventorySummary {
    param([hashtable]$State, [bool]$DebuggerEnded, [bool]$DrainsEnded)
    $inventory = $State.Inventory
    $bound = Test-HermesStartupInventoryBinding $State
    $final = Get-HermesStartupInventoryNativeRow $State 'Final'
    $resumed = Get-HermesStartupInventoryNativeRow $State 'Resumed'
    $result = [ordered]@{
        complete = 0; status = 2; binding = [int]$bound
        subjects = [Math]::Min(16, $inventory.Subjects.Count); otherSubjects = 0
        malformed = (Get-HermesStartupInventoryNumber $inventory.Malformed)
        mismatch = (Get-HermesStartupInventoryNumber $inventory.Mismatch)
        limit = [int]($inventory.Limit -ne 0 -or $inventory.Subjects.Count -gt 16)
        openBlock = [int]($null -ne $inventory.Block)
        partialLine = [int]($State.Out.Tail.Length -ne 0); unclassifiedEvents = 0
        nativeFinalBound = [int]($null -ne $final); nativeResumedReceipt = [int]($null -ne $resumed)
        nativeFinalExit = 0; nativeFinalResumed = 0; nativeFinalFault = 0
        nativeFinalCleanup = 0; nativeFinalCleanupFault = 0; nativeFinalParent = 0
        ownedCleanup = [int]($DebuggerEnded -and $DrainsEnded -and $State.Out.End -and $State.Err.End `
            -and $State.InputClosed -and $State.CleanupFault -eq 0)
    }
    foreach ($prefix in @('target', 'helper', 'other')) {
        foreach ($name in @('Create', 'CreateRepeat', 'Exit', 'Initial')) { $result[$prefix + $name] = 0 }
    }
    $allExited = $true
    $counterFault = $inventory.Malformed -isnot [int] -or $inventory.Mismatch -isnot [int] `
        -or $inventory.Malformed -lt 0 -or $inventory.Malformed -gt 8388608 `
        -or $inventory.Mismatch -lt 0 -or $inventory.Mismatch -gt 8388608
    foreach ($entry in $inventory.Subjects.GetEnumerator() | Select-Object -First 16) {
        $subject = $entry.Value
        if ((Get-HermesStartupInventoryNumber $subject.Exit) -eq 0) { $allExited = $false }
        $prefix = if (-not $bound) { '' } elseif ($entry.Key -ceq [string]$State.Target.pid) { 'target' } `
            elseif ($entry.Key -ceq [string]$State.Target.helper) { 'helper' } else { 'other' }
        if ($prefix -ceq 'other') { $result.otherSubjects++ }
        foreach ($name in @('Create', 'Exit', 'Initial')) {
            $count = Get-HermesStartupInventoryNumber $subject[$name]
            if ($subject[$name] -isnot [int] -or $subject[$name] -lt 0 -or $subject[$name] -gt 8388608) {
                $counterFault = $true
            }
            $key = if ($prefix -ceq '') { 'unclassifiedEvents' } else { $prefix + $name }
            $result[$key] = [Math]::Min(8388608, $result[$key] + $count)
        }
        if ($prefix -cne '') {
            $key = $prefix + 'CreateRepeat'
            $result[$key] = [Math]::Min(8388608,
                $result[$key] + [Math]::Max(0, (Get-HermesStartupInventoryNumber $subject.Create) - 1))
        }
    }
    if ($null -ne $final) {
        foreach ($pair in @(
            @('Exit', 'exit', [long][uint32]::MaxValue), @('Resumed', 'resumed', 1), @('Fault', 'fault', 65535),
            @('Cleanup', 'cleanup', 1), @('CleanupFault', 'cleanupFault', 65535), @('Parent', 'parent', 1)
        )) { $result['nativeFinal' + $pair[0]] = Get-HermesStartupInventoryNumber $final[$pair[1]] $pair[2] }
    }
    $missing = -not $bound -or $null -eq $final -or $null -eq $resumed -or -not $allExited `
        -or $result.targetCreate -eq 0 -or $result.targetInitial -eq 0 `
        -or $result.targetExit -eq 0 -or $result.helperExit -eq 0 `
        -or $result.openBlock -ne 0 -or $result.partialLine -ne 0
    # Inventory completeness is not trace or native acceptance. Strict parser
    # faults remain visible, but cannot stop this independent bounded ledger.
    $result.status = if ($State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608) { 7 } `
        elseif ($State.Timeout -ne 0 -or $result.nativeFinalFault -in @(1223, 1460)) { 5 } `
        elseif ($State.Error -ne 0 -or $result.nativeFinalFault -ne 0) { 8 } `
        elseif ($result.ownedCleanup -ne 1 -or ($null -ne $final `
            -and ($result.nativeFinalCleanup -ne 1 -or $result.nativeFinalCleanupFault -ne 0))) { 6 } `
        elseif ($result.limit -ne 0) { 4 } `
        elseif ($result.malformed -ne 0 -or $result.mismatch -ne 0 -or $inventory.RootFault -ne 0 `
            -or $State.NativeRowsFault -ne 0 -or $counterFault) { 3 } `
        elseif ($missing) { 2 } elseif ($result.nativeFinalParent -ne 1) { 8 } else { 1 }
    $result.complete = [int]($result.status -eq 1)
    return $result
}

function New-HermesStartupReport {
    param([hashtable]$State, [bool]$DebuggerEnded, [bool]$DrainsEnded)
    $point = if ($State.TraceFaulted) { $State.TraceFaultPoint } else { $State.TracePoint }
    $role = if ($State.TraceFaulted) { $State.TraceFaultRole } else { $State.ToolRole }
    $traceValid = Test-HermesStartupTracePoint $point $role
    $target = $State.Target
    $final = $State.Final
    $scopedFinalBound = $false
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        $scopedFinalBound = Test-HermesStartupScopedFinal $State
        $final = if ($scopedFinalBound) { $State.NativeRowSnapshots.Final.Row } else { $null }
    }
    $armed = Test-HermesStartupArmed $State
    $targetExit = $null
    $helperExit = $null
    if ($null -ne $target) {
        $targetExit = $State.Exits[[string]$target.pid]
        $helperExit = $State.Exits[[string]$target.helper]
    }
    $events = $null -ne $final -and $null -ne $targetExit `
        -and $null -ne $helperExit -and $targetExit -eq $final.exit
    $cleanup = $events -and $final.cleanup -eq 1 -and $DebuggerEnded `
        -and $DrainsEnded -and $State.CleanupFault -eq 0 -and $final.cleanupFault -eq 0
    $parent = $null -ne $final -and $final.parent -eq 1
    $fault = if ($null -ne $final) { $final.fault } else { 0 }
    $cleanupFault = if ($null -ne $final -and $final.cleanupFault -ne 0) {
        $final.cleanupFault
    } else { $State.CleanupFault }
    $protocol = $State.Protocol -ne 0 -or $null -ne $State.Block `
        -or ($null -ne $final -and -not $events) `
        -or ($null -ne $helperExit -and $helperExit -ne 0)
    $continued = -not $State.NativeProtocol -or ($State.Continued -and $State.InputClosed `
        -and $null -ne $State.InputPending -and $State.InputPending.IsCompleted)
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        $continued = $continued -and $State.InputTasks.Count -gt 0 `
            -and @($State.InputTasks | Where-Object { -not $_.IsCompletedSuccessfully }).Count -eq 0
        $protocol = $protocol -or $null -ne $State.Inventory.Block -or -not $scopedFinalBound
    }
    $evidence = $armed -and $continued -and $State.Acked -and $State.Unknown -eq 0 `
        -and $null -ne $State.Failure
    $status = if ($State.Bounds -ne 0 -or $State.AcceptedUtf8Bytes -ge 8388608) { 7 } `
        elseif ($State.Timeout -ne 0 -or $fault -in @(1223, 1460)) { 5 } `
        elseif ($State.Error -ne 0 -or $fault -ne 0 -or -not $traceValid) { 8 } `
        elseif ($protocol) { 4 } `
        elseif (-not $armed -or $State.Unknown -ne 0) { 3 } `
        elseif (-not $cleanup -or -not $parent) { 6 } `
        elseif (-not $evidence) { 2 } else { 1 }
    $failure = if ($evidence) { $State.Failure } else {
        @{ Kind = 0; Status = 0; ModuleCategory = 0; ModuleSha256 = '' }
    }
    $report = [ordered]@{
        v = 1; advisory = 1; nativeAcceptance = 0; originalGatePassed = 0
        completed = [int]($status -eq 1); status = $status
        tracePoint = $(if ($traceValid) { $point } else { 0 })
        toolRole = $(if ($traceValid) { $role } else { 0 })
        helperMatched = [int]($null -ne $target -and $State.Initial -eq $target.helper)
        targetMatched = [int]($null -ne $target -and $null -ne $State.Arm `
            -and $State.Arm.Pid -eq $target.pid)
        armingVerified = [int]($armed -and $State.Acked)
        firstFailureKind = $failure.Kind; firstFailureStatus = $failure.Status
        moduleCategory = $failure.ModuleCategory; moduleSha256 = $failure.ModuleSha256
        exitObserved = [int]($null -ne $targetExit)
        exitCode = $(if ($null -ne $targetExit) { $targetExit } else { 0 })
        powershellExit37 = [int]($null -ne $targetExit -and $targetExit -eq 37)
        debuggerChangedFailure = [int]($null -ne $targetExit -and $targetExit -ne 3221225794)
        parentUnchanged = [int]$parent; cleanupComplete = [int]$cleanup
        error = $(if ($State.Error -ne 0) { $State.Error } `
            elseif (-not $traceValid) { 13 } else { $fault })
        cleanupFault = $cleanupFault
        outputBudgetBytes = 8388608; acceptedUtf8Bytes = $State.AcceptedUtf8Bytes
        # Chars are accepted UTF-16 code units, not bytes or rejected output.
        # Dropped counts only accepted characters not retained in the prefix.
        outChars = $State.Out.Count; outKept = $State.Out.Prefix.Length
        outDropped = $State.Out.Count - $State.Out.Prefix.Length
        errChars = $State.Err.Count; errKept = $State.Err.Prefix.Length
        errDropped = $State.Err.Count - $State.Err.Prefix.Length
        outSha256 = $State.Out.Fingerprint; errSha256 = $State.Err.Fingerprint
        debuggerSha256 = $State.DebuggerSha256
        helperSha256 = $State.HelperSha256; commandSha256 = $State.CommandSha256
        createCommandSha256 = $State.CreateCommandSha256
        exitCommandSha256 = $State.ExitCommandSha256
        initialCommandSha256 = $State.InitialCommandSha256
        observations = (New-HermesStartupObservations $State)
    }
    if ($State.DiagnosticMode -ceq 'Inventory') {
        $summary = New-HermesStartupInventorySummary $State $DebuggerEnded $DrainsEnded
        $report.observationOnly = 1
        $report.inventory = $summary
        foreach ($name in @('completed', 'armingVerified', 'firstFailureKind', 'firstFailureStatus',
                'moduleCategory', 'exitObserved', 'exitCode', 'powershellExit37', 'debuggerChangedFailure', 'targetMatched')) {
            $report[$name] = 0
        }
        $report.moduleSha256 = ''
        $report.status = if ($summary.status -eq 1) { 3 } else { $summary.status }
        $report.helperMatched = $summary.binding
        $report.cleanupComplete = [int]($summary.ownedCleanup -eq 1 -and $summary.nativeFinalBound -eq 1 `
            -and $summary.nativeFinalCleanup -eq 1 -and $summary.nativeFinalCleanupFault -eq 0)
        $report.cleanupFault = if ($State.CleanupFault -ne 0) { $State.CleanupFault } else { $summary.nativeFinalCleanupFault }
        $report.parentUnchanged = $summary.nativeFinalParent
        $report.error = if ($State.Error -ne 0) { $State.Error } else { $summary.nativeFinalFault }
        # Keep the lower-permit phases and strict refusal observations without
        # exporting the private per-PID ledger or enlarging the 4096-byte report.
        $selected = [ordered]@{}
        foreach ($name in @(
            'Marker0', 'Marker1', 'Marker2', 'Marker3', 'Marker4', 'MarkerMalformed',
            'LastEventSeen', 'CreateEventAccepted', 'ExitEventAccepted', 'LastEventUnrecognized',
            'ParserUnknown', 'ParserProtocol', 'ReadyFirst', 'CreateCloseFirst', 'CreatedJointFirst',
            'CreatedCheckFirst', 'CreatedPublishAttempted', 'CreatedAcknowledged', 'ResumedRowPresent',
            'RowsReads', 'RowsMissing', 'RowsEmpty', 'RowsReadyRead',
            'CprFilter', 'EprFilter', 'IbpBreak', 'IbpCommandVerified', 'InputClosed',
            'OutReadStartMax', 'ErrReadStartMax', 'CreateReadAge', 'CreateParseWork',
            'CreateReceiveDelay', 'CreateRowsWork', 'CreateCheckDelay'
        )) { $selected[$name] = $report.observations[$name] }
        $report.observations = $selected
    }
    if ($State.DiagnosticMode -ceq 'ScopedCreateV1') {
        $report.v = 2
        $report.scopedCreateVersion = 1
        $report.armCommandSha256 = $State.ArmCommandSha256
        $report.extensionSha256 = $State.ExtensionSha256
        $report.driverSha256 = $State.DriverSha256
        $report.moduleSourceSha256 = $State.ModuleSha256
        $report.debuggerExitAvailable = $State.DebuggerExitAvailable
        $report.debuggerExitValue = $State.DebuggerExitValue
        $report.debuggerKillRequested = Get-HermesStartupInventoryNumber $State.DebuggerKillRequested 1
        # Non-zero CDB exits remain errors, even with bound advisory evidence.
        # Do not infer that a debugger propagated the target's exit status.
        if (($State.DebuggerExitAvailable -ne 1 -or $State.DebuggerExitValue -ne 0) `
            -and $report.status -notin @(5, 7)) {
            $report.status = 8; $report.completed = 0
            if ($report.error -eq 0) { $report.error = 13 }
        }
        $selected = [ordered]@{}
        foreach ($name in @('ParserUnknown', 'ParserProtocol', 'CreatedAcknowledged',
                'ResumedRowPresent', 'InputClosed', 'SdkCommandErrors', 'SdkSelectedExtensionChain')) {
            $selected[$name] = $report.observations[$name]
        }
        $selected.armInvocations = [int]$State.ArmAttempted
        $selected.targetContinuations = [int]$State.ContinueAttempted
        $selected.sideContinuations = $State.SideWrites
        $selected.subjects = $State.Inventory.Subjects.Count
        $selected.inputTasks = $State.InputTasks.Count
        # Reason: 1 echo, 2 marker grammar/version, 3 open sequence, 4 close
        # binding, 5 last-event shape/sequence, 6 event identity, 7 typed format,
        # 8 typed duplicate, 9 scope/sequence, 10 subject replay/limit,
        # 11 native rows, 12 readiness ack, 13 failure scope,
        # 14 syntax-shaped unknown printf; 15 stage grammar/scope/sequence.
        # Completion bits are bound earlier observations, never flag evidence:
        # 1 SDK returned; 2 query returned (possibly without an available field).
        $selected.scopedStages = Get-HermesStartupInventoryNumber $State.ScopedStages 3
        # Exact .expr output after accepted SDK1: 0 unavailable, 1 MASM, 2 C++.
        # Advisory only; neither startup argv nor command echo is readback.
        $selected.scopedEvaluator = Get-HermesStartupInventoryNumber $State.ScopedEvaluator 2
        $selected.scopedPhase = Get-HermesStartupInventoryNumber $State.ScopedPhase 15
        $selected.scopedFirstReason = Get-HermesStartupInventoryNumber $State.ScopedFault.Reason 15
        $selected.scopedFirstPhase = Get-HermesStartupInventoryNumber $State.ScopedFault.Phase 15
        $selected.scopedFirstBlock = Get-HermesStartupInventoryNumber $State.ScopedFault.Block 7
        $selected.scopedFirstHeld = Get-HermesStartupInventoryNumber $State.ScopedFault.Held 5
        # Echo mask: 1 prompt; 2/4/8 leading .catch/.if/.printf; 16 contained
        # syntax shape; 32 no-export text; 64 closed resolver text;
        # 128 exact source after HorizontalWhitespaceCollapse. Never authority.
        $selected.scopedFirstEchoMask = Get-HermesStartupInventoryNumber $State.ScopedFault['Echo'] 255
        # First stage refusal: 0 unavailable; 1 record grammar (including
        # diagnostic/source shapes); 2 the unchanged arm-frame guard refused;
        # 3 state mismatch; 4 duplicate; 5 order. Never completion evidence.
        $selected.scopedStageFirstReason = Get-HermesStartupInventoryNumber $State.ScopedStageFault.Reason 5
        # Shape bits: 1 anchored prefix after optional CDB prompt; 2 prompt;
        # 4 .echo; 8/16 quoted Syntax error in/at; 32 leading caret token;
        # 64 quoted numeric-expression diagnostic; 128 closed resolver shape;
        # 256/512 exact SDK/query marker after observation-only HorizontalTrim.
        $selected.scopedStageFirstShape = Get-HermesStartupInventoryNumber $State.ScopedStageFault.Shape 1023
        # First unknown-printf refusal, only after accepted SDK completion in
        # the still-bound arm frame: 0 unavailable; 1 unknown; 2 type-information
        # missing; 3 memory-access; 4 syntax; 5 expression/resolver; 6 fixed source.
        # These are anchored display families, never a runtime diagnosis.
        $selected.scopedReadFirstKind = Get-HermesStartupInventoryNumber $State.ScopedQueryFault.Kind 6
        # Exact source regions only: 0 other/unknown; 1 whole fixed query;
        # 2 its fixed printf/read command. No substring establishes a region.
        $selected.scopedReadFirstSource = Get-HermesStartupInventoryNumber $State.ScopedQueryFault.Source 2
        # Lexical display bits only, never causes: ordinal-ignore-case wording
        # 1 bad register; 2 type information; 4 memory access; 8 syntax error;
        # 16 numeric expression/resolve; 32 symbol. Exact-case source fragments:
        # 64 sizeof(@$peb->NtGlobalFlag); 128 @@c++(@$peb->NtGlobalFlag).
        # 256 leading caret token after HorizontalTrim; 512 edge ASCII whitespace.
        $selected.scopedReadFirstLexical = Get-HermesStartupInventoryNumber $State.ScopedQueryFault['Lexical'] 1023
        # Existing closed diagnostic family after HorizontalTrim only: 0 none;
        # 2 type information; 3 memory access; 4 syntax; 5 expression/resolver.
        # Case/escapes/internal whitespace unchanged. Kind/Source stay original.
        $trimKind = Get-HermesStartupInventoryNumber $State.ScopedQueryFault['TrimKind'] 5
        $selected.scopedReadFirstTrimKind = if ($trimKind -ge 2) { $trimKind } else { 0 }
        $report.observations = $selected
    }
    return $report
}

Export-ModuleMember -Function *-HermesStartup*
