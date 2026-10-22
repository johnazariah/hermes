#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-HermesCrashDiagnostic {
    param([object]$Phase, [object]$Failure)
    if ($Phase -isnot [int] -or $Phase -lt 0 -or $Phase -gt 12) { $Phase = 0 }
    $family = 0
    $reason = 0
    [uint32]$nativeError = 0
    $exception = if ($Failure -is [Management.Automation.ErrorRecord]) { $Failure.Exception }
        elseif ($Failure -is [Exception]) { $Failure }
        else { $null }
    $wrappers = @([Reflection.TargetInvocationException], [Management.Automation.MethodInvocationException],
        [Management.Automation.CmdletInvocationException], [Management.Automation.RuntimeException])
    for ($depth = 0; $depth -lt 8 -and $null -ne $exception; $depth++) {
        if ($wrappers -notcontains $exception.GetType() -or $null -eq $exception.InnerException) { break }
        $exception = $exception.InnerException
    }
    # Ordinal family IDs follow this fixed exact-type whitelist. No type name,
    # message, HRESULT, ErrorRecord text, target or child output is exported.
    $known = @(
        [Management.Automation.RuntimeException], [ComponentModel.Win32Exception],
        [IO.IOException], [UnauthorizedAccessException], [IO.InvalidDataException], [InvalidOperationException],
        [ArgumentException], [ArgumentNullException], [ArgumentOutOfRangeException],
        [NotSupportedException], [PlatformNotSupportedException], [TimeoutException],
        [OperationCanceledException], [Threading.Tasks.TaskCanceledException], [Text.Json.JsonException],
        [IO.FileNotFoundException], [IO.FileLoadException], [BadImageFormatException], [TypeLoadException],
        [MissingMethodException], [Reflection.AmbiguousMatchException], [Reflection.ReflectionTypeLoadException],
        [ObjectDisposedException], [Security.Cryptography.CryptographicException], [Text.DecoderFallbackException],
        [FormatException], [OverflowException], [Management.Automation.ParameterBindingException],
        [Management.Automation.MethodException], [AggregateException]
    )
    if ($null -ne $exception) {
        for ($index = 0; $index -lt $known.Count; $index++) {
            if ($exception.GetType() -eq $known[$index]) { $family = $index + 1; break }
        }
        if ($family -eq 1) {
            $constants = @('1', '2', '3', '4', '5', '6', '7', '8', '9', '10',
                '11', '12', '13', '14', '15', '16', '17', '18', '19', '20')
            $reason = 1 + [Array]::IndexOf($constants, $exception.Message)
            if ($reason -eq 0) { $family = 0 }
        } elseif ($family -eq 2) {
            $nativeError = [uint32]([long]$exception.NativeErrorCode -band 0xffffffffL)
        }
    }
    return "HERMES_CRASH_DIAGNOSTIC v=1 phase=$Phase family=$family reason=$reason nativeError=$nativeError"
}

function Assert-HermesCrashHost {
    if (-not [OperatingSystem]::IsWindows() -or [Environment]::Version.Major -lt 9 `
        -or -not [Environment]::UserInteractive -or $env:OS -cne 'Windows_NT' `
        -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_OS -cne 'Windows' `
        -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:GITHUB_JOB -cne 'installer' `
        -or $env:GITHUB_SERVER_URL -cne 'https://github.com' `
        -or $env:GITHUB_REPOSITORY -cne 'johnazariah/hermes') { throw '1' }
}

function Assert-HermesCrashHash {
    param([string]$Hash)
    if ($Hash -cnotmatch '\A[0-9a-f]{64}\z' -or $Hash -ceq ('0' * 64)) { throw '2' }
}

function Assert-HermesCrashPath {
    param([string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw '3' }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -match '(^|[\\/])\.env($|[.\\/])') { throw '3' }
    $root = [IO.Path]::GetPathRoot($full)
    $current = $root
    foreach ($part in $full.Substring($root.Length).Split(
            [char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar),
            [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '3' }
    }
}

function New-HermesCrashDirectory {
    param([string]$Path)
    if (-not [IO.Directory]::Exists($Path)) {
        $parent = [IO.Path]::GetDirectoryName($Path)
        Assert-HermesCrashPath $parent
        $null = [IO.Directory]::CreateDirectory($Path)
    }
    Assert-HermesCrashPath $Path
}

function Assert-HermesCrashRelativePath {
    param([string]$Path)
    if ($Path -cnotmatch '\A[A-Za-z0-9_.+-]+(/[A-Za-z0-9_.+-]+)*\z' `
        -or @($Path.Split('/') | Where-Object { $_ -in @('.', '..') }).Count -ne 0) { throw '4' }
}

