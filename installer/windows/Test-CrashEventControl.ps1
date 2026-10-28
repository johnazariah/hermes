#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('controls', 'fixture', 'task-contract', 'task-contract-load')][string]$Mode = 'controls',
    [ValidateSet('valid', 'at-cap', 'base-only', 'event-only', 'noise', 'crlf', 'utf8', 'stderr',
        'exit', 'overflow', 'timeout', 'failure', 'failed-guard', 'failed-callback',
        'failed-fence', 'failed-dispose', 'failed-base-success', 'wrapper-failure',
        'timing', 'timing-worst', 'timing-overflow', 'timing-native', 'timing-order', 'timing-duplicate',
        'timing-success', 'timing-stderr', 'timing-utf8', 'wrapper-timing', 'wrapper-timing-invalid',
        'wrapper-timing-phase', 'wrapper-timing-success')][string]$Fixture = 'valid'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$observation = "HERMES_CRASH_EVENT_OBSERVATION v=1 reason=0 matched=1 lowerEmpty=1 upperEmpty=1 nativeAcceptance=0`n"
$base = "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734499 error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n"
$event = "HERMES_CRASH_EVENT_CONTROL v=1 status=0 reason=0 reportError=0 originalGatePassed=0 nativeAcceptance=0`n"
$protocol = $observation + $base + $event
$timing = "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 polls=1 attempts=1 returns=1 empty=0 last=3 failure=0 code=0 native=-1 entryMs=20000 queryMs=19999 expiry=1 location=3 rejected=1 qUs=12000 qMaxUs=12000 nUs=500 nMaxUs=500 rUs=100 rMaxUs=100 cUs=20 cMaxUs=20 fUs=21000000 fMaxUs=21000000 dUs=0 dMaxUs=0 nativeAcceptance=0`n"
$worstTiming = "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 polls=999 attempts=999 returns=999 empty=999 last=9 failure=5 code=999 native=4294967295 entryMs=-1000000 queryMs=-1000000 expiry=3 location=5 rejected=1 qUs=999999999 qMaxUs=999999999 nUs=999999999 nMaxUs=999999999 rUs=999999999 rMaxUs=999999999 cUs=999999999 cMaxUs=999999999 fUs=999999999 fMaxUs=999999999 dUs=999999999 dMaxUs=999999999 nativeAcceptance=0`n"
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
function Get-TimedCommandProtocol {
    param([string]$Row = $timing, [string]$Name = 'failed-fence')
    return (Get-FailedCommandProtocol $Name).Replace('HERMES_CRASH_CONTROL v=1', ($Row + 'HERMES_CRASH_CONTROL v=1'))
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
        $count = 0
        foreach ($name in @('valid', 'timing', 'timing-worst')) {
            $start = [Diagnostics.ProcessStartInfo]::new()
            $start.FileName = [Environment]::ProcessPath
            foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
                    $PSCommandPath, '-Mode', 'fixture', '-Fixture', $name)) { $start.ArgumentList.Add($argument) }
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
                if ($name -ceq 'valid') {
                    Assert-HermesCrashEventOutcome $capture ([pscustomobject]@{ Exit = [uint32]2148734499 })
                    if (-not [string]::IsNullOrEmpty((ConvertTo-HermesCrashEventTiming $capture))) { throw '1' }
                } else {
                    $expected = if ($name -ceq 'timing') { $timing } else { $worstTiming }
                    $expected = $expected.Replace('HERMES_CRASH_EVENT_DIAGNOSTIC ', 'HERMES_CRASH_EVENT_TIMING ').TrimEnd([char]10)
                    if ($capture.ExitCode -ne 1 -or (ConvertTo-HermesCrashEventTiming $capture) -cne $expected `
                        -or -not (ConvertTo-HermesCrashEventFacts $capture).Contains('available=1 status=1')) { throw '1' }
                    $refused = $false
                    try { Assert-HermesCrashEventOutcome $capture ([pscustomobject]@{ Exit = [uint32]2148734499 }) }
                    catch { $refused = $true }
                    if (-not $refused) { throw '1' }
                }
                $count = [Math]::Max($count, $operations.Count)
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
        return $count
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
    if ($Fixture.StartsWith('wrapper-', [StringComparison]::Ordinal)) {
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
        $script:ownedPresence = $false
        $script:phase = 9
        $done = [Threading.Tasks.Task]::CompletedTask
        $script:capture = [pscustomobject]@{
            ExitCode = 1; Deadline = 0; StdoutComplete = 1; StderrComplete = 1; WaitSettled = 1
            Stdout = [Text.Encoding]::UTF8.GetBytes("private/path`n$([char]27)[31m")
            Stderr = [byte[]]::new(0); Tasks = @($done, $done, $done)
        }
        if ($Fixture -cne 'wrapper-failure') {
            $row = if ($Fixture -ceq 'wrapper-timing-invalid') { $timing.Replace('native=-1', 'native=4294967296') } else { $timing }
            $script:capture.Stdout = [Text.Encoding]::UTF8.GetBytes((Get-TimedCommandProtocol $row))
        }
        if ($Fixture -ceq 'wrapper-timing-phase') { $script:phase = 10 }
        if ($Fixture -ceq 'wrapper-timing-success') {
            $script:capture.Stdout = [Text.Encoding]::UTF8.GetBytes(($observation + $timing + $base + $event))
            $script:capture.ExitCode = 0
        }
        try { throw "private/path`n$([char]27)[31m" }
        catch { Write-CrashEventFailure $_; Write-CrashEventFailure $_ }
        finally { Clear-HermesCrashEventCapture $script:capture }
        exit 1
    }
    if ($Fixture -ceq 'base-only') { $protocol = $base }
    if ($Fixture -ceq 'event-only') { $protocol = $event }
    if ($Fixture -ceq 'noise') { $protocol += "private/path`n" }
    if ($Fixture -ceq 'at-cap') { $protocol = 'x' * 1023 }
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
    if ($Fixture.StartsWith('timing', [StringComparison]::Ordinal)) {
        $protocol = Get-TimedCommandProtocol
        switch -CaseSensitive ($Fixture) {
            'timing-worst' { $protocol = Get-TimedCommandProtocol $worstTiming }
            'timing-overflow' {
                $protocol = Get-TimedCommandProtocol $worstTiming
                $protocol += 'x' * (1024 - $protocol.Length)
            }
            'timing-native' { $protocol = Get-TimedCommandProtocol ($timing.Replace('native=-1', 'native=4294967296')) }
            'timing-order' { $protocol = (Get-FailedCommandProtocol 'failed-fence') + $timing }
            'timing-duplicate' { $protocol = Get-TimedCommandProtocol ($timing * 2) 'failed-guard' }
            'timing-success' { $protocol = $observation + $timing + $base + $event }
        }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($protocol)
    if ($Fixture -ceq 'utf8') { $bytes = [byte[]]@(255) }
    if ($Fixture -ceq 'timing-utf8') { $bytes[60] = 255 }
    $stdout = [Console]::OpenStandardOutput()
    $stdout.Write($bytes)
    $stdout.Flush()
    if ($Fixture -in @('stderr', 'timing-stderr')) {
        $stderr = [Console]::OpenStandardError()
        $stderr.WriteByte(120)
        $stderr.Flush()
    }
    if ($Fixture -in @('exit', 'failure') -or $Fixture.StartsWith('failed-', [StringComparison]::Ordinal) `
        -or ($Fixture.StartsWith('timing', [StringComparison]::Ordinal) -and $Fixture -cne 'timing-success')) { exit 1 }
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
    Assert-Control ([string]::IsNullOrEmpty((ConvertTo-HermesCrashEventTiming $Capture)))
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
    $bounds = @(28, 47, 1024, 1, 1027, 8, 2097151)
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
    Assert-CaptureDiagnostic $capture 18
}

function Set-TimingField {
    param([string]$Row, [string]$Name, [string]$Value)
    return [regex]::Replace($Row, ('(?<= ' + [regex]::Escape($Name) + '=)[^ ]+'),
        [Text.RegularExpressions.MatchEvaluator]{ param($unused) $Value })
}

function Test-Timing {
    $ranges = [ordered]@{
        polls = @(0L, 999L); attempts = @(0L, 999L); returns = @(0L, 999L); empty = @(0L, 999L)
        last = @(0L, 9L); failure = @(0L, 5L); code = @(0L, 999L); native = @(-1L, 4294967295L)
        entryMs = @(-1000000L, 999999L); queryMs = @(-1000000L, 999999L)
        expiry = @(0L, 3L); location = @(0L, 5L); rejected = @(0L, 1L)
        qUs = @(0L, 999999999L); qMaxUs = @(0L, 999999999L)
        nUs = @(0L, 999999999L); nMaxUs = @(0L, 999999999L)
        rUs = @(0L, 999999999L); rMaxUs = @(0L, 999999999L)
        cUs = @(0L, 999999999L); cMaxUs = @(0L, 999999999L)
        fUs = @(0L, 999999999L); fMaxUs = @(0L, 999999999L)
        dUs = @(0L, 999999999L); dMaxUs = @(0L, 999999999L)
    }
    Assert-Control ($worstTiming.Length -eq 393 -and [Text.Encoding]::ASCII.GetByteCount($worstTiming) -eq 393)
    $capture = New-Capture (Get-TimedCommandProtocol $worstTiming) 1
    Assert-Control ($capture.Stdout.Length -eq 901)
    Assert-CaptureDiagnostic $capture 0
    Assert-Control ((ConvertTo-HermesCrashEventCaptureDiagnostic $capture).Contains('rows=6 shape=142921'))
    foreach ($row in @($timing, $worstTiming)) {
        $expected = $row.Replace('HERMES_CRASH_EVENT_DIAGNOSTIC ', 'HERMES_CRASH_EVENT_TIMING ').TrimEnd([char]10)
        foreach ($name in @('failed-guard', 'failed-callback', 'failed-fence', 'failed-dispose', 'failed-base-success')) {
            $capture = New-Capture (Get-TimedCommandProtocol $row $name) 1
            $raw = [Convert]::ToBase64String($capture.Stdout)
            Assert-CaptureDiagnostic $capture 0
            Assert-Control ((ConvertTo-HermesCrashEventTiming $capture) -ceq $expected)
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture) -ceq
                (ConvertTo-HermesCrashEventFacts (New-Capture (Get-FailedCommandProtocol $name) 1)))
            Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
            Assert-Control ([Convert]::ToBase64String($capture.Stdout) -ceq $raw)
        }
    }
    foreach ($count in 0..3) {
        $text = ($observation * $count) + $timing +
            $base.Replace('status=0', 'status=1').Replace('error=0 ', 'error=6 ') +
            "HERMES_CRASH_EXIT v=1 check=25 nativeError=4294967295 nativeAcceptance=0`n" +
            $event.Replace('status=0', 'status=1').Replace('reason=0', 'reason=19')
        $capture = New-Capture $text 1
        Assert-CaptureDiagnostic $capture 0
        Assert-Control (-not [string]::IsNullOrEmpty((ConvertTo-HermesCrashEventTiming $capture)))
        Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        if ($count -eq 3) {
            Assert-Control ((ConvertTo-HermesCrashEventCaptureDiagnostic $capture).Contains('rows=7 shape=1158729'))
        }
    }
    $field = 22
    foreach ($name in $ranges.Keys) {
        $field++
        $original = [regex]::Match($timing, ('(?<= ' + $name + '=)[^ ]+')).Value
        foreach ($value in @($ranges[$name][0], $ranges[$name][1])) {
            $row = Set-TimingField $timing $name ($value.ToString([Globalization.CultureInfo]::InvariantCulture))
            $capture = New-Capture (Get-TimedCommandProtocol $row) 1
            Assert-CaptureDiagnostic $capture 0
            Assert-Control ((ConvertTo-HermesCrashEventTiming $capture).Contains(" $name=$value "))
        }
        foreach ($value in @(($ranges[$name][0] - 1), ($ranges[$name][1] + 1))) {
            $row = Set-TimingField $timing $name ($value.ToString([Globalization.CultureInfo]::InvariantCulture))
            $capture = New-Capture (Get-TimedCommandProtocol $row) 1
            Assert-FactsUnavailable $capture
            Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
            if ($value -ge 0 -or $name -ceq 'native') {
                $check = if (($name -ceq 'native' -and $value -eq -2) `
                    -or $name -cin @('entryMs', 'queryMs')) { 18 } else { 28 }
                Assert-CaptureDiagnostic $capture $check $(if ($check -eq 28) { $field } else { 0 })
            }
        }
        foreach ($value in @('00', '01', '-0', '+1', '1.0', '1e0', 'null', '"1"', 'true',
                '18446744073709551616', '١', 'private/path')) {
            $row = Set-TimingField $timing $name $value
            Assert-FactsUnavailable (New-Capture (Get-TimedCommandProtocol $row) 1)
        }
        foreach ($row in @(
            $timing.Replace(" $name=$original", ''),
            $timing.Replace(" $name=$original", " $name=$original $name=$original"),
            $timing.Replace(" $name=$original", " $($name.ToUpperInvariant())=$original")
        )) { Assert-FactsUnavailable (New-Capture (Get-TimedCommandProtocol $row) 1) }
    }
    foreach ($name in @('last', 'failure', 'expiry', 'location', 'rejected')) {
        foreach ($value in 0..([int]$ranges[$name][1])) {
            Assert-CaptureDiagnostic (New-Capture (Get-TimedCommandProtocol (Set-TimingField $timing $name ([string]$value))) 1) 0
        }
    }
    foreach ($name in @('entryMs', 'queryMs')) {
        foreach ($value in @('-999999', '-1', '0', '1', '999999', '-1000000')) {
            Assert-CaptureDiagnostic (New-Capture (Get-TimedCommandProtocol (Set-TimingField $timing $name $value)) 1) 0
        }
        foreach ($value in @('-1000001', '1000000', '-000001', '-0999999', '-10000000')) {
            Assert-FactsUnavailable (New-Capture (Get-TimedCommandProtocol (Set-TimingField $timing $name $value)) 1)
        }
    }
    foreach ($value in @('-1', '0', '2147483648', '4294967295')) {
        Assert-CaptureDiagnostic (New-Capture (Get-TimedCommandProtocol (Set-TimingField $timing 'native' $value)) 1) 0
    }
    foreach ($row in @(
        $timing.Replace('polls=1 attempts=1', 'attempts=1 polls=1'),
        $timing.Replace(' nativeAcceptance=0', ' extra=0 nativeAcceptance=0'),
        $timing.Replace('nativeAcceptance=0', 'nativeAcceptance=1'),
        $timing.Replace('v=1', 'v=2'), $timing.Replace("`n", "`r`n"),
        $timing.TrimEnd([char]10), ($timing + "`n"), ($timing + [char]0),
        $timing.Replace(' last=3', " last=3 private/path$([char]27)[31m"),
        $timing.Replace(' last=3', ' last=3 query=Application record=private.xml exception=Private.Type token=private'),
        ([string][char]0xfeff + $timing)
    )) { Assert-FactsUnavailable (New-Capture (Get-TimedCommandProtocol $row) 1) }
    foreach ($text in @(
        $timing, ($timing + $protocol), ($protocol + $timing),
        ($observation + $base + $timing + $event),
        (Get-TimedCommandProtocol ($timing * 2) 'failed-guard'),
        ($observation + $timing + $base + $event)
    )) {
        foreach ($status in @(0, 1)) {
            $capture = New-Capture $text $status
            Assert-FactsUnavailable $capture
            Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
        }
    }
    foreach ($name in @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled', 'Stderr', 'Tasks')) {
        $capture = New-Capture (Get-TimedCommandProtocol) 1
        $capture.$name = (New-Capture).$name
        if ($name -ceq 'ExitCode') { $capture.ExitCode = 0 }
        elseif ($name -ceq 'Tasks') { $capture.Tasks = [Threading.Tasks.Task[]]@([Threading.Tasks.Task]::CompletedTask) }
        elseif ($name -ceq 'Stderr') { $capture.Stderr = [byte[]]@(255) }
        else { $capture.$name = 2 }
        Assert-FactsUnavailable $capture
    }
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $capture = New-Capture (Get-TimedCommandProtocol) 1
    $capture.Tasks[1] = $pending.Task
    Assert-FactsUnavailable $capture
    $pending.SetResult(0)
    Assert-CaptureDiagnostic $capture 0
    foreach ($task in @(
        [Threading.Tasks.Task]::FromException([IO.IOException]::new('private/path')),
        [Threading.Tasks.Task]::FromCanceled([Threading.CancellationToken]::new($true))
    )) {
        $capture = New-Capture (Get-TimedCommandProtocol) 1
        $capture.Tasks[1] = $task
        Assert-CaptureDiagnostic $capture 15 8
        try { $null = $task.GetAwaiter().GetResult() } catch { }
    }
    foreach ($name in @((New-Capture).PSObject.Properties.Name)) {
        $capture = New-Capture (Get-TimedCommandProtocol) 1
        $capture.PSObject.Properties.Remove($name)
        Assert-FactsUnavailable $capture
    }
    foreach ($bytes in @([byte[]]@(255), [byte[]]@(0xc0, 0xaf), [byte[]]@(0xc3))) {
        $capture = New-Capture (Get-TimedCommandProtocol) 1
        $capture.Stdout = [byte[]]($capture.Stdout + $bytes)
        Assert-CaptureDiagnostic $capture 17 6
    }
    $culture = [Globalization.CultureInfo]::CurrentCulture
    try {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
        $capture = New-Capture (Get-TimedCommandProtocol $worstTiming) 1
        Assert-Control ((ConvertTo-HermesCrashEventTiming $capture) -ceq
            $worstTiming.Replace('HERMES_CRASH_EVENT_DIAGNOSTIC ', 'HERMES_CRASH_EVENT_TIMING ').TrimEnd([char]10))
    } finally { [Globalization.CultureInfo]::CurrentCulture = $culture }
    Assert-Control ([string]::IsNullOrEmpty((ConvertTo-HermesCrashEventTiming (New-Capture))))
    Assert-Control ([string]::IsNullOrEmpty((ConvertTo-HermesCrashEventTiming (New-Capture (Get-FailedCommandProtocol 'failed-guard') 1))))
}

function Test-ReadBudget {
    $crash = Get-Module Crash-Control
    $stream = [IO.MemoryStream]::new([byte[]]@(120))
    $errors = [IO.MemoryStream]::new([byte[]]::new(0))
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    $operations.Add([Threading.Tasks.Task]::CompletedTask)
    $output = $null
    $stderr = $null
    try {
        $output = & $crash { param($s, $o) New-HermesCrashRead $s 1024 $o } $stream $operations
        $stderr = & $crash { param($s, $o) New-HermesCrashRead $s 1 $o } $errors $operations
        foreach ($index in 1..1023) {
            # Rewind the one-byte stream before each actual continuation read.
            # On the final byte, leave it at EOF to obtain the 1024th read task.
            $stream.Position = [int]($index -eq 1023)
            & $crash { param($r) Update-HermesCrashRead $r } $output
        }
        & $crash { param($r) Update-HermesCrashRead $r } $output
        & $crash { param($r) Update-HermesCrashRead $r } $stderr
        Assert-Control ($output.Count -eq 1023 -and $output.End -and $stderr.End -and $operations.Count -eq 1026)
        Assert-HermesCrashEventSettled $operations
        $snapshot = [object[]]::new($operations.Count)
        for ($index = 0; $index -lt $operations.Count; $index++) { $snapshot[$index] = $operations[$index] }
        Assert-HermesCrashEventTaskBinding $snapshot $operations
        $capture = New-Capture (Get-TimedCommandProtocol) 1
        $capture.Tasks = $snapshot
        Assert-CaptureDiagnostic $capture 0
        $operations.Add([Threading.Tasks.Task]::CompletedTask)
        Assert-Refused { Assert-HermesCrashEventSettled $operations }
        $capture.Tasks = [object[]]@($snapshot + [Threading.Tasks.Task]::CompletedTask)
        Assert-CaptureDiagnostic $capture 13 8
    } finally {
        if ($null -ne $output) { [Array]::Clear($output.Buffer) }
        if ($null -ne $stderr) { [Array]::Clear($stderr.Buffer) }
        $stream.Dispose()
        $errors.Dispose()
    }
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
        @{ Field = 'Stdout'; Value = [byte[]]::new(1024); Check = 9; Code = 6 },
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
    $capture = New-Capture ("`n" * 1023)
    $line = ConvertTo-HermesCrashEventCaptureDiagnostic $capture
    Assert-Control ($line -ceq 'HERMES_CRASH_EVENT_CAPTURE v=1 check=18 field=0 out=1023 err=0 tasks=3 rows=8 shape=1797558')
    $capture = New-Capture
    $capture.Tasks = @([Threading.Tasks.Task]::CompletedTask) * 1027
    Assert-CaptureDiagnostic $capture 13 8
    Assert-Control ((ConvertTo-HermesCrashEventCaptureDiagnostic $capture).Contains('tasks=1027'))
    $capture = New-Capture
    $capture | Add-Member ScriptProperty Stdout { throw "private/path`n$([char]27)[31m" } -Force
    Assert-CaptureDiagnostic $capture 8 6
    $capture = New-Capture ("private/path`n" + ('a' * 64) + "`n$([char]27)[31m")
    Assert-Control ((ConvertTo-HermesCrashEventCaptureDiagnostic $capture) -ceq (
        'HERMES_CRASH_EVENT_CAPTURE v=1 check=18 field=0 out=83 err=0 tasks=3 rows=3 shape=365'))
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
            @($done, $done, $null), (@($done) * 1027))) {
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
    [IO.File]::WriteAllBytes($metadata, $script:schemaBytes)
    $fileCreated = [IO.File]::GetCreationTimeUtc($metadata).Ticks
    $fileWritten = [IO.File]::GetLastWriteTimeUtc($metadata).Ticks
    $entries = @(
        (Get-HermesCrashFileEntry $source 'schema-source.ps1' 4096),
        (Get-HermesCrashFileEntry $workflow 'workflow.yml' 4096),
        (Get-HermesCrashFileEntry $metadata 'startup-event-schema.json' 4096)
    )
    $checkout = [pscustomobject]@{ Revision = ('a' * 40); Tree = ('b' * 40); SourceSha256 = ('c' * 64) }
    # Synthetic predicate inputs: filesystem and wall clocks need not share ordering or precision.
    # Real file observations below test replacement identity, not genuine publication freshness.
    $started = [DateTime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc).Ticks
    $shape = [ordered]@{
        schemaVersion = 1; revision = $checkout.Revision; tree = $checkout.Tree; sourceSha256 = $checkout.SourceSha256
        workflowSha256 = $entries[1].sha256; schemaSourceSha256 = $entries[0].sha256
        metadataSha256 = $entries[2].sha256; length = $entries[2].length; runId = 123; runAttempt = 2
        startedUtcTicks = $started; completedUtcTicks = $started + (10 * [TimeSpan]::TicksPerSecond)
        createdUtcTicks = $started + [TimeSpan]::TicksPerSecond
        writtenUtcTicks = $started + (2 * [TimeSpan]::TicksPerSecond)
    }
    $bytes = ConvertTo-Bytes $shape
    $receipt = Read-HermesCrashEventPublication $bytes
    Assert-Control ($receipt.Count -eq 14)
    Assert-HermesCrashEventPublicationBinding $receipt $checkout $entries[1] $entries[0] $entries[2] '123' '2'
    $created = $shape.createdUtcTicks
    $written = $shape.writtenUtcTicks
    Assert-HermesCrashEventFreshness $receipt $created $written $shape.completedUtcTicks
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
    foreach ($name in @('createdUtcTicks', 'writtenUtcTicks')) {
        $saved = $receipt[$name]
        foreach ($outside in @(($started - 1), ($shape.completedUtcTicks + 1))) {
            $receipt[$name] = $outside
            Assert-Refused { Assert-HermesCrashEventFreshness $receipt $receipt.createdUtcTicks $receipt.writtenUtcTicks $boundary }
        }
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
    [IO.File]::SetLastWriteTimeUtc($metadata, [DateTime]::new($fileWritten + [TimeSpan]::TicksPerSecond, [DateTimeKind]::Utc))
    $replacementCreated = [IO.File]::GetCreationTimeUtc($metadata).Ticks
    $replacementWritten = [IO.File]::GetLastWriteTimeUtc($metadata).Ticks
    Assert-Control ($replacementWritten -ne $fileWritten)
    # Apply real replacement deltas to the synthetic clock, without claiming live publication proof.
    Assert-Refused { Assert-HermesCrashEventFreshness $receipt ($created + ($replacementCreated - $fileCreated)) `
        ($written + ($replacementWritten - $fileWritten)) $boundary }
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
    $verified = $false
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
            $good = $match.Success -and [int]$match.Groups[1].Value -ge 3 -and [int]$match.Groups[1].Value -le 1026 `
                -and $capture.ExitCode -eq 0 -and $capture.Stderr.Length -eq 0
            Assert-Control $good
            if (-not $good) { throw '1' }
            $accepted = $true
            $verified = $true
            return
        }
        if ($Name -ceq 'at-cap') { Assert-Control ($capture.Stdout.Length -eq 1023); $verified = $true }
        if ($Name -ceq 'wrapper-failure') {
            $newline = [Environment]::NewLine
            $expected = "HERMES_CRASH_EVENT_WRAPPER v=1 status=1 published=0 originalGatePassed=0 nativeAcceptance=0$newline" +
                "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=9 family=0 reason=0$newline" +
                "$script:unavailable$newline" +
                "HERMES_CRASH_EVENT_CAPTURE v=1 check=18 field=0 out=18 err=0 tasks=3 rows=2 shape=45$newline"
            Assert-Control ($capture.ExitCode -eq 1 -and $capture.Stderr.Length -eq 0 `
                -and [Text.Encoding]::UTF8.GetString($capture.Stdout) -ceq $expected)
            $verified = $true
            return
        }
        if ($Name.StartsWith('wrapper-timing', [StringComparison]::Ordinal)) {
            $row = if ($Name -ceq 'wrapper-timing-invalid') { $timing.Replace('native=-1', 'native=4294967296') } else { $timing }
            $inner = New-Capture (Get-TimedCommandProtocol $row) 1
            if ($Name -ceq 'wrapper-timing-success') { $inner = New-Capture ($observation + $timing + $base + $event) }
            $newline = [Environment]::NewLine
            $phase = if ($Name -ceq 'wrapper-timing-phase') { 10 } else { 9 }
            $expected = "HERMES_CRASH_EVENT_WRAPPER v=1 status=1 published=0 originalGatePassed=0 nativeAcceptance=0$newline" +
                "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=$phase family=0 reason=0$newline"
            if ($phase -eq 9) {
                $expected += "$(ConvertTo-HermesCrashEventFacts $inner)$newline" +
                    "$(ConvertTo-HermesCrashEventCaptureDiagnostic $inner)$newline"
            }
            if ($Name -ceq 'wrapper-timing') {
                $expected += $timing.Replace('HERMES_CRASH_EVENT_DIAGNOSTIC ', 'HERMES_CRASH_EVENT_TIMING ').TrimEnd([char]10) + $newline
            }
            Assert-Control ($capture.ExitCode -eq 1 -and $capture.Stderr.Length -eq 0 `
                -and [Text.Encoding]::UTF8.GetString($capture.Stdout) -ceq $expected)
            $verified = $true
            return
        }
        if ($Name -ceq 'failure') {
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture).Contains('available=1 status=1 controlStatus=1 reason=10'))
        }
        if ($Name.StartsWith('failed-', [StringComparison]::Ordinal)) {
            Assert-CaptureDiagnostic $capture 0
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture).Contains('available=1 status=1'))
        }
        if ($Name -in @('timing', 'timing-worst')) {
            Assert-CaptureDiagnostic $capture 0
            $row = if ($Name -ceq 'timing') { $timing } else { $worstTiming }
            $expected = $row.Replace('HERMES_CRASH_EVENT_DIAGNOSTIC ', 'HERMES_CRASH_EVENT_TIMING ').TrimEnd([char]10)
            $raw = [Convert]::ToBase64String($capture.Stdout)
            Assert-Control ((ConvertTo-HermesCrashEventTiming $capture) -ceq $expected)
            Assert-Control ((ConvertTo-HermesCrashEventFacts $capture).Contains('available=1 status=1'))
            if ($Name -ceq 'timing-worst') { Assert-Control ($capture.Stdout.Length -eq 901) }
            Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
            Assert-Control ([Convert]::ToBase64String($capture.Stdout) -ceq $raw)
            $verified = $true
            return
        }
        if ($Name -in @('timing-native', 'timing-order', 'timing-duplicate', 'timing-success', 'timing-utf8')) {
            $check = switch -CaseSensitive ($Name) {
                'timing-native' { 28 }; 'timing-success' { 26 }; 'timing-utf8' { 17 }; default { 18 }
            }
            $field = switch -CaseSensitive ($Name) { 'timing-native' { 30 }; 'timing-utf8' { 6 }; default { 0 } }
            Assert-CaptureDiagnostic $capture $check $field
            $verified = $true
        }
        Assert-HermesCrashEventOutcome $capture $script:control
        $accepted = $true
    } catch {
        if ($Name -in @('overflow', 'timing-overflow', 'stderr', 'timing-stderr')) {
            Assert-Control ($null -eq $capture -and $_.Exception.Message -ceq '13')
            $verified = $true
        }
    } finally {
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
        if ($Name.StartsWith('timing', [StringComparison]::Ordinal) `
            -or $Name.StartsWith('wrapper-', [StringComparison]::Ordinal) `
            -or $Name -in @('task-contract', 'task-contract-load', 'at-cap')) { Assert-Control $verified }
    }
    Assert-Control ($accepted -eq ($Name -in @('valid', 'task-contract', 'task-contract-load')))
}

