#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('controls', 'fixture', 'task-contract', 'task-contract-load')][string]$Mode = 'controls',
    [ValidateSet('valid', 'at-cap', 'base-only', 'event-only', 'noise', 'crlf', 'utf8', 'stderr',
        'exit', 'overflow', 'timeout', 'failure', 'failed-guard', 'failed-callback',
        'failed-fence', 'failed-dispose', 'failed-base-success', 'wrapper-failure')][string]$Fixture = 'valid'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$observation = "HERMES_CRASH_EVENT_OBSERVATION v=1 reason=0 matched=1 lowerEmpty=1 upperEmpty=1 nativeAcceptance=0`n"
$base = "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734499 error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n"
$event = "HERMES_CRASH_EVENT_CONTROL v=1 status=0 reason=0 reportError=0 originalGatePassed=0 nativeAcceptance=0`n"
$protocol = $observation + $base + $event
function Get-FailedCommandProtocol {
    param([string]$Name)
    $failedObservation = $observation.Replace('reason=0', 'reason=10').Replace('matched=1', 'matched=0')
    $failedObservation = $failedObservation.Replace('lowerEmpty=1', 'lowerEmpty=0').Replace('upperEmpty=1', 'upperEmpty=0')
    $failedBase = $base.Replace('status=0', 'status=1').Replace('error=0 ', 'error=7 ')
    $failedEvent = $event.Replace('status=0', 'status=1')
    switch -CaseSensitive ($Name) {
        'failed-guard' {
            return $base.Replace('status=0', 'status=1').Replace('exit=2148734499', 'exit=0').Replace('error=0 ', 'error=1 ') +
                $failedEvent.Replace('reason=0', 'reason=2')
        }
        'failed-callback' { return ($failedObservation * 2) + $failedBase + $failedEvent.Replace('reason=0', 'reason=10') }
        'failed-fence' {
            return ($failedObservation * 2) + $failedObservation.Replace('reason=10', 'reason=4') +
                $failedBase + $failedEvent.Replace('reason=0', 'reason=4')
        }
        'failed-dispose' {
            return ($failedObservation * 2) + $failedObservation.Replace('reason=10', 'reason=6') +
                $failedBase + $failedEvent.Replace('reason=0', 'reason=6')
        }
        'failed-base-success' {
            return $observation + $observation.Replace('reason=0', 'reason=4') + $base +
                $failedEvent.Replace('reason=0', 'reason=4')
        }
        default { throw '1' }
    }
}
if ($Mode -in @('task-contract', 'task-contract-load')) {
    function Invoke-ColdTaskCapture {
        Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -DisableNameChecking
        Import-Module (Join-Path $PSScriptRoot 'Crash-Event-Control.psm1') -DisableNameChecking
        if ($Mode -ceq 'task-contract-load') {
            Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -DisableNameChecking
            $assembly = [Text.Json.JsonDocument].Assembly
            $null = Get-HermesManagedModuleMvid $assembly.Location
            $loaded = [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath($assembly.Location)
            if (-not [object]::ReferenceEquals($loaded, $assembly)) { throw '1' }
        }
        $start = [Diagnostics.ProcessStartInfo]::new()
        $start.FileName = [Environment]::ProcessPath
        foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                $PSCommandPath, '-Mode', 'fixture', '-Fixture', 'valid')) { $start.ArgumentList.Add($argument) }
        $start.UseShellExecute = $false
        $start.RedirectStandardOutput = $true
        $start.RedirectStandardError = $true
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $start
        $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
        $capture = $null
        try {
            if (-not $process.Start()) { throw '1' }
            $capture = Invoke-HermesCrashEventCapture $process -Operations $operations
            if (-not $capture.Tasks.GetType().Equals([object[]]) -or $capture.Tasks.Rank -ne 1 `
                -or $capture.Tasks.GetLowerBound(0) -ne 0 -or $capture.Tasks.Length -ne $operations.Count) { throw '1' }
            Assert-HermesCrashEventTaskBinding $capture.Tasks $operations
            Assert-HermesCrashEventOutcome $capture ([pscustomobject]@{ Exit = [uint32]2148734499 })
            return $operations.Count
        } finally {
            if (-not $process.HasExited) { $process.Kill($true) }
            if (-not $process.WaitForExit(5000)) { throw '15' }
            $process.Dispose()
            $all = [Threading.Tasks.Task]::WhenAll($operations.ToArray())
            try { $null = $all.Wait(5000) } catch { }
            Assert-HermesCrashEventSettled $operations
            Clear-HermesCrashEventCapture $capture
        }
    }
    try {
        $count = Invoke-ColdTaskCapture
        $loaded = [int]($Mode -ceq 'task-contract-load')
        [Console]::Out.WriteLine("HERMES_CRASH_EVENT_TASK_CONTRACT v=1 status=0 loaded=$loaded tasks=$count")
        exit 0
    } catch {
        [Console]::Out.WriteLine('HERMES_CRASH_EVENT_TASK_CONTRACT v=1 status=1 loaded=0 tasks=0')
        exit 1
    }
}
if ($Mode -ceq 'fixture') {
    if ($Fixture -ceq 'wrapper-failure') {
        Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -DisableNameChecking
        Import-Module (Join-Path $PSScriptRoot 'Crash-Event-Control.psm1') -DisableNameChecking
        $t = $null
        $e = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot 'Invoke-CrashEventControl.ps1'), [ref]$t, [ref]$e)
        if ($e.Count -ne 0) { exit 2 }
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Write-CrashEventFailure'
        }, $false)
        . ([scriptblock]::Create($definition.Extent.Text))
        $script:failureReported = $false
        $script:phase = 9
        $done = [Threading.Tasks.Task]::CompletedTask
        $script:capture = [pscustomobject]@{
            ExitCode = 1; Deadline = 0; StdoutComplete = 1; StderrComplete = 1; WaitSettled = 1
            Stdout = [Text.Encoding]::UTF8.GetBytes("private/path`n$([char]27)[31m")
            Stderr = [byte[]]::new(0); Tasks = @($done, $done, $done)
        }
        try { throw "private/path`n$([char]27)[31m" }
        catch { Write-CrashEventFailure $_; Write-CrashEventFailure $_ }
        finally { Clear-HermesCrashEventCapture $script:capture }
        exit 1
    }
    if ($Fixture -ceq 'base-only') { $protocol = $base }
    if ($Fixture -ceq 'event-only') { $protocol = $event }
    if ($Fixture -ceq 'noise') { $protocol += "private/path`n" }
    if ($Fixture -ceq 'at-cap') { $protocol = 'x' * 511 }
    if ($Fixture -ceq 'crlf') { $protocol = $protocol.Replace("`n", "`r`n") }
    if ($Fixture -ceq 'overflow') { $protocol = 'x' * 4096 }
    if ($Fixture -ceq 'timeout') { [Threading.Thread]::Sleep(30000); exit 1 }
    if ($Fixture -ceq 'failure') {
        $failedObservation = $observation.Replace('reason=0', 'reason=10').Replace('matched=1', 'matched=0')
        $protocol = $failedObservation.Replace('lowerEmpty=1', 'lowerEmpty=0').Replace('upperEmpty=1', 'upperEmpty=0') +
            $base.Replace('status=0', 'status=1').Replace('error=0 ', 'error=8 ') +
            $event.Replace('status=0', 'status=1').Replace('reason=0', 'reason=10')
    }
    if ($Fixture.StartsWith('failed-', [StringComparison]::Ordinal)) { $protocol = Get-FailedCommandProtocol $Fixture }
    $bytes = [Text.Encoding]::UTF8.GetBytes($protocol)
    if ($Fixture -ceq 'utf8') { $bytes = [byte[]]@(255) }
    $stdout = [Console]::OpenStandardOutput()
    $stdout.Write($bytes)
    $stdout.Flush()
    if ($Fixture -ceq 'stderr') {
        $stderr = [Console]::OpenStandardError()
        $stderr.WriteByte(120)
        $stderr.Flush()
    }
    if ($Fixture -in @('exit', 'failure') -or $Fixture.StartsWith('failed-', [StringComparison]::Ordinal)) { exit 1 }
    exit 0
}

Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Crash-Event-Control.psm1') -Force -DisableNameChecking
$script:passed = 0
$script:failed = 0
$script:case = 0
function Assert-Control {
    param([bool]$Condition)
    $script:case++
    if ($Condition) { $script:passed++ }
    else {
        $script:failed++
        $line = (@(Get-PSCallStack | Select-Object -Skip 1 -ExpandProperty ScriptLineNumber) -join '.')
        [Console]::Out.WriteLine("HERMES_CRASH_EVENT_TEST_FAILURE case=$script:case line=$line")
    }
}
function Assert-Refused {
    param([scriptblock]$Action)
    $refused = $false
    try { $null = & $Action } catch { $refused = $true }
    Assert-Control $refused
}
function ConvertTo-Bytes {
    param([object]$Value)
    return ,[Text.UTF8Encoding]::new($false, $true).GetBytes(($Value | ConvertTo-Json -Depth 10 -Compress))
}
function New-Capture {
    param([string]$Text = $protocol, [int]$Code = 0)
    $done = [Threading.Tasks.Task]::CompletedTask
    return [pscustomobject]@{
        ExitCode = $Code; Deadline = 0; StdoutComplete = 1; StderrComplete = 1; WaitSettled = 1
        Stdout = [Text.Encoding]::UTF8.GetBytes($Text); Stderr = [byte[]]::new(0); Tasks = @($done, $done, $done)
    }
}
function Assert-FactsUnavailable {
    param([object]$Capture)
    Assert-Control ((ConvertTo-HermesCrashEventFacts $Capture) -ceq $script:unavailable)
}

function Assert-CaptureDiagnostic {
    param([object]$Capture, [int]$Check, [int]$Field = 0)
    $line = ConvertTo-HermesCrashEventCaptureDiagnostic $Capture
    $match = [regex]::Match($line,
        '\AHERMES_CRASH_EVENT_CAPTURE v=1 check=([0-9]+) field=([0-9]+) out=([0-9]+) err=([0-9]+) tasks=([0-9]+) rows=([0-9]+) shape=([0-9]+)\z')
    Assert-Control $match.Success
    if (-not $match.Success) { return }
    $values = @(foreach ($index in 1..7) { [int]$match.Groups[$index].Value })
    Assert-Control ($values[0] -eq $Check -and $values[1] -eq $Field)
    $bounds = @(27, 22, 512, 1, 515, 7, 117648)
    Assert-Control (@(0..6 | Where-Object { $values[$_] -lt 0 -or $values[$_] -gt $bounds[$_] }).Count -eq 0)
    if ($Check -eq 0) { Assert-Control ((ConvertTo-HermesCrashEventFacts $Capture).Contains('available=1')) }
    else { Assert-FactsUnavailable $Capture }
}

function Test-FailedCommandProtocols {
    foreach ($name in @('failed-guard', 'failed-callback', 'failed-fence', 'failed-dispose', 'failed-base-success')) {
        $capture = New-Capture (Get-FailedCommandProtocol $name) 1
        Assert-Control ($capture.Stdout.Length -le 511)
        Assert-CaptureDiagnostic $capture 0
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        if ($name -in @('failed-fence', 'failed-dispose')) {
            Assert-Control ($capture.Stdout.Length -eq 508)
            $reason = if ($name -ceq 'failed-fence') { 4 } else { 6 }
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture) -ceq (
                "HERMES_CRASH_EVENT_FACTS v=1 available=1 status=1 controlStatus=1 reason=$reason matched=0 lowerEmpty=0 upperEmpty=0 controlError=7 cleanupError=0 reportError=0 originalGatePassed=0 nativeAcceptance=0"))
        }
        if ($name -ceq 'failed-base-success') {
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture).Contains('status=1 controlStatus=0 reason=4'))
        }
    }
    $three = Get-FailedCommandProtocol 'failed-fence'
    $capture = New-Capture ($three.Replace('reason=10', 'reason=22')) 1
    Assert-CaptureDiagnostic $capture 20 9
    $capture = New-Capture ($observation * 3 + $base + $event)
    Assert-CaptureDiagnostic $capture 26
    Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
    $capture = New-Capture ($observation * 4 + $base + $event)
    Assert-CaptureDiagnostic $capture 9 6
}

