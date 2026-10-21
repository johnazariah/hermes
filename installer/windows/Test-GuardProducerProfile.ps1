#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Guard-ProducerProfile.psm1') -Force

function Set-Word([byte[]]$Bytes, [int]$Offset, [uint16]$Value) {
    [BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

function Set-Dword([byte[]]$Bytes, [int]$Offset, [uint32]$Value) {
    [BitConverter]::GetBytes($Value).CopyTo($Bytes, $Offset)
}

function Set-Section(
    [byte[]]$Bytes, [int]$Offset, [uint32]$Rva,
    [uint32]$Raw, [uint32]$Size, [uint32]$Flags
) {
    Set-Dword $Bytes ($Offset + 8) $Size
    Set-Dword $Bytes ($Offset + 12) $Rva
    Set-Dword $Bytes ($Offset + 16) $Size
    Set-Dword $Bytes ($Offset + 20) $Raw
    Set-Dword $Bytes ($Offset + 36) $Flags
}

function New-ProducerFixture {
    # Independent synthetic PE encoding. This is NOT evidence of actual exports.
    # Data bytes come from the pinned v9.0.21 C++ declaration, not the probe.
    $bytes = [byte[]]::new(0xa00)
    Set-Word $bytes 0 0x5a4d
    Set-Dword $bytes 0x3c 0x80
    Set-Dword $bytes 0x80 0x4550
    Set-Word $bytes 0x84 0x8664
    Set-Word $bytes 0x86 3
    Set-Word $bytes 0x94 0xf0
    Set-Word $bytes 0x96 0x2022
    Set-Word $bytes 0x98 0x20b
    Set-Dword $bytes 0xa8 0x1000
    Set-Dword $bytes 0xac 0x1000
    Set-Dword $bytes 0xb8 0x1000
    Set-Dword $bytes 0xbc 0x200
    Set-Dword $bytes 0xd0 0x4000
    Set-Dword $bytes 0xd4 0x200
    Set-Word $bytes 0xdc 2
    Set-Dword $bytes 0x104 16
    Set-Dword $bytes 0x108 0x2000
    Set-Dword $bytes 0x10c 0x200
    Set-Section $bytes 0x188 0x1000 0x200 0x200 0x60000020
    Set-Section $bytes 0x1b0 0x2000 0x400 0x400 0x40000040
    Set-Section $bytes 0x1d8 0x3000 0x800 0x200 3221225536
    Set-Dword $bytes 0x40c 0x20a0
    Set-Dword $bytes 0x410 1
    Set-Dword $bytes 0x414 3
    Set-Dword $bytes 0x418 3
    Set-Dword $bytes 0x41c 0x2040
    Set-Dword $bytes 0x420 0x2050
    Set-Dword $bytes 0x424 0x2060
    Set-Dword $bytes 0x440 0x3000
    Set-Dword $bytes 0x444 0x1000
    Set-Dword $bytes 0x448 0x1010
    Set-Dword $bytes 0x450 0x2100
    Set-Dword $bytes 0x454 0x2140
    Set-Dword $bytes 0x458 0x2180
    Set-Word $bytes 0x460 0
    Set-Word $bytes 0x462 1
    Set-Word $bytes 0x464 2
    [Text.Encoding]::ASCII.GetBytes("Synthetic.dll`0").CopyTo($bytes, 0x4a0)
    [Text.Encoding]::ASCII.GetBytes("DotNetRuntimeDebugHeader`0").CopyTo($bytes, 0x500)
    [Text.Encoding]::ASCII.GetBytes("HermesPreflight`0").CopyTo($bytes, 0x540)
    [Text.Encoding]::ASCII.GetBytes("HermesTransactionFence`0").CopyTo($bytes, 0x580)
    [Convert]::FromHexString(
        '444E444804000000010000000000000000000000000000000000000000000000'
    ).CopyTo($bytes, 0x800)
    return ,$bytes
}

function Assert-ProducerResult([byte[]]$Bytes, [bool]$Expected) {
    $stream = [IO.MemoryStream]::new($Bytes, $false)
    try {
        $image = [System.Reflection.PortableExecutable.PEReader]::new(
            $stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        try {
            $actual = @(Test-HermesGuardProducerProfile $image)
            if ($actual.Count -ne 1 -or $actual[0] -isnot [bool] -or $actual[0] -ne $Expected) {
                throw 'The closed producer-profile probe returned an incorrect or non-boolean result.'
            }
        } finally { $image.Dispose() }
    } finally { $stream.Dispose() }
}

Assert-ProducerResult (New-ProducerFixture) $true

# A different EAT order is valid only with corresponding name ordinals.
$permuted = New-ProducerFixture
Set-Dword $permuted 0x440 0x1000
Set-Dword $permuted 0x444 0x1010
Set-Dword $permuted 0x448 0x3000
Set-Word $permuted 0x460 2
Set-Word $permuted 0x462 0
Set-Word $permuted 0x464 1
Assert-ProducerResult $permuted $true

$mutations = [ordered]@{
    UnknownExtraExport = { param($b) Set-Dword $b 0x414 4; Set-Dword $b 0x418 4 }
    MissingName = { param($b) Set-Dword $b 0x418 2 }
    UnknownRuntimeEntry = { param($b) $b[0x500] = 88 }
    UnknownCallback = { param($b) $b[0x540] = 88 }
    MissingNameTerminator = { param($b) $b[0x518] = 65 }
    DuplicateName = { param($b) Set-Dword $b 0x458 0x2140 }
    DuplicateOrdinal = { param($b) Set-Word $b 0x464 1 }
    SparseOrdinal = { param($b) Set-Word $b 0x464 3 }
    MissingFunction = { param($b) Set-Dword $b 0x448 0 }
    AliasedCallbacks = { param($b) Set-Dword $b 0x448 0x1000 }
    ForwardedCallback = { param($b) Set-Dword $b 0x444 0x2100 }
    ForwardedData = { param($b) Set-Dword $b 0x440 0x2100 }
    WrongNameOrdinalRole = {
        param($b)
        Set-Word $b 0x460 1
        Set-Word $b 0x462 0
    }
    DataAsCallback = { param($b) Set-Dword $b 0x444 0x3020 }
    CodeAsData = { param($b) Set-Dword $b 0x440 0x1018 }
    ExecutableData = { param($b) Set-Dword $b 0x1fc 3758096448 }
    UnreadableData = { param($b) Set-Dword $b 0x1fc 2147483712 }
    ReadOnlyRuntimeObject = { param($b) Set-Dword $b 0x1fc 0x40000040 }
    NonExecutableCallback = { param($b) Set-Dword $b 0x1ac 0x40000020 }
    WrongCookie = { param($b) $b[0x800] = 0 }
    UnknownMajor = { param($b) $b[0x804] = 5 }
    UnknownMinor = { param($b) $b[0x806] = 1 }
    WrongPointerFlags = { param($b) $b[0x808] = 0 }
    UnknownFlags = { param($b) $b[0x809] = 1 }
    NonzeroReserved = { param($b) $b[0x80c] = 1 }
    InitializedRuntimePointer = { param($b) $b[0x810] = 1 }
    UnalignedData = { param($b) Set-Dword $b 0x440 0x3001 }
    DataPastRawEnd = { param($b) Set-Dword $b 0x440 0x31f0 }
    DataPastVirtualEnd = { param($b) Set-Dword $b 0x1e0 16 }
    CallbackPastRawEnd = {
        param($b)
        Set-Dword $b 0x190 0x300
        Set-Dword $b 0x444 0x1250
    }
    BadFunctionTable = { param($b) Set-Dword $b 0x41c ([uint32]::MaxValue) }
    BadNameTable = { param($b) Set-Dword $b 0x420 ([uint32]::MaxValue) }
    BadOrdinalTable = { param($b) Set-Dword $b 0x424 ([uint32]::MaxValue) }
    BadNameTarget = { param($b) Set-Dword $b 0x450 ([uint32]::MaxValue) }
    ExportDirectoryPastFile = { param($b) Set-Dword $b 0x10c 0x1000 }
    RawDataPastFile = { param($b) Set-Dword $b 0x1ec 0xa00 }
    OverlappingVirtualSections = { param($b) Set-Dword $b 0x1e4 0x1000 }
    WrongMachine = { param($b) Set-Word $b 0x84 0x14c }
    ClrDirectory = { param($b) Set-Dword $b 0x178 0x3000; Set-Dword $b 0x17c 72 }
}
foreach ($mutation in $mutations.Values) {
    $bytes = New-ProducerFixture
    & $mutation $bytes
    Assert-ProducerResult $bytes $false
}

# The observation is specifically the candidate three-entry producer profile.
# This does not discard or modify the coordinator's intentional strict-two tests.
$two = New-ProducerFixture
Set-Dword $two 0x414 2
Set-Dword $two 0x418 2
Set-Dword $two 0x440 0x1000
Set-Dword $two 0x444 0x1010
Set-Dword $two 0x450 0x2140
Set-Dword $two 0x454 0x2180
Assert-ProducerResult $two $false

$truncated = New-ProducerFixture
Assert-ProducerResult ([byte[]]$truncated[0..0x81e]) $false
Assert-ProducerResult ([byte[]]@(0)) $false

# Exercise the actual catch body, following Test-PackageLayout.ps1's
# failure-context probe. No exception-injection API is added to the module.
$profileTokens = $null
$profileErrors = $null
$profileAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'Guard-ProducerProfile.psm1'),
    [ref]$profileTokens, [ref]$profileErrors)
$profileCatches = @($profileAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CatchClauseAst]
}, $true))
if ($profileErrors.Count -ne 0 -or $profileCatches.Count -ne 1) {
    throw 'The producer-profile exception boundary must parse and remain unambiguous.'
}
$failureProbe = [scriptblock]::Create(
    'try { throw $expectedFailure } catch ' + $profileCatches[0].Body.Extent.Text)