function Test-WerPolicySnapshot {
    # Extract definitions into this local scope: every host capability below is
    # fake, even on Windows. No policy/task/registry read occurs in controls.
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'Crash-Event-Control.psm1'), [ref]$tokens, [ref]$errors)
    Assert-Control ($errors.Count -eq 0)
    $definitions = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] `
            -and ($node.Name -cmatch 'EventWer' -or $node.Name -ceq 'Invoke-HermesCrashEventSchemaPublication')
    }, $false))
    foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    $private = "private/path`n$([char]27)[31m"
    $script:wer = @{
        Calls = [Collections.Generic.List[string]]::new(); Fail = ''; KeyMissing = $false; ValueMissing = $false
        Kind = [Microsoft.Win32.RegistryValueKind]::DWord; Value = 0; TaskValue = 3; TaskError = 0
        FailGuard = $false; Guards = 0
    }
    $script:wer.Key = [pscustomobject]@{}
    $script:wer.Key | Add-Member ScriptMethod GetValue {
        param($Name, $Default, $Options)
        $script:wer.Calls.Add("value:$Name|$([int]$Options)")
        if ($script:wer.Fail -ceq 'value') { throw "private/path`n$([char]27)[31m" }
        if ($script:wer.ValueMissing) { return $Default }
        return ,$script:wer.Value
    }
    $script:wer.Key | Add-Member ScriptMethod GetValueKind {
        param($Name)
        $script:wer.Calls.Add("kind:$Name")
        if ($script:wer.Fail -ceq 'kind') { throw "private/path`n$([char]27)[31m" }
        return $script:wer.Kind
    }
    $script:wer.Key | Add-Member ScriptMethod Dispose {
        $script:wer.Calls.Add('close:key')
        if ($script:wer.Fail -ceq 'key-close') { throw "private/path`n$([char]27)[31m" }
        return 'private/noise'
    }
    $script:wer.Base = [pscustomobject]@{}
    $script:wer.Base | Add-Member ScriptMethod OpenSubKey {
        param($Path, $Writable)
        $script:wer.Calls.Add("key:$Path|$([int]$Writable)")
        if ($script:wer.Fail -ceq 'open-key') { throw "private/path`n$([char]27)[31m" }
        if ($script:wer.KeyMissing) { return $null }
        return $script:wer.Key
    }
    $script:wer.Base | Add-Member ScriptMethod Dispose {
        $script:wer.Calls.Add('close:base')
        if ($script:wer.Fail -ceq 'base-close') { throw "private/path`n$([char]27)[31m" }
        return 'private/noise'
    }
    function Open-EventWerRegistryBase {
        param([bool]$CurrentUser)
        $script:wer.Calls.Add("base:$([int]$CurrentUser)")
        if ($script:wer.Fail -ceq 'open-base') { throw "private/path`n$([char]27)[31m" }
        return $script:wer.Base
    }
    $path = 'SOFTWARE\Microsoft\Windows\Windows Error Reporting'
    $expected = @(
        "base:0|key:$path|0|value:Disabled|1|kind:Disabled|close:key|close:base",
        "base:1|key:$path|0|value:Disabled|1|kind:Disabled|close:key|close:base",
        "base:0|key:$path|0|value:ForceQueue|1|kind:ForceQueue|close:key|close:base",
        "base:0|key:$path|0|value:DontShowUI|1|kind:DontShowUI|close:key|close:base",
        "base:0|key:$path\Consent|0|value:DefaultConsent|1|kind:DefaultConsent|close:key|close:base",
        'base:0|key:SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting|0|value:Disabled|1|kind:Disabled|close:key|close:base',
        'base:0|key:SYSTEM\CurrentControlSet\Services\WerSvc|0|value:Start|1|kind:Start|close:key|close:base'
    )
    foreach ($field in 1..7) {
        $script:wer.Calls.Clear()
        $rows = @(Get-EventWerRegistryState $field)
        Assert-Control ($rows.Count -eq 1 -and $rows[0] -is [int] -and $rows[0] -eq 1)
        Assert-Control (($script:wer.Calls -join '|') -ceq $expected[$field - 1])
    }
    foreach ($field in @(-1, 0, 8, [int]::MaxValue)) {
        $script:wer.Calls.Clear()
        Assert-Refused { Get-EventWerRegistryState $field }
        Assert-Control ($script:wer.Calls.Count -eq 0)
    }
    foreach ($value in 0..4) {
        Assert-Control ((ConvertTo-EventWerRegistryState ([Microsoft.Win32.RegistryValueKind]::DWord) $value) -eq (1 + $value))
    }
    foreach ($value in @(5, -1, [int]::MinValue, [int]::MaxValue)) {
        Assert-Control ((ConvertTo-EventWerRegistryState ([Microsoft.Win32.RegistryValueKind]::DWord) $value) -eq 6)
    }
    foreach ($kind in [Enum]::GetValues([Microsoft.Win32.RegistryValueKind])) {
        if ($kind -eq [Microsoft.Win32.RegistryValueKind]::DWord) { continue }
        $expectedState = if ($kind -eq [Microsoft.Win32.RegistryValueKind]::Unknown) { 8 } else { 7 }
        Assert-Control ((ConvertTo-EventWerRegistryState $kind $private) -eq $expectedState)
    }
    foreach ($value in @($null, $true, '0', $private, 0L, [uint32]0, [double]0, [decimal]0, [byte]0, @(0, 1), [pscustomobject]@{})) {
        Assert-Control ((ConvertTo-EventWerRegistryState ([Microsoft.Win32.RegistryValueKind]::DWord) $value) -eq 7)
        $script:wer.Value = $value
        Assert-Control ((Get-EventWerRegistryState 1) -eq 7)
    }
    $script:wer.Value = 0
    foreach ($kind in @($null, 'DWord', $private, 4, [Microsoft.Win32.RegistryValueOptions]::None)) {
        Assert-Control ((ConvertTo-EventWerRegistryState $kind 0) -eq 8)
    }
    foreach ($missing in @('KeyMissing', 'ValueMissing')) {
        $script:wer.Calls.Clear()
        $script:wer[$missing] = $true
        Assert-Control ((Get-EventWerRegistryState 1) -eq 0)
        Assert-Control (-not ($script:wer.Calls -join '|').Contains('kind:'))
        Assert-Control ($script:wer.Calls[$script:wer.Calls.Count - 1] -ceq 'close:base')
        $script:wer[$missing] = $false
    }
    foreach ($failure in @('open-base', 'open-key', 'value', 'kind', 'key-close', 'base-close')) {
        $script:wer.Calls.Clear()
        $script:wer.Fail = $failure
        Assert-Control ((Get-EventWerRegistryState 1) -eq 9)
        $expectedCloses = switch ($failure) { 'open-base' { '' }; 'open-key' { 'close:base' }; default { 'close:key|close:base' } }
        Assert-Control ((@($script:wer.Calls | Where-Object { $_.StartsWith('close:') }) -join '|') -ceq $expectedCloses)
    }
    $script:wer.Fail = ''
    $script:wer.Task = [pscustomobject]@{ Id = 'task' }
    $script:wer.Task | Add-Member ScriptProperty State {
        $script:wer.Calls.Add('state')
        if ($script:wer.Fail -ceq 'state') { throw "private/path`n$([char]27)[31m" }
        return ,$script:wer.TaskValue
    }
    $script:wer.Folder = [pscustomobject]@{ Id = 'folder' }
    $script:wer.Folder | Add-Member ScriptMethod GetTask {
        param($Name)
        $script:wer.Calls.Add("task:$Name")
        if ($script:wer.TaskError -ne 0) { throw [Runtime.InteropServices.COMException]::new('private/path', $script:wer.TaskError) }
        if ($script:wer.Fail -ceq 'task') { throw "private/path`n$([char]27)[31m" }
        return $script:wer.Task
    }
    $script:wer.Service = [pscustomobject]@{ Id = 'service' }
    $script:wer.Service | Add-Member ScriptMethod Connect {
        $script:wer.Calls.Add('connect')
        if ($script:wer.Fail -ceq 'connect') { throw "private/path`n$([char]27)[31m" }
        return 'private/noise'
    }
    $script:wer.Service | Add-Member ScriptMethod GetFolder {
        param($Path)
        $script:wer.Calls.Add("folder:$Path")
        if ($script:wer.Fail -ceq 'folder') { throw "private/path`n$([char]27)[31m" }
        return $script:wer.Folder
    }
    function New-EventWerTaskService {
        $script:wer.Calls.Add('service')
        if ($script:wer.Fail -ceq 'service') { throw "private/path`n$([char]27)[31m" }
        return $script:wer.Service
    }
    function Close-EventWerTaskObject {
        param([object]$Value)
        $script:wer.Calls.Add("release:$($Value.Id)")
        if ($script:wer.Fail -ceq ('release:' + $Value.Id)) { throw "private/path`n$([char]27)[31m" }
    }
    $script:wer.Calls.Clear()
    Assert-Control ((Get-EventWerTaskState) -eq 3)
    Assert-Control (($script:wer.Calls -join '|') -ceq (
        'service|connect|folder:\Microsoft\Windows\Windows Error Reporting|task:QueueReporting|state|release:task|release:folder|release:service'))
    foreach ($value in 1..4) { Assert-Control ((ConvertTo-EventWerTaskState $value) -eq $value) }
    foreach ($value in @(0, -1, 5, [int]::MinValue, [int]::MaxValue)) {
        Assert-Control ((ConvertTo-EventWerTaskState $value) -eq 5)
    }
    foreach ($value in @($null, $true, '3', $private, 3L, [uint32]3, [double]3, @(3, 4), [pscustomobject]@{})) {
        Assert-Control ((ConvertTo-EventWerTaskState $value) -eq 6)
        $script:wer.TaskValue = $value
        $expectedState = if ($null -eq $value) { 7 } else { 6 }
        Assert-Control ((Get-EventWerTaskState) -eq $expectedState)
    }
    $script:wer.TaskValue = 3
    foreach ($errorCode in @(-2147024894, -2147024893, -2147024891)) {
        $script:wer.TaskError = $errorCode
        $script:wer.Calls.Clear()
        $expectedState = if ($errorCode -eq -2147024891) { 7 } else { 0 }
        Assert-Control ((Get-EventWerTaskState) -eq $expectedState)
        Assert-Control (($script:wer.Calls -join '|').EndsWith('release:folder|release:service'))
    }
    $script:wer.TaskError = 0
    foreach ($failure in @('service', 'connect', 'folder', 'task', 'state', 'release:task', 'release:folder', 'release:service')) {
        $script:wer.Calls.Clear()
        $script:wer.Fail = $failure
        Assert-Control ((Get-EventWerTaskState) -eq 7)
        $expectedReleases = switch ($failure) {
            'service' { '' }
            { $_ -in @('connect', 'folder') } { 'release:service' }
            'task' { 'release:folder|release:service' }
            default { 'release:task|release:folder|release:service' }
        }
        Assert-Control ((@($script:wer.Calls | Where-Object { $_.StartsWith('release:') }) -join '|') -ceq $expectedReleases)
    }
    $script:wer.Fail = ''
    function Assert-HermesCrashHost {
        $script:wer.Guards++
        if ($script:wer.FailGuard) { throw "private/path`n$([char]27)[31m" }
    }
    function Get-EventWerRegistryState {
        param([int]$Field)
        $script:wer.Calls.Add("registry:$Field")
        if ($script:wer.Fail -ceq 'registry') { throw "private/path`n$([char]27)[31m" }
        return ,$script:wer.Value
    }
    function Get-EventWerTaskState {
        $script:wer.Calls.Add('task')
        if ($script:wer.Fail -ceq 'task') { throw "private/path`n$([char]27)[31m" }
        return ,$script:wer.TaskValue
    }
    function Assert-WerSnapshot {
        param([int]$Registry, [int]$Task, [int]$Status)
        $script:wer.Calls.Clear()
        $rows = @(Get-EventWerPolicySnapshot)
        Assert-Control ($rows.Count -eq 1 -and $rows[0] -is [string] -and $rows[0].Length -lt 512)
        Assert-Control ($rows[0] -ceq (
            "HERMES_CRASH_WER_POLICY v=1 status=$Status lmDisabled=$Registry cuDisabled=$Registry forceQueue=$Registry dontShowUI=$Registry consent=$Registry policyDisabled=$Registry werSvcStart=$Registry queueReporting=$Task advisory=1 originalGatePassed=0 nativeAcceptance=0"))
        Assert-Control (($script:wer.Calls -join '|') -ceq 'registry:1|registry:2|registry:3|registry:4|registry:5|registry:6|registry:7|task')
    }
    foreach ($value in 0..9) {
        $script:wer.Value = $value
        Assert-WerSnapshot $value 3 ([int]($value -ge 7))
    }
    $script:wer.Value = 0
    foreach ($value in 0..7) {
        $script:wer.TaskValue = $value
        Assert-WerSnapshot 0 $value ([int]($value -ge 5))
    }
    foreach ($value in @($null, $true, '0', $private, 0L, -1, 10, [int]::MaxValue, @(0, 1))) {
        $script:wer.Value = $value
        $script:wer.TaskValue = $value
        Assert-WerSnapshot 9 7 1
    }
    $script:wer.Value = 0
    $script:wer.TaskValue = 0
    foreach ($failure in @('registry', 'task')) {
        $script:wer.Fail = $failure
        if ($failure -ceq 'registry') { Assert-WerSnapshot 9 0 1 } else { Assert-WerSnapshot 0 7 1 }
    }
    $script:wer.Fail = ''
    $culture = [Globalization.CultureInfo]::CurrentCulture
    try {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
        Assert-WerSnapshot 0 0 0
    } finally { [Globalization.CultureInfo]::CurrentCulture = $culture }
    $script:wer.FailGuard = $true
    $script:wer.Calls.Clear()
    Assert-Refused { Get-EventWerPolicySnapshot }
    Assert-Control ($script:wer.Calls.Count -eq 0)
    $writer = [IO.StringWriter]::new([Globalization.CultureInfo]::InvariantCulture)
    $output = [Console]::Out
    try {
        [Console]::SetOut($writer)
        Assert-Refused { Invoke-HermesCrashEventSchemaPublication }
    } finally { [Console]::SetOut($output) }
    try {
        Assert-Control ($writer.ToString() -ceq ("HERMES_CRASH_EVENT_SCHEMA_PUBLICATION v=1 status=1" + [Environment]::NewLine))
        Assert-Control ($script:wer.Calls.Count -eq 0)
    } finally { $writer.Dispose() }
    if (-not [OperatingSystem]::IsWindows()) {
        Assert-Refused { & (Get-Module Crash-Event-Control) { Get-EventWerPolicySnapshot } }
    }
    Remove-Variable -Name wer -Scope Script
}