function Test-CaptureDiagnostics {
    Assert-CaptureDiagnostic (New-Capture) 0
    Assert-CaptureDiagnostic $null 1
    $names = @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled', 'Stdout', 'Stderr', 'Tasks')
    for ($index = 0; $index -lt $names.Count; $index++) {
        $capture = New-Capture
        $capture.PSObject.Properties.Remove($names[$index])
        Assert-CaptureDiagnostic $capture 1 ($index + 1)
    }
    foreach ($mutation in @(
        @{ Field = 'ExitCode'; Value = 0L; Check = 2; Code = 1 },
        @{ Field = 'ExitCode'; Value = 2; Check = 3; Code = 1 },
        @{ Field = 'Deadline'; Value = 1; Check = 4; Code = 2 },
        @{ Field = 'StdoutComplete'; Value = 0; Check = 5; Code = 3 },
        @{ Field = 'StderrComplete'; Value = 0; Check = 6; Code = 4 },
        @{ Field = 'WaitSettled'; Value = 0; Check = 7; Code = 5 },
        @{ Field = 'Stdout'; Value = 'private/path'; Check = 8; Code = 6 },
        @{ Field = 'Stdout'; Value = [byte[]]::new(0); Check = 9; Code = 6 },
        @{ Field = 'Stdout'; Value = [byte[]]::new(512); Check = 9; Code = 6 },
        @{ Field = 'Stderr'; Value = 'private/path'; Check = 10; Code = 7 },
        @{ Field = 'Stderr'; Value = [byte[]]@(120); Check = 11; Code = 7 },
        @{ Field = 'Tasks'; Value = [Threading.Tasks.Task]::CompletedTask; Check = 12; Code = 8 },
        @{ Field = 'Tasks'; Value = [object[]]@(); Check = 13; Code = 8 }
    )) {
        $capture = New-Capture
        $capture.($mutation.Field) = $mutation.Value
        Assert-CaptureDiagnostic $capture $mutation.Check $mutation.Code
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
    }
    $capture = New-Capture
    $capture.Tasks[1] = [pscustomobject]@{ Status = [Threading.Tasks.TaskStatus]::RanToCompletion }
    Assert-CaptureDiagnostic $capture 14 8
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $faulted = [Threading.Tasks.Task]::FromException([IO.IOException]::new('private/path'))
    $cancelled = [Threading.Tasks.Task]::FromCanceled([Threading.CancellationToken]::new($true))
    foreach ($task in @($pending.Task, $faulted, $cancelled)) {
        $capture = New-Capture
        $capture.Tasks[1] = $task
        Assert-CaptureDiagnostic $capture 15 8
    }
    $pending.SetResult(0)
    try { $null = $faulted.GetAwaiter().GetResult() } catch { }
    $capture = New-Capture
    $capture.Stdout = [byte[]]@(255)
    Assert-CaptureDiagnostic $capture 17 6
    Assert-CaptureDiagnostic (New-Capture "private/path`n$([char]27)[31m") 18
    Assert-CaptureDiagnostic (New-Capture ($protocol.Replace('exit=2148734499', 'exit=4294967296'))) 19 14
    Assert-CaptureDiagnostic (New-Capture ($protocol.Replace('reason=0', 'reason=22'))) 20 9
    Assert-CaptureDiagnostic (New-Capture $protocol 1) 21 20
    Assert-CaptureDiagnostic (New-Capture ($observation + $base + $event.Replace('reason=0', 'reason=22'))) 22 21
    Assert-CaptureDiagnostic (New-Capture ($protocol.Replace('error=0 ', 'error=12 '))) 23 15
    Assert-CaptureDiagnostic (New-Capture ($protocol.Replace('cleanupError=0', 'cleanupError=256'))) 24 16
    $withExit = $base.Replace('status=0', 'status=1').Replace('error=0 ', 'error=6 ') +
        "HERMES_CRASH_EXIT v=1 check=0 nativeError=31 nativeAcceptance=0`n" +
        $event.Replace('status=0', 'status=1').Replace('reason=0', 'reason=19')
    Assert-CaptureDiagnostic (New-Capture $withExit 1) 25 18
    Assert-CaptureDiagnostic (New-Capture ($protocol.Replace('matched=1', 'matched=0'))) 26
    $capture = New-Capture ("`n" * 511)
    $line = ConvertTo-HermesCrashEventCaptureDiagnostic $capture
    Assert-Control ($line -ceq 'HERMES_CRASH_EVENT_CAPTURE v=1 check=18 field=0 out=511 err=0 tasks=3 rows=7 shape=117648')
    $capture = New-Capture
    $capture.Tasks = @([Threading.Tasks.Task]::CompletedTask) * 515
    Assert-CaptureDiagnostic $capture 13 8
    Assert-Control ((ConvertTo-HermesCrashEventCaptureDiagnostic $capture).Contains('tasks=515'))
    $capture = New-Capture
    $capture | Add-Member ScriptProperty Stdout { throw "private/path`n$([char]27)[31m" } -Force
    Assert-CaptureDiagnostic $capture 8 6
    $capture = New-Capture ("private/path`n" + ('a' * 64) + "`n$([char]27)[31m")
    Assert-Control ((ConvertTo-HermesCrashEventCaptureDiagnostic $capture) -ceq (
        'HERMES_CRASH_EVENT_CAPTURE v=1 check=18 field=0 out=83 err=0 tasks=3 rows=3 shape=285'))
    $culture = [Globalization.CultureInfo]::CurrentCulture
    try {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
        Assert-CaptureDiagnostic (New-Capture) 0
    } finally { [Globalization.CultureInfo]::CurrentCulture = $culture }
}

