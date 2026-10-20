#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Push-Location $repository
try {
    $scripts = @(Get-ChildItem -LiteralPath $PSScriptRoot -Include *.ps1,*.psm1 -Recurse) + @(Get-Item scripts/install.ps1)
    foreach ($file in $scripts) {
        $tokens = $null
        $errors = $null
        [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors) | Out-Null
        if ($errors.Count) { throw ($errors.Message -join '; ') }
    }
    foreach ($path in @('Package.wxs', 'Hermes.Installer.wixproj', 'Publish-Inventory.targets', 'tests/PublishInventory.proj')) {
        $null = [xml](Get-Content -LiteralPath (Join-Path $PSScriptRoot $path) -Raw)
    }
    foreach ($path in @('src/Hermes.Tray/Hermes.Tray.csproj', 'tests/Hermes.Tray.Smoke/Hermes.Tray.Smoke.csproj', 'tests/Hermes.Tray.Tests/Hermes.Tray.Tests.csproj')) {
        $null = [xml](Get-Content -LiteralPath $path -Raw)
    }
    $harnessProject = [xml](Get-Content -LiteralPath tests/Hermes.Tray.Smoke/Hermes.Tray.Smoke.csproj -Raw)
    $trayReferences = @($harnessProject.SelectNodes('//ProjectReference') | Where-Object {
        $_.Include.Replace('\', '/').Split('/')[-1] -ieq 'Hermes.Tray.csproj'
    })
    if ($trayReferences.Count -ne 0) {
        throw 'The external installed-Tray harness must not reference the Tray assembly.'
    }
    $lifecycleSource = Get-Content -LiteralPath installer/windows/Test-Installer.ps1 -Raw
    $fixtureCreation = $lifecycleSource.IndexOf('--create-owner-fixture', [StringComparison]::Ordinal)
    $preservationBaseline = $lifecycleSource.IndexOf('$before = Get-SyntheticHashes', [StringComparison]::Ordinal)
    if ([regex]::Matches($lifecycleSource, '--create-owner-fixture').Count -ne 1 `
        -or $fixtureCreation -lt 0 -or $fixtureCreation -ge $preservationBaseline `
        -or -not $lifecycleSource.Contains('--owner-fixture-receipt')) {
        throw 'Owner fixture creation must occur once before the preserved baseline and be reused by native proof.'
    }
    if ($lifecycleSource -match '\[IO\.File\]::WriteAllText\(\(Join-Path \$config\b' `
        -or $lifecycleSource.Contains('[IO.Directory]::CreateDirectory($config)')) {
        throw 'PowerShell must not create or rewrite the owner configuration fixture itself.'
    }
    Import-Module (Join-Path $PSScriptRoot 'Installer-Identity.psm1') -Force
    foreach ($version in @('0.0.0', '255.255.65535')) { Assert-HermesPackageVersion $version }
    foreach ($version in @('256.0.0', '0.256.0', '0.0.65536', '01.0.0', '1.2.3.4')) {
        $rejected = $false
        try { Assert-HermesPackageVersion $version } catch { $rejected = $true }
        if (-not $rejected) { throw 'Invalid MSI version accepted.' }
    }
    & node --test installer/windows/payload.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic publish inventory controls failed.' }
    & (Join-Path $PSScriptRoot 'Test-PackageLayout.ps1')
    & (Join-Path $PSScriptRoot 'Test-SetupContract.ps1')
    & (Join-Path $PSScriptRoot 'Test-ConstructionContract.ps1')
    & (Join-Path $PSScriptRoot 'Test-AcceptanceReport.ps1')
    & dotnet msbuild installer/windows/tests/PublishInventory.proj -t:WriteHermesPublishInventory -nologo -v:q
    if ($LASTEXITCODE -ne 0) { throw 'MSBuild publish inventory task failed.' }
    $fields = (Get-Content -LiteralPath installer/windows/obj/static-publish-inventory.txt -Raw).Trim().Split('|')
    $expected = (Get-FileHash -LiteralPath installer/windows/payload.test.mjs -Algorithm SHA256).Hash
    if ($fields.Count -ne 3 -or $fields[1] -ne 'synthetic-fixture.dll' -or $fields[2] -ne $expected) {
        throw 'MSBuild publish source metadata/hash capture failed.'
    }
    & actionlint .github/workflows/ci.yml .github/workflows/release.yml
    if ($LASTEXITCODE -ne 0) { throw 'Workflow syntax lint failed.' }
    & bash -n scripts/install.sh scripts/setup-mac.sh
    if ($LASTEXITCODE -ne 0) { throw 'Excluded macOS wrapper syntax failed.' }
    foreach ($path in @('Package.wxs', 'Hermes.Installer.wixproj', 'payload.mjs', 'payload.test.mjs',
        'Read-PayloadMetadata.ps1', 'Write-BuildIdentity.ps1', 'Finalize-PackageIdentity.ps1', 'Test-PackageLayout.ps1',
        'Package-Validation.psm1', 'Validate-PackageIce.ps1', 'Test-ValidationPolicy.ps1', 'Test-PackageIceInputs.ps1')) {
        $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot $path))
        if ($text.Contains("`r") -or $text -match '(?m)[ \t]+$') { throw 'Package source must use LF without trailing whitespace.' }
    }
    Write-Output 'Static installer controls passed; no Windows MSI, application, profile or provider action was run.'
} finally { Pop-Location }
