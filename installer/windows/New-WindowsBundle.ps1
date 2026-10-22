#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ServicingBuildPath,
    [Parameter(Mandatory = $true)][string]$PackageDirectory,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Bundle staging is restricted to the disposable hosted Windows build.'
}
$build = Get-Content -LiteralPath $ServicingBuildPath -Raw | ConvertFrom-Json
$sourceRoot = [IO.Path]::GetDirectoryName($build.setupExecutable)
$output = [IO.Path]::GetFullPath($OutputPath)
$workspace = [IO.Path]::GetFullPath($env:GITHUB_WORKSPACE).TrimEnd('\') + '\'
if (-not $output.StartsWith($workspace, [StringComparison]::OrdinalIgnoreCase)) { throw 'Distribution staging must remain outside AppRoot in the disposable workspace.' }
if (Test-Path -LiteralPath $output) { throw 'Refusing to replace an existing distribution archive.' }
$staging = Join-Path ([IO.Path]::GetDirectoryName($output)) ('bundle-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($staging) | Out-Null
foreach ($file in $build.setupFiles) {
    if ($file.path -match '(^|/)\.\.?(/|$)|[:\\]' -or $file.path.StartsWith('/')) { throw 'Invalid staged coordinator identity.' }
    $source = Join-Path $sourceRoot $file.path
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256 `
        -or (Get-Item -LiteralPath $source).Length -ne $file.length) { throw 'Coordinator media changed after publication.' }
    $target = Join-Path $staging $file.path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($target)) | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw 'Staged coordinator bytes differ from the verified publication.' }
}
foreach ($name in @('Hermes.Installer.msi', 'payload-inventory.json')) {
    Copy-Item -LiteralPath (Join-Path $PackageDirectory $name) -Destination (Join-Path $staging $name)
}
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $output, [IO.Compression.CompressionLevel]::Optimal, $false)
Write-Output $output
