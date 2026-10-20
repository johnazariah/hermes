#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$IdentityPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
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
        $view.Execute()
        while ($null -ne ($record = $view.Fetch())) {
            try {
                $values = for ($column = 1; $column -le $Columns; $column++) {
                    $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $record, @($column))
                }
                ,@($values)
            } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null }
        }
    } finally { $view.Close(); [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null }
}

try {
    $database = $installer.OpenDatabase((Resolve-Path -LiteralPath $PackagePath).Path, 1)
    foreach ($property in @(
        @('ProductCode', ([Guid]$identity.productCode).ToString('B')),
        @('UpgradeCode', ([Guid]$manifest.upgradeCode).ToString('B')),
        @('ProductVersion', $identity.version)
    )) {
        $rows = @(Get-RowValues "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$($property[0])'" 1)
        if ($rows.Count -ne 1 -or $rows[0][0] -ne $property[1]) { throw 'Bound MSI product identity differs from the manifest.' }
    }
    foreach ($property in @('ALLUSERS', 'INSTALLFOLDER')) {
        $rows = @(Get-RowValues "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$property'" 1)
        if ($rows.Count -gt 0 -and $rows[0][0] -ne '') { throw 'MSI contains a machine scope or root override.' }
    }
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
    $summary = $database.GetType().InvokeMember('SummaryInformation', [Reflection.BindingFlags]::GetProperty, $null, $database, @(1))
    $template = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(7))
    $wordCount = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(15))
    if ($template.Split(';')[0] -cne 'x64' -or [int]$wordCount -ne 10) { throw 'MSI summary platform or compressed per-user scope differs from the manifest contract.' }
    $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::SetProperty, $null, $summary, @(9, ([Guid]$identity.packageCode).ToString('B').ToUpperInvariant())) | Out-Null
    $summary.Persist()
    $database.Commit()
    $actual = $summary.GetType().InvokeMember('Property', [Reflection.BindingFlags]::GetProperty, $null, $summary, @(9))
    if ([Guid]$actual -ne [Guid]$identity.packageCode) { throw 'MSI summary PackageCode was not persisted.' }
} finally {
    foreach ($item in @($summary, $database, $installer)) {
        if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
    }
}
