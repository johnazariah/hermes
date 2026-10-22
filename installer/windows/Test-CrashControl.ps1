#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('controls', 'fixture')][string]$Mode = 'controls',
    [ValidateSet('valid', 'noise', 'contradiction', 'crlf', 'utf8', 'stderr', 'exit', 'overflow', 'timeout',
        'failed', 'maximum', 'wrapper-report', 'wrapper-early', 'wrapper-late', 'wrapper-missing', 'wrapper-malformed')]
    [string]$Fixture = 'valid'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Mode -ceq 'fixture') {
    $row = "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734499 error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n"
    if ($Fixture.StartsWith('wrapper-', [StringComparison]::Ordinal)) {
        Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -Force -DisableNameChecking
        $tokens = $null
        $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot 'Invoke-CrashControl.ps1'), [ref]$tokens, [ref]$errors)
        if ($errors.Count -ne 0) { exit 2 }
        $definition = $ast.Find({
            param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Write-CrashFailure'
        }, $false)
        . ([scriptblock]::Create($definition.Extent.Text))
        $script:failureReported = $false
        $script:phase = switch ($Fixture) { 'wrapper-early' { 8 } 'wrapper-late' { 10 } default { 9 } }
        $complete = [Threading.Tasks.Task]::CompletedTask
        $script:capture = [pscustomobject]@{
            ExitCode = 1; Deadline = 0; StdoutComplete = 1; StderrComplete = 1; WaitSettled = 1
            Stdout = [Text.Encoding]::UTF8.GetBytes(
                "HERMES_CRASH_CONTROL v=1 status=1 exit=4294967295 error=11 cleanupError=255 reportError=1 nativeAcceptance=0`n")
            Stderr = [byte[]]::new(0); Tasks = @($complete, $complete, $complete)
        }
        if ($Fixture -ceq 'wrapper-missing') { $script:capture = $null }
        if ($Fixture -ceq 'wrapper-malformed') {
            $script:capture.Stdout = [Text.Encoding]::UTF8.GetBytes("private/path`n$('a' * 64)`n$([char]27)[31m")
        }
        try { throw '12' }
        catch { Write-CrashFailure $_; Write-CrashFailure $_ }
        finally {
            [Console]::Out.WriteLine('HERMES_CRASH_FIXTURE_DISPOSE v=1')
            [Console]::Out.Flush()
        }
        exit 1
    }
    if ($Fixture -ceq 'failed') {
        $row = "HERMES_CRASH_CONTROL v=1 status=1 exit=0 error=11 cleanupError=255 reportError=1 nativeAcceptance=0`n"
    }
    if ($Fixture -ceq 'maximum') { $row = $row.Replace('exit=2148734499', 'exit=4294967295') }
    if ($Fixture -ceq 'timeout') { [Threading.Thread]::Sleep(30000); exit 1 }
    if ($Fixture -ceq 'noise') { $row = "noise`n$row" }
    if ($Fixture -ceq 'contradiction') { $row = $row.Replace('exit=2148734499', 'exit=3221226505') }
    if ($Fixture -ceq 'crlf') { $row = $row.Replace("`n", "`r`n") }
    if ($Fixture -ceq 'overflow') { $row = 'x' * 4096 }
    $bytes = [Text.Encoding]::UTF8.GetBytes($row)
    if ($Fixture -ceq 'utf8') { $bytes = [byte[]]@(0xff, 0xfe) }
    $output = [Console]::OpenStandardOutput()
    $output.Write($bytes)
    $output.Flush()
    if ($Fixture -ceq 'stderr') {
        $errorOutput = [Console]::OpenStandardError()
        $errorOutput.WriteByte(120)
        $errorOutput.Flush()
    }
    if ($Fixture -in @('exit', 'failed')) { exit 1 }
    exit 0
}

Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -Force -DisableNameChecking
$script:passed = 0
$script:failed = 0
$script:case = 0
function Assert-Control {
    param([bool]$Condition)
    $script:case++
    if ($Condition) { $script:passed++ }
    else { $script:failed++; [Console]::Out.WriteLine("HERMES_CRASH_TEST_FAILURE case=$script:case") }
}
function Assert-Refused {
    param([scriptblock]$Action)
    $refused = $false
    try { $null = & $Action } catch { $refused = $true }
    Assert-Control $refused
}
function ConvertTo-ControlBytes {
    param([object]$Value)
    return ,[Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Compress -Depth 8))
}
function Assert-Diagnostic {
    param([object]$Phase, [object]$Failure, [int]$ExpectedPhase, [int]$Family = 0,
        [int]$Reason = 0, [uint32]$NativeError = 0)
    $line = ConvertTo-HermesCrashDiagnostic -Phase $Phase -Failure $Failure
    Assert-Control ($line -ceq (
        "HERMES_CRASH_DIAGNOSTIC v=1 phase=$ExpectedPhase family=$Family reason=$Reason nativeError=$NativeError"))
}

