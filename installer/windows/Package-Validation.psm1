#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-HermesUnredirectedPath([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    while ($null -ne $item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Validator provenance forbids redirected paths: $Path" }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}

function Open-HermesPinnedWixValidator([string]$WixPath) {
    # Independently fetched from the public NuGet flat-container URL, not a
    # candidate-supplied hash, assembly version, .sha512 sidecar or tool output.
    # https://api.nuget.org/v3-flatcontainer/wixtoolset.sdk/6.0.0/wixtoolset.sdk.6.0.0.nupkg
    $pinnedSha256 = '48ef1b10e6c0c06a40544ca0cabadfe1428bf2fe75b848857233467e2d9353a2'
    $wix = [IO.Path]::GetFullPath($WixPath)
    $root = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($wix)))
    $expectedWix = [IO.Path]::GetFullPath((Join-Path $root 'tools/net6.0/wix.dll'))
    if ($wix -cne $expectedWix) { throw 'Validator must be the pinned SDK tools/net6.0/wix.dll.' }
    $package = Join-Path $root 'wixtoolset.sdk.6.0.0.nupkg'
    $leases = [Collections.Generic.List[IDisposable]]::new()
    $archive = $null
    try {
        Assert-HermesUnredirectedPath $package
        $stream = [IO.File]::Open($package, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $leases.Add($stream)
        $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        if ($digest -cne $pinnedSha256) { throw 'Validator SDK archive is not the independently pinned WiX 6.0.0 distribution.' }
        $stream.Position = 0
        $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        $entries = @($archive.Entries | Where-Object { $_.FullName.StartsWith('tools/net6.0/', [StringComparison]::Ordinal) -and $_.Name })
        $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $entries) { [void]$expected.Add($entry.FullName) }
        $toolRoot = Join-Path $root 'tools/net6.0'
        Assert-HermesUnredirectedPath $toolRoot
        foreach ($item in Get-ChildItem -LiteralPath $toolRoot -Recurse -Force) {
            Assert-HermesUnredirectedPath $item.FullName
            if (-not $item.PSIsContainer -and -not $expected.Remove([IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/'))) {
                throw 'Validator SDK contains an unexpected tool file.'
            }
        }
        if ($expected.Count) { throw 'Validator SDK is missing a pinned tool file.' }
        $metadata = @('Sdk/Sdk.props', 'Sdk/Sdk.targets', 'tools/wix.props', 'tools/wix.targets')
        foreach ($name in @($entries.FullName) + $metadata) {
            $path = Join-Path $root $name
            Assert-HermesUnredirectedPath $path
            $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
            $leases.Add($file)
            $reference = $archive.GetEntry($name).Open()
            try {
                $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($file))
                $referenceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($reference))
                if ($actualHash -cne $referenceHash) { throw "Validator SDK file differs from the pinned distribution: $name" }
            } finally { $reference.Dispose() }
        }
        # Retain read-only, non-delete-shared handles through process completion.
        # This pins the executable, dependencies, native helper and ICE cubes,
        # rather than authenticating only the small CLI entry assembly.
        [pscustomobject]@{ WixPath = $expectedWix; PackageSha256 = $digest; Leases = $leases }
    } catch {
        foreach ($lease in $leases) { $lease.Dispose() }
        throw
    } finally { if ($null -ne $archive) { $archive.Dispose() } }
}

function Get-HermesPayloadDirectoryId([string]$Path) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Path.ToLowerInvariant())
    'Dir_' + [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant().Substring(0, 32)
}

function Get-HermesLongTargetName([string]$Name) {
    $parts = $Name.Split('|')
    if ($parts.Count -gt 2 -or $parts[0] -ceq '' -or $parts[-1] -ceq '' -or $Name.Contains(':')) {
        throw 'MSI target names must be unambiguous long names, optionally paired with a short name.'
    }
    $parts[-1]
}

