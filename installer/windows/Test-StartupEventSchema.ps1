#Requires -Version 7.0
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Pure metadata fixtures and AST checks only, including on Windows. These are
# portability controls, never proof of the actual Windows publisher schema.
$source = Join-Path $PSScriptRoot 'Invoke-StartupEventSchema.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'Probe syntax control failed.' }
$definitions = @($ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst]
}, $true))
foreach ($definition in $definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
$checks = 0
$maxReportBytes = 0
function Assert-SchemaControl {
    param([bool]$Condition)
    if (-not $Condition) { throw ('Schema control failed: ' + $script:checks) }
    $script:checks++
}
function New-SchemaFixture {
    param([string]$Template, [int]$Version = 0)
    [pscustomobject]@{
        Id = 1000; Version = $Version; PublisherName = 'Application Error'
        PublisherLogBound = $true; LogName = 'Application'; Template = $Template
    }
}
function Get-SchemaFixtureReport {
    param([AllowEmptyCollection()][object[]]$Metadata)
    $report = ConvertTo-StartupSchemaReport $Metadata
    $bytes = ConvertTo-StartupSchemaBytes $report
    $script:maxReportBytes = [Math]::Max($script:maxReportBytes, $bytes.Length)
    Assert-SchemaControl ($bytes.Length -le 4096 -and $bytes[0] -eq 123)
    $json = [Text.Encoding]::UTF8.GetString($bytes)
    Assert-SchemaControl ($json -notmatch 'SYNTHETIC_PRIVATE|Application Error|AppPath|win:|xs:|<|\\|template xmlns')
    $wire = $json | ConvertFrom-Json -AsHashtable
    Assert-SchemaControl (($wire.Keys | Sort-Object) -join ',' -ceq
        'metadataCount,schemaVersion,unavailable,usable,versions')
    foreach ($name in @('metadataCount', 'schemaVersion', 'unavailable', 'usable')) {
        Assert-SchemaControl ($wire[$name] -is [long])
    }
    foreach ($version in $wire.versions) {
        Assert-SchemaControl (($version.Keys | Sort-Object) -join ',' -ceq
            'caseMismatchCount,duplicateCount,fieldCount,fields,id,knownFieldPresentBits,templateSha256,unavailable,version')
        foreach ($name in @('caseMismatchCount', 'duplicateCount', 'fieldCount', 'id',
                'knownFieldPresentBits', 'unavailable', 'version')) {
            Assert-SchemaControl ($version[$name] -is [long])
        }
        Assert-SchemaControl ($version.templateSha256 -cmatch '\A[0-9a-f]{64}\z')
        foreach ($field in $version.fields) {
            Assert-SchemaControl ($field.Count -eq 5)
            foreach ($value in $field) { Assert-SchemaControl ($value -is [long]) }
        }
    }
    return $report
}

$template = @'
<template xmlns="http://schemas.microsoft.com/win/2004/08/events">
  <data name="ProcessId" inType="win:UInt32" outType="win:PID"/>
  <data name="ProcessCreationTime" inType="win:FILETIME" outType="xs:dateTime"/>
  <data name="AppPath" inType="win:UnicodeString" outType="xs:string"/>
  <data name="ExceptionCode" inType="win:HexInt32" outType="win:HexInt32"/>
  <data name="ModuleName" inType="win:UnicodeString"/>
  <data name="ModulePath" inType="win:UnicodeString"/>
  <data name="SYNTHETIC_PRIVATE" inType="SYNTHETIC_PRIVATE"/>
</template>
'@
$good = Get-SchemaFixtureReport @(New-SchemaFixture $template)
Assert-SchemaControl ($good.usable -eq 1 -and $good.unavailable -eq 0 `
    -and $good.metadataCount -eq 1 -and $good.versions[0].knownFieldPresentBits -eq 63 `
    -and $good.versions[0].fieldCount -eq 7 -and $good.versions[0].duplicateCount -eq 0)
Assert-SchemaControl (($good.versions[0].fields[0] -join ',') -ceq '1,1,1,8,20')
Assert-SchemaControl (($good.versions[0].fields[1] -join ',') -ceq '2,1,1,17,2')
Assert-SchemaControl (($good.versions[0].fields[2] -join ',') -ceq '3,1,1,1,1')
Assert-SchemaControl (($good.versions[0].fields[3] -join ',') -ceq '4,1,1,20,18')
Assert-SchemaControl (($good.versions[0].fields[4] -join ',') -ceq '5,1,1,1,0')
Assert-SchemaControl (($good.versions[0].fields[5] -join ',') -ceq '6,1,1,1,0')
$digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
    [Text.Encoding]::UTF8.GetBytes($template))).ToLowerInvariant()