function Test-CrashDiagnostics {
    $privateText = "private/path`nHERMES_CRASH_DIAGNOSTIC v=1 phase=99 family=99`n$(
        'a' * 64)`n$([char]27)[31m"
    $unknown = [Exception]::new($privateText)
    foreach ($phase in 0..12) { Assert-Diagnostic $phase $unknown $phase }
    foreach ($phase in @(-1, 13, [int]::MaxValue, 1L, 1.0, $true, '1', $privateText, $null)) {
        Assert-Diagnostic $phase $unknown 0
    }
    foreach ($reason in 1..20) {
        $failure = [Management.Automation.RuntimeException]::new([string]$reason)
        Assert-Diagnostic 5 $failure 5 1 $reason
    }
    foreach ($text in @($privateText, '0', '21', '01', '+1', ' 1', '1 ', "1`n", "1`r`n", '1.0', '1e0')) {
        Assert-Diagnostic 5 ([Management.Automation.RuntimeException]::new($text)) 5
    }
    Assert-Diagnostic 4 $null 4
    Assert-Diagnostic 4 ([pscustomobject]@{ Exception = [ComponentModel.Win32Exception]::new(5, $privateText) }) 4
    foreach ($code in @(0, 5, 87, [int]::MaxValue, [int]::MinValue, -1)) {
        $expected = [uint32]([long]$code -band 0xffffffffL)
        Assert-Diagnostic 6 ([ComponentModel.Win32Exception]::new($code, $privateText)) 6 2 0 $expected
    }
    $cases = @(
        @{ Error = [IO.IOException]::new($privateText); Family = 3 }
        @{ Error = [UnauthorizedAccessException]::new($privateText); Family = 4 }
        @{ Error = [IO.InvalidDataException]::new($privateText); Family = 5 }
        @{ Error = [InvalidOperationException]::new($privateText); Family = 6 }
        @{ Error = [ArgumentException]::new($privateText); Family = 7 }
        @{ Error = [ArgumentNullException]::new('synthetic', $privateText); Family = 8 }
        @{ Error = [ArgumentOutOfRangeException]::new('synthetic', $privateText); Family = 9 }
        @{ Error = [NotSupportedException]::new($privateText); Family = 10 }
        @{ Error = [PlatformNotSupportedException]::new($privateText); Family = 11 }
        @{ Error = [TimeoutException]::new($privateText); Family = 12 }
        @{ Error = [OperationCanceledException]::new($privateText); Family = 13 }
        @{ Error = [Threading.Tasks.TaskCanceledException]::new($privateText); Family = 14 }
        @{ Error = [Text.Json.JsonException]::new($privateText); Family = 15 }
        @{ Error = [IO.FileNotFoundException]::new($privateText, 'private-file'); Family = 16 }
        @{ Error = [IO.FileLoadException]::new($privateText, 'private-file'); Family = 17 }
        @{ Error = [BadImageFormatException]::new($privateText); Family = 18 }
        @{ Error = [TypeLoadException]::new($privateText); Family = 19 }
        @{ Error = [MissingMethodException]::new($privateText); Family = 20 }
        @{ Error = [Reflection.AmbiguousMatchException]::new($privateText); Family = 21 }
        @{ Error = [Reflection.ReflectionTypeLoadException]::new(
            [Type[]]::new(0), [Exception[]]::new(0), $privateText); Family = 22 }
        @{ Error = [ObjectDisposedException]::new('private-object', $privateText); Family = 23 }
        @{ Error = [Security.Cryptography.CryptographicException]::new($privateText); Family = 24 }
        @{ Error = [Text.DecoderFallbackException]::new($privateText); Family = 25 }
        @{ Error = [FormatException]::new($privateText); Family = 26 }
        @{ Error = [OverflowException]::new($privateText); Family = 27 }
        @{ Error = [Management.Automation.ParameterBindingException]::new($privateText); Family = 28 }
        @{ Error = [Management.Automation.MethodException]::new($privateText); Family = 29 }
        @{ Error = [AggregateException]::new($privateText); Family = 30 }
    )
    foreach ($case in $cases) { Assert-Diagnostic 9 $case.Error 9 $case.Family }
    Assert-Diagnostic 9 ([IO.IOException]::new('18')) 9 3
    $win32 = [ComponentModel.Win32Exception]::new(5, $privateText)
    Assert-Diagnostic 6 ([Exception]::new($privateText, $win32)) 6
    $nested = $win32
    foreach ($depth in 1..9) {
        $nested = [Reflection.TargetInvocationException]::new($privateText, $nested)
        if ($depth -le 8) { Assert-Diagnostic 6 $nested 6 2 0 5 }
        else { Assert-Diagnostic 6 $nested 6 }
    }
    $record = [Management.Automation.ErrorRecord]::new(
        [Management.Automation.RuntimeException]::new($privateText, $nested.InnerException),
        $privateText, [Management.Automation.ErrorCategory]::NotSpecified, $privateText)
    $record.ErrorDetails = [Management.Automation.ErrorDetails]::new($privateText)
    # Nine wrappers deliberately exceed the bounded unwrapping depth.
    Assert-Diagnostic 6 $record 6
    $record = [Management.Automation.ErrorRecord]::new(
        [Reflection.TargetInvocationException]::new($privateText, $win32),
        $privateText, [Management.Automation.ErrorCategory]::NotSpecified, $privateText)
    Assert-Diagnostic 6 $record 6 2 0 5
    try {
        $method = [string].GetMethod('Substring', [Type[]]@([int]))
        $null = $method.Invoke('synthetic', [object[]]@([int]9999))
        Assert-Control $false
    } catch { Assert-Diagnostic 6 $_ 6 9 }
    try { Assert-HermesCrashHash 'synthetic-invalid'; Assert-Control $false }
    catch { Assert-Diagnostic 2 $_ 2 1 2 }
}

function Test-CrashGuardDiagnostic {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [Environment]::ProcessPath
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
            (Join-Path $PSScriptRoot 'Invoke-CrashControl.ps1'))) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    try {
        if (-not $process.Start()) { throw '1' }
        $capture = Invoke-HermesCrashCapture $process -DeadlineMilliseconds 20000 -Operations $operations
        $expected = "HERMES_CRASH_WRAPPER v=1 status=1 published=0 nativeAcceptance=0`n" +
            "HERMES_CRASH_DIAGNOSTIC v=1 phase=1 family=1 reason=1 nativeError=0`n"
        Assert-Control ($capture.ExitCode -eq 1 -and $capture.Stderr.Length -eq 0)
        Assert-Control ([Text.UTF8Encoding]::new($false, $true).GetString($capture.Stdout) -ceq $expected)
    } finally {
        if (-not $process.HasExited) { $process.Kill() }
        if (-not $process.WaitForExit(5000)) { throw '2' }
        $process.Dispose()
        $joined = [Threading.Tasks.Task]::WhenAll($operations.ToArray())
        try { $null = $joined.Wait(5000) } catch { }
        foreach ($operation in $operations) {
            if (-not $operation.IsCompleted) { throw '2' }
            try { $null = $operation.GetAwaiter().GetResult() } catch { }
        }
        Assert-Control ($operations.Count -ge 3 -and $joined.IsCompleted)
    }
}

