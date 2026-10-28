#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [Parameter(Mandatory = $true)][string]$Suite
)
$ErrorActionPreference = 'Stop'
$document = [xml](Get-Content -LiteralPath $ReportPath -Raw)
if ($document.DocumentElement.LocalName -ne 'TestRun') {
    throw 'Test execution requires a VSTest TRX report.'
}
$counters = $document.TestRun.ResultSummary.Counters
if ($null -eq $counters) { throw 'Test execution counters are missing.' }
$counts = @{}
foreach ($name in @('total', 'executed', 'passed', 'failed', 'notExecuted')) {
    $value = 0L
    if (-not [long]::TryParse($counters.GetAttribute($name),
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
        throw "Test execution count $name must be a nonnegative integer."
    }
    $counts[$name] = $value
}
$total = $counts['total']
if ($total -le 0 -or $counts['executed'] -ne $total -or
    $counts['passed'] -ne $total -or $counts['failed'] -ne 0 -or
    $counts['notExecuted'] -ne 0) {
    throw "$Suite coverage execution must run every selected test without failures or skips."
}
Write-Output ("{0} coverage tests: {1}/{2} passed; zero failed/skipped." -f $Suite, $counts['passed'], $total)