Assert-SchemaControl ($good.versions[0].templateSha256 -ceq $digest)

foreach ($pair in @(
        @('win:UnicodeString', 1), @('win:UInt32', 8), @('win:UInt64', 10),
        @('win:FILETIME', 17), @('win:HexInt32', 20), @('win:HexInt64', 21),
        @('win:FileTime', 0), @('win:UInt16', 0), @('SYNTHETIC_PRIVATE', 0), @('', 0))) {
    Assert-SchemaControl ((Get-StartupSchemaTypeCode $pair[0] $false) -eq $pair[1])
}
foreach ($pair in @(
        @('win:String', 0), @('xs:string', 1), @('win:DateTime', 0), @('xs:dateTime', 2),
        @('win:UnsignedInt', 0), @('xs:unsignedInt', 8), @('win:UnsignedLong', 0),
        @('xs:unsignedLong', 10), @('win:HexInt32', 18), @('win:HexInt64', 19),
        @('win:PID', 20), @('win:ErrorCode', 29), @('win:Win32Error', 30),
        @('win:NTSTATUS', 31), @('win:HResult', 32), @('win:UInt32', 0),
        @('xs:String', 0), @('SYNTHETIC_PRIVATE', 0), @('', 0))) {
    Assert-SchemaControl ((Get-StartupSchemaTypeCode $pair[0] $true) -eq $pair[1])
}
foreach ($inputName in @('win:UInt64', 'win:HexInt64')) {
    $candidate = $template.Replace('inType="win:FILETIME" outType="xs:dateTime"',
        ('inType="' + $inputName + '" outType="win:HexInt64"'))
    $report = Get-SchemaFixtureReport @(New-SchemaFixture $candidate)
    Assert-SchemaControl ($report.usable -eq 1)
}
$hexPid = $template.Replace('inType="win:UInt32" outType="win:PID"', 'inType="win:HexInt32"')
$report = Get-SchemaFixtureReport @(New-SchemaFixture $hexPid)
Assert-SchemaControl ($report.usable -eq 1 -and $report.versions[0].fields[0][3] -eq 20)
$unknownType = $template.Replace('name="ModulePath" inType="win:UnicodeString"',
    'name="ModulePath" inType="SYNTHETIC_PRIVATE" outType="SYNTHETIC_PRIVATE"')
$report = Get-SchemaFixtureReport @(New-SchemaFixture $unknownType)
Assert-SchemaControl ($report.versions[0].fields[5][3] -eq 0 -and $report.versions[0].fields[5][4] -eq 0)

