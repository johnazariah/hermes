#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Preserve the original observation's immutable source binding, not a new contract.
# This temporary fetch bit neither changes an allowlist nor establishes a failure origin.
$script:tableSourceCommit = '5b06889943baf695492c651725822be7c336f352'
$script:tableSourceHashes = [ordered]@{
    'installer/windows/Hermes.Setup/PackageAuthoringValidator.cs' = '918c41734bc05dcf58b537c51f8cdb696a2fc8f8b1ef9efc996cefb622c6ee39'
    'installer/windows/Hermes.Setup/PackageAuthoringSnapshot.cs' = '4373ad8e6cc1622054f5842b6dfdee90dc69960114c026be29f0fcece9e199c6'
}

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

function Close-HermesTableCom($Item) {
    if ($null -ne $Item -and [Runtime.InteropServices.Marshal]::IsComObject($Item)) {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($Item)
    }
}

function Read-HermesNativeSignatureEmpty([string]$PackagePath) {
    $installer = $null; $database = $null; $view = $null; $record = $null
    $result = [ordered]@{
        querySuccess = $false; fetchSuccess = $false
        nativeQueryFailureCode = $null; nativeQueryFailureKind = $null; isEmpty = $null
    }
    $phase = 'CreateInstaller'
    try {
        $installer = New-HermesTableInstaller
        $phase = 'OpenDatabase'
        $database = $installer.OpenDatabase($PackagePath, 0)
        $phase = 'OpenView'
        $view = $database.OpenView('SELECT `Signature` FROM `Signature`')
        $phase = 'Execute'
        [void]$view.Execute()
        $result.querySuccess = $true
        $phase = 'Fetch'
        # COM Fetch returns null for no rows (the native API uses ERROR_NO_MORE_ITEMS).
        # Never read, convert, enumerate or emit a returned record.
        $record = $view.Fetch()
        $result.fetchSuccess = $true
        $phase = 'CloseView'
        [void]$view.Close()
        $result.isEmpty = $null -eq $record
    } catch {
        $errorObject = $_.Exception
        while ($null -ne $errorObject.InnerException -and $errorObject -isnot [Runtime.InteropServices.COMException]) {
            $errorObject = $errorObject.InnerException
        }
        $code = if ($errorObject -is [Runtime.InteropServices.COMException]) { $errorObject.HResult } else { $null }
        $result.nativeQueryFailureCode = $code
        $result.nativeQueryFailureKind = if ($null -ne $code) { "$phase/HResult" } else { "$phase/ObservationFailure" }
    } finally {
        foreach ($item in @($record, $view, $database, $installer)) { Close-HermesTableCom $item }
    }
    $result
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
        packageHashMatch = $false; querySuccess = $false; fetchSuccess = $false
        nativeQueryFailureCode = $null; nativeQueryFailureKind = $null
        isEmpty = $null
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
        $observation = Read-HermesNativeSignatureEmpty $package
        $result.querySuccess = $observation.querySuccess
        $result.fetchSuccess = $observation.fetchSuccess
        $result.nativeQueryFailureCode = $observation.nativeQueryFailureCode
        $result.nativeQueryFailureKind = $observation.nativeQueryFailureKind
        $result.sourceCommitBinding = Get-HermesTableSourceBinding $RepositoryRoot
        $result.packageHashMatch = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expected
        if ($result.sourceCommitBinding.matches -and $result.packageHashMatch -and $observation.querySuccess `
            -and $observation.fetchSuccess -and $null -eq $observation.nativeQueryFailureKind) {
            $result.isEmpty = $observation.isEmpty
        }
    } catch {
        # Binding/I/O failures must never disclose paths, receipt content or exception messages.
        $result.packageHashMatch = $false
        $result.isEmpty = $null
    } finally { foreach ($lease in $leases) { $lease.Dispose() } }
    $result
}

Export-ModuleMember -Function Invoke-HermesPublishedTableObservation
