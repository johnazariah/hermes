#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1')

$script:FirstRefusalFingerprint = 'aecc0abe72feb788a2e892b17d04644891f48dec865a495a8126035233ad5cbd'
$script:FirstRefusalPublicKey = @'
-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAw5uLKXzY64yY2EOpUCyW
7yeDhW4qUVWVeQIBKxgaNeLw7KnN6DAXBSErmML3ab9Had1AU2tRwWJUn9rKkpNX
ci8uTa+JB0oOthS7NXnCohTnOFtj++jEed8LgHR7B8RKioMsjPFSGpeQYkTE05Fo
lQpLT9Cq41YbBzdkDtirNBITOAySCvcdScPYXvydEPFNZPYkCyBmbF7HKfdOU6S6
Pm5BhwW10zDKwe3bWlhMn1XtECs56WvecElb6Miarft7RvSjgROVk2UD5PYCqh7w
S1N2q2z2HSLYDC1TcUAzu/rI9b5zRy/O/NXjPadEIZQlwwUuzS4kQVyiuiCWo5k9
4XK+q+TiWQH/RqTpEQDrkRr3It+HcIPYYCHjrkSX0u9kTjS8jfP5q76g++O0Y0m/
Ny9S8NRUtQvmR6+jpflEuHa0m1WV+FbA/YmHMzgqGpnuf17dSOdl+sF3YatMO6FL
1+TqkAwwrD1/qXVKq1L1HBBrHvO0ZL3lvGkPUQSwMbzLAgMBAAE=
-----END PUBLIC KEY-----
'@

function Get-FirstRefusalPublicKey {
    $rsa = [Security.Cryptography.RSA]::Create()
    try {
        $rsa.ImportFromPem($script:FirstRefusalPublicKey)
        $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            $rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
        if ($rsa.KeySize -ne 3072 -or $fingerprint -cne $script:FirstRefusalFingerprint) { throw '1' }
        return $rsa
    } catch { $rsa.Dispose(); throw '1' }
}

function Get-HermesFirstRefusalHeader {
    param([Collections.IDictionary]$Context)
    if ($Context.Count -ne 5 -or $Context.key -cnotmatch '\A[0-9a-f]{64}\z' `
        -or $Context.source -cnotmatch '\A[0-9a-f]{64}\z' -or $Context.revision -cnotmatch '\A[0-9a-f]{40}\z' `
        -or $Context.run -cnotmatch '\A[1-9][0-9]{0,19}\z' -or $Context.attempt -cnotmatch '\A[1-9][0-9]{0,9}\z') {
        throw '2'
    }
    return "HERMES-FIRST-REFUSAL`nv=1`nkey=$($Context.key)`nsource=$($Context.source)`nrevision=$($Context.revision)`nrun=$($Context.run)`nattempt=$($Context.attempt)`njob=installer`nrole=first-refused-debugger-line`n"
}

function Get-FirstRefusalSourceDigest {
    param([object[]]$Entries)
    # Pin Git's live state too, but keep the public source digest reproducible
    # from the reviewed checkout rather than machine-specific index bytes.
    return Get-HermesCrashEntryDigest @($Entries | Where-Object {
        -not $_.path.StartsWith('.git/', [StringComparison]::Ordinal)
    })
}

function Read-HermesFirstRefusalEnvelope {
    param([byte[]]$Bytes, [Collections.IDictionary]$Context)
    # HFRLINE1, two big-endian uint16 lengths, canonical ASCII AAD, then
    # RSA-3072/OAEP-SHA256 wrapped key, nonce, tag and AES-256-GCM ciphertext.
    if ($Bytes.Length -lt 425 -or $Bytes.Length -gt 4096 `
        -or [Text.Encoding]::ASCII.GetString($Bytes, 0, 8) -cne 'HFRLINE1') { throw '3' }
    $headerLength = [int]$Bytes[8] * 256 + [int]$Bytes[9]
    $cipherLength = [int]$Bytes[10] * 256 + [int]$Bytes[11]
    if ($headerLength -lt 1 -or $headerLength -gt 512 -or $cipherLength -lt 1 -or $cipherLength -gt 1024 `
        -or $Bytes.Length -ne (12 + $headerLength + 384 + 12 + 16 + $cipherLength)) { throw '3' }
    $header = [Text.UTF8Encoding]::new($false, $true).GetString($Bytes, 12, $headerLength)
    if ($header -cne (Get-HermesFirstRefusalHeader $Context)) { throw '3' }
    $offset = 12
    $parts = @{}
    foreach ($part in @(@('Aad', $headerLength), @('WrappedKey', 384), @('Nonce', 12),
            @('Tag', 16), @('Ciphertext', $cipherLength))) {
        $buffer = [byte[]]::new($part[1])
        [Buffer]::BlockCopy($Bytes, $offset, $buffer, 0, $buffer.Length)
        $parts[$part[0]] = $buffer
        $offset += $buffer.Length
    }
    return $parts
}

