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
$admin = $package.SelectSingleNode('w:AdminExecuteSequence/w:Custom', $namespace)
Require ($admin.Action -ceq 'HermesPreflight' -and $admin.Sequence -ceq '1399' -and -not $admin.HasAttribute('Condition')) 'Unsupported administrative operations must retain unconditional native preflight before mutation.'
$advertise = $package.SelectSingleNode('w:AdvertiseExecuteSequence/w:Custom', $namespace)
Require ($advertise.Action -ceq 'HermesRejectAdvertisement' -and $advertise.Sequence -ceq '1399' -and -not $advertise.HasAttribute('Condition')) 'Advertising must unconditionally refuse before standard initialization or publication.'
$refusal = $package.SelectSingleNode('w:CustomAction[@Id="HermesRejectAdvertisement"]', $namespace)
Require ($refusal.Attributes.Count -eq 2 -and $refusal.Error -ceq 'Hermes advertisement is unsupported; use Hermes.Setup.') 'Advertising must use the permitted built-in type-19 failure, not a DLL action or property-dependent message.'
Require (@($package.SelectNodes('w:AdvertiseExecuteSequence/w:Custom', $namespace)).Count -eq 1 `
    -and @($package.SelectNodes('w:CustomAction', $namespace)).Count -eq 3) 'No additional custom-action or advertisement path may bypass checked refusal.'
Require ($package.SelectSingleNode('w:MediaTemplate', $namespace).EmbedCab -ceq 'yes' -and @($package.SelectNodes('w:Media', $namespace)).Count -eq 0) 'Every installation cabinet must remain embedded.'
$project = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Hermes.Installer.wixproj') -Raw)
$validation = $project.SelectSingleNode('/Project/Target[@Name="ValidateHermesPayload"]')
Require (@($validation.SelectNodes('Error[not(@Condition)]')).Count -eq 0 `
    -and $validation.BeforeTargets.Split(';') -contains 'CoreCompile') 'Actual construction requirements must cover both ordinary and direct compiler entry.'
$construction = @($validation.SelectNodes('Exec') | Where-Object { $_.Command.Contains('Validate-PackageConstruction.ps1') })
Require ($construction.Count -eq 1 -and $construction[0].Command.Contains('-ServicingBuildPath') `
    -and $construction[0].Command.Contains('-ExpectedGuardSha256') -and $construction[0].Command.Contains('-ManifestPath') `
    -and $construction[0].Command.Contains('-PayloadDirectory')) 'The source-authorized build must still require actual native guard, compiled trust, coordinator inventory and fixed payload identity.'
$imports = @($project.SelectNodes('/Project/Import'))
Require ($imports.Count -eq 2 -and $imports[0].Project -ceq 'Sdk.props' -and $imports[1].Project -ceq 'Sdk.targets' `
    -and $imports[0].Sdk -ceq 'WixToolset.Sdk' -and $imports[1].Sdk -ceq $imports[0].Sdk `
    -and $imports[0].Version -ceq '6.0.0' -and $imports[1].Version -ceq $imports[0].Version) 'Native validation must use the pinned SDK, not an installed global tool.'
$iceTarget = $project.SelectSingleNode('/Project/Target[@Name="WindowsInstallerValidation"]')
Require ($iceTarget.DependsOnTargets -ceq 'CoreCompile' -and -not $iceTarget.HasAttribute('Condition') `
    -and $iceTarget.SelectSingleNode('preceding-sibling::*[1]') -eq $imports[1]) 'The complete native policy target must override the SDK target after its import and cannot conditionally skip validation.'
$iceCommands = @($iceTarget.SelectNodes('Exec'))
Require ($iceCommands.Count -eq 2 -and $iceCommands[0].Command.Contains('Test-PackageIceInputs.ps1') `
    -and -not $iceCommands[0].HasAttribute('IgnoreExitCode')) 'Actual isolated MSI path-mutation controls must run and fail the build before normal native validation.'