function Assert-HermesIceFileBindings($Manifest, [hashtable]$Facts) {
    $files = @($Manifest.files)
    if (-not $files.Count -or @($Facts.files).Count -ne $files.Count) {
        throw 'ICE91 policy requires the complete manifest and actual MSI File inventory to agree.'
    }
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $fileIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $componentIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $files) {
        if ($file.path -cnotmatch '^[A-Za-z0-9_@.+-]+(?:/[A-Za-z0-9_@.+-]+)+$' `
            -or @($file.path.Split('/') | Where-Object { $_ -in @('.', '..') -or $_.EndsWith('.') }).Count `
            -or -not $paths.Add($file.path) -or -not $fileIds.Add($file.fileId) -or -not $componentIds.Add($file.componentId)) {
            throw 'ICE91 policy requires unique, safe manifest paths and file/component identities.'
        }
        $rows = @($Facts.files | Where-Object { $_[0] -ceq $file.fileId })
        if ($rows.Count -ne 1 -or $rows[0][1] -cne $file.componentId `
            -or (Get-HermesLongTargetName $rows[0][2]) -cne $file.path.Split('/')[-1] `
            -or [long]$rows[0][3] -ne [long]$file.length -or [long]$file.length -lt 0) {
            throw 'ICE91 policy requires actual MSI file names, component bindings and lengths to match the complete manifest.'
        }
        $component = @($Facts.components | Where-Object { $_[0] -ceq $file.componentId })
        $directory = Get-HermesPayloadDirectoryId $file.path.Substring(0, $file.path.LastIndexOf('/'))
        if ($component.Count -ne 1 -or $component[0][2] -cne $directory `
            -or [guid]$component[0][1] -eq [guid]::Empty -or [guid]$component[0][1] -ne [guid]$file.componentCode) {
            throw 'ICE91 policy requires actual MSI component GUIDs and target directories to match the manifest.'
        }
    }
}

function Assert-HermesIcePrerequisites($Manifest, [hashtable]$Facts) {
    Assert-HermesIceFileBindings $Manifest $Facts
    foreach ($identity in @(@('ProductCode', 'productCode'), @('UpgradeCode', 'upgradeCode'), @('ProductVersion', 'version'))) {
        $rows = @($Facts.properties | Where-Object { $_[0] -ceq $identity[0] })
        $expected = $Manifest.($identity[1])
        if ($rows.Count -ne 1 -or [string]::IsNullOrWhiteSpace($expected)) { throw 'ICE policy requires the actual MSI product identity.' }
        if ($identity[0] -ceq 'ProductVersion') {
            if ($rows[0][1] -cne $expected) { throw 'ICE policy product version differs from the manifest.' }
        } elseif ([guid]$expected -eq [guid]::Empty -or [guid]$rows[0][1] -ne [guid]$expected) {
            throw 'ICE policy product GUID differs from the manifest.'
        }
    }
    if ([guid]$Manifest.packageCode -eq [guid]::Empty -or [guid]$Facts.packageCode -ne [guid]$Manifest.packageCode) {
        throw 'ICE policy requires the finalised actual MSI package identity.'
    }
    if ($Manifest.scope -cne 'perUser' -or $Manifest.root -cne 'LocalApplicationData/Hermes/App' `
        -or [int]$Facts.wordCount -ne 10) { throw 'ICE policy requires the fixed compressed per-user package.' }
    if (@($Facts.properties | Where-Object { $_[0] -in @('ALLUSERS', 'MSIINSTALLPERUSER') }).Count `
        -or @($Facts.properties | Where-Object { $_[0] -ceq 'INSTALLFOLDER' -and $_[1] -cne '' }).Count) {
        throw 'ICE policy cannot approve machine/dual scope or an authored root override.'
    }
    $removal = @($Facts.upgrades | Where-Object { $_[4] -ceq 'HERMES_RELATED_PRODUCTS' })
    if ($removal.Count -ne 1 -or $removal[0][0] -cne '0.0.0' -or $removal[0][1] -cne '' `
        -or [int]$removal[0][2] -ne 256 -or $removal[0][3] -cne '') {
        throw 'ICE61 policy applies only to intentional all-version, whole-product removal.'
    }
    foreach ($pair in @(@('InstallInitialize', '1500'), @('HermesTransactionFence', '1501'), @('RemoveExistingProducts', '1502'))) {
        $rows = @($Facts.sequence | Where-Object { $_[0] -ceq $pair[0] })
        if ($rows.Count -ne 1 -or $rows[0][2] -cne $pair[1]) { throw 'ICE63 policy requires the frozen transaction fence positions.' }
    }
    $between = @($Facts.sequence | Where-Object { [int]$_[2] -gt 1500 -and [int]$_[2] -lt 1502 })
    $fence = @($Facts.actions | Where-Object { $_[0] -ceq 'HermesTransactionFence' })
    if ($between.Count -ne 1 -or $between[0][0] -cne 'HermesTransactionFence' -or $between[0][1] -cne '' `
        -or $fence.Count -ne 1 -or [int]$fence[0][1] -ne 1 -or $fence[0][2] -cne 'HermesServicingGuard' `
        -or $fence[0][3] -cne 'HermesTransactionFence') {
        throw 'ICE63 policy permits only the unconditional immediate DLL fence, never a script-generating action.'
    }
    $expected = @(
        ,@('HERMES_COMPANY', 'LocalAppDataFolder', 'Hermes')
        ,@('INSTALLFOLDER', 'HERMES_COMPANY', 'App')
    )
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $Manifest.files) {
        $parts = $file.path.Split('/')
        for ($index = 1; $index -lt $parts.Length; $index++) {
            $path = $parts[0..($index - 1)] -join '/'
            if ($seen.Add($path)) {
                $parent = if ($index -eq 1) { 'INSTALLFOLDER' } else { Get-HermesPayloadDirectoryId ($parts[0..($index - 2)] -join '/') }
                $expected += ,@((Get-HermesPayloadDirectoryId $path), $parent, $parts[$index - 1])
            }
        }
    }
    foreach ($directory in $expected) {
        $rows = @($Facts.directories | Where-Object { $_[0] -ceq $directory[0] })
        if ($rows.Count -ne 1 -or $rows[0][1] -cne $directory[1] -or (Get-HermesLongTargetName $rows[0][2]) -cne $directory[2]) {
            throw 'ICE91 policy requires actual MSI directories to retain the manifest path under the fixed known-folder root.'
        }
    }
}

