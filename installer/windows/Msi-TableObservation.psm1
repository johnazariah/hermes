#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Literal supplied by the language author, bound to both immutable source blobs.
# A mismatch is a stop, not permission to derive or merge a different allowlist.
$script:tableSourceCommit = '5b06889943baf695492c651725822be7c336f352'
$script:tableSourceHashes = [ordered]@{
    'installer/windows/Hermes.Setup/PackageAuthoringValidator.cs' = '918c41734bc05dcf58b537c51f8cdb696a2fc8f8b1ef9efc996cefb622c6ee39'
    'installer/windows/Hermes.Setup/PackageAuthoringSnapshot.cs' = '4373ad8e6cc1622054f5842b6dfdee90dc69960114c026be29f0fcece9e199c6'
}
$script:allowedTableNames = @(
    'AdminExecuteSequence', 'AdminUISequence', 'AdvtExecuteSequence', 'AppSearch', 'Binary',
    'Component', 'Condition', 'CreateFolder', 'CustomAction', 'Directory', 'Error', 'Feature',
    'FeatureComponents', 'File', 'Icon', 'InstallExecuteSequence', 'InstallUISequence',
    'LaunchCondition', 'Media', 'MsiDigitalCertificate', 'MsiDigitalSignature', 'MsiFileHash',
    'Property', 'RegLocator', 'Registry', 'RemoveFile', 'RemoveRegistry', 'Shortcut', 'Upgrade', '_Validation'
)

function Get-HermesTableSourceBinding([string]$RepositoryRoot) {
    $matches = $true
    foreach ($entry in $script:tableSourceHashes.GetEnumerator()) {
        $path = Join-Path $RepositoryRoot $entry.Key
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) `
            -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.Value) {
            $matches = $false
        }
    }
    [ordered]@{ commit = $script:tableSourceCommit; files = $script:tableSourceHashes; matches = $matches }
}

function Get-HermesTablePredicate([AllowEmptyCollection()][object[]]$Names) {
    $observed = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $allowed = [Collections.Generic.HashSet[string]]::new([string[]]$script:allowedTableNames, [StringComparer]::Ordinal)
    if ($Names.Count -gt 1024) { throw 'TableObservationRowBound' }
    foreach ($name in $Names) {
        if ($name -isnot [string] -or $name -cnotmatch '\A[A-Za-z_][A-Za-z0-9_.]{0,71}\z') {
            throw 'TableObservationNameBound'
        }
        [void]$observed.Add($name)
    }
    [string[]]$unmatched = @($observed | Where-Object { -not $allowed.Contains($_) })
    [Array]::Sort($unmatched, [StringComparer]::Ordinal)
    $countValid = $observed.Count -ge 1 -and $observed.Count -le 128
    [ordered]@{
        observedCount = $observed.Count; countValid = $countValid
        unmatchedNames = $unmatched; predicate = $countValid -and $unmatched.Count -eq 0
    }
}

function Get-HermesTableReceiptHash($Receipt, $Identity) {
    if ($Receipt.schemaVersion -ne 1 -or $Receipt.nativeExitCode -ne 0 `
        -or $Receipt.version -cne '0.1.0' -or $Identity.version -cne '0.1.0' `
        -or $Receipt.packageSha256 -isnot [string] -or $Receipt.packageSha256 -cnotmatch '\A[0-9a-f]{64}\z' `
        -or [Guid]$Receipt.productCode -eq [Guid]::Empty -or [Guid]$Receipt.packageCode -eq [Guid]::Empty `
        -or [Guid]$Receipt.productCode -ne [Guid]$Identity.productCode `
        -or [Guid]$Receipt.packageCode -ne [Guid]$Identity.packageCode) { throw 'TableObservationReceiptBinding' }
    $Receipt.packageSha256
}

function New-HermesTableInstaller { New-Object -ComObject WindowsInstaller.Installer }

function Get-HermesTableRecordName($Record) {
    $Record.GetType().InvokeMember('StringData', [Reflection.BindingFlags]::GetProperty, $null, $Record, @(1))
}

function Close-HermesTableCom($Item) {
    if ($null -ne $Item -and [Runtime.InteropServices.Marshal]::IsComObject($Item)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Item)
    }
}