function Test-WerPolicyBoundaries {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot 'Crash-Event-Control.psm1'), [ref]$tokens, [ref]$errors)
    $functions = @{}
    foreach ($node in $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
        $functions[$node.Name] = $node
    }
    $reader = $functions['Get-EventWerRegistryState'].Extent.Text
    Assert-Control ($reader.Contains('[ValidateRange(1, 7)]') -and $reader.Contains('$base.OpenSubKey($path, $false)') `
        -and $reader.Contains('[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames') `
        -and $reader.Contains('[object]::ReferenceEquals($value, $missing)') -and $reader.Contains('$null = $handle.Dispose()'))
    $paths = @($functions['Get-EventWerRegistryState'].Body.FindAll({
        param($node)
        $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value -cmatch '\A(SOFTWARE|SYSTEM)\\'
    }, $false) | ForEach-Object { $_.Value })
    Assert-Control (($paths -join '|') -ceq (
        'SOFTWARE\Microsoft\Windows\Windows Error Reporting|SOFTWARE\Microsoft\Windows\Windows Error Reporting\Consent|SOFTWARE\Policies\Microsoft\Windows\Windows Error Reporting|SYSTEM\CurrentControlSet\Services\WerSvc'))
    $open = $functions['Open-EventWerRegistryBase'].Extent.Text
    Assert-Control ($open.Contains('[Microsoft.Win32.RegistryHive]::CurrentUser') `
        -and $open.Contains('[Microsoft.Win32.RegistryHive]::LocalMachine') `
        -and $open.Contains('[Microsoft.Win32.RegistryView]::Registry64'))
    $task = $functions['Get-EventWerTaskState']
    Assert-Control ($null -eq $task.Body.ParamBlock `
        -and $task.Extent.Text.Contains('$null = $service.Connect()') `
        -and $task.Extent.Text.Contains("`$service.GetFolder('\Microsoft\Windows\Windows Error Reporting')") `
        -and $task.Extent.Text.Contains("`$folder.GetTask('QueueReporting')") `
        -and $task.Extent.Text.Contains('@($task, $folder, $service)'))
    Assert-Control ($functions['New-EventWerTaskService'].Extent.Text.Contains("New-Object -ComObject 'Schedule.Service' -ErrorAction Stop") `
        -and $functions['Close-EventWerTaskObject'].Extent.Text.Contains('[Runtime.InteropServices.Marshal]::FinalReleaseComObject($Value)'))
    $snapshot = $functions['Get-EventWerPolicySnapshot']
    Assert-Control ($null -eq $snapshot.Body.ParamBlock `
        -and $snapshot.Body.EndBlock.Statements[0].Extent.Text -ceq 'Assert-HermesCrashHost')
    $publication = $functions['Invoke-HermesCrashEventSchemaPublication'].Extent.Text
    Assert-Control ([regex]::Matches($publication, 'Get-EventWerPolicySnapshot').Count -eq 1 `
        -and $publication.IndexOf('Assert-HermesCrashHost', [StringComparison]::Ordinal) -lt
            $publication.IndexOf('Get-EventWerPolicySnapshot', [StringComparison]::Ordinal) `
        -and $publication.IndexOf('if ($root -cne', [StringComparison]::Ordinal) -lt
            $publication.IndexOf('Get-EventWerPolicySnapshot', [StringComparison]::Ordinal) `
        -and $publication.IndexOf('Get-EventWerPolicySnapshot', [StringComparison]::Ordinal) -lt
            $publication.IndexOf('$summary = @(', [StringComparison]::Ordinal))
    foreach ($name in @($functions.Keys | Where-Object { $_ -cmatch 'EventWer' })) {
        $text = $functions[$name].Extent.Text
        Assert-Control ($text -cnotmatch '(?i)\$env:|Get-WinEvent|ReadAll|WriteAll|Start-Process|SetValue|CreateSubKey|Delete|Set-|Register-|Enable-|Disable-|\.Run\(|\.Start\(|ChangeService|Get-ChildItem|GetValueNames|\.Definition|\.Xml|\.Actions|\.Principal|\.Settings')
        Assert-Control (-not (Get-Module Crash-Event-Control).ExportedFunctions.ContainsKey($name))
    }
}

function Assert-PresenceRefused {
    param([object]$Capture)
    Assert-Refused { Assert-HermesCrashEventPresence $Capture }
    Assert-Control ((ConvertTo-HermesCrashEventFacts $Capture -OwnedPresence) -ceq (
        'HERMES_CRASH_EVENT_PRESENCE_FACTS v=1 available=0 originalGatePassed=0 precisionAccepted=0 nativeAcceptance=0'))
    Assert-Control ([string]::IsNullOrEmpty((ConvertTo-HermesCrashEventTiming $Capture -OwnedPresence)))
}

function Test-OwnedPresence {
    $presence = "HERMES_CRASH_EVENT_PRESENCE v=1 status=1 outcome=2 reason=1 controlStatus=0 controlError=0 cleanupError=0 receiptClosed=1 polls=1 queries=1 originalGatePassed=0 precisionAccepted=0 nativeAcceptance=0`n"
    $row = $timing.Replace('last=3', 'last=9').Replace('expiry=1', 'expiry=0')
    $row = $row.Replace('location=3', 'location=0').Replace('rejected=1', 'rejected=0')
    $text = $row + $presence
    $capture = New-Capture $text 1
    $saved = [Convert]::ToBase64String($capture.Stdout)
    Assert-Control (@(Assert-HermesCrashEventPresence $capture).Count -eq 0)
    Assert-Control ((ConvertTo-HermesCrashEventFacts $capture -OwnedPresence) -ceq (
        'HERMES_CRASH_EVENT_PRESENCE_FACTS v=1 available=1 status=1 outcome=2 reason=1 controlStatus=0 controlError=0 cleanupError=0 receiptClosed=1 polls=1 queries=1 originalGatePassed=0 precisionAccepted=0 nativeAcceptance=0'))
    Assert-Control ((ConvertTo-HermesCrashEventTiming $capture -OwnedPresence) -ceq (
        $row.Replace('HERMES_CRASH_EVENT_DIAGNOSTIC ', 'HERMES_CRASH_EVENT_TIMING ').TrimEnd([char]10)))
    Assert-Control ((ConvertTo-HermesCrashEventCaptureDiagnostic $capture -OwnedPresence).Contains('check=0 field=0'))
    Assert-Control ([Convert]::ToBase64String($capture.Stdout) -ceq $saved)
    Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }
    Assert-FactsUnavailable $capture
    Assert-PresenceRefused (New-Capture $text 0)
    Assert-PresenceRefused (New-Capture $protocol)
    Assert-PresenceRefused (New-Capture $protocol 1)

    $guard = $presence.Replace('outcome=2', 'outcome=0').Replace('reason=1', 'reason=2')
    $guard = $guard.Replace('controlStatus=0', 'controlStatus=1').Replace('controlError=0', 'controlError=1')
    $guard = $guard.Replace('receiptClosed=1', 'receiptClosed=0').Replace('polls=1', 'polls=0').Replace('queries=1', 'queries=0')
    Assert-Control (@(Assert-HermesCrashEventPresence (New-Capture $guard 1)).Count -eq 0)
    Assert-Control ([string]::IsNullOrEmpty((ConvertTo-HermesCrashEventTiming (New-Capture $guard 1) -OwnedPresence)))
    Assert-PresenceRefused (New-Capture $presence 1)

    $fields = [ordered]@{
        v = 1; status = 1; outcome = 2; reason = 1; controlStatus = 0
        controlError = 0; cleanupError = 0; receiptClosed = 1; polls = 1; queries = 1
        originalGatePassed = 0; precisionAccepted = 0; nativeAcceptance = 0
    }
    Assert-Control ($fields.Count -eq 13)
    Assert-Control ($presence.EndsWith(" nativeAcceptance=0`n", [StringComparison]::Ordinal))
    foreach ($name in $fields.Keys) {
        # Match the token, not its delimiter: the last field ends with LF.
        $token = [regex]::new(
            [regex]::Escape(" $name=$($fields[$name])") + '(?= |\n)')
        Assert-Control ($token.Matches($presence).Count -eq 1)
        foreach ($bad in @('-0', '+0', '00', '01', '1.0', '1e0', '-1', '4294967296',
                '18446744073709551616', 'true', 'null', '"1"', '١')) {
            Assert-PresenceRefused (New-Capture ($row + $token.Replace($presence, " $name=$bad", 1)) 1)
        }
        Assert-PresenceRefused (New-Capture ($row + $token.Replace($presence, '', 1)) 1)
        Assert-PresenceRefused (New-Capture ($row + $token.Replace(
            $presence, " $name=$($fields[$name]) $name=$($fields[$name])", 1)) 1)
    }
    $bounds = [ordered]@{
        outcome = 10; reason = 22; controlStatus = 2; controlError = 12
        cleanupError = 256; receiptClosed = 2; polls = 82; queries = 82
        status = 0; originalGatePassed = 1; precisionAccepted = 1; nativeAcceptance = 1
    }
    foreach ($name in $bounds.Keys) {
        $token = [regex]::new(
            [regex]::Escape(" $name=$($fields[$name])") + '(?= |\n)')
        Assert-Control ($token.Matches($presence).Count -eq 1)
        Assert-PresenceRefused (New-Capture ($row + $token.Replace(
            $presence, " $name=$($bounds[$name])", 1)) 1)
    }
    foreach ($bad in @(
        '', $row, ($text * 2), ($presence + $row), ($row + $row + $presence),
        $text.TrimEnd([char]10), $text.Replace("`n", "`r`n"), ($text + "`n"),
        ([string][char]0xfeff + $text), ($text + [char]0), ("private/path`n" + $text),
        ($row + $presence.Replace(' outcome=2 ', ' unknown=0 outcome=2 ')),
        ($row + $presence.Replace(' outcome=2 ', ' Outcome=2 ')),
        ($row + $presence.Replace('queries=1', 'queries=2')),
        ($row + $presence.Replace('polls=1', 'polls=0')),
        ($row + $presence.Replace('polls=1', 'polls=3')),
        ($row + $presence.Replace('controlError=0', 'controlError=1')),
        ($row + $presence.Replace('cleanupError=0', 'cleanupError=1')),
        ($row.Replace('polls=1', 'polls=0') + $presence),
        ($row.Replace('attempts=1', 'attempts=0') + $presence),
        ($row.Replace('returns=1', 'returns=2') + $presence),
        ($row.Replace('empty=0', 'empty=2') + $presence),
        ($row.Replace('qMaxUs=12000', 'qMaxUs=12001') + $presence),
        ($row.Replace('native=-1', 'native=4294967296') + $presence),
        ($row.Replace('native=-1', 'native=-0') + $presence),
        ($row.Replace('queryMs=19999', 'queryMs=+19999') + $presence),
        ($row.Replace('failure=0', 'failure=6') + $presence),
        ($row.Replace('last=9', 'last=10') + $presence),
        ($row.Replace('expiry=0', 'expiry=4') + $presence),
        ($row.Replace('location=0', 'location=6') + $presence),
        ($row.Replace('rejected=0', 'rejected=2') + $presence),
        ('x' * 600)
    )) { Assert-PresenceRefused (New-Capture $bad 1) }

    $maximum = $presence.Replace('outcome=2', 'outcome=9').Replace('reason=1', 'reason=21')
    $maximum = $maximum.Replace('controlStatus=0', 'controlStatus=1').Replace('controlError=0', 'controlError=11')
    $maximum = $maximum.Replace('cleanupError=0', 'cleanupError=255').Replace('polls=1', 'polls=81').Replace('queries=1', 'queries=81')
    $maximumTiming = $worstTiming.Replace('polls=999', 'polls=81').Replace('attempts=999', 'attempts=81')
    $maximumTiming = $maximumTiming.Replace('returns=999', 'returns=81').Replace('empty=999', 'empty=81')
    Assert-Control ($maximum.Length -eq 206 -and $worstTiming.Length -eq 393)
    $capture = New-Capture ($maximumTiming + $maximum) 1
    Assert-Control ($capture.Stdout.Length -le 599)
    Assert-Control (@(Assert-HermesCrashEventPresence $capture).Count -eq 0)
    Assert-Refused { Assert-HermesCrashEventOutcome $capture $script:control }

    foreach ($name in @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled')) {
        foreach ($bad in @($null, $true, '1', 1L, -1, 2)) {
            $capture = New-Capture $text 1
            $capture.$name = $bad
            Assert-PresenceRefused $capture
        }
    }
    foreach ($name in @((New-Capture).PSObject.Properties.Name)) {
        $capture = New-Capture $text 1
        $capture.PSObject.Properties.Remove($name)
        Assert-PresenceRefused $capture
    }
    $capture = New-Capture $text 1
    $capture.Stdout = [byte[]]@(255)
    Assert-PresenceRefused $capture
    $capture = New-Capture $text 1
    $capture.Stderr = [byte[]]@(120)
    Assert-PresenceRefused $capture
    $done = [Threading.Tasks.Task]::CompletedTask
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $faulted = [Threading.Tasks.Task]::FromException([ArithmeticException]::new('private/path'))
    foreach ($tasks in @(
        ([object[]]@($done, $done)), ([object[]]@($done, $done, $pending.Task)),
        ([object[]]@($done, $done, $faulted)), ([object[]]@($done, $done, $null)),
        ([Threading.Tasks.Task[]]@($done, $done, $done)), ([object[]](@($done) * 1027))
    )) {
        $capture = New-Capture $text 1
        $capture.Tasks = $tasks
        Assert-PresenceRefused $capture
    }
    $pending.SetResult(0)
    try { $null = $faulted.GetAwaiter().GetResult() } catch { }

    # Bind this protocol to actual operation references, including order and a
    # second check. Parsed numeric settlement claims cannot replace this fence.
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    foreach ($index in 1..3) {
        $source = [Threading.Tasks.TaskCompletionSource[int]]::new()
        $source.SetResult($index)
        $operations.Add($source.Task)
    }
    $capture = New-Capture $text 1
    $capture.Tasks = [object[]]::new(3)
    foreach ($index in 0..2) { $capture.Tasks[$index] = $operations[$index] }
    Assert-HermesCrashEventTaskBinding $capture.Tasks $operations
    Assert-HermesCrashEventPresence $capture
    Assert-HermesCrashEventTaskBinding $capture.Tasks $operations
    Assert-Control $true
    $capture.Tasks[1] = $operations[0]
    Assert-Refused { Assert-HermesCrashEventTaskBinding $capture.Tasks $operations }
    $capture.Tasks = [object[]]@($operations[1], $operations[0], $operations[2])
    Assert-Refused { Assert-HermesCrashEventTaskBinding $capture.Tasks $operations }
    $capture.Tasks = [object[]]@($operations[0], $operations[1])
    Assert-Refused { Assert-HermesCrashEventTaskBinding $capture.Tasks $operations }
}