$cases = @(
    @{ Text = ''; Reason = 8 },
    @{ Text = ('x' * 16385); Reason = 9 },
    @{ Text = ([string][char]0x00e9 * 8193); Reason = 9 },
    @{ Text = '<template>'; Reason = 10 },
    @{ Text = ($template + [string][char]0xd800); Reason = 10 },
    @{ Text = ('<!DOCTYPE template [<!ENTITY x SYSTEM "file:///SYNTHETIC_PRIVATE">]>' + $template); Reason = 10 },
    @{ Text = ('<!DOCTYPE template SYSTEM "https://invalid.invalid/SYNTHETIC_PRIVATE">' + $template); Reason = 10 },
    @{ Text = ('<!DOCTYPE template [<!ENTITY x "SYNTHETIC_PRIVATE">]>' + $template); Reason = 10 },
    @{ Text = $template.Replace('/2004/08/events', '/wrong'); Reason = 11 },
    @{ Text = $template.Replace('name="SYNTHETIC_PRIVATE"', 'name=""'); Reason = 11 },
    @{ Text = $template.Replace('<data name="SYNTHETIC_PRIVATE" inType="SYNTHETIC_PRIVATE"/>', '<struct name="SYNTHETIC_PRIVATE"/>'); Reason = 11 },
    @{ Text = $template.Replace('name="ProcessId"', 'name="processId"'); Reason = 13 },
    @{ Text = $template.Replace('name="ProcessId"', 'name="SYNTHETIC_PRIVATE_PID"'); Reason = 13 },
    @{ Text = $template.Replace('name="ProcessCreationTime"', 'name="SYNTHETIC_PRIVATE_BIRTH"'); Reason = 13 },
    @{ Text = $template.Replace('name="AppPath"', 'name="SYNTHETIC_PRIVATE_IMAGE"'); Reason = 13 },
    @{ Text = $template.Replace('</template>', '<data name="ProcessId" inType="win:UInt32"/></template>'); Reason = 12 },
    @{ Text = $template.Replace('</template>', '<data name="SYNTHETIC_PRIVATE"/></template>'); Reason = 12 },
    @{ Text = $template.Replace('</template>', '<data name="processId" inType="win:UInt32"/></template>'); Reason = 13 },
    @{ Text = $template.Replace('inType="win:UInt32"', 'inType="win:UnicodeString"'); Reason = 14 },
    @{ Text = $template.Replace('inType="win:FILETIME"', 'inType="win:UInt32"'); Reason = 14 },
    @{ Text = $template.Replace('name="AppPath" inType="win:UnicodeString"', 'name="AppPath" inType="win:UInt64"'); Reason = 14 },
    @{ Text = $template.Replace('outType="win:PID"', 'outType="SYNTHETIC_PRIVATE"'); Reason = 14 },
    @{ Text = $template.Replace('outType="xs:dateTime"', 'outType="xs:unsignedLong"'); Reason = 14 },
    @{ Text = $template.Replace('outType="xs:string"', 'outType=""'); Reason = 14 },
    @{ Text = $template.Replace('outType="win:PID"', 'outType="xs:string"'); Reason = 14 },
    @{ Text = $template.Replace('name="ProcessId"', 'name="ProcessId" count="2"'); Reason = 14 },
    @{ Text = $template.Replace('name="ProcessCreationTime"', 'name="ProcessCreationTime" map="SYNTHETIC_PRIVATE"'); Reason = 14 },
    @{ Text = $template.Replace('name="AppPath"', 'name="AppPath" length="ProcessId"'); Reason = 14 },
    @{ Text = $template.Replace('</template>', (('<data name="extra"/>' * 129) + '</template>')); Reason = 11 }
)
foreach ($case in $cases) {
    $report = Get-SchemaFixtureReport @(New-SchemaFixture $case.Text)
    Assert-SchemaControl ($report.usable -eq 0 -and $report.unavailable -eq $case.Reason)
}
foreach ($name in @('ProcessId', 'ProcessCreationTime', 'AppPath')) {
    $duplicate = $template.Replace('</template>', ('<data name="' + $name + '"/></template>'))
    $report = Get-SchemaFixtureReport @(New-SchemaFixture $duplicate)
    Assert-SchemaControl ($report.usable -eq 0 -and $report.unavailable -eq 12 `
        -and $report.versions[0].duplicateCount -eq 1)
}
$report = Get-SchemaFixtureReport @(New-SchemaFixture ($template.Replace('name="ProcessId"', 'name="processId"')))
Assert-SchemaControl ($report.versions[0].knownFieldPresentBits -eq 62 `
    -and $report.versions[0].caseMismatchCount -eq 1 `
    -and ($report.versions[0].fields[0] -join ',') -ceq '1,0,1,0,0')
$duplicate = $template.Replace('</template>', '<data name="ProcessId"/></template>')
$report = Get-SchemaFixtureReport @(New-SchemaFixture $duplicate)
Assert-SchemaControl (($report.versions[0].fields[0] -join ',') -ceq '1,1,2,0,0')
$report = Get-SchemaFixtureReport @()
Assert-SchemaControl ($report.unavailable -eq 5 -and $report.usable -eq 0)
foreach ($property in @('PublisherName', 'PublisherLogBound', 'LogName', 'Id', 'Version')) {
    $row = New-SchemaFixture $template
    $row.$property = switch ($property) {
        PublisherName { 'SYNTHETIC_PRIVATE' }; PublisherLogBound { $false }
        LogName { 'SYNTHETIC_PRIVATE' }; Id { 1001 }; Version { 256 }
    }
    $report = Get-SchemaFixtureReport @($row)
    Assert-SchemaControl ($report.usable -eq 0 -and $report.unavailable -in @(4, 16))
}
$report = Get-SchemaFixtureReport @((New-SchemaFixture $template), (New-SchemaFixture $template))
Assert-SchemaControl ($report.usable -eq 0 -and $report.unavailable -eq 7 -and $report.versions.Count -eq 2)
$rows = @(0..7 | ForEach-Object { New-SchemaFixture $template $_ })
$report = Get-SchemaFixtureReport $rows
Assert-SchemaControl ($report.usable -eq 1 -and $report.versions.Count -eq 8)
$report = Get-SchemaFixtureReport ($rows + (New-SchemaFixture $template 8))
Assert-SchemaControl ($report.usable -eq 0 -and $report.unavailable -eq 6 `
    -and $report.metadataCount -eq 9 -and $report.versions.Count -eq 8)
$padded = $template.Replace('</template>', (('<data name="extra"/>' * 121) + '</template>'))
$report = Get-SchemaFixtureReport @(248..255 | ForEach-Object { New-SchemaFixture $padded $_ })
Assert-SchemaControl ($report.usable -eq 0 -and $report.versions.Count -eq 8 `
    -and $report.versions[0].fieldCount -eq 128 -and $report.versions[0].duplicateCount -eq 120)
foreach ($version in @(-1, '0', $true, 1.5)) {
    $row = New-SchemaFixture $template
    $row.Version = $version
    $report = Get-SchemaFixtureReport @($row)
    Assert-SchemaControl ($report.usable -eq 0 -and $report.unavailable -eq 16)
}
$report = Get-SchemaFixtureReport @((New-SchemaFixture $template), (New-SchemaFixture '' 1))
Assert-SchemaControl ($report.usable -eq 0 -and $report.unavailable -eq 8 -and $report.versions.Count -eq 2)
$canonical = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Assert-SchemaControl (Test-StartupSchemaWorkspace $canonical $canonical)
foreach ($path in @('', '.', 'SYNTHETIC_PRIVATE', ($canonical + '/..'), ($canonical + '/'),
        ($canonical + '/.artifacts'), ($canonical + '-reflected'), ($canonical + '"'), ($canonical + "`n"))) {
    Assert-SchemaControl (-not (Test-StartupSchemaWorkspace $path $canonical))
}

# Static controls keep the OS boundary fixed without acquiring OS metadata.
Assert-SchemaControl ($ast.ParamBlock.Parameters.Count -eq 0)
$commands = @($ast.FindAll({ param($node)
    $node -is [Management.Automation.Language.CommandAst]
}, $true))
$acquisition = @($commands | Where-Object { $_.GetCommandName() -ceq 'Get-WinEvent' })
Assert-SchemaControl ($acquisition.Count -eq 1)
Assert-SchemaControl ($acquisition[0].Extent.Text -ceq
    "Get-WinEvent -ListProvider 'Application Error' -ErrorAction Stop")
$text = $ast.Extent.Text
Assert-SchemaControl ($text -notmatch
    '(?i)EvtQuery|FilterHashtable|FilterXPath|ReadEvent|Start-Process|ProcessStartInfo|Register-|wevtutil|Invoke-Expression|Invoke-Command|Add-Type')
Assert-SchemaControl ($text -notmatch '(?i)\$(provider|event)\.(Message|Description|ResourceFilePath|MessageFilePath|ParameterFilePath)\b')
Assert-SchemaControl ($text.Contains('$provider.Events') -and $text.Contains('$event.Template') `
    -and $text.Contains('$event.LogLink.LogName') -and $text.Contains('$provider.LogLinks'))
foreach ($required in @(
        '[Xml.DtdProcessing]::Prohibit', '$settings.XmlResolver = $null', '$document.XmlResolver = $null',
        '$settings.MaxCharactersInDocument = 16384', '[IO.FileMode]::CreateNew', '[IO.FileShare]::None',
        '[IO.FileAttributes]::ReparsePoint', '[IO.FileAttributes]::Directory', '$provider.Dispose()',
        "'startup-event-schema.json'", "'installer-evidence'", "'johnazariah/hermes'",
        "'https://github.com'", "'github-hosted'", "'startup-event-schema'")) {
    Assert-SchemaControl ($text.Contains($required))
}
$entry = ($definitions | Where-Object Name -CEQ 'Invoke-StartupEventSchema').Extent.Text
Assert-SchemaControl ($entry.IndexOf('Get-StartupSchemaHostStatus') -lt $entry.IndexOf('Read-StartupPublisherMetadata'))
Assert-SchemaControl ($entry.IndexOf('Assert-StartupSchemaDirectory') -lt $entry.IndexOf('Read-StartupPublisherMetadata'))
$writer = ($definitions | Where-Object Name -CEQ 'Publish-StartupSchemaReport').Extent.Text
Assert-SchemaControl ($writer.Contains('Test-StartupSchemaWorkspace $workspace'))
Assert-SchemaControl ($writer.Contains("[IO.Path]::GetFullPath((Join-Path `$PSScriptRoot '../..'))"))
Assert-SchemaControl ($writer.Contains('Assert-StartupSchemaDirectory $directory') `
    -and $writer.Contains("foreach (`$part in @('.artifacts', 'installer-evidence'))") `
    -and $writer.Contains('$entry.LinkTarget'))
