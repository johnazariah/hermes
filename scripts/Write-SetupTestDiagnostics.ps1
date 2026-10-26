#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [Parameter(Mandatory = $true)][DateTime]$NotBeforeUtc
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Advisory only: the global carrier is gated by a failed method family, not a
# particular theory case. Producer task observations are not an atomic snapshot.
# Unavailable: 1 missing, 2 stale, 3 no eligible family, 4 unreadable/malformed XML,
# 5 absent row, 6 refused structure/schema/bounds. Caller reserves 7 for reader failure.
function Write-Unavailable([int]$Reason) {
    Write-Output "HERMES_SETUP_DIAGNOSTIC_EVIDENCE v=1 unavailable=$Reason"
}

$stream = $null
$reader = $null
try {
    $ReportPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReportPath)
    if (-not [IO.File]::Exists($ReportPath)) { Write-Unavailable 1; return }
    if ([IO.File]::GetLastWriteTimeUtc($ReportPath) -lt $NotBeforeUtc.ToUniversalTime()) {
        Write-Unavailable 2
        return
    }
    $stream = [IO.File]::Open($ReportPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    if ($stream.Length -gt 8MB) { Write-Unavailable 6; return }
    $settings = [Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $settings.XmlResolver = $null
    $settings.MaxCharactersInDocument = 8MB
    $reader = [Xml.XmlReader]::Create($stream, $settings)
    $document = [Xml.XmlDocument]::new()
    $document.XmlResolver = $null
    $document.Load($reader)
    $namespace = 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'
    if ($document.DocumentElement.LocalName -cne 'TestRun' -or
        $document.DocumentElement.NamespaceURI -cne $namespace) {
        Write-Unavailable 6
        return
    }
    $namespaces = [Xml.XmlNamespaceManager]::new($document.NameTable)
    $namespaces.AddNamespace('t', $namespace)
    foreach ($container in @('TestDefinitions', 'Results', 'ResultSummary')) {
        if ($document.SelectNodes("/t:TestRun/t:$container", $namespaces).Count -ne 1) {
            Write-Unavailable 6
            return
        }
    }
    $definitions = [Collections.Generic.Dictionary[string, bool]]::new([StringComparer]::Ordinal)
    foreach ($definition in $document.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest', $namespaces)) {
        $id = $definition.GetAttribute('id')
        $methods = $definition.SelectNodes('t:TestMethod', $namespaces)
        if ([string]::IsNullOrEmpty($id) -or $definitions.ContainsKey($id) -or $methods.Count -ne 1) {
            Write-Unavailable 6
            return
        }
        $method = $methods[0]
        $eligible = $method.GetAttribute('className') -ceq 'Hermes.Setup.Tests.SetupChildTransportTests' -and
            $method.GetAttribute('name') -ceq 'Every_diagnostic_record_is_consumed_and_semantically_bound'
        $definitions.Add($id, $eligible)
    }
    $resultIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $failedFamily = $false
    foreach ($result in $document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult', $namespaces)) {
        $id = $result.GetAttribute('testId')
        if (-not $definitions.ContainsKey($id) -or -not $resultIds.Add($id)) {
            Write-Unavailable 6
            return
        }
        if ($definitions[$id] -and $result.GetAttribute('outcome') -ceq 'Failed') {
            $failedFamily = $true
        }
    }
    if (-not $failedFamily) { Write-Unavailable 3; return }
    $outputs = $document.SelectNodes('/t:TestRun/t:ResultSummary/t:Output/t:StdOut', $namespaces)
    if ($outputs.Count -eq 0) { Write-Unavailable 5; return }
    if ($outputs.Count -ne 1 -or $outputs[0].SelectNodes('*').Count -ne 0) {
        Write-Unavailable 6
        return
    }
    # Inline ranges avoid integer conversion, Unicode digits, overflow and
    # noncanonical forms. No raw XML, testName, ErrorInfo or other output is exported.
    $pattern = '\AHERMES_SETUP_DIAGNOSTIC_ASSERT v=1 bounded=[0-7] operation=[0-7] kind=[0-5] operationKind=[0-5] observed=[01] stdout=(?:-1|[0-7]) stderr=(?:-1|[0-7]) exit=(?:-1|[0-7]) fault=(?:-1|[0-9]|1[01]) operationFault=(?:-1|[0-9]|1[01]) initiating=(?:-1|[0-9]|1[01]) checks=(?:[0-9]|1[0-5]) closed=[0-3] exited=[01] kills=[0-2]\z'
    $rows = [Collections.Generic.List[string]]::new()
    foreach ($line in ($outputs[0].InnerText -split '\r\n|\n|\r')) {
        if (-not $line.StartsWith('HERMES_SETUP_DIAGNOSTIC_ASSERT', [StringComparison]::Ordinal)) { continue }
        if ([Text.Encoding]::UTF8.GetByteCount($line) -gt 512 -or $rows.Count -ge 32 -or
            -not [regex]::IsMatch($line, $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
            Write-Unavailable 6
            return
        }
        $rows.Add($line)
    }
    if ($rows.Count -eq 0) { Write-Unavailable 5; return }
    $rows | Write-Output
} catch {
    Write-Unavailable 4
} finally {
    if ($null -ne $reader) { $reader.Dispose() }
    if ($null -ne $stream) { $stream.Dispose() }
}