function Test-OwnedPresenceSelection {
    $eventName = $env:GITHUB_EVENT_NAME
    $job = $env:GITHUB_JOB
    try {
        [Environment]::SetEnvironmentVariable('GITHUB_EVENT_NAME', 'workflow_dispatch')
        [Environment]::SetEnvironmentVariable('GITHUB_JOB', 'installer')
        Assert-Control ((Get-HermesCrashEventCommand) -ceq 'startup-crash-event-probe')
        Assert-Control ((Get-HermesCrashEventCommand -OwnedPresence) -ceq 'startup-crash-event-presence')
        foreach ($name in @('', 'push', 'pull_request', 'Workflow_dispatch')) {
            [Environment]::SetEnvironmentVariable('GITHUB_EVENT_NAME', $name)
            Assert-Refused { Get-HermesCrashEventCommand -OwnedPresence }
            Assert-Control ((Get-HermesCrashEventCommand) -ceq 'startup-crash-event-probe')
        }
        [Environment]::SetEnvironmentVariable('GITHUB_EVENT_NAME', 'workflow_dispatch')
        foreach ($name in @('', 'control', 'Installer')) {
            [Environment]::SetEnvironmentVariable('GITHUB_JOB', $name)
            Assert-Refused { Get-HermesCrashEventCommand -OwnedPresence }
        }
        $command = Get-Command Get-HermesCrashEventCommand
        Assert-Control ($command.Parameters.ContainsKey('OwnedPresence') -and $command.Parameters.Count -eq 1)
    } finally {
        [Environment]::SetEnvironmentVariable('GITHUB_EVENT_NAME', $eventName)
        [Environment]::SetEnvironmentVariable('GITHUB_JOB', $job)
    }
}

