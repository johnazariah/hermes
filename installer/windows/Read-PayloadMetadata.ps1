#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$InventoryPath,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata
$payload = (Resolve-Path -LiteralPath $PayloadDirectory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$inventory = Get-Content -LiteralPath $InventoryPath -Raw | ConvertFrom-Json
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)

function Assert-PlainPath([string]$Path) {
    $item = [IO.FileInfo]::new($Path)
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Payload metadata does not follow links.' }
        if ([string]::Equals($item.FullName, $payload, [StringComparison]::OrdinalIgnoreCase)) { return }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
    throw 'Payload metadata path did not reach its declared root.'
}

function Read-ModuleVersion([IO.FileStream]$Stream, [string]$Path) {
    $first = $Stream.ReadByte()
    $second = $Stream.ReadByte()
    $Stream.Position = 0
    $peName = [IO.Path]::GetExtension($Path) -in @('.exe', '.dll')
    if ($first -ne 0x4D -or $second -ne 0x5A) {
        if ($peName) { throw [BadImageFormatException]::new('A published DLL or executable is not a PE image.') }
        return $null
    }
    $image = [System.Reflection.PortableExecutable.PEReader]::new($Stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
    try {
        if ($null -eq $image.PEHeaders.PEHeader) { throw [BadImageFormatException]::new('Missing PE header.') }
        if ($null -eq $image.PEHeaders.CorHeader) { return $null }
        if (-not $image.HasMetadata) { throw [BadImageFormatException]::new('Managed PE image has no metadata.') }
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($image)
        $mvid = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid)
        if ($mvid -eq [Guid]::Empty) { throw [BadImageFormatException]::new('Managed module identity is empty.') }
        return $mvid.ToString('D')
    } finally { $image.Dispose() }
}

$files = foreach ($file in $inventory.files) {
    if ($file.path -notmatch '^(service|tray)/[A-Za-z0-9_@.+/-]+$' `
        -or @($file.path.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') }).Count -ne 0 `
        -or -not $seen.Add($file.path) -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $file.length -lt 0) {
        throw 'Invalid validated-publish metadata request.'
    }
    $path = [IO.Path]::GetFullPath((Join-Path $payload $file.path))
    if (-not $path.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Payload metadata path escaped its root.' }
    Assert-PlainPath $path
    $stream = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $hash = [Convert]::ToHexString($hasher.ComputeHash($stream)).ToLowerInvariant() }
        finally { $hasher.Dispose() }
        if ($stream.Length -ne $file.length -or $hash -cne $file.sha256) { throw 'Payload changed after SDK/canonical provenance verification.' }
        $stream.Position = 0
        [ordered]@{ path = $file.path; sha256 = $hash; length = $stream.Length; moduleVersionId = Read-ModuleVersion $stream $path }
    } finally { $stream.Dispose() }
}
[ordered]@{ files = @($files) } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
