#Requires -Version 5.1
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-HermesPackageVersion {
    param([Parameter(Mandatory = $true)][string]$Version)
    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw 'MSI version must have three canonical numeric fields without leading zeroes.'
    }
    $parts = $Version.Split('.')
    if ([decimal]$parts[0] -gt 255 -or [decimal]$parts[1] -gt 255 -or [decimal]$parts[2] -gt 65535) {
        throw 'MSI version fields must be within 255.255.65535.'
    }
}

function Get-HermesRelatedProducts {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $products = $null
    try {
        # Includes advertised products: the application registry key is not installation authority.
        $products = $installer.GetType().InvokeMember('RelatedProducts', [Reflection.BindingFlags]::GetProperty,
            $null, $installer, @('{7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A}'))
        foreach ($product in $products) { [string]$product }
    }
    finally {
        foreach ($item in @($products, $installer)) {
            if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
                [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null
            }
        }
    }
}

function Get-HermesCachedPackage {
    param([Parameter(Mandatory = $true)][string]$ProductCode)
    if ($ProductCode -notmatch '^\{[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$') {
        throw 'Invalid registered ProductCode.'
    }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    try {
        $installer.GetType().InvokeMember('ProductInfo', [Reflection.BindingFlags]::GetProperty,
            $null, $installer, @($ProductCode, 'LocalPackage'))
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null }
}

function Get-HermesMsiIdentity {
    param([Parameter(Mandatory = $true)][string]$PackagePath)
    $path = (Resolve-Path -LiteralPath $PackagePath).Path
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    $properties = @{}
    try {
        $database = $installer.OpenDatabase($path, 0)
        foreach ($name in @('ProductCode', 'UpgradeCode', 'ProductName', 'ProductVersion')) {
            $view = $null
            $record = $null
            try {
                $view = $database.OpenView("SELECT ``Value`` FROM ``Property`` WHERE ``Property``='$name'")
                [void]$view.Execute()
                $record = $view.Fetch()
                if ($null -eq $record) { throw "Missing MSI identity property: $name" }
                $properties[$name] = $record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty,
                    $null, $record, @(1))
            }
            finally {
                foreach ($item in @($record, $view)) {
                    if ($null -ne $item -and [Runtime.InteropServices.Marshal]::IsComObject($item)) {
                        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null
                    }
                }
            }
        }
    }
    finally {
        foreach ($item in @($database, $installer)) {
            if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
        }
    }
    if ($properties.UpgradeCode -ne '{7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A}' `
        -or $properties.ProductName -ne 'Hermes' `
        -or $properties.ProductCode -notmatch '^\{[0-9A-Fa-f]{8}(-[0-9A-Fa-f]{4}){3}-[0-9A-Fa-f]{12}\}$') {
        throw 'The selected package is not the expected Hermes Windows installer.'
    }
    Assert-HermesPackageVersion $properties.ProductVersion
    [pscustomobject]$properties
}

function Assert-HermesRuntimeStopped {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)
    $ownedPaths = @(
        [IO.Path]::GetFullPath((Join-Path $InstallDirectory 'service/Hermes.Service.exe')),
        [IO.Path]::GetFullPath((Join-Path $InstallDirectory 'tray/Hermes.Tray.exe'))
    )
    $processes = Get-CimInstance -ClassName Win32_Process `
        -Filter "Name='Hermes.Service.exe' OR Name='Hermes.Tray.exe'" -ErrorAction Stop
    foreach ($process in $processes) {
        if ($null -eq $process.ExecutablePath) {
            throw 'A Hermes process path could not be verified. Close Hermes before changing its installation.'
        }
        $path = [IO.Path]::GetFullPath($process.ExecutablePath)
        if ($ownedPaths -contains $path) {
            throw "Quit the installed Hermes instance before continuing (PID $($process.ProcessId)). No process was stopped."
        }
    }
}

Export-ModuleMember -Function Get-HermesMsiIdentity, Get-HermesRelatedProducts, Get-HermesCachedPackage, Assert-HermesRuntimeStopped, Assert-HermesPackageVersion
