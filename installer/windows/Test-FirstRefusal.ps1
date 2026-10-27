#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'First-Refusal.psm1') -Force

& (Get-Module First-Refusal) {
    $script:checks = 0
    function Require([bool]$Condition) {
        $script:checks++
        if (-not $Condition) { throw "First-refusal control $script:checks failed." }
    }
    function Refuses([scriptblock]$Action) {
        try { $null = & $Action; return $false } catch { return $true }
    }
    function Unprotect-Synthetic {
        param([byte[]]$Bytes, $Context, [Security.Cryptography.RSA]$Rsa)
        $parts = Read-HermesFirstRefusalEnvelope $Bytes $Context
        $key = $null; $plain = $null; $aes = $null
        try {
            $key = $Rsa.Decrypt($parts.WrappedKey, [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
            $plain = [byte[]]::new($parts.Ciphertext.Length)
            $aes = [Security.Cryptography.AesGcm]::new($key, 16)
            $aes.Decrypt($parts.Nonce, $parts.Ciphertext, $parts.Tag, $plain, $parts.Aad)
            return [Text.UTF8Encoding]::new($false, $true).GetString($plain)
        } finally {
            if ($null -ne $key) { [Array]::Clear($key) }
            if ($null -ne $plain) { [Array]::Clear($plain) }
            if ($null -ne $aes) { $aes.Dispose() }
        }
    }
    $rsa = [Security.Cryptography.RSA]::Create(3072)
    $wrong = [Security.Cryptography.RSA]::Create(3072)
    $pinned = Get-FirstRefusalPublicKey
    $originalSource = (Get-Command Assert-FirstRefusalSource).ScriptBlock
    $originalKey = (Get-Command Get-FirstRefusalPublicKey).ScriptBlock
    $originalProtect = (Get-Command Protect-FirstRefusalLine).ScriptBlock
    $originalNew = (Get-Command New-HermesFirstRefusalCapture).ScriptBlock
    $originalHost = (Get-Command Assert-FirstRefusalManualContext).ScriptBlock
    $originalCheckout = (Get-Command Get-HermesCrashCheckout).ScriptBlock
    $originalClose = (Get-Command Close-HermesCrashPins).ScriptBlock
    $testRoot = Join-Path $PSScriptRoot ('obj/first-refusal-controls/' + [Guid]::NewGuid().ToString('N'))
    try {
        Require ($pinned.KeySize -eq 3072)
        Require ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            $pinned.ExportSubjectPublicKeyInfo())).ToLowerInvariant() -ceq
            'aecc0abe72feb788a2e892b17d04644891f48dec865a495a8126035233ad5cbd')
        $context = [ordered]@{
            key = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
                $rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
            source = 'b' * 64; revision = 'c' * 40; run = '123'; attempt = '1'
        }
        foreach ($line in @('SYNTHETIC first refused debugger line', ('a' * 1024),
                ('é' * 512), ('😀' * 256), " `tSYNTHETIC .printf literal \n `t ")) {
            $bytes = Protect-FirstRefusalLine $line $rsa $context
            Require ($bytes.Length -le 4096 -and (Unprotect-Synthetic $bytes $context $rsa) -ceq $line)
            Require (-not [Text.Encoding]::UTF8.GetString($bytes).Contains($line, [StringComparison]::Ordinal))
            $again = Protect-FirstRefusalLine $line $rsa $context
            Require (-not [Linq.Enumerable]::SequenceEqual[byte]($bytes, $again))
        }
        foreach ($line in @(('a' * 1025), ('é' * 513), ('😀' * 257), '',
                "SYNTHETIC`nsecond", "SYNTHETIC`rsecond", ([string][char]0xd800))) {
            Require (Refuses { Protect-FirstRefusalLine $line $rsa $context })
        }
        $bytes = Protect-FirstRefusalLine 'SYNTHETIC authenticated line' $rsa $context
        Require (Refuses { Unprotect-Synthetic $bytes $context $wrong })
        foreach ($field in @('key', 'source', 'revision', 'run', 'attempt')) {
            $other = [ordered]@{}
            foreach ($name in $context.Keys) { $other[$name] = $context[$name] }
            $other[$field] = switch ($field) { key { 'd' * 64 }; source { 'e' * 64 }; revision { 'f' * 40 }; default { '2' } }
            Require (Refuses { Read-HermesFirstRefusalEnvelope $bytes $other })
            if ($field -ceq 'key') { Require (Refuses { Protect-FirstRefusalLine 'SYNTHETIC' $rsa $other }) }
        }
        $wrappedOffset = 12 + (Get-HermesFirstRefusalHeader $context).Length
        foreach ($offset in @(0, 7, 8, 9, 10, 11, 12, 42, ($bytes.Length - 1),
                $wrappedOffset, ($wrappedOffset + 384), ($wrappedOffset + 396), ($wrappedOffset + 412))) {
            $tampered = [byte[]]$bytes.Clone()
            $tampered[$offset] = $tampered[$offset] -bxor 1
            Require (Refuses { Unprotect-Synthetic $tampered $context $rsa })
        }
        foreach ($malformed in @([byte[]]::new(0), [byte[]]::new(4097),
                [byte[]]$bytes[0..($bytes.Length - 2)], [byte[]]($bytes + [byte]0))) {
            Require (Refuses { Read-HermesFirstRefusalEnvelope $malformed $context })
        }
        foreach ($lengths in @(@(513, 1), @(1, 1025), @(0, 1), @(1, 0))) {
            $malformed = [byte[]]$bytes.Clone()
            $malformed[8] = [byte]($lengths[0] -shr 8); $malformed[9] = [byte]($lengths[0] -band 255)
            $malformed[10] = [byte]($lengths[1] -shr 8); $malformed[11] = [byte]($lengths[1] -band 255)
            Require (Refuses { Read-HermesFirstRefusalEnvelope $malformed $context })
        }
        $extraHeader = [Text.Encoding]::ASCII.GetBytes("extra=1`n")
        $malformed = [byte[]]($bytes[0..($wrappedOffset - 1)] + $extraHeader + $bytes[$wrappedOffset..($bytes.Length - 1)])
        $headerLength = $wrappedOffset - 12 + $extraHeader.Length
        $malformed[8] = [byte]($headerLength -shr 8); $malformed[9] = [byte]($headerLength -band 255)
        Require (Refuses { Read-HermesFirstRefusalEnvelope $malformed $context })
        # AAD has a closed canonical grammar, not JSON with permissive fields.
        foreach ($extra in @("x=extra`n", "v=2`n", "job=other`n")) {
            $other = [ordered]@{}
            foreach ($name in $context.Keys) { $other[$name] = $context[$name] }
            $other.run += $extra
            Require (Refuses { Get-HermesFirstRefusalHeader $other })
        }
        $extraContext = [ordered]@{}
        foreach ($name in $context.Keys) { $extraContext[$name] = $context[$name] }
        $extraContext.extra = 'SYNTHETIC'
        Require (Refuses { Get-HermesFirstRefusalHeader $extraContext })

        # Instrument the actual implementation in memory, retaining only its
        # mutable buffers to prove both successful and exceptional finally paths.
        foreach ($fail in @($false, $true)) {
            $instrumented = $originalProtect.ToString().Replace(
                '$plain = $encoding.GetBytes($Line)', '$plain = $encoding.GetBytes($Line); $script:ownedPlain = $plain').Replace(
                '$key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)',
                '$key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32); $script:ownedKey = $key')
            if ($fail) { $instrumented = $instrumented.Replace('$nonce = ', 'throw ''7''; $nonce = ') }
            Set-Item Function:script:Protect-FirstRefusalLine ([scriptblock]::Create($instrumented))
            $failed = Refuses { Protect-FirstRefusalLine 'SYNTHETIC buffer clearing' $rsa $context }
            Require ($failed -eq $fail)
            Require (@($script:ownedPlain | Where-Object { $_ -ne 0 }).Count -eq 0)
            Require (@($script:ownedKey | Where-Object { $_ -ne 0 }).Count -eq 0)
        }
        Set-Item Function:script:Protect-FirstRefusalLine $originalProtect

        # Only the ownership/key acquisition boundary is faked. Encryption,
        # one-attempt state, file creation and validation remain the real code.
        Set-Item Function:script:Assert-FirstRefusalSource { param($Capture) }
        $script:syntheticPublic = $rsa.ExportSubjectPublicKeyInfo()
        Set-Item Function:script:Get-FirstRefusalPublicKey {
            $public = [Security.Cryptography.RSA]::Create()
            $read = 0
            $public.ImportSubjectPublicKeyInfo($script:syntheticPublic, [ref]$read)
            return $public
        }
        foreach ($line in @('SYNTHETIC first line', ('a' * 1025), "SYNTHETIC`nsecond",
                ([string][char]0xd800), ('é' * 513))) {
            $capture = @{ Status = 1; Count = 0; Bytes = $null; Pins = $null; Context = $context; Root = $testRoot }
            $output = @(Invoke-HermesFirstRefusalCapture $capture $line)
            $expected = if ($line -ceq 'SYNTHETIC first line') { 2 } elseif ($line.Length -gt 1024 -or $line -ceq ('é' * 513)) { 5 } else { 6 }
            Require ($output.Count -eq 0 -and $capture.Status -eq $expected -and $capture.Count -eq 1)
            $first = $capture.Bytes
            Invoke-HermesFirstRefusalCapture $capture 'SYNTHETIC later line'
            Require ($capture.Count -eq 1 -and $capture.Status -eq $expected -and [object]::ReferenceEquals($first, $capture.Bytes))
            if ($expected -eq 2) { Require ((Unprotect-Synthetic $capture.Bytes $context $rsa) -ceq $line) }
            Complete-HermesFirstRefusalCapture $capture $false
            Require ($null -eq $capture.Bytes -and $null -eq $capture.Pins -and $capture.Status -ne 3)
        }
        foreach ($fault in @('source', 'crypto')) {
            if ($fault -ceq 'source') {
                Set-Item Function:script:Assert-FirstRefusalSource { param($Capture) throw 'SYNTHETIC private exception' }
            } else {
                Set-Item Function:script:Assert-FirstRefusalSource { param($Capture) }
                Set-Item Function:script:Protect-FirstRefusalLine { throw 'SYNTHETIC private exception' }
            }
            $capture = @{ Status = 1; Count = 0; Bytes = $null; Pins = $null; Context = $context; Root = $testRoot }
            $output = @(Invoke-HermesFirstRefusalCapture $capture 'SYNTHETIC first line')
            Require ($output.Count -eq 0 -and $capture.Status -eq 7 -and $capture.Count -eq 1 -and $null -eq $capture.Bytes)
            Invoke-HermesFirstRefusalCapture $capture 'SYNTHETIC later line'
            Require ($capture.Count -eq 1 -and $capture.Status -eq 7)
        }
        Set-Item Function:script:Assert-FirstRefusalSource { param($Capture) }
        Set-Item Function:script:Protect-FirstRefusalLine $originalProtect

        $directory = Join-Path $testRoot '.artifacts/installer-evidence'
        $null = [IO.Directory]::CreateDirectory($directory)
        $path = Join-Path $directory 'startup-first-refusal.enc'
        $capture = @{ Status = 1; Count = 0; Bytes = $null; Pins = $null; Context = $context; Root = $testRoot }
        Invoke-HermesFirstRefusalCapture $capture 'SYNTHETIC disk ciphertext'
        Complete-HermesFirstRefusalCapture $capture $true
        Require ($capture.Status -eq 3 -and $capture.Count -eq 1 -and $null -eq $capture.Bytes)
        $disk = [IO.File]::ReadAllBytes($path)
        Require ((Unprotect-Synthetic $disk $context $rsa) -ceq 'SYNTHETIC disk ciphertext')
        Require (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReadOnly) -ne 0)
        $capture.Status = 1; $capture.Count = 0
        Invoke-HermesFirstRefusalCapture $capture 'SYNTHETIC must not overwrite'
        Complete-HermesFirstRefusalCapture $capture $true
        Require ($capture.Status -eq 8 -and [Linq.Enumerable]::SequenceEqual[byte]($disk, [IO.File]::ReadAllBytes($path)))

        $script:artifactCapture = @{ Status = 1; Count = 0; Bytes = $null; Pins = $null; Context = $context; Root = $testRoot }
        Set-Item Function:script:New-HermesFirstRefusalCapture { return $script:artifactCapture }
        $reportPath = Join-Path $directory 'startup-debug.json'
        $report = @{ firstRefusalStatus = 3; firstRefusalCount = 1; completed = 0; cleanupFault = 0
            observations = @{ ParserProtocol = 1; scopedReadFirstKind = 1 } }
        [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Compress))
        $before = [IO.File]::GetLastWriteTimeUtc($path)
        $output = @(Assert-HermesFirstRefusalArtifact)
        Require ($output.Count -eq 0 -and [IO.File]::GetLastWriteTimeUtc($path) -eq $before)
        function script:Close-HermesCrashPins {
            param($Pins)
            Crash-Control\Close-HermesCrashPins $Pins
            if (@($Pins).Count -eq 0) { throw 'SYNTHETIC settlement failure' }
        }
        try {
            $script:artifactCapture.Pins = @()
            Require (Refuses { Assert-HermesFirstRefusalArtifact })
            Require ($script:artifactCapture.Status -eq 8 -and $null -eq $script:artifactCapture.Pins)
        } finally { Set-Item Function:script:Close-HermesCrashPins $originalClose }
        $script:artifactCapture.Status = 1
        foreach ($status in @(1, 2, 4, 5, 6, 7, 8)) {
            $report.firstRefusalStatus = $status
            [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Compress))
            Require (Refuses { Assert-HermesFirstRefusalArtifact })
        }
        $report.firstRefusalStatus = 3
        [IO.File]::WriteAllText($reportPath, ($report | ConvertTo-Json -Compress))
        [IO.File]::SetAttributes($path, [IO.FileAttributes]::Normal)
        Require (Refuses { Assert-HermesFirstRefusalArtifact })
        [IO.File]::Delete($path)
        $link = Join-Path $testRoot 'link'
        if ([OperatingSystem]::IsWindows()) {
            $null = New-Item -ItemType Junction -Path $link -Target $directory
        } else { $null = [IO.Directory]::CreateSymbolicLink($link, $directory) }
        Require (Refuses { Assert-HermesCrashPath $link })
        $linkedRoot = Join-Path $testRoot 'linked-root'
        $null = [IO.Directory]::CreateDirectory((Join-Path $linkedRoot '.artifacts'))
        $linkedDirectory = Join-Path $linkedRoot '.artifacts/installer-evidence'
        if ([OperatingSystem]::IsWindows()) {
            $null = New-Item -ItemType Junction -Path $linkedDirectory -Target $directory
        } else { $null = [IO.Directory]::CreateSymbolicLink($linkedDirectory, $directory) }
        $capture.Root = $linkedRoot; $capture.Status = 1; $capture.Count = 0
        Invoke-HermesFirstRefusalCapture $capture 'SYNTHETIC refused reparse'
        Complete-HermesFirstRefusalCapture $capture $true
        Require ($capture.Status -eq 8 -and -not [IO.File]::Exists($path))
        [IO.Directory]::Delete($linkedDirectory)
        [IO.Directory]::Delete($link)

        Set-Item Function:script:New-HermesFirstRefusalCapture $originalNew
        Set-Item Function:script:Assert-FirstRefusalSource $originalSource
        Set-Item Function:script:Get-FirstRefusalPublicKey $originalKey
        $script:sourceRoot = $testRoot
        $script:sourceFacts = @{ GITHUB_SHA = 'c' * 40; GITHUB_RUN_ID = '123'; GITHUB_RUN_ATTEMPT = '1' }
        $null = [IO.Directory]::CreateDirectory((Join-Path $testRoot '.github/workflows'))
        [IO.File]::WriteAllText((Join-Path $testRoot '.github/workflows/ci.yml'), 'SYNTHETIC fixed CI')
        [IO.File]::WriteAllText((Join-Path $testRoot 'source.ps1'), 'SYNTHETIC fixed source')
        function script:Get-HermesCrashCheckout {
            param($Root, $ExpectedRevision)
            return @{ Revision = $script:sourceFacts.GITHUB_SHA; Sources = @(
                Get-HermesCrashFileEntry (Join-Path $Root 'source.ps1') 'source.ps1') }
        }
        Set-Item Function:script:Assert-FirstRefusalManualContext { return $script:sourceRoot }
        Set-Item Function:script:New-HermesFirstRefusalCapture (
            [scriptblock]::Create($originalNew.ToString().Replace('$env:', '$script:sourceFacts.')))
        Set-Item Function:script:Assert-FirstRefusalSource (
            [scriptblock]::Create($originalSource.ToString().Replace('$env:', '$script:sourceFacts.')))
        $capture = New-HermesFirstRefusalCapture
        try {
            Require ($capture.Status -eq 1 -and $capture.Count -eq 0 -and $capture.Pins.Count -eq 2)
            $output = @(Assert-FirstRefusalSource $capture)
            Require ($output.Count -eq 0)
            foreach ($field in @('source', 'key', 'revision', 'run', 'attempt')) {
                $original = $capture.Context[$field]; $capture.Context[$field] = 'invalid'
                Require (Refuses { Assert-FirstRefusalSource $capture })
                $capture.Context[$field] = $original
            }
            $original = $script:sourceRoot; $script:sourceRoot += '-other'
            Require (Refuses { Assert-FirstRefusalSource $capture })
            $script:sourceRoot = $original
            $original = $capture.Pins[0].Entry.sha256; $capture.Pins[0].Entry.sha256 = 'f' * 64
            Require (Refuses { Assert-FirstRefusalSource $capture })
            $capture.Pins[0].Entry.sha256 = $original
        } finally { Complete-HermesFirstRefusalCapture $capture $false }
        Require ($null -eq $capture.Pins -and -not [IO.File]::Exists($path))
        $script:sourceFacts.GITHUB_RUN_ID = 'invalid'
        $capture = New-HermesFirstRefusalCapture
        Require ($capture.Status -eq 4 -and $null -eq $capture.Pins)
        Set-Item Function:script:New-HermesFirstRefusalCapture $originalNew
        Set-Item Function:script:Assert-FirstRefusalSource $originalSource
        Set-Item Function:script:Assert-FirstRefusalManualContext $originalHost
        Set-Item Function:script:Get-HermesCrashCheckout $originalCheckout
        $entries = @([ordered]@{ path = 'source.ps1'; sha256 = 'a' * 64; length = 1 })
        $withGit = $entries + @([ordered]@{ path = '.git/index'; sha256 = 'b' * 64; length = 2 })
        Require ((Get-FirstRefusalSourceDigest $withGit) -ceq (Get-HermesCrashEntryDigest $entries))
        # Portable admission controls evaluate the exact production predicate
        # using fake facts, never set real GitHub environment values or attest a host.
        $guard = (Get-Command Assert-FirstRefusalManualContext).ScriptBlock.ToString().
            Replace('[OperatingSystem]::IsWindows()', '$Windows').
            Replace('$env:', '$Facts.').Replace('(Get-Location).Path', '$Facts.GITHUB_WORKSPACE').
            Replace('$PSScriptRoot', '$ControlModuleBase')
        $ControlModuleBase = $PSScriptRoot
        $guard = [scriptblock]::Create("param(`$Facts, `$Windows)`n" + $guard)
        $workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
        $facts = @{
            OS = 'Windows_NT'; GITHUB_ACTIONS = 'true'; RUNNER_OS = 'Windows'
            RUNNER_ENVIRONMENT = 'github-hosted'; GITHUB_JOB = 'installer'
            GITHUB_SERVER_URL = 'https://github.com'; GITHUB_REPOSITORY = 'johnazariah/hermes'
            GITHUB_EVENT_NAME = 'workflow_dispatch'; GITHUB_REF = 'refs/heads/main'
            GITHUB_WORKFLOW_REF = 'johnazariah/hermes/.github/workflows/ci.yml@refs/heads/main'
            GITHUB_SHA = 'a' * 40; GITHUB_WORKFLOW_SHA = 'a' * 40
            GITHUB_RUN_ID = '123'; GITHUB_RUN_ATTEMPT = '1'; GITHUB_WORKSPACE = $workspace; GITHUB_EVENT_PATH = 'fake'
        }
        $event = @{ repository = @{ full_name = 'johnazariah/hermes'; private = $false; fork = $false }
            ref = 'main'; inputs = @{} }
        function Read-HermesCrashBytes { param($Path, $Maximum) return ,[Text.Encoding]::UTF8.GetBytes(($event | ConvertTo-Json -Compress)) }
        Require ((& $guard $facts $true) -ceq $workspace)
        $event.ref = 'refs/heads/main'
        Require ((& $guard $facts $true) -ceq $workspace)
        $event.ref = 'main'
        $event.Remove('inputs')
        Require ((& $guard $facts $true) -ceq $workspace)
        $event.inputs = @{}
        Require (Refuses { & $guard $facts $false })
        foreach ($entry in @(
            @('OS', 'Unix'), @('GITHUB_ACTIONS', 'false'), @('RUNNER_OS', 'Linux'),
            @('RUNNER_ENVIRONMENT', 'self-hosted'), @('GITHUB_JOB', 'other'),
            @('GITHUB_SERVER_URL', 'https://example.test'), @('GITHUB_REPOSITORY', 'other/hermes'),
            @('GITHUB_EVENT_NAME', 'push'), @('GITHUB_EVENT_NAME', 'pull_request'), @('GITHUB_EVENT_NAME', 'workflow_call'),
            @('GITHUB_REF', 'refs/tags/v1'), @('GITHUB_WORKFLOW_REF', 'johnazariah/hermes/.github/workflows/release.yml@refs/heads/main'),
            @('GITHUB_WORKFLOW_SHA', ('b' * 40)), @('GITHUB_SHA', 'invalid'),
            @('GITHUB_RUN_ID', '0'), @('GITHUB_RUN_ATTEMPT', '01'), @('GITHUB_WORKSPACE', $testRoot)
        )) {
            $original = $facts[$entry[0]]; $facts[$entry[0]] = $entry[1]
            Require (Refuses { & $guard $facts $true })
            $facts[$entry[0]] = $original
        }
        foreach ($entry in @(@('private', $true), @('fork', $true), @('full_name', 'fork/hermes'))) {
            $original = $event.repository[$entry[0]]; $event.repository[$entry[0]] = $entry[1]
            Require (Refuses { & $guard $facts $true })
            $event.repository[$entry[0]] = $original
        }
        $event.inputs.force = $true
        Require (Refuses { & $guard $facts $true })
        $event.inputs.Clear(); $event.ref = 'other'
        Require (Refuses { & $guard $facts $true })

        $driver = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'Invoke-NativeStartupTrace.ps1'))
        Require ($driver.Contains("if (`$env:GITHUB_EVENT_NAME -ceq 'workflow_dispatch') {", [StringComparison]::Ordinal))
        Require (-not $driver.Contains('HERMES_FIRST_REFUSAL_FORCE', [StringComparison]::Ordinal))
        Require ($driver.IndexOf('Complete-HermesFirstRefusalCapture', [StringComparison]::Ordinal) -gt
            $driver.IndexOf('$debugger.Dispose()', [StringComparison]::Ordinal))
        Require ($driver.IndexOf('Complete-HermesFirstRefusalCapture', [StringComparison]::Ordinal) -lt
            $driver.IndexOf('$report = New-HermesStartupReport', [StringComparison]::Ordinal))
    } finally {
        Set-Item Function:script:Assert-FirstRefusalSource $originalSource
        Set-Item Function:script:Get-FirstRefusalPublicKey $originalKey
        Set-Item Function:script:Protect-FirstRefusalLine $originalProtect
        Set-Item Function:script:New-HermesFirstRefusalCapture $originalNew
        Set-Item Function:script:Assert-FirstRefusalManualContext $originalHost
        Set-Item Function:script:Get-HermesCrashCheckout $originalCheckout
        Set-Item Function:script:Close-HermesCrashPins $originalClose
        $rsa.Dispose(); $wrong.Dispose(); $pinned.Dispose()
        if ([IO.Directory]::Exists($testRoot)) {
            Get-ChildItem -LiteralPath $testRoot -File -Recurse -Force | ForEach-Object {
                [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal)
            }
            Remove-Item -LiteralPath $testRoot -Force -Recurse
        }
        Remove-Variable syntheticPublic, artifactCapture, ownedPlain, ownedKey, sourceRoot, sourceFacts -Scope Script -ErrorAction SilentlyContinue
    }
    Write-Output "HERMES_FIRST_REFUSAL_CONTROLS v=1 total=$script:checks passed=$script:checks failed=0"
}