function Read-HermesNativeTableNames([string]$PackagePath) {
    $installer = $null; $database = $null; $view = $null
    $names = [Collections.Generic.List[object]]::new()
    $phase = 'CreateInstaller'
    try {
        $installer = New-HermesTableInstaller
        $phase = 'OpenDatabase'
        $database = $installer.OpenDatabase($PackagePath, 0)
        $phase = 'OpenView'
        $view = $database.OpenView('SELECT `Name` FROM `_Tables`')
        $phase = 'Execute'
        [void]$view.Execute()
        $phase = 'Fetch'
        while ($null -ne ($record = $view.Fetch())) {
            try {
                if ($names.Count -ge 1024) { throw 'TableObservationRowBound' }
                $names.Add((Get-HermesTableRecordName $record))
            } finally { Close-HermesTableCom $record }
        }
        $phase = 'CloseView'
        [void]$view.Close()
        # Validate names before they can enter the bounded evidence surface.
        $phase = 'Parse'
        $predicate = Get-HermesTablePredicate $names.ToArray()
        [ordered]@{ querySuccess = $true; nativeQueryFailureCode = $null; nativeQueryFailureKind = $null; facts = $predicate }
    } catch {
        $errorObject = $_.Exception
        while ($null -ne $errorObject.InnerException -and $errorObject -isnot [Runtime.InteropServices.COMException]) {
            $errorObject = $errorObject.InnerException
        }
        $code = if ($errorObject -is [Runtime.InteropServices.COMException]) { $errorObject.HResult } else { $null }
        $kind = if ($null -ne $code) { "$phase/COMException" } else { "$phase/ObservationFailure" }
        [ordered]@{ querySuccess = $false; nativeQueryFailureCode = $code; nativeQueryFailureKind = $kind; facts = $null }
    } finally {
        foreach ($item in @($view, $database, $installer)) { Close-HermesTableCom $item }
    }
}

function Invoke-HermesPublishedTableObservation {
    param(
        [Parameter(Mandatory = $true)][string]$PublishedDirectory,
        [Parameter(Mandatory = $true)][string]$RepositoryRoot
    )
    if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_OS -cne 'Windows' `
        -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:GITHUB_SERVER_URL -cne 'https://github.com' `
        -or $env:GITHUB_REPOSITORY -cne 'johnazariah/hermes' -or $env:GITHUB_JOB -cne 'installer') {
        throw 'Table observation requires the dedicated public GitHub-hosted Windows installer job.'
    }
    Invoke-HermesBoundTableObservation $PublishedDirectory $RepositoryRoot
}

function Invoke-HermesBoundTableObservation([string]$PublishedDirectory, [string]$RepositoryRoot) {
    $result = [ordered]@{
        sourceCommitBinding = [ordered]@{ commit = $script:tableSourceCommit; files = $script:tableSourceHashes; matches = $false }
        packageHashMatch = $false; querySuccess = $false
        nativeQueryFailureCode = $null; nativeQueryFailureKind = $null
        observedCount = $null; countValid = $false; unmatchedNames = @(); predicate = $false
    }
    $leases = [Collections.Generic.List[IO.FileStream]]::new()
    try {
        $package = Join-Path $PublishedDirectory 'Hermes.Installer.msi'
        $receiptPath = Join-Path $PublishedDirectory 'generated/ice-validation.json'
        $identityPath = Join-Path $PublishedDirectory 'generated/build-identity.json'
        foreach ($path in @($script:tableSourceHashes.Keys | ForEach-Object { Join-Path $RepositoryRoot $_ })) {
            $leases.Add([IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        }
        $result.sourceCommitBinding = Get-HermesTableSourceBinding $RepositoryRoot
        if (-not $result.sourceCommitBinding.matches) { return $result }
        foreach ($path in @($package, $receiptPath, $identityPath)) {
            $leases.Add([IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read))
        }
        if ((Get-Item -LiteralPath $receiptPath).Length -gt 16KB -or (Get-Item -LiteralPath $identityPath).Length -gt 4KB) { return $result }
        $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
        $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
        $expected = Get-HermesTableReceiptHash $receipt $identity
        $result.packageHashMatch = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expected
        if (-not $result.packageHashMatch) { return $result }
        $observation = Read-HermesNativeTableNames $package
        $result.querySuccess = $observation.querySuccess
        $result.nativeQueryFailureCode = $observation.nativeQueryFailureCode
        $result.nativeQueryFailureKind = $observation.nativeQueryFailureKind
        $result.sourceCommitBinding = Get-HermesTableSourceBinding $RepositoryRoot
        $result.packageHashMatch = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expected
        if ($result.sourceCommitBinding.matches -and $result.packageHashMatch -and $observation.querySuccess) {
            foreach ($key in @('observedCount', 'countValid', 'unmatchedNames', 'predicate')) { $result[$key] = $observation.facts[$key] }
        }
    } catch {
        # Binding/I/O failures must never disclose paths, receipt content or exception messages.
        $result.packageHashMatch = $false
        $result.predicate = $false
    } finally { foreach ($lease in $leases) { $lease.Dispose() } }
    $result
}

Export-ModuleMember -Function Invoke-HermesPublishedTableObservation