function Get-HermesCrashStreamHash {
    param([IO.Stream]$Stream)
    $Stream.Position = 0
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Stream)).ToLowerInvariant()
    $Stream.Position = 0
    return $hash
}

function Get-HermesCrashFileEntry {
    param([string]$Path, [string]$Name, [long]$Maximum = [long]::MaxValue)
    Assert-HermesCrashPath $Path
    $file = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($file.Length -gt $Maximum) { throw '9' }
        return [ordered]@{ path = $Name; sha256 = (Get-HermesCrashStreamHash $file); length = $file.Length }
    } finally { $file.Dispose() }
}

function Get-HermesCrashEntryDigest {
    param([object[]]$Entries)
    $lines = [Collections.Generic.List[string]]::new()
    $byPath = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Entries) { $byPath.Add($entry.path, $entry) }
    $paths = [string[]]@($byPath.Keys)
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    foreach ($path in $paths) {
        $entry = $byPath[$path]
        $length = [Convert]::ToString($entry.length, [Globalization.CultureInfo]::InvariantCulture)
        $lines.Add("$path|$($entry.sha256)|$length`n")
    }
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes(($lines -join '')))).ToLowerInvariant()
}

function Open-HermesCrashPins {
    param([string]$Root, [object[]]$Entries, [switch]$Absolute)
    $pins = [Collections.Generic.List[object]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    try {
        foreach ($entry in $Entries) {
            if ($Absolute) { $path = [string]$entry.path }
            else {
                Assert-HermesCrashRelativePath $entry.path
                $path = Join-Path $Root $entry.path
            }
            if (-not $seen.Add($path)) { throw '5' }
            Assert-HermesCrashHash $entry.sha256
            Assert-HermesCrashPath $path
            $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $pins.Add([pscustomobject]@{ Path = $path; Entry = $entry; Stream = $file })
            if ($file.Length -ne $entry.length -or (Get-HermesCrashStreamHash $file) -cne $entry.sha256) { throw '5' }
        }
        return ,$pins
    } catch {
        foreach ($pin in $pins) { $pin.Stream.Dispose() }
        throw
    }
}

function Assert-HermesCrashPins {
    param([object]$Pins)
    foreach ($pin in $Pins) {
        Assert-HermesCrashPath $pin.Path
        if ($pin.Stream.SafeFileHandle.IsClosed -or $pin.Stream.Length -ne $pin.Entry.length `
            -or (Get-HermesCrashStreamHash $pin.Stream) -cne $pin.Entry.sha256) { throw '5' }
        $current = Get-HermesCrashFileEntry $pin.Path $pin.Entry.path
        if ($current.sha256 -cne $pin.Entry.sha256 -or $current.length -ne $pin.Entry.length) { throw '5' }
    }
}

function Close-HermesCrashPins {
    param([object]$Pins)
    foreach ($pin in $Pins) { $pin.Stream.Dispose() }
}

function Assert-HermesCrashTree {
    param([string]$Directory, [object[]]$Entries)
    Assert-HermesCrashPath $Directory
    $files = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    $directories = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $Entries) {
        Assert-HermesCrashRelativePath $entry.path
        Assert-HermesCrashHash $entry.sha256
        if (-not $files.TryAdd($entry.path, $entry)) { throw '6' }
        $parts = $entry.path.Split('/')
        for ($index = 1; $index -lt $parts.Length; $index++) {
            $null = $directories.Add(($parts[0..($index - 1)] -join '/'))
        }
    }
    if ($files.Count -eq 0) { throw '6' }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Directory)
    $count = 0
    while ($pending.Count -gt 0) {
        foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($pending.Pop())) {
            Assert-HermesCrashPath $path
            $relative = [IO.Path]::GetRelativePath($Directory, $path).Replace('\', '/')
            if ([IO.Directory]::Exists($path)) {
                if (-not $directories.Contains($relative)) { throw '6' }
                $pending.Push($path)
            } else {
                if (-not $files.ContainsKey($relative) -or $files[$relative].path -cne $relative) { throw '6' }
                $actual = Get-HermesCrashFileEntry $path $relative
                if ($actual.sha256 -cne $files[$relative].sha256 -or $actual.length -ne $files[$relative].length) { throw '6' }
                $count++
            }
        }
    }
    if ($count -ne $files.Count) { throw '6' }
}

function Read-HermesCrashInventory {
    param([string]$InventoryPath, [string]$Directory, [string[]]$SourceRoots)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString((Read-HermesCrashBytes $InventoryPath 16777216))
    if ($text[0] -eq [char]0xfeff) { $text = $text.Substring(1) }
    if ($text.EndsWith("`n", [StringComparison]::Ordinal)) { $text = $text.Substring(0, $text.Length - 1) }
    $bundle = [Collections.Generic.List[object]]::new()
    $sources = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in $text.Split("`n")) {
        $fields = $line.TrimEnd([char]13).Split('|')
        if ($fields.Length -ne 3) { throw '7' }
        $relative = $fields[1].Replace('\', '/')
        Assert-HermesCrashRelativePath $relative
        $hash = $fields[2].ToLowerInvariant()
        Assert-HermesCrashHash $hash
        if (-not [IO.Path]::IsPathFullyQualified($fields[0]) `
            -or $fields[0] -match '(^|[\\/])\.\.?([\\/]|$)') { throw '7' }
        $source = [IO.Path]::GetFullPath($fields[0])
        if ($source -cne $fields[0].Replace([IO.Path]::AltDirectorySeparatorChar, [IO.Path]::DirectorySeparatorChar)) { throw '7' }
        $approved = @($SourceRoots | Where-Object {
            $source.StartsWith([IO.Path]::GetFullPath($_).TrimEnd(
                [IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
        if (-not $approved) { throw '7' }
        $entry = Get-HermesCrashFileEntry $source $source
        if ($entry.sha256 -cne $hash) { throw '7' }
        if ($sources.ContainsKey($source) -and $sources[$source].path -cne $source) { throw '7' }
        $sources[$source] = $entry
        $bundle.Add([ordered]@{ path = $relative; sha256 = $hash; length = $entry.length })
    }
    Assert-HermesCrashTree $Directory $bundle.ToArray()
    return [pscustomobject]@{ Bundle = $bundle.ToArray(); Sources = @($sources.Values) }
}

function Invoke-HermesCrashGit {
    param([string]$Root, [string[]]$Arguments)
    $result = & git --no-optional-locks -C $Root @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) { throw '8' }
    return ($result -join "`n")
}

function Get-HermesCrashCheckout {
    param([string]$Root, [string]$ExpectedRevision)
    if ($ExpectedRevision -cnotmatch '\A[0-9a-f]{40}\z') { throw '8' }
    Assert-HermesCrashPath $Root
    $top = Invoke-HermesCrashGit $Root @('rev-parse', '--show-toplevel')
    if ([IO.Path]::GetFullPath($top) -cne $Root) { throw '8' }
    $revision = Invoke-HermesCrashGit $Root @('rev-parse', '--verify', 'HEAD')
    $tree = Invoke-HermesCrashGit $Root @('rev-parse', 'HEAD^{tree}')
    if ($revision -cne $ExpectedRevision -or $tree -cnotmatch '\A[0-9a-f]{40}\z') { throw '8' }
    $null = Invoke-HermesCrashGit $Root @('diff', '--quiet', 'HEAD', '--')
    # This closure includes all compiler inputs, linked Tray sources, publishers and build policy.
    $roots = @('installer/windows', 'tests/Hermes.Servicing.Acceptance',
        'tests/Hermes.Servicing.Acceptance.Model', 'tests/Hermes.Servicing.ProcessProbe',
        'src/Hermes.Servicing', 'src/Hermes.Tray/InstalledProofContract.cs', 'src/Hermes.Tray/OwnerFixture.cs',
        ':(top,glob)Directory.*', 'global.json', ':(top,glob)*[Nn]u[Gg]et.[Cc]onfig', '.config',
        '.github/workflows/startup-crash-control.yml')
    $unknown = Invoke-HermesCrashGit $Root (@('ls-files', '--others', '-z', '--') + $roots)
    if (@($unknown.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Where-Object {
        $_ -notmatch '(^|/)(bin|obj)/'
    }).Count -ne 0) { throw '8' }
    $tracked = Invoke-HermesCrashGit $Root (@('ls-files', '--cached', '-z', '--') + $roots)
    $paths = @($tracked.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries))
    if ($paths.Count -eq 0 -or @($paths | Where-Object {
        $_ -match '(^|/)\.env($|\.)' -or $_ -match '(^|/)(bin|obj)/'
    }).Count -ne 0) { throw '8' }
    $git = Join-Path $Root '.git'
    if (-not [IO.Directory]::Exists($git)) { throw '8' }
    $paths += '.git/HEAD', '.git/index'
    $head = [IO.File]::ReadAllText((Join-Path $git 'HEAD')).TrimEnd("`r", "`n")
    if ($head.StartsWith('ref: ', [StringComparison]::Ordinal)) {
        $reference = $head.Substring(5)
        Assert-HermesCrashRelativePath $reference
        if (-not $reference.StartsWith('refs/heads/', [StringComparison]::Ordinal)) { throw '8' }
        $refPath = Join-Path $git $reference
        if ([IO.File]::Exists($refPath)) { $paths += ".git/$reference" }
        else { $paths += '.git/packed-refs' }
    } elseif ($head -cne $revision) { throw '8' }
    $entries = @(foreach ($path in @($paths | Sort-Object -Unique -CaseSensitive)) {
        Assert-HermesCrashRelativePath $path
        Get-HermesCrashFileEntry (Join-Path $Root $path) $path
    })
    return [pscustomobject]@{
        Revision = $revision; Tree = $tree; Sources = $entries
        SourceSha256 = (Get-HermesCrashEntryDigest $entries)
    }
}

function Read-HermesCrashBytes {
    param([string]$Path, [int]$Maximum = 4096)
    Assert-HermesCrashPath $Path
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ($stream.Length -lt 1 -or $stream.Length -gt $Maximum) { throw '9' }
        $buffer = [byte[]]::new([int]$stream.Length)
        $stream.ReadExactly($buffer)
        if ($stream.ReadByte() -ne -1) { throw '9' }
        return ,$buffer
    } finally { $stream.Dispose() }
}

function Get-HermesCrashJsonProperties {
    param([Text.Json.JsonElement]$Element, [string[]]$Names)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw '10' }
    $properties = [Collections.Generic.Dictionary[string, Text.Json.JsonElement]]::new([StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if ($Names -cnotcontains $property.Name -or -not $properties.TryAdd($property.Name, $property.Value)) { throw '10' }
    }
    if ($properties.Count -ne $Names.Count) { throw '10' }
    return ,$properties
}

function Get-HermesCrashJsonUInt32 {
    param([Text.Json.JsonElement]$Element)
    [uint32]$value = 0
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Number `
        -or $Element.GetRawText() -cnotmatch '\A(0|[1-9][0-9]{0,9})\z' `
        -or -not $Element.TryGetUInt32([ref]$value)) { throw '10' }
    return $value
}

function Read-HermesCrashReceiptEntries {
    param([Text.Json.JsonElement]$Element, [switch]$Absolute)
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Array -or $Element.GetArrayLength() -lt 1 `
        -or $Element.GetArrayLength() -gt 32768) { throw '11' }
    $entries = @(foreach ($row in $Element.EnumerateArray()) {
        $properties = Get-HermesCrashJsonProperties $row @('path', 'sha256', 'length')
        if ($properties['path'].ValueKind -ne [Text.Json.JsonValueKind]::String `
            -or $properties['sha256'].ValueKind -ne [Text.Json.JsonValueKind]::String) { throw '11' }
        $path = $properties['path'].GetString()
        if ($Absolute) {
            if (-not [IO.Path]::IsPathFullyQualified($path) -or [IO.Path]::GetFullPath($path) -cne $path) { throw '11' }
        } else { Assert-HermesCrashRelativePath $path }
        $hash = $properties['sha256'].GetString()
        Assert-HermesCrashHash $hash
        [ordered]@{ path = $path; sha256 = $hash; length = (Get-HermesCrashJsonUInt32 $properties['length']) }
    })
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $entries) { if (-not $seen.Add($entry.path)) { throw '11' } }
    return ,$entries
}