function Test-TaskContainerContract {
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    foreach ($index in 1..3) {
        $source = [Threading.Tasks.TaskCompletionSource[int]]::new()
        $source.SetResult($index)
        $operations.Add($source.Task)
    }
    $typed = $operations.ToArray()
    Assert-Control ($typed.GetType().Equals([Threading.Tasks.Task[]]) -and $typed.Rank -eq 1 -and $typed.Length -eq 3)
    $snapshot = [object[]]::new($operations.Count)
    for ($index = 0; $index -lt $operations.Count; $index++) { $snapshot[$index] = $operations[$index] }
    Assert-HermesCrashEventTaskBinding $snapshot $operations
    Assert-Control ($snapshot.GetType().Equals([object[]]) -and $snapshot.Rank -eq 1 -and $snapshot.GetLowerBound(0) -eq 0)
    foreach ($index in 0..2) { Assert-Control ([object]::ReferenceEquals($snapshot[$index], $operations[$index])) }
    $capture = New-Capture
    $capture.Tasks = $typed
    Assert-CaptureDiagnostic $capture 12 8
    Assert-Refused { Assert-HermesCrashEventTaskBinding $typed $operations }
    # Warm the same consumer call sites with exact object arrays. Typed arrays
    # must remain refused; the former -is check could change after this order.
    foreach ($unused in 1..100) { $null = ConvertTo-HermesCrashEventFacts (New-Capture) }
    Assert-CaptureDiagnostic $capture 12 8
    Assert-Refused { Assert-HermesCrashEventTaskBinding $typed $operations }
    $capture.Tasks = $snapshot
    Assert-CaptureDiagnostic $capture 0
    Assert-Control (@(Assert-HermesCrashEventOutcome $capture $script:control).Count -eq 0)
    foreach ($container in @($operations, ([Array]::CreateInstance([object], [int[]]@(3, 1))),
            ([Array]::CreateInstance([object], [int[]]@(3), [int[]]@(1))))) {
        $capture.Tasks = $container
        Assert-CaptureDiagnostic $capture 12 8
        Assert-Refused { Assert-HermesCrashEventTaskBinding $container $operations }
    }
    foreach ($bad in @(
        ([object[]]@($snapshot[0], $snapshot[1])),
        ([object[]]@($snapshot[1], $snapshot[0], $snapshot[2])),
        ([object[]]@($snapshot[0], $snapshot[0], $snapshot[2])),
        ([object[]]@($snapshot[0], $snapshot[1], [Threading.Tasks.Task]::CompletedTask)),
        ([object[]]@($snapshot[0], $snapshot[1], $snapshot[2], $snapshot[2]))
    )) { Assert-Refused { Assert-HermesCrashEventTaskBinding $bad $operations } }
    $other = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    foreach ($index in 1..3) {
        $source = [Threading.Tasks.TaskCompletionSource[int]]::new()
        $source.SetResult($index)
        $other.Add($source.Task)
    }
    Assert-Refused { Assert-HermesCrashEventTaskBinding $snapshot $other }
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $operations[1] = $pending.Task
    $snapshot[1] = $pending.Task
    Assert-Refused { Assert-HermesCrashEventTaskBinding $snapshot $operations }
    $pending.SetResult(0)
    Assert-HermesCrashEventTaskBinding $snapshot $operations
    Assert-Control $true
    foreach ($task in @(
        [Threading.Tasks.Task]::FromException([IO.IOException]::new('synthetic fault')),
        [Threading.Tasks.Task]::FromCanceled([Threading.CancellationToken]::new($true))
    )) {
        $operations[1] = $task
        $snapshot[1] = $task
        Assert-Refused { Assert-HermesCrashEventTaskBinding $snapshot $operations }
        try { $null = $task.GetAwaiter().GetResult() } catch { }
    }
}

