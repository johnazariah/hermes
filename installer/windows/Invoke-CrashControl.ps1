#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:failureReported = $false
$script:capture = $null
# 0 bootstrap, 1 guard, 2 receipt, 3 checkout, 4 inventory, 5 load,
# 6 begin, 7 start, 8 capture, 9 report, 10 recheck, 11 release, 12 output.
$script:phase = 0
function Write-CrashFailure {
    param([object]$Failure)
    if ($script:failureReported) { return }
    $script:failureReported = $true
    [Console]::Out.WriteLine('HERMES_CRASH_WRAPPER v=1 status=1 published=0 nativeAcceptance=0')
    $diagnostic = "HERMES_CRASH_DIAGNOSTIC v=1 phase=$script:phase family=0 reason=0 nativeError=0"
    try { $diagnostic = ConvertTo-HermesCrashDiagnostic -Phase $script:phase -Failure $Failure } catch { }
    [Console]::Out.WriteLine($diagnostic)
    if ($script:phase -eq 9 -and $null -ne $script:capture) {
        try {
            [Console]::Out.WriteLine((ConvertTo-HermesCrashControlFacts -Capture $script:capture))
        } catch { }
    }
    [Console]::Out.Flush()
}
trap { Write-CrashFailure $_; exit 1 }

Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -Force -DisableNameChecking
$script:phase = 1
Assert-HermesCrashHost
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($root -cne [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)) { throw '1' }
Assert-HermesCrashReportAbsent $root
$script:phase = 2
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
    $script:phase = 3
    $checkout = Get-HermesCrashCheckout $root $env:GITHUB_SHA
    Assert-HermesCrashReceiptBinding $receipt $checkout
    $sourcePins = Open-HermesCrashPins $root $receipt.sources
    $script:phase = 4
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
    $script:phase = 5
    $begin = Import-HermesCrashSupervisor $directory $receipt.bundle
    $script:phase = 10
    Assert-HermesCrashPins $sourcePins
    Assert-HermesCrashPins $sdkPins
    Assert-HermesCrashPins $bundlePins
    Assert-HermesCrashPins $receiptPins
    Assert-HermesCrashPins $inventoryPins
    Assert-HermesCrashReportAbsent $root

    # The workflow's independent step deadline is already armed. These synchronous
    # calls are NOT bounded by the cooperative capture stopwatch below.
    $script:phase = 6
    $scope = $begin.Invoke($null, [object[]]::new(0))
    if ($null -eq $scope) { throw '1' }
    $script:phase = 7
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
    $script:phase = 8
    $script:capture = Invoke-HermesCrashCapture $process
    $script:phase = 11
    $process.Dispose()
    $process = $null
    $script:phase = 9
    $reportPath = Join-Path $root '.artifacts/installer-evidence/startup-crash-control.json'
    $entry = Get-HermesCrashFileEntry $reportPath 'startup-crash-control.json' 4096
    if ($entry.length -gt 4096) { throw '1' }
    $reportPins = Open-HermesCrashPins ([IO.Path]::GetDirectoryName($reportPath)) @($entry)
    $image = @($receipt.bundle | Where-Object { $_.path -ceq 'Hermes.Servicing.Acceptance.exe' })
    $source = @($receipt.bundle | Where-Object { $_.path -ceq 'Hermes.Servicing.Acceptance.dll' })
    if ($image.Count -ne 1 -or $source.Count -ne 1) { throw '1' }
    $report = Assert-HermesCrashReport (Read-HermesCrashBytes $reportPath) $image[0].sha256 $source[0].sha256
    Assert-HermesCrashOutcome $capture $report
    $script:phase = 10
    Assert-HermesCrashAssemblies $directory $receipt.bundle
    Assert-HermesCrashTree $directory $receipt.bundle
    foreach ($pins in @($sourcePins, $sdkPins, $bundlePins, $receiptPins, $reportPins, $inventoryPins)) {
        Assert-HermesCrashPins $pins
    }
    [Array]::Clear($capture.Stdout)
    # No more launches, outstanding I/O, process handles or publication pins.
    $script:phase = 11
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
    Write-CrashFailure $_
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
$script:phase = 12
if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { throw '1' }
[IO.File]::AppendAllText($env:GITHUB_OUTPUT, "published=1`n", [Text.UTF8Encoding]::new($false))
[Console]::Out.WriteLine('HERMES_CRASH_WRAPPER v=1 status=0 published=1 nativeAcceptance=0')
exit 0
