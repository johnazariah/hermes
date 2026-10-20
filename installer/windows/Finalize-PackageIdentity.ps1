#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$IdentityPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ErrorView = 'NormalView'
if ($env:OS -ne 'Windows_NT') { throw 'MSI identity finalization requires Windows; it never installs the package.' }
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$identity = Get-Content -LiteralPath $IdentityPath -Raw | ConvertFrom-Json
if ([Guid]$manifest.productCode -ne [Guid]$identity.productCode `
    -or [Guid]$manifest.packageCode -ne [Guid]$identity.packageCode -or $manifest.version -cne $identity.version) {
    throw 'Build identity and embedded-manifest source disagree.'
}
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
$summary = $null

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

$phase = 'product-properties'
try {
    $database = $installer.OpenDatabase((Resolve-Path -LiteralPath $PackagePath).Path, 1)
    foreach ($property in @(
        @('ProductCode', ([Guid]$identity.productCode).ToString('B')),
        @('UpgradeCode', ([Guid]$manifest.upgradeCode).ToString('B')),
        @('ProductVersion', $identity.version)
    )) {
        $rows = @(Get-RowValues "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$($property[0])'" 1)
        if ($rows.Count -ne 1 -or $rows[0][0] -ne $property[1]) {
            $actual = if ($rows.Count -eq 1) { [string]$rows[0][0] } else { '<not unique>' }
            $kind = if ($rows.Count -eq 1 -and $null -ne $rows[0][0]) { $rows[0][0].GetType().FullName } else { '<none>' }
            throw "Bound MSI identity differs for $($property[0]): rows=$($rows.Count); expected=$($property[1]); actual=$actual; type=$kind."
        }
    }
    $phase = 'scope-properties'
    foreach ($property in @('ALLUSERS', 'INSTALLFOLDER')) {
        $rows = @(Get-RowValues "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$property'" 1)
        if ($rows.Count -gt 0 -and $rows[0][0] -ne '') { throw 'MSI contains a machine scope or root override.' }
    }
    $phase = 'file-component-inventory'
    $files = @(Get-RowValues 'SELECT `File`, `Component_`, `FileSize` FROM `File`' 3)
    $components = @(Get-RowValues 'SELECT `Component`, `ComponentId` FROM `Component`' 2)
    if ($files.Count -ne @($manifest.files).Count) { throw 'MSI File table and manifest inventory have different sizes.' }
    foreach ($file in $manifest.files) {
        $rows = @($files | Where-Object { $_[0] -ceq $file.fileId })
        $owners = @($components | Where-Object { $_[0] -ceq $file.componentId })
        if ($rows.Count -ne 1 -or $rows[0][1] -cne $file.componentId -or [long]$rows[0][2] -ne $file.length `
            -or $owners.Count -ne 1 -or [Guid]$owners[0][1] -ne [Guid]$file.componentCode) {
            throw 'Bound MSI file/component identity differs from the embedded manifest.'
        }
    }
    $phase = 'summary-contract'
    $summary = $database.GetType().InvokeMember('SummaryInformation', [Reflection.BindingFlags]::GetProperty, $null, $database, @(1))
    $template = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(7))
    $wordCount = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(15))
    if ($template.Split(';')[0] -cne 'x64' -or [int]$wordCount -ne 10) { throw 'MSI summary platform or compressed per-user scope differs from the manifest contract.' }
    $phase = 'summary-package-write'
    $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $summary, @(9, ([Guid]$identity.packageCode).ToString('B').ToUpperInvariant())) | Out-Null
    $pending = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(9))
    if ($null -eq $pending -or [Guid]$pending -ne [Guid]$identity.packageCode) { throw 'MSI summary PackageCode setter did not retain the selected identity.' }
    [void]$summary.Persist()
    [void]$database.Commit()
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary) | Out-Null
    $summary = $null
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null
    $database = $null
    $phase = 'summary-package-readback'
    $database = $installer.OpenDatabase((Resolve-Path -LiteralPath $PackagePath).Path, 0)
    $summary = $database.GetType().InvokeMember('SummaryInformation', [Reflection.BindingFlags]::GetProperty, $null, $database, @(0))
    $actual = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(9))
    if ($null -eq $actual -or [Guid]$actual -ne [Guid]$identity.packageCode) { throw 'MSI summary PackageCode was not persisted.' }
} catch {
    [Console]::Error.WriteLine("MSI finalization failed during $phase; source line $($_.InvocationInfo.ScriptLineNumber).")
    @($_.ScriptStackTrace -split '\r?\n' | Select-Object -First 4) | ForEach-Object {
        [Console]::Error.WriteLine($_.Substring(0, [Math]::Min(512, $_.Length)))
    }
    throw
} finally {
    foreach ($item in @($summary, $database, $installer)) {
        if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
    }
}
