#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Package-Validation.psm1') -Force

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Refuse([scriptblock]$Action, [string]$Message, [string]$ExpectedPrefix = '') {
    $refused = $false
    try { & $Action | Out-Null } catch {
        if ($ExpectedPrefix -and -not $_.Exception.Message.StartsWith($ExpectedPrefix, [StringComparison]::Ordinal)) {
            throw "Wrong refusal: $($_.Exception.Message); expected $ExpectedPrefix"
        }
        $refused = $true
    }
    Require $refused $Message
}

function Get-SyntheticProcessOutput([string]$Stdout, [string]$Stderr, [int]$ExitCode = 0) {
    $payload = @{ stdout = $Stdout; stderr = $Stderr; exitCode = $ExitCode } | ConvertTo-Json -Compress
    $encodedPayload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($payload))
    $child = '$data = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String("' + $encodedPayload + '")) | ConvertFrom-Json; ' +
        '[Console]::Out.Write([string]$data.stdout); [Console]::Error.Write([string]$data.stderr); exit [int]$data.exitCode'
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('-NoProfile', '-NonInteractive', '-EncodedCommand', [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($child)))) {
        [void]$start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = Read-HermesIceOutput $process
        Require ($output.StandardOutput -ceq $Stdout -and $output.StandardError -ceq $Stderr) 'Synthetic child streams must be captured without losing or rewriting output.'
        Require ($output.Text -ceq ($Stdout + $Stderr)) 'The retained transcript must include both complete, unmodified streams.'
        $expectedLines = @($Stdout -split '\r?\n'; $Stderr -split '\r?\n')
        Require ($output.Lines.Count -eq $expectedLines.Count -and ($output.Lines -join "`0") -ceq ($expectedLines -join "`0")) 'Captured diagnostic lines must preserve empty elements and independent stream boundaries.'
        $output | Add-Member -NotePropertyName ExitCode -NotePropertyValue $process.ExitCode
        $output
    } finally { $process.Dispose() }
}

function New-ManifestFile([string]$Path, [long]$Length) {
    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Path))).ToLowerInvariant()
    [pscustomobject]@{
        path = $Path; length = $Length; fileId = "File_Payload_$($hash.Substring(0, 32))"
        componentId = "Payload_$($hash.Substring(0, 32))"
        componentCode = "$($hash.Substring(0, 8))-$($hash.Substring(8, 4))-5$($hash.Substring(13, 3))-a$($hash.Substring(17, 3))-$($hash.Substring(20, 12))"
    }
}