function Read-HermesCrashReceipt {
    param([string]$Path)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString((Read-HermesCrashBytes $Path 16777216))
    $document = [Text.Json.JsonDocument]::Parse($text)
    try {
        $properties = Get-HermesCrashJsonProperties $document.RootElement @(
            'schemaVersion', 'revision', 'tree', 'sourceSha256', 'bundleSha256', 'sources', 'bundle', 'sdkSources')
        if ((Get-HermesCrashJsonUInt32 $properties['schemaVersion']) -ne 1) { throw '11' }
        $receipt = [ordered]@{ schemaVersion = 1 }
        foreach ($name in @('revision', 'tree', 'sourceSha256', 'bundleSha256')) {
            if ($properties[$name].ValueKind -ne [Text.Json.JsonValueKind]::String) { throw '11' }
            $receipt[$name] = $properties[$name].GetString()
        }
        if ($receipt.revision -cnotmatch '\A[0-9a-f]{40}\z' -or $receipt.tree -cnotmatch '\A[0-9a-f]{40}\z') { throw '11' }
        Assert-HermesCrashHash $receipt.sourceSha256
        Assert-HermesCrashHash $receipt.bundleSha256
        $receipt.sources = Read-HermesCrashReceiptEntries $properties['sources']
        $receipt.bundle = Read-HermesCrashReceiptEntries $properties['bundle']
        $receipt.sdkSources = Read-HermesCrashReceiptEntries $properties['sdkSources'] -Absolute
        if ((Get-HermesCrashEntryDigest $receipt.sources) -cne $receipt.sourceSha256 `
            -or (Get-HermesCrashEntryDigest $receipt.bundle) -cne $receipt.bundleSha256) { throw '11' }
        return $receipt
    } finally { $document.Dispose() }
}

function Assert-HermesCrashReceiptBinding {
    param([Collections.IDictionary]$Receipt, [object]$Checkout)
    if ($Receipt.revision -cne $Checkout.Revision -or $Receipt.tree -cne $Checkout.Tree `
        -or $Receipt.sourceSha256 -cne $Checkout.SourceSha256 `
        -or (Get-HermesCrashEntryDigest $Receipt.sources) -cne $Checkout.SourceSha256) { throw '11' }
}

function Assert-HermesCrashReport {
    param([byte[]]$Bytes, [string]$ImageSha256, [string]$SourceSha256)
    Assert-HermesCrashHash $ImageSha256
    Assert-HermesCrashHash $SourceSha256
    if ($Bytes.Length -lt 1 -or $Bytes.Length -gt 4096) { throw '12' }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes)
    $document = [Text.Json.JsonDocument]::Parse($text)
    try {
        $ones = @('schemaVersion', 'started', 'ready', 'identity', 'armAttempted', 'armed',
            'exited', 'held', 'cleanup', 'waitSettled', 'drainsSettled', 'writeSettled',
            'callbackSettled', 'stdoutComplete', 'stderrComplete', 'outerSupervisionRequired')
        $zeros = @('originalGatePassed', 'nativeAcceptance', 'status', 'synchronousCallsInterruptible',
            'deadline', 'cancelled', 'error', 'cleanupError', 'reportError')
        $properties = Get-HermesCrashJsonProperties $document.RootElement (
            $ones + $zeros + @('exit', 'imageSha256', 'sourceSha256'))
        foreach ($name in $ones) {
            if ((Get-HermesCrashJsonUInt32 $properties[$name]) -ne 1) { throw '12' }
        }
        foreach ($name in $zeros) {
            if ((Get-HermesCrashJsonUInt32 $properties[$name]) -ne 0) { throw '12' }
        }
        $exitCode = Get-HermesCrashJsonUInt32 $properties['exit']
        # Includes COR_E_FAILFAST for the fixed .NET 9 Environment.FailFast child.
        if ($exitCode -notin @(0x80131506L, 0x80131623L, 0xc0000409L, 0xc0000602L, 0xe0434352L)) { throw '12' }
        foreach ($name in @('imageSha256', 'sourceSha256')) {
            if ($properties[$name].ValueKind -ne [Text.Json.JsonValueKind]::String) { throw '12' }
            Assert-HermesCrashHash $properties[$name].GetString()
        }
        if ($properties['imageSha256'].GetString() -cne $ImageSha256 `
            -or $properties['sourceSha256'].GetString() -cne $SourceSha256) { throw '12' }
        return [pscustomobject]@{ Exit = $exitCode; ImageSha256 = $ImageSha256; SourceSha256 = $SourceSha256 }
    } finally { $document.Dispose() }
}