function Read-HermesIceOutput([Diagnostics.Process]$Process) {
    $stdout = $Process.StandardOutput.ReadToEndAsync()
    $stderr = $Process.StandardError.ReadToEndAsync()
    $Process.WaitForExit()
    $stdoutText = $stdout.GetAwaiter().GetResult()
    $stderrText = $stderr.GetAwaiter().GetResult()
    # Each stream has its own line boundary, even without a final newline.
    [pscustomobject]@{
        Text = $stdoutText + $stderrText
        Lines = @($stdoutText -split '\r?\n'; $stderrText -split '\r?\n')
        StandardOutput = $stdoutText
        StandardError = $stderrText
    }
}

function Assert-HermesIceDiagnostics {
    param(
        [Parameter(Mandatory = $true)]$Manifest,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][AllowEmptyString()][string[]]$Lines,
        [Parameter(Mandatory = $true)][int]$NativeExitCode
    )
    if ($NativeExitCode -ne 0) { throw "Complete native MSI validation failed with exit code $NativeExitCode." }
    # These are exact native WiX 6 messages, not an ICE-wide or WIX1076 exemption.
    # ICE61: ADR33 deliberately removes all versions; downgrade admission remains guarded.
    # https://learn.microsoft.com/windows/win32/msi/ice61
    # A NULL VersionMax explicitly includes every version above the lower bound.
    # https://learn.microsoft.com/windows/win32/msi/upgrade-table
    # ICE63 explicitly permits intervening actions that do not generate script actions.
    # https://learn.microsoft.com/windows/win32/msi/ice63
    # ICE91 explicitly permits this layout for packages never used per-machine.
    # https://learn.microsoft.com/windows/win32/msi/ice91
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    [void]$expected.Add('ICE61: This product should remove only older versions of itself. No Maximum version was detected for the current product. (HERMES_RELATED_PRODUCTS)')
    [void]$expected.Add('ICE63: Some action falls between InstallInitialize and RemoveExistingProducts.')
    foreach ($file in $Manifest.files) {
        $directory = Get-HermesPayloadDirectoryId ($file.path.Substring(0, $file.path.LastIndexOf('/')))
        $message = "ICE91: The file '$($file.fileId)' will be installed to the per user directory '$directory' that doesn't vary based on ALLUSERS value. This file won't be copied to each user's profile even if a per machine installation is desired."
        if (-not $expected.Add($message)) { throw 'ICE policy requires unique manifest file identities.' }
    }
    $count = $expected.Count
    foreach ($line in $Lines) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line -cnotmatch '^.+: warning WIX1076: (?<message>ICE[0-9]+: .+)$' `
            -or -not $expected.Remove($Matches.message)) {
            throw "Unexpected or duplicate native validation diagnostic: $line"
        }
    }
    if ($expected.Count) { throw "Native validation omitted $($expected.Count) expected policy diagnostics." }
    Write-Output "Complete native ICE validation passed with $count precisely matched, prerequisite-checked policy warnings."
}

Export-ModuleMember -Function Open-HermesPinnedWixValidator, Assert-HermesIcePrerequisites, Read-HermesIceOutput, Assert-HermesIceDiagnostics