foreach ($expectedFailure in @(
    [BadImageFormatException]::new('synthetic mismatch'),
    [IO.EndOfStreamException]::new('synthetic read failure'),
    [Management.Automation.MethodInvocationException]::new(
        'synthetic method wrapper', [IO.IOException]::new('synthetic read failure')),
    [Management.Automation.GetValueInvocationException]::new(
        'synthetic property wrapper', [BadImageFormatException]::new('synthetic malformed image')),
    [Reflection.TargetInvocationException]::new(
        [IO.IOException]::new('synthetic read failure'))
)) {
    $actual = @(& $failureProbe)
    if ($actual.Count -ne 1 -or $actual[0] -isnot [bool] -or $actual[0]) {
        throw 'Expected profile/read failures must return exactly one false boolean.'
    }
}

foreach ($expectedFailure in @(
    [InvalidOperationException]::new('synthetic programming failure'),
    [ArgumentException]::new('synthetic argument failure'),
    [Management.Automation.RuntimeException]::new('synthetic runtime failure'),
    [OperationCanceledException]::new('synthetic cancellation'),
    [Reflection.TargetInvocationException]::new(
        [OperationCanceledException]::new('synthetic wrapped cancellation')),
    [InvalidOperationException]::new(
        'synthetic opaque failure', [IO.IOException]::new('synthetic inner read failure')),
    [OperationCanceledException]::new(
        'synthetic cancellation', [BadImageFormatException]::new('synthetic inner mismatch'))
)) {
    $results = [Collections.Generic.List[object]]::new()
    $propagated = $false
    try {
        & $failureProbe | ForEach-Object { $results.Add($_) }
    } catch {
        if (-not [object]::ReferenceEquals(
            $_.Exception.GetBaseException(), $expectedFailure.GetBaseException())) {
            throw 'The unexpected producer-profile failure was replaced.'
        }
        $propagated = $true
    }
    if (-not $propagated -or $results.Count -ne 0) {
        throw 'Unexpected failures and cancellation must propagate without a boolean result.'
    }
}