Assert-SchemaControl ($text -notmatch '(?i)FileMode\]::(Create|OpenOrCreate|Truncate)\b|WriteAll|Set-Content|Out-File|Remove-Item')
if (-not [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([Runtime.InteropServices.OSPlatform]::Windows)) {
    function Get-WinEvent { throw 'OS acquisition reached from a portable control.' }
    $result = @(Invoke-StartupEventSchema)
    Assert-SchemaControl ($result.Count -eq 2 -and $result[1] -eq 1 `
        -and $result[0] -ceq 'HERMES_STARTUP_SCHEMA v=1 usable=0 unavailable=1 metadataCount=0 versions=0 published=0')
}

$workflow = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../.github/workflows/startup-event-schema.yml'))
foreach ($required in @('windows-latest', 'timeout-minutes: 5', 'actions/checkout@v4',
        'actions/upload-artifact@v4', 'retention-days: 3', 'if: always()',
        'installer/windows/Test-StartupEventSchema.ps1', 'installer/windows/Invoke-StartupEventSchema.ps1',
        '.artifacts/installer-evidence/startup-event-schema.json')) {
    Assert-SchemaControl ($workflow.Contains($required))
}
Assert-SchemaControl ($workflow -notmatch '(?i)setup-dotnet|setup-node|cache:|pull_request:|inputs:|continue-on-error|secrets\.')
Assert-SchemaControl ($workflow.Contains("steps.metadata.outputs.published == '1'") `
    -and $workflow.Contains('"published=$published" >> $env:GITHUB_OUTPUT') `
    -and $workflow.Contains('exit $code'))
Assert-SchemaControl ([regex]::Matches($workflow, '(?m)^\s+(?:- )?uses:').Count -eq 2)
Assert-SchemaControl ($workflow.Contains("branches: ['**']"))
Write-Output ('HERMES_STARTUP_SCHEMA_CONTROLS v=1 passed={0} maxReportBytes={1} nativePublisherProof=0' -f
    $checks, $maxReportBytes)