function Invoke-ControlFixture {
    param([string]$Name, [bool]$Accepted, [switch]$PassThru)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = [Environment]::ProcessPath
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
            $PSCommandPath, '-Mode', 'fixture', '-Fixture', $Name)) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $acceptedActual = $false
    $capture = $null
    $operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    try {
        if (-not $process.Start()) { throw '1' }
        $limit = if ($Name -ceq 'timeout') { 200 } else { 20000 }
        $capture = Invoke-HermesCrashCapture $process -DeadlineMilliseconds $limit -Operations $operations
        Assert-HermesCrashOutcome $capture $script:validReport
        Assert-Control (@($capture.Tasks | Where-Object {
            $_.Status -ne [Threading.Tasks.TaskStatus]::RanToCompletion
        }).Count -eq 0)
        Assert-Control ($capture.Tasks.Count -eq $operations.Count -and @($capture.Tasks | Where-Object {
            -not $operations.Contains($_)
        }).Count -eq 0)
        $acceptedActual = $true
    } catch { $acceptedActual = $false }
    finally {
        if (-not $process.HasExited) { $process.Kill() }
        if (-not $process.WaitForExit(5000)) { throw '2' }
        $process.Dispose()
        # These are the actual reads/wait retained before capture threw, including
        # pending reads on timeout. Settlement here follows owned fixture teardown.
        $joined = [Threading.Tasks.Task]::WhenAll($operations.ToArray())
        try { $null = $joined.Wait(5000) } catch { }
        $observed = 0
        foreach ($operation in $operations) {
            if (-not $operation.IsCompleted) { throw '2' }
            try { $null = $operation.GetAwaiter().GetResult() } catch { }
            $observed++
        }
        Assert-Control ($operations.Count -ge 3 -and $observed -eq $operations.Count)
        Assert-Control ($joined.IsCompleted)
    }
    Assert-Control ($acceptedActual -eq $Accepted)
    if ($PassThru) { return $capture }
}

function New-ControlFactsCapture {
    param([string]$Row, [object]$ExitCode = 0)
    $complete = [Threading.Tasks.Task]::CompletedTask
    return [pscustomobject]@{
        ExitCode = $ExitCode; Deadline = 0; StdoutComplete = 1; StderrComplete = 1; WaitSettled = 1
        Stdout = [Text.Encoding]::UTF8.GetBytes($Row)
        Stderr = [byte[]]::new(0); Tasks = @($complete, $complete, $complete)
    }
}

function Assert-ControlFactsUnavailable {
    param([object]$Capture)
    $lines = @(ConvertTo-HermesCrashControlFacts -Capture $Capture)
    Assert-Control ($lines.Count -eq 1 -and $lines[0] -is [string] -and $lines[0] -ceq (
        'HERMES_CRASH_CONTROL_FACTS v=1 available=0 status=0 exit=0 error=0 cleanupError=0 reportError=0 nativeAcceptance=0'))
}

