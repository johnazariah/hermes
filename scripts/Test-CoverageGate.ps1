#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$gate = Join-Path $PSScriptRoot 'Assert-Coverage.ps1'
$cases = @(
    @{ Name = 'exact boundary'; Lines = 850; LineTotal = 1000; Branches = 600; BranchTotal = 1000; Ui = $true; Pass = $true },
    @{ Name = 'above boundary'; Lines = 851; LineTotal = 1000; Branches = 601; BranchTotal = 1000; Ui = $true; Pass = $true },
    @{ Name = 'line rounding cannot pass'; Lines = 84999; LineTotal = 100000; Branches = 600; BranchTotal = 1000; Ui = $true; Pass = $false },
    @{ Name = 'branch rounding cannot pass'; Lines = 850; LineTotal = 1000; Branches = 59999; BranchTotal = 100000; Ui = $true; Pass = $false },
    @{ Name = 'excluded assembly cannot pass'; Lines = 850; LineTotal = 1000; Branches = 600; BranchTotal = 1000; Ui = $false; Pass = $false },
    @{ Name = 'impossible count'; Lines = 1001; LineTotal = 1000; Branches = 600; BranchTotal = 1000; Ui = $true; Pass = $false },
    @{ Name = 'empty denominator'; Lines = 0; LineTotal = 0; Branches = 600; BranchTotal = 1000; Ui = $true; Pass = $false }
)
foreach ($case in $cases) {
    $path = [IO.Path]::GetTempFileName()
    try {
        $ui = if ($case.Ui) { '<package name="Hermes.UI"/>' } else { '' }
        $xml = @"
<coverage lines-covered="$($case.Lines)" lines-valid="$($case.LineTotal)" branches-covered="$($case.Branches)" branches-valid="$($case.BranchTotal)">
  <packages><package name="Hermes.Core"/><package name="Hermes.Service"/><package name="Hermes.Servicing"/>$ui</packages>
</coverage>
"@
        [IO.File]::WriteAllText($path, $xml, [Text.UTF8Encoding]::new($false))
        $output = & pwsh -NoLogo -NoProfile -NonInteractive -File $gate -ReportPath $path 2>&1
        if (($LASTEXITCODE -eq 0) -ne $case.Pass) {
            throw "Coverage control failed: $($case.Name): $($output -join [Environment]::NewLine)"
        }
    }
    finally {
        [IO.File]::Delete($path)
    }
}
Write-Output "$($cases.Count) exact coverage-gate controls passed."