function Test-Schema {
    $json = '{"schemaVersion":1,"usable":1,"unavailable":0,"metadataCount":1,"versions":[{"id":1000,"version":0,"unavailable":0,"fieldCount":15,"duplicateCount":0,"caseMismatchCount":0,"knownFieldPresentBits":63,"templateSha256":"cd0a3d85a95e98a2909d1a77bada29db34ed1abbe37a058d6c06e9aca599557e","fields":[[1,1,1,20,18],[2,1,1,21,19],[3,1,1,1,1],[4,1,1,1,1],[5,1,1,1,1],[6,1,1,1,1]]}]}'
    $script:schemaBytes = [Text.Encoding]::UTF8.GetBytes($json)
    Assert-Control (@(Assert-HermesCrashEventSchema $script:schemaBytes).Count -eq 0)
    $shape = $json | ConvertFrom-Json -AsHashtable
    foreach ($name in @($shape.Keys | Where-Object { $_ -cne 'versions' })) {
        $copy = $json | ConvertFrom-Json -AsHashtable
        $copy.Remove($name)
        Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
        foreach ($value in @(-1, $null, $true, '1', 1.5, [uint64]::MaxValue)) {
            $copy[$name] = $value
            Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
        }
    }
    foreach ($name in @($shape.versions[0].Keys)) {
        $copy = $json | ConvertFrom-Json -AsHashtable
        $copy.versions[0].Remove($name)
        Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
        $copy.versions[0][$name] = $null
        Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
    }
    foreach ($bad in @(
        '', '{}', '[]', 'null', '{', ($json + 'x'), (' ' * 4097),
        $json.Replace('"usable":1', '"usable":1,"usable":1'),
        $json.Replace('"usable":1', '"usable":1,"\u0075sable":1'),
        $json.Replace('"usable":1', '"Usable":1'),
        $json.Replace('"usable":1', '"usable":1,"extra":0'),
        $json.Replace('"usable":1', '"usable":0'),
        $json.Replace('"unavailable":0', '"unavailable":1'),
        $json.Replace('"metadataCount":1', '"metadataCount":0'),
        $json.Replace('"version":0', '"version":1'),
        $json.Replace('"fieldCount":15', '"fieldCount":14'),
        $json.Replace('"duplicateCount":0', '"duplicateCount":1'),
        $json.Replace('"caseMismatchCount":0', '"caseMismatchCount":1'),
        $json.Replace('"knownFieldPresentBits":63', '"knownFieldPresentBits":62'),
        $json.Replace('cd0a3d85', 'Cd0a3d85'),
        $json.Replace('cd0a3d85', 'dd0a3d85'),
        $json.Replace('[1,1,1,20,18]', '[1,1,1,8,8]'),
        $json.Replace('[2,1,1,21,19]', '[2,1,1,10,10]'),
        $json.Replace('[3,1,1,1,1]', '[3,0,0,0,0]'),
        $json.Replace('[4,1,1,1,1]', '[4,1,1,1,0]'),
        $json.Replace('[5,1,1,1,1]', '[6,1,1,1,1]'),
        $json.Replace('[6,1,1,1,1]', '[6,1,1,1,1,0]'),
        $json.Replace('[1,1,1,20,18]', '[1,1,2,20,18]')
    )) { Assert-Refused { Assert-HermesCrashEventSchema ([Text.Encoding]::UTF8.GetBytes($bad)) } }
    foreach ($token in @('1.0', '1e0', '-0', '"1"', 'true', 'null', '4294967296')) {
        Assert-Refused { Assert-HermesCrashEventSchema ([Text.Encoding]::UTF8.GetBytes($json.Replace('"usable":1', "`"usable`":$token"))) }
    }
    Assert-Refused { Assert-HermesCrashEventSchema ([byte[]]@(255)) }
    $copy = $json | ConvertFrom-Json -AsHashtable
    $copy.versions += ($json | ConvertFrom-Json -AsHashtable).versions[0]
    $copy.metadataCount = 2
    Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
    $copy.versions[1].version = 1
    Assert-Control (@(Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy)).Count -eq 0)
    $copy.versions[1].fields[0] = @(1, 0, 0, 0, 0)
    $copy.versions[1].knownFieldPresentBits = 62
    Assert-Control (@(Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy)).Count -eq 0)
    $copy.versions[1].fieldCount = 4
    Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
    $copy.versions[1].fieldCount = 15
    $copy.versions[1].knownFieldPresentBits = 63
    Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
    $copy.versions[1].knownFieldPresentBits = 62
    foreach ($value in @(-1, 256, 4294967296L)) {
        $copy.versions[1].version = $value
        Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
    }
    $copy.versions[1].version = 1
    foreach ($code in @(3, 7, 22)) {
        $copy.versions[1].fields[0] = @(1, 1, 1, $code, 1)
        $copy.versions[1].knownFieldPresentBits = 63
        Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
    }
    $copy = $json | ConvertFrom-Json -AsHashtable
    for ($index = 1; $index -le 7; $index++) {
        $row = ($json | ConvertFrom-Json -AsHashtable).versions[0]
        $row.version = $index
        $copy.versions += $row
    }
    $copy.metadataCount = 8
    Assert-Control (@(Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy)).Count -eq 0)
    $copy.metadataCount = 9
    $copy.versions += ($json | ConvertFrom-Json -AsHashtable).versions[0]
    Assert-Refused { Assert-HermesCrashEventSchema (ConvertTo-Bytes $copy) }
}

function Test-EventReport {
    $shape = [ordered]@{
        schemaVersion = 1; status = 0; reason = 0; originalGatePassed = 0; nativeAcceptance = 0
        thisOwnedControlOnly = 1; schemaAdmitted = 1; metadataSha256 = $script:metadataHash
        schemaSourceSha256 = $script:schemaSourceHash; independentPublicationVerified = 0; receiptClosed = 1
        exactCandidates = 1; exactReadMatched = 1; birthMinusOneCandidates = 0; birthPlusOneCandidates = 0
        completedQueryEnumerations = 3; maximumNextCallsPerQuery = 2; nextBatchCapacity = 2
        globalLogUniqueness = 0; polls = 1; queries = 3; maximumPollSeconds = 20; maximumWindowSeconds = 120
        controlStatus = 0; reportError = 0; outerSupervisionRequired = 1; synchronousCallsInterruptible = 0
    }
    $script:eventBytes = ConvertTo-Bytes $shape
    $parsed = Assert-HermesCrashEventReport $script:eventBytes $script:metadataHash $script:schemaSourceHash
    Assert-Control ($shape.Count -eq 27 -and $parsed.Polls -eq 1 -and $parsed.Queries -eq 3)
    foreach ($name in @($shape.Keys)) {
        $saved = $shape[$name]
        $shape.Remove($name)
        Assert-Refused { Assert-HermesCrashEventReport (ConvertTo-Bytes $shape) $script:metadataHash $script:schemaSourceHash }
        foreach ($value in @($null, $true, '0', -1, 4294967296L)) {
            $shape[$name] = $value
            Assert-Refused { Assert-HermesCrashEventReport (ConvertTo-Bytes $shape) $script:metadataHash $script:schemaSourceHash }
        }
        if ($name -notin @('polls', 'queries', 'metadataSha256', 'schemaSourceSha256')) {
            $shape[$name] = $saved + 1
            Assert-Refused { Assert-HermesCrashEventReport (ConvertTo-Bytes $shape) $script:metadataHash $script:schemaSourceHash }
        }
        $shape[$name] = $saved
    }
    foreach ($polls in @(1, 2, 80, 81)) {
        $shape.polls = $polls
        $shape.queries = $polls + 2
        $parsed = Assert-HermesCrashEventReport (ConvertTo-Bytes $shape) $script:metadataHash $script:schemaSourceHash
        Assert-Control ($parsed.Polls -eq $polls -and $parsed.Queries -eq $polls + 2)
    }
    foreach ($polls in @(0, 82, 1000)) {
        $shape.polls = $polls
        $shape.queries = $polls + 2
        Assert-Refused { Assert-HermesCrashEventReport (ConvertTo-Bytes $shape) $script:metadataHash $script:schemaSourceHash }
    }
    $shape.polls = 1
    foreach ($queries in @(0, 1, 2, 4, 83, 84)) {
        $shape.queries = $queries
        Assert-Refused { Assert-HermesCrashEventReport (ConvertTo-Bytes $shape) $script:metadataHash $script:schemaSourceHash }
    }
    $json = [Text.Encoding]::UTF8.GetString($script:eventBytes)
    foreach ($bad in @(
        '', '{}', '[]', 'null', '{', (' ' * 4097), ($json + 'x'),
        $json.Replace('"status":0', '"status":0,"status":0'),
        $json.Replace('"status":0', '"status":0,"\u0073tatus":0'),
        $json.Replace('"status":0', '"Status":0'),
        $json.Replace('"status":0', '"status":0,"extra":0'),
        $json.Replace($script:metadataHash, ('C' * 64)),
        $json.Replace($script:metadataHash, ('0' * 64)),
        $json.Replace($script:schemaSourceHash, ('f' * 64))
    )) { Assert-Refused { Assert-HermesCrashEventReport ([Text.Encoding]::UTF8.GetBytes($bad)) $script:metadataHash $script:schemaSourceHash } }
    foreach ($token in @('0.0', '0e0', '-0', '"0"', 'false', 'null')) {
        Assert-Refused { Assert-HermesCrashEventReport (
            [Text.Encoding]::UTF8.GetBytes($json.Replace('"status":0', "`"status`":$token"))) $script:metadataHash $script:schemaSourceHash }
    }
    Assert-Refused { Assert-HermesCrashEventReport ([byte[]]@(255)) $script:metadataHash $script:schemaSourceHash }
}

function Test-OutcomeAndFacts {
    $script:control = [pscustomobject]@{ Exit = [uint32]2148734499; ImageSha256 = ('a' * 64); SourceSha256 = ('b' * 64) }
    Assert-Control (@(Assert-HermesCrashEventOutcome (New-Capture) $script:control).Count -eq 0)
    Assert-Control ((ConvertTo-HermesCrashEventFacts (New-Capture)) -ceq (
        'HERMES_CRASH_EVENT_FACTS v=1 available=1 status=0 controlStatus=0 reason=0 matched=1 lowerEmpty=1 upperEmpty=1 controlError=0 cleanupError=0 reportError=0 originalGatePassed=0 nativeAcceptance=0'))
    foreach ($text in @(
        '', $base, $event, ($base + $event), ($observation + $base), ($base + $observation + $event),
        ($protocol * 2), $protocol.TrimEnd([char]10), $protocol.Replace("`n", "`r`n"),
        ("noise`n" + $protocol), ($protocol + "`n"), ($protocol + [char]0),
        ([string][char]0xfeff + $protocol), $protocol.Replace('v=1', 'v=2'),
        $protocol.Replace('nativeAcceptance=0', 'nativeAcceptance=1'),
        $protocol.Replace('originalGatePassed=0', 'originalGatePassed=1'),
        $protocol.Replace('reason=0', 'reason=22'), $protocol.Replace('reason=0', 'reason=-1'),
        $protocol.Replace('reason=0', 'reason=00'), $protocol.Replace('reason=0', 'reason=4294967296'),
        $protocol.Replace('error=0 ', 'error=12 '), $protocol.Replace('cleanupError=0', 'cleanupError=256'),
        $protocol.Replace('reportError=0', 'reportError=2'),
        $protocol.Replace('matched=1', 'matched=2'),
        $protocol.Replace('exit=2148734499', 'exit=4294967296'),
        "private/path`n$([char]27)[31m", ('x' * 512)
    )) {
        $capture = New-Capture $text
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        Assert-FactsUnavailable $capture
    }
    foreach ($text in @(
        $protocol.Replace('matched=1', 'matched=0'),
        $protocol.Replace('lowerEmpty=1', 'lowerEmpty=0'),
        $protocol.Replace('upperEmpty=1', 'upperEmpty=0'),
        $protocol.Replace('reason=0', 'reason=10'),
        $protocol.Replace('exit=2148734499', 'exit=37'),
        $protocol.Replace('exit=2148734499', 'exit=3221226505')
    )) {
        Assert-Refused { Assert-HermesCrashEventOutcome (New-Capture $text) $script:control }
    }
    foreach ($code in @(0x80131506L, 0x80131623L, 0xc0000409L, 0xc0000602L, 0xe0434352L)) {
        $script:control.Exit = [uint32]$code
        $capture = New-Capture ($protocol.Replace('exit=2148734499', "exit=$code"))
        Assert-Control (@(Assert-HermesCrashEventOutcome $capture $script:control).Count -eq 0)
    }
    $script:control.Exit = [uint32]2148734499
    foreach ($name in @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled')) {
        foreach ($bad in @($null, $true, '0', 0L, -1, 2)) {
            $capture = New-Capture
            $capture.$name = $bad
            Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
            Assert-FactsUnavailable $capture
        }
    }
    foreach ($name in @((New-Capture).PSObject.Properties.Name)) {
        $capture = New-Capture
        $capture.PSObject.Properties.Remove($name)
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        Assert-FactsUnavailable $capture
    }
    $done = [Threading.Tasks.Task]::CompletedTask
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $faulted = [Threading.Tasks.Task]::FromException([IO.IOException]::new('private/path'))
    foreach ($tasks in @(@(), @($done), @($done, $done, $pending.Task), @($done, $done, $faulted),
            @($done, $done, $null), (@($done) * 515))) {
        $capture = New-Capture
        $capture.Tasks = $tasks
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        Assert-FactsUnavailable $capture
    }
    $pending.SetResult(0)
    try { $null = $faulted.GetAwaiter().GetResult() } catch { }
    foreach ($bytes in @([byte[]]@(255), [byte[]]@(0xc0, 0xaf), [byte[]]@(0xc3))) {
        $capture = New-Capture
        $capture.Stdout = $bytes
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        Assert-FactsUnavailable $capture
    }
    $capture = New-Capture
    $capture.Stderr = [byte[]]@(120)
    Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
    Assert-FactsUnavailable $capture
    Assert-FactsUnavailable (New-Capture ($protocol.Replace('exit=2148734499', 'exit=37')))
    foreach ($reason in 0..21) {
        $text = $observation.Replace('reason=0', "reason=$reason") +
            $base.Replace('status=0', 'status=1').Replace('error=0 ', 'error=8 ') +
            $event.Replace('status=0', 'status=1').Replace('reason=0', "reason=$reason")
        $capture = New-Capture $text 1
        $facts = ConvertTo-HermesCrashEventFacts $capture
        Assert-Control ($facts -ceq (
            "HERMES_CRASH_EVENT_FACTS v=1 available=1 status=1 controlStatus=1 reason=$reason matched=1 lowerEmpty=1 upperEmpty=1 controlError=8 cleanupError=0 reportError=0 originalGatePassed=0 nativeAcceptance=0"))
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
    }
    foreach ($reason in @(4, 5, 6, 20)) {
        foreach ($diagnostic in @('', $observation.Replace('reason=0', "reason=$reason"))) {
            $capture = New-Capture ($observation + $diagnostic + $base +
                $event.Replace('status=0', 'status=1').Replace('reason=0', "reason=$reason")) 1
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture) -ceq (
                "HERMES_CRASH_EVENT_FACTS v=1 available=1 status=1 controlStatus=0 reason=$reason matched=1 lowerEmpty=1 upperEmpty=1 controlError=0 cleanupError=0 reportError=0 originalGatePassed=0 nativeAcceptance=0"))
            Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        }
    }
    foreach ($extra in @('', $observation, ($observation * 2))) {
        $text = $extra + $base.Replace('status=0', 'status=1').Replace('error=0 ', 'error=6 ') +
            "HERMES_CRASH_EXIT v=1 check=10 nativeError=31 nativeAcceptance=0`n" +
            $event.Replace('status=0', 'status=1').Replace('reason=0', 'reason=19')
        $capture = New-Capture $text 1
        Assert-Control ((ConvertTo-HermesCrashEventFacts $capture).Contains('available=1'))
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        foreach ($bad in @($text.Replace('check=10', 'check=0'), $text.Replace('check=10', 'check=26'),
                $text.Replace('nativeError=31', 'nativeError=4294967296'), $text.Replace('error=6 ', 'error=5 '))) {
            Assert-FactsUnavailable (New-Capture $bad 1)
        }
    }
    $capture = New-Capture
    Clear-HermesCrashEventCapture $capture
    Assert-Control (@($capture.Stdout | Where-Object { $_ -ne 0 }).Count -eq 0)
    Clear-HermesCrashEventCapture $null
    $culture = [Globalization.CultureInfo]::CurrentCulture
    try {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
        Assert-Control ((ConvertTo-HermesCrashEventFacts (New-Capture)).Contains('available=1 status=0 controlStatus=0 reason=0'))
    } finally { [Globalization.CultureInfo]::CurrentCulture = $culture }
}

function Test-Publication {
    param([string]$Scratch)
    $source = Join-Path $Scratch 'schema-source.ps1'
    $workflow = Join-Path $Scratch 'workflow.yml'
    $metadata = Join-Path $Scratch 'startup-event-schema.json'
    [IO.File]::WriteAllText($source, 'synthetic fixed metadata source')
    [IO.File]::WriteAllText($workflow, 'synthetic fixed workflow source')
    $started = [DateTime]::UtcNow.Ticks
    [IO.File]::WriteAllBytes($metadata, $script:schemaBytes)
    $entries = @(
        (Get-HermesCrashFileEntry $source 'schema-source.ps1' 4096),
        (Get-HermesCrashFileEntry $workflow 'workflow.yml' 4096),
        (Get-HermesCrashFileEntry $metadata 'startup-event-schema.json' 4096)
    )
    $checkout = [pscustomobject]@{ Revision = ('a' * 40); Tree = ('b' * 40); SourceSha256 = ('c' * 64) }
    $shape = [ordered]@{
        schemaVersion = 1; revision = $checkout.Revision; tree = $checkout.Tree; sourceSha256 = $checkout.SourceSha256
        workflowSha256 = $entries[1].sha256; schemaSourceSha256 = $entries[0].sha256
        metadataSha256 = $entries[2].sha256; length = $entries[2].length; runId = 123; runAttempt = 2
        startedUtcTicks = $started; completedUtcTicks = [DateTime]::UtcNow.Ticks
        createdUtcTicks = [IO.File]::GetCreationTimeUtc($metadata).Ticks
        writtenUtcTicks = [IO.File]::GetLastWriteTimeUtc($metadata).Ticks
    }
    $bytes = ConvertTo-Bytes $shape
    $receipt = Read-HermesCrashEventPublication $bytes
    Assert-Control ($receipt.Count -eq 14)
    Assert-HermesCrashEventPublicationBinding $receipt $checkout $entries[1] $entries[0] $entries[2] '123' '2'
    $created = $shape.createdUtcTicks
    $written = $shape.writtenUtcTicks
    Assert-HermesCrashEventFreshness $receipt $created $written ([DateTime]::UtcNow.Ticks)
    Assert-Control $true
    foreach ($name in @($shape.Keys)) {
        $saved = $shape[$name]
        $shape.Remove($name)
        Assert-Refused { Read-HermesCrashEventPublication (ConvertTo-Bytes $shape) }
        foreach ($bad in @($null, $true, '1', -1)) {
            $shape[$name] = $bad
            Assert-Refused { Read-HermesCrashEventPublication (ConvertTo-Bytes $shape) }
        }
        $shape[$name] = $saved
    }
    $json = [Text.Encoding]::UTF8.GetString($bytes)
    foreach ($bad in @('', '{}', '[]', 'null', '{', ($json + 'x'), (' ' * 4097),
            $json.Replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1'),
            $json.Replace('"schemaVersion":1', '"schemaVersion":1,"extra":0'),
            $json.Replace('"schemaVersion":1', '"schemaVersion":1.0'),
            $json.Replace('"runId":123', '"runId":18446744073709551616'),
            $json.Replace('"runId":123', '"runId":1e0'),
            $json.Replace('"runId":123', '"runId":-0'),
            $json.Replace($checkout.Revision, ('A' * 40)),
            $json.Replace('"length":' + $entries[2].length, '"length":4097'),
            $json.Replace('"runAttempt":2', '"runAttempt":0'),
            $json.Replace('"completedUtcTicks":' + $shape.completedUtcTicks, '"completedUtcTicks":3155378976000000000'))) {
        Assert-Refused { Read-HermesCrashEventPublication ([Text.Encoding]::UTF8.GetBytes($bad)) }
    }
    Assert-Refused { Read-HermesCrashEventPublication ([byte[]]@(255)) }
    foreach ($name in @('revision', 'tree', 'sourceSha256', 'workflowSha256', 'schemaSourceSha256', 'metadataSha256', 'length', 'runId', 'runAttempt')) {
        $saved = $receipt[$name]
        $receipt[$name] = if ($saved -is [string]) { 'f' * $saved.Length } else { $saved + 1 }
        Assert-Refused { Assert-HermesCrashEventPublicationBinding $receipt $checkout $entries[1] $entries[0] $entries[2] '123' '2' }
        $receipt[$name] = $saved
    }
    foreach ($bad in @('', '0123', '123 ', '+123', '1e2', 'private/path')) {
        Assert-Refused { Assert-HermesCrashEventPublicationBinding $receipt $checkout $entries[1] $entries[0] $entries[2] $bad '2' }
        Assert-Refused { Assert-HermesCrashEventPublicationBinding $receipt $checkout $entries[1] $entries[0] $entries[2] '123' $bad }
    }
    $boundary = $started + [TimeSpan]::FromMinutes(5).Ticks
    Assert-HermesCrashEventFreshness $receipt $created $written $boundary
    Assert-Control $true
    Assert-Refused { Assert-HermesCrashEventFreshness $receipt $created $written ($boundary + 1) }
    Assert-Refused { Assert-HermesCrashEventFreshness $receipt $created $written ($shape.completedUtcTicks - 1) }
    Assert-Refused { Assert-HermesCrashEventFreshness $receipt ($created + 1) $written $boundary }
    Assert-Refused { Assert-HermesCrashEventFreshness $receipt $created ($written + 1) $boundary }
    foreach ($name in @('startedUtcTicks', 'completedUtcTicks', 'createdUtcTicks', 'writtenUtcTicks')) {
        $saved = $receipt[$name]
        $receipt[$name] = $boundary + 1
        Assert-Refused { Assert-HermesCrashEventFreshness $receipt $created $written $boundary }
        $receipt[$name] = $saved
    }
    $pins = Open-HermesCrashPins $Scratch $entries
    try {
        Assert-HermesCrashPins $pins
        Assert-Control ($pins.Count -eq 3)
        $changed = $false
        try { [IO.File]::WriteAllText($source, 'changed synthetic source'); $changed = $true } catch { }
        if ($changed) { Assert-Refused { Assert-HermesCrashPins $pins } }
        else { Assert-HermesCrashPins $pins; Assert-Control $true }
    } finally { Close-HermesCrashPins $pins }
    Assert-Refused { Assert-HermesCrashPins $pins }
    [IO.File]::WriteAllText($source, 'synthetic fixed metadata source')
    [IO.File]::Delete($metadata)
    [IO.File]::WriteAllBytes($metadata, $script:schemaBytes)
    [IO.File]::SetLastWriteTimeUtc($metadata, [DateTime]::new($written + 10000000, [DateTimeKind]::Utc))
    Assert-Refused { Assert-HermesCrashEventFreshness $receipt ([IO.File]::GetCreationTimeUtc($metadata).Ticks) `
        ([IO.File]::GetLastWriteTimeUtc($metadata).Ticks) $boundary }
    $link = Join-Path $Scratch 'metadata-link'
    if (-not [OperatingSystem]::IsWindows()) {
        $null = [IO.File]::CreateSymbolicLink($link, $metadata)
        try { Assert-Refused { Open-HermesCrashPins $Scratch @([ordered]@{ path = 'metadata-link'; length = $entries[2].length; sha256 = $entries[2].sha256 }) } }
        finally { [IO.File]::Delete($link) }
    }
    Assert-HermesCrashEventAbsent $Scratch -Metadata
    $evidence = Join-Path $Scratch '.artifacts/installer-evidence'
    foreach ($name in @('event-correlation.json', 'EVENT-CORRELATION.JSON', 'startup-crash-control.json', 'startup-event-schema.json')) {
        $path = Join-Path $evidence $name
        [IO.File]::WriteAllText($path, 'stale')
        Assert-Refused { Assert-HermesCrashEventAbsent $Scratch -Metadata }
        [IO.File]::Delete($path)
        $null = [IO.Directory]::CreateDirectory($path)
        Assert-Refused { Assert-HermesCrashEventAbsent $Scratch -Metadata }
        [IO.Directory]::Delete($path)
    }
    $gitFixture = Join-Path $Scratch 'tracked-workflow'
    $hooks = Join-Path $Scratch 'empty-hooks'
    $null = [IO.Directory]::CreateDirectory($hooks)
    $null = [IO.Directory]::CreateDirectory((Join-Path $gitFixture '.github/workflows'))
    & git -C $gitFixture init --quiet --initial-branch=fixture "--template=$hooks" 2>$null
    if ($LASTEXITCODE -ne 0) { throw '8' }
    $workflowPath = Join-Path $gitFixture '.github/workflows/startup-crash-event-control.yml'
    [IO.File]::WriteAllText($workflowPath, 'synthetic workflow')
    Assert-Refused { Get-HermesCrashEventWorkflowEntry $gitFixture }
    & git -C $gitFixture -c core.autocrlf=false -c core.safecrlf=false add --force -- '.github/workflows/startup-crash-event-control.yml' 2>$null
    if ($LASTEXITCODE -ne 0) { throw '8' }
    $tracked = Get-HermesCrashEventWorkflowEntry $gitFixture
    $pins = Open-HermesCrashPins $gitFixture @($tracked)
    try { Assert-HermesCrashPins $pins; Assert-Control ($tracked.path -ceq '.github/workflows/startup-crash-event-control.yml') }
    finally { Close-HermesCrashPins $pins }
}

function Test-Lifecycle {
    param([string]$Scratch)
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    foreach ($unused in 1..3) { $operations.Add([Threading.Tasks.Task]::CompletedTask) }
    Assert-HermesCrashEventSettled $operations
    Assert-Control $true
    $script:scopeCalls = [Collections.Generic.List[int]]::new()
    $scope = [pscustomobject]@{}
    $scope | Add-Member ScriptMethod CompleteAfterChildrenExited { $script:scopeCalls.Add(1) }
    $scope | Add-Member ScriptMethod Dispose { $script:scopeCalls.Add(2) }
    $path = Join-Path $Scratch 'lifecycle-pin'
    [IO.File]::WriteAllText($path, 'synthetic pin')
    $entry = Get-HermesCrashFileEntry $path 'lifecycle-pin' 4096
    $pins = Open-HermesCrashPins $Scratch @($entry)
    try {
        Assert-Refused { Complete-HermesCrashEventScope $scope @($pins) $operations }
        Assert-Control ($script:scopeCalls.Count -eq 0)
    } finally { Close-HermesCrashPins $pins }
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $operations.Add($pending.Task)
    Assert-Refused { Complete-HermesCrashEventScope $scope @($pins) $operations }
    Assert-Control ($script:scopeCalls.Count -eq 0)
    $pending.SetResult(0)
    Complete-HermesCrashEventScope $scope @($pins) $operations
    Assert-Control (($script:scopeCalls -join ',') -ceq '1,2')
    $script:scopeCalls.Clear()
    $scope | Add-Member ScriptMethod CompleteAfterChildrenExited { $script:scopeCalls.Add(1); throw '1' } -Force
    Assert-Refused { Complete-HermesCrashEventScope $scope @($pins) $operations }
    Assert-Control (($script:scopeCalls -join ',') -ceq '1')
    $faulted = [Threading.Tasks.Task]::FromException([IO.IOException]::new('synthetic fault'))
    $operations.Add($faulted)
    Assert-Refused { Assert-HermesCrashEventSettled $operations }
    try { $null = $faulted.GetAwaiter().GetResult() } catch { }
    $operations.Remove($faulted) | Out-Null
    $operations.Add([Threading.Tasks.Task]::FromCanceled([Threading.CancellationToken]::new($true)))
    Assert-Refused { Assert-HermesCrashEventSettled $operations }
}

function Test-DiagnosticsAndGuard {
    $private = "private/path`n$('a' * 64)`n$([char]27)[31m"
    foreach ($phase in 0..12) {
        Assert-Control ((ConvertTo-HermesCrashEventDiagnostic $phase ([Exception]::new($private))) -ceq (
            "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=$phase family=0 reason=0"))
    }
    Assert-Control ((ConvertTo-HermesCrashEventDiagnostic 9 ([ComponentModel.Win32Exception]::new(31, $private))) -ceq (
        'HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=9 family=2 reason=0'))
    foreach ($reason in 1..20) {
        Assert-Control ((ConvertTo-HermesCrashEventDiagnostic 3 ([Management.Automation.RuntimeException]::new([string]$reason))) -ceq (
            "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=3 family=1 reason=$reason"))
    }
    if ([OperatingSystem]::IsWindows()) { return }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [Environment]::ProcessPath
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
            (Join-Path $PSScriptRoot 'Invoke-CrashEventControl.ps1'))) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    $capture = $null
    try {
        if (-not $process.Start()) { throw '1' }
        $capture = Invoke-HermesCrashEventCapture $process $operations
        $newline = [Environment]::NewLine
        $expected = "HERMES_CRASH_EVENT_WRAPPER v=1 status=1 published=0 originalGatePassed=0 nativeAcceptance=0$newline" +
            "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=1 family=1 reason=1$newline"
        Assert-Control ($capture.ExitCode -eq 1 -and $capture.Stderr.Length -eq 0)
        Assert-Control ([string]::Equals([Text.Encoding]::UTF8.GetString($capture.Stdout), $expected, [StringComparison]::Ordinal))
    } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        if (-not $process.WaitForExit(5000)) { throw '15' }
        $process.Dispose()
        Assert-HermesCrashEventSettled $operations
        Clear-HermesCrashEventCapture $capture
    }
}

function Invoke-Fixture {
    param([string]$Name)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [Environment]::ProcessPath
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $PSCommandPath)
    if ($Name -in @('task-contract', 'task-contract-load')) { $arguments += '-Mode', $Name }
    else { $arguments += '-Mode', 'fixture', '-Fixture', $Name }
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    $capture = $null
    $accepted = $false
    try {
        if (-not $process.Start()) { throw '1' }
        $limit = if ($Name -ceq 'timeout') { 200 } else { 20000 }
        $capture = Invoke-HermesCrashEventCapture $process -DeadlineMilliseconds $limit -Operations $operations
        Assert-Control ($capture.Tasks.Count -eq $operations.Count -and @($capture.Tasks | Where-Object {
            -not $operations.Contains($_)
        }).Count -eq 0)
        Assert-Control ($capture.Tasks.GetType().Equals([object[]]) -and $capture.Tasks.Rank -eq 1 `
            -and $capture.Tasks.GetLowerBound(0) -eq 0)
        Assert-HermesCrashEventTaskBinding $capture.Tasks $operations
        if ($Name -in @('task-contract', 'task-contract-load')) {
            $loaded = [int]($Name -ceq 'task-contract-load')
            $text = [Text.UTF8Encoding]::new($false, $true).GetString($capture.Stdout)
            $match = [regex]::Match($text, "\AHERMES_CRASH_EVENT_TASK_CONTRACT v=1 status=0 loaded=$loaded tasks=([0-9]+)$([Environment]::NewLine)\z")
            $good = $match.Success -and [int]$match.Groups[1].Value -ge 3 -and [int]$match.Groups[1].Value -le 514 `
                -and $capture.ExitCode -eq 0 -and $capture.Stderr.Length -eq 0
            Assert-Control $good
            if (-not $good) { throw '1' }
            $accepted = $true
            return
        }
        if ($Name -ceq 'at-cap') { Assert-Control ($capture.Stdout.Length -eq 511) }
        if ($Name -ceq 'wrapper-failure') {
            $newline = [Environment]::NewLine
            $expected = "HERMES_CRASH_EVENT_WRAPPER v=1 status=1 published=0 originalGatePassed=0 nativeAcceptance=0$newline" +
                "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=9 family=0 reason=0$newline" +
                "$script:unavailable$newline" +
                "HERMES_CRASH_EVENT_CAPTURE v=1 check=18 field=0 out=18 err=0 tasks=3 rows=2 shape=40$newline"
            Assert-Control ($capture.ExitCode -eq 1 -and $capture.Stderr.Length -eq 0 `
                -and [Text.Encoding]::UTF8.GetString($capture.Stdout) -ceq $expected)
            return
        }
        if ($Name -ceq 'failure') {
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture).Contains('available=1 status=1 controlStatus=1 reason=10'))
        }
        if ($Name.StartsWith('failed-', [StringComparison]::Ordinal)) {
            Assert-CaptureDiagnostic $capture 0
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture).Contains('available=1 status=1'))
        }
        Assert-HermesCrashEventOutcome $capture $script:control
        $accepted = $true
    } catch { } finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        if (-not $process.WaitForExit(5000)) { throw '15' }
        $process.Dispose()
        $all = [Threading.Tasks.Task]::WhenAll($operations.ToArray())
        try { $null = $all.Wait(5000) } catch { }
        foreach ($task in $operations) {
            if (-not $task.IsCompleted) { throw '15' }
            try { $null = $task.GetAwaiter().GetResult() } catch { }
        }
        Clear-HermesCrashEventCapture $capture
        Assert-Control ($operations.Count -ge 3 -and $all.IsCompleted)
    }
    Assert-Control ($accepted -eq ($Name -in @('valid', 'task-contract', 'task-contract-load')))
}