function New-HermesCrashRead {
    param([IO.Stream]$Stream, [int]$Capacity, [Collections.Generic.List[Threading.Tasks.Task]]$Operations)
    $buffer = [byte[]]::new($Capacity)
    $task = $Stream.ReadAsync($buffer, 0, $Capacity)
    $tasks = [Collections.Generic.List[Threading.Tasks.Task]]::new()
    $tasks.Add($task)
    if ($null -ne $Operations) { $Operations.Add($task) }
    return @{
        Stream = $Stream; Buffer = $buffer; Count = 0; End = $false
        Task = $task; Tasks = $tasks; Operations = $Operations
    }
}

function Update-HermesCrashRead {
    param([Collections.IDictionary]$Read)
    if ($Read.End -or -not $Read.Task.IsCompleted) { return }
    $count = $Read.Task.GetAwaiter().GetResult()
    if ($count -eq 0) { $Read.End = $true; return }
    $Read.Count += $count
    # The last byte is an overflow sentinel. Never schedule another read at the cap.
    if ($Read.Count -ge $Read.Buffer.Length) { throw '13' }
    $Read.Task = $Read.Stream.ReadAsync($Read.Buffer, $Read.Count, $Read.Buffer.Length - $Read.Count)
    $Read.Tasks.Add($Read.Task)
    if ($null -ne $Read.Operations) { $Read.Operations.Add($Read.Task) }
}

