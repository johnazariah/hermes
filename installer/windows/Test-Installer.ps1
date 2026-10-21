#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PayloadDirectory,
    [Parameter(Mandatory = $true)][string]$InventoryDirectory,
    [Parameter(Mandatory = $true)][string]$UpgradePayloadDirectory,
    [Parameter(Mandatory = $true)][string]$UpgradeInventoryDirectory,
    [Parameter(Mandatory = $true)][string]$SmokeExecutable,
    [Parameter(Mandatory = $true)][string]$SetupExecutable,
    [Parameter(Mandatory = $true)][string]$GuardPath,
    [Parameter(Mandatory = $true)][string]$ServicingBuildPath,
    [Parameter(Mandatory = $true)][string]$AcceptanceExecutable,
    [Parameter(Mandatory = $true)][string]$ProbeOldDirectory,
    [Parameter(Mandatory = $true)][string]$ProbeNewDirectory,
    [Parameter(Mandatory = $true)][ValidatePattern('\A[0-9a-f]{64}\z')][string]$GuardSha256,
    [Parameter(Mandatory = $true)][string]$EvidenceDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_OS -ne 'Windows' `
    -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer lifecycle smoke is restricted to a disposable GitHub-hosted Windows job.'
}
if (-not [string]::IsNullOrEmpty($env:HERMES_CONFIG_DIR) -or -not [string]::IsNullOrEmpty($env:HERMES_PORT)) {
    throw 'Inherited Hermes runtime overrides are not permitted in disposable installer acceptance.'
}
Import-Module (Join-Path $PSScriptRoot 'Installer-Identity.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Setup-Process.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Acceptance-Report.psm1') -Force
Import-Module (Join-Path $PSScriptRoot 'Native-Evidence.psm1') -Force
$registration = 'HKCU:\Software\Hermes\Installer'
$config = Join-Path ([Environment]::GetFolderPath('ApplicationData')) 'hermes'
$local = [Environment]::GetFolderPath('LocalApplicationData')
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Hermes/Hermes.lnk'
# Windows Installer inventory must be checked before creating even synthetic profile state.
if (@(Get-HermesRelatedProducts).Count -ne 0) {
    throw 'Related installed, advertised or damaged Hermes product found; refusing all MSI actions.'
}
if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $config) `
    -or (Test-Path -LiteralPath $shortcut) `
    -or (Test-Path -LiteralPath (Join-Path $local 'Hermes')) `
    -or (Get-ItemProperty -LiteralPath $runKey -Name Hermes -ErrorAction SilentlyContinue)) {
    throw 'Existing Hermes state found; refusing to modify this Windows profile.'
}
$workspace = [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE)
$destination = [IO.Path]::GetFullPath($EvidenceDirectory)
if (-not $destination.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Synthetic evidence must remain inside this disposable workspace.'
}
$root = Join-Path $workspace ('.artifacts/installer-' + [Guid]::NewGuid().ToString('N'))
$nativeRoot = Join-Path ([IO.Path]::GetFullPath($env:RUNNER_TEMP)) ('hermes-installer-proof-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
[IO.Directory]::CreateDirectory($nativeRoot) | Out-Null
[IO.Directory]::CreateDirectory($destination) | Out-Null
$install = Join-Path $local 'Hermes/App'
$SetupExecutable = (Resolve-Path -LiteralPath $SetupExecutable).Path
if ($SetupExecutable.StartsWith($install.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'The distributed coordinator must execute outside AppRoot.' }
if ((Get-FileHash -LiteralPath $GuardPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $GuardSha256) { throw 'The selected native guard changed after independent publication.' }
$archive = Join-Path $nativeRoot 'synthetic-archive'
$ownerFixtureReceiptPath = Join-Path $nativeRoot 'owner-fixture.json'
$ownerFixtureId = $null
$ownerFixtureReceipt = $null
$msiHost = Join-Path $env:SystemRoot 'System32/msiexec.exe'
$ownedProducts = [Collections.Generic.HashSet[string]]::new()
$evidence = [Collections.Generic.List[object]]::new()
$cleanupFailures = [Collections.Generic.List[string]]::new()
$nativeReports = [Collections.Generic.List[string]]::new()
$failure = $null
$completed = $false
$stage = 'build'
$packages = @{}
$lastCoordinator = $null
$before = $null
$componentExpectation = $null
$componentExitCode = $null

function Save-Evidence {
    Assert-HermesCoordinatorStages @($evidence | ForEach-Object { [pscustomobject]$_ })
    [ordered]@{
        passed = $completed; originalFailure = $failure; stage = $stage
        stages = $evidence; nativeReports = $nativeReports; cleanupFailures = $cleanupFailures
        ownerFixtureId = $ownerFixtureId
        guardSha256 = $GuardSha256
        componentExpectation = $componentExpectation; componentExitCode = $componentExitCode
    } | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $destination 'acceptance.json') -Encoding utf8
}

function Assert-RawMsiRefused([ValidateSet('/i', '/fa', '/x', '/a', '/ju')][string]$Operation, [string]$Target, [string]$Name) {
    if ($Target.Contains('"') -or $install.Contains('"')) { throw 'Invalid MSI argument path.' }
    $log = Join-Path $root "$Name.log"
    $arguments = "$Operation `"$Target`" /qn /norestart /l*v `"$log`""
    if ($Operation -eq '/i') { $arguments += ' HERMES_ALLOW_ROLLBACK=1' }
    $child = Start-Process -FilePath $msiHost -ArgumentList $arguments -Wait -PassThru
    $evidence.Add([ordered]@{ action = $Name; kind = 'raw-msi-negative'; exitCode = $child.ExitCode; expectedRefusal = $true })
    Save-Evidence
    if ($child.ExitCode -ne 1603) { throw "Expected direct MSI safety refusal (1603): $Name" }
}

function Invoke-Coordinator([string]$Operation, [string]$Name, [string]$PackagePath = '', [switch]$Rollback, [int]$ExpectedExit = 0, [int[]]$AcceptedExits = @()) {
    $saved = @{ PATH = $env:PATH; DOTNET_ROOT = $env:DOTNET_ROOT; DOTNET_ROOT_X64 = $env:DOTNET_ROOT_X64 }
    try {
        $env:PATH = Join-Path $env:SystemRoot 'System32'
        $env:DOTNET_ROOT = Join-Path $root 'absent-dotnet-sdk'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $result = Invoke-HermesSetup -SetupExecutable $SetupExecutable -Operation $Operation -PackagePath $PackagePath -AllowRollback:$Rollback -Quiet
    } finally {
        $env:PATH = $saved.PATH; $env:DOTNET_ROOT = $saved.DOTNET_ROOT; $env:DOTNET_ROOT_X64 = $saved.DOTNET_ROOT_X64
    }
    $script:lastCoordinator = $result
    $evidence.Add([ordered]@{ action = $Name; kind = 'coordinator'; exitCode = $result.exitCode
        report = $result.report; diagnosticCodes = $result.diagnosticCodes; protocolError = $result.protocolError
        refusalEvidence = $result.refusalEvidence; diagnosticError = $result.diagnosticError
        msiFailureEvidence = $result.msiFailureEvidence; sdkPathAbsent = $true })
    Save-Evidence
    if ($null -ne $result.protocolError -or $null -eq $result.report) { throw "Coordinator report failed validation: $Name" }
    $permitted = if ($AcceptedExits.Count -gt 0) { $AcceptedExits } else { @($ExpectedExit) }
    if ($result.exitCode -notin $permitted) { throw "Unexpected coordinator result during $Name (exit $($result.exitCode))." }
    if ($result.exitCode -eq 0 -and (-not $result.report.ready -or $result.report.outcome -cne 'Completed')) { throw 'Coordinator did not verify a completed ready outcome.' }
    return $result
}

function Get-SyntheticHashes {
    $paths = @(
        (Join-Path $config 'config.yaml'), (Join-Path $config 'synthetic-settings.txt'),
        (Join-Path $config '.hermes-installer-fixture.json'), $ownerFixtureReceiptPath,
        (Join-Path $archive 'source.txt'), (Join-Path $install 'user-data/keep.txt'),
        (Join-Path $install 'service/unknown-user-file.txt'), (Join-Path $install 'tray/user-data/keep.txt')
    )
    $hashes = [ordered]@{}
    foreach ($path in $paths) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Synthetic user-owned data disappeared.' }
        $hashes[$path] = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
    $hashes | ConvertTo-Json -Compress
}

function Assert-DataUnchanged([string]$Before) {
    if ((Get-SyntheticHashes) -cne $Before) { throw 'Installer changed synthetic configuration/archive/unknown application bytes.' }
}

function Assert-Installed($Package) {
    $state = Get-ItemProperty -LiteralPath $registration
    if ($state.Version -ne $Package.version -or $state.ProductCode -ne $Package.product `
        -or [IO.Path]::GetFullPath($state.InstallDirectory).TrimEnd('\') -ne $install.TrimEnd('\')) {
        throw 'Application registration does not identify the expected package and install directory.'
    }
    $related = @(Get-HermesRelatedProducts)
    if ($related.Count -ne 1 -or $related[0] -ne $Package.product) { throw 'Related product inventory mismatch.' }
    foreach ($file in $Package.inventory.files) {
        $actual = Join-Path $install $file.path
        if (-not (Test-Path -LiteralPath $actual -PathType Leaf) `
            -or (Get-FileHash -LiteralPath $actual -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) {
            throw "Installed runtime inventory mismatch: $($file.path)"
        }
    }
    $tray = Join-Path $install 'tray/Hermes.Tray.exe'
    if ((Get-ItemProperty -LiteralPath $runKey -Name Hermes).Hermes -cne "`"$tray`"") { throw 'Run registration mismatch.' }
    if (-not (Test-Path -LiteralPath $shortcut -PathType Leaf)) { throw 'Start Menu shortcut missing.' }
    $shell = New-Object -ComObject WScript.Shell
    $link = $null
    try {
        $link = $shell.CreateShortcut($shortcut)
        if ([IO.Path]::GetFullPath($link.TargetPath) -ne $tray) { throw 'Shortcut target mismatch.' }
    } finally {
        foreach ($item in @($link, $shell)) {
            if ($null -ne $item) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($item) | Out-Null }
        }
    }
}

function Assert-WrapperRefuses([string]$Target, [switch]$Uninstall, [switch]$Repair) {
    $arguments = @{ SetupExecutable = $SetupExecutable; Quiet = $true }
    $operation = if ($Uninstall) { $arguments.Uninstall = $true; 'uninstall' } elseif ($Repair) { $arguments.Repair = $true; 'repair' } else { $arguments.PackagePath = $Target; 'install' }
    $output = @(& (Join-Path $PSScriptRoot 'Install-Package.ps1') @arguments)
    $code = $LASTEXITCODE
    $report = ConvertFrom-HermesSetupResult -StandardOutput ($output -join "`n") -ExitCode $code -Operation $operation
    $evidence.Add([ordered]@{ action = "running-wrapper-$operation"; exitCode = $code; report = $report })
    Save-Evidence
    if ($code -ne 1618 -or $report.ready) { throw 'Wrapper did not preserve coordinator busy refusal while the installed runtime was live.' }
}

function Invoke-InstalledRuntime([string]$Name, $Package, $UpgradePackage) {
    $report = Join-Path $nativeRoot "$Name-native.json"
    $ready = Join-Path $nativeRoot "$Name-ready"
    $continue = Join-Path $nativeRoot "$Name-continue"
    $tray = Join-Path $install 'tray/Hermes.Tray.exe'
    $savedPath = $env:PATH
    $savedRoot = $env:DOTNET_ROOT
    $savedRootX64 = $env:DOTNET_ROOT_X64
    $hostProcess = $null
    try {
        $env:PATH = Join-Path $env:SystemRoot 'System32'
        $env:DOTNET_ROOT = Join-Path $root 'absent-dotnet-sdk'
        $env:DOTNET_ROOT_X64 = $env:DOTNET_ROOT
        $arguments = "--installed-tray-path `"$tray`" --port 31879 --report `"$report`" --timeout-seconds 300 --ready-file `"$ready`" --continue-file `"$continue`" --owner-fixture-receipt `"$ownerFixtureReceiptPath`""
        $hostProcess = Start-Process -FilePath $SmokeExecutable -ArgumentList $arguments -PassThru
        $deadline = [DateTime]::UtcNow.AddSeconds(180)
        while (-not (Test-Path -LiteralPath $ready)) {
            if ($hostProcess.HasExited -or [DateTime]::UtcNow -ge $deadline) { throw 'Installed Tray did not prove readiness.' }
            Start-Sleep -Milliseconds 250
        }
        if ((Get-Item -LiteralPath $ready).Length -gt 4KB) { throw 'Native readiness evidence exceeds its bound.' }
        $live = Get-Content -LiteralPath $ready -Raw | ConvertFrom-Json
        if ($live.trayProcessId -le 0 -or $live.serviceProcessId -le 0 -or $live.serviceStartUtcTicks -le 0) {
            throw 'Native readiness does not identify the owned installed processes.'
        }
        $null = Invoke-Coordinator 'install' "$Name-running-upgrade" $UpgradePackage.path -ExpectedExit 1618
        $null = Invoke-Coordinator 'repair' "$Name-running-repair" -ExpectedExit 1618
        $null = Invoke-Coordinator 'uninstall' "$Name-running-uninstall" -ExpectedExit 1618
        Assert-RawMsiRefused '/i' $UpgradePackage.path "$Name-running-raw-upgrade"
        Assert-RawMsiRefused '/fa' $Package.product "$Name-running-raw-repair"
        Assert-RawMsiRefused '/x' $Package.product "$Name-running-raw-uninstall"
        Assert-WrapperRefuses $UpgradePackage.path
        Assert-WrapperRefuses '' -Repair
        Assert-WrapperRefuses '' -Uninstall
        Assert-Installed $Package
        Assert-DataUnchanged $before
    } catch {
        $script:failure = [ordered]@{ stage = $Name; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
        Save-Evidence
        throw
    } finally {
        # Always release the bounded native harness; never kill Tray, Service or unrelated processes.
        [IO.File]::WriteAllText($continue, 'continue')
        $env:PATH = $savedPath; $env:DOTNET_ROOT = $savedRoot; $env:DOTNET_ROOT_X64 = $savedRootX64
        if ($null -ne $hostProcess -and -not $hostProcess.WaitForExit(360000)) {
            $cleanupFailures.Add('Native harness did not exit within its bounded graceful shutdown budget.')
        }
        if (Test-Path -LiteralPath $report) {
            if ((Get-Item -LiteralPath $report).Length -gt 256KB) {
                $cleanupFailures.Add('Native synthetic report exceeds evidence size bound; report is not approved for upload.')
            } else {
                Copy-Item -LiteralPath $report -Destination (Join-Path $destination "$Name-native.json")
                $nativeReports.Add([IO.Path]::GetFileName($report))
            }
        }
        Save-Evidence
    }
    if ($hostProcess.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $report)) { throw 'Installed Tray native smoke failed.' }
    $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if (-not $result.passed -or -not $result.cleaned -or -not $result.stop.graceful `
        -or $result.stop.forced -or -not $result.stop.exited -or $result.stop.exitCode -ne 0 `
        -or -not $result.installedTrayLaunched -or $result.trayExecutablePath -ne $tray `
        -or $result.trayProcessId -ne $live.trayProcessId -or $result.trayStartUtcTicks -le 0 `
        -or $result.processId -ne $live.serviceProcessId -or $result.serviceStartUtcTicks -ne $live.serviceStartUtcTicks `
        -or -not $result.installedSourceUnchanged -or -not $result.readyReached -or -not $result.continued `
        -or -not $result.ownerStateVerified -or -not $result.ownerFixtureVerified -or $result.ownerFixtureId -cne $ownerFixtureId `
        -or $result.installedProductCode -ne $Package.product -or $result.installedVersion -ne $Package.version) {
        throw 'Installed Tray/owned Service/WebView lifecycle proof failed.'
    }
    foreach ($proof in $result.installedFiles) {
        $file = @($Package.inventory.files | Where-Object path -CEQ $proof.relativePath)
        if ($file.Count -ne 1 -or $file[0].sha256 -ne $proof.sha256) { throw 'Native source hashes differ from the package inventory.' }
    }
    if (@($result.installedFiles).Count -ne 4) { throw 'Native executable/managed-source identity evidence is incomplete.' }
    $ownerHashes = @{ 'config.yaml' = $ownerFixtureReceipt.configSha256; 'synthetic-settings.txt' = $ownerFixtureReceipt.settingsSha256 }
    foreach ($ownerFile in $ownerHashes.Keys) {
        $proof = @($result.ownerFixtureFiles | Where-Object relativePath -CEQ $ownerFile)
        if ($proof.Count -ne 1 -or $proof[0].sha256 -ne $ownerHashes[$ownerFile]) { throw 'Native owner-fixture hashes differ from the original receipt.' }
    }
    if (@($result.ownerFixtureFiles).Count -ne 2) { throw 'Native owner-fixture preservation evidence is incomplete.' }
    $evidence.Add([ordered]@{ stage = $Name; sdkPathAbsent = $true; runtime = $result; inventoryVerified = $true })
    Save-Evidence
}

function Invoke-CorrectiveRepair(
    [ValidateSet('missing', 'changed')][string]$Damage,
    [ValidateSet('service/Hermes.Service.dll', 'service/wwwroot/index.html')][string]$RelativePath,
    $Package,
    $UpgradePackage,
    [switch]$SamePackageInstall
) {
    $name = "corrective-$Damage"
    $script:stage = $name
    Assert-Installed $Package
    Assert-DataUnchanged $before
    Assert-HermesRuntimeStopped -InstallDirectory $install
    $owned = @($Package.inventory.files | Where-Object path -CEQ $RelativePath)
    if ($owned.Count -ne 1 -or -not $owned[0].fileId -or -not $owned[0].componentId `
        -or [Guid]$owned[0].componentCode -eq [Guid]::Empty) { throw 'Corrective damage requires one attested MSI-owned file/component.' }
    $path = [IO.Path]::GetFullPath((Join-Path $install $RelativePath))
    if (-not $path.StartsWith($install.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Corrective repair target escaped AppRoot.' }
    $item = Get-Item -LiteralPath $path -Force
    $reachedRoot = $false
    while ($null -ne $item) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Corrective repair never follows a redirected owned target.' }
        if ($item.FullName.TrimEnd('\') -ieq $install.TrimEnd('\')) { $reachedRoot = $true; break }
        $item = if ($item -is [IO.FileInfo]) { $item.Directory } else { $item.Parent }
    }
    if (-not $reachedRoot) { throw 'The corrective target did not retain its fixed AppRoot ancestry.' }
    $marker = Get-ItemPropertyValue -LiteralPath 'HKCU:\Software\Hermes\Installer\Components' -Name $owned[0].componentId
    if ($marker -ne 1 -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $owned[0].sha256) {
        throw 'Corrective mutation refused: current owned marker or pristine hash is not established.'
    }
    $evidence.Add([ordered]@{ action = $name; phase = 'before-damage'; ownedPath = $RelativePath
        fileId = $owned[0].fileId; componentId = $owned[0].componentId; componentCode = $owned[0].componentCode
        expectedSha256 = $owned[0].sha256; verifiedPristine = $true })
    Save-Evidence
    if ($Damage -eq 'missing') {
        [IO.File]::Move($path, (Join-Path $root "$name-owned-original.bin"))
        if (Test-Path -LiteralPath $path) { throw 'The attested missing-file condition was not established.' }
    } else {
        [IO.File]::WriteAllText($path, 'SYNTHETIC damage to an attested MSI-owned application resource')
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $owned[0].sha256) {
            throw 'The attested changed-file condition was not established.'
        }
    }
    Assert-DataUnchanged $before
    $source = if ($SamePackageInstall) { $Package.path } else { '' }
    $operation = if ($SamePackageInstall) { 'install' } else { 'repair' }
    $null = Invoke-Coordinator $operation "$name-$operation" $source
    Assert-Installed $Package
    Assert-DataUnchanged $before
    Invoke-InstalledRuntime $name $Package $UpgradePackage
    Assert-Installed $Package
    Assert-DataUnchanged $before
    $evidence.Add([ordered]@{ action = $name; phase = 'restored'; ownedPath = $RelativePath
        fullInventoryRestored = $true; currentReady = $true; actualInstalledNativeProof = $true; samePackageInstall = [bool]$SamePackageInstall })
    Save-Evidence
}

function Invoke-ActualServicingComponent {
    $script:stage = 'actual-recovery-and-interleaving'
    Assert-DataUnchanged $before
    $executable = (Resolve-Path -LiteralPath $AcceptanceExecutable).Path
    $loadedSetup = Join-Path ([IO.Path]::GetDirectoryName($executable)) 'Hermes.Setup.dll'
    $distributedSetup = Join-Path ([IO.Path]::GetDirectoryName($SetupExecutable)) 'Hermes.Setup.dll'
    $setupHash = (Get-FileHash -LiteralPath $distributedSetup -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($path in @($loadedSetup, (Join-Path $ProbeOldDirectory 'Hermes.Setup.dll'), (Join-Path $ProbeNewDirectory 'Hermes.Setup.dll'))) {
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $setupHash) { throw 'Acceptance/probe loaded Setup assembly differs from the distributed coordinator.' }
    }
    $inputs = foreach ($path in @($packages['0.1.0'].path, $packages['0.1.1'].path, $SetupExecutable, $GuardPath,
        (Join-Path $ProbeOldDirectory 'Hermes.Service.dll'), (Join-Path $ProbeNewDirectory 'Hermes.Service.dll'), $loadedSetup)) {
        [ordered]@{ path = [IO.Path]::GetFullPath($path); sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $baseline = $before | ConvertFrom-Json -AsHashtable
    $owner = foreach ($path in $baseline.Keys) {
        [ordered]@{ path = $path; sha256 = $baseline[$path].ToLowerInvariant(); length = (Get-Item -LiteralPath $path).Length }
    }
    $script:componentExpectation = [ordered]@{
        ownerFixtureId = $ownerFixtureId
        job = [ordered]@{ runId = $env:GITHUB_RUN_ID; attempt = $env:GITHUB_RUN_ATTEMPT; job = $env:GITHUB_JOB }
        inputs = @($inputs); ownerUnknown = @($owner)
    }
    Save-Evidence
    $work = Join-Path ([IO.Path]::GetFullPath($env:RUNNER_TEMP)) ('hermes-a2-component-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $work) { throw 'Actual component workspace must not preexist.' }
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($executable)
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @('run', '--old-msi', $packages['0.1.0'].path, '--new-msi', $packages['0.1.1'].path,
        '--setup', $SetupExecutable, '--guard', $GuardPath, '--probe-old', [IO.Path]::GetFullPath($ProbeOldDirectory),
        '--probe-new', [IO.Path]::GetFullPath($ProbeNewDirectory), '--owner-fixture-receipt', $ownerFixtureReceiptPath,
        '--archive', $archive, '--work', $work)) { $start.ArgumentList.Add($argument) }
    $start.Environment['PATH'] = Join-Path $env:SystemRoot 'System32'
    $start.Environment['DOTNET_ROOT'] = Join-Path $root 'component-absent-sdk'
    $start.Environment['DOTNET_ROOT_X64'] = $start.Environment['DOTNET_ROOT']
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $start
    $publisher = $null
    try {
        $publisher = Start-HermesNativeEvidence -Directory (Join-Path $nativeRoot 'native-event-provider')
        if (-not $child.Start()) { throw 'Actual servicing component did not start.' }
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        $child.WaitForExit()
        $null = $stdout.GetAwaiter().GetResult()
        $null = $stderr.GetAwaiter().GetResult()
        $script:componentExitCode = $child.ExitCode
    } finally {
        $child.Dispose()
        if ($null -ne $publisher) { Stop-HermesNativeEvidence $publisher }
    }
    $reportPath = Join-Path $work 'actual-servicing.json'
    $evidence.Add([ordered]@{ action = $stage; exitCode = $componentExitCode; sdkPathAbsent = $true })
    Save-Evidence
    if (-not (Test-Path -LiteralPath $reportPath -PathType Leaf) -or (Get-Item -LiteralPath $reportPath).Length -gt 32MB) {
        throw 'Actual component final report is absent or exceeds its strict bound.'
    }
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -Depth 100
    Assert-HermesComponentEnvelope $report $componentExpectation
    Copy-Item -LiteralPath $reportPath -Destination (Join-Path $destination 'actual-servicing.json')
    Save-Evidence
    Assert-HermesServicingComponentReport $report $componentExpectation $componentExitCode
    Assert-DataUnchanged $before
    if (@(Get-HermesRelatedProducts).Count -ne 0 -or (Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $shortcut) `
        -or (Get-ItemProperty -LiteralPath $runKey -Name Hermes -ErrorAction SilentlyContinue)) {
        throw 'Actual component cleanup left product/shortcut/Run state behind.'
    }
    foreach ($package in $packages.Values) {
        foreach ($file in $package.inventory.files) {
            if (Test-Path -LiteralPath (Join-Path $install $file.path)) { throw 'Actual component cleanup left owned payload behind.' }
        }
    }
    $evidence.Add([ordered]@{ action = $stage; componentPassed = $true; componentComplete = $true; cases = @($report.cases.name) })
    Save-Evidence
}

try {
    foreach ($candidate in @(
        @{ version = '0.1.0'; payload = $PayloadDirectory; inventory = $InventoryDirectory },
        @{ version = '0.1.1'; payload = $UpgradePayloadDirectory; inventory = $UpgradeInventoryDirectory }
    )) {
        $directory = Join-Path $root $candidate.version
        & (Join-Path $PSScriptRoot 'Build-Package.ps1') -PayloadDirectory $candidate.payload `
            -InventoryDirectory $candidate.inventory -PackageVersion $candidate.version -OutputDirectory $directory `
            -GuardPath $GuardPath -ExpectedGuardSha256 $GuardSha256 -ServicingBuildPath $ServicingBuildPath | Out-Host
        $path = Join-Path $directory 'Hermes.Installer.msi'
        $identity = Get-HermesMsiIdentity $path
        [void]$ownedProducts.Add($identity.ProductCode)
        $packages[$candidate.version] = @{
            path = $path; product = $identity.ProductCode; version = $candidate.version
            inventory = Get-Content -LiteralPath (Join-Path $directory 'payload-inventory.json') -Raw | ConvertFrom-Json
        }
    }
    foreach ($requiredChange in @('service/Hermes.Service.dll', 'tray/Hermes.Tray.dll')) {
        $old = $packages['0.1.0'].inventory.files | Where-Object path -CEQ $requiredChange
        $new = $packages['0.1.1'].inventory.files | Where-Object path -CEQ $requiredChange
        if ($old.sha256 -ceq $new.sha256) { throw "Upgrade must change actual runtime payload bytes: $requiredChange" }
    }
    if ($packages['0.1.0'].product -eq $packages['0.1.1'].product) { throw 'Major upgrade ProductCodes must differ.' }
    $stage = 'synthetic-state'
    foreach ($directory in @($archive, (Join-Path $install 'user-data'), (Join-Path $install 'service'), (Join-Path $install 'tray/user-data'))) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    & $SmokeExecutable --create-owner-fixture $ownerFixtureReceiptPath --archive-directory $archive | Out-Host
    $fixtureExitCode = $LASTEXITCODE
    $evidence.Add([ordered]@{ stage = 'owner-fixture-creation'; exitCode = $fixtureExitCode })
    Save-Evidence
    if ($fixtureExitCode -ne 0) { throw 'The native helper refused creation of the synthetic owner fixture.' }
    if (-not (Test-Path -LiteralPath $ownerFixtureReceiptPath -PathType Leaf) `
        -or (Get-Item -LiteralPath $ownerFixtureReceiptPath).Length -gt 8KB) {
        throw 'The owner-fixture creation receipt is missing or exceeds its bound.'
    }
    $ownerFixtureReceipt = Get-Content -LiteralPath $ownerFixtureReceiptPath -Raw | ConvertFrom-Json
    $ownerFixtureId = ([Guid]::Parse($ownerFixtureReceipt.fixtureId)).ToString('N')
    if ($ownerFixtureReceipt.schema -ne 1 -or $ownerFixtureReceipt.kind -cne 'hermes-installer-owner-fixture' `
        -or $ownerFixtureId -eq ([Guid]::Empty).ToString('N') `
        -or $ownerFixtureReceipt.job.runId -cne $env:GITHUB_RUN_ID `
        -or $ownerFixtureReceipt.job.attempt -cne $env:GITHUB_RUN_ATTEMPT `
        -or $ownerFixtureReceipt.job.job -cne $env:GITHUB_JOB `
        -or (Get-FileHash -LiteralPath (Join-Path $config 'config.yaml') -Algorithm SHA256).Hash -ne $ownerFixtureReceipt.configSha256 `
        -or (Get-FileHash -LiteralPath (Join-Path $config 'synthetic-settings.txt') -Algorithm SHA256).Hash -ne $ownerFixtureReceipt.settingsSha256) {
        throw 'The native owner fixture does not match its job-bound creation receipt.'
    }
    [IO.File]::WriteAllText((Join-Path $archive 'source.txt'), 'SYNTHETIC retained archive source')
    foreach ($file in @('user-data/keep.txt', 'service/unknown-user-file.txt', 'tray/user-data/keep.txt')) {
        [IO.File]::WriteAllText((Join-Path $install $file), 'SYNTHETIC unowned application file')
    }
    $before = Get-SyntheticHashes
    foreach ($step in @(
        @{ name = 'install'; version = '0.1.0'; operation = 'install' },
        @{ name = 'repair'; version = '0.1.0'; operation = 'repair' },
        @{ name = 'upgrade'; version = '0.1.1'; operation = 'install' },
        @{ name = 'rollback'; version = '0.1.0'; operation = 'install' }
    )) {
        $stage = $step.name
        $package = $packages[$step.version]
        if ($stage -eq 'rollback') {
            $null = Invoke-Coordinator 'install' 'implicit-downgrade-refused' $package.path -ExpectedExit 1603
            Assert-Installed $packages['0.1.1']
        }
        $source = if ($step.operation -eq 'install') { $package.path } else { '' }
        $null = Invoke-Coordinator $step.operation $stage $source -Rollback:($stage -eq 'rollback')
        Assert-Installed $package
        Assert-DataUnchanged $before
        $recovery = Invoke-Coordinator 'recover' "$stage-recover-without-interruption" -ExpectedExit 1603
        if ($recovery.diagnosticCodes -cnotcontains 'NoRecoveryPending') { throw 'Recover without an interrupted transaction must refuse truthfully.' }
        $probe = if ($step.version -eq '0.1.1') { $packages['0.1.0'] } else { $packages['0.1.1'] }
        Assert-RawMsiRefused '/i' $probe.path "$stage-stopped-raw-install"
        Assert-RawMsiRefused '/fa' $package.product "$stage-stopped-raw-repair"
        Assert-RawMsiRefused '/x' $package.product "$stage-stopped-raw-uninstall"
        Assert-RawMsiRefused '/a' $probe.path "$stage-raw-administrative"
        Assert-RawMsiRefused '/ju' $probe.path "$stage-raw-advertisement"
        Assert-Installed $package
        Assert-DataUnchanged $before
        Invoke-InstalledRuntime $stage $package $probe
        Assert-Installed $package
        Assert-DataUnchanged $before
        if ($step.name -eq 'repair') {
            Invoke-CorrectiveRepair -Damage missing -RelativePath 'service/Hermes.Service.dll' -Package $package -UpgradePackage $probe
            Invoke-CorrectiveRepair -Damage changed -RelativePath 'service/wwwroot/index.html' -Package $package -UpgradePackage $probe -SamePackageInstall
        }
    }
    $stage = 'uninstall'
    $null = Invoke-Coordinator 'uninstall' 'uninstall'
    Assert-DataUnchanged $before
    if (@(Get-HermesRelatedProducts).Count -ne 0 -or (Test-Path -LiteralPath $registration) `
        -or (Test-Path -LiteralPath $shortcut) `
        -or (Get-ItemProperty -LiteralPath $runKey -Name Hermes -ErrorAction SilentlyContinue)) {
        throw 'Owned product, shortcut or Run registration remains after uninstall.'
    }
    foreach ($package in $packages.Values) {
        foreach ($file in $package.inventory.files) {
            if (Test-Path -LiteralPath (Join-Path $install $file.path)) { throw 'Owned runtime file remains after uninstall.' }
        }
    }
    $evidence.Add([ordered]@{ stage = 'uninstall'; dataHashesUnchanged = $true; registrationRemoved = $true; runtimeRemoved = $true })
    Invoke-ActualServicingComponent
    $completed = $true
} catch {
    # Persist the original error and already-written native reports before any cleanup attempt.
    if ($null -eq $failure) {
        $failure = [ordered]@{ stage = $stage; type = $_.Exception.GetType().FullName; message = $_.Exception.Message }
    }
    Save-Evidence
} finally {
    Save-Evidence
    if (-not $completed) {
        try {
            $remaining = @(Get-HermesRelatedProducts)
            foreach ($product in $remaining) {
                if (-not $ownedProducts.Contains($product)) { throw 'Unowned related product detected; cleanup refused.' }
            }
            if ($remaining.Count -gt 0 -or ($null -ne $lastCoordinator -and ($null -eq $lastCoordinator.report -or -not $lastCoordinator.report.ready))) {
                $recovered = Invoke-Coordinator 'recover' 'failure-recover' -AcceptedExits @(0, 1603)
                if ($recovered.exitCode -eq 1603 -and $recovered.diagnosticCodes -cnotcontains 'NoRecoveryPending') {
                    throw 'Coordinator cleanup recovery did not establish a safe completed state.'
                }
                $remaining = @(Get-HermesRelatedProducts)
                if (@($remaining | Where-Object { -not $ownedProducts.Contains($_) }).Count -ne 0) { throw 'Unowned product appeared during cleanup; removal refused.' }
                if ($remaining.Count -gt 0) { $null = Invoke-Coordinator 'uninstall' 'failure-uninstall' }
                if ($null -ne $before) { Assert-DataUnchanged $before }
            }
        } catch { $cleanupFailures.Add($_.Exception.Message) }
    }
    Save-Evidence
}
if (-not $completed) { throw "Installer acceptance failed during $stage; original/native evidence persisted before cleanup." }
Write-Output 'Ordinary installed native lifecycle and the separate actual servicing/recovery component both passed; target Windows evidence is required.'
