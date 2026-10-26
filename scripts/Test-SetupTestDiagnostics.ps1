#Requires -Version 7.0
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$reader = Join-Path $PSScriptRoot 'Write-SetupTestDiagnostics.ps1'
$scratch = Join-Path $root ('.artifacts/setup-test-diagnostic-controls-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $scratch
$report = Join-Path $scratch 'tests.trx'
$since = [DateTime]::UtcNow.AddMinutes(-1)
$row = 'HERMES_SETUP_DIAGNOSTIC_ASSERT v=1 bounded=7 operation=1 kind=3 operationKind=0 observed=1 stdout=5 stderr=5 exit=5 fault=0 operationFault=0 initiating=0 checks=0 closed=0 exited=1 kills=0'
$family = 'Every_diagnostic_record_is_consumed_and_semantically_bound'
$class = 'Hermes.Setup.Tests.SetupChildTransportTests'
$private = 'SYNTHETIC_PRIVATE_PATH_TOKEN_THEORY_ARGUMENT'
$script:count = 0

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-Report([string]$Text = $row, [string]$Method = $family, [string]$ClassName = $class, [string]$Outcome = 'Failed') {
    $escaped = [Security.SecurityElement]::Escape($Text)
    return @"
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <TestDefinitions><UnitTest id="a"><TestMethod className="$ClassName" name="$Method" /></UnitTest></TestDefinitions>
  <Results><UnitTestResult testId="a" outcome="$Outcome" testName="$private"><Output><ErrorInfo><Message>$private</Message></ErrorInfo><StdOut>$private</StdOut><StdErr>$private</StdErr></Output></UnitTestResult></Results>
  <ResultSummary><Output><StdOut>$private
$escaped
$private</StdOut><StdErr>$private</StdErr></Output></ResultSummary>
</TestRun>
"@
}

function Check([string]$Name, [string]$Xml, [string[]]$Expected, [bool]$Stale = $false) {
    [IO.File]::WriteAllText($report, $Xml, [Text.UTF8Encoding]::new($false))
    if ($Stale) { [IO.File]::SetLastWriteTimeUtc($report, $since.AddMinutes(-1)) }
    $actual = @(& $reader -ReportPath $report -NotBeforeUtc $since *>&1)
    Require ($actual.Count -eq $Expected.Count) "$Name output count failed."
    for ($i = 0; $i -lt $actual.Count; $i++) {
        Require ([string]$actual[$i] -ceq $Expected[$i]) "$Name closed output failed."
    }
    $script:count++
}

function Unavailable([int]$Reason) { return "HERMES_SETUP_DIAGNOSTIC_EVIDENCE v=1 unavailable=$Reason" }

try {
    $missing = @(& $reader -ReportPath $report -NotBeforeUtc $since *>&1)
    Require ($missing.Count -eq 1 -and $missing[0] -ceq (Unavailable 1)) 'Missing report failed.'
    $script:count++
    Check 'valid' (New-Report) @($row)
    Push-Location -LiteralPath $scratch
    try {
        $relative = @(& $reader -ReportPath 'tests.trx' -NotBeforeUtc $since *>&1)
        Require ($relative.Count -eq 1 -and $relative[0] -ceq $row) 'PowerShell-relative report resolution failed.'
        $script:count++
    } finally {
        Pop-Location
    }
    Check 'absent' (New-Report $private) @((Unavailable 5))
    Check 'stale' (New-Report) @((Unavailable 2)) $true
    Check 'malformed XML' '<TestRun>' @((Unavailable 4))
    Check 'DTD' ('<!DOCTYPE TestRun [<!ENTITY private SYSTEM "file:///not-read">]>' + (New-Report)) @((Unavailable 4))
    Check 'foreign method' (New-Report -Method 'Temporary_owned_diagnostic_visibility_probe') @((Unavailable 3))
    Check 'foreign class' (New-Report -ClassName 'Foreign.SetupChildTransportTests') @((Unavailable 3))
    Check 'forged display name' ((New-Report -Method 'foreign').Replace("testName=`"$private`"", "testName=`"$class.$family`"")) @((Unavailable 3))
    Check 'unfailed method' (New-Report -Outcome 'Passed') @((Unavailable 3))
    $unrelatedFailure = (New-Report -Outcome 'Passed').Replace('</TestDefinitions>', '<UnitTest id="b"><TestMethod className="foreign" name="foreign" /></UnitTest></TestDefinitions>')
    Check 'only unrelated failure' ($unrelatedFailure.Replace('</Results>', '<UnitTestResult testId="b" outcome="Failed" /></Results>')) @((Unavailable 3))
    Check 'noncanonical outcome' (New-Report -Outcome 'failed') @((Unavailable 3))
    Check 'foreign namespace' ((New-Report).Replace('TeamTest/2010', 'TeamTest/2099')) @((Unavailable 6))
    Check 'missing definition' ((New-Report).Replace('testId="a"', 'testId="b"')) @((Unavailable 6))
    Check 'duplicate definition ID' ((New-Report).Replace('</TestDefinitions>', '<UnitTest id="a"><TestMethod className="foreign" name="foreign" /></UnitTest></TestDefinitions>')) @((Unavailable 6))
    Check 'duplicate result ID' ((New-Report).Replace('</Results>', '<UnitTestResult testId="a" outcome="Passed" /></Results>')) @((Unavailable 6))
    Check 'ambiguous method' ((New-Report).Replace('</UnitTest>', '<TestMethod className="foreign" name="foreign" /></UnitTest>')) @((Unavailable 6))
    Check 'duplicate summary' ((New-Report).Replace('</TestRun>', '<ResultSummary /></TestRun>')) @((Unavailable 6))
    Check 'duplicate carrier' ((New-Report).Replace('</StdOut><StdErr>', '</StdOut><StdOut>unknown</StdOut><StdErr>')) @((Unavailable 6))
    Check 'missing carrier' ((New-Report).Replace("<StdOut>$private`n$row`n$private</StdOut>", '')) @((Unavailable 5))
    Check 'wrong carrier' (New-Report $private).Replace("<StdErr>$private</StdErr>", "<StdErr>$row</StdErr>") @((Unavailable 5))
    Check 'nested carrier' ((New-Report).Replace($row, "<nested>$row</nested>")) @((Unavailable 6))
    Check 'mixed invalid and valid' (New-Report ("$row`n$row extra=0")) @((Unavailable 6))
    Check 'private suffix' (New-Report ("$row $private")) @((Unavailable 6))
    Check 'private prefix' (New-Report ("$private $row")) @((Unavailable 5))
    Check 'unknown fields' (New-Report ("$row other=0")) @((Unavailable 6))
    Check 'missing field' (New-Report ($row.Replace(' kills=0', ''))) @((Unavailable 6))
    Check 'duplicate field' (New-Report ("$row kills=0")) @((Unavailable 6))
    Check 'wrong order' (New-Report ($row.Replace('bounded=7 operation=1', 'operation=1 bounded=7'))) @((Unavailable 6))
    Check 'unknown version' (New-Report ($row.Replace('v=1', 'v=2'))) @((Unavailable 6))
    Check 'leading zero' (New-Report ($row.Replace('bounded=7', 'bounded=07'))) @((Unavailable 6))
    Check 'positive sign' (New-Report ($row.Replace('bounded=7', 'bounded=+7'))) @((Unavailable 6))
    Check 'negative zero' (New-Report ($row.Replace('fault=0', 'fault=-0'))) @((Unavailable 6))
    Check 'overflow' (New-Report ($row.Replace('bounded=7', 'bounded=99999999999999999999'))) @((Unavailable 6))
    Check 'Unicode digit' (New-Report ($row.Replace('bounded=7', "bounded=$([char]0x0667)"))) @((Unavailable 6))
    Check 'trailing space' (New-Report ("$row ")) @((Unavailable 6))
    Check 'oversize line' (New-Report ($row + ('0' * 513))) @((Unavailable 6))
    Check 'max rows' (New-Report ((@($row) * 32) -join "`n")) (@($row) * 32)
    Check 'excess rows' (New-Report ((@($row) * 33) -join "`n")) @((Unavailable 6))
    $boundedXml = New-Report
    $padding = 'x' * (8MB - [Text.Encoding]::UTF8.GetByteCount($boundedXml))
    $boundedXml = $boundedXml.Replace('</TestRun>', "<!--$($padding.Substring(7))--></TestRun>")
    Require ([Text.Encoding]::UTF8.GetByteCount($boundedXml) -eq 8MB) 'Byte-limit control construction failed.'
    Check 'max bytes' $boundedXml @($row)
    Check 'excess bytes' ($boundedXml + ' ') @((Unavailable 6))
    foreach ($spec in @(
        @('bounded', 0, 7), @('operation', 0, 7), @('kind', 0, 5), @('operationKind', 0, 5),
        @('observed', 0, 1), @('stdout', -1, 7), @('stderr', -1, 7), @('exit', -1, 7),
        @('fault', -1, 11), @('operationFault', -1, 11), @('initiating', -1, 11),
        @('checks', 0, 15), @('closed', 0, 3), @('exited', 0, 1), @('kills', 0, 2)
    )) {
        foreach ($value in @($spec[1], $spec[2], ($spec[1] - 1), ($spec[2] + 1))) {
            $changed = [regex]::Replace($row, "(?<= )$($spec[0])=-?[0-9]+(?= |\z)", "$($spec[0])=$value")
            $expected = if ($value -ge $spec[1] -and $value -le $spec[2]) { $changed } else { Unavailable 6 }
            Check 'canonical field bounds' (New-Report $changed) @($expected)
        }
    }
    Write-Output "$script:count private Setup diagnostic-reader controls passed."

    # Execute the actual YAML run block with synthetic dotnet/reader commands.
    # No real Setup, forced-failure test, environment switch or service is involved.
    $ci = [IO.File]::ReadAllText((Join-Path $root '.github/workflows/ci.yml'))
    $match = [regex]::Match($ci, '(?ms)^      - name: Test portable Setup and installer authority\n        shell: pwsh\n        run: \|\n(?<run>.*?)(?=\n      - name:)')
    Require $match.Success 'Portable Setup CI block was not found.'
    $run = [regex]::Replace($match.Groups['run'].Value, '(?m)^          ', '')
    Require ([regex]::IsMatch($run, '(?m)^dotnet test tests/Hermes.Setup.Tests/Hermes.Setup.Tests.csproj --no-build --no-restore -c Release -v q --logger ''trx;LogFileName=tests.trx'' --results-directory .artifacts/setup-test-evidence\n\$setupTestExitCode = \$LASTEXITCODE\n')) 'Quiet Setup arguments and immediate exit capture changed.'
    $null = New-Item -ItemType Directory -Path (Join-Path $scratch 'scripts')
    $stub = Join-Path $scratch 'scripts/Write-SetupTestDiagnostics.ps1'
    $launcher = Join-Path $scratch 'ci-control.ps1'
    $flowCount = 0
    foreach ($case in @(
        @{ Setup = 0; Installer = 0; Reader = "throw 'must not run'"; Exit = 0; Calls = 'setup,installer' },
        @{ Setup = 0; Installer = 17; Reader = "throw 'must not run'"; Exit = 17; Calls = 'setup,installer' },
        @{ Setup = 23; Installer = 0; Reader = "Write-Output '$row'"; Exit = 23; Calls = 'setup' },
        @{ Setup = 29; Installer = 0; Reader = "throw '$private'"; Exit = 29; Calls = 'setup' },
        @{ Setup = 31; Installer = 0; Reader = 'exit 99'; Exit = 31; Calls = 'setup' }
    )) {
        [IO.File]::WriteAllText($stub, $case.Reader)
        $preamble = @"
Set-Location -LiteralPath '$($scratch.Replace("'", "''"))'
`$global:LASTEXITCODE = 0
function dotnet {
    if (`$args[1] -like '*Hermes.Setup.Tests*') {
        Add-Content -LiteralPath calls.txt -Value setup
        if (Test-Path -LiteralPath .artifacts/setup-test-evidence/tests.trx) { exit 97 }
        `$global:LASTEXITCODE = $($case.Setup)
    } else {
        Add-Content -LiteralPath calls.txt -Value installer
        `$global:LASTEXITCODE = $($case.Installer)
    }
}
"@
        [IO.File]::WriteAllText($launcher, $preamble + "`n" + $run)
        $calls = Join-Path $scratch 'calls.txt'
        if (Test-Path -LiteralPath $calls) { Remove-Item -LiteralPath $calls }
        $null = New-Item -ItemType Directory -Force -Path (Join-Path $scratch '.artifacts/setup-test-evidence')
        [IO.File]::WriteAllText((Join-Path $scratch '.artifacts/setup-test-evidence/tests.trx'), $private)
        $sibling = Join-Path $scratch '.artifacts/setup-test-evidence/keep.private'
        [IO.File]::WriteAllText($sibling, $private)
        $actual = @(& pwsh -NoLogo -NoProfile -NonInteractive -File $launcher *>&1)
        $exitCode = $LASTEXITCODE
        Require ($exitCode -eq $case.Exit) 'CI original exit preservation failed.'
        Require ([IO.File]::ReadAllText($sibling) -ceq $private) 'CI must clean only the exact previous report.'
        Require (([IO.File]::ReadAllLines($calls) -join ',') -ceq $case.Calls) 'CI following invocation policy failed.'
        $expectedOutput = if ($case.Setup -eq 23) { @($row) } elseif ($case.Setup -eq 29) { @((Unavailable 7)) } else { @() }
        Require (($actual -join "`n") -ceq ($expectedOutput -join "`n")) 'CI privacy/closed fallback failed.'
        $flowCount++
    }
    Write-Output "$flowCount actual CI-block exit/following-command controls passed (synthetic commands only)."
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
