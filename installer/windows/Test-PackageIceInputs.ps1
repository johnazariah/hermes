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
if ($env:OS -ne 'Windows_NT') { throw 'Actual MSI input controls require disposable hosted Windows; they never install a package.' }
$package = (Resolve-Path -LiteralPath $PackagePath).Path
$before = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash
$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$file = @($manifest.files | Where-Object { $_.path -cnotin @('service/Hermes.Service.dll', 'tray/Hermes.Tray.dll') })[0]
if ($file.fileId -cnotmatch '^[A-Za-z0-9_]+$' -or $file.componentId -cnotmatch '^[A-Za-z0-9_]+$') {
    throw 'Actual MSI input controls require canonical payload identifiers.'
}
$scratch = Join-Path ([IO.Path]::GetFullPath($IntermediateDirectory)) ('ice-inputs-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($scratch)
try {
    $candidate = Join-Path $scratch 'changed.msi'
    $inventory = Join-Path $scratch 'payload-inventory.json'
    Copy-Item -LiteralPath $ManifestPath -Destination $inventory
    $controls = @(
        @{
            query = "UPDATE ``File`` SET ``FileName``='hermes-policy-renamed.invalid' WHERE ``File``='$($file.fileId)'"
            refusal = 'ICE91 policy requires actual MSI file names, component bindings and lengths to match the complete manifest.'
        },
        @{
            query = "UPDATE ``Component`` SET ``Directory_``='INSTALLFOLDER' WHERE ``Component``='$($file.componentId)'"
            refusal = 'ICE91 policy requires actual MSI component GUIDs and target directories to match the manifest.'
        }
    )
    foreach ($control in $controls) {
        Copy-Item -LiteralPath $package -Destination $candidate -Force
        $installer = $null
        $database = $null
        $view = $null
        try {
            $installer = New-Object -ComObject WindowsInstaller.Installer
            $database = $installer.OpenDatabase($candidate, 1)
            $view = $database.OpenView($control.query)
            [void]$view.Execute()
            [void]$view.Close()
            [void]$database.Commit()
        } finally {
            foreach ($item in @($view, $database, $installer)) {
                if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
            }
        }
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -ceq $before) {
            throw 'Actual MSI input control did not mutate its isolated copy.'
        }
        $refused = $false
        try {
            & (Join-Path $PSScriptRoot 'Validate-PackageIce.ps1') -PackagePath $candidate -ManifestPath $inventory `
                -WixPath $WixPath -WixpdbPath $WixpdbPath -IntermediateDirectory $scratch | Out-Null
        } catch {
            if ($_.Exception.Message -cne $control.refusal) { throw }
            $refused = $true
        }
        if (-not $refused) { throw 'A changed actual MSI target path passed the real native validation entrypoint.' }
        if (Test-Path -LiteralPath (Join-Path $scratch 'ice-validation.log')) {
            throw 'A path-binding refusal must occur before native validation executes.'
        }
    }
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -cne $before) {
        throw 'Actual MSI input controls changed the original finalised package.'
    }
} finally { Remove-Item -LiteralPath $scratch -Recurse -Force }
Write-Output 'Actual isolated MSI filename and component-reparenting inputs were rejected by the real read-only gate before native execution; no package was installed.'