function Test-EventCleanupFaults {
    $calls = [Collections.Generic.List[int]]::new()
    $capture = New-Capture
    $process = [pscustomobject]@{ Calls = $calls }
    $process | Add-Member ScriptMethod Dispose { $this.Calls.Add(1); throw 'private/process' }
    $pins = @()
    foreach ($index in 2..3) {
        $stream = [pscustomobject]@{ Calls = $calls; Id = $index }
        $stream | Add-Member ScriptMethod Dispose { $this.Calls.Add($this.Id); throw 'private/pin' }
        $pins += [pscustomobject]@{ Stream = $stream }
    }
    $scope = [pscustomobject]@{ Calls = $calls }
    $scope | Add-Member ScriptMethod CompleteAfterChildrenExited { $this.Calls.Add(99) }
    $scope | Add-Member ScriptMethod Dispose { $this.Calls.Add(4); throw 'private/scope' }
    $original = [ArithmeticException]::new('private/initiating')
    $failure = $null
    try { Close-HermesCrashEventResources $capture $process @($pins) $scope $original }
    catch { $failure = $_.Exception.GetBaseException() }
    Assert-Control ($failure -is [AggregateException])
    if ($failure -is [AggregateException]) {
        Assert-Control ([object]::ReferenceEquals($failure.InnerExceptions[0], $original))
        Assert-Control ($failure.Flatten().InnerExceptions.Count -eq 5)
        Assert-Control (@($failure.Flatten().InnerExceptions | Where-Object {
            [object]::ReferenceEquals($_, $original)
        }).Count -eq 1)
    }
    Assert-Control (($calls -join ',') -ceq '1,2,3,4')
    Assert-Control (@($capture.Stdout | Where-Object { $_ -ne 0 }).Count -eq 0)
    Assert-Control (@($capture.Stderr | Where-Object { $_ -ne 0 }).Count -eq 0)

    $calls.Clear()
    $scope | Add-Member ScriptMethod Dispose { $this.Calls.Add(4) } -Force
    Assert-Control (@(Close-HermesCrashEventResources $null $null @() $scope $null).Count -eq 0)
    Assert-Control (($calls -join ',') -ceq '4')
}

