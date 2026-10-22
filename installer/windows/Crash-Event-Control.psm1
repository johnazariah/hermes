#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -DisableNameChecking
$script:crash = Get-Module Crash-Control
$script:templateHash = 'cd0a3d85a95e98a2909d1a77bada29db34ed1abbe37a058d6c06e9aca599557e'

function Get-EventProperties {
    param([Text.Json.JsonElement]$Element, [string[]]$Names)
    return ,(& $script:crash {
        param($Element, $Names)
        Get-HermesCrashJsonProperties $Element $Names
    } $Element $Names)
}

function Get-EventUInt32 {
    param([Text.Json.JsonElement]$Element)
    return (& $script:crash { param($Element) Get-HermesCrashJsonUInt32 $Element } $Element)
}

function Get-EventUInt64 {
    param([Text.Json.JsonElement]$Element)
    [uint64]$number = 0
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Number `
        -or $Element.GetRawText() -cnotmatch '\A(0|[1-9][0-9]{0,19})\z' `
        -or -not $Element.TryGetUInt64([ref]$number)) { throw '10' }
    return $number
}

function Read-EventJson {
    param([byte[]]$Bytes)
    if ($Bytes.Length -lt 1 -or $Bytes.Length -gt 4096) { throw '9' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 8
    return [Text.Json.JsonDocument]::Parse($text, $options)
}

function Get-EventHash {
    param([Text.Json.JsonElement]$Element)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::String) { throw '10' }
    $hash = $Element.GetString()
    Assert-HermesCrashHash $hash
    return $hash
}

