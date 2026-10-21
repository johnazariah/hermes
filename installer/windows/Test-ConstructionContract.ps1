#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Servicing-Build.psm1') -Force
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$shared = Join-Path $repository 'src/Hermes.Servicing/bin/Release/net9.0/Hermes.Servicing.dll'
if ((Get-HermesServicingAbi $shared) -ne 1) { throw 'Actual compiled shared admission ABI differs from the frozen construction contract.' }
$refused = $false
try { $null = Get-HermesServicingAbi ([IO.Path]::Combine($PSHOME, 'System.Runtime.dll')) }
catch { $refused = $true }
if (-not $refused) { throw 'A non-servicing assembly must not establish an admission ABI.' }
$fixture = Join-Path $PSScriptRoot ('obj/construction-controls-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($fixture) | Out-Null
foreach ($name in @('Hermes.Setup.exe', 'Hermes.Setup.dll', 'Hermes.Setup.deps.json', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll')) {
    [IO.File]::WriteAllText((Join-Path $fixture $name), 'SYNTHETIC metadata-control file; never executed')
}
Copy-Item -LiteralPath $shared -Destination (Join-Path $fixture 'Hermes.Servicing.dll')
[IO.File]::WriteAllText((Join-Path $fixture 'Hermes.Setup.runtimeconfig.json'),
    '{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"9.0.0"},"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"9.0.0"}]}}')
$refused = $false
try { Assert-HermesSdkFreeRuntime $fixture 'Hermes.Setup' } catch { $refused = $true }
if (-not $refused) { throw 'A framework-dependent coordinator must not pass construction checks.' }
$lifecycle = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Test-Installer.ps1') -Raw
foreach ($required in @(
    "Invoke-CorrectiveRepair -Damage missing -RelativePath 'service/Hermes.Service.dll'",
    "Invoke-CorrectiveRepair -Damage changed -RelativePath 'service/wwwroot/index.html'",
    '-SamePackageInstall', 'verifiedPristine', 'fullInventoryRestored', 'actualInstalledNativeProof'
)) {
    if (-not $lifecycle.Contains($required)) { throw 'Corrective repair must retain missing/changed attested-owned-file and real native-restoration acceptance.' }
}
$gate = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Validate-PackageConstruction.ps1') -Raw
foreach ($required in @('Assert-HermesNativeGuard', 'Get-HermesCompiledGuardTrust', 'Get-HermesSetupPublishFiles',
    'Assert-HermesSdkFreeRuntime', 'moduleVersionId', 'LocalApplicationData/Hermes/App', 'github-hosted')) {
    if (-not $gate.Contains($required)) { throw 'The replacement construction gate lacks a required actual-artifact check.' }
}
& {
    function Assert-PackageLanguageSource([xml]$Document) {
        $namespaces = [Xml.XmlNamespaceManager]::new($Document.NameTable)
        $namespaces.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
        $packages = @($Document.SelectNodes('/w:Wix/w:Package', $namespaces))
        $overrides = @($Document.SelectNodes('//w:Property[@Id="ProductLanguage"]', $namespaces))
        if ($packages.Count -ne 1 -or $packages[0].GetAttribute('Language') -cne '1033' -or $overrides.Count -ne 0) {
            throw 'Package must author Language="1033" without a ProductLanguage override.'
        }
    }

    function Refuse-LanguageChange([scriptblock]$Action, [string]$Expected) {
        $refused = $false
        try { & $Action | Out-Null } catch {
            if ($_.Exception.Message -cne $Expected) { throw }
            $refused = $true
        }
        if (-not $refused) { throw 'A changed package language passed its contract check.' }
    }

    $sourceRefusal = 'Package must author Language="1033" without a ProductLanguage override.'
    $source = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Package.wxs') -Raw)
    Assert-PackageLanguageSource $source
    foreach ($value in @($null, '', '0', '1031', '$(var.HermesLanguage)', '!(loc.PackageLanguage)')) {
        $changed = [xml]$source.CloneNode($true)
        $package = $changed.GetElementsByTagName('Package', $changed.DocumentElement.NamespaceURI)[0]
        if ($null -eq $value) { $package.RemoveAttribute('Language') }
        else { $package.SetAttribute('Language', $value) }
        Refuse-LanguageChange { Assert-PackageLanguageSource $changed } $sourceRefusal
    }
    foreach ($value in @('1033', '0')) {
        $changed = [xml]$source.CloneNode($true)
        $package = $changed.GetElementsByTagName('Package', $changed.DocumentElement.NamespaceURI)[0]
        $override = $changed.CreateElement('Property', $package.NamespaceURI)
        $override.SetAttribute('Id', 'ProductLanguage')
        $override.SetAttribute('Value', $value)
        [void]$package.AppendChild($override)
        Refuse-LanguageChange { Assert-PackageLanguageSource $changed } $sourceRefusal
    }

    $finalizer = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Finalize-PackageIdentity.ps1') -Raw
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($finalizer, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'The producer language finalization gate must parse.' }
    $helpers = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Assert-HermesPackageLanguage'
    }, $true))
    $calls = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -ceq 'Assert-HermesPackageLanguage'
    }, $true))
    $readOnly = $finalizer.IndexOf('$database = $installer.OpenDatabase((Resolve-Path -LiteralPath $PackagePath).Path, 0)', [StringComparison]::Ordinal)
    if ($helpers.Count -ne 1 -or $calls.Count -ne 1 -or $readOnly -lt 0 -or $calls[0].Extent.StartOffset -le $readOnly) {
        throw 'The producer language helper must run once after the fresh read-only database is opened.'
    }
    . ([scriptblock]::Create($helpers[0].Extent.Text))

    function Get-RowValues([string]$Query, [int]$Columns) {
        if ($Query -cne "SELECT ``Value`` FROM ``Property`` WHERE ``Property``='ProductLanguage'" -or $Columns -ne 1) {
            throw 'ProductLanguage readback must use the existing scalar row query.'
        }
        foreach ($row in $languageRows) { ,$row }
    }

    $compiledRefusal = 'Bound MSI ProductLanguage must be exactly 1033.'
    $languageRows = @(,@('1033'))
    Assert-HermesPackageLanguage
    $languageRows = @()
    Refuse-LanguageChange { Assert-HermesPackageLanguage } $compiledRefusal
    foreach ($value in @($null, '', '0', '1031', '01033', '1033 ')) {
        $languageRows = @(,@($value))
        Refuse-LanguageChange { Assert-HermesPackageLanguage } $compiledRefusal
    }
    foreach ($extra in @('1033', '0')) {
        $languageRows = @(
            ,@('1033')
            ,@($extra)
        )
        Refuse-LanguageChange { Assert-HermesPackageLanguage } $compiledRefusal
        $languageRows = @(
            ,@($extra)
            ,@('1033')
        )
        Refuse-LanguageChange { Assert-HermesPackageLanguage } $compiledRefusal
    }
}
Write-Output 'Producer language source and compiled-row controls passed; no MSI was opened.'
Write-Output 'Construction contract and corrective-repair source controls passed; no coordinator, native guard, MSI or profile operation was executed.'