class CrashEventCleanupFaultWriter : IO.StringWriter {
    [bool]$FailWrite
    [Exception]$Failure
    [Collections.Generic.List[int]]$Calls

    [void] WriteLine([string]$Value) {
        $this.Calls.Add(5)
        if ($this.FailWrite) { throw $this.Failure }
        $null = $this.GetStringBuilder().Append($Value).Append($this.NewLine)
    }

    [void] Flush() {
        $this.Calls.Add(6)
        throw $this.Failure
    }
}

function Test-EventCleanupOutputFaults {
    foreach ($mode in @('write', 'cleanup-flush', 'abandon-flush')) {
        # Calls 1..4 are releases, 5 is WriteLine, 6 is Flush.
        # CompleteAfterChildrenExited would add the forbidden call 99.
        $calls = [Collections.Generic.List[int]]::new()
        $resourceFaults = $mode -cne 'abandon-flush'
        $resources = @(foreach ($index in 1..4) {
            $resource = [pscustomobject]@{
                Calls = $calls; Id = $index; Failure = $null
            }
            if ($resourceFaults) {
                $resource.Failure = [ArithmeticException]::new("private/resource/$index")
            }
            $resource | Add-Member ScriptMethod Dispose {
                $this.Calls.Add($this.Id)
                if ($null -ne $this.Failure) { throw $this.Failure }
            }
            $resource
        })
        $process = $resources[0]
        $pins = @(
            [pscustomobject]@{ Stream = $resources[1] }
            [pscustomobject]@{ Stream = $resources[2] }
        )
        $scope = $resources[3]
        $scope | Add-Member ScriptMethod CompleteAfterChildrenExited { $this.Calls.Add(99) }
        $capture = New-Capture
        $capture.Stderr = [byte[]]@(120, 121)
        $original = [ArithmeticException]::new('private/initiating')
        $outputFailure = [IO.IOException]::new('private/output')
        $writer = [CrashEventCleanupFaultWriter]::new()
        $writer.Calls = $calls
        $writer.FailWrite = $mode -ceq 'write'
        $writer.Failure = $outputFailure
        $failure = $null
        try {
            $output = [Console]::Out
            try {
                [Console]::SetOut($writer)
                try { Close-HermesCrashEventResources $capture $process @($pins) $scope $original }
                catch { $failure = $_.Exception.GetBaseException() }
            } finally { [Console]::SetOut($output) }

            # Assert only after restoring the real sink, including on failure.
            Assert-Control ($failure -is [AggregateException])
            if ($failure -is [AggregateException]) {
                Assert-Control ([object]::ReferenceEquals($failure.InnerExceptions[0], $original))
                $leaves = @($failure.Flatten().InnerExceptions | ForEach-Object {
                    $_.GetBaseException()
                })
                $expected = @($original, $outputFailure)
                if ($resourceFaults) {
                    $expected += @($resources | ForEach-Object { $_.Failure })
                }
                Assert-Control ($leaves.Count -eq $expected.Count)
                foreach ($error in $expected) {
                    Assert-Control (@($leaves | Where-Object {
                        [object]::ReferenceEquals($_, $error)
                    }).Count -eq 1)
                }
            }
            $expectedCalls = switch ($mode) {
                'write' { '1,5,2,3,4' }
                'cleanup-flush' { '1,5,6,2,3,4' }
                'abandon-flush' { '1,2,3,6,4' }
            }
            Assert-Control (($calls -join ',') -ceq $expectedCalls)
            Assert-Control (@($capture.Stdout | Where-Object { $_ -ne 0 }).Count -eq 0)
            Assert-Control (@($capture.Stderr | Where-Object { $_ -ne 0 }).Count -eq 0)
            $expectedText = if ($mode -ceq 'cleanup-flush') {
                'HERMES_CRASH_EVENT_CLEANUP v=1 resource=2 failed=1 originalGatePassed=0 nativeAcceptance=0' +
                    [Environment]::NewLine
            } else { '' }
            Assert-Control ($writer.ToString() -ceq $expectedText)
        } finally { $writer.Dispose() }
    }
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
    Assert-Control ($e.Count -eq 0 -and $ast.ParamBlock.Parameters.Count -eq 1)
    Assert-Control ($ast.ParamBlock.Parameters[0].Name.VariablePath.UserPath -ceq 'OwnedPresence' `
        -and $ast.ParamBlock.Parameters[0].StaticType -eq [Management.Automation.SwitchParameter])
    $failureFunction = $ast.Find({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Write-CrashEventFailure'
    }, $false)
    Assert-Control ($failureFunction.Extent.Text.Contains('ConvertTo-HermesCrashEventCaptureDiagnostic $script:capture') `
        -and [regex]::Matches($wrapper, 'ConvertTo-HermesCrashEventCaptureDiagnostic').Count -eq 1)
    Assert-Control ($failureFunction.Extent.Text.Contains('ConvertTo-HermesCrashEventTiming $script:capture') `
        -and [regex]::Matches($wrapper, 'ConvertTo-HermesCrashEventTiming').Count -eq 1 `
        -and $failureFunction.Extent.Text.Contains('$script:phase -eq 9') `
        -and $failureFunction.Extent.Text.Contains('if ($script:failureReported) { return }'))
    foreach ($path in @('Crash-Event-Control.psm1', 'Test-CrashEventControl.ps1')) {
        $null = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $path), [ref]$t, [ref]$e)
        Assert-Control ($e.Count -eq 0)
    }
    Assert-Control ($wrapper.Contains('ArgumentList.Add($command)') `
        -and [regex]::Matches($wrapper, 'ArgumentList\.Add\(').Count -eq 1)
    Assert-Control ($wrapper.Contains('$command = Get-HermesCrashEventCommand -OwnedPresence:$OwnedPresence') `
        -and $wrapper.Contains('$script:ownedPresence = $OwnedPresence.IsPresent'))
    Assert-Control ($module.Contains("return 'startup-crash-event-probe'") `
        -and $module.Contains("return 'startup-crash-event-presence'") `
        -and $module.Contains("`$env:GITHUB_EVENT_NAME -cne 'workflow_dispatch'") `
        -and $module.Contains("`$env:GITHUB_JOB -cne 'installer'"))
    Assert-Control ($wrapper.Contains('Assert-HermesCrashEventPresence $script:capture') `
        -and [regex]::Matches($wrapper, 'Assert-HermesCrashEventAbsent \$root').Count -eq 4)
    Assert-Control ($wrapper.Contains('ConvertTo-HermesCrashEventFacts $script:capture -OwnedPresence:$script:ownedPresence') `
        -and $wrapper.Contains('ConvertTo-HermesCrashEventTiming $script:capture -OwnedPresence:$script:ownedPresence'))
    $presenceBranches = $ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.IfStatementAst] `
            -and $node.Clauses[0].Item1.Extent.Text -cmatch '\A\$OwnedPresence\z'
    }, $true)
    Assert-Control ($presenceBranches.Count -eq 2)
    foreach ($branch in $presenceBranches) {
        $body = $branch.Clauses[0].Item2.Extent.Text
        Assert-Control ($body.Contains('Assert-HermesCrashEventAbsent $root') `
            -and -not $body.Contains('AppendAllText') -and -not $body.Contains('Complete-HermesCrashEventScope') `
            -and -not $body.Contains('Open-HermesCrashPins'))
    }
    Assert-Control (@($presenceBranches | Where-Object {
        $_.Clauses[0].Item2.Extent.Text.Contains("throw '16'")
    }).Count -eq 1)
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
        $wrapper.IndexOf('Close-HermesCrashEventResources $script:capture', [StringComparison]::Ordinal))
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
    Assert-Control ($module.Contains('New-HermesCrashRead $Stream 1024 $Operations') `
        -and $module.Contains('New-HermesCrashRead $Stream 1 $Operations') `
        -and $module.Contains('Update-HermesCrashRead $Read') `
        -and $module.Contains('[Array]::Clear($output.Buffer)') -and $module.Contains('[Array]::Clear($errors.Buffer)'))
    $baseModule = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Crash-Control.psm1'))
    Assert-Control ($baseModule.Contains('New-HermesCrashRead $Process.StandardOutput.BaseStream 512 $Operations') `
        -and $baseModule.Contains('$Capture.Stdout.Length -gt 511') -and -not $baseModule.Contains('1024'))
    Assert-Control ($module.Contains('$Capture.Stdout.Length -gt 1023') `
        -and $module.Contains('$Capture.Tasks.Count -gt 1026') -and $module.Contains('$Operations.Count -gt 1026') `
        -and $module.Contains('[Math]::Min(1024, $Capture.Stdout.Length)') `
        -and $module.Contains('[Math]::Min(1027, $Capture.Tasks.Count)'))
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
    Assert-Control ($workflow -cmatch '(?s)id: control\s+shell: pwsh\s+(?:#[^\n]*\n\s*)*timeout-minutes: 2\s+run: \|')
    Assert-Control ($workflow -cmatch '(?m)^  workflow_dispatch:\s*\r?$' `
        -and $workflow -cnotmatch '(?m)^\s+inputs:')
    Assert-Control ($workflow.Contains("if (`$env:GITHUB_EVENT_NAME -ceq 'workflow_dispatch') {") `
        -and $workflow.Contains("./installer/windows/Invoke-CrashEventControl.ps1 -OwnedPresence") `
        -and [regex]::Matches($workflow, 'Invoke-CrashEventControl\.ps1 -OwnedPresence').Count -eq 1)
    Assert-Control ($workflow -cmatch '(?s)if \(\$env:GITHUB_EVENT_NAME -ceq ''workflow_dispatch''\) \{\s+./installer/windows/Invoke-CrashEventControl.ps1 -OwnedPresence\s+\} else \{\s+./installer/windows/Invoke-CrashEventControl.ps1\s+\}')
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
    Assert-Control ($wrapper.Contains('$failure = $_.Exception') `
        -and $wrapper.Contains(') $scope $failure') `
        -and $module.Contains("$" + 'errors.Add($Failure)') `
        -and $module.Contains('Add-EventCleanupFailure $errors $_.Exception 3'))
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
    Test-OwnedPresence
    Test-OwnedPresenceSelection
    Test-EventCleanupFaults
    Test-EventCleanupOutputFaults
    Test-FailedCommandProtocols
    Test-Timing
    Test-ReadBudget
    Test-CaptureDiagnostics
    Test-Publication $scratch
    Test-Lifecycle $scratch
    Test-DiagnosticsAndGuard
    Test-WerPolicySnapshot
    Test-WerPolicyBoundaries
    foreach ($name in @('task-contract', 'task-contract-load', 'valid', 'at-cap', 'base-only', 'event-only', 'noise', 'crlf', 'utf8', 'stderr',
            'exit', 'overflow', 'timeout', 'failure', 'failed-guard', 'failed-callback',
            'failed-fence', 'failed-dispose', 'failed-base-success', 'wrapper-failure',
            'timing', 'timing-worst', 'timing-overflow', 'timing-native', 'timing-order', 'timing-duplicate',
            'timing-success', 'timing-stderr', 'timing-utf8', 'wrapper-timing', 'wrapper-timing-invalid',
            'wrapper-timing-phase', 'wrapper-timing-success')) { Invoke-Fixture $name }
    Test-StaticBoundaries
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
[Console]::Out.WriteLine("HERMES_CRASH_EVENT_TESTS v=1 passed=$script:passed failed=$script:failed")
if ($script:failed -ne 0) { exit 1 }
exit 0
