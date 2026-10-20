#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$WixPath,
    [Parameter(Mandatory = $true)][string]$WixpdbPath,
    [Parameter(Mandatory = $true)][string]$IntermediateDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Complete native MSI validation requires Windows; it never installs the package.' }
Import-Module (Join-Path $PSScriptRoot 'Package-Validation.psm1') -Force
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$manifestPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$before = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$manifestBefore = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
$receiptPath = Join-Path ([IO.Path]::GetDirectoryName($manifestPath)) 'ice-validation.json'
if (Test-Path -LiteralPath $receiptPath) { Remove-Item -LiteralPath $receiptPath }

function Get-RowValues([string]$Query, [int]$Columns) {
    $view = $database.OpenView($Query)
    try {
        [void]$view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            try {
                $values = for ($column = 1; $column -le $Columns; $column++) {
                    $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($column))
                }
                ,@($values)
            } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null }
        }
    } finally { [void]$view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
}

$validator = Open-HermesPinnedWixValidator $WixPath
$process = $null
try {
    $installer = $null
    $database = $null
    $summary = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($package, 0)
        $summary = $database.GetType().InvokeMember('SummaryInformation', [Reflection.BindingFlags]::GetProperty, $null, $database, @(0))
        $facts = @{
            wordCount = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(15))
            packageCode = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(9))
            properties = @(Get-RowValues 'SELECT `Property`, `Value` FROM `Property`' 2)
            upgrades = @(Get-RowValues 'SELECT `VersionMin`, `VersionMax`, `Attributes`, `Remove`, `ActionProperty` FROM `Upgrade` WHERE `VersionMax` IS NULL AND `Remove` IS NULL' 5)
            actions = @(Get-RowValues 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`' 4)
            sequence = @(Get-RowValues 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallExecuteSequence`' 3)
            directories = @(Get-RowValues 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' 3)
            files = @(Get-RowValues 'SELECT `File`, `Component_`, `FileName`, `FileSize` FROM `File`' 4)
            components = @(Get-RowValues 'SELECT `Component`, `ComponentId`, `Directory_` FROM `Component`' 3)
        }
        Assert-HermesIcePrerequisites $manifest $facts
    } finally {
        foreach ($item in @($summary, $database, $installer)) {
            if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
        }
    }
    foreach ($name in @('DOTNET_STARTUP_HOOKS', 'DOTNET_ADDITIONAL_DEPS', 'DOTNET_SHARED_STORE', 'CORECLR_ENABLE_PROFILING', 'COR_ENABLE_PROFILING')) {
        if (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name))) {
            throw "Native validation forbids runtime code-injection settings: $name"
        }
    }
    $start = [Diagnostics.ProcessStartInfo]::new((Get-Command dotnet -CommandType Application -ErrorAction Stop).Source)
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($validator.WixPath)
    foreach ($argument in @($validator.WixPath, 'msi', 'validate', $package, '-nologo',
        '-pdb', [IO.Path]::GetFullPath($WixpdbPath), '-intermediatefolder', [IO.Path]::GetFullPath($IntermediateDirectory))) {
        [void]$start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $text = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
    $log = Join-Path ([IO.Path]::GetDirectoryName($manifestPath)) 'ice-validation.log'
    [IO.File]::WriteAllText($log, $text, [Text.UTF8Encoding]::new($false))
    [Console]::Out.Write($text)
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -cne $before) {
        throw 'Validation changed the finalised MSI; post-validation package mutation is forbidden.'
    }
    if ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash -cne $manifestBefore) {
        throw 'Validation changed the manifest; warning allowances must describe the same inventory.'
    }
    Assert-HermesIceDiagnostics -Manifest $manifest -Lines @($text -split '\r?\n') -NativeExitCode $process.ExitCode
    $receipt = [ordered]@{
        schemaVersion = 1
        packageSha256 = $before.ToLowerInvariant()
        manifestSha256 = $manifestBefore.ToLowerInvariant()
        productCode = $manifest.productCode
        packageCode = $manifest.packageCode
        version = $manifest.version
        files = @($manifest.files).Count
        validatorPackageSha256 = $validator.PackageSha256
        validatorPath = $validator.WixPath
        diagnosticsSha256 = (Get-FileHash -LiteralPath $log -Algorithm SHA256).Hash.ToLowerInvariant()
        nativeExitCode = $process.ExitCode
    }
    [IO.File]::WriteAllText($receiptPath, ($receipt | ConvertTo-Json) + "`n", [Text.UTF8Encoding]::new($false))
} finally {
    if ($null -ne $process) { $process.Dispose() }
    foreach ($lease in $validator.Leases) { $lease.Dispose() }
}
