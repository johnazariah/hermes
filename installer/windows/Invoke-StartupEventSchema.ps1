#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Metadata only; never an installed/native acceptance result.
# Unavailable: 1 host, 2 workspace, 3 API, 4 publisher/log, 5 absent event,
# 6 version cap, 7 duplicate version, 8 absent template, 9 template bound,
# 10 XML, 11 structure/field bound, 12 duplicate field, 13 critical name,
# 14 critical type, 15 publication, 16 event identity/version, 17 report bound.
function New-StartupSchemaReport {
    [ordered]@{ schemaVersion = 1; usable = 0; unavailable = 0; metadataCount = 0; versions = @() }
}

function Get-StartupSchemaHostStatus {
    if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
            [Runtime.InteropServices.OSPlatform]::Windows) `
        -or $env:OS -cne 'Windows_NT' -or $env:GITHUB_ACTIONS -cne 'true' `
        -or $env:RUNNER_OS -cne 'Windows' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' `
        -or $env:GITHUB_SERVER_URL -cne 'https://github.com' `
        -or $env:GITHUB_REPOSITORY -cne 'johnazariah/hermes' `
        -or $env:GITHUB_JOB -cne 'startup-event-schema') { return 1 }
    return 0
}

function Read-StartupPublisherMetadata {
    # The only OS acquisition call is publisher metadata, not an event query.
    $providers = @(Get-WinEvent -ListProvider 'Application Error' -ErrorAction Stop)
    if ($providers.Count -ne 1 -or $providers[0].Name -cne 'Application Error') { throw '4' }
    $provider = $providers[0]
    try {
        $bound = @($provider.LogLinks | Where-Object LogName -CEQ 'Application').Count -eq 1
        if (-not $bound) { throw '4' }
        $rows = [Collections.Generic.List[object]]::new()
        foreach ($event in $provider.Events) {
            if ($event.Id -ne 1000) { continue }
            # Retain the ninth identity only to prove that the eight-version cap failed.
            $rows.Add([pscustomobject]@{
                Id = $event.Id; Version = $event.Version
                PublisherName = $provider.Name; PublisherLogBound = $bound
                LogName = $event.LogLink.LogName
                Template = $(if ($rows.Count -lt 8) { $event.Template } else { $null })
            })
            if ($rows.Count -eq 9) { break }
        }
        return $rows.ToArray()
    } finally { $provider.Dispose() }
}

function Get-StartupSchemaTypeCode {
    param([string]$Name, [bool]$OutputType)
    # TDH_IN_TYPE / TDH_OUT_TYPE codes; manifest spellings are from winmeta.xml.
    # https://learn.microsoft.com/windows/win32/wes/eventmanifestschema-outputtype-complextype
    $types = if ($OutputType) {
        @{
            'xs:string' = 1; 'xs:dateTime' = 2
            'xs:unsignedInt' = 8; 'xs:unsignedLong' = 10
            'win:HexInt32' = 18; 'win:HexInt64' = 19; 'win:PID' = 20
            'win:ErrorCode' = 29; 'win:Win32Error' = 30; 'win:NTSTATUS' = 31; 'win:HResult' = 32
        }
    } else {
        @{
            'win:UnicodeString' = 1; 'win:UInt32' = 8; 'win:UInt64' = 10
            'win:FILETIME' = 17; 'win:HexInt32' = 20; 'win:HexInt64' = 21
        }
    }
    # PowerShell hashtable lookup is case-insensitive; schema tokens are not.
    foreach ($key in $types.Keys) { if ($Name -ceq $key) { return [int]$types[$key] } }
    return 0
}

function Test-StartupSchemaCriticalType {
    param([int]$Field, [int]$InputCode, [int]$OutputCode, [bool]$OutputAbsent)
    if (-not $OutputAbsent -and $OutputCode -eq 0) { return $false }
    switch ($Field) {
        1 { return $InputCode -in @(8, 20) -and ($OutputAbsent -or $OutputCode -in @(8, 18, 20)) }
        2 {
            if ($InputCode -eq 17) { return $OutputAbsent -or $OutputCode -eq 2 }
            return $InputCode -in @(10, 21) -and ($OutputAbsent -or $OutputCode -in @(10, 19))
        }
        3 { return $InputCode -eq 1 -and ($OutputAbsent -or $OutputCode -eq 1) }
    }
    return $false
}

function New-StartupSchemaVersion {
    param([int]$Version)
    # Each fields row is [fixed-name enum, exactCaseKnownName present, occurrence count,
    # numericInTypeCode, numericOutTypeCode]. Zero is unknown/absent, never inferred.
    # Name enums: ProcessId=1, ProcessCreationTime=2, AppPath=3,
    # ExceptionCode=4, ModuleName=5, ModulePath=6. Present bits use enum-1.
    [ordered]@{
        id = 1000; version = $Version; unavailable = 0; fieldCount = 0
        duplicateCount = 0; caseMismatchCount = 0; knownFieldPresentBits = 0
        templateSha256 = ('0' * 64); fields = @()
    }
}

