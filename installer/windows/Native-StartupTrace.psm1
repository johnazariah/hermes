#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-HermesStartupStream {
    @{
        Count = [long]0; Prefix = ''; Tail = ''; DiscardLine = $false
        End = $false; Pending = $null; Source = $null; Faulted = $false
        Bytes = [byte[]]::new(4096); Buffer = [char[]]::new(4096)
        Decoder = [Text.UTF8Encoding]::new($false, $true).GetDecoder()
        Hash = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        Fingerprint = ''
    }
}

function New-HermesStartupState {
    param([switch]$LegacyParserControls)
    @{
        NativeProtocol = -not $LegacyParserControls
        Created = $null; CreatedBinding = $null; CreatedAcked = $false
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
            'CprPayloadLength', 'EprPayloadLength', 'CprSourceLength', 'EprSourceLength')]
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
    if ($State.Bounds -ne 0) { return }
    if ($Text.Length -gt 2048 -or $Text -cmatch '[^\x20-\x7e\n]' `
        -or -not $Text.StartsWith($State.RowsText, [StringComparison]::Ordinal)) {
        $State.Protocol = 1
        return
    }
    $State.RowsText = $Text
    $lines = @($Text -split "`n")
    $complete = @($lines | Select-Object -SkipLast 1)
    if ($complete.Count -gt 3 -or $lines[-1].Length -gt 384) {
        $State.Protocol = 1
        return
    }
    $previous = 0
    foreach ($line in $complete) {
        $row = ConvertFrom-HermesStartupRow $line
        if ($null -eq $row -or $row.version -ne 2 -or $row.stage -le $previous `
            -or ($row.stage -eq 2 -and $previous -ne 1)) {
            $State.Protocol = 1
            return
        }
        if ($null -ne $State.Target) {
            foreach ($name in @('helper', 'pid', 'born', 'image', 'debug', 'context')) {
                if ($row[$name] -ne $State.Target[$name]) { $State.Protocol = 1; return }
            }
        }
        switch ($row.stage) {
            1 { $State.Target = $row; $State.TargetText = $line }
            2 { $State.Resumed = $row; $State.ResumedText = $line }
            3 {
                if (($previous -eq 2 -and $row.resumed -ne 1) `
                    -or ($previous -eq 0 -and $row.resumed -ne 0)) {
                    $State.Protocol = 1
                    return
                }
                $State.Final = $row; $State.FinalText = $line
            }
        }
        $previous = $row.stage
    }
    if ($previous -eq 3 -and $lines[-1].Length -ne 0) { $State.Protocol = 1 }
}

function Test-HermesStartupCreatedBinding {
    param([hashtable]$State)
    $target = $State.Target
    $created = $State.Created
    return $State.NativeProtocol -and $State.Protocol -eq 0 -and $State.Unknown -eq 0 `
        -and $State.Bounds -eq 0 -and $null -ne $target -and $null -ne $created `
        -and $State.Initial -eq $target.helper -and $target.pid -ne $target.helper `
        -and $target.born -gt 0 -and $target.image -eq 1 -and $target.debug -eq 1 `
        -and $target.context -eq 1 -and $created.Kind -eq 1 -and $created.Version -eq 3 `
        -and $created.Event -eq 1 -and $created.Pid -eq $target.pid `
        -and $created.EventPid -eq $target.pid `
        -and $State.FilterVerified.Cpr -and $State.FilterVerified.Ibp -and $State.IbpBreak `
        -and $State.FilterVerified.Epr -and $State.Observations.CprFilter -eq 9 `
        -and $State.Observations.EprFilter -eq 9 `
        -and $State.CommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
        -and $State.CreateCommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
        -and $State.InitialCommandSha256 -cmatch '\A[0-9a-f]{64}\z' `
        -and $State.ExitCommandSha256 -cmatch '\A[0-9a-f]{64}\z'
}

function Test-HermesStartupCreated {
    param([hashtable]$State)
    return (Test-HermesStartupCreatedBinding $State) `
        -and -not $State.CreatedAcked -and $null -eq $State.CreatedBinding `
        -and $null -eq $State.Block -and $null -eq $State.Final `
        -and $null -eq $State.Resumed `
        -and -not $State.Exits.ContainsKey([string]$State.Target.pid) `
        -and -not $State.Exits.ContainsKey([string]$State.Target.helper)
}

