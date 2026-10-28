#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$document = [xml](Get-Content -LiteralPath $ReportPath -Raw)
if ($document.DocumentElement.Name -ne 'coverage') {
    throw 'The report must be a native Cobertura coverage document.'
}
$names = @($document.coverage.packages.package | ForEach-Object { [string]$_.name })
foreach ($name in @('Hermes.Core', 'Hermes.Service', 'Hermes.UI', 'Hermes.Servicing')) {
    if (@($names | Where-Object { $_ -ceq $name }).Count -ne 1) {
        throw "Coverage must retain exactly one $name assembly."
    }
}
$counts = @{}
foreach ($name in @('lines-covered', 'lines-valid', 'branches-covered', 'branches-valid')) {
    $value = 0L
    $text = $document.DocumentElement.GetAttribute($name)
    if (-not [long]::TryParse($text, [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$value)) {
        throw "Coverage count $name must be a nonnegative integer."
    }
    $counts[$name] = $value
}
foreach ($kind in @('lines', 'branches')) {
    if ($counts["$kind-valid"] -le 0 -or
        $counts["$kind-covered"] -gt $counts["$kind-valid"]) {
        throw "Invalid $kind coverage denominator or covered count."
    }
}
$lines = $counts['lines-covered']
$lineTotal = $counts['lines-valid']
$branches = $counts['branches-covered']
$branchTotal = $counts['branches-valid']
Write-Output ("Line coverage: {0}/{1} ({2:N4}%)" -f $lines, $lineTotal, (100.0 * $lines / $lineTotal))
Write-Output ("Branch coverage: {0}/{1} ({2:N4}%)" -f $branches, $branchTotal, (100.0 * $branches / $branchTotal))
if ([decimal]$lines * 100 -lt [decimal]$lineTotal * 85) {
    throw 'Line coverage is below the exact 85% gate.'
}
if ([decimal]$branches * 100 -lt [decimal]$branchTotal * 60) {
    throw 'Branch coverage is below the exact 60% gate.'
}