function Protect-FirstRefusalLine {
    param([string]$Line, [Security.Cryptography.RSA]$Rsa, [Collections.IDictionary]$Context)
    $plain = $null; $key = $null; $aes = $null
    try {
        if ($Line.Contains("`r") -or $Line.Contains("`n")) { throw '6' }
        $encoding = [Text.UTF8Encoding]::new($false, $true)
        try { $length = $encoding.GetByteCount($Line) } catch { throw '6' }
        if ($length -lt 1 -or $length -gt 1024) { throw '5' }
        $fingerprint = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
            $Rsa.ExportSubjectPublicKeyInfo())).ToLowerInvariant()
        if ($Rsa.KeySize -ne 3072 -or $Context.key -cne $fingerprint) { throw '7' }
        $aad = $encoding.GetBytes((Get-HermesFirstRefusalHeader $Context))
        $plain = $encoding.GetBytes($Line)
        $key = [Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
        $nonce = [Security.Cryptography.RandomNumberGenerator]::GetBytes(12)
        $tag = [byte[]]::new(16); $cipher = [byte[]]::new($length)
        $aes = [Security.Cryptography.AesGcm]::new($key, 16)
        $aes.Encrypt($nonce, $plain, $cipher, $tag, $aad)
        $wrapped = $Rsa.Encrypt($key, [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
        $bytes = [byte[]]::new(12 + $aad.Length + 384 + 12 + 16 + $length)
        [Buffer]::BlockCopy([Text.Encoding]::ASCII.GetBytes('HFRLINE1'), 0, $bytes, 0, 8)
        $bytes[8] = [byte]($aad.Length -shr 8); $bytes[9] = [byte]($aad.Length -band 255)
        $bytes[10] = [byte]($length -shr 8); $bytes[11] = [byte]($length -band 255)
        $offset = 12
        foreach ($part in @($aad, $wrapped, $nonce, $tag, $cipher)) {
            [Buffer]::BlockCopy($part, 0, $bytes, $offset, $part.Length)
            $offset += $part.Length
        }
        $null = Read-HermesFirstRefusalEnvelope $bytes $Context
        return ,$bytes
    } finally {
        # Only owned mutable byte buffers can be erased; the parser's existing
        # immutable string is not claimed to have been erased.
        if ($null -ne $plain) { [Array]::Clear($plain) }
        if ($null -ne $key) { [Array]::Clear($key) }
        if ($null -ne $aes) { $aes.Dispose() }
    }
}

function Assert-FirstRefusalManualContext {
    # These are GitHub's real process/event assertions, not a local attestation
    # service. No caller-supplied context, force environment or workflow inputs.
    if (-not [OperatingSystem]::IsWindows() -or $env:OS -cne 'Windows_NT' `
        -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_OS -cne 'Windows' `
        -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:GITHUB_JOB -cne 'installer' `
        -or $env:GITHUB_SERVER_URL -cne 'https://github.com' `
        -or $env:GITHUB_REPOSITORY -cne 'johnazariah/hermes' `
        -or $env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' `
        -or $env:GITHUB_REF -cnotmatch '\Arefs/heads/[A-Za-z0-9_./-]+\z' `
        -or $env:GITHUB_WORKFLOW_REF -cne "johnazariah/hermes/.github/workflows/ci.yml@$($env:GITHUB_REF)" `
        -or $env:GITHUB_SHA -cnotmatch '\A[0-9a-f]{40}\z' `
        -or $env:GITHUB_WORKFLOW_SHA -cne $env:GITHUB_SHA `
        -or $env:GITHUB_RUN_ID -cnotmatch '\A[1-9][0-9]{0,19}\z' `
        -or $env:GITHUB_RUN_ATTEMPT -cnotmatch '\A[1-9][0-9]{0,9}\z') { throw '4' }
    $eventBytes = Read-HermesCrashBytes $env:GITHUB_EVENT_PATH 1048576
    $event = [Text.UTF8Encoding]::new($false, $true).GetString($eventBytes) | ConvertFrom-Json
    $inputs = $event.PSObject.Properties['inputs']
    if ($event.repository.full_name -cne 'johnazariah/hermes' `
        -or $event.repository.private -isnot [bool] -or $event.repository.private `
        -or $event.repository.fork -isnot [bool] -or $event.repository.fork `
        -or ($event.ref -cne $env:GITHUB_REF -and $event.ref -cne $env:GITHUB_REF.Substring(11)) `
        -or ($null -ne $inputs -and $null -ne $inputs.Value `
            -and ($inputs.Value -isnot [pscustomobject] -or @($inputs.Value.PSObject.Properties).Count -ne 0))) { throw '4' }
    $root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    if ($root -cne [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE) `
        -or $root -cne [IO.Path]::GetFullPath((Get-Location).Path)) { throw '4' }
    Assert-HermesCrashPath $root
    return $root
}

function New-HermesFirstRefusalCapture {
    # Status: 1 armed/no qualifying line; 2 encrypted/pending cleanup; 3 written;
    # 4 context/source unavailable; 5 byte bound; 6 encoding/newline; 7 capture
    # failure; 8 output/settlement failure. Count is the consumed attempt, 0 or 1.
    $capture = @{ Status = 4; Count = 0; Bytes = $null; Pins = $null; Context = $null; Root = '' }
    try {
        $capture.Root = Assert-FirstRefusalManualContext
        $checkout = Get-HermesCrashCheckout $capture.Root $env:GITHUB_SHA
        $entries = @($checkout.Sources) + @(
            Get-HermesCrashFileEntry (Join-Path $capture.Root '.github/workflows/ci.yml') '.github/workflows/ci.yml')
        $capture.Pins = Open-HermesCrashPins $capture.Root $entries
        $capture.Context = [ordered]@{
            key = $script:FirstRefusalFingerprint; source = (Get-FirstRefusalSourceDigest $entries)
            revision = $checkout.Revision; run = $env:GITHUB_RUN_ID; attempt = $env:GITHUB_RUN_ATTEMPT
        }
        $null = Get-HermesFirstRefusalHeader $capture.Context
        $rsa = Get-FirstRefusalPublicKey
        $rsa.Dispose()
        Assert-HermesCrashPins $capture.Pins
        $capture.Status = 1
    } catch {
        if ($null -ne $capture.Pins) { Close-HermesCrashPins $capture.Pins; $capture.Pins = $null }
    }
    return $capture
}

function Assert-FirstRefusalSource {
    param([hashtable]$Capture)
    if ((Assert-FirstRefusalManualContext) -cne $Capture.Root) { throw '4' }
    Assert-HermesCrashPins $Capture.Pins
    $checkout = Get-HermesCrashCheckout $Capture.Root $env:GITHUB_SHA
    $entries = @($checkout.Sources) + @(
        Get-HermesCrashFileEntry (Join-Path $Capture.Root '.github/workflows/ci.yml') '.github/workflows/ci.yml')
    if ((Get-FirstRefusalSourceDigest $entries) -cne $Capture.Context.source `
        -or $Capture.Context.revision -cne $env:GITHUB_SHA -or $Capture.Context.run -cne $env:GITHUB_RUN_ID `
        -or $Capture.Context.attempt -cne $env:GITHUB_RUN_ATTEMPT `
        -or $Capture.Context.key -cne $script:FirstRefusalFingerprint) { throw '4' }
}