function Assert-HermesCrashEventSchema {
    param([byte[]]$Bytes)
    $document = Read-EventJson $Bytes
    try {
        $root = Get-EventProperties $document.RootElement @(
            'schemaVersion', 'usable', 'unavailable', 'metadataCount', 'versions')
        foreach ($name in @('schemaVersion', 'usable')) {
            if ((Get-EventUInt32 $root[$name]) -ne 1) { throw '10' }
        }
        if ((Get-EventUInt32 $root['unavailable']) -ne 0 `
            -or $root['versions'].ValueKind -ne [Text.Json.JsonValueKind]::Array) { throw '10' }
        $count = $root['versions'].GetArrayLength()
        if ($count -lt 1 -or $count -gt 8 -or (Get-EventUInt32 $root['metadataCount']) -ne $count) { throw '10' }
        $seen = [Collections.Generic.HashSet[uint32]]::new()
        foreach ($version in $root['versions'].EnumerateArray()) {
            $row = Get-EventProperties $version @('id', 'version', 'unavailable', 'fieldCount',
                'duplicateCount', 'caseMismatchCount', 'knownFieldPresentBits', 'templateSha256', 'fields')
            if ((Get-EventUInt32 $row['id']) -ne 1000) { throw '10' }
            foreach ($name in @('unavailable', 'duplicateCount', 'caseMismatchCount')) {
                if ((Get-EventUInt32 $row[$name]) -ne 0) { throw '10' }
            }
            $identity = Get-EventUInt32 $row['version']
            $fieldCount = Get-EventUInt32 $row['fieldCount']
            $bits = Get-EventUInt32 $row['knownFieldPresentBits']
            $hash = Get-EventHash $row['templateSha256']
            if ($identity -gt 255 -or -not $seen.Add($identity) -or $fieldCount -lt 1 `
                -or $fieldCount -gt 128 -or $bits -gt 63 `
                -or $row['fields'].ValueKind -ne [Text.Json.JsonValueKind]::Array `
                -or $row['fields'].GetArrayLength() -ne 6) { throw '10' }
            $index = 0
            $present = 0
            $actualBits = 0
            foreach ($field in $row['fields'].EnumerateArray()) {
                $index++
                if ($field.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $field.GetArrayLength() -ne 5) { throw '10' }
                $values = @(foreach ($value in $field.EnumerateArray()) { Get-EventUInt32 $value })
                if ($values[0] -ne $index -or $values[1] -gt 1 -or $values[2] -ne $values[1] `
                    -or $values[3] -notin @(0, 1, 8, 10, 17, 20, 21) `
                    -or $values[4] -notin @(0, 1, 2, 8, 10, 18, 19, 20, 29, 30, 31, 32) `
                    -or ($values[1] -eq 0 -and ($values[3] -ne 0 -or $values[4] -ne 0))) { throw '10' }
                if ($identity -eq 0) {
                    $expected = switch ($index) { 1 { '1,1,1,20,18' }; 2 { '2,1,1,21,19' }; default { "$index,1,1,1,1" } }
                    if (($values -join ',') -cne $expected) { throw '10' }
                }
                $present += $values[1]
                $actualBits = $actualBits -bor ([int]$values[1] -shl ($index - 1))
            }
            if ($present -gt $fieldCount -or $actualBits -ne $bits) { throw '10' }
            if ($identity -eq 0 -and ($fieldCount -ne 15 -or $bits -ne 63 -or $hash -cne $script:templateHash)) { throw '10' }
        }
        if (-not $seen.Contains(0)) { throw '10' }
    } finally { $document.Dispose() }
}

function Assert-HermesCrashEventReport {
    param([byte[]]$Bytes, [string]$MetadataSha256, [string]$SchemaSourceSha256)
    Assert-HermesCrashHash $MetadataSha256
    Assert-HermesCrashHash $SchemaSourceSha256
    $document = Read-EventJson $Bytes
    try {
        $ones = @('schemaVersion', 'thisOwnedControlOnly', 'schemaAdmitted', 'receiptClosed',
            'exactCandidates', 'exactReadMatched', 'outerSupervisionRequired')
        $zeros = @('status', 'reason', 'originalGatePassed', 'nativeAcceptance',
            'independentPublicationVerified', 'birthMinusOneCandidates', 'birthPlusOneCandidates',
            'globalLogUniqueness', 'controlStatus', 'reportError', 'synchronousCallsInterruptible')
        $constants = [ordered]@{
            completedQueryEnumerations = 3; maximumNextCallsPerQuery = 2; nextBatchCapacity = 2
            maximumPollSeconds = 20; maximumWindowSeconds = 120
        }
        $properties = Get-EventProperties $document.RootElement (
            $ones + $zeros + @($constants.Keys) + @('polls', 'queries', 'metadataSha256', 'schemaSourceSha256'))
        foreach ($name in $ones) { if ((Get-EventUInt32 $properties[$name]) -ne 1) { throw '12' } }
        foreach ($name in $zeros) { if ((Get-EventUInt32 $properties[$name]) -ne 0) { throw '12' } }
        foreach ($name in $constants.Keys) {
            if ((Get-EventUInt32 $properties[$name]) -ne $constants[$name]) { throw '12' }
        }
        if ((Get-EventHash $properties['metadataSha256']) -cne $MetadataSha256 `
            -or (Get-EventHash $properties['schemaSourceSha256']) -cne $SchemaSourceSha256) { throw '12' }
        $polls = Get-EventUInt32 $properties['polls']
        $queries = Get-EventUInt32 $properties['queries']
        if ($polls -lt 1 -or $polls -gt 81 -or $queries -ne $polls + 2) { throw '12' }
        return [pscustomobject]@{ Polls = $polls; Queries = $queries }
    } finally { $document.Dispose() }
}

function Assert-EventCapture {
    param([object]$Capture)
    foreach ($name in @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled')) {
        if ($Capture.$name -isnot [int]) { throw '16' }
    }
    if ($Capture.ExitCode -notin @(0, 1) -or $Capture.Deadline -ne 0 -or $Capture.StdoutComplete -ne 1 `
        -or $Capture.StderrComplete -ne 1 -or $Capture.WaitSettled -ne 1 `
        -or $Capture.Stdout -isnot [byte[]] -or $Capture.Stdout.Length -lt 1 -or $Capture.Stdout.Length -gt 511 `
        -or $Capture.Stderr -isnot [byte[]] -or $Capture.Stderr.Length -ne 0 `
        -or $Capture.Tasks -isnot [object[]] -or $Capture.Tasks.Count -lt 3 -or $Capture.Tasks.Count -gt 514) { throw '16' }
    foreach ($task in $Capture.Tasks) {
        if ($task -isnot [Threading.Tasks.Task] -or $task.Status -ne [Threading.Tasks.TaskStatus]::RanToCompletion) { throw '16' }
        $null = $task.GetAwaiter().GetResult()
    }
}

function Assert-HermesCrashEventOutcome {
    param([object]$Capture, [object]$Control)
    Assert-EventCapture $Capture
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Capture.Stdout)
    $observation = "HERMES_CRASH_EVENT_OBSERVATION v=1 reason=0 matched=1 lowerEmpty=1 upperEmpty=1 nativeAcceptance=0`n"
    $base = "HERMES_CRASH_CONTROL v=1 status=0 exit=$($Control.Exit) error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n"
    $event = "HERMES_CRASH_EVENT_CONTROL v=1 status=0 reason=0 reportError=0 originalGatePassed=0 nativeAcceptance=0`n"
    if (-not [string]::Equals($text, ($observation + $base + $event), [StringComparison]::Ordinal)) { throw '16' }
    $baseBytes = [Text.Encoding]::UTF8.GetBytes($base)
    try {
        Assert-HermesCrashOutcome ([pscustomobject]@{
            ExitCode = $Capture.ExitCode; Deadline = $Capture.Deadline
            StdoutComplete = $Capture.StdoutComplete; StderrComplete = $Capture.StderrComplete
            WaitSettled = $Capture.WaitSettled; Tasks = $Capture.Tasks; Stdout = $baseBytes; Stderr = $Capture.Stderr
        }) $Control
    } finally { [Array]::Clear($baseBytes) }
}