function Read-StartupSchemaTemplate {
    param([string]$Template, [Collections.IDictionary]$Version)
    if ([string]::IsNullOrEmpty($Template)) { $Version.unavailable = 8; return }
    $encoding = [Text.UTF8Encoding]::new($false, $true)
    try {
        if ($Template.Length -gt 16384 -or $encoding.GetByteCount($Template) -gt 16384) {
            $Version.unavailable = 9; return
        }
        $Version.templateSha256 = [Convert]::ToHexString(
            [Security.Cryptography.SHA256]::HashData($encoding.GetBytes($Template))).ToLowerInvariant()
    } catch { $Version.unavailable = 10; return }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 16384
    $text = [IO.StringReader]::new($Template)
    $reader = $null
    try {
        $reader = [Xml.XmlReader]::Create($text, $settings)
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        $root = $document.DocumentElement
        if ($root.LocalName -cne 'template' `
            -or $root.NamespaceURI -cne 'http://schemas.microsoft.com/win/2004/08/events') {
            $Version.unavailable = 11; return
        }
        $nodes = @($root.ChildNodes | Where-Object NodeType -EQ ([Xml.XmlNodeType]::Element))
        $Version.fieldCount = [Math]::Min(129, $nodes.Count)
        if ($nodes.Count -gt 128 -or @($nodes | Where-Object {
                    $_.LocalName -cne 'data' -or $_.NamespaceURI -cne $root.NamespaceURI `
                        -or $_.HasChildNodes -or [string]::IsNullOrEmpty($_.GetAttribute('name'))
                }).Count -ne 0) { $Version.unavailable = 11; return }
        Set-StartupSchemaFields $nodes $Version
    } catch { $Version.unavailable = 10 }
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        $text.Dispose()
    }
}

function Set-StartupSchemaFields {
    param([object[]]$Nodes, [Collections.IDictionary]$Version)
    $names = @('ProcessId', 'ProcessCreationTime', 'AppPath', 'ExceptionCode', 'ModuleName', 'ModulePath')
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($node in $Nodes) {
        if (-not $seen.Add($node.GetAttribute('name'))) { $Version.duplicateCount++ }
    }
    $criticalNames = $true
    $criticalTypes = $true
    for ($index = 0; $index -lt $names.Count; $index++) {
        $matches = @($Nodes | Where-Object { $_.GetAttribute('name') -ieq $names[$index] })
        $exact = @($matches | Where-Object { $_.GetAttribute('name') -ceq $names[$index] })
        $Version.caseMismatchCount += $matches.Count - $exact.Count
        if ($exact.Count -gt 0) {
            $Version.knownFieldPresentBits = $Version.knownFieldPresentBits -bor (1 -shl $index)
        }
        $inputCode = 0
        $outputCode = 0
        if ($matches.Count -eq 1 -and $exact.Count -eq 1) {
            $inputCode = Get-StartupSchemaTypeCode $exact[0].GetAttribute('inType') $false
            $outputCode = Get-StartupSchemaTypeCode $exact[0].GetAttribute('outType') $true
        }
        $Version.fields += ,@(($index + 1), [int]($exact.Count -gt 0),
            $matches.Count, $inputCode, $outputCode)
        if ($index -ge 3) { continue }
        if ($matches.Count -ne 1 -or $exact.Count -ne 1) { $criticalNames = $false; continue }
        if ($exact[0].HasAttribute('count') -or $exact[0].HasAttribute('length') `
            -or $exact[0].HasAttribute('map')) { $criticalTypes = $false; continue }
        if (-not (Test-StartupSchemaCriticalType ($index + 1) $inputCode $outputCode `
                    (-not $exact[0].HasAttribute('outType')))) { $criticalTypes = $false }
    }
    if ($Version.duplicateCount -ne 0) { $Version.unavailable = 12 }
    elseif (-not $criticalNames) { $Version.unavailable = 13 }
    elseif (-not $criticalTypes) { $Version.unavailable = 14 }
}