$path = 'service/test.dll'
$directoryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('service'))).ToLowerInvariant()
$directoryId = "Dir_$($directoryHash.Substring(0, 32))"
$assetDirectoryHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('service/long-directory'))).ToLowerInvariant()
$assetDirectoryId = "Dir_$($assetDirectoryHash.Substring(0, 32))"
$files = @((New-ManifestFile $path 15), (New-ManifestFile 'service/long-directory/ordinary-file.json' 17))
$fileId = $files[0].fileId
$componentId = $files[0].componentId
$manifest = [pscustomobject]@{
    scope = 'perUser'; root = 'LocalApplicationData/Hermes/App'
    productCode = '11111111-1111-1111-1111-111111111111'
    packageCode = '22222222-2222-2222-2222-222222222222'
    upgradeCode = '7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A'; version = '0.1.0'
    files = $files
}
$facts = @{
    wordCount = 10
    packageCode = '{22222222-2222-2222-2222-222222222222}'
    properties = @(
        ,@('ProductCode', '{11111111-1111-1111-1111-111111111111}')
        ,@('UpgradeCode', '{7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A}')
        ,@('ProductVersion', '0.1.0')
    )
    upgrades = @(,@('0.0.0', '', '256', '', 'HERMES_RELATED_PRODUCTS'))
    actions = @(,@('HermesTransactionFence', '1', 'HermesServicingGuard', 'HermesTransactionFence'))
    sequence = @(
        ,@('InstallInitialize', '', '1500')
        ,@('HermesTransactionFence', '', '1501')
        ,@('RemoveExistingProducts', 'HERMES_RELATED_PRODUCTS', '1502')
    )
    directories = @(
        ,@('HERMES_COMPANY', 'LocalAppDataFolder', 'Hermes')
        ,@('INSTALLFOLDER', 'HERMES_COMPANY', 'App')
        ,@($directoryId, 'INSTALLFOLDER', 'service')
        ,@($assetDirectoryId, $directoryId, 'LONGDI~1|long-directory')
    )
    files = @($files | ForEach-Object {
        ,@($_.fileId, $_.componentId, $_.path.Split('/')[-1], "$($_.length)")
    })
    components = @(
        ,@($files[0].componentId, $files[0].componentCode, $directoryId)
        ,@($files[1].componentId, $files[1].componentCode, $assetDirectoryId)
    )
}
Assert-HermesIcePrerequisites $manifest $facts
$shortNames = $facts.Clone()
$shortNames.files = @(
    ,@($files[0].fileId, $files[0].componentId, 'TEST~1.DLL|test.dll', '15')
    ,@($files[1].fileId, $files[1].componentId, 'ORDINA~1.JSO|ordinary-file.json', '17')
)
Assert-HermesIcePrerequisites $manifest $shortNames
$warnings = @(
    'ICE61: This product should remove only older versions of itself. No Maximum version was detected for the current product. (HERMES_RELATED_PRODUCTS)'
    'ICE63: Some action falls between InstallInitialize and RemoveExistingProducts.'
    "ICE91: The file '$fileId' will be installed to the per user directory '$directoryId' that doesn't vary based on ALLUSERS value. This file won't be copied to each user's profile even if a per machine installation is desired."
    "ICE91: The file '$($files[1].fileId)' will be installed to the per user directory '$assetDirectoryId' that doesn't vary based on ALLUSERS value. This file won't be copied to each user's profile even if a per machine installation is desired."
)
$lines = @($warnings | ForEach-Object { "SYNTHETIC.wxs(1): warning WIX1076: $_" })
Assert-HermesIceDiagnostics -Manifest $manifest -Lines $lines -NativeExitCode 0 | Out-Null
foreach ($streams in @(
    @{ stdout = ($lines -join "`n") + "`n"; stderr = '' },
    @{ stdout = ''; stderr = ($lines -join "`r`n") + "`r`n" },
    @{ stdout = "`n" + ($lines -join "`n`n") + "`n`n"; stderr = "`r`n" },
    @{ stdout = $lines[0]; stderr = ($lines[1..3] -join "`r`n") + "`r`n" },
    @{ stdout = ($lines[0..1] -join "`r`n") + "`r`n"; stderr = "`n" + ($lines[2..3] -join "`n") + "`n" },
    @{ stdout = $lines -join "`n"; stderr = '' }
)) {
    $output = Get-SyntheticProcessOutput $streams.stdout $streams.stderr
    Assert-HermesIceDiagnostics -Manifest $manifest -Lines $output.Lines -NativeExitCode $output.ExitCode | Out-Null
}
foreach ($streams in @(
    @{ stdout = ''; stderr = ''; reason = 'Native validation omitted 4 expected policy diagnostics.' },
    @{ stdout = "`n`n"; stderr = "`r`n"; reason = 'Native validation omitted 4 expected policy diagnostics.' },
    @{ stdout = $lines[0..2] -join "`n"; stderr = ''; reason = 'Native validation omitted 1 expected policy diagnostics.' },
    @{ stdout = ($lines -join "`n") + "`n"; stderr = $lines[0] + "`r`n"; reason = 'Unexpected or duplicate native validation diagnostic:' },
    @{ stdout = ($lines -join "`n") + "`n"; stderr = 'SYNTHETIC: warning WIX9999: Unknown.'; reason = 'Unexpected or duplicate native validation diagnostic:' },
    @{ stdout = ($lines -join "`n") + "`n"; stderr = 'SYNTHETIC: error WIX0204: ICE72: DLL action forbidden.'; reason = 'Unexpected or duplicate native validation diagnostic:' },
    @{ stdout = ($lines -join "`n").Replace($fileId, 'File_Forged'); stderr = ''; reason = 'Unexpected or duplicate native validation diagnostic:' },
    @{ stdout = ($lines[0..2] -join "`n") + "`n" + $lines[3].Substring(0, $lines[3].Length - 1); stderr = ''; reason = 'Unexpected or duplicate native validation diagnostic:' },
    @{ stdout = ($lines[0..2] -join "`n") + "`n" + $lines[3].Substring(0, 30); stderr = $lines[3].Substring(30); reason = 'Unexpected or duplicate native validation diagnostic:' }
)) {
    $output = Get-SyntheticProcessOutput $streams.stdout $streams.stderr
    Refuse { Assert-HermesIceDiagnostics -Manifest $manifest -Lines $output.Lines -NativeExitCode $output.ExitCode } `
        'Captured empty, incomplete, duplicate, foreign, truncated or cross-stream-forged diagnostics must fail classification.' $streams.reason
}
$output = Get-SyntheticProcessOutput (($lines -join "`n") + "`n") '' 7
Refuse { Assert-HermesIceDiagnostics -Manifest $manifest -Lines $output.Lines -NativeExitCode $output.ExitCode } `
    'Real nonzero child exit must fail even with the complete valid transcript.' 'Complete native MSI validation failed with exit code 7.'
Write-Output 'Real synthetic child-process stdout/stderr controls passed: 6 valid layouts and 10 rejected outputs/exits; no native validator was executed.'
foreach ($exitCode in @(1, 204, 1603)) {
    Refuse { Assert-HermesIceDiagnostics $manifest $lines $exitCode } 'A failing native validator must never be accepted.'
}
foreach ($replacement in @(
    'error WIX1076: ICE63: Some action falls between InstallInitialize and RemoveExistingProducts.',
    'warning WIX1076: ICE63: Unknown warning from the same ICE.',
    'warning WIX1076: ICE61: This product should remove only older versions of itself. Different range.',
    'warning WIX1076: ICE64: A user profile directory is missing from RemoveFile.',
    'warning WIX9999: Unrecognised diagnostic.',
    'error WIX0204: ICE72: DLL action forbidden.',
    'Unparsed native output must fail closed.'
)) {
    Refuse { Assert-HermesIceDiagnostics $manifest @($lines + "SYNTHETIC.wxs(1): $replacement") 0 } 'Unknown warnings/errors or native output must fail.'
}
foreach ($badLines in @(
    ,@($lines + $lines[0])
    ,@($lines[0..1])
    ,@($lines | ForEach-Object { $_.Replace($fileId, 'File_Unknown') })
    ,@($lines | ForEach-Object { $_.Replace($directoryId, 'Dir_Unknown') })
    ,@($lines | ForEach-Object { $_.Replace("doesn't", 'does not') })
)) {
    Refuse { Assert-HermesIceDiagnostics $manifest $badLines 0 } 'Duplicate, missing, foreign or changed native messages must fail.'
}
foreach ($patch in @(
    @{ wordCount = 2 },
    @{ properties = @($facts.properties + ,@('ALLUSERS', '1')) },
    @{ properties = @($facts.properties + ,@('MSIINSTALLPERUSER', '1')) },
    @{ properties = @($facts.properties + ,@('INSTALLFOLDER', 'DifferentRoot')) },
    @{ properties = @($facts.properties | Where-Object { $_[0] -cne 'ProductCode' }) },
    @{ packageCode = '{33333333-3333-3333-3333-333333333333}' },
    @{ upgrades = @(,@('0.0.0', '1.0.0', '256', '', 'HERMES_RELATED_PRODUCTS')) },
    @{ upgrades = @(,@('0.0.0', '', '258', '', 'HERMES_RELATED_PRODUCTS')) },
    @{ actions = @(,@('HermesTransactionFence', '1025', 'HermesServicingGuard', 'HermesTransactionFence')) },
    @{ actions = @(,@('HermesTransactionFence', '1', 'AnotherBinary', 'HermesTransactionFence')) },
    @{ directories = @(
        ,@('HERMES_COMPANY', 'AppDataFolder', 'Hermes')
        ,@('INSTALLFOLDER', 'HERMES_COMPANY', 'App')
        ,@($directoryId, 'INSTALLFOLDER', 'service')
    ) },
    @{ directories = @(
        ,@('HERMES_COMPANY', 'LocalAppDataFolder', 'Hermes')
        ,@('INSTALLFOLDER', 'HERMES_COMPANY', 'Different')
        ,@($directoryId, 'INSTALLFOLDER', 'service')
    ) },
    @{ sequence = @(
        ,@('InstallInitialize', '', '1500')
        ,@('HermesTransactionFence', 'A_PROPERTY', '1501')
        ,@('RemoveExistingProducts', 'HERMES_RELATED_PRODUCTS', '1502')
    ) }
)) {
    $changed = $facts.Clone()
    foreach ($name in $patch.Keys) { $changed[$name] = $patch[$name] }
    Refuse { Assert-HermesIcePrerequisites $manifest $changed } 'Changed native prerequisites must not receive a warning exemption.'
}
foreach ($patch in @(
    @{ files = @(
        ,$facts.files[0]
        ,@($files[1].fileId, $files[1].componentId, 'renamed-but-same-id.json', '17')
    ) },
    @{ files = @(
        ,$facts.files[0]
        ,@($files[1].fileId, $files[1].componentId, 'ORDINA~1.JSO|renamed.json', '17')
    ) },
    @{ components = @(
        ,$facts.components[0]
        ,@($files[1].componentId, $files[1].componentCode, $directoryId)
    ) },
    @{ files = @(
        ,$facts.files[0]
        ,@($files[1].fileId, $componentId, 'ordinary-file.json', '17')
    ) },
    @{ files = @($facts.files + ,@('File_Extra', 'Payload_Extra', 'extra.dll', '17')) },
    @{ files = @(,$facts.files[0]) },
    @{ files = @($facts.files[0], $facts.files[0]) },
    @{ components = @($facts.components + ,$facts.components[0]) },
    @{ components = @(
        ,$facts.components[0]
        ,@($files[1].componentId, '{33333333-3333-3333-3333-333333333333}', $assetDirectoryId)
    ) }
)) {
    $changed = $facts.Clone()
    foreach ($name in $patch.Keys) { $changed[$name] = $patch[$name] }
    # The exact native diagnostics are unchanged by these MSI-only mutations.
    # Passing that transcript must not make a renamed/reparented file eligible.
    Assert-HermesIceDiagnostics $manifest $lines 0 | Out-Null
    Refuse { Assert-HermesIcePrerequisites $manifest $changed } 'Unchanged warnings cannot approve a changed actual MSI file path or binding.'
}
$wrongManifest = $manifest | Select-Object *
$wrongManifest.scope = 'perMachine'
Refuse { Assert-HermesIcePrerequisites $wrongManifest $facts } 'Machine scope cannot receive a per-user warning exemption.'
$wrongManifest.scope = 'perUser'
$wrongManifest.root = 'LocalApplicationData/Elsewhere'
Refuse { Assert-HermesIcePrerequisites $wrongManifest $facts } 'A relocated manifest cannot receive a warning exemption.'

$sdkOutput = & dotnet msbuild (Join-Path $PSScriptRoot 'Hermes.Installer.wixproj') -nologo -getProperty:WixBinDir
if ($LASTEXITCODE -ne 0) { throw 'Declared WiX SDK resolution failed; provenance controls cannot be skipped.' }
$sdkWix = Join-Path (($sdkOutput -join "`n").Trim()) 'wix.dll'
$validated = Open-HermesPinnedWixValidator $sdkWix
try { Require ($validated.Leases.Count -gt 40) 'Provenance must retain the complete SDK validator closure, not only wix.dll.' }
finally { foreach ($lease in $validated.Leases) { $lease.Dispose() } }
$scratch = Join-Path $PSScriptRoot ("obj/validation-policy-" + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
try {
    $sdkRoot = Split-Path (Split-Path (Split-Path $sdkWix))
    $packageName = 'wixtoolset.sdk.6.0.0.nupkg'
    $archive = Join-Path $scratch $packageName
    Copy-Item -LiteralPath (Join-Path $sdkRoot $packageName) -Destination $archive
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $scratch)
    $candidate = Join-Path $scratch 'tools/net6.0/wix.dll'
    $validated = Open-HermesPinnedWixValidator $candidate
    try {
        Require ($validated.WixPath -ceq $candidate) 'An independently authenticated SDK copy must remain supported.'
        if ($env:OS -eq 'Windows_NT') {
            Refuse { $write = [IO.File]::OpenWrite($candidate); $write.Dispose() } 'Validated SDK files must remain write-locked through native completion.'
            Refuse { $write = [IO.File]::OpenWrite($archive); $write.Dispose() } 'The authenticated SDK archive must remain write-locked through native completion.'
        }
    } finally { foreach ($lease in $validated.Leases) { $lease.Dispose() } }
    foreach ($relative in @('tools/net6.0/wix.dll', 'tools/net6.0/WixToolset.Core.WindowsInstaller.dll',
        'tools/net6.0/runtimes/win-x64/native/wixnative.exe', 'tools/net6.0/cubes/darice.cub',
        'tools/net6.0/wix.deps.json', 'tools/net6.0/wix.runtimeconfig.json', 'Sdk/Sdk.targets')) {
        $target = Join-Path $scratch $relative
        $original = [IO.File]::ReadAllBytes($target)
        try {
            [IO.File]::WriteAllText($target, ($lines -join "`n"))
            Refuse { Open-HermesPinnedWixValidator $candidate } 'A substituted validator/dependency/cube cannot gain authority by emitting the complete accepted transcript.'
        } finally { [IO.File]::WriteAllBytes($target, $original) }
    }
    $extra = Join-Path $scratch 'tools/net6.0/wix.runtimeconfig.dev.json'
    [IO.File]::WriteAllText($extra, '{}')
    Refuse { Open-HermesPinnedWixValidator $candidate } 'Additional unpinned tool/runtime files must fail provenance.'
    Remove-Item -LiteralPath $extra
    $original = [IO.File]::ReadAllBytes($archive)
    try {
        [IO.File]::WriteAllBytes($archive, [byte[]]@($original + 0))
        Refuse { Open-HermesPinnedWixValidator $candidate } 'An SDK archive with the expected filename/version but different bytes must fail.'
    } finally { [IO.File]::WriteAllBytes($archive, $original) }
    $removed = Join-Path $scratch 'tools/net6.0/cubes/darice.cub'
    Remove-Item -LiteralPath $removed
    Refuse { Open-HermesPinnedWixValidator $candidate } 'Missing native validation data must fail, never invoke a partial/default fallback.'
    $overrideOutput = & dotnet msbuild (Join-Path $PSScriptRoot 'Hermes.Installer.wixproj') -nologo `
        "-p:WixBinDir=$([IO.Path]::GetDirectoryName($candidate))/" -getProperty:WixBinDir
    if ($LASTEXITCODE -ne 0) { throw 'SDK override regression fixture failed to evaluate.' }
    $overrideCandidate = Join-Path (($overrideOutput -join "`n").Trim()) 'wix.dll'
    Refuse { Open-HermesPinnedWixValidator $overrideCandidate } 'A global WixBinDir override is a candidate, not provenance authority.'
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
if ($env:OS -ne 'Windows_NT') {
    foreach ($control in @(
        @('Validate-PackageIce.ps1', 'Complete native MSI validation requires Windows; it never installs the package.'),
        @('Test-PackageIceInputs.ps1', 'Actual MSI input controls require disposable hosted Windows; they never install a package.')
    )) {
        $blocked = $false
        try {
            & (Join-Path $PSScriptRoot $control[0]) -PackagePath 'SYNTHETIC-never-opened.msi' `
                -ManifestPath 'SYNTHETIC-never-opened.json' -WixPath 'SYNTHETIC-never-executed.dll' `
                -WixpdbPath 'SYNTHETIC-never-opened.wixpdb' -IntermediateDirectory 'SYNTHETIC-never-created'
        } catch {
            if ($_.Exception.Message -cne $control[1]) { throw }
            $blocked = $true
        }
        Require $blocked 'Non-Windows validation must refuse before opening any artifact or executing WiX.'
    }
}
Write-Output 'Synthetic exact ICE-policy, actual file/path binding and pinned SDK provenance controls passed; no native MSI validation or profile operation was executed.'
