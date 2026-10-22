#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServicingBuildPath,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Acceptance helper publication requires the dedicated hosted Windows job.'
}
Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$workspace = [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd('\') + '\'
if (-not $output.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase) `
    -or ((Test-Path -LiteralPath $output) -and @(Get-ChildItem -LiteralPath $output -Force).Count -ne 0)) {
    throw 'Helper publication requires fresh workspace output outside AppRoot.'
}
$build = Get-Content -LiteralPath $ServicingBuildPath -Raw | ConvertFrom-Json
Assert-HermesNativeGuard $build.guardPath $build.guardSha256
$setupAssembly = Join-Path ([IO.Path]::GetDirectoryName($build.setupExecutable)) 'Hermes.Setup.dll'
$setupHash = (Get-FileHash -LiteralPath $setupAssembly -Algorithm SHA256).Hash.ToLowerInvariant()
$trust = Get-HermesCompiledGuardTrust $setupAssembly
if ($trust -cne $build.approvedGuardSha256 -or $trust.Split(';') -cnotcontains $build.guardSha256) { throw 'Helpers require the same independently compiled guard trust as distributed Setup.' }
$escaped = $trust.Replace(';', '%3B')
$results = @{}
foreach ($item in @(
    @{ name = 'acceptance'; project = 'Hermes.Servicing.Acceptance'; entry = 'Hermes.Servicing.Acceptance'; generation = '' },
    @{ name = 'probe-old'; project = 'Hermes.Servicing.ProcessProbe'; entry = 'Hermes.Service'; generation = '0.1.0' },
    @{ name = 'probe-new'; project = 'Hermes.Servicing.ProcessProbe'; entry = 'Hermes.Service'; generation = '0.1.1' }
)) {
    $directory = Join-Path $output $item.name
    $inventory = Join-Path $output "$($item.name).publish.txt"
    $arguments = @('publish', (Join-Path $repository "tests/$($item.project)/$($item.project).csproj"),
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=false',
        "-p:HermesApprovedGuardSha256=$escaped", "-p:CustomAfterMicrosoftCommonTargets=$(Join-Path $PSScriptRoot 'Publish-Inventory.targets')",
        "-p:HermesPublishInventoryPath=$inventory", '-o', $directory, '-v', 'q')
    if ($item.generation) { $arguments += "-p:HermesProbeGeneration=$($item.generation)" }
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "Acceptance helper publication failed: $($item.name)" }
    $null = Get-HermesSetupPublishFiles -Directory $directory -InventoryPath $inventory -AcceptanceHelpers
    Assert-HermesSdkFreeRuntime -Directory $directory -EntryName $item.entry
    $referenced = Join-Path $directory 'Hermes.Setup.dll'
    if ((Get-FileHash -LiteralPath $referenced -Algorithm SHA256).Hash.ToLowerInvariant() -cne $setupHash `
        -or (Get-HermesCompiledGuardTrust $referenced) -cne $trust) {
        throw 'A helper rebuilt a different Setup dependency. Do not replace assemblies or accept a version/trust mismatch.'
    }
    $results[$item.name] = $directory
}
$oldMvid = Get-HermesManagedModuleMvid (Join-Path $results['probe-old'] 'Hermes.Service.dll')
$newMvid = Get-HermesManagedModuleMvid (Join-Path $results['probe-new'] 'Hermes.Service.dll')
if ($oldMvid -ceq $newMvid) { throw 'Old/new diagnostic probe generations must have distinct actually compiled MVIDs.' }
[ordered]@{
    schemaVersion = 1; guardSha256 = $build.guardSha256; setupAssemblySha256 = $setupHash
    acceptanceExecutable = (Join-Path $results.acceptance 'Hermes.Servicing.Acceptance.exe')
    probeOld = $results['probe-old']; probeNew = $results['probe-new']; oldProbeMvid = $oldMvid; newProbeMvid = $newMvid
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'acceptance-build.json') -Encoding utf8NoBOM
Write-Output (Join-Path $output 'acceptance-build.json')
