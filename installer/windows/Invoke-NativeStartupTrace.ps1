#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# No argument, target, debugger, script, symbol server, or command selection.
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -cne 'true' `
    -or $env:RUNNER_OS -cne 'Windows' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' `
    -or $env:GITHUB_JOB -cne 'installer') {
    Write-Output 'HERMES_STARTUP_TRACE v=1 completed=0 status=8 error=5 cleanupFault=0 tracePoint=0 toolRole=0'
    exit 1
}

Import-Module (Join-Path $PSScriptRoot 'Native-StartupTrace.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force

function Assert-TracePath {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $full -ine $Path `
        -or $Path.Length -gt 240 -or $Path -match '["\x00-\x1f]') {
        throw 'Trace path refused.'
    }
    for ($current = $full; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'Trace reparse point refused.'
        }
    }
    return $full
}

function Open-TracePin {
    param([string]$Path)
    $null = Assert-TracePath $Path
    $pin = [IO.File]::Open($Path, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $pins.Add($pin)
    return $pin
}

function Get-TraceHash {
    param([IO.Stream]$Stream)
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        $Stream.Position = 0
        $result = [Convert]::ToHexString($hasher.ComputeHash($Stream)).ToLowerInvariant()
        $Stream.Position = 0
        return $result
    } finally { $hasher.Dispose() }
}

function Test-TraceExtensionFile {
    param([string]$Path)
    return [IO.File]::Exists($Path)
}

function Resolve-TraceExtension {
    param([string]$Kits)
    # Only these SDK-local layouts qualify; never prefer one ambiguous copy.
    $candidates = @(foreach ($relative in @('winxp/exts.dll', 'winext/exts.dll', 'exts.dll')) {
        $candidate = Join-Path $Kits $relative
        if (Test-TraceExtensionFile $candidate) { $candidate }
    })
    if ($candidates.Count -ne 1) { throw 'Unique SDK extension required.' }
    return $candidates[0]
}

function Confirm-TraceTools {
    param([string]$Kits)
    # Roles 1..6: cdb, gflags, dbgeng, dbghelp, symsrv, exts; 0 = no SDK tool.
    $role = 0
    foreach ($relative in @('cdb.exe', 'gflags.exe', 'dbgeng.dll', 'dbghelp.dll',
            'symsrv.dll', 'exts.dll')) {
        $role++
        Set-HermesStartupTracePoint $state 6 $role # Pin.
        $path = if ($role -eq 6) { Resolve-TraceExtension $Kits } `
            else { Join-Path $Kits $relative }
        $pin = Open-TracePin $path
        Set-HermesStartupTracePoint $state 7 $role # Query signature.
        $signature = Get-AuthenticodeSignature -LiteralPath $path
        Set-HermesStartupTracePoint $state 8 $role # Validate signature status.
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
            throw 'Microsoft debugger trust refused.'
        }
        Set-HermesStartupTracePoint $state 9 $role # Validate signer.
        if ($null -eq $signature.SignerCertificate `
            -or $signature.SignerCertificate.Subject -notmatch '(?:^|,\s*)O=Microsoft Corporation(?:,|$)') {
            throw 'Microsoft debugger trust refused.'
        }
        if ($relative -ceq 'cdb.exe') {
            Set-HermesStartupTracePoint $state 10 $role # Hash pinned debugger.
            $state.DebuggerSha256 = Get-TraceHash $pin
        }
    }
    return $path
}

function Assert-TraceCommandPath {
    param([string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path) `
        -or [IO.Path]::GetFullPath($Path) -ine $Path -or $Path.Length -gt 240 `
        -or $Path -cmatch '[";\x00-\x1f\x7f-\uffff]') {
        throw 'Debugger command path refused.'
    }
    return $Path
}

function New-TraceCommands {
    param([string]$Extension)
    $null = Assert-TraceCommandPath $Extension
    $cpr = Assert-TraceCommandPath (Join-Path $root 'startup-cpr.cdb')
    $epr = Assert-TraceCommandPath (Join-Path $root 'startup-epr.cdb')
    # $$>< takes a filename (spaces permitted), not script arguments. Refuse
    # its semicolon separator and the enclosing -c quote delimiter in paths.
    # https://learn.microsoft.com/windows-hardware/drivers/debuggercmds/-----------------------a---run-script-file-
    # The measured -c parse removes unescaped backslashes. Encode only the
    # validated callback paths; sx readback must retain their literal separators.
    # Diagnostic control experiment: force callback breaks, then read filters.
    # Neither registration nor its readback establishes runtime target binding.
    $commands = @(
        ('.load "' + $Extension + '"'),
        '.chain',
        '.printf "HERMES_CDB v=1 kind=0 pid=%u\n", @$tpid',
        'sxd ibp',
        ('sxe -c "$$><' + $cpr.Replace('\', '\\') + '" cpr'),
        ('sxe -c "$$><' + $epr.Replace('\', '\\') + '" epr'),
        'sx',
        'g'
    ) -join "`n"
    $commands += "`n"
    if (@($commands.ToCharArray() | Where-Object { [int]$_ -gt 127 }).Count -ne 0) {
        throw 'Debugger command encoding refused.'
    }
    return $commands
}

function New-TraceEventCommands {
    [ordered]@{
        'startup-cpr.cdb' = (@(
            '.printf "HERMES_CDB v=1 kind=1 pid=%u\n", @$tpid',
            '.lastevent', '|.', '!gflag +sls', '!gflag',
            '.printf "HERMES_CDB v=1 kind=2 pid=%u\n", @$tpid', 'g'
        ) -join "`r`n") + "`r`n"
        'startup-epr.cdb' = (@(
            '.printf "HERMES_CDB v=1 kind=3 pid=%u\n", @$tpid',
            '.lastevent',
            '.printf "HERMES_CDB v=1 kind=4 pid=%u\n", @$tpid', 'g'
        ) -join "`r`n") + "`r`n"
    }
}

function Write-TraceCommandFile {
    param([string]$Path, [byte[]]$Bytes)
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
    [IO.File]::SetAttributes($Path, [IO.FileAttributes]::ReadOnly)
}

function Publish-TraceCommands {
    param([string]$Extension)
    $sources = New-TraceEventCommands
    $sources['startup.cdb'] = New-TraceCommands $Extension
    foreach ($source in $sources.GetEnumerator()) {
        $path = Assert-TraceCommandPath (Join-Path $root $source.Key)
        $bytes = [Text.Encoding]::ASCII.GetBytes($source.Value)
        Write-TraceCommandFile $path $bytes
    }
    $hashes = @{}
    foreach ($source in $sources.GetEnumerator()) {
        $path = Join-Path $root $source.Key
        $bytes = [Text.Encoding]::ASCII.GetBytes($source.Value)
        $pin = Open-TracePin $path
        if ($pin.Length -ne $bytes.Length) { throw 'Debugger command source length changed.' }
        $hash = Get-TraceHash $pin
        $expected = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($hash -cne $expected) { throw 'Debugger command source changed.' }
        $hashes[$source.Key] = $hash
    }
    $state.CommandSha256 = $hashes['startup.cdb']
    $state.CreateCommandSha256 = $hashes['startup-cpr.cdb']
    $state.ExitCommandSha256 = $hashes['startup-epr.cdb']
    $state.ExtensionExpected = $Extension
    # Compare sx readback with the fixed, fully verified publication only.
    $state.FilterExpected.Cpr = 'Command: "$$><' + (Join-Path $root 'startup-cpr.cdb') + '"'
    $state.FilterExpected.Epr = 'Command: "$$><' + (Join-Path $root 'startup-epr.cdb') + '"'
    return Join-Path $root 'startup.cdb'
}

function Confirm-TracePublication {
    param([string]$Workspace)
    $publication = Join-Path $Workspace '.artifacts/servicing-acceptance'
    $bundle = Join-Path $publication 'acceptance'
    $helper = Join-Path $bundle 'Hermes.Servicing.Acceptance.exe'
    $manifestPath = Join-Path $publication 'acceptance-build.json'
    $inventoryPath = Join-Path $publication 'acceptance.publish.txt'
    Set-HermesStartupTracePoint $state 11 # Pin receipt.
    $manifestPin = Open-TracePin $manifestPath
    Set-HermesStartupTracePoint $state 12 # Pin inventory.
    $inventoryPin = Open-TracePin $inventoryPath
    Set-HermesStartupTracePoint $state 13 # Bound provenance.
    if ($manifestPin.Length -gt 4096 -or $inventoryPin.Length -gt 524288) {
        throw 'Publication provenance exceeds its bound.'
    }
    Set-HermesStartupTracePoint $state 14 # Read receipt.
    $manifestBytes = [byte[]]::new([int]$manifestPin.Length)
    $manifestPin.ReadExactly($manifestBytes)
    Set-HermesStartupTracePoint $state 15 # Parse receipt.
    $manifest = [Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
    Set-HermesStartupTracePoint $state 16 # Bind fixed helper.
    if ($manifest.acceptanceExecutable -cne $helper) {
        throw 'Fixed published helper required.'
    }
    Set-HermesStartupTracePoint $state 17 # Read declared inventory.
    $files = @(Get-HermesSetupPublishFiles -Directory $bundle `
        -InventoryPath $inventoryPath -AcceptanceHelpers)
    Set-HermesStartupTracePoint $state 18 # Bound inventory.
    if ($files.Count -lt 4 -or $files.Count -gt 1024) {
        throw 'Published helper inventory bound refused.'
    }
    foreach ($file in $files) {
        Set-HermesStartupTracePoint $state 19 # Pin published file.
        $pin = Open-TracePin (Join-Path $bundle $file.path)
        Set-HermesStartupTracePoint $state 20 # Hash pinned file.
        $hash = Get-TraceHash $pin
        Set-HermesStartupTracePoint $state 21 # Compare declared hash.
        if ($hash -cne $file.sha256) { throw 'Pinned helper source changed.' }
        if ($file.path -ceq 'Hermes.Servicing.Acceptance.exe') {
            $state.HelperSha256 = $hash
        }
    }
    Set-HermesStartupTracePoint $state 22 # Require apphost provenance.
    if ($state.HelperSha256 -ceq '') { throw 'Published apphost provenance absent.' }
    return $helper
}

function New-TraceRoot {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) { throw 'Trace requires a fresh owned directory.' }
    $null = [IO.Directory]::CreateDirectory($Path)
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        if ($null -eq $identity.User) { throw 'Trace owner unavailable.' }
        $security = [Security.AccessControl.DirectorySecurity]::new()
        $security.SetAccessRuleProtection($true, $false)
        $security.SetOwner($identity.User)
        $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
        foreach ($owner in @($identity.User, $system)) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new(
                $owner, [Security.AccessControl.FileSystemRights]::FullControl,
                [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',
                [Security.AccessControl.PropagationFlags]::None,
                [Security.AccessControl.AccessControlType]::Allow)
            $security.AddAccessRule($rule)
        }
        [IO.FileSystemAclExtensions]::SetAccessControl(
            [IO.DirectoryInfo]::new($Path), $security)
    } finally { $identity.Dispose() }
    $null = Assert-TracePath $Path
}

function Read-TraceControl {
    param([string]$Path)
    if (-not [IO.File]::Exists($Path)) { return }
    $null = Assert-TracePath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        $buffer = [byte[]]::new(2049)
        $count = 0
        while ($count -lt $buffer.Length) {
            $read = $stream.Read($buffer, $count, $buffer.Length - $count)
            if ($read -eq 0) { break }
            $count += $read
        }
        if ($count -gt 2048) { $state.Protocol = 1; return }
        Update-HermesStartupRows $state ([Text.Encoding]::ASCII.GetString($buffer, 0, $count))
    } finally { $stream.Dispose() }
}

function Start-TraceRead {
    param([hashtable]$Stream, [IO.StreamReader]$Reader)
    if ($state.AcceptedUtf8Bytes -ge 8388608) { $state.Bounds = 1 }
    if ($state.Bounds -ne 0) { return }
    $Stream.Pending = $Reader.ReadAsync($Stream.Buffer, 0, $Stream.Buffer.Length)
}

function Receive-TraceRead {
    param([hashtable]$Stream, [IO.StreamReader]$Reader, [bool]$Parse)
    if ($state.AcceptedUtf8Bytes -ge 8388608) { $state.Bounds = 1 }
    for ($iteration = 0; $iteration -lt 16; $iteration++) {
        if ($state.Bounds -ne 0 -or $Stream.End `
            -or $null -eq $Stream.Pending -or -not $Stream.Pending.IsCompleted) {
            return
        }
        $count = $Stream.Pending.GetAwaiter().GetResult()
        $Stream.Pending = $null
        if ($count -eq 0) {
            $Stream.End = $true
            if ($Parse -and $Stream.Tail.Length -ne 0) { $state.Protocol = 1 }
            return
        }
        Add-HermesStartupText $state $Stream (
            [string]::new($Stream.Buffer, 0, $count)) $Parse
        if ($state.Bounds -ne 0) { return }
        Start-TraceRead $Stream $Reader
    }
}

function Invoke-TraceCollection {
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $exitObservedAt = [long]-1
    while ($state.Bounds -eq 0 -and $elapsed.Elapsed.TotalSeconds -lt 210) {
        Receive-TraceRead $state.Out $debugger.StandardOutput $true
        if ($state.Bounds -ne 0) { return }
        Receive-TraceRead $state.Err $debugger.StandardError $false
        if ($state.Bounds -ne 0) { return }
        Read-TraceControl (Join-Path $root 'helper.rows')
        Publish-TraceArm
        if ($debugger.HasExited) {
            if ($exitObservedAt -lt 0) { $exitObservedAt = $elapsed.ElapsedMilliseconds }
            if ($state.Out.End -and $state.Err.End) { break }
            if ($elapsed.ElapsedMilliseconds - $exitObservedAt -gt 5000) {
                $state.CleanupFault = 13
                break
            }
        }
        Start-Sleep -Milliseconds 20
    }
    if ($state.Bounds -ne 0) { return }
    if (-not $debugger.HasExited) { $state.Timeout = 1 }
    elseif ($debugger.ExitCode -ne 0) { $state.Error = 13 }
}

function Publish-TraceArm {
    if ($state.Acked -or -not (Test-HermesStartupArm $state)) { return }
    $row = [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        "HERMES_STARTUP_ARM v=1 pid={0} born={1}`n",
        [object[]]@($state.Target.pid, $state.Target.born))
    $pending = Join-Path $root 'armed.pending'
    $armed = Join-Path $root 'armed.row'
    $stream = [IO.File]::Open($pending, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.Encoding]::ASCII.GetBytes($row)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    [IO.File]::SetAttributes($pending, [IO.FileAttributes]::ReadOnly)
    [IO.File]::Move($pending, $armed)
    $null = Open-TracePin $armed
    Set-HermesStartupArmAcknowledged $state
}

function Stop-TraceDebugger {
    if (-not $started) { return }
    # Process.Start's retained process handle is pinned before polling.
    # No PID lookup, process-tree walk, detach, -pd, or kill-on-exit override.
    if (-not $debugger.HasExited) { $debugger.Kill() }
    if (-not $debugger.WaitForExit(5000)) { throw 'Owned debugger did not end.' }
}

$state = New-HermesStartupState
$pins = [Collections.Generic.List[IO.FileStream]]::new()
$debugger = [Diagnostics.Process]::new()
$started = $false
$debuggerEnded = $false
$drainsEnded = $false
$rootCreated = $false
$root = ''
$reportPath = ''

try {
    Set-HermesStartupTracePoint $state 1 # Workspace path.
    $workspace = Assert-TracePath ([IO.Path]::GetFullPath($env:GITHUB_WORKSPACE))
    Set-HermesStartupTracePoint $state 2 # Temporary root path.
    $temporary = Assert-TracePath ([IO.Path]::GetFullPath($env:RUNNER_TEMP))
    $root = Join-Path $temporary 'hermes-native-startup-trace'
    Set-HermesStartupTracePoint $state 3 # Fresh report destination.
    $reportDirectory = Join-Path $workspace '.artifacts/installer-evidence'
    $reportPath = Join-Path $reportDirectory 'startup-debug.json'
    $null = [IO.Directory]::CreateDirectory($reportDirectory)
    $null = Assert-TracePath $reportDirectory
    if ([IO.File]::Exists($reportPath)) { throw 'Stale trace report refused.' }
    Set-HermesStartupTracePoint $state 4 # Owned private root.
    New-TraceRoot $root
    $rootCreated = $true

    Set-HermesStartupTracePoint $state 5 # Fixed SDK directory.
    $kits = Join-Path ([Environment]::GetFolderPath(
        [Environment+SpecialFolder]::ProgramFilesX86)) 'Windows Kits/10/Debuggers/x64'
    $kits = Assert-TracePath ([IO.Path]::GetFullPath($kits))
    $extension = Confirm-TraceTools $kits
    $helper = Confirm-TracePublication $workspace
    Set-HermesStartupTracePoint $state 23 # Pin diagnostic sources.
    foreach ($source in @($PSCommandPath, (Join-Path $PSScriptRoot 'Native-StartupTrace.psm1'))) {
        $null = Open-TracePin $source
    }

    Set-HermesStartupTracePoint $state 24 # Fixed command and symbol setup.
    $commandPath = Publish-TraceCommands $extension
    $symbols = Join-Path $root 'symbols'
    $null = [IO.Directory]::CreateDirectory($symbols)

    Set-HermesStartupTracePoint $state 25 # Owned debugger setup and start.
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $kits 'cdb.exe'
    $start.WorkingDirectory = $kits
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in @('_NT_SYMBOL_PATH', '_NT_ALT_SYMBOL_PATH',
            '_NT_DEBUGGER_EXTENSION_PATH', '_NT_SOURCE_PATH', '_NT_EXECUTABLE_IMAGE_PATH')) {
        $null = $start.Environment.Remove($name)
    }
    foreach ($argument in @('-o', '-cf', $commandPath, '-y',
            "srv*$symbols*https://msdl.microsoft.com/download/symbols",
            $helper, 'startup-debug-probe')) {
        $start.ArgumentList.Add($argument)
    }
    $debugger.StartInfo = $start
    if (-not $debugger.Start()) { throw 'Owned debugger did not start.' }
    $started = $true
    $heldDebuggerHandle = $debugger.SafeHandle
    $debugger.StandardInput.Close()
    Set-HermesStartupTracePoint $state 26 # Owned output collection.
    Start-TraceRead $state.Out $debugger.StandardOutput
    Start-TraceRead $state.Err $debugger.StandardError
    Invoke-TraceCollection
} catch {
    # Never print debugger output, exception text, paths, identities, or commands.
    Set-HermesStartupTraceFault $state
} finally {
    try {
        Stop-TraceDebugger
        $debuggerEnded = -not $started -or $debugger.HasExited
    } catch { $state.CleanupFault = 13 }
    if ($started) {
        $grace = [Diagnostics.Stopwatch]::StartNew()
        try {
            # After a bound, close owned readers below without consuming or
            # rescheduling pending buffers. Closure is not evidence of EOF.
            while ($state.Bounds -eq 0 -and $grace.ElapsedMilliseconds -lt 5000 `
                -and (-not $state.Out.End -or -not $state.Err.End)) {
                Receive-TraceRead $state.Out $debugger.StandardOutput $true
                if ($state.Bounds -ne 0) { break }
                Receive-TraceRead $state.Err $debugger.StandardError $false
                if ($state.Bounds -ne 0) { break }
                Start-Sleep -Milliseconds 20
            }
            if ($state.Bounds -eq 0) {
                Read-TraceControl (Join-Path $root 'helper.rows')
            }
            $drainsEnded = $state.Out.End -and $state.Err.End
            if (-not $drainsEnded) { $state.CleanupFault = 13 }
        } catch { $state.CleanupFault = 13 }
        foreach ($reader in @($debugger.StandardOutput, $debugger.StandardError)) {
            try { $reader.BaseStream.Dispose(); $reader.Dispose() }
            catch { $state.CleanupFault = 13 }
        }
    }
    try { $debugger.Dispose() } catch { $state.CleanupFault = 13 }
    foreach ($pin in $pins) {
        try { $pin.Dispose() } catch { $state.CleanupFault = 13 }
    }
    foreach ($stream in @($state.Out, $state.Err)) {
        try {
            $stream.Fingerprint = [Convert]::ToHexString(
                $stream.Hash.GetHashAndReset()).ToLowerInvariant()
            $stream.Hash.Dispose()
        } catch { $state.CleanupFault = 13 }
    }
    if ($rootCreated) {
        try {
            $null = Assert-TracePath $root
            $entries = @(Get-ChildItem -LiteralPath $root -Recurse -Force)
            if (@($entries | Where-Object {
                    ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
                }).Count -ne 0) { throw 'Trace cleanup reparse point refused.' }
            Remove-Item -LiteralPath $root -Recurse -Force
        } catch { $state.CleanupFault = 13 }
    }
}

$report = New-HermesStartupReport $state $debuggerEnded $drainsEnded
try {
    if ($reportPath -ceq '') { throw 'Report destination unavailable.' }
    $json = $report | ConvertTo-Json -Depth 4 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json + "`n")
    if ($bytes.Length -gt 4096) { throw 'Derived report exceeds its bound.' }
    $stream = [IO.File]::Open($reportPath, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
} catch {
    Write-Output ("HERMES_STARTUP_TRACE v=1 completed=0 status=8 error=13 cleanupFault=13 tracePoint={0} toolRole={1}" `
        -f $report.tracePoint, $report.toolRole)
    exit 1
}
Write-Output ("HERMES_STARTUP_TRACE v=1 completed={0} status={1} error={2} cleanupFault={3} tracePoint={4} toolRole={5}" `
    -f $report.completed, $report.status, $report.error, $report.cleanupFault,
        $report.tracePoint, $report.toolRole)
if ($report.completed -ne 1) { exit 1 }
exit 0