function Invoke-HermesFirstRefusalCapture {
    param([hashtable]$Capture, [string]$Line)
    if ($Capture.Status -ne 1 -or $Capture.Count -ne 0) { return }
    # Consume the only attempt before any fallible operation. Never try a later
    # line after oversize, invalid encoding, missing ownership or crypto failure.
    $Capture.Count = 1
    $rsa = $null
    try {
        Assert-FirstRefusalSource $Capture
        $rsa = Get-FirstRefusalPublicKey
        $Capture.Bytes = Protect-FirstRefusalLine $Line $rsa $Capture.Context
        $Capture.Status = 2
    } catch {
        $Capture.Status = if ($_.Exception.Message -ceq '5') { 5 } elseif ($_.Exception.Message -ceq '6') { 6 } else { 7 }
    } finally { if ($null -ne $rsa) { $rsa.Dispose() } }
}

function Write-FirstRefusalEnvelope {
    param([hashtable]$Capture)
    Assert-FirstRefusalSource $Capture
    $null = Read-HermesFirstRefusalEnvelope $Capture.Bytes $Capture.Context
    $directory = Join-Path $Capture.Root '.artifacts/installer-evidence'
    Assert-HermesCrashPath $directory
    $path = Join-Path $directory 'startup-first-refusal.enc'
    if (Test-Path -LiteralPath $path) { throw '8' }
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Capture.Bytes); $stream.Flush($true) } finally { $stream.Dispose() }
    [IO.File]::SetAttributes($path, [IO.FileAttributes]::ReadOnly)
    Assert-HermesCrashPath $path
    $disk = Read-HermesCrashBytes $path
    $null = Read-HermesFirstRefusalEnvelope $disk $Capture.Context
    if (-not [Linq.Enumerable]::SequenceEqual[byte]($disk, $Capture.Bytes)) { throw '8' }
    Assert-FirstRefusalSource $Capture
}

