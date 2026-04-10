#!/usr/bin/env pwsh
# Reset all documents to unclassified for LLM re-classification
# This moves all files back to unclassified/ and updates the DB

$ErrorActionPreference = "Stop"
$archiveDir = "C:\Users\johnaz\Documents\Hermes"
$dbPath = Join-Path $archiveDir "db.sqlite"
$unclassifiedDir = Join-Path $archiveDir "unclassified"

Write-Host "Archive: $archiveDir" -ForegroundColor Cyan
Write-Host "DB: $dbPath" -ForegroundColor Cyan

# Ensure unclassified dir exists
New-Item -ItemType Directory -Path $unclassifiedDir -Force | Out-Null

# Get all category subdirectories (excluding unclassified)
$categories = Get-ChildItem $archiveDir -Directory |
    Where-Object { $_.Name -ne "unclassified" -and $_.Name -ne ".hermes" } |
    Select-Object -ExpandProperty Name

Write-Host "`nCategories to reset:" -ForegroundColor Yellow
foreach ($cat in $categories) {
    $catDir = Join-Path $archiveDir $cat
    $fileCount = (Get-ChildItem $catDir -File -ErrorAction SilentlyContinue | Measure-Object).Count
    Write-Host "  $cat ($fileCount files)"
}

# Move all files to unclassified
$moved = 0
foreach ($cat in $categories) {
    $catDir = Join-Path $archiveDir $cat
    $files = Get-ChildItem $catDir -File -ErrorAction SilentlyContinue
    foreach ($f in $files) {
        $dest = Join-Path $unclassifiedDir $f.Name
        if (Test-Path $dest) {
            # Conflict: add category prefix
            $dest = Join-Path $unclassifiedDir "$($cat)_$($f.Name)"
        }
        Move-Item -LiteralPath $f.FullName -Destination $dest -Force
        $moved++
    }
}

Write-Host "`nMoved $moved files to unclassified/" -ForegroundColor Green

# Update DB: reset all categories to 'unclassified' and clear classification metadata
# Using sqlite3 CLI if available, otherwise use dotnet
$sqliteExe = Get-Command sqlite3 -ErrorAction SilentlyContinue

if ($sqliteExe) {
    $sql = @"
UPDATE documents SET
    category = 'unclassified',
    classification_tier = NULL,
    classification_confidence = NULL,
    saved_path = 'unclassified/' || original_name
WHERE category NOT IN ('unclassified');
"@
    $sql | sqlite3 $dbPath
    Write-Host "DB updated via sqlite3" -ForegroundColor Green
} else {
    Write-Host "sqlite3 CLI not found — using F# script to update DB" -ForegroundColor Yellow
    # Write a simple update script
    $fsx = @"
#r "tests/Hermes.Tests/bin/Debug/net9.0/Microsoft.Data.Sqlite.dll"
open Microsoft.Data.Sqlite
let conn = new SqliteConnection("Data Source=$($dbPath.Replace('\','\\'))")
conn.Open()
let cmd = conn.CreateCommand()
cmd.CommandText <- "UPDATE documents SET category = 'unclassified', classification_tier = NULL, classification_confidence = NULL WHERE category NOT IN ('unclassified')"
let rows = cmd.ExecuteNonQuery()
printfn "Updated %d rows" rows
conn.Close()
"@
    $fsx | Set-Content "$env:TEMP\hermes-reset.fsx"
    Push-Location (Split-Path $dbPath -Parent | Split-Path -Parent)
    dotnet fsi "$env:TEMP\hermes-reset.fsx"
    Pop-Location
}

Write-Host "`nDone! Restart the service to begin LLM classification." -ForegroundColor Green
Write-Host "At ~30 docs/min, $moved docs will take ~$([math]::Ceiling($moved / 30)) minutes." -ForegroundColor DarkGray