function Set-HermesStartupCreatedAcknowledged {
    param([hashtable]$State)
    if (-not (Test-HermesStartupCreated $State)) {
        $State.Protocol = 1
        return
    }
    $State.CreatedBinding = @{
        Helper = $State.Target.helper; Pid = $State.Target.pid; Born = $State.Target.born
        RootHash = $State.CommandSha256; CreateHash = $State.CreateCommandSha256
        InitialHash = $State.InitialCommandSha256; ExitHash = $State.ExitCommandSha256
    }
    $State.CreatedAcked = $true
}

function Test-HermesStartupInitialBinding {
    param([hashtable]$State)
    $bound = $State.CreatedBinding
    $arm = $State.Arm
    if ($null -ne $State.Target -and $null -ne $State.Resumed) {
        foreach ($name in @('helper', 'pid', 'born', 'image', 'debug', 'context')) {
            if ($State.Resumed[$name] -ne $State.Target[$name]) { return $false }
        }
    }
    return (Test-HermesStartupCreatedBinding $State) `
        -and $State.CreatedAcked -and $null -ne $bound -and $null -ne $State.Resumed `
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
        foreach ($owned in @($Writer.BaseStream, $Writer)) {
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
        -or $State.Exits.ContainsKey([string]$State.Target.pid) `
        -or $State.Exits.ContainsKey([string]$State.Target.helper)) {
        $State.Protocol = 1
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
    # Root stdout only, before marker 0; never search-path text or callbacks.
    # Documented full-path row example:
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/-load---loadby--load-extension-dll-
    if ($State.Initial -ne 0 -or $Line.Contains('HERMES_CDB', [StringComparison]::Ordinal)) {
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

function Add-HermesStartupLine {
    param([hashtable]$State, [string]$Line)
    if ($State.NativeProtocol -and $State.Bounds -ne 0) { return }
    if ($Line.Length -gt 4096) { $State.Bounds = 1; return }
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
                if ($Parse) { $State.Bounds = 1 }
                $Stream.Tail = ''
                $Stream.DiscardLine = $true
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

function New-HermesStartupReport {
    param([hashtable]$State, [bool]$DebuggerEnded, [bool]$DrainsEnded)
    $point = if ($State.TraceFaulted) { $State.TraceFaultPoint } else { $State.TracePoint }
    $role = if ($State.TraceFaulted) { $State.TraceFaultRole } else { $State.ToolRole }
    $traceValid = Test-HermesStartupTracePoint $point $role
    $target = $State.Target
    $final = $State.Final
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
    $evidence = $armed -and $continued -and $State.Acked -and $State.Unknown -eq 0 `
        -and $null -ne $State.Failure
    $status = if ($State.Bounds -ne 0) { 7 } `
        elseif ($State.Timeout -ne 0 -or $fault -in @(1223, 1460)) { 5 } `
        elseif ($State.Error -ne 0 -or $fault -ne 0 -or -not $traceValid) { 8 } `
        elseif ($protocol) { 4 } `
        elseif (-not $armed -or $State.Unknown -ne 0) { 3 } `
        elseif (-not $cleanup -or -not $parent) { 6 } `
        elseif (-not $evidence) { 2 } else { 1 }
    $failure = if ($evidence) { $State.Failure } else {
        @{ Kind = 0; Status = 0; ModuleCategory = 0; ModuleSha256 = '' }
    }
    [ordered]@{
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
}

Export-ModuleMember -Function *-HermesStartup*
