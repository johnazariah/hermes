#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$failureReported = $false
trap {
    if (-not $failureReported) {
        [Console]::Out.WriteLine('HERMES_CRASH_WRAPPER v=1 status=1 published=0 nativeAcceptance=0')
    }
    exit 1
}

Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -Force -DisableNameChecking
Assert-HermesCrashHost
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($root -cne [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)) { throw '1' }
Assert-HermesCrashReportAbsent $root
$private = Join-Path $root '.artifacts/crash-control-private'
$directory = Join-Path $private 'helpers/acceptance'
$receiptPath = Join-Path $private 'publication.json'
$receiptEntry = Get-HermesCrashFileEntry $receiptPath 'publication.json' 16777216
$receiptPins = Open-HermesCrashPins $private @($receiptEntry)
$sourcePins = $null
$sdkPins = $null
$bundlePins = $null
$inventoryPins = $null
$reportPins = $null
$scope = $null
$process = $null
try {
    $receipt = Read-HermesCrashReceipt $receiptPath
    $checkout = Get-HermesCrashCheckout $root $env:GITHUB_SHA
    Assert-HermesCrashReceiptBinding $receipt $checkout
    $sourcePins = Open-HermesCrashPins $root $receipt.sources
    Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force -DisableNameChecking
    $inventoryPath = Join-Path $private 'helpers/acceptance.publish.txt'
    $inventoryEntry = Get-HermesCrashFileEntry $inventoryPath 'helpers/acceptance.publish.txt' 16777216
    $inventoryPins = Open-HermesCrashPins $private @($inventoryEntry)
    $null = Get-HermesSetupPublishFiles -Directory $directory -InventoryPath $inventoryPath -AcceptanceHelpers
    $inventory = Read-HermesCrashInventory $inventoryPath $directory @(
        $root,
        $(if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else {
            Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages'
        }),
        $(if ($env:DOTNET_ROOT) { Join-Path $env:DOTNET_ROOT 'packs' } else { Join-Path $private 'absent-packs' })
    )
    if ((Get-HermesCrashEntryDigest $inventory.Bundle) -cne $receipt.bundleSha256 `
        -or (Get-HermesCrashEntryDigest $inventory.Sources) -cne (Get-HermesCrashEntryDigest $receipt.sdkSources)) { throw '1' }
    $sdkPins = Open-HermesCrashPins $root $receipt.sdkSources -Absolute
    $bundlePins = Open-HermesCrashPins $directory $receipt.bundle
    Assert-HermesSdkFreeRuntime -Directory $directory -EntryName 'Hermes.Servicing.Acceptance'
    Assert-HermesCrashTree $directory $receipt.bundle
    $begin = Import-HermesCrashSupervisor $directory $receipt.bundle
    Assert-HermesCrashPins $sourcePins
    Assert-HermesCrashPins $sdkPins
    Assert-HermesCrashPins $bundlePins
    Assert-HermesCrashPins $receiptPins
    Assert-HermesCrashPins $inventoryPins
    Assert-HermesCrashReportAbsent $root

    # The workflow's independent step deadline is already armed. These synchronous
    # calls are NOT bounded by the cooperative capture stopwatch below.
    $scope = $begin.Invoke($null, [object[]]::new(0))
    if ($null -eq $scope) { throw '1' }
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $directory 'Hermes.Servicing.Acceptance.exe'
    $start.ArgumentList.Add('startup-crash-probe')
    $start.WorkingDirectory = $directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    if (-not $process.Start()) { throw '1' }
    $process.StandardInput.Close()
    $capture = Invoke-HermesCrashCapture $process
    $process.Dispose()
    $process = $null
    $reportPath = Join-Path $root '.artifacts/installer-evidence/startup-crash-control.json'
    $entry = Get-HermesCrashFileEntry $reportPath 'startup-crash-control.json' 4096
    if ($entry.length -gt 4096) { throw '1' }
    $reportPins = Open-HermesCrashPins ([IO.Path]::GetDirectoryName($reportPath)) @($entry)
    $image = @($receipt.bundle | Where-Object { $_.path -ceq 'Hermes.Servicing.Acceptance.exe' })
    $source = @($receipt.bundle | Where-Object { $_.path -ceq 'Hermes.Servicing.Acceptance.dll' })
    if ($image.Count -ne 1 -or $source.Count -ne 1) { throw '1' }
    $report = Assert-HermesCrashReport (Read-HermesCrashBytes $reportPath) $image[0].sha256 $source[0].sha256
    Assert-HermesCrashOutcome $capture $report
    Assert-HermesCrashAssemblies $directory $receipt.bundle
    Assert-HermesCrashTree $directory $receipt.bundle
    foreach ($pins in @($sourcePins, $sdkPins, $bundlePins, $receiptPins, $reportPins, $inventoryPins)) {
        Assert-HermesCrashPins $pins
    }
    [Array]::Clear($capture.Stdout)
    # No more launches, outstanding I/O, process handles or publication pins.
    Close-HermesCrashPins $reportPins
    Close-HermesCrashPins $bundlePins
    Close-HermesCrashPins $sdkPins
    Close-HermesCrashPins $sourcePins
    Close-HermesCrashPins $receiptPins
    Close-HermesCrashPins $inventoryPins
    $scope.CompleteAfterChildrenExited()
    $scope.Dispose()
    $scope = $null
} catch {
    $failureReported = $true
    [Console]::Out.WriteLine('HERMES_CRASH_WRAPPER v=1 status=1 published=0 nativeAcceptance=0')
    throw
} finally {
    # Failure/abandonment terminates THIS private job, including this PowerShell.
    # Never call CompleteAfterChildrenExited from failure cleanup.
    if ($null -ne $scope) { $scope.Dispose() }
    if ($null -ne $process) { $process.Dispose() }
    Close-HermesCrashPins $reportPins
    Close-HermesCrashPins $bundlePins
    Close-HermesCrashPins $sdkPins
    Close-HermesCrashPins $sourcePins
    Close-HermesCrashPins $receiptPins
    Close-HermesCrashPins $inventoryPins
}
if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { throw '1' }
[IO.File]::AppendAllText($env:GITHUB_OUTPUT, "published=1`n", [Text.UTF8Encoding]::new($false))
[Console]::Out.WriteLine('HERMES_CRASH_WRAPPER v=1 status=0 published=1 nativeAcceptance=0')
exit 0