$iceCommand = $iceCommands[1]
Require ($iceCommand.Command.Contains('Validate-PackageIce.ps1') -and $iceCommand.Command.Contains('$(WixBinDir)wix.dll') `
    -and $iceCommand.IgnoreStandardErrorWarningFormat -ceq 'true' -and -not $iceCommand.HasAttribute('IgnoreExitCode')) 'Only the independently authenticated native-policy wrapper may interpret native diagnostics; wrapper failure must fail the build.'
$iceSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Validate-PackageIce.ps1') -Raw
$iceTokens = $null
$iceErrors = $null
$iceAst = [Management.Automation.Language.Parser]::ParseInput($iceSource, [ref]$iceTokens, [ref]$iceErrors)
Require ($iceErrors.Count -eq 0) 'The actual native-validation wrapper must parse.'
$argumentLoops = @($iceAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Body.Extent.Text.Contains('$start.ArgumentList.Add')
}, $true))
Require ($argumentLoops.Count -eq 1) 'The complete native command must have one bounded argument source.'
$start = [Diagnostics.ProcessStartInfo]::new('dotnet')
$validator = [pscustomobject]@{ WixPath = Join-Path $PSScriptRoot 'Hermes.Installer.wixproj' }
$package = Join-Path $PSScriptRoot 'obj/synthetic-never-executed.msi'
$WixpdbPath = Join-Path $PSScriptRoot 'obj/synthetic-never-executed.wixpdb'
$IntermediateDirectory = Join-Path $PSScriptRoot 'obj'
& ([scriptblock]::Create($argumentLoops[0].Extent.Text))
$expectedArguments = @($validator.WixPath, 'msi', 'validate', $package, '-nologo',
    '-pdb', [IO.Path]::GetFullPath($WixpdbPath), '-intermediatefolder', [IO.Path]::GetFullPath($IntermediateDirectory))
Require (($start.ArgumentList -join "`n") -ceq ($expectedArguments -join "`n")) 'The native invocation must execute the complete default ICE set with no ICE restriction, suppression or warning switches.'
Require ($iceSource.Contains('$installer.OpenDatabase($package, 0)') `
    -and $iceSource.Contains('$validator = Open-HermesPinnedWixValidator $WixPath') `
    -and $iceSource.IndexOf('Open-HermesPinnedWixValidator $WixPath', [StringComparison]::Ordinal) -lt $iceSource.IndexOf('[Diagnostics.Process]::Start($start)', [StringComparison]::Ordinal) `
    -and $iceSource.Contains('Assert-HermesIcePrerequisites $manifest $facts') `
    -and $iceSource.Contains('SELECT `File`, `Component_`, `FileName`, `FileSize` FROM `File`') `
    -and $iceSource.Contains('SELECT `Component`, `ComponentId`, `Directory_` FROM `Component`') `
    -and $iceSource.Contains('WHERE `VersionMax` IS NULL AND `Remove` IS NULL') `
    -and $iceSource.Contains('$output = Read-HermesIceOutput $process') `
    -and $iceSource.Contains('-Lines $output.Lines') `
    -and $iceSource.Contains('-NativeExitCode $process.ExitCode') `
    -and $iceSource.Contains(" 'ice-validation.log'") `
    -and $iceSource.Contains("throw 'Validation changed the finalised MSI") `
    -and $iceSource.Contains('validatorPackageSha256 = $validator.PackageSha256') `
    -and $iceSource.Contains('foreach ($lease in $validator.Leases) { $lease.Dispose() }')) 'Native policy must authenticate and retain pinned SDK handles, bind actual file paths and identities, require native success, retain diagnostics and forbid post-validation mutation.'
