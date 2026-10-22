#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('controls', 'fixture')][string]$Mode = 'controls',
    [ValidateSet('valid', 'noise', 'contradiction', 'crlf', 'utf8', 'stderr', 'exit', 'overflow', 'timeout')]
    [string]$Fixture = 'valid'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Mode -ceq 'fixture') {
    $row = "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734214 error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n"
    if ($Fixture -ceq 'timeout') { [Threading.Thread]::Sleep(30000); exit 1 }
    if ($Fixture -ceq 'noise') { $row = "noise`n$row" }
    if ($Fixture -ceq 'contradiction') { $row = $row.Replace('exit=2148734214', 'exit=3221226505') }
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
    if ($Fixture -ceq 'exit') { exit 1 }
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
function Invoke-ControlFixture {
    param([string]$Name, [bool]$Accepted)
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
    $imageHash = 'a' * 64
    $sourceHash = 'b' * 64
    $shape = [ordered]@{
        schemaVersion = 1; originalGatePassed = 0; nativeAcceptance = 0; status = 0
        started = 1; ready = 1; identity = 1; armAttempted = 1; armed = 1; exited = 1
        held = 1; cleanup = 1; waitSettled = 1; drainsSettled = 1; writeSettled = 1
        callbackSettled = 1; stdoutComplete = 1; stderrComplete = 1
        outerSupervisionRequired = 1; synchronousCallsInterruptible = 0
        deadline = 0; cancelled = 0; exit = 2148734214L; error = 0; cleanupError = 0; reportError = 0
        imageSha256 = $imageHash; sourceSha256 = $sourceHash
    }
    $good = ConvertTo-ControlBytes $shape
    $script:validReport = Assert-HermesCrashReport $good $imageHash $sourceHash
    Assert-Control ($shape.Count -eq 28 -and $script:validReport.Exit -eq 2148734214L)
    foreach ($name in @($shape.Keys | Where-Object { $_ -notin @('exit', 'imageSha256', 'sourceSha256') })) {
        $saved = $shape[$name]
        $shape[$name] = 1 - $saved
        Assert-Refused { Assert-HermesCrashReport (ConvertTo-ControlBytes $shape) $imageHash $sourceHash }
        $shape[$name] = $saved
    }
    foreach ($code in @(0x80131506L, 0xc0000409L, 0xc0000602L, 0xe0434352L)) {
        $shape.exit = $code
        Assert-Control ((Assert-HermesCrashReport (ConvertTo-ControlBytes $shape) $imageHash $sourceHash).Exit -eq $code)
    }
    foreach ($code in @(0, 1, 259, 4294967295L, 4294967296L, -1, 1.0, '2148734214', $true, $null)) {
        $shape.exit = $code
        Assert-Refused { Assert-HermesCrashReport (ConvertTo-ControlBytes $shape) $imageHash $sourceHash }
    }
    $shape.exit = 2148734214L
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
            "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734214 error=0 cleanupError=0 reportError=0 nativeAcceptance=0`n")
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
    if (-not [OperatingSystem]::IsWindows()) { Assert-Refused { Assert-HermesCrashHost } }
    Assert-HermesCrashReportAbsent $scratch
    $existingReport = Join-Path $scratch '.artifacts/installer-evidence/startup-crash-control.json'
    [IO.File]::WriteAllBytes($existingReport, $good)
    Assert-Refused { Assert-HermesCrashReportAbsent $scratch }
    [IO.File]::Delete($existingReport)
    [IO.Directory]::CreateDirectory($existingReport) | Out-Null
    Assert-Refused { Assert-HermesCrashReportAbsent $scratch }
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
[Console]::Out.WriteLine("HERMES_CRASH_TESTS v=1 passed=$script:passed failed=$script:failed")
if ($script:failed -ne 0) { exit 1 }
exit 0