function ConvertTo-StartupSchemaReport {
    param([AllowEmptyCollection()][object[]]$Metadata)
    $report = New-StartupSchemaReport
    $report.metadataCount = [Math]::Min(9, $Metadata.Count)
    if ($Metadata.Count -eq 0) { $report.unavailable = 5; return $report }
    $seen = [Collections.Generic.HashSet[int]]::new()
    foreach ($row in @($Metadata | Select-Object -First 8)) {
        if ($row.PublisherName -cne 'Application Error' -or $row.PublisherLogBound -cne $true `
            -or $row.LogName -cne 'Application') { $report.unavailable = 4; continue }
        if ($row.Id -ne 1000 -or -not ($row.Version -is [byte] -or $row.Version -is [int] `
                -or $row.Version -is [long]) -or $row.Version -lt 0 -or $row.Version -gt 255) {
            $report.unavailable = 16; continue
        }
        $version = New-StartupSchemaVersion ([int]$row.Version)
        Read-StartupSchemaTemplate $row.Template $version
        if (-not $seen.Add([int]$row.Version)) { $version.unavailable = 7 }
        $report.versions += $version
        if ($version.unavailable -ne 0 -and $report.unavailable -eq 0) {
            $report.unavailable = $version.unavailable
        }
    }
    if ($Metadata.Count -gt 8) { $report.unavailable = 6 }
    $report.usable = [int]($report.unavailable -eq 0 -and $report.versions.Count -ne 0)
    return $report
}

function ConvertTo-StartupSchemaBytes {
    param([Collections.IDictionary]$Report)
    $json = ($Report | ConvertTo-Json -Depth 6 -Compress) + "`n"
    $bytes = [Text.UTF8Encoding]::new($false, $true).GetBytes($json)
    if ($bytes.Length -gt 4096) { throw '17' }
    return ,$bytes
}

function Assert-StartupSchemaDirectory {
    param([string]$Path)
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.Length -gt 240 `
        -or [IO.Path]::GetFullPath($Path) -cne $Path -or $Path -match '["\x00-\x1f]') { throw '2' }
    for ($current = $Path; $current; $current = [IO.Path]::GetDirectoryName($current)) {
        $attributes = [IO.File]::GetAttributes($current)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 `
            -or ($attributes -band [IO.FileAttributes]::Directory) -eq 0) { throw '2' }
    }
}

function Test-StartupSchemaWorkspace {
    param([string]$Path, [string]$Checkout)
    # Pure lexical admission. Both production callers derive Checkout only from
    # the executing script's fixed root; there is no CLI path selector.
    return -not [string]::IsNullOrEmpty($Path) -and $Path.Length -le 240 `
        -and $Path -cnotmatch '["\x00-\x1f]' `
        -and $Path -ceq $Checkout
}

function Publish-StartupSchemaReport {
    param([byte[]]$Bytes)
    if ($Bytes.Length -gt 4096) { throw '17' }
    $workspace = $env:GITHUB_WORKSPACE
    if (-not (Test-StartupSchemaWorkspace $workspace `
                ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))) { throw '2' }
    Assert-StartupSchemaDirectory $workspace
    $directory = $workspace
    foreach ($part in @('.artifacts', 'installer-evidence')) {
        $directory = Join-Path $directory $part
        $entry = [IO.DirectoryInfo]::new($directory)
        if ($null -ne $entry.LinkTarget) { throw '2' }
        if (-not [IO.Directory]::Exists($directory)) {
            $null = [IO.Directory]::CreateDirectory($directory)
        }
        Assert-StartupSchemaDirectory $directory
    }
    # Refuse every pre-existing target, even an earlier report. CreateNew also
    # refuses a hard link, symlink, directory or unknown file without truncation.
    $path = Join-Path $directory 'startup-event-schema.json'
    $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
    finally { $stream.Dispose() }
}

function Invoke-StartupEventSchema {
    $report = New-StartupSchemaReport
    $published = 0
    $report.unavailable = Get-StartupSchemaHostStatus
    if ($report.unavailable -eq 0) {
        try {
            # Validate the destination before reading even public OS metadata.
            $report.unavailable = 2
            if (-not (Test-StartupSchemaWorkspace $env:GITHUB_WORKSPACE `
                        ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))))) { throw '2' }
            Assert-StartupSchemaDirectory $env:GITHUB_WORKSPACE
            $report.unavailable = 3
            $metadata = @(Read-StartupPublisherMetadata)
            $report = ConvertTo-StartupSchemaReport $metadata
        } catch {
            $report.usable = 0
            if ($_.Exception.Message -ceq '2') { $report.unavailable = 2 }
            elseif ($_.Exception.Message -ceq '4') { $report.unavailable = 4 }
        }
        try {
            Publish-StartupSchemaReport (ConvertTo-StartupSchemaBytes $report)
            $published = 1
        } catch {
            $report.usable = 0
            $report.unavailable = switch -CaseSensitive ($_.Exception.Message) {
                '2' { 2 }; '17' { 17 }; default { 15 }
            }
        }
    }
    Write-Output ('HERMES_STARTUP_SCHEMA v=1 usable={0} unavailable={1} metadataCount={2} versions={3} published={4}' -f
        $report.usable, $report.unavailable, $report.metadataCount, $report.versions.Count, $published)
    return [int]($report.usable -ne 1)
}

$result = @(Invoke-StartupEventSchema)
$result[0]
exit $result[-1]
