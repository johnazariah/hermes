# Hermes Windows Installer Build

## Prerequisites
- .NET 10 SDK
- WiX Toolset v4+ (`dotnet tool install --global wix`)

## Build Steps

```powershell
# 1. Publish the app (self-contained, single-file)
dotnet publish src/Hermes.App/Hermes.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -o installer/windows/publish

# 2. Build the MSI
dotnet build installer/windows/Hermes.Installer.wixproj `
  -p:PublishDir=publish/ `
  -c Release

# Output: installer/windows/bin/Release/Hermes.Installer.msi
```

## What the installer does
- Installs to `%LOCALAPPDATA%\Hermes\App\`
- Creates Start Menu shortcut
- Registers auto-start via HKCU Run key (launches on login)
- Per-user install (no admin required)
- Supports upgrade (MajorUpgrade — uninstalls old, installs new)

## Manual build (without WiX)
If WiX is not available, you can distribute the published folder directly:
```powershell
dotnet publish src/Hermes.App/Hermes.App.csproj -c Release -r win-x64 --self-contained -o dist/win-x64
# Then zip dist/win-x64 and distribute
```
