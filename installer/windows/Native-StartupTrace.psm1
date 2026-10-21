#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function New-HermesStartupStream {
    @{
        Count = [long]0; Prefix = ''; Tail = ''; DiscardLine = $false
        End = $false; Pending = $null; Buffer = [char[]]::new(4096)
        Hash = [Security.Cryptography.IncrementalHash]::CreateHash(
            [Security.Cryptography.HashAlgorithmName]::SHA256)
        Fingerprint = ''
    }
}

function New-HermesStartupState {
    @{
        Initial = [uint32]0; Arm = $null; Block = $null; Exits = @{}
        Target = $null; Final = $null; TargetText = ''; FinalText = ''
        Acked = $false; ArmedBinding = $null
        Failure = $null; Unknown = 0; Protocol = 0
        Bounds = 0; AcceptedUtf8Bytes = [long]0
        Timeout = 0; Error = 0; CleanupFault = 0
        Out = (New-HermesStartupStream); Err = (New-HermesStartupStream)
        DebuggerSha256 = ''; HelperSha256 = ''; CommandSha256 = ''
    }
}

function ConvertFrom-HermesStartupRow {
    param([string]$Line)
    $pattern = '\AHERMES_STARTUP v=1 stage=(?<stage>[12]) helper=(?<helper>0|[1-9][0-9]{0,9}) pid=(?<pid>0|[1-9][0-9]{0,9}) born=(?<born>0|[1-9][0-9]{0,18}) image=(?<image>[01]) debug=(?<debug>[01]) context=(?<context>[01]) flags=134742020 exit=(?<exit>0|[1-9][0-9]{0,9}) parent=(?<parent>[01]) cleanup=(?<cleanup>[01]) fault=(?<fault>0|[1-9][0-9]{0,4}) cleanupFault=(?<cleanupFault>0|[1-9][0-9]{0,4})\z'
    if ($Line.Length -gt 384 -or $Line -cnotmatch $pattern) { return $null }
    $captures = $Matches
    $row = @{}
    try {
        foreach ($name in @('stage', 'helper', 'pid', 'born', 'image', 'debug',
                'context', 'exit', 'parent', 'cleanup', 'fault', 'cleanupFault')) {
            $row[$name] = [long]::Parse($captures[$name],
                [Globalization.CultureInfo]::InvariantCulture)
        }
    } catch { return $null }
    if ($row.helper -le 0 -or $row.helper -gt [uint32]::MaxValue `
        -or $row.pid -gt [uint32]::MaxValue -or $row.exit -gt [uint32]::MaxValue `
        -or $row.fault -gt 65535 -or $row.cleanupFault -gt 65535) { return $null }
    if ($row.stage -eq 1 -and ($row.pid -eq 0 -or $row.born -eq 0 `
        -or $row.image -ne 1 -or $row.debug -ne 1 -or $row.context -ne 1 `
        -or $row.exit -ne 0 -or $row.parent -ne 0 -or $row.cleanup -ne 0 `
        -or $row.fault -ne 0 -or $row.cleanupFault -ne 0)) { return $null }
    return $row
}

function Update-HermesStartupRows {
    param([hashtable]$State, [string]$Text)
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
    param([hashtable]$State, [int]$Kind, [uint32]$Pid)
    if ($Pid -eq 0) { $State.Protocol = 1; return }
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
                Kind = $Kind; Pid = $Pid; EventPid = [uint32]0
                Flags = [long]-1; Code = [long]-1; Event = 0
            }
        }
        { $_ -in @(2, 4) } {
            $block = $State.Block
            if ($null -eq $block -or $block.Pid -ne $Pid `
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
    if (-not (Test-HermesStartupArmed $State) -or $null -ne $State.Block `
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

function Add-HermesStartupLine {
    param([hashtable]$State, [string]$Line)
    if ($Line.Length -gt 4096) { $State.Bounds = 1; return }
    if ($Line -cmatch '\AHERMES_CDB v=1 kind=(?<kind>[0-4]) pid=(?<pid>[1-9][0-9]{0,9})\z') {
        $kind = [int]$Matches.kind
        $pidValue = [long]$Matches.pid
        if ($pidValue -gt [uint32]::MaxValue) { $State.Protocol = 1; return }
        Add-HermesStartupMarker $State $kind ([uint32]$pidValue)
        return
    }
    if ($Line.Contains('HERMES_CDB', [StringComparison]::Ordinal) `
        -and -not $Line.Contains('.printf', [StringComparison]::Ordinal)) {
        $State.Protocol = 1
        return
    }
    if ($Line.StartsWith('Last event:', [StringComparison]::Ordinal)) {
        Add-HermesStartupLastEvent $State $Line
        return
    }
    if ($null -ne $State.Block -and $State.Block.Kind -eq 1 `
        -and $Line -cmatch '\A\s*Current NtGlobalFlag contents:\s*(?:0x)?(?<flags>[0-9a-fA-F]{1,8})\s*\z') {
        $State.Block.Flags = [Convert]::ToUInt32($Matches.flags, 16)
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
    $Stream.Hash.AppendData([Text.Encoding]::UTF8.GetBytes($Chunk))
    $keep = [Math]::Min(4096 - $Stream.Prefix.Length, $Chunk.Length)
    if ($keep -gt 0) { $Stream.Prefix += $Chunk.Substring(0, $keep) }
    # The last admitted byte is accounted for, but exhaustion ends collection.
    if ($State.AcceptedUtf8Bytes -ge 8388608) { $State.Bounds = 1; return }
    if (-not $Parse) { return }
    $pieces = @($Chunk -split "`n")
    for ($index = 0; $index -lt $pieces.Count; $index++) {
        $last = $index -eq $pieces.Count - 1
        if (-not $Stream.DiscardLine) {
            $Stream.Tail += $pieces[$index]
            if ($Stream.Tail.Length -gt 4096) {
                $State.Bounds = 1
                $Stream.Tail = ''
                $Stream.DiscardLine = $true
            }
        }
        if (-not $last) {
            if (-not $Stream.DiscardLine) {
                Add-HermesStartupLine $State ($Stream.Tail.TrimEnd("`r"))
            }
            $Stream.Tail = ''
            $Stream.DiscardLine = $false
        }
    }
}

function New-HermesStartupReport {
    param([hashtable]$State, [bool]$DebuggerEnded, [bool]$DrainsEnded)
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
    $evidence = $armed -and $State.Acked -and $State.Unknown -eq 0 `
        -and $null -ne $State.Failure
    $status = if ($State.Bounds -ne 0) { 7 } `
        elseif ($State.Timeout -ne 0 -or $fault -in @(1223, 1460)) { 5 } `
        elseif ($State.Error -ne 0 -or $fault -ne 0) { 8 } `
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
        error = $(if ($State.Error -ne 0) { $State.Error } else { $fault })
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
    }
}

Export-ModuleMember -Function *-HermesStartup*