function Test-StaticBoundaries {
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $wrapperPath = Join-Path $PSScriptRoot 'Invoke-CrashEventControl.ps1'
    $wrapper = [IO.File]::ReadAllText($wrapperPath)
    $module = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Crash-Event-Control.psm1'))
    $workflow = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/startup-crash-event-control.yml'))
    $t = $null
    $e = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($wrapperPath, [ref]$t, [ref]$e)
    Assert-Control ($e.Count -eq 0 -and $ast.ParamBlock.Parameters.Count -eq 0)
    $failureFunction = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Write-CrashEventFailure'
    }, $false)
    Assert-Control ($failureFunction.Extent.Text.Contains('ConvertTo-HermesCrashEventCaptureDiagnostic $script:capture') `
        -and [regex]::Matches($wrapper, 'ConvertTo-HermesCrashEventCaptureDiagnostic').Count -eq 1)
    foreach ($path in @('Crash-Event-Control.psm1', 'Test-CrashEventControl.ps1')) {
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $path), [ref]$t, [ref]$e)
        Assert-Control ($e.Count -eq 0)
    }
    Assert-Control ($wrapper.Contains("ArgumentList.Add('startup-crash-event-probe')") `
        -and [regex]::Matches($wrapper, 'ArgumentList\.Add\(').Count -eq 1)
    Assert-Control ($wrapper.Contains("Join-Path `$directory 'Hermes.Servicing.Acceptance.exe'") `
        -and $wrapper.Contains('$start.UseShellExecute = $false') -and $wrapper.Contains('$start.RedirectStandardInput = $true'))
    Assert-Control ($wrapper.IndexOf('$scope = $begin.Invoke(', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('$process.Start()', [StringComparison]::Ordinal))
    Assert-Control ($wrapper.Contains('Invoke-HermesCrashEventCapture $process -Operations $operations') `
        -and $wrapper.Contains('Assert-HermesCrashEventSettled $operations'))
    Assert-Control ([regex]::Matches($wrapper, 'Assert-HermesCrashEventTaskBinding \$script:capture.Tasks \$operations').Count -eq 2)
    Assert-Control ($module.Contains('$tasks = [object[]]::new($Operations.Count)') `
        -and $module.Contains('$tasks[$index] = $Operations[$index]') `
        -and $module.Contains('Assert-HermesCrashEventTaskBinding $result.Tasks $Operations') `
        -and $module.Contains('[object]::ReferenceEquals($Snapshot[$index], $Operations[$index])'))
    Assert-Control ($wrapper.IndexOf('$process.Dispose()', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('Complete-HermesCrashEventScope $scope', [StringComparison]::Ordinal))
    Assert-Control ($wrapper.IndexOf('Assert-HermesCrashEventReport (', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('Complete-HermesCrashEventScope $scope', [StringComparison]::Ordinal))
    Assert-Control ($wrapper.IndexOf('Assert-HermesCrashReport (', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('Complete-HermesCrashEventScope $scope', [StringComparison]::Ordinal))
    Assert-Control ($wrapper.IndexOf('Close-HermesCrashPins $pins', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('Complete-HermesCrashEventScope $scope', [StringComparison]::Ordinal))
    Assert-Control ($wrapper.LastIndexOf('Clear-HermesCrashEventCapture $script:capture', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('if ($null -ne $scope) { $scope.Dispose() }', [StringComparison]::Ordinal))
    Assert-Control ([regex]::Matches($wrapper, 'Complete-HermesCrashEventScope \$scope').Count -eq 1)
    Assert-Control ($wrapper.Contains('Assert-HermesCrashAssemblies $directory $receipt.bundle') `
        -and $wrapper.Contains('Assert-HermesCrashTree $directory $receipt.bundle') `
        -and $wrapper.Contains('Assert-HermesCrashReceiptBinding $receipt $fresh'))
    Assert-Control ($wrapper.Contains('Assert-HermesCrashEventPublicationBinding $schemaReceipt $fresh') `
        -and $wrapper.Contains('$schemaReceiptPins, $metadataPins, $workflowPins, $baseReportPins, $eventReportPins'))
    Assert-Control ($module.Contains("& (Join-Path `$PSScriptRoot 'Invoke-StartupEventSchema.ps1')") `
        -and $module.Contains('$sourcePins = Open-HermesCrashPins $root $checkout.Sources') `
        -and $module.Contains('$workflowPins = Open-HermesCrashPins $root @($workflow)'))
    Assert-Control ($module.Contains('[IO.FileMode]::CreateNew') -and $module.Contains('Assert-HermesCrashEventSchema (Read-HermesCrashBytes $path)'))
    Assert-Control ($module.Contains('New-HermesCrashRead $Stream 512 $Operations') `
        -and $module.Contains('New-HermesCrashRead $Stream 1 $Operations') `
        -and $module.Contains('Update-HermesCrashRead $Read') `
        -and $module.Contains('[Array]::Clear($output.Buffer)') -and $module.Contains('[Array]::Clear($errors.Buffer)'))
    Assert-Control ($wrapper.IndexOf('Clear-HermesCrashEventCapture $script:capture', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('Complete-HermesCrashEventScope $scope', [StringComparison]::Ordinal))
    Assert-Control ($wrapper.IndexOf('[IO.File]::AppendAllText($env:GITHUB_OUTPUT', [StringComparison]::Ordinal) -lt
        $wrapper.IndexOf('Complete-HermesCrashEventScope $scope', [StringComparison]::Ordinal))
    foreach ($text in @($wrapper, $module, $workflow)) {
        Assert-Control ($text -cnotmatch '(?m)\$env:GITHUB_JOB\s*=' -and $text -cnotmatch 'SetEnvironmentVariable\s*\(\s*[''"]GITHUB_JOB' `
            -and $text -cnotmatch 'self-hosted|continue-on-error|always\(\)|ReadToEnd|Add-Type|Start-Job|Task\.Run')
    }
    Assert-Control ($workflow -cmatch "(?m)^  installer:\r?$" -and $workflow -cmatch '(?m)^    runs-on: windows-latest\r?$')
    Assert-Control ($workflow.Contains("github.repository == 'johnazariah/hermes' && github.server_url == 'https://github.com' && github.event.repository.private == false"))
    $jobs = [regex]::Match($workflow, '(?s)\njobs:\r?\n(?<jobs>.*)').Groups['jobs'].Value
    Assert-Control ([regex]::Matches($jobs, '(?m)^  [a-z][a-z_-]*:\r?$').Count -eq 1)
    Assert-Control ($workflow.Contains("persist-credentials: false") -and $workflow.Contains("contents: read"))
    Assert-Control ($workflow -cmatch '(?s)id: control\s+shell: pwsh\s+(?:#[^\n]*\n\s*)*timeout-minutes: 2\s+run: ./installer/windows/Invoke-CrashEventControl.ps1')
    Assert-Control ($workflow.IndexOf('./installer/windows/Test-CrashControl.ps1', [StringComparison]::Ordinal) -lt
        $workflow.IndexOf('./installer/windows/Publish-CrashControl.ps1', [StringComparison]::Ordinal))
    Assert-Control ($workflow.IndexOf('./installer/windows/Test-CrashEventControl.ps1', [StringComparison]::Ordinal) -lt
        $workflow.IndexOf('./installer/windows/Publish-CrashControl.ps1', [StringComparison]::Ordinal))
    Assert-Control ($workflow.IndexOf('./installer/windows/Publish-CrashControl.ps1', [StringComparison]::Ordinal) -lt
        $workflow.IndexOf('Invoke-HermesCrashEventSchemaPublication', [StringComparison]::Ordinal))
    Assert-Control ($workflow.IndexOf('Invoke-HermesCrashEventSchemaPublication', [StringComparison]::Ordinal) -lt
        $workflow.IndexOf('./installer/windows/Invoke-CrashEventControl.ps1', [StringComparison]::Ordinal))
    Assert-Control ($workflow.Contains("if: success() && steps.control.outcome == 'success' && steps.control.outputs.published == '1'"))
    $paths = [regex]::Match($workflow, '(?m)^          path: \|\r?\n(?<files>(?:            [^\r\n]+\r?\n)+)')
    Assert-Control ($paths.Success)
    $fixed = @($paths.Groups['files'].Value.TrimEnd().Split("`n") | ForEach-Object { $_.Trim() })
    Assert-Control (($fixed -join '|') -ceq (
        '.artifacts/installer-evidence/event-correlation.json|.artifacts/installer-evidence/startup-crash-control.json|.artifacts/installer-evidence/startup-event-schema.json'))
    Assert-Control ($workflow.Contains('retention-days: 3') -and $workflow.Contains('if-no-files-found: error') `
        -and [regex]::Matches($workflow, 'actions/upload-artifact@').Count -eq 1)
    $finally = $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.TryStatementAst] -and $null -ne $node.Finally
    }, $true)
    foreach ($node in $finally) { Assert-Control (-not $node.Finally.Extent.Text.Contains('Complete-HermesCrashEventScope')) }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
New-HermesCrashDirectory (Join-Path $root '.artifacts')
$scratch = Join-Path $root ('.artifacts/crash-event-control-tests-' + [Guid]::NewGuid().ToString('N'))
New-HermesCrashDirectory $scratch
$script:metadataHash = 'c' * 64
$script:schemaSourceHash = 'd' * 64
$script:unavailable = 'HERMES_CRASH_EVENT_FACTS v=1 available=0 status=0 controlStatus=0 reason=0 matched=0 lowerEmpty=0 upperEmpty=0 controlError=0 cleanupError=0 reportError=0 originalGatePassed=0 nativeAcceptance=0'
trap {
    [Console]::Out.WriteLine("HERMES_CRASH_EVENT_TEST_REFUSAL v=1 line=$($_.InvocationInfo.ScriptLineNumber)")
    [Console]::Out.WriteLine("HERMES_CRASH_EVENT_TESTS v=1 passed=$script:passed failed=$($script:failed + 1)")
    if ([IO.Directory]::Exists($scratch)) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    exit 1
}
try {
    Test-Schema
    Test-EventReport
    Test-OutcomeAndFacts
    Test-TaskContainerContract
    Test-FailedCommandProtocols
    Test-CaptureDiagnostics
    Test-Publication $scratch
    Test-Lifecycle $scratch
    Test-DiagnosticsAndGuard
    foreach ($name in @('task-contract', 'task-contract-load', 'valid', 'at-cap', 'base-only', 'event-only', 'noise', 'crlf', 'utf8', 'stderr',
            'exit', 'overflow', 'timeout', 'failure', 'failed-guard', 'failed-callback',
            'failed-fence', 'failed-dispose', 'failed-base-success', 'wrapper-failure')) { Invoke-Fixture $name }
    Test-StaticBoundaries
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
[Console]::Out.WriteLine("HERMES_CRASH_EVENT_TESTS v=1 passed=$script:passed failed=$script:failed")
if ($script:failed -ne 0) { exit 1 }
exit 0
