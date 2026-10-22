#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ProviderName = 'Hermes.Servicing.Evidence.Native'
$script:ProviderKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WINEVT\Publishers\{C760C1AB-37CB-4C3A-A245-3EA632FC9C5D}'

function Invoke-EvidenceTool([string]$Path, [string[]]$ToolArguments) {
    & $Path @ToolArguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Native evidence resource compilation/registration failed.' }
}

function Assert-EvidenceEnvironment {
    if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true' `
        -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
        throw 'Native diagnostic registration is restricted to disposable hosted Windows acceptance.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal = [Security.Principal.WindowsPrincipal]::new($identity)
        if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'The disposable harness must own elevated diagnostic registration; production does not enroll a provider.'
        }
    } finally { $identity.Dispose() }
}

function Assert-EvidenceVacant {
    if (Test-Path -LiteralPath $script:ProviderKey) {
        throw 'The diagnostic provider GUID already exists; refusing to replace it.'
    }
    $publishers = @(& (Join-Path $env:SystemRoot 'System32/wevtutil.exe') ep)
    if ($LASTEXITCODE -ne 0 -or $publishers -contains $script:ProviderName) {
        throw 'The diagnostic publisher name is unavailable; refusing registration.'
    }
}

function Get-EvidenceTools {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory |
        Where-Object { $_.Name -match '\A\d+\.\d+\.\d+\.\d+\z' `
            -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64/mc.exe')) `
            -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64/rc.exe')) } |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($null -eq $sdk) { throw 'Windows SDK manifest/resource compilers are required by the test harness.' }
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $vs = @(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $vs.Count -ne 1) { throw 'The test-only resource linker is unavailable.' }
    $vc = Get-ChildItem -LiteralPath (Join-Path $vs[0] 'VC/Tools/MSVC') -Directory |
        Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if ($null -eq $vc) { throw 'The test-only resource linker is unavailable.' }
    $link = Join-Path $vc.FullName 'bin/Hostx64/x64/link.exe'
    if (-not (Test-Path -LiteralPath $link -PathType Leaf)) { throw 'The x64 resource linker is unavailable.' }
    return @{ mc = (Join-Path $sdk.FullName 'x64/mc.exe')
        rc = (Join-Path $sdk.FullName 'x64/rc.exe'); link = $link }
}

function Test-EvidenceOwnership($Lease) {
    if (-not (Test-Path -LiteralPath $script:ProviderKey)) { return $false }
    $registration = Get-ItemProperty -LiteralPath $script:ProviderKey
    $resource = [Environment]::ExpandEnvironmentVariables([string]$registration.ResourceFileName)
    return [IO.Path]::GetFullPath($resource).Equals(
        [IO.Path]::GetFullPath($Lease.resource), [StringComparison]::OrdinalIgnoreCase)
}

function Stop-HermesNativeEvidence {
    param([Parameter(Mandatory = $true)]$Lease)
    if (-not (Test-EvidenceOwnership $Lease)) {
        throw 'Diagnostic registration ownership was lost; unrelated provider state will not be removed.'
    }
    Invoke-EvidenceTool (Join-Path $env:SystemRoot 'System32/wevtutil.exe') @('um', $Lease.manifest)
    if (Test-Path -LiteralPath $script:ProviderKey) {
        throw 'Owned diagnostic registration cleanup did not complete.'
    }
    Remove-Item -LiteralPath $Lease.directory -Recurse -Force
}

function Start-HermesNativeEvidence {
    param([Parameter(Mandatory = $true)][string]$Directory)
    Assert-EvidenceEnvironment
    Assert-EvidenceVacant
    $directoryPath = [IO.Path]::GetFullPath($Directory)
    $temporaryRoot = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\') + '\'
    if (-not $directoryPath.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase) `
        -or (Test-Path -LiteralPath $directoryPath)) {
        throw 'Diagnostic resources require a new owned directory under RUNNER_TEMP.'
    }
    $tools = Get-EvidenceTools
    [IO.Directory]::CreateDirectory($directoryPath) | Out-Null
    $lease = [pscustomobject]@{ directory = $directoryPath
        manifest = (Join-Path $directoryPath 'Native-Evidence.man')
        resource = (Join-Path $directoryPath 'Hermes.Servicing.Evidence.Native.dll') }
    try {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Native-Evidence.man') -Destination $lease.manifest
        Push-Location $directoryPath
        try {
            Invoke-EvidenceTool $tools.mc @('-um', '-h', $directoryPath, '-r', $directoryPath, $lease.manifest)
            $compiledResource = Join-Path $directoryPath 'Native-Evidence.res'
            Invoke-EvidenceTool $tools.rc @('/nologo', '/fo', $compiledResource, (Join-Path $directoryPath 'Native-Evidence.rc'))
            Invoke-EvidenceTool $tools.link @('/NOLOGO', '/DLL', '/NOENTRY', '/MACHINE:X64',
                "/OUT:$($lease.resource)", $compiledResource)
        } finally { Pop-Location }
        Assert-EvidenceVacant
        Invoke-EvidenceTool (Join-Path $env:SystemRoot 'System32/wevtutil.exe') @(
            'im', $lease.manifest, "/rf:$($lease.resource)", "/mf:$($lease.resource)")
        if (-not (Test-EvidenceOwnership $lease)) { throw 'New diagnostic registration does not identify its owned resource.' }
        return $lease
    } catch {
        if (Test-EvidenceOwnership $lease) {
            Stop-HermesNativeEvidence $lease
        } elseif (-not (Test-Path -LiteralPath $script:ProviderKey)) {
            Remove-Item -LiteralPath $directoryPath -Recurse -Force
        }
        throw
    }
}

Export-ModuleMember -Function Start-HermesNativeEvidence, Stop-HermesNativeEvidence