$finalize = $project.SelectSingleNode('/Project/Target[@Name="FinalizeHermesPackageCode"]')
Require ($finalize.BeforeTargets -ceq 'WindowsInstallerValidation' -and $finalize.DependsOnTargets -ceq 'CoreCompile' `
    -and -not $finalize.HasAttribute('AfterTargets')) 'The selected PackageCode must be finalized before, never after or instead of, normal ICE validation.'
$finalizeCommand = $finalize.SelectSingleNode('Exec')
Require ($finalizeCommand.StandardOutputImportance -ceq 'High' -and $finalizeCommand.StandardErrorImportance -ceq 'High') 'Identity finalization failures must retain their original command diagnostics.'
$builder = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Build-Package.ps1') -Raw
Require ($builder -match '\-v\s+minimal\b') 'The build wrapper must not suppress finalization diagnostics with quiet logging.'
$finalizer = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Finalize-PackageIdentity.ps1') -Raw
Require ($finalizer.Contains("`$ErrorView = 'NormalView'")) 'Finalization type-conversion failures must retain their source location.'
Require ($finalizer.Contains('MSI finalization failed during $phase') -and $finalizer.Contains('$_.ScriptStackTrace')) 'Finalization failures must retain explicit phase and stack context independently of the error formatter.'
Require ($finalizer.Contains("`n`$phase = 'product-properties'`ntry {")) 'The initial failure phase must be set in script scope before opening the MSI, not in the row helper scope.'
$parseTokens = $null
$parseErrors = $null
$finalizerAst = [Management.Automation.Language.Parser]::ParseInput($finalizer, [ref]$parseTokens, [ref]$parseErrors)
Require ($parseErrors.Count -eq 0) 'The finalizer diagnostic contract must parse.'
$failureCatch = @($finalizerAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.CatchClauseAst]
}, $true))
Require ($failureCatch.Count -eq 1) 'The finalizer must have one failure-context catch.'
$phase = 'synthetic-phase'
$expectedFailure = [InvalidOperationException]::new('synthetic finalization failure')
$failureProbe = [scriptblock]::Create('try { throw $expectedFailure } catch ' + $failureCatch[0].Body.Extent.Text)
$diagnostics = [IO.StringWriter]::new()
$savedError = [Console]::Error
$contextFailed = $false
try {
    [Console]::SetError($diagnostics)
    try { & $failureProbe }
    catch {
        Require ([object]::ReferenceEquals($_.Exception, $expectedFailure)) 'Diagnostic context must preserve the original exception identity.'
        $contextFailed = $true
    }
} finally { [Console]::SetError($savedError) }
Require ($contextFailed -and $diagnostics.ToString().Contains('MSI finalization failed during synthetic-phase; source line ')) 'Explicit finalization diagnostics must retain failure and identify its phase.'
$readbackStart = $finalizer.IndexOf("`$phase = 'summary-package-readback'")
Require ($readbackStart -gt $finalizer.IndexOf('[void]$database.Commit()')) 'Persisted summary readback must follow the database commit.'
$writeSource = $finalizer.Substring(0, $readbackStart)
Require ($writeSource.Contains('[void]$summary.Persist()') `
    -and $writeSource.Contains('[Runtime.InteropServices.Marshal]::FinalReleaseComObject($summary)') `
    -and $writeSource.Contains('[Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)')) 'Summary readback must close the writable summary and database after persistence.'
$readbackSource = $finalizer.Substring($readbackStart)
Require ($readbackSource.Contains('$database = $installer.OpenDatabase((Resolve-Path -LiteralPath $PackagePath).Path, 0)') `
    -and $readbackSource.Contains("'SummaryInformation', [Reflection.BindingFlags]::GetProperty, `$null, `$database, @(0)")) 'Persisted identity must be read from a fresh read-only database and summary.'
$packageChecks = @($finalizerAst.FindAll({
    param($node)
    $node -is [Management.Automation.Language.IfStatementAst] `
        -and ($node.Extent.Text.Contains("throw 'MSI summary PackageCode setter") `
            -or $node.Extent.Text.Contains("throw 'MSI summary PackageCode was not persisted."))
}, $true))
Require ($packageChecks.Count -eq 2) 'Both retained and persisted PackageCode require strict identity checks.'
$identity = [pscustomobject]@{ packageCode = '11111111-1111-1111-1111-111111111111' }
foreach ($check in $packageChecks) {
    $guard = [scriptblock]::Create($check.Extent.Text)
    $pending = $actual = $identity.packageCode
    & $guard
    foreach ($invalid in @($null, '', 'not-a-guid', '22222222-2222-2222-2222-222222222222')) {
        $pending = $actual = $invalid
        $identityFailed = $false
        try { & $guard } catch { $identityFailed = $true }
        Require $identityFailed 'Missing, malformed or different retained/persisted PackageCode must fail.'
    }
}
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
& (Join-Path $PSScriptRoot 'Test-ValidationPolicy.ps1')
Write-Output 'Package source layout matches the frozen schema/guard contract; MSI compilation and Windows validation were not run.'