function ConvertTo-HermesCrashEventDiagnostic {
    param([object]$Phase, [object]$Failure)
    $line = ConvertTo-HermesCrashDiagnostic $Phase $Failure
    $match = [regex]::Match($line,
        '\AHERMES_CRASH_DIAGNOSTIC v=1 phase=(0|[1-9]|1[0-2]) family=(0|[1-9]|[12][0-9]|30) reason=(0|[1-9]|1[0-9]|20) nativeError=[0-9]{1,10}\z')
    if (-not $match.Success) { throw '10' }
    return [string]::Format([Globalization.CultureInfo]::InvariantCulture,
        'HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase={0} family={1} reason={2}',
        [object[]]@([int]$match.Groups[1].Value, [int]$match.Groups[2].Value, [int]$match.Groups[3].Value))
}

function ConvertTo-HermesCrashEventFacts {
    param([object]$Capture)
    $unavailable = 'HERMES_CRASH_EVENT_FACTS v=1 available=0 status=0 controlStatus=0 reason=0 matched=0 lowerEmpty=0 upperEmpty=0 controlError=0 cleanupError=0 reportError=0 originalGatePassed=0 nativeAcceptance=0'
    try {
        Assert-EventCapture $Capture
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($Capture.Stdout)
        $u = '(0|[1-9][0-9]{0,9})'
        $pattern = '\A' +
            "(?:HERMES_CRASH_EVENT_OBSERVATION v=1 reason=(?<observation>$u) matched=(?<matched>[01]) lowerEmpty=(?<lower>[01]) upperEmpty=(?<upper>[01]) nativeAcceptance=0\n){0,2}" +
            "HERMES_CRASH_CONTROL v=1 status=(?<controlStatus>[01]) exit=(?<exit>$u) error=(?<error>$u) cleanupError=(?<cleanup>$u) reportError=(?<baseReport>[01]) nativeAcceptance=0\n" +
            "(?:HERMES_CRASH_EXIT v=1 check=(?<check>$u) nativeError=(?<native>$u) nativeAcceptance=0\n)?" +
            "HERMES_CRASH_EVENT_CONTROL v=1 status=(?<status>[01]) reason=(?<reason>$u) reportError=(?<report>[01]) originalGatePassed=0 nativeAcceptance=0\n\z"
        $match = [regex]::Match($text, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)
        if (-not $match.Success) { return $unavailable }
        $values = @{}
        foreach ($name in @('observation', 'matched', 'lower', 'upper', 'controlStatus', 'exit',
                'error', 'cleanup', 'baseReport', 'check', 'native', 'status', 'reason', 'report')) {
            $values[$name] = [uint32]0
            foreach ($item in $match.Groups[$name].Captures) {
                [uint32]$value = 0
                if (-not [uint32]::TryParse($item.Value, [Globalization.NumberStyles]::None,
                        [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) { return $unavailable }
                if ($name -ceq 'observation' -and $value -gt 21) { return $unavailable }
                $values[$name] = $value
            }
        }
        if ($values.status -ne $Capture.ExitCode -or $values.reason -gt 21 -or $values.error -gt 11 `
            -or $values.cleanup -gt 255 -or ($match.Groups['check'].Success -and (
                $values.controlStatus -ne 1 -or $values.error -ne 6 -or $values.check -lt 1 -or $values.check -gt 25))) {
            return $unavailable
        }
        if ($values.status -eq 0 -and ($values.controlStatus -ne 0 -or $values.reason -ne 0 `
                -or $values.report -ne 0 -or $values.baseReport -ne 0 -or $values.error -ne 0 -or $values.cleanup -ne 0 `
                -or $values.exit -notin @(0x80131506L, 0x80131623L, 0xc0000409L, 0xc0000602L, 0xe0434352L) `
                -or $match.Groups['observation'].Captures.Count -ne 1 -or $values.observation -ne 0 `
                -or $values.matched -ne 1 -or $values.lower -ne 1 -or $values.upper -ne 1)) { return $unavailable }
        # Advisory only. Never echo captures, hashes, exit/native codes or exception text.
        return [string]::Format([Globalization.CultureInfo]::InvariantCulture,
            'HERMES_CRASH_EVENT_FACTS v=1 available=1 status={0} controlStatus={1} reason={2} matched={3} lowerEmpty={4} upperEmpty={5} controlError={6} cleanupError={7} reportError={8} originalGatePassed=0 nativeAcceptance=0',
            [object[]]@($values.status, $values.controlStatus, $values.reason, $values.matched, $values.lower,
                $values.upper, $values.error, $values.cleanup, $values.report))
    } catch { return $unavailable }
}

function Assert-HermesCrashEventAbsent {
    param([string]$Workspace, [switch]$Metadata)
    Assert-HermesCrashReportAbsent $Workspace
    $names = @('event-correlation.json')
    if ($Metadata) { $names += 'startup-event-schema.json' }
    foreach ($path in [IO.Directory]::EnumerateFileSystemEntries((Join-Path $Workspace '.artifacts/installer-evidence'))) {
        if ($names -icontains [IO.Path]::GetFileName($path)) { throw '17' }
    }
}

function Get-HermesCrashEventWorkflowEntry {
    param([string]$Root)
    $relative = '.github/workflows/startup-crash-event-control.yml'
    $tracked = & $script:crash {
        param($Root, $Relative)
        Invoke-HermesCrashGit $Root @('ls-files', '--error-unmatch', '--', $Relative)
    } $Root $relative
    if ($tracked -cne $relative) { throw '8' }
    return Get-HermesCrashFileEntry (Join-Path $Root $relative) $relative 65536
}

function Assert-HermesCrashEventFreshness {
    param([Collections.IDictionary]$Receipt, [long]$Created, [long]$Written, [long]$Now)
    [long]$start = $Receipt.startedUtcTicks
    [long]$end = $Receipt.completedUtcTicks
    if ($start -lt 1 -or $end -lt $start -or $Created -lt $start -or $Written -lt $start `
        -or $Created -gt $end -or $Written -gt $end -or $Now -lt $end `
        -or $Receipt.createdUtcTicks -ne $Created -or $Receipt.writtenUtcTicks -ne $Written `
        -or $Now - $start -gt [TimeSpan]::FromMinutes(5).Ticks) { throw '11' }
}

function Read-HermesCrashEventPublication {
    param([byte[]]$Bytes)
    $document = Read-EventJson $Bytes
    try {
        $hashes = @('workflowSha256', 'schemaSourceSha256', 'metadataSha256', 'sourceSha256')
        $numbers = @('schemaVersion', 'length', 'runId', 'runAttempt', 'startedUtcTicks',
            'completedUtcTicks', 'createdUtcTicks', 'writtenUtcTicks')
        $row = Get-EventProperties $document.RootElement ($hashes + $numbers + @('revision', 'tree'))
        $receipt = [ordered]@{}
        foreach ($name in $hashes) { $receipt[$name] = Get-EventHash $row[$name] }
        foreach ($name in $numbers) { $receipt[$name] = Get-EventUInt64 $row[$name] }
        foreach ($name in @('revision', 'tree')) {
            if ($row[$name].ValueKind -ne [Text.Json.JsonValueKind]::String `
                -or $row[$name].GetString() -cnotmatch '\A[0-9a-f]{40}\z') { throw '11' }
            $receipt[$name] = $row[$name].GetString()
        }
        if ($receipt.schemaVersion -ne 1 -or $receipt.length -lt 1 -or $receipt.length -gt 4096 `
            -or $receipt.runId -lt 1 -or $receipt.runAttempt -lt 1) { throw '11' }
        foreach ($name in @('startedUtcTicks', 'completedUtcTicks', 'createdUtcTicks', 'writtenUtcTicks')) {
            if ($receipt[$name] -gt [DateTime]::MaxValue.Ticks) { throw '11' }
        }
        return $receipt
    } finally { $document.Dispose() }
}

function Assert-HermesCrashEventPublicationBinding {
    param([Collections.IDictionary]$Receipt, [object]$Checkout, [object]$Workflow,
        [object]$SchemaSource, [object]$Metadata, [string]$RunId, [string]$RunAttempt)
    if ($RunId -cnotmatch '\A[1-9][0-9]{0,19}\z' -or $RunAttempt -cnotmatch '\A[1-9][0-9]{0,19}\z' `
        -or $Receipt.runId.ToString([Globalization.CultureInfo]::InvariantCulture) -cne $RunId `
        -or $Receipt.runAttempt.ToString([Globalization.CultureInfo]::InvariantCulture) -cne $RunAttempt `
        -or $Receipt.revision -cne $Checkout.Revision -or $Receipt.tree -cne $Checkout.Tree `
        -or $Receipt.sourceSha256 -cne $Checkout.SourceSha256 `
        -or $Receipt.workflowSha256 -cne $Workflow.sha256 `
        -or $Receipt.schemaSourceSha256 -cne $SchemaSource.sha256 `
        -or $Receipt.metadataSha256 -cne $Metadata.sha256 -or $Receipt.length -ne $Metadata.length) { throw '11' }
}

function Invoke-HermesCrashEventSchemaPublication {
    [CmdletBinding()]
    param()
    $sourcePins = $null
    $workflowPins = $null
    $metadataPins = $null
    try {
        Assert-HermesCrashHost
        $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
        if ($root -cne [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)) { throw '1' }
        Assert-HermesCrashEventAbsent $root -Metadata
        $private = Join-Path $root '.artifacts/crash-control-private'
        Assert-HermesCrashPath $private
        $receiptPath = Join-Path $private 'event-schema-publication.json'
        if (Test-Path -LiteralPath $receiptPath) { throw '17' }
        $checkout = Get-HermesCrashCheckout $root $env:GITHUB_SHA
        Assert-HermesCrashReceiptBinding (Read-HermesCrashReceipt (Join-Path $private 'publication.json')) $checkout
        $sourcePins = Open-HermesCrashPins $root $checkout.Sources
        $workflow = Get-HermesCrashEventWorkflowEntry $root
        $workflowPins = Open-HermesCrashPins $root @($workflow)
        $source = @($checkout.Sources | Where-Object { $_.path -ceq 'installer/windows/Invoke-StartupEventSchema.ps1' })
        if ($source.Count -ne 1) { throw '11' }
        $started = [DateTime]::UtcNow.Ticks
        # Invoke the existing fixed metadata-only script in THIS actual installer
        # job. No child process, selector, GITHUB_JOB substitution or raw metadata.
        $summary = @(& (Join-Path $PSScriptRoot 'Invoke-StartupEventSchema.ps1'))
        if ($LASTEXITCODE -ne 0 -or $summary.Count -ne 1 -or $summary[0] -isnot [string] `
            -or $summary[0] -cnotmatch '\AHERMES_STARTUP_SCHEMA v=1 usable=1 unavailable=0 metadataCount=([1-8]) versions=\1 published=1\z') { throw '11' }
        $path = Join-Path $root '.artifacts/installer-evidence/startup-event-schema.json'
        $metadata = Get-HermesCrashFileEntry $path 'startup-event-schema.json' 4096
        $metadataPins = Open-HermesCrashPins ([IO.Path]::GetDirectoryName($path)) @($metadata)
        Assert-HermesCrashEventSchema (Read-HermesCrashBytes $path)
        $fresh = Get-HermesCrashCheckout $root $env:GITHUB_SHA
        if ($fresh.SourceSha256 -cne $checkout.SourceSha256 -or $fresh.Tree -cne $checkout.Tree) { throw '8' }
        foreach ($pins in @($sourcePins, $workflowPins, $metadataPins)) { Assert-HermesCrashPins $pins }
        $receipt = [ordered]@{
            schemaVersion = 1; revision = $checkout.Revision; tree = $checkout.Tree
            sourceSha256 = $checkout.SourceSha256; workflowSha256 = $workflow.sha256
            schemaSourceSha256 = $source[0].sha256; metadataSha256 = $metadata.sha256; length = $metadata.length
            runId = [uint64]$env:GITHUB_RUN_ID; runAttempt = [uint64]$env:GITHUB_RUN_ATTEMPT
            startedUtcTicks = $started; completedUtcTicks = [DateTime]::UtcNow.Ticks
            createdUtcTicks = [IO.File]::GetCreationTimeUtc($path).Ticks
            writtenUtcTicks = [IO.File]::GetLastWriteTimeUtc($path).Ticks
        }
        $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes(($receipt | ConvertTo-Json -Compress))
        $parsed = Read-HermesCrashEventPublication $bytes
        Assert-HermesCrashEventPublicationBinding $parsed $checkout $workflow $source[0] $metadata `
            $env:GITHUB_RUN_ID $env:GITHUB_RUN_ATTEMPT
        Assert-HermesCrashEventFreshness $parsed $receipt.createdUtcTicks $receipt.writtenUtcTicks ([DateTime]::UtcNow.Ticks)
        $file = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $file.Write($bytes); $file.Flush($true) } finally { $file.Dispose() }
        [Console]::Out.WriteLine('HERMES_CRASH_EVENT_SCHEMA_PUBLICATION v=1 status=0')
    } catch {
        [Console]::Out.WriteLine('HERMES_CRASH_EVENT_SCHEMA_PUBLICATION v=1 status=1')
        throw '11'
    } finally {
        Close-HermesCrashPins $metadataPins
        Close-HermesCrashPins $workflowPins
        Close-HermesCrashPins $sourcePins
    }
}

function Clear-HermesCrashEventCapture {
    param([object]$Capture)
    if ($null -eq $Capture) { return }
    foreach ($name in @('Stdout', 'Stderr')) {
        if ($Capture.$name -is [byte[]]) { [Array]::Clear($Capture.$name) }
    }
}

function Invoke-HermesCrashEventCapture {
    param([Diagnostics.Process]$Process,
        [Collections.Generic.List[Threading.Tasks.Task]]$Operations,
        [int]$DeadlineMilliseconds = 30000)
    if ($null -eq $Operations -or $Operations.Count -ne 0 `
        -or $DeadlineMilliseconds -lt 1 -or $DeadlineMilliseconds -gt 30000) { throw '14' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $output = $null
    $errors = $null
    $bytes = $null
    $transferred = $false
    try {
        $wait = $Process.WaitForExitAsync()
        $Operations.Add($wait)
        # Reuse the tested bounded readers, but retain their buffers here so
        # exceptions/overflow/deadlines also reach explicit zeroing.
        $output = & $script:crash {
            param($Stream, $Operations)
            New-HermesCrashRead $Stream 512 $Operations
        } $Process.StandardOutput.BaseStream $Operations
        $errors = & $script:crash {
            param($Stream, $Operations)
            New-HermesCrashRead $Stream 1 $Operations
        } $Process.StandardError.BaseStream $Operations
        while ($true) {
            if ($clock.ElapsedMilliseconds -ge $DeadlineMilliseconds) { throw '14' }
            & $script:crash { param($Read) Update-HermesCrashRead $Read } $output
            & $script:crash { param($Read) Update-HermesCrashRead $Read } $errors
            if ($wait.IsCompleted -and $output.End -and $errors.End) { break }
            [Threading.Thread]::Sleep(5)
        }
        Assert-HermesCrashEventSettled $Operations
        if ($clock.ElapsedMilliseconds -ge $DeadlineMilliseconds -or -not $Process.HasExited) { throw '14' }
        $bytes = [byte[]]::new($output.Count)
        [Array]::Copy($output.Buffer, $bytes, $output.Count)
        $result = [pscustomobject]@{
            ExitCode = $Process.ExitCode; Deadline = 0; StdoutComplete = 1; StderrComplete = 1
            WaitSettled = 1; Stdout = $bytes; Stderr = [byte[]]::new(0); Tasks = $Operations.ToArray()
        }
        $transferred = $true
        return $result
    } catch {
        # Failure cannot take normal job release. First attempt bounded actual
        # process/task settlement; the caller then abandons the entire private job.
        try {
            if (-not $Process.HasExited) { $Process.Kill($true) }
            $null = $Process.WaitForExit(5000)
        } catch { }
        try { $null = [Threading.Tasks.Task]::WhenAll($Operations.ToArray()).Wait(5000) } catch { }
        throw
    } finally {
        if ($null -ne $output) { [Array]::Clear($output.Buffer) }
        if ($null -ne $errors) { [Array]::Clear($errors.Buffer) }
        if (-not $transferred -and $null -ne $bytes) { [Array]::Clear($bytes) }
    }
}

function Assert-HermesCrashEventSettled {
    param([Collections.Generic.List[Threading.Tasks.Task]]$Operations)
    if ($null -eq $Operations -or $Operations.Count -lt 3 -or $Operations.Count -gt 514) { throw '15' }
    foreach ($task in $Operations) {
        if ($task.Status -ne [Threading.Tasks.TaskStatus]::RanToCompletion) { throw '15' }
        $null = $task.GetAwaiter().GetResult()
    }
}

function Complete-HermesCrashEventScope {
    param([object]$Scope, [object[]]$PinSets,
        [Collections.Generic.List[Threading.Tasks.Task]]$Operations)
    Assert-HermesCrashEventSettled $Operations
    foreach ($pins in $PinSets) {
        foreach ($pin in $pins) {
            if (-not $pin.Stream.SafeFileHandle.IsClosed) { throw '15' }
        }
    }
    # The native scope independently requires this PowerShell to be the only
    # remaining process. Never invoke this function from failure cleanup.
    $Scope.CompleteAfterChildrenExited()
    $Scope.Dispose()
}

Export-ModuleMember -Function Assert-HermesCrashEventSchema, Assert-HermesCrashEventReport,
    Assert-HermesCrashEventOutcome, ConvertTo-HermesCrashEventFacts, ConvertTo-HermesCrashEventDiagnostic,
    Assert-HermesCrashEventAbsent,
    Get-HermesCrashEventWorkflowEntry, Assert-HermesCrashEventFreshness, Read-HermesCrashEventPublication,
    Assert-HermesCrashEventPublicationBinding, Invoke-HermesCrashEventSchemaPublication,
    Clear-HermesCrashEventCapture, Invoke-HermesCrashEventCapture, Assert-HermesCrashEventSettled,
    Complete-HermesCrashEventScope