function Invoke-HermesCrashCapture {
    param(
        [Diagnostics.Process]$Process,
        [int]$DeadlineMilliseconds = 30000,
        [Collections.Generic.List[Threading.Tasks.Task]]$Operations
    )
    if ($DeadlineMilliseconds -lt 1 -or $DeadlineMilliseconds -gt 30000) { throw '14' }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $wait = $Process.WaitForExitAsync()
    if ($null -ne $Operations) { $Operations.Add($wait) }
    $output = New-HermesCrashRead $Process.StandardOutput.BaseStream 512 $Operations
    $errorOutput = New-HermesCrashRead $Process.StandardError.BaseStream 1 $Operations
    while ($true) {
        if ($clock.ElapsedMilliseconds -ge $DeadlineMilliseconds) { throw '14' }
        Update-HermesCrashRead $output
        Update-HermesCrashRead $errorOutput
        if ($wait.IsCompleted -and $output.End -and $errorOutput.End) { break }
        [Threading.Thread]::Sleep(5)
    }
    # Retain and join the actual operations, not a racing delay/cancellation proxy.
    $tasks = @($wait) + @($output.Tasks.ToArray()) + @($errorOutput.Tasks.ToArray())
    foreach ($task in $tasks) {
        if ($task.Status -ne [Threading.Tasks.TaskStatus]::RanToCompletion) { throw '15' }
        $null = $task.GetAwaiter().GetResult()
    }
    if ($clock.ElapsedMilliseconds -ge $DeadlineMilliseconds -or -not $Process.HasExited) { throw '14' }
    $bytes = [byte[]]::new($output.Count)
    [Array]::Copy($output.Buffer, $bytes, $output.Count)
    [Array]::Clear($output.Buffer)
    [Array]::Clear($errorOutput.Buffer)
    return [pscustomobject]@{
        ExitCode = $Process.ExitCode; Deadline = 0; StdoutComplete = 1; StderrComplete = 1
        WaitSettled = 1; Stdout = $bytes; Stderr = [byte[]]::new(0); Tasks = $tasks
    }
}

