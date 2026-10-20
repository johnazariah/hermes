#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$document = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Package.wxs') -Raw)
$namespace = [Xml.XmlNamespaceManager]::new($document.NameTable)
$namespace.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
$package = $document.SelectSingleNode('/w:Wix/w:Package', $namespace)

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

Require ($package.Scope -ceq 'perUser' -and $package.Compressed -ceq 'yes' -and $package.ShortNames -ceq 'no') 'Package must remain compressed, long-name, per-user MSI (summary WordCount 10).'
Require ($package.ProductCode -ceq '$(var.HermesProductCode)' -and $package.UpgradeStrategy -ceq 'none') 'Explicit product identity and manual standard Upgrade-table authoring are required.'
Require ($null -eq $package.SelectSingleNode('w:MajorUpgrade', $namespace)) 'MajorUpgrade convenience scheduling cannot precede the transaction fence.'
$upgrade = $package.SelectSingleNode('w:Upgrade/w:UpgradeVersion[@Property="HERMES_RELATED_PRODUCTS"]', $namespace)
Require ($upgrade.Minimum -ceq '0.0.0' -and $upgrade.IncludeMinimum -ceq 'yes' -and $upgrade.OnlyDetect -ceq 'no' -and -not $upgrade.HasAttribute('Maximum')) 'Related-product removal must include all versions.'
$newer = $package.SelectSingleNode('w:Upgrade/w:UpgradeVersion[@Property="HERMES_NEWER_PRODUCT"]', $namespace)
Require ($newer.Minimum -ceq '$(ProductVersion)' -and $newer.IncludeMinimum -ceq 'no' -and $newer.OnlyDetect -ceq 'yes') 'Newer-product detection must remain separate from explicit rollback permission.'
foreach ($property in @('HERMES_SERVICING_RUN', 'HERMES_SERVICING_ABI', 'HERMES_ALLOW_ROLLBACK')) {
    $node = $package.SelectSingleNode("w:Property[@Id='$property']", $namespace)
    Require ($null -ne $node -and $node.Secure -ceq 'yes' -and -not $node.HasAttribute('Value')) 'Coordinator properties are secure correlation only, with no authored bypass value.'
}
foreach ($pair in @(@('REBOOT', 'ReallySuppress'), @('MSIRESTARTMANAGERCONTROL', 'Disable'))) {
    Require ($package.SelectSingleNode("w:Property[@Id='$($pair[0])']", $namespace).Value -ceq $pair[1]) 'Restart and application-shutdown suppression must remain separately authored.'
}
foreach ($pair in @(@('HermesPayloadManifest', '$(var.HermesManifestPath)'), @('HermesServicingGuard', '$(var.HermesGuardPath)'))) {
    Require ($package.SelectSingleNode("w:Binary[@Id='$($pair[0])']", $namespace).SourceFile -ceq $pair[1]) 'Manifest and actual native guard must be embedded Binary rows, not installed payload files.'
}
foreach ($action in @('HermesPreflight', 'HermesTransactionFence')) {
    $node = $package.SelectSingleNode("w:CustomAction[@Id='$action']", $namespace)
    Require ($node.BinaryRef -ceq 'HermesServicingGuard' -and $node.DllEntry -ceq $action `
        -and $node.Execute -ceq 'immediate' -and $node.Impersonate -ceq 'yes' -and $node.Return -ceq 'check') 'Both real native exports must be immediate user-context, synchronous checked actions.'
}
$execute = $package.SelectSingleNode('w:InstallExecuteSequence', $namespace)
Require ($execute.SelectSingleNode('w:FindRelatedProducts', $namespace).Sequence -ceq '25') 'Standard related-product discovery must precede guarded servicing.'
foreach ($pair in @(@('HermesPreflight', '1399'), @('HermesTransactionFence', '1501'))) {
    $node = $execute.SelectSingleNode("w:Custom[@Action='$($pair[0])']", $namespace)
    Require ($node.Sequence -ceq $pair[1] -and -not $node.HasAttribute('Condition')) 'Install, repair, uninstall and nested removal must all traverse unconditional guards.'
}
foreach ($pair in @(@('InstallValidate', '1400'), @('InstallInitialize', '1500'), @('RemoveExistingProducts', '1502'), @('InstallFinalize', '6600'))) {
    Require ($execute.SelectSingleNode("w:$($pair[0])", $namespace).Sequence -ceq $pair[1]) 'Standard MSI transaction/removal ordering does not match the frozen A2 fence.'
}
Require ($execute.SelectSingleNode('w:RemoveExistingProducts', $namespace).Condition -ceq 'HERMES_RELATED_PRODUCTS') 'Standard old-product removal must use the complete related-product inventory.'
foreach ($sequence in @('AdminExecuteSequence', 'AdvertiseExecuteSequence')) {
    $node = $package.SelectSingleNode("w:$sequence/w:Custom[@Action='HermesPreflight']", $namespace)
    Require ($null -ne $node -and $node.Sequence -ceq '1399' -and -not $node.HasAttribute('Condition')) 'Unsupported administrative and advertisement operations must refuse in execute sequences before mutation.'
}
Require ($package.SelectSingleNode('w:MediaTemplate', $namespace).EmbedCab -ceq 'yes' -and @($package.SelectNodes('w:Media', $namespace)).Count -eq 0) 'Every installation cabinet must remain embedded.'
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Hermes.Installer.wixproj') -Raw)
$validation = $project.SelectSingleNode('/Project/Target[@Name="ValidateHermesPayload"]')
Require (@($validation.SelectNodes('Error[not(@Condition)]')).Count -eq 0 `
    -and $validation.BeforeTargets.Split(';') -contains 'CoreCompile') 'Actual construction requirements must cover both ordinary and direct compiler entry.'
$construction = @($validation.SelectNodes('Exec') | Where-Object { $_.Command.Contains('Validate-PackageConstruction.ps1') })
Require ($construction.Count -eq 1 -and $construction[0].Command.Contains('-ServicingBuildPath') `
    -and $construction[0].Command.Contains('-ExpectedGuardSha256') -and $construction[0].Command.Contains('-ManifestPath') `
    -and $construction[0].Command.Contains('-PayloadDirectory')) 'The source-authorized build must still require actual native guard, compiled trust, coordinator inventory and fixed payload identity.'
$finalize = $project.SelectSingleNode('/Project/Target[@Name="FinalizeHermesPackageCode"]')
Require ($finalize.BeforeTargets -ceq 'WindowsInstallerValidation' -and $finalize.DependsOnTargets -ceq 'CoreCompile' `
    -and -not $finalize.HasAttribute('AfterTargets')) 'The selected PackageCode must be finalized before, never after or instead of, normal ICE validation.'
$finalizeCommand = $finalize.SelectSingleNode('Exec')
Require ($finalizeCommand.StandardOutputImportance -ceq 'High' -and $finalizeCommand.StandardErrorImportance -ceq 'High') 'Identity finalization failures must retain their original command diagnostics.'
$builder = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Build-Package.ps1') -Raw
Require ($builder -match '\-v\s+minimal\b') 'The build wrapper must not suppress finalization diagnostics with quiet logging.'
$finalizer = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Finalize-PackageIdentity.ps1') -Raw
Require ($finalizer.Contains("`$ErrorView = 'NormalView'")) 'Finalization type-conversion failures must retain their source location.'
Require ($finalizer -match '\[void\]\$view\.Execute\(\)' -and $finalizer -match '\[void\]\$view\.Close\(\)') 'MSI row queries must not emit bookkeeping method results as rows.'
$view = [pscustomobject]@{}
$view | Add-Member ScriptMethod Execute { 'synthetic execute result' }
$view | Add-Member ScriptMethod Close { 'synthetic close result' }
$noisyRows = @($view.Execute(); ,@('synthetic identity'); $view.Close())
Require ($noisyRows.Count -eq 3) 'The regression fixture must reproduce bookkeeping output contamination.'
$readRows = {
    param($query)
    try {
        [void]$query.Execute()
        ,@('synthetic identity')
    } finally { [void]$query.Close() }
}
$rows = @(& $readRows $view)
Require ($rows.Count -eq 1 -and $rows[0].Count -eq 1 -and $rows[0][0] -ceq 'synthetic identity') 'Bookkeeping results must not alter MSI row cardinality or shape.'
$view | Add-Member ScriptMethod Execute { throw 'synthetic query failure' } -Force
$queryFailed = $false
try { & $readRows $view | Out-Null }
catch {
    if ($_.Exception.GetBaseException().Message -cne 'synthetic query failure') { throw }
    $queryFailed = $true
}
Require $queryFailed 'Suppressing bookkeeping results must not suppress query failures.'
Write-Output 'Package source layout matches the frozen schema/guard contract; MSI compilation and Windows validation were not run.'
