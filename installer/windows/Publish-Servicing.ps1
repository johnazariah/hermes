#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [string]$CompatibleGuardSha256 = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true' `
    -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Native servicing publication requires the dedicated disposable GitHub-hosted Windows runner.'
}
Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force
$output = [IO.Path]::GetFullPath($OutputDirectory)
$workspace = [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd('\')
if (-not $output.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Published servicing media must remain in the disposable workspace, outside AppRoot.'
}
if ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0) {
    throw 'Servicing publication requires a fresh directory; existing files are never deleted.'
}
$setupProject = Join-Path $PSScriptRoot 'Hermes.Setup/Hermes.Setup.csproj'
$project = [xml](Get-Content -LiteralPath $setupProject -Raw)
if ($project.Project.PropertyGroup.OutputType -cne 'Exe' -or $project.Project.PropertyGroup.IsPublishable -cne 'true') {
    throw 'The actual executable composition must exist before preparing servicing media.'
}
$guardDirectory = Join-Path $output 'guard'
$setupDirectory = Join-Path $output 'setup'
& dotnet publish (Join-Path $PSScriptRoot 'Hermes.Installer.Guard/Hermes.Installer.Guard.csproj') `
    -c Release -r win-x64 -o $guardDirectory -v q
if ($LASTEXITCODE -ne 0) { throw 'NativeAOT guard publication failed.' }
$guard = Join-Path $guardDirectory 'Hermes.Installer.Guard.dll'
$pin = [IO.File]::Open($guard, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    Add-Type -AssemblyName System.Reflection.Metadata
    $image = [System.Reflection.PortableExecutable.PEReader]::new($pin, [System.Reflection.PortableExecutable.PEStreamOptions]::LeaveOpen)
    try {
        if ($null -ne $image.PEHeaders.CorHeader -or $null -eq $image.PEHeaders.PEHeader `
            -or $image.PEHeaders.CoffHeader.Machine -ne [System.Reflection.PortableExecutable.Machine]::Amd64 `
            -or ($image.PEHeaders.CoffHeader.Characteristics -band [System.Reflection.PortableExecutable.Characteristics]::Dll) -eq 0) {
            throw 'The guard must be the actual x64 native shared-library output, not its managed build assembly.'
        }
    } finally { $image.Dispose() }
    $library = [Runtime.InteropServices.NativeLibrary]::Load($guard)
    try {
        foreach ($export in @('HermesPreflight', 'HermesTransactionFence')) {
            if ([Runtime.InteropServices.NativeLibrary]::GetExport($library, $export) -eq [IntPtr]::Zero) { throw 'Native guard export is absent.' }
        }
    } finally { [Runtime.InteropServices.NativeLibrary]::Free($library) }
    $pin.Position = 0
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $guardHash = [Convert]::ToHexString($hasher.ComputeHash($pin)).ToLowerInvariant() }
    finally { $hasher.Dispose() }
    # This independently built binary is hashed before any MSI candidate exists.
    $approved = ConvertTo-HermesGuardHashList -CurrentHash $guardHash -CompatibleHashes $CompatibleGuardSha256
    $escapedApproved = $approved.Replace(';', '%3B')
    & dotnet publish $setupProject -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=false "-p:HermesApprovedGuardSha256=$escapedApproved" `
        "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $PSScriptRoot 'Publish-Inventory.targets')" `
        "-p:HermesPublishInventoryPath=$(Join-Path $output 'setup.publish.txt')" -o $setupDirectory -v q
    if ($LASTEXITCODE -ne 0) { throw 'Trusted SDK-free coordinator publication failed.' }
    $setupFiles = @(Get-HermesSetupPublishFiles -Directory $setupDirectory -InventoryPath (Join-Path $output 'setup.publish.txt'))
    $compiled = Get-HermesCompiledGuardTrust -AssemblyPath (Join-Path $setupDirectory 'Hermes.Setup.dll')
    if ($compiled -cne $approved) { throw 'Published Setup compiled trust differs from the independently selected guard inputs.' }
    Assert-HermesSdkFreeRuntime -Directory $setupDirectory -EntryName 'Hermes.Setup'
    foreach ($name in @('Hermes.Setup.exe', 'Hermes.Setup.dll', 'Hermes.Setup.deps.json', 'Hermes.Setup.runtimeconfig.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $setupDirectory $name) -PathType Leaf)) { throw 'Coordinator publication is incomplete.' }
    }
    foreach ($name in @('Install-Package.ps1', 'Setup-Process.psm1')) {
        $source = Join-Path $PSScriptRoot $name
        Copy-Item -LiteralPath $source -Destination (Join-Path $setupDirectory $name)
        $setupFiles += [pscustomobject]@{ path = $name; sha256 = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant(); length = (Get-Item -LiteralPath $source).Length }
    }
    $record = [ordered]@{
        schemaVersion = 1; admissionAbi = 1; guardPath = $guard; guardSha256 = $guardHash
        approvedGuardSha256 = $approved; setupExecutable = (Join-Path $setupDirectory 'Hermes.Setup.exe')
        setupFiles = $setupFiles
    }
    $record | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'servicing-build.json') -Encoding utf8NoBOM
} finally { $pin.Dispose() }
Write-Output (Join-Path $output 'servicing-build.json')
