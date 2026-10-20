#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$module = Import-Module (Join-Path $PSScriptRoot 'Msi-TableObservation.psm1') -Force -PassThru
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $PSScriptRoot ('obj/table-observation-controls-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory((Join-Path $fixture 'generated'))
try {
    & $module {
        param($repository, $fixture)
        function Require([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
        function Refuse([scriptblock]$Action) {
            $refused = $false
            try { & $Action | Out-Null } catch { $refused = $true }
            Require $refused 'Invalid input was not refused.'
        }
        Require ((Get-HermesTableSourceBinding $repository).matches) 'Immutable source hashes differ.'
        Require (-not (Get-HermesTableSourceBinding $fixture).matches) 'Missing source must refuse.'
        $script:empty = $true
        $script:failurePhase = ''
        $script:opened = 0
        $script:released = 0
        $script:releasedRecords = 0
        $script:fetches = 0
        $script:queries = [Collections.Generic.List[string]]::new()
        $script:mode = -1
        $script:fakeRecord = [pscustomobject]@{}
        $script:fakeRecord | Add-Member ScriptProperty StringData { throw 'PRIVATE row identifier must never be read' }
        $script:fakeRecord | Add-Member ScriptMethod ToString { throw 'PRIVATE row contents must never be converted' } -Force
        function script:New-HermesTableInstaller {
            $script:opened++
            if ($script:failurePhase -ceq 'CreateInstaller') {
                throw [Runtime.InteropServices.COMException]::new('PRIVATE COM creation', -2147467259)
            }
            $installer = [pscustomobject]@{}
            $installer | Add-Member ScriptMethod OpenDatabase {
                param($path, $mode)
                $script:mode = $mode
                if ($script:failurePhase -ceq 'OpenDatabase') {
                    throw [Runtime.InteropServices.COMException]::new('PRIVATE owner path and receipt', -2147467259)
                }
                $database = [pscustomobject]@{}
                $database | Add-Member ScriptMethod OpenView {
                    param($query)
                    $script:queries.Add($query)
                    if ($script:failurePhase -ceq 'OpenView') {
                        throw [Runtime.InteropServices.COMException]::new('PRIVATE query text', -2147467259)
                    }
                    $view = [pscustomobject]@{}
                    $view | Add-Member ScriptMethod Execute {
                        if ($script:failurePhase -ceq 'Execute') {
                            throw [Runtime.InteropServices.COMException]::new('PRIVATE native payload', -2147352567)
                        }
                    }
                    $view | Add-Member ScriptMethod Fetch {
                        $script:fetches++
                        if ($script:fetches -ne 1) { throw 'A second fetch is forbidden.' }
                        if ($script:failurePhase -ceq 'Fetch') {
                            throw [Runtime.InteropServices.COMException]::new('PRIVATE fetch details', -2147467259)
                        }
                        if ($script:failurePhase -ceq 'FetchNoMoreItemsHResult') {
                            throw [Runtime.InteropServices.COMException]::new('PRIVATE fetch HRESULT', -2147024637)
                        }
                        if ($script:failurePhase -ceq 'NonComFetch') { throw 'PRIVATE non-COM details' }
                        if ($script:empty) { return $null }
                        return $script:fakeRecord
                    }
                    $view | Add-Member ScriptMethod Close {
                        if ($script:failurePhase -ceq 'CloseView') {
                            throw [Runtime.InteropServices.COMException]::new('PRIVATE close details', -2147467259)
                        }
                    }
                    $view
                }
                $database
            }
            $installer
        }
        function script:Close-HermesTableCom($Item) {
            if ($null -ne $Item) { $script:released++ }
            if ([Object]::ReferenceEquals($Item, $script:fakeRecord)) { $script:releasedRecords++ }
        }
        foreach ($empty in @($true, $false)) {
            $script:empty = $empty
            $script:fetches = 0
            $script:queries.Clear()
            $script:released = 0
            $script:releasedRecords = 0
            $native = Read-HermesNativeSignatureEmpty 'SYNTHETIC not an MSI'
            Require ($native.querySuccess -and $native.fetchSuccess -and $native.isEmpty -eq $empty `
                -and $null -eq $native.nativeQueryFailureCode -and $null -eq $native.nativeQueryFailureKind) 'Null COM record means empty; a record means nonempty.'
            Require ($script:mode -eq 0 -and $script:queries.Count -eq 1 -and $script:queries[0] -ceq 'SELECT `Signature` FROM `Signature`' `
                -and $script:fetches -eq 1) 'Use exactly the selected read-only query and one fetch.'
            $expectedReleases = if ($empty) { 3 } else { 4 }
            Require ($script:released -eq $expectedReleases -and $script:releasedRecords -eq [int](-not $empty)) 'Release every COM handle, including an unread returned record.'
            Require (($native | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|SYNTHETIC|StringData') 'No record contents or input paths may escape.'
        }
        foreach ($phase in @('CreateInstaller', 'OpenDatabase', 'OpenView', 'Execute', 'Fetch', 'CloseView', 'NonComFetch', 'FetchNoMoreItemsHResult')) {
            $script:fetches = 0
            $script:failurePhase = $phase
            $script:releasedRecords = 0
            $native = Read-HermesNativeSignatureEmpty 'SYNTHETIC not an MSI'
            Require ($null -eq $native.isEmpty) 'Every failed observation leaves emptiness unknown.'
            $querySucceeded = $phase -cin @('Fetch', 'CloseView', 'NonComFetch', 'FetchNoMoreItemsHResult')
            Require ($native.querySuccess -eq $querySucceeded -and $native.fetchSuccess -eq ($phase -ceq 'CloseView')) 'Preserve query and fetch status independently.'
            if ($phase -ceq 'NonComFetch') {
                Require ($null -eq $native.nativeQueryFailureCode -and $native.nativeQueryFailureKind -ceq 'Fetch/ObservationFailure') 'Never invent a native HRESULT for non-COM failure.'
            } else {
                $expectedCode = switch ($phase) { Execute { -2147352567 }; FetchNoMoreItemsHResult { -2147024637 }; default { -2147467259 } }
                $failurePhase = if ($phase -ceq 'FetchNoMoreItemsHResult') { 'Fetch' } else { $phase }
                Require ($native.nativeQueryFailureCode -eq $expectedCode -and $native.nativeQueryFailureKind -ceq "$failurePhase/HResult") 'Retain the actual COM HRESULT and failure phase; only a null record, never an exception, means empty.'
            }
            if ($phase -ceq 'CloseView') { Require ($script:releasedRecords -eq 1) 'Release the unread record even when closing fails.' }
            Require (($native | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|SYNTHETIC') 'Native messages and paths must not escape.'
        }
        $script:failurePhase = ''
        $script:empty = $true
        $script:fetches = 0
        [IO.File]::WriteAllText((Join-Path $fixture 'Hermes.Installer.msi'), 'SYNTHETIC inert bytes, never opened by COM')
        $expected = (Get-FileHash -LiteralPath (Join-Path $fixture 'Hermes.Installer.msi') -Algorithm SHA256).Hash.ToLowerInvariant()
        $receipt = @{
            schemaVersion = 1; nativeExitCode = 0; version = '0.1.0'; packageSha256 = $expected
            productCode = '{11111111-1111-1111-1111-111111111111}'
            packageCode = '{22222222-2222-2222-2222-222222222222}'
        }
        $identity = [ordered]@{ version = '0.1.0'; productCode = $receipt.productCode; packageCode = $receipt.packageCode }
        $receiptPath = Join-Path $fixture 'generated/ice-validation.json'
        $identityPath = Join-Path $fixture 'generated/build-identity.json'
        $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath
        $identity | ConvertTo-Json | Set-Content -LiteralPath $identityPath
        foreach ($empty in @($true, $false)) {
            $script:empty = $empty
            $script:fetches = 0
            $bound = Invoke-HermesBoundTableObservation $fixture $repository
            Require ($bound.sourceCommitBinding.matches -and $bound.packageHashMatch -and $bound.querySuccess `
                -and $bound.fetchSuccess -and $bound.isEmpty -eq $empty) 'Bindings must retain both possible fetch bits.'
            Require ((Get-FileHash -LiteralPath (Join-Path $fixture 'Hermes.Installer.msi') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expected) 'Retain the package bytes.'
            Require (($bound.Keys -join '|') -ceq 'sourceCommitBinding|packageHashMatch|querySuccess|fetchSuccess|nativeQueryFailureCode|nativeQueryFailureKind|isEmpty') 'Only approved binding, status and emptiness fields may be emitted.'
            $json = $bound | ConvertTo-Json -Depth 8
            Require ([Text.Encoding]::UTF8.GetByteCount($json) -le 128KB -and $json -cnotmatch 'SYNTHETIC|obj/|PRIVATE|productCode|packageCode|StringData') 'Keep output bounded and private.'
        }
        $opened = $script:opened
        $mismatch = Invoke-HermesBoundTableObservation $fixture $fixture
        Require (-not $mismatch.sourceCommitBinding.matches -and -not $mismatch.querySuccess -and -not $mismatch.fetchSuccess `
            -and $null -eq $mismatch.isEmpty -and $script:opened -eq $opened) 'Missing source must stop before querying.'
        function script:Get-FileHash {
            param([string]$LiteralPath, [string]$Algorithm)
            [pscustomobject]@{ Hash = '0' * 64 }
        }
        try {
            $mismatch = Invoke-HermesBoundTableObservation $fixture $repository
            Require (-not $mismatch.sourceCommitBinding.matches -and -not $mismatch.querySuccess `
                -and $null -eq $mismatch.isEmpty -and $script:opened -eq $opened) 'Different source hashes must stop before querying.'
        } finally { Remove-Item Function:Get-FileHash }
        foreach ($change in @('hash', 'identity', 'version', 'native-exit', 'receipt-size', 'invalid-json')) {
            $changed = $receipt.Clone()
            switch ($change) {
                hash { $changed.packageSha256 = '0' * 64 }
                identity { $changed.packageCode = '{33333333-3333-3333-3333-333333333333}' }
                version { $changed.version = '0.1.1' }
                native-exit { $changed.nativeExitCode = 1603 }
            }
            $text = $changed | ConvertTo-Json
            if ($change -ceq 'receipt-size') { $text = 'PRIVATE' * 3000 }
            if ($change -ceq 'invalid-json') { $text = '{"PRIVATE owner-path":' }
            [IO.File]::WriteAllText($receiptPath, $text)
            $mismatch = Invoke-HermesBoundTableObservation $fixture $repository
            Require (-not $mismatch.packageHashMatch -and -not $mismatch.querySuccess -and -not $mismatch.fetchSuccess `
                -and $null -eq $mismatch.isEmpty -and $script:opened -eq $opened) 'Receipt mismatch must stop without inventing emptiness.'
            Require (($mismatch | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|owner-path|SYNTHETIC') 'Binding failures must not leak input.'
        }
        $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath
        [IO.File]::WriteAllText((Join-Path $fixture 'Hermes.Installer.msi'), 'PRIVATE changed candidate bytes')
        $mismatch = Invoke-HermesBoundTableObservation $fixture $repository
        Require (-not $mismatch.packageHashMatch -and -not $mismatch.querySuccess -and $null -eq $mismatch.isEmpty `
            -and $script:opened -eq $opened) 'Changed MSI bytes cannot supply their own expected hash.'
        Require (($mismatch | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|candidate bytes') 'Changed MSI bytes must not leak.'
        [IO.File]::WriteAllText((Join-Path $fixture 'Hermes.Installer.msi'), 'SYNTHETIC inert bytes, never opened by COM')
        $script:failurePhase = 'Fetch'
        $script:fetches = 0
        $bound = Invoke-HermesBoundTableObservation $fixture $repository
        Require ($bound.sourceCommitBinding.matches -and $bound.packageHashMatch -and $bound.querySuccess -and -not $bound.fetchSuccess `
            -and $null -eq $bound.isEmpty -and $bound.nativeQueryFailureCode -eq -2147467259 `
            -and $bound.nativeQueryFailureKind -ceq 'Fetch/HResult') 'The final envelope must preserve a native fetch failure as unknown, not empty.'
        if (-not $IsWindows) { Refuse { Invoke-HermesPublishedTableObservation $fixture $repository } }
    } $repository $fixture
} finally {
    Remove-Module $module.Name -Force
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
Write-Output 'Source/package-bound Signature empty/nonempty/failure/privacy controls passed; synthetic fakes only, no COM, MSI, install or profile action.'