function Assert-HermesCrashOutcome {
    param([object]$Capture, [object]$Report)
    if ($Capture.ExitCode -ne 0 -or $Capture.Deadline -ne 0 -or $Capture.StdoutComplete -ne 1 `
        -or $Capture.StderrComplete -ne 1 -or $Capture.WaitSettled -ne 1 -or $Capture.Stderr.Length -ne 0 `
        -or $Capture.Stdout.Length -gt 511 -or $Capture.Tasks.Count -lt 3) { throw '16' }
    foreach ($task in $Capture.Tasks) {
        if ($task.Status -ne [Threading.Tasks.TaskStatus]::RanToCompletion) { throw '16' }
        $null = $task.GetAwaiter().GetResult()
    }
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($Capture.Stdout)
    $expected = "HERMES_CRASH_CONTROL v=1 status=0 exit=$($Report.Exit) error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n"
    if ($text -cne $expected) { throw '16' }
}

function ConvertTo-HermesCrashControlFacts {
    param([object]$Capture)
    $unavailable = 'HERMES_CRASH_CONTROL_FACTS v=1 available=0 status=0 exit=0 error=0 cleanupError=0 reportError=0 nativeAcceptance=0'
    try {
        foreach ($name in @('ExitCode', 'Deadline', 'StdoutComplete', 'StderrComplete', 'WaitSettled')) {
            if ($Capture.$name -isnot [int]) { return $unavailable }
        }
        if ($Capture.ExitCode -notin @(0, 1) -or $Capture.Deadline -ne 0 `
            -or $Capture.StdoutComplete -ne 1 -or $Capture.StderrComplete -ne 1 -or $Capture.WaitSettled -ne 1 `
            -or $Capture.Stdout -isnot [byte[]] -or $Capture.Stdout.Length -lt 1 -or $Capture.Stdout.Length -gt 511 `
            -or $Capture.Stderr -isnot [byte[]] -or $Capture.Stderr.Length -ne 0 `
            -or $Capture.Tasks -isnot [object[]] -or $Capture.Tasks.Count -lt 3 -or $Capture.Tasks.Count -gt 514) {
            return $unavailable
        }
        foreach ($operation in $Capture.Tasks) {
            if ($operation -isnot [Threading.Tasks.Task] `
                -or $operation.Status -ne [Threading.Tasks.TaskStatus]::RanToCompletion) { return $unavailable }
            $null = $operation.GetAwaiter().GetResult()
        }
        $text = [Text.UTF8Encoding]::new($false, $true).GetString($Capture.Stdout)
        $uint = '(0|[1-9][0-9]{0,9})'
        $match = [regex]::Match($text, (
            "\AHERMES_CRASH_CONTROL v=1 status=([01]) exit=$uint error=$uint cleanupError=$uint reportError=$uint nativeAcceptance=0\n\z"))
        if (-not $match.Success) { return $unavailable }
        $values = [uint32[]]::new(5)
        for ($index = 0; $index -lt $values.Length; $index++) {
            [uint32]$value = 0
            if (-not [uint32]::TryParse($match.Groups[$index + 1].Value,
                    [Globalization.NumberStyles]::None, [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
                return $unavailable
            }
            $values[$index] = $value
        }
        if ($values[0] -ne $Capture.ExitCode -or $values[2] -gt 11 `
            -or $values[3] -gt 255 -or $values[4] -gt 1) { return $unavailable }
        # Advisory observations only: reconstruct numeric fields, never echo the captured line.
        return [string]::Format([Globalization.CultureInfo]::InvariantCulture,
            'HERMES_CRASH_CONTROL_FACTS v=1 available=1 status={0} exit={1} error={2} cleanupError={3} reportError={4} nativeAcceptance=0',
            [object[]]$values)
    } catch { return $unavailable }
}

function Assert-HermesCrashReportAbsent {
    param([string]$Workspace)
    $artifacts = Join-Path $Workspace '.artifacts'
    New-HermesCrashDirectory $artifacts
    $evidence = Join-Path $artifacts 'installer-evidence'
    New-HermesCrashDirectory $evidence
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($evidence)) {
        if ([IO.Path]::GetFileName($entry).Equals('startup-crash-control.json', [StringComparison]::OrdinalIgnoreCase)) {
            throw '17'
        }
    }
}