function Test-CrashControlFacts {
    $row = "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734499 error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n"
    $privateText = "private/path`nHERMES_CRASH_CONTROL_FACTS v=1 available=1 status=1`n$(
        'a' * 64)`n$([char]27)[31m"
    $cases = @(
        foreach ($status in 0..1) {
            foreach ($code in @(0, 1, 37, 2148734214L, 2148734499L, 4294967295L)) {
                @{ Status = $status; Exit = $code; Error = 0; Cleanup = 0; Report = 0 }
            }
        }
        @{ Status = 1; Exit = 2148734499L; Error = 6; Cleanup = 0; Report = 0 }
        foreach ($fault in 0..11) { @{ Status = 1; Exit = 0; Error = $fault; Cleanup = 0; Report = 0 } }
        foreach ($flags in @(0, 1, 2, 4, 8, 16, 32, 64, 128, 254, 255)) {
            @{ Status = 1; Exit = 4294967295L; Error = 11; Cleanup = $flags; Report = 1 }
        }
    )
    foreach ($case in $cases) {
        $fields = "status=$($case.Status) exit=$($case.Exit) error=$($case.Error) cleanupError=$($case.Cleanup) reportError=$($case.Report) nativeAcceptance=0"
        $capture = New-ControlFactsCapture "HERMES_CRASH_CONTROL v=1 $fields`n" $case.Status
        $lines = @(ConvertTo-HermesCrashControlFacts $capture)
        Assert-Control ($lines.Count -eq 1 -and $lines[0] -is [string] `
            -and $lines[0] -ceq "HERMES_CRASH_CONTROL_FACTS v=1 available=1 $fields")
    }
    foreach ($text in @(
        '', $privateText, $row.TrimEnd([char]10), $row.Replace("`n", "`r`n"), ($row * 2),
        ("noise`n" + $row), ($row + "noise`n"), ($row + "`n"), ("`n" + $row),
        ([string][char]0xfeff + $row), ($row + [char]0), $row.Replace('CONTROL', 'control'),
        $row.Replace('v=1', 'v=0'), $row.Replace('v=1', 'v=2'),
        $row.Replace('status=0', 'status=0 status=0'), $row.Replace('status=0', 'status=2'),
        $row.Replace('error=0', 'error=12'), $row.Replace('cleanupError=0', 'cleanupError=256'),
        $row.Replace('reportError=0', 'reportError=2'), $row.Replace('nativeAcceptance=0', 'nativeAcceptance=1'),
        $row.Replace('exit=2148734499', 'exit=4294967296'), ('x' * 511), ('x' * 512), ('x' * 4096)
    )) { Assert-ControlFactsUnavailable (New-ControlFactsCapture $text) }
    foreach ($field in @('status=0', 'exit=2148734499', 'error=0', 'cleanupError=0', 'reportError=0', 'nativeAcceptance=0')) {
        $name = $field.Split('=')[0]
        foreach ($value in @('00', '01', '+1', '-1', '-0', ' 0', '0 ', 'true', 'false', '1.0', '1e0',
                '4294967296', ('9' * 400), '', 'x', ([string][char]0x0660), $privateText)) {
            Assert-ControlFactsUnavailable (New-ControlFactsCapture ($row.Replace($field, "$name=$value")))
        }
    }
    foreach ($bytes in @([byte[]]@(255), [byte[]]@(254), [byte[]]@(0xc3), [byte[]]@(0xc0, 0xaf))) {
        $capture = New-ControlFactsCapture $row
        $capture.Stdout = $bytes
        Assert-ControlFactsUnavailable $capture
    }
    foreach ($value in @($null, $privateText, $true, @{}, [pscustomobject]@{})) {
        Assert-ControlFactsUnavailable $value
    }
    $capture = New-ControlFactsCapture $row
    foreach ($name in @($capture.PSObject.Properties.Name)) {
        $missing = New-ControlFactsCapture $row
        $missing.PSObject.Properties.Remove($name)
        Assert-ControlFactsUnavailable $missing
    }
    foreach ($name in @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled')) {
        foreach ($value in @($null, $true, $false, '0', '1', 0L, 1L, 0.0, -1, 2, $privateText)) {
            $capture = New-ControlFactsCapture $row
            $capture.$name = $value
            Assert-ControlFactsUnavailable $capture
        }
        $capture = New-ControlFactsCapture $row
        $capture.$name = 1 - $capture.$name
        Assert-ControlFactsUnavailable $capture
    }
    Assert-ControlFactsUnavailable (New-ControlFactsCapture ($row.Replace('status=0', 'status=1')))
    foreach ($name in @('Stdout', 'Stderr')) {
        foreach ($value in @($null, $privateText, [object[]]@(0), [byte[]]::new(0), [byte[]]@(120))) {
            if ($name -ceq 'Stderr' -and $value -is [byte[]] -and $value.Length -eq 0) { continue }
            $capture = New-ControlFactsCapture $row
            $capture.$name = $value
            Assert-ControlFactsUnavailable $capture
        }
    }
    $complete = [Threading.Tasks.Task]::CompletedTask
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $faulted = [Threading.Tasks.Task]::FromException([InvalidOperationException]::new($privateText))
    $cancelled = [Threading.Tasks.Task]::FromCanceled([Threading.CancellationToken]::new($true))
    foreach ($tasks in @(
        $null, @(), @($complete), @($complete, $complete), $complete, $privateText,
        @($complete, $complete, $null), @($complete, $complete, $pending.Task),
        @($complete, $complete, $faulted), @($complete, $complete, $cancelled),
        @($complete, $complete, [pscustomobject]@{ Status = [Threading.Tasks.TaskStatus]::RanToCompletion }),
        (@($complete) * 515)
    )) {
        $capture = New-ControlFactsCapture $row
        $capture.Tasks = $tasks
        Assert-ControlFactsUnavailable $capture
    }
    $pending.SetResult(0)
    try { $null = $faulted.GetAwaiter().GetResult() } catch { }
    $culture = [Globalization.CultureInfo]::CurrentCulture
    try {
        [Globalization.CultureInfo]::CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('ar-SA')
        Assert-Control ((ConvertTo-HermesCrashControlFacts (New-ControlFactsCapture $row)) -ceq (
            'HERMES_CRASH_CONTROL_FACTS v=1 available=1 status=0 exit=2148734499 error=0 cleanupError=0 reportError=0 nativeAcceptance=0'))
    } finally { [Globalization.CultureInfo]::CurrentCulture = $culture }
    foreach ($name in @('valid', 'failed', 'maximum', 'noise', 'crlf', 'utf8', 'exit')) {
        $capture = Invoke-ControlFixture $name ($name -ceq 'valid') -PassThru
        if ($name -in @('valid', 'failed', 'maximum')) {
            $expected = switch ($name) {
                'valid' { 'status=0 exit=2148734499 error=0 cleanupError=0 reportError=0' }
                'failed' { 'status=1 exit=0 error=11 cleanupError=255 reportError=1' }
                'maximum' { 'status=0 exit=4294967295 error=0 cleanupError=0 reportError=0' }
            }
            Assert-Control ((ConvertTo-HermesCrashControlFacts $capture) -ceq (
                "HERMES_CRASH_CONTROL_FACTS v=1 available=1 $expected nativeAcceptance=0"))
            Assert-Control ($capture.Tasks.Count -ge 3 -and @($capture.Tasks | Where-Object {
                $_ -isnot [Threading.Tasks.Task] -or $_.Status -ne [Threading.Tasks.TaskStatus]::RanToCompletion
            }).Count -eq 0)
        } else { Assert-ControlFactsUnavailable $capture }
    }
    foreach ($name in @('wrapper-report', 'wrapper-early', 'wrapper-late', 'wrapper-missing', 'wrapper-malformed')) {
        $capture = Invoke-ControlFixture $name $false -PassThru
        $phase = switch ($name) { 'wrapper-early' { 8 } 'wrapper-late' { 10 } default { 9 } }
        # Wrapper Console.WriteLine uses platform newlines; the helper protocol remains exact LF.
        $newline = [Environment]::NewLine
        $expected = "HERMES_CRASH_WRAPPER v=1 status=1 published=0 nativeAcceptance=0$newline" +
            "HERMES_CRASH_DIAGNOSTIC v=1 phase=$phase family=1 reason=12 nativeError=0$newline"
        if ($name -ceq 'wrapper-report') {
            $expected += "HERMES_CRASH_CONTROL_FACTS v=1 available=1 status=1 exit=4294967295 error=11 cleanupError=255 reportError=1 nativeAcceptance=0$newline"
        } elseif ($name -ceq 'wrapper-malformed') {
            $expected += "HERMES_CRASH_CONTROL_FACTS v=1 available=0 status=0 exit=0 error=0 cleanupError=0 reportError=0 nativeAcceptance=0$newline"
        }
        $expected += "HERMES_CRASH_FIXTURE_DISPOSE v=1$newline"
        Assert-Control ($capture.ExitCode -eq 1 -and $capture.Stderr.Length -eq 0 `
            -and [Text.UTF8Encoding]::new($false, $true).GetString($capture.Stdout) -ceq $expected)
    }
}

function Invoke-CheckoutFixtureGit {
    param([string[]]$Arguments)
    $result = & git --no-optional-locks -C $script:checkoutFixture `
        -c "core.hooksPath=$script:checkoutHooks" -c commit.gpgSign=false -c tag.gpgSign=false `
        -c 'user.name=Hermes Synthetic Fixture' -c user.email=fixture@example.invalid `
        -c credential.helper= -c core.autocrlf=false -c core.filemode=false `
        -c core.fsmonitor=false -c core.untrackedCache=false @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw '20' }
    return ($result -join "`n")
}

function Assert-CheckoutFixtureSnapshot {
    param([string]$Revision, [string]$Tree, [string[]]$ExpectedSources, [string]$Reference)
    $snapshot = Get-HermesCrashCheckout $script:checkoutFixture $Revision
    Assert-Control ($snapshot.Revision -ceq $Revision -and $snapshot.Tree -ceq $Tree)
    Assert-Control ($snapshot.Sources.Count -eq $ExpectedSources.Count + 2 + [int]($Reference.Length -ne 0))
    foreach ($path in $ExpectedSources + @('.git/HEAD', '.git/index')) {
        $entry = @($snapshot.Sources | Where-Object { $_.path -ceq $path })
        $expected = Get-HermesCrashFileEntry (Join-Path $script:checkoutFixture $path) $path
        Assert-Control ($entry.Count -eq 1 -and $entry[0].sha256 -ceq $expected.sha256 `
            -and $entry[0].length -eq $expected.length)
    }
    if ($Reference.Length -ne 0) {
        Assert-Control (@($snapshot.Sources | Where-Object { $_.path -ceq $Reference }).Count -eq 1)
    }
    Assert-Control ($snapshot.SourceSha256 -ceq (Get-HermesCrashEntryDigest $snapshot.Sources))
    $pins = Open-HermesCrashPins $script:checkoutFixture $snapshot.Sources
    try {
        Assert-HermesCrashPins $pins
        $second = Get-HermesCrashCheckout $script:checkoutFixture $Revision
        Assert-Control ($second.SourceSha256 -ceq $snapshot.SourceSha256 -and $second.Tree -ceq $Tree)
        Assert-HermesCrashPins $pins
    } finally { Close-HermesCrashPins $pins }
    return $snapshot
}

function Test-CheckoutFixture {
    param([string]$Scratch)
    $script:checkoutFixture = Join-Path $Scratch 'checkout-fixture'
    $script:checkoutHooks = Join-Path $Scratch 'git-hooks-empty'
    $configuration = Join-Path $Scratch 'git-config-empty'
    New-HermesCrashDirectory $script:checkoutFixture
    New-HermesCrashDirectory $script:checkoutHooks
    [IO.File]::WriteAllText($configuration, '')
    # Isolate Git configuration only, not GITHUB/RUNNER admission variables.
    $saved = @{}
    foreach ($name in @([Environment]::GetEnvironmentVariables().Keys | Where-Object {
        $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)
    })) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        [Environment]::SetEnvironmentVariable($name, [NullString]::Value)
    }
    $isolated = @{
        GIT_CONFIG_NOSYSTEM = '1'; GIT_CONFIG_GLOBAL = $configuration; GIT_CONFIG_SYSTEM = $configuration
        GIT_TERMINAL_PROMPT = '0'; GIT_AUTHOR_DATE = '2000-01-01T00:00:00Z'
        GIT_COMMITTER_DATE = '2000-01-01T00:00:00Z'
    }
    try {
        foreach ($name in $isolated.Keys) { [Environment]::SetEnvironmentVariable($name, $isolated[$name]) }
        $null = Invoke-CheckoutFixtureGit @('init', '--quiet', '--object-format=sha1',
            '--initial-branch=crash-control-fixture', "--template=$script:checkoutHooks")
        $paths = @(
            'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props', 'global.json', 'NuGet.Config',
            '.config/dotnet-tools.json', '.github/workflows/startup-crash-control.yml',
            'installer/windows/Publish-CrashControl.ps1',
            'installer/windows/Hermes.Setup/Hermes.Setup.csproj', 'installer/windows/Hermes.Setup/Program.cs',
            'installer/windows/Hermes.Installer.Control/Hermes.Installer.Control.csproj',
            'installer/windows/Hermes.Installer.Control/ControlContract.cs',
            'installer/windows/Hermes.Installer.Guard/Hermes.Installer.Guard.csproj',
            'installer/windows/Hermes.Installer.Guard/Exports.cs',
            'tests/Hermes.Servicing.Acceptance/Hermes.Servicing.Acceptance.csproj',
            'tests/Hermes.Servicing.Acceptance/Program.cs',
            'tests/Hermes.Servicing.Acceptance.Model/Hermes.Servicing.Acceptance.Model.csproj',
            'tests/Hermes.Servicing.Acceptance.Model/nested/Model.cs',
            'tests/Hermes.Servicing.ProcessProbe/Hermes.Servicing.ProcessProbe.csproj',
            'tests/Hermes.Servicing.ProcessProbe/Program.cs',
            'src/Hermes.Servicing/Hermes.Servicing.csproj', 'src/Hermes.Servicing/Contracts.cs',
            'src/Hermes.Tray/InstalledProofContract.cs', 'src/Hermes.Tray/OwnerFixture.cs'
        )
        foreach ($path in $paths + @('unrelated.txt')) {
            $full = Join-Path $script:checkoutFixture $path
            $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))
            [IO.File]::WriteAllText($full, "synthetic fixture input`n")
        }
        $null = Invoke-CheckoutFixtureGit @('add', '--force', '--all')
        $null = Invoke-CheckoutFixtureGit @('commit', '--quiet', '--no-verify', '--no-gpg-sign', '-m', 'synthetic initial')
        $initial = Invoke-CheckoutFixtureGit @('rev-parse', 'HEAD')
        $tree = Invoke-CheckoutFixtureGit @('rev-parse', 'HEAD^{tree}')
        $loose = '.git/refs/heads/crash-control-fixture'
        $baseline = Assert-CheckoutFixtureSnapshot $initial $tree $paths $loose
        Assert-Control (@($baseline.Sources | Where-Object { $_.path -ceq 'unrelated.txt' }).Count -eq 0)
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture ('f' * 40) }
        Assert-Refused { Get-HermesCrashCheckout (Join-Path $script:checkoutFixture 'installer') $initial }
        $changed = $paths[15]
        $changedPath = Join-Path $script:checkoutFixture $changed
        [IO.File]::WriteAllText($changedPath, "dirty synthetic input`n")
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture $initial }
        $null = Invoke-CheckoutFixtureGit @('add', '--', $changed)
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture $initial }
        $null = Invoke-CheckoutFixtureGit @('restore', '--source=HEAD', '--staged', '--worktree', '--', $changed)
        $newInput = 'tests/Hermes.Servicing.Acceptance.Model/UntrackedFixture.cs'
        $newPath = Join-Path $script:checkoutFixture $newInput
        [IO.File]::WriteAllText($newPath, "untracked compiler input`n")
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture $initial }
        $null = [IO.Directory]::CreateDirectory((Join-Path $script:checkoutFixture '.git/info'))
        [IO.File]::WriteAllText((Join-Path $script:checkoutFixture '.git/info/exclude'), "$newInput`n")
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture $initial }
        $null = Invoke-CheckoutFixtureGit @('add', '--force', '--', $newInput)
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture $initial }
        $null = Invoke-CheckoutFixtureGit @('restore', '--source=HEAD', '--staged', '--', $newInput)
        [IO.File]::Delete($newPath)
        $rootInput = Join-Path $script:checkoutFixture 'Directory.Extra.targets'
        [IO.File]::WriteAllText($rootInput, 'untracked root compiler policy')
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture $initial }
        [IO.File]::Delete($rootInput)
        foreach ($folder in @('bin/Release', 'obj/Release')) {
            $output = Join-Path $script:checkoutFixture "tests/Hermes.Servicing.Acceptance/$folder"
            $null = [IO.Directory]::CreateDirectory($output)
            [IO.File]::WriteAllText((Join-Path $output 'synthetic.dll'), 'untracked build output')
        }
        $null = Assert-CheckoutFixtureSnapshot $initial $tree $paths $loose
        $null = Invoke-CheckoutFixtureGit @('checkout', '--quiet', '--detach', $initial)
        $detached = Assert-CheckoutFixtureSnapshot $initial $tree $paths ''
        Assert-Control ($detached.SourceSha256 -cne $baseline.SourceSha256)
        $null = Invoke-CheckoutFixtureGit @('checkout', '--quiet', 'crash-control-fixture')
        $null = Invoke-CheckoutFixtureGit @('pack-refs', '--all', '--prune')
        Assert-Control (-not [IO.File]::Exists((Join-Path $script:checkoutFixture $loose)))
        $null = Assert-CheckoutFixtureSnapshot $initial $tree $paths '.git/packed-refs'
        [IO.File]::WriteAllText($changedPath, "next committed synthetic input`n")
        $null = Invoke-CheckoutFixtureGit @('add', '--', $changed)
        $null = Invoke-CheckoutFixtureGit @('commit', '--quiet', '--no-verify', '--no-gpg-sign', '-m', 'synthetic next')
        $current = Invoke-CheckoutFixtureGit @('rev-parse', 'HEAD')
        $currentTree = Invoke-CheckoutFixtureGit @('rev-parse', 'HEAD^{tree}')
        Assert-Refused { Get-HermesCrashCheckout $script:checkoutFixture $initial }
        $next = Assert-CheckoutFixtureSnapshot $current $currentTree $paths $loose
        Assert-Control ($next.SourceSha256 -cne $baseline.SourceSha256 -and $next.Tree -cne $tree)
    } finally {
        foreach ($name in $isolated.Keys) { [Environment]::SetEnvironmentVariable($name, [NullString]::Value) }
        foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
        $restored = @([Environment]::GetEnvironmentVariables().Keys | Where-Object {
            $_.StartsWith('GIT_', [StringComparison]::OrdinalIgnoreCase)
        })
        Assert-Control ($restored.Count -eq $saved.Count -and @($restored | Where-Object {
            [Environment]::GetEnvironmentVariable($_) -cne $saved[$_]
        }).Count -eq 0)
    }
}

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$artifacts = Join-Path $root '.artifacts'
New-HermesCrashDirectory $artifacts
$scratch = Join-Path $artifacts ("crash-control-tests-" + [Guid]::NewGuid().ToString('N'))
New-HermesCrashDirectory $scratch
trap {
    [Console]::Out.WriteLine("HERMES_CRASH_TESTS v=1 passed=$script:passed failed=$($script:failed + 1)")
    if ([IO.Directory]::Exists($scratch)) { Remove-Item -LiteralPath $scratch -Recurse -Force }
    exit 1
}
try {
    Test-CrashDiagnostics
    $imageHash = 'a' * 64
    $sourceHash = 'b' * 64
    $shape = [ordered]@{
        schemaVersion = 1; originalGatePassed = 0; nativeAcceptance = 0; status = 0
        started = 1; ready = 1; identity = 1; armAttempted = 1; armed = 1; exited = 1
        held = 1; cleanup = 1; waitSettled = 1; drainsSettled = 1; writeSettled = 1
        callbackSettled = 1; stdoutComplete = 1; stderrComplete = 1
        outerSupervisionRequired = 1; synchronousCallsInterruptible = 0
        deadline = 0; cancelled = 0; exit = 2148734499L; error = 0; cleanupError = 0; reportError = 0
        imageSha256 = $imageHash; sourceSha256 = $sourceHash
    }
    $good = ConvertTo-ControlBytes $shape
    $script:validReport = Assert-HermesCrashReport $good $imageHash $sourceHash
    Assert-Control ($shape.Count -eq 28 -and $script:validReport.Exit -eq 2148734499L)
    Assert-Control ($script:validReport.Exit -eq 0x80131623L)
    Assert-Control ($script:validReport.Exit -is [uint32] `
        -and @($script:validReport.PSObject.Properties.Name).Count -eq 3)
    Assert-Control ($script:validReport.ImageSha256 -ceq $imageHash `
        -and $script:validReport.SourceSha256 -ceq $sourceHash)
    foreach ($name in @($shape.Keys | Where-Object { $_ -notin @('exit', 'imageSha256', 'sourceSha256') })) {
        $saved = $shape[$name]
        $shape[$name] = 1 - $saved
        Assert-Refused { Assert-HermesCrashReport (ConvertTo-ControlBytes $shape) $imageHash $sourceHash }
        $shape[$name] = $saved
    }
    foreach ($code in @(0x80131506L, 0x80131623L, 0xc0000409L, 0xc0000602L, 0xe0434352L)) {
        $shape.exit = $code
        $report = Assert-HermesCrashReport (ConvertTo-ControlBytes $shape) $imageHash $sourceHash
        Assert-Control ($report.Exit -eq $code)
        $boundCapture = New-ControlFactsCapture (
            "HERMES_CRASH_CONTROL v=1 status=0 exit=$code error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n")
        Assert-Control (@(Assert-HermesCrashOutcome $boundCapture $report).Count -eq 0)
        if ($code -ne 2148734499L) {
            Assert-Refused { Assert-HermesCrashOutcome $boundCapture $script:validReport }
        }
    }
    foreach ($code in @(0, 1, 37, 64, 65, 66, 67, 68, 259,
            0x80131622L, 0x80131624L, 0xc0000142L,
            4294967295L, 4294967296L, -1, 1.0, 2148734499.0,
            '2148734214', '2148734499', '0x80131623', $true, $null)) {
        $shape.exit = $code
        Assert-Refused { Assert-HermesCrashReport (ConvertTo-ControlBytes $shape) $imageHash $sourceHash }
    }
    $shape.exit = 2148734499L
    $json = [Text.Encoding]::UTF8.GetString($good)
    foreach ($bad in @(
        '', '{}', '[]', 'null', '{',
        $json.Replace('"status":0', '"status":0,"status":0'),
        $json.Replace('"status":0', '"status":0,"\u0073tatus":0'),
        $json.Replace('"status":0', '"Status":0'),
        $json.Replace('"status":0,', ''),
        $json.Replace('"status":0', '"status":0,"extra":0'),
        $json.Replace('"status":0', '"status":false'),
        $json.Replace('"status":0', '"status":"0"'),
        $json.Replace('"status":0', '"status":0.0'),
        $json.Replace('"status":0', '"status":0e0'),
        $json.Replace('"status":0', '"status":-0'),
        $json.Replace('"status":0', '"status":4294967296'),
        $json.Replace('"status":0', '"status":{}'),
        $json.Replace('"status":0', '"status":null'),
        $json.Replace($imageHash, ('A' * 64)),
        $json.Replace($imageHash, ('0' * 64)),
        $json.Replace($sourceHash, ('c' * 64)),
        ($json + 'x'), (' ' * 4097)
    )) {
        $badBytes = [Text.Encoding]::UTF8.GetBytes($bad)
        Assert-Refused { Assert-HermesCrashReport $badBytes $imageHash $sourceHash }
    }
    Assert-Refused { Assert-HermesCrashReport ([byte[]]@(255)) $imageHash $sourceHash }
    Assert-Refused { Assert-HermesCrashReport $good ('0' * 64) $sourceHash }
    Assert-Refused { Assert-HermesCrashReport $good ('c' * 64) $sourceHash }
    foreach ($name in @('valid', 'noise', 'contradiction', 'crlf', 'utf8', 'stderr', 'exit', 'overflow', 'timeout')) {
        Invoke-ControlFixture $name ($name -ceq 'valid')
    }
    $complete = [Threading.Tasks.Task]::CompletedTask
    $capture = [pscustomobject]@{
        ExitCode = 0; Deadline = 0; StdoutComplete = 1; StderrComplete = 1; WaitSettled = 1
        Stdout = [Text.Encoding]::UTF8.GetBytes(
            "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734499 error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n")
        Stderr = [byte[]]::new(0); Tasks = @($complete, $complete, $complete)
    }
    foreach ($name in @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled')) {
        $saved = $capture.$name
        $capture.$name = 1 - $saved
        Assert-Refused { Assert-HermesCrashOutcome $capture $script:validReport }
        $capture.$name = $saved
    }
    $pending = [Threading.Tasks.TaskCompletionSource[int]]::new()
    $capture.Tasks = @($complete, $complete, $pending.Task)
    Assert-Refused { Assert-HermesCrashOutcome $capture $script:validReport }
    $pending.SetResult(0)
    $capture.Tasks = @($complete, $complete,
        [Threading.Tasks.Task]::FromException([InvalidOperationException]::new('synthetic')))
    Assert-Refused { Assert-HermesCrashOutcome $capture $script:validReport }
    $capture.Tasks = @($complete, $complete, $complete)
    $capture.Stderr = [byte[]]@(120)
    Assert-Refused { Assert-HermesCrashOutcome $capture $script:validReport }
    $capture.Stderr = [byte[]]::new(0)
    $savedOutput = $capture.Stdout
    foreach ($text in @(
        [Text.Encoding]::UTF8.GetString($savedOutput).TrimEnd([char]10),
        ([Text.Encoding]::UTF8.GetString($savedOutput) * 2),
        [Text.Encoding]::UTF8.GetString($savedOutput).Replace('status=0', 'status=1'),
        [Text.Encoding]::UTF8.GetString($savedOutput).Replace('error=0', 'error=1'),
        [Text.Encoding]::UTF8.GetString($savedOutput).Replace('nativeAcceptance=0', 'nativeAcceptance=1')
    )) {
        $capture.Stdout = [Text.Encoding]::UTF8.GetBytes($text)
        Assert-Refused { Assert-HermesCrashOutcome $capture $script:validReport }
    }
    $capture.Stdout = $savedOutput

    $sdk = Join-Path $scratch 'sdk'
    $bundle = Join-Path $scratch 'bundle'
    New-HermesCrashDirectory $sdk
    New-HermesCrashDirectory $bundle
    $sdkFile = Join-Path $sdk 'Model.dll'
    $bundleFile = Join-Path $bundle 'Model.dll'
    [IO.File]::WriteAllText($sdkFile, 'current synthetic SDK source')
    [IO.File]::Copy($sdkFile, $bundleFile)
    $sourceEntry = Get-HermesCrashFileEntry $sdkFile $sdkFile
    $bundleEntry = Get-HermesCrashFileEntry $bundleFile 'Model.dll'
    $inventoryPath = Join-Path $scratch 'inventory.txt'
    $inventoryText = "$sdkFile|Model.dll|$($sourceEntry.sha256)`n"
    [IO.File]::WriteAllText($inventoryPath, $inventoryText)
    $inventory = Read-HermesCrashInventory $inventoryPath $bundle @($sdk)
    Assert-Control ($inventory.Bundle.Count -eq 1 -and $inventory.Sources.Count -eq 1)
    Assert-HermesCrashTree $bundle @($bundleEntry)
    $pins = Open-HermesCrashPins $bundle @($bundleEntry)
    try { Assert-HermesCrashPins $pins; Assert-Control ($pins.Count -eq 1) }
    finally { Close-HermesCrashPins $pins }
    Assert-Refused { Assert-HermesCrashPins $pins }
    $pins = Open-HermesCrashPins $bundle @($bundleEntry)
    try {
        # Windows sharing must refuse a writer. Unix may allow it; in that case
        # the same exported recheck must detect the changed held file.
        $wrote = $false
        try { [IO.File]::WriteAllText($bundleFile, 'changed while pinned'); $wrote = $true } catch { }
        if ($wrote) { Assert-Refused { Assert-HermesCrashPins $pins } }
        else { Assert-HermesCrashPins $pins; Assert-Control $true }
    } finally { Close-HermesCrashPins $pins }
    [IO.File]::Copy($sdkFile, $bundleFile, $true)
    foreach ($relative in @('../Model.dll', '/Model.dll', 'a/../Model.dll', 'a//Model.dll',
            'a\Model.dll', './Model.dll', 'a:Model.dll', 'Model.dll ')) {
        Assert-Refused { Assert-HermesCrashRelativePath $relative }
    }
    foreach ($bad in @(
        $inventoryText.Replace('Model.dll|', '../Model.dll|'),
        $inventoryText.Replace('|Model.dll|', '|model.dll|'),
        ($inventoryText + $inventoryText),
        $inventoryText.Replace($sourceEntry.sha256, ('d' * 64)),
        $inventoryText.Replace($sdkFile, 'relative.dll'),
        $inventoryText.Replace($sdkFile, (Join-Path $sdk '../sdk/Model.dll'))
    )) {
        [IO.File]::WriteAllText($inventoryPath, $bad)
        Assert-Refused { Read-HermesCrashInventory $inventoryPath $bundle @($sdk) }
    }
    [IO.File]::WriteAllText($inventoryPath, $inventoryText)
    Assert-Refused { Read-HermesCrashInventory $inventoryPath $bundle @((Join-Path $scratch 'sd')) }
    [IO.File]::WriteAllText($sdkFile, 'stale SDK source')
    Assert-Refused { Read-HermesCrashInventory $inventoryPath $bundle @($sdk) }
    [IO.File]::WriteAllText($sdkFile, 'current synthetic SDK source')
    [IO.File]::WriteAllText($bundleFile, 'stale bundle')
    Assert-Refused { Assert-HermesCrashTree $bundle @($bundleEntry) }
    Assert-Refused { Open-HermesCrashPins $bundle @($bundleEntry) }
    [IO.File]::Copy($sdkFile, $bundleFile, $true)
    $extra = Join-Path $bundle 'extra.dll'
    [IO.File]::WriteAllText($extra, 'extra')
    Assert-Refused { Assert-HermesCrashTree $bundle @($bundleEntry) }
    [IO.File]::Delete($extra)
    [IO.File]::Delete($bundleFile)
    Assert-Refused { Assert-HermesCrashTree $bundle @($bundleEntry) }
    [IO.File]::Copy($sdkFile, $bundleFile)
    $extraDirectory = Join-Path $bundle 'empty'
    New-HermesCrashDirectory $extraDirectory
    Assert-Refused { Assert-HermesCrashTree $bundle @($bundleEntry) }
    [IO.Directory]::Delete($extraDirectory)
    $link = Join-Path $bundle 'linked'
    if ([OperatingSystem]::IsWindows()) {
        $null = New-Item -ItemType Junction -Path $link -Target $sdk
    } else { $null = [IO.Directory]::CreateSymbolicLink($link, $sdk) }
    try {
        Assert-Refused { Assert-HermesCrashTree $bundle @($bundleEntry) }
        Assert-Refused { Assert-HermesCrashPath (Join-Path $link 'Model.dll') }
    } finally { [IO.Directory]::Delete($link) }
    $source = Get-HermesCrashFileEntry $sdkFile 'sdk/Model.dll'
    $receipt = [ordered]@{
        schemaVersion = 1; revision = ('a' * 40); tree = ('b' * 40)
        sourceSha256 = (Get-HermesCrashEntryDigest @($source))
        bundleSha256 = (Get-HermesCrashEntryDigest @($bundleEntry))
        sources = @($source); bundle = @($bundleEntry); sdkSources = @($sourceEntry)
    }
    $receiptPath = Join-Path $scratch 'publication.json'
    [IO.File]::WriteAllBytes($receiptPath, (ConvertTo-ControlBytes $receipt))
    $parsed = Read-HermesCrashReceipt $receiptPath
    $checkout = [pscustomobject]@{
        Revision = $receipt.revision; Tree = $receipt.tree
        SourceSha256 = $receipt.sourceSha256; Sources = @($source)
    }
    Assert-HermesCrashReceiptBinding $parsed $checkout
    Assert-Control ($parsed.bundle.Count -eq 1)
    foreach ($property in @('Revision', 'Tree', 'SourceSha256')) {
        $saved = $checkout.$property
        $checkout.$property = 'c' * $saved.Length
        Assert-Refused { Assert-HermesCrashReceiptBinding $parsed $checkout }
        $checkout.$property = $saved
    }
    $receiptJson = [Text.Encoding]::UTF8.GetString((ConvertTo-ControlBytes $receipt))
    foreach ($bad in @(
        $receiptJson.Replace('"schemaVersion":1', '"schemaVersion":1,"schemaVersion":1'),
        $receiptJson.Replace('"schemaVersion":1', '"schemaVersion":1,"extra":0'),
        $receiptJson.Replace('"schemaVersion":1', '"schemaVersion":true'),
        $receiptJson.Replace('"length":', '"Length":'),
        $receiptJson.Replace($bundleEntry.sha256, ('c' * 64))
    )) {
        [IO.File]::WriteAllText($receiptPath, $bad)
        Assert-Refused { Read-HermesCrashReceipt $receiptPath }
    }
    $manySources = @(for ($index = 0; $index -lt 50; $index++) {
        [ordered]@{ path = "sdk/source$index.cs"; sha256 = $source.sha256; length = $source.length }
    })
    $receipt.sources = $manySources
    $receipt.sourceSha256 = Get-HermesCrashEntryDigest $manySources
    $largeReceipt = ConvertTo-ControlBytes $receipt
    [IO.File]::WriteAllBytes($receiptPath, $largeReceipt)
    Assert-Control ($largeReceipt.Length -gt 4096 -and (Read-HermesCrashReceipt $receiptPath).sources.Count -eq 50)
    [IO.File]::WriteAllText($receiptPath, 'x' * 4097)
    Assert-Refused { Read-HermesCrashBytes $receiptPath }
    Assert-Refused { Get-HermesCrashFileEntry $receiptPath 'publication.json' 4096 }
    Assert-Refused { Get-HermesCrashCheckout $root 'not-a-revision' }
    Test-CheckoutFixture $scratch
    if (-not [OperatingSystem]::IsWindows()) {
        Assert-Refused { Assert-HermesCrashHost }
        Test-CrashGuardDiagnostic
    }
    Assert-HermesCrashReportAbsent $scratch
    $existingReport = Join-Path $scratch '.artifacts/installer-evidence/startup-crash-control.json'
    [IO.File]::WriteAllBytes($existingReport, $good)
    Assert-Refused { Assert-HermesCrashReportAbsent $scratch }
    [IO.File]::Delete($existingReport)
    [IO.Directory]::CreateDirectory($existingReport) | Out-Null
    Assert-Refused { Assert-HermesCrashReportAbsent $scratch }
    Test-CrashControlFacts
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
[Console]::Out.WriteLine("HERMES_CRASH_TESTS v=1 passed=$script:passed failed=$script:failed")
if ($script:failed -ne 0) { exit 1 }
exit 0
