#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:failureReported = $false
$script:capture = $null
$script:phase = 0
function Write-CrashEventFailure {
    param([object]$Failure)
    if ($script:failureReported) { return }
    $script:failureReported = $true
    [Console]::Out.WriteLine('HERMES_CRASH_EVENT_WRAPPER v=1 status=1 published=0 originalGatePassed=0 nativeAcceptance=0')
    $diagnostic = "HERMES_CRASH_EVENT_DIAGNOSTIC v=1 phase=$script:phase family=0 reason=0"
    try { $diagnostic = ConvertTo-HermesCrashEventDiagnostic -Phase $script:phase -Failure $Failure } catch { }
    [Console]::Out.WriteLine($diagnostic)
    if ($script:phase -eq 9 -and $null -ne $script:capture) {
        try { [Console]::Out.WriteLine((ConvertTo-HermesCrashEventFacts $script:capture)) } catch { }
    }
    [Console]::Out.Flush()
}
trap { Write-CrashEventFailure $_; exit 1 }

Import-Module (Join-Path $PSScriptRoot 'Crash-Control.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $PSScriptRoot 'Crash-Event-Control.psm1') -Force -DisableNameChecking
$script:phase = 1
Assert-HermesCrashHost
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if ($root -cne [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)) { throw '1' }
if ([string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) { throw '1' }
Assert-HermesCrashEventAbsent $root
$private = Join-Path $root '.artifacts/crash-control-private'
$directory = Join-Path $private 'helpers/acceptance'
$receiptPath = Join-Path $private 'publication.json'
$metadataPath = Join-Path $root '.artifacts/installer-evidence/startup-event-schema.json'
$schemaReceiptPath = Join-Path $private 'event-schema-publication.json'
$sourcePins = $null
$sdkPins = $null
$bundlePins = $null
$receiptPins = $null
$inventoryPins = $null
$schemaReceiptPins = $null
$metadataPins = $null
$workflowPins = $null
$baseReportPins = $null
$eventReportPins = $null
$scope = $null
$process = $null
$operations = [Collections.Generic.List[Threading.Tasks.Task]]::new()
try {
    $script:phase = 2
    $receiptEntry = Get-HermesCrashFileEntry $receiptPath 'publication.json' 16777216
    $receiptPins = Open-HermesCrashPins $private @($receiptEntry)
    $receipt = Read-HermesCrashReceipt $receiptPath
    $script:phase = 3
    $checkout = Get-HermesCrashCheckout $root $env:GITHUB_SHA
    Assert-HermesCrashReceiptBinding $receipt $checkout
    $sourcePins = Open-HermesCrashPins $root $receipt.sources
    $workflow = Get-HermesCrashEventWorkflowEntry $root
    $workflowPins = Open-HermesCrashPins $root @($workflow)
    $schemaSource = @($receipt.sources | Where-Object { $_.path -ceq 'installer/windows/Invoke-StartupEventSchema.ps1' })
    if ($schemaSource.Count -ne 1) { throw '11' }
    $schemaReceiptEntry = Get-HermesCrashFileEntry $schemaReceiptPath 'event-schema-publication.json' 4096
    $schemaReceiptPins = Open-HermesCrashPins $private @($schemaReceiptEntry)
    $schemaReceipt = Read-HermesCrashEventPublication (Read-HermesCrashBytes $schemaReceiptPath)
    $metadata = Get-HermesCrashFileEntry $metadataPath 'startup-event-schema.json' 4096
    $metadataPins = Open-HermesCrashPins ([IO.Path]::GetDirectoryName($metadataPath)) @($metadata)
    Assert-HermesCrashEventSchema (Read-HermesCrashBytes $metadataPath)
    Assert-HermesCrashEventPublicationBinding $schemaReceipt $checkout $workflow $schemaSource[0] $metadata `
        $env:GITHUB_RUN_ID $env:GITHUB_RUN_ATTEMPT
    Assert-HermesCrashEventFreshness $schemaReceipt ([IO.File]::GetCreationTimeUtc($metadataPath).Ticks) `
        ([IO.File]::GetLastWriteTimeUtc($metadataPath).Ticks) ([DateTime]::UtcNow.Ticks)
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
    foreach ($pins in @($sourcePins, $sdkPins, $bundlePins, $receiptPins, $inventoryPins,
            $schemaReceiptPins, $metadataPins, $workflowPins)) { Assert-HermesCrashPins $pins }
    Assert-HermesCrashEventAbsent $root
    Assert-HermesCrashEventFreshness $schemaReceipt ([IO.File]::GetCreationTimeUtc($metadataPath).Ticks) `
        ([IO.File]::GetLastWriteTimeUtc($metadataPath).Ticks) ([DateTime]::UtcNow.Ticks)

    # The worker's fixed two-minute execution-step timeout is armed before entry.
    # Begin and Start are synchronous; the capture stopwatch cannot interrupt them.
    $script:phase = 6
    $scope = $begin.Invoke($null, [object[]]::new(0))
    if ($null -eq $scope) { throw '1' }
    $script:phase = 7
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $directory 'Hermes.Servicing.Acceptance.exe'
    $start.ArgumentList.Add('startup-crash-event-probe')
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
    $script:capture = Invoke-HermesCrashEventCapture $process -Operations $operations
    Assert-HermesCrashEventSettled $operations
    $script:phase = 11
    $process.Dispose()
    $process = $null
    $script:phase = 9
    $basePath = Join-Path $root '.artifacts/installer-evidence/startup-crash-control.json'
    $eventPath = Join-Path $root '.artifacts/installer-evidence/event-correlation.json'
    $baseEntry = Get-HermesCrashFileEntry $basePath 'startup-crash-control.json' 4096
    $baseReportPins = Open-HermesCrashPins ([IO.Path]::GetDirectoryName($basePath)) @($baseEntry)
    $eventEntry = Get-HermesCrashFileEntry $eventPath 'event-correlation.json' 4096
    $eventReportPins = Open-HermesCrashPins ([IO.Path]::GetDirectoryName($eventPath)) @($eventEntry)
    $image = @($receipt.bundle | Where-Object { $_.path -ceq 'Hermes.Servicing.Acceptance.exe' })
    $source = @($receipt.bundle | Where-Object { $_.path -ceq 'Hermes.Servicing.Acceptance.dll' })
    if ($image.Count -ne 1 -or $source.Count -ne 1) { throw '1' }
    $control = Assert-HermesCrashReport (Read-HermesCrashBytes $basePath) $image[0].sha256 $source[0].sha256
    $null = Assert-HermesCrashEventReport (Read-HermesCrashBytes $eventPath) $metadata.sha256 $schemaSource[0].sha256
    Assert-HermesCrashEventOutcome $script:capture $control
    $script:phase = 10
    Assert-HermesCrashAssemblies $directory $receipt.bundle
    Assert-HermesCrashTree $directory $receipt.bundle
    $fresh = Get-HermesCrashCheckout $root $env:GITHUB_SHA
    Assert-HermesCrashReceiptBinding $receipt $fresh
    Assert-HermesCrashEventPublicationBinding $schemaReceipt $fresh $workflow $schemaSource[0] $metadata `
        $env:GITHUB_RUN_ID $env:GITHUB_RUN_ATTEMPT
    Assert-HermesCrashEventSchema (Read-HermesCrashBytes $metadataPath)
    Assert-HermesCrashEventFreshness $schemaReceipt ([IO.File]::GetCreationTimeUtc($metadataPath).Ticks) `
        ([IO.File]::GetLastWriteTimeUtc($metadataPath).Ticks) ([DateTime]::UtcNow.Ticks)
    $pinSets = @($sourcePins, $sdkPins, $bundlePins, $receiptPins, $inventoryPins,
        $schemaReceiptPins, $metadataPins, $workflowPins, $baseReportPins, $eventReportPins)
    foreach ($pins in $pinSets) { Assert-HermesCrashPins $pins }
    Clear-HermesCrashEventCapture $script:capture
    $script:phase = 11
    foreach ($pins in $pinSets) { Close-HermesCrashPins $pins }
    # A failed append still abandons the job. A subsequent release failure keeps
    # the step non-success, so the workflow cannot publish this output alone.
    $script:phase = 12
    [IO.File]::AppendAllText($env:GITHUB_OUTPUT, "published=1`n", [Text.UTF8Encoding]::new($false))
    $script:phase = 11
    Complete-HermesCrashEventScope $scope $pinSets $operations
    $scope = $null
} catch {
    Write-CrashEventFailure $_
    throw
} finally {
    Clear-HermesCrashEventCapture $script:capture
    # A failed/unsettled scope is abandoned, never normally completed. Its
    # KillOnClose fence includes this PowerShell and all descendants.
    if ($null -ne $scope) { $scope.Dispose() }
    if ($null -ne $process) { $process.Dispose() }
    foreach ($pins in @($sourcePins, $sdkPins, $bundlePins, $receiptPins, $inventoryPins,
            $schemaReceiptPins, $metadataPins, $workflowPins, $baseReportPins, $eventReportPins)) {
        Close-HermesCrashPins $pins
    }
}
$script:phase = 12
[Console]::Out.WriteLine('HERMES_CRASH_EVENT_WRAPPER v=1 status=0 published=1 originalGatePassed=0 nativeAcceptance=0')
exit 0
