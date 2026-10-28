#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-HermesGuardHashList {
    param([Parameter(Mandatory = $true)][string]$CurrentHash, [string]$CompatibleHashes = '')
    $hashes = @($CurrentHash)
    if ($CompatibleHashes) { $hashes += $CompatibleHashes.Split(';') }
    if (@($hashes | Where-Object { $_ -cnotmatch '\A[0-9a-f]{64}\z' }).Count -ne 0) {
        throw 'Approved guard inputs must be independently verified canonical lowercase SHA-256 values.'
    }
    return (($hashes | Select-Object -Unique) -join ';')
}

function Get-HermesCompiledGuardTrust {
    param([Parameter(Mandatory = $true)][string]$AssemblyPath)
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::Open($AssemblyPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $image = [System.Reflection.PortableExecutable.PEReader]::new($stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        try {
            if (-not $image.HasMetadata) { throw 'Setup coordinator must be a managed non-single-file assembly.' }
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($image)
            $values = @(
                foreach ($handle in $metadata.GetAssemblyDefinition().GetCustomAttributes()) {
                    $attribute = $metadata.GetCustomAttribute($handle)
                    if ($attribute.Constructor.Kind -ne [System.Reflection.Metadata.HandleKind]::MemberReference) { continue }
                    $constructor = $metadata.GetMemberReference([System.Reflection.Metadata.MemberReferenceHandle]$attribute.Constructor)
                    if ($constructor.Parent.Kind -ne [System.Reflection.Metadata.HandleKind]::TypeReference) { continue }
                    $type = $metadata.GetTypeReference([System.Reflection.Metadata.TypeReferenceHandle]$constructor.Parent)
                    if ($metadata.GetString($type.Namespace) -cne 'System.Reflection' `
                        -or $metadata.GetString($type.Name) -cne 'AssemblyMetadataAttribute') { continue }
                    $blob = $metadata.GetBlobReader($attribute.Value)
                    if ($blob.ReadUInt16() -ne 1) { throw 'Invalid compiled assembly metadata.' }
                    $key = $blob.ReadSerializedString()
                    $value = $blob.ReadSerializedString()
                    if ($blob.ReadUInt16() -ne 0 -or $blob.RemainingBytes -ne 0) { throw 'Unexpected compiled metadata arguments.' }
                    if ($key -ceq 'HermesApprovedGuardSha256') { $value }
                }
            )
            if ($values.Count -ne 1 -or $values[0] -cnotmatch '\A[0-9a-f]{64}(;[0-9a-f]{64})*\z' `
                -or @($values[0].Split(';') | Select-Object -Unique).Count -ne $values[0].Split(';').Count) {
                throw 'Setup does not contain exactly one valid compiled guard trust list.'
            }
            return $values[0]
        } finally { $image.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-HermesSetupPublishFiles {
    param([string]$Directory, [string]$InventoryPath, [switch]$WithPackagingWrappers, [switch]$AcceptanceHelpers)
    $repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $allowedRoots = @(
        (Join-Path $repository 'installer/windows/Hermes.Setup/bin'),
        (Join-Path $repository 'installer/windows/Hermes.Setup/obj'),
        (Join-Path $repository 'installer/windows/Hermes.Installer.Control/bin'),
        (Join-Path $repository 'installer/windows/Hermes.Installer.Control/obj'),
        (Join-Path $repository 'src/Hermes.Servicing/bin'),
        (Join-Path $repository 'src/Hermes.Servicing/obj')
    )
    if ($AcceptanceHelpers) {
        foreach ($project in @('Hermes.Servicing.Acceptance', 'Hermes.Servicing.Acceptance.Model', 'Hermes.Servicing.ProcessProbe')) {
            foreach ($folder in @('bin', 'obj')) { $allowedRoots += Join-Path $repository "tests/$project/$folder" }
        }
    }
    $packages = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $allowedRoots += $packages
    if ($env:DOTNET_ROOT) { $allowedRoots += Join-Path $env:DOTNET_ROOT 'packs' }
    $expected = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in (Get-Content -LiteralPath $InventoryPath)) {
        $fields = $line.TrimStart([char]0xfeff).Split('|')
        if ($fields.Count -ne 3) { throw 'Malformed coordinator SDK publish inventory.' }
        $relative = $fields[1].Replace('\', '/')
        $runtimeName = $relative -cmatch '\A[A-Za-z0-9_.+-]+\.(dll|exe|pdb|xml)\z' `
            -or $relative -cmatch '\AHermes\.Setup\.(deps|runtimeconfig)\.json\z' `
            -or ($AcceptanceHelpers -and $relative -cmatch '\AHermes\.(Service|Servicing\.Acceptance)\.(deps|runtimeconfig)\.json\z') `
            -or $relative -cmatch '\A[a-z]{2}(-[A-Za-z0-9]+)?/[A-Za-z0-9_.+-]+\.resources\.dll\z' `
            -or $relative -cmatch '\Aruntimes/win-(x64|x86|arm64)/native/[A-Za-z0-9_.+-]+\.dll\z'
        $source = [IO.Path]::GetFullPath($fields[0])
        $approvedSource = @($allowedRoots | Where-Object {
            $source.StartsWith([IO.Path]::GetFullPath($_).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        }).Count -gt 0
        if (-not $runtimeName -or -not $approvedSource -or $fields[2] -cnotmatch '\A[0-9A-Fa-f]{64}\z' `
            -or $expected.ContainsKey($relative)) { throw 'Unexpected coordinator publish file or source.' }
        $path = Join-Path $Directory $relative
        $file = Get-Item -LiteralPath $path -Force
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 `
            -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ine $fields[2]) {
            throw 'Coordinator output differs from its SDK source hash.'
        }
        $expected.Add($relative, [pscustomobject]@{ path = $relative; sha256 = $fields[2].ToLowerInvariant(); length = $file.Length })
    }
    if ($WithPackagingWrappers) {
        foreach ($name in @('Install-Package.ps1', 'Setup-Process.psm1')) {
            $source = Join-Path $PSScriptRoot $name
            $path = Join-Path $Directory $name
            $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
            if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hash) { throw 'Published wrapper differs from its fixed packaging source.' }
            $expected.Add($name, [pscustomobject]@{ path = $name; sha256 = $hash; length = (Get-Item -LiteralPath $path).Length })
        }
    }
    $actual = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    if (@($actual | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count -gt 0 `
        -or @($actual | Where-Object { -not $_.PSIsContainer }).Count -ne $expected.Count) {
        throw 'Unexpected coordinator publish entries.'
    }
    foreach ($file in $actual | Where-Object { -not $_.PSIsContainer }) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName).Replace('\', '/')
        if (-not $expected.ContainsKey($relative) -or $expected[$relative].path -cne $relative) { throw 'Coordinator output inventory mismatch.' }
    }
    return @($expected.Values | Sort-Object path)
}

function Get-HermesManagedModuleMvid {
    param([Parameter(Mandatory = $true)][string]$Path)
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($Path)
    try {
        $image = [System.Reflection.PortableExecutable.PEReader]::new($stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($image)
            $mvid = $metadata.GetGuid($metadata.GetModuleDefinition().Mvid)
            if ($mvid -eq [Guid]::Empty) { throw 'Managed module identity is empty.' }
            return $mvid.ToString('D')
        } finally { $image.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-HermesServicingAbi {
    param([Parameter(Mandatory = $true)][string]$AssemblyPath)
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::OpenRead($AssemblyPath)
    try {
        $image = [System.Reflection.PortableExecutable.PEReader]::new($stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        try {
            $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($image)
            $values = @(
                foreach ($handle in $metadata.TypeDefinitions) {
                    $type = $metadata.GetTypeDefinition($handle)
                    if ($metadata.GetString($type.Namespace) -cne 'Hermes.Servicing' -or $metadata.GetString($type.Name) -cne 'HermesServicingProtocol') { continue }
                    foreach ($fieldHandle in $type.GetFields()) {
                        $field = $metadata.GetFieldDefinition($fieldHandle)
                        if ($metadata.GetString($field.Name) -cne 'AdmissionAbi') { continue }
                        $constant = $metadata.GetConstant($field.GetDefaultValue())
                        if ($constant.TypeCode -ne [System.Reflection.Metadata.ConstantTypeCode]::Int32) { throw 'The compiled servicing ABI is not a literal Int32.' }
                        $blob = $metadata.GetBlobReader($constant.Value)
                        $value = $blob.ReadInt32()
                        if ($blob.RemainingBytes -ne 0) { throw 'Malformed compiled ABI constant.' }
                        $value
                    }
                }
            )
            if ($values.Count -ne 1) { throw 'Compiled servicing ABI is unavailable or ambiguous.' }
            return [int]$values[0]
        } finally { $image.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-HermesSdkFreeRuntime {
    param([Parameter(Mandatory = $true)][string]$Directory, [Parameter(Mandatory = $true)][string]$EntryName)
    foreach ($name in @("$EntryName.exe", "$EntryName.dll", "$EntryName.deps.json", "$EntryName.runtimeconfig.json",
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'Hermes.Servicing.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $Directory $name) -PathType Leaf)) { throw 'A required self-contained runtime file is absent.' }
    }
    $config = Get-Content -LiteralPath (Join-Path $Directory "$EntryName.runtimeconfig.json") -Raw | ConvertFrom-Json
    $options = $config.runtimeOptions
    $names = @($options.PSObject.Properties.Name)
    if ($names -contains 'framework' -or $names -contains 'frameworks' -or $names -notcontains 'includedFrameworks' `
        -or @($options.includedFrameworks | Where-Object { $_.name -ceq 'Microsoft.NETCore.App' -and $_.version -match '^9\.' }).Count -ne 1) {
        throw 'The published entry is not an SDK-free .NET 9 self-contained runtime.'
    }
    $deps = Get-Content -LiteralPath (Join-Path $Directory "$EntryName.deps.json") -Raw | ConvertFrom-Json
    if (-not $deps.runtimeTarget.name.EndsWith('/win-x64', [StringComparison]::Ordinal)) { throw 'The published runtime is not win-x64.' }
    if ((Get-HermesServicingAbi (Join-Path $Directory 'Hermes.Servicing.dll')) -ne 1) { throw 'The published runtime does not implement the frozen A2 admission ABI.' }
    $stream = [IO.File]::OpenRead((Join-Path $Directory "$EntryName.dll"))
    try {
        $image = [System.Reflection.PortableExecutable.PEReader]::new($stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        try {
            if ($null -eq $image.PEHeaders.CorHeader -or $image.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress -eq 0) {
                throw 'The runtime entry assembly is a library or has no actual managed entrypoint.'
            }
        } finally { $image.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-HermesNativeGuard {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$ExpectedSha256)
    if ($env:OS -ne 'Windows_NT') { throw 'Native guard verification requires Windows.' }
    if ($ExpectedSha256 -cnotmatch '\A[0-9a-f]{64}\z') { throw 'Native guard identity must be canonical lowercase SHA-256.' }
    Add-Type -AssemblyName System.Reflection.Metadata
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $image = [System.Reflection.PortableExecutable.PEReader]::new($stream, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
        try {
            if ($null -ne $image.PEHeaders.CorHeader -or $null -eq $image.PEHeaders.PEHeader `
                -or $image.PEHeaders.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64 `
                -or ($image.PEHeaders.CoffHeader.Characteristics -band [System.Reflection.PortableExecutable.Characteristics]::Dll) -eq 0) {
                throw 'The servicing guard is not the actual x64 NativeAOT shared library.'
            }
        } finally { $image.Dispose() }
        $stream.Position = 0
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $actual = [Convert]::ToHexString($hasher.ComputeHash($stream)).ToLowerInvariant() } finally { $hasher.Dispose() }
        if ($actual -cne $ExpectedSha256) { throw 'The native guard no longer matches its independent build fingerprint.' }
        $library = [Runtime.InteropServices.NativeLibrary]::Load($Path)
        try {
            foreach ($name in @('HermesPreflight', 'HermesTransactionFence')) {
                if ([Runtime.InteropServices.NativeLibrary]::GetExport($library, $name) -eq [IntPtr]::Zero) { throw 'The native servicing export is absent.' }
            }
        } finally { [Runtime.InteropServices.NativeLibrary]::Free($library) }
    } finally { $stream.Dispose() }
}

Export-ModuleMember -Function ConvertTo-HermesGuardHashList, Get-HermesCompiledGuardTrust, Get-HermesSetupPublishFiles, Get-HermesServicingAbi, Assert-HermesSdkFreeRuntime, Assert-HermesNativeGuard, Get-HermesManagedModuleMvid