function Complete-HermesFirstRefusalCapture {
    param([hashtable]$Capture, [bool]$Settled)
    try {
        if ($Capture.Status -eq 2) {
            if (-not $Settled) { throw '8' }
            Write-FirstRefusalEnvelope $Capture
            $Capture.Status = 3
        }
    } catch { $Capture.Status = 8 }
    finally {
        $Capture.Bytes = $null
        try { if ($null -ne $Capture.Pins) { Close-HermesCrashPins $Capture.Pins } }
        catch { $Capture.Status = 8 }
        $Capture.Pins = $null
    }
}

function Assert-HermesFirstRefusalArtifact {
    # Read-only validation immediately before the one fixed upload. The public
    # validator checks structure/context; only the Mac's private key can verify GCM.
    $capture = New-HermesFirstRefusalCapture
    try {
        if ($capture.Status -ne 1) { throw '4' }
        Assert-FirstRefusalSource $capture
        $reportPath = Join-Path $capture.Root '.artifacts/installer-evidence/startup-debug.json'
        $report = [Text.UTF8Encoding]::new($false, $true).GetString(
            (Read-HermesCrashBytes $reportPath)) | ConvertFrom-Json
        if ($report.firstRefusalStatus -isnot [long] -or $report.firstRefusalStatus -ne 3 `
            -or $report.firstRefusalCount -isnot [long] -or $report.firstRefusalCount -ne 1 `
            -or $report.completed -ne 0 -or $report.cleanupFault -ne 0 `
            -or $report.observations.ParserProtocol -ne 1 `
            -or $report.observations.scopedReadFirstKind -lt 1) { throw '8' }
        $path = Join-Path $capture.Root '.artifacts/installer-evidence/startup-first-refusal.enc'
        Assert-HermesCrashPath $path
        if (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::ReadOnly) -eq 0) { throw '8' }
        $entry = Get-HermesCrashFileEntry $path 'startup-first-refusal.enc' 4096
        $pins = Open-HermesCrashPins $capture.Root @(
            [ordered]@{ path = '.artifacts/installer-evidence/startup-first-refusal.enc'; sha256 = $entry.sha256; length = $entry.length })
        try {
            $null = Read-HermesFirstRefusalEnvelope (Read-HermesCrashBytes $path) $capture.Context
            Assert-HermesCrashPins $pins
            Assert-FirstRefusalSource $capture
        } finally { Close-HermesCrashPins $pins }
    } catch { throw '8' }
    finally {
        Complete-HermesFirstRefusalCapture $capture $false
        if ($capture.Status -ne 1) { throw '8' }
    }
}

Export-ModuleMember -Function New-HermesFirstRefusalCapture, Invoke-HermesFirstRefusalCapture,
    Complete-HermesFirstRefusalCapture, Assert-HermesFirstRefusalArtifact,
    Get-HermesFirstRefusalHeader, Read-HermesFirstRefusalEnvelope