function Get-HermesCrashAssemblySet {
    return @('Hermes.Servicing', 'Hermes.Installer.Control', 'Hermes.Setup',
        'Hermes.Servicing.Acceptance.Model', 'Hermes.Servicing.Acceptance')
}

function Assert-HermesCrashAssemblies {
    param([string]$Directory, [object[]]$Bundle)
    $names = Get-HermesCrashAssemblySet
    $loaded = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
        $_.GetName().Name.StartsWith('Hermes.', [StringComparison]::OrdinalIgnoreCase)
    })
    if ($loaded.Count -ne $names.Count) { throw '18' }
    foreach ($assembly in $loaded) {
        $name = $assembly.GetName().Name
        if ($names -cnotcontains $name -or $assembly.IsDynamic) { throw '18' }
        $path = Join-Path $Directory "$name.dll"
        $entries = @($Bundle | Where-Object { $_.path -ceq "$name.dll" })
        if ($entries.Count -ne 1 -or $assembly.Location -cne $path `
            -or $assembly.ManifestModule.ModuleVersionId.ToString('D') -cne (Get-HermesManagedModuleMvid $path) `
            -or (Get-HermesCrashFileEntry $path "$name.dll").sha256 -cne $entries[0].sha256) { throw '18' }
    }
}

function Import-HermesCrashSupervisor {
    param([string]$Directory, [object[]]$Bundle)
    if ([Environment]::Version.Major -lt 9 -or @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
        $_.GetName().Name.StartsWith('Hermes.', [StringComparison]::OrdinalIgnoreCase)
    }).Count -ne 0) { throw '18' }
    Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force -DisableNameChecking
    foreach ($name in (Get-HermesCrashAssemblySet)) {
        $null = [Runtime.Loader.AssemblyLoadContext]::Default.LoadFromAssemblyPath((Join-Path $Directory "$name.dll"))
    }
    Assert-HermesCrashAssemblies $Directory $Bundle
    $assembly = @([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object {
        $_.GetName().Name -ceq 'Hermes.Servicing.Acceptance'
    })[0]
    $type = $assembly.GetType('Hermes.Servicing.Acceptance.WindowsCrashSupervisor', $true, $false)
    $method = $type.GetMethod('BeginCurrentStep', [Reflection.BindingFlags]'Public,Static', [Type]::EmptyTypes)
    if ($null -eq $method -or -not $method.ReturnType.IsSealed `
        -or $method.ReturnType.FullName -cne 'Hermes.Servicing.Acceptance.WindowsCrashStepScope' `
        -or $method.ReturnType.Assembly -ne $assembly) { throw '18' }
    return $method
}

Export-ModuleMember -Function Assert-HermesCrashHost, Assert-HermesCrashHash, Assert-HermesCrashPath,
    New-HermesCrashDirectory, Assert-HermesCrashRelativePath, Get-HermesCrashFileEntry,
    Get-HermesCrashEntryDigest, Open-HermesCrashPins, Assert-HermesCrashPins, Close-HermesCrashPins,
    Assert-HermesCrashTree, Read-HermesCrashInventory, Get-HermesCrashCheckout, Read-HermesCrashBytes,
    Read-HermesCrashReceipt, Assert-HermesCrashReceiptBinding, Assert-HermesCrashReport, Invoke-HermesCrashCapture,
    Assert-HermesCrashOutcome, Assert-HermesCrashReportAbsent, Import-HermesCrashSupervisor,
    Assert-HermesCrashAssemblies, ConvertTo-HermesCrashDiagnostic, ConvertTo-HermesCrashControlFacts
