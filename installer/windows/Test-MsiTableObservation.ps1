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
        Require ($script:allowedTableNames.Count -eq 30) 'The authorised literal has exactly 30 keys.'
        $authorised = '["AdminExecuteSequence","AdminUISequence","AdvtExecuteSequence","AppSearch","Binary","Component","Condition","CreateFolder","CustomAction","Directory","Error","Feature","FeatureComponents","File","Icon","InstallExecuteSequence","InstallUISequence","LaunchCondition","Media","MsiDigitalCertificate","MsiDigitalSignature","MsiFileHash","Property","RegLocator","Registry","RemoveFile","RemoveRegistry","Shortcut","Upgrade","_Validation"]'
        Require (($script:allowedTableNames | ConvertTo-Json -Compress) -ceq $authorised) 'Do not expand or normalise the language-authorised literal.'
        Require ((Get-HermesTablePredicate $script:allowedTableNames).predicate) 'Every authorised key must match.'
        $zero = Get-HermesTablePredicate @()
        Require ($zero.observedCount -eq 0 -and -not $zero.countValid -and -not $zero.predicate) 'Zero is outside the count bound.'
        $one = Get-HermesTablePredicate @('File')
        Require ($one.observedCount -eq 1 -and $one.countValid -and $one.predicate) 'A single allowed table is sufficient.'
        $duplicates = Get-HermesTablePredicate @('File', 'File', 'File')
        Require ($duplicates.observedCount -eq 1 -and $duplicates.predicate) 'Count observed unique names, not rows or allowed keys.'
        foreach ($count in @(128, 129)) {
            $names = @(1..$count | ForEach-Object { "Unknown$_" })
            $value = Get-HermesTablePredicate $names
            Require ($value.observedCount -eq $count -and $value.countValid -eq ($count -eq 128) -and -not $value.predicate) 'The upper count bound is inclusive.'
        }
        $case = Get-HermesTablePredicate @('File', 'file', 'FILE', 'file', '_validation', '_Validation', 'Zeta', 'alpha')
        Require ($case.observedCount -eq 7 -and ($case.unmatchedNames -join '|') -ceq 'FILE|Zeta|_validation|alpha|file') 'Membership and unmatched sorting must be ordinal.'
        foreach ($invalid in @(' File', 'File ', '', "File`nSECRET", ('x' * 73))) {
            Refuse { Get-HermesTablePredicate @($invalid) }
        }
        Refuse { Get-HermesTablePredicate @($null) }
        Refuse { Get-HermesTablePredicate @(1..1025 | ForEach-Object { 'File' }) }

        $script:fakeNames = @('File', 'file', 'File')
        $script:failurePhase = ''
        $script:fetchIndex = 0
        $script:opened = 0
        $script:released = 0
        $script:query = ''
        $script:mode = -1
        function script:New-HermesTableInstaller {
            $script:opened++
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
                    $script:query = $query
                    $view = [pscustomobject]@{}
                    $view | Add-Member ScriptMethod Execute {
                        if ($script:failurePhase -ceq 'Execute') {
                            throw [Runtime.InteropServices.COMException]::new('PRIVATE native payload', -2147352567)
                        }
                    }
                    $view | Add-Member ScriptMethod Fetch {
                        if ($script:failurePhase -ceq 'Fetch' -and $script:fetchIndex -ge 1) {
                            throw [Runtime.InteropServices.COMException]::new('PRIVATE after partial rows', -2147467259)
                        }
                        if ($script:fetchIndex -lt $script:fakeNames.Count) {
                            $value = $script:fakeNames[$script:fetchIndex]
                            $script:fetchIndex++
                            return [pscustomobject]@{ name = $value }
                        }
                        return $null
                    }
                    $view | Add-Member ScriptMethod Close {}
                    $view
                }
                $database
            }
            $installer
        }
        function script:Get-HermesTableRecordName($Record) { $Record.name }
        function script:Close-HermesTableCom($Item) { if ($null -ne $Item) { $script:released++ } }
        $native = Read-HermesNativeTableNames 'SYNTHETIC not an MSI'
        Require ($native.querySuccess -and $native.facts.observedCount -eq 2 -and $native.facts.unmatchedNames[0] -ceq 'file') 'The real reader must use the real unique ordinal predicate.'
        Require ($script:mode -eq 0 -and $script:query -ceq 'SELECT `Name` FROM `_Tables`' -and $script:released -eq 6) 'One read-only query must release all handles and records.'
        foreach ($phase in @('OpenDatabase', 'Execute', 'Fetch')) {
            $script:fetchIndex = 0
            $script:failurePhase = $phase
            $native = Read-HermesNativeTableNames 'SYNTHETIC not an MSI'
            Require (-not $native.querySuccess -and $null -eq $native.facts -and $native.nativeQueryFailureKind -ceq "$phase/COMException") 'Native failure must not become an empty successful inventory.'
            $expectedCode = if ($phase -ceq 'Execute') { -2147352567 } else { -2147467259 }
            Require ($native.nativeQueryFailureCode -eq $expectedCode) 'Retain the actual native HRESULT.'
            Require (($native | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|SYNTHETIC') 'Native messages and paths must not escape.'
        }
        $script:failurePhase = ''
        foreach ($count in @(0, 1, 128, 129, 1025)) {
            $script:fetchIndex = 0
            $script:fakeNames = @(for ($index = 0; $index -lt $count; $index++) { "Unknown$index" })
            $native = Read-HermesNativeTableNames 'SYNTHETIC not an MSI'
            if ($count -le 1024) {
                Require ($native.querySuccess -and $native.facts.observedCount -eq $count -and -not $native.facts.predicate) 'The actual reader must preserve observed count boundaries.'
                Require ($native.facts.countValid -eq ($count -ge 1 -and $count -le 128)) 'The reader must apply the inclusive count predicate.'
            } else {
                Require (-not $native.querySuccess -and $null -eq $native.facts -and $null -eq $native.nativeQueryFailureCode) 'An oversized native observation must fail, not truncate.'
            }
        }
        $script:fetchIndex = 0
        $script:fakeNames = @('File', "PRIVATE`nowner-path")
        $native = Read-HermesNativeTableNames 'SYNTHETIC not an MSI'
        Require (-not $native.querySuccess -and $null -eq $native.facts -and $null -eq $native.nativeQueryFailureCode) 'Malformed names fail closed without inventing a native code.'
        Require (($native | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|owner-path') 'Malformed names must not leak.'
        $script:fetchIndex = 0
        $script:fakeNames = @('File')
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
        $bound = Invoke-HermesBoundTableObservation $fixture $repository
        Require ($bound.sourceCommitBinding.matches -and $bound.packageHashMatch -and $bound.querySuccess -and $bound.predicate) 'Valid receipt/source bindings reach exactly the selected observation.'
        Require ((Get-FileHash -LiteralPath (Join-Path $fixture 'Hermes.Installer.msi') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $expected) 'Read-only observation must retain package bytes.'
        Require ($bound.Keys.Count -eq 9 -and ($bound | ConvertTo-Json -Depth 8) -cnotmatch 'SYNTHETIC|obj/|PRIVATE|productCode|packageCode') 'Output contains only the bounded approved fields.'
        $opened = $script:opened
        $mismatch = Invoke-HermesBoundTableObservation $fixture $fixture
        Require (-not $mismatch.sourceCommitBinding.matches -and -not $mismatch.querySuccess -and -not $mismatch.predicate -and $null -eq $mismatch.observedCount) 'Source mismatch must refuse without fabricated counts.'
        Require ($script:opened -eq $opened) 'Source mismatch must not open the MSI.'
        function script:Get-FileHash {
            param([string]$LiteralPath, [string]$Algorithm)
            [pscustomobject]@{ Hash = '0' * 64 }
        }
        try {
            $mismatch = Invoke-HermesBoundTableObservation $fixture $repository
            Require (-not $mismatch.sourceCommitBinding.matches -and -not $mismatch.querySuccess `
                -and -not $mismatch.predicate -and $script:opened -eq $opened) 'Different existing source hashes must stop before querying.'
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
            Require (-not $mismatch.packageHashMatch -and -not $mismatch.querySuccess -and -not $mismatch.predicate -and $null -eq $mismatch.observedCount) 'Receipt mismatch must stop, without fabricated observations.'
            Require ($script:opened -eq $opened) 'Receipt mismatch must not open the MSI.'
            Require (($mismatch | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|owner-path|SYNTHETIC') 'Binding failures must not leak input.'
        }
        $receipt | ConvertTo-Json | Set-Content -LiteralPath $receiptPath
        [IO.File]::WriteAllText((Join-Path $fixture 'Hermes.Installer.msi'), 'PRIVATE changed candidate bytes')
        $mismatch = Invoke-HermesBoundTableObservation $fixture $repository
        Require (-not $mismatch.packageHashMatch -and -not $mismatch.querySuccess -and -not $mismatch.predicate `
            -and $script:opened -eq $opened) 'Changed MSI bytes must not be adopted as their own expected hash.'
        Require (($mismatch | ConvertTo-Json -Depth 8) -cnotmatch 'PRIVATE|candidate bytes') 'Changed MSI bytes must not leak.'
        [IO.File]::WriteAllText((Join-Path $fixture 'Hermes.Installer.msi'), 'SYNTHETIC inert bytes, never opened by COM')
        $script:failurePhase = 'Execute'
        $bound = Invoke-HermesBoundTableObservation $fixture $repository
        Require ($bound.sourceCommitBinding.matches -and $bound.packageHashMatch -and -not $bound.querySuccess `
            -and $null -eq $bound.observedCount -and -not $bound.countValid -and -not $bound.predicate `
            -and $bound.nativeQueryFailureCode -eq -2147352567) 'The final envelope must retain a real native failure without inventing inventory.'
        if (-not $IsWindows) { Refuse { Invoke-HermesPublishedTableObservation $fixture $repository } }
    } $repository $fixture
} finally {
    Remove-Module $module.Name -Force
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
Write-Output 'Source-bound table-inventory parser/predicate controls passed; synthetic fakes only, no COM, MSI, install or profile action.'