$strictProbe = [scriptblock]::Create(
    'Set-StrictMode -Version Latest; try { $null = $hermesProducerProbeUndefinedVariable } catch ' `
        + $profileCatches[0].Body.Extent.Text)
$strictResults = [Collections.Generic.List[object]]::new()
$strictPropagated = $false
try {
    & $strictProbe | ForEach-Object { $strictResults.Add($_) }
} catch {
    $strictPropagated = $true
}
if (-not $strictPropagated -or $strictResults.Count -ne 0) {
    throw 'A strict-mode fault must propagate, not become profile zero.'
}

# End-to-end unexpected failure using a real PEReader, not the extracted catch.
$disposedStream = [IO.MemoryStream]::new((New-ProducerFixture), $false)
try {
    $disposedImage = [System.Reflection.PortableExecutable.PEReader]::new(
        $disposedStream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
    $disposedImage.Dispose()
    $disposedResults = [Collections.Generic.List[object]]::new()
    $disposedPropagated = $false
    try {
        Test-HermesGuardProducerProfile $disposedImage |
            ForEach-Object { $disposedResults.Add($_) }
    } catch {
        if ($_.Exception.GetBaseException() -isnot [ObjectDisposedException]) { throw }
        $disposedPropagated = $true
    }
    if (-not $disposedPropagated -or $disposedResults.Count -ne 0) {
        throw 'A disposed-reader programming fault must propagate without a boolean result.'
    }
} finally { $disposedStream.Dispose() }

Write-Output 'Closed producer-profile probe controls passed using synthetic bytes; no actual native output was observed.'
