# Windows Service and Tray package

This package targets the canonical .NET 9 `Hermes.Service` and Windows
`Hermes.Tray`, with React served by the Service. The Service is a user process
owned by the Tray, **not** a machine-wide Windows service. MAUI, the former
`Hermes.App` project and macOS installers are excluded.

The [Windows servicing architecture](../../.project/design/33-windows-servicing.md)
defines coordinator-only standard MSI servicing, runtime admission and the fixed
OS-resolved `%LOCALAPPDATA%\Hermes\App` root. Custom relocation is not supported.
Implementation state and actual validation evidence belong in the
[stabilization wave](../../.project/waves/wave-v5-stabilization.md) and
[testing register](../../.project/testing-register.md). Architecture approval
and cross-platform script parsing are not Windows installation evidence.

### Native integration boundaries

Servicing follows the admission, ownership and recovery rules in the
[architecture reference](../../.project/design/33-windows-servicing.md).
Every installed entrypoint participates, including direct Service launches.
The distributed coordinator retains exclusive admission through standard MSI
execution and verifies completion before reopening it. A process snapshot,
Tray-only lease or diagnostic hold cannot replace that protocol.

The external smoke harness targets `net9.0` with an assembly-level Windows
platform annotation and has no Tray assembly reference or Windows Desktop
framework dependency. The production Tray still targets `net9.0-windows`.
Publish both for `win-x64`, self-contained and non-single-file. The harness's
default publish directory is
`tests/Hermes.Tray.Smoke/bin/Release/net9.0/win-x64/publish/`; CI's explicit
`-o` directories are unchanged. The
installed apphost loads its installed `Hermes.Tray.dll`; the existing supervisor,
ownership oracle and React/WebView assertions run inside that application.

```powershell
# Only inside the admitted disposable GitHub-hosted Windows job, after preflight.
$nonce = [Guid]::NewGuid().ToString('N')
$archive = Join-Path $env:RUNNER_TEMP "hermes-archive-$nonce"
$receipt = Join-Path $env:RUNNER_TEMP "hermes-owner-fixture-$nonce.json"
[IO.Directory]::CreateDirectory($archive) | Out-Null
./Hermes.Tray.Smoke.exe `
  --create-owner-fixture $receipt `
  --archive-directory $archive

# Reuse this receipt throughout install, upgrade and explicit rollback.
./Hermes.Tray.Smoke.exe `
  --installed-tray-path 'C:\absolute\installed\tray\Hermes.Tray.exe' `
  --port 31879 `
  --report "$env:RUNNER_TEMP/hermes-native-$nonce.json" `
  --timeout-seconds 300 `
  --ready-file "$env:RUNNER_TEMP/hermes-ready-$nonce.json" `
  --continue-file "$env:RUNNER_TEMP/hermes-continue-$nonce.json" `
  --owner-fixture-receipt $receipt
```

Report and optional paired ready/continue paths must be new absolute paths below
the disposable runner's `RUNNER_TEMP`. The old `--service-path` harness argument
is not supported. Readiness follows installed-file hashes, owned Service
identity and real WebView/canonical-route proof. Continuation is bounded by the
diagnostic deadline and followed by a same-process/start-identity recheck and
graceful stop. **This hold is diagnostic coordination only, not an installer
lock.**

The bounded synthetic report retains `passed`, `cleaned` and `stop`, and adds
`installedTrayLaunched`, `trayExecutablePath`, `trayProcessId`, OS start
identities, installed hashes and UI/ownership evidence. Failure/native evidence
must survive cleanup failures.

The native fixture helper requires the genuine default owner directory to be
absent. It prepares synthetic configuration in a new sibling, publishes it
atomically without replacing any existing directory, and emits a receipt bound
to that GitHub run, attempt and job. Subsequent smoke invocations require the
same receipt and verify the exact fixture inventory and hashes before and after
native proof. Final reports must confirm `ownerStateVerified`,
`ownerFixtureVerified` and the original `ownerFixtureId`.

PowerShell never creates or rewrites the owner configuration itself. Its
preservation baseline includes the configuration, settings, fixture marker and
receipt, and remains unchanged across every MSI transition. The installed
Service still uses separate disposable configuration; it never serves or adopts
the preservation fixture. Neither C# nor PowerShell automatically deletes the
default owner directory. The receipt attests fixture provenance in the admitted
job, not authentication against malicious code running as the same user.

## Build on Windows

Builders need the .NET 9 SDK, Node.js 22 and PowerShell 7. The project restores
the pinned WiX 6 SDK; a global WiX installation is unnecessary. Use a clean
checkout and new output directories. Do not put credentials, local
configuration, archives or diagnostic logs in a publish directory.

```powershell
npm ci --prefix src/Hermes.Web
npm run build --prefix src/Hermes.Web

./installer/windows/Publish-Payload.ps1 `
  -PayloadDirectory "$PWD/publish" `
  -InventoryDirectory "$PWD/.artifacts/publish-inventory" `
  -RuntimeVersion 0.1.0

./installer/windows/Build-Package.ps1 `
  -PayloadDirectory "$PWD/publish" `
  -InventoryDirectory "$PWD/.artifacts/publish-inventory" `
  -PackageVersion 0.1.0 `
  -OutputDirectory "$PWD/.artifacts/package"
```

The MSI is `.artifacts/package/Hermes.Installer.msi`. Its companion
`payload-inventory.json` records the exact installed relative paths and
SHA-256 values. It is build evidence, not permission to add files to a
candidate payload.

`Publish-Inventory.targets` captures the SDK's resolved source paths separately
from the output directory, including hashes captured before another version can
overwrite compiler intermediates. Packaging checks this explicit inventory against
allowed runtime names and build/package source roots, requires essential
Service/Tray/runtime assemblies, compares every output file to its resolved
source hash, admits only the explicitly declared comprehension prompt resources,
and rejects unexpected files or directories. Web files must also match
the independently verified canonical React build and its explicit HTML,
hashed JS/CSS and declared SVG inventory. Rehashing an injected
`credentials.json`, archive document or log does not make it eligible.

Each runtime file has a deterministic component identity derived from its
relative path and an HKCU registry keypath. The package remains per-user.
Normal WiX/MSI validation is enabled; no ICE checks are suppressed.
MSI versions use three numeric fields within `255.255.65535`, without leading
zeroes.

## Install and service

End users need Windows x64 and the WebView2 Evergreen Runtime. The payload is
self-contained: it does not require a .NET SDK or separately installed .NET
runtime. This unsigned preview does not install WebView2, request administrator
rights or change machine-wide service policy.

```powershell
# Coordinator command contract. Quit Hermes before servicing.
Hermes.Setup.exe install --package 'C:\Packages\Hermes.Installer.msi'
Hermes.Setup.exe repair
Hermes.Setup.exe uninstall

# Only when deliberately selecting an older package:
Hermes.Setup.exe install --package 'C:\Packages\older\Hermes.Installer.msi' --allow-rollback

# Interrupted-operation recovery uses retained media, not a replacement root.
Hermes.Setup.exe recover
```

Application files are installed beneath `%LOCALAPPDATA%\Hermes\App\service` and
`tray`. The Start Menu shortcut and HKCU Run entry point to the installed
`tray\Hermes.Tray.exe`; installation does not launch the application.
`--quiet` requests quiet presentation, not permission to stop applications.
`REBOOT=ReallySuppress` prevents an OS restart, not an application shutdown.

Downgrades require the coordinator's explicit `--allow-rollback`; its MSI
correlation/rollback properties alone cannot authorize raw MSI servicing.
Upgrade identity is preserved across package versions. The coordinator refuses
ambiguous/damaged registration or a different root rather than guessing which
product to adopt or remove.

Configuration under `%APPDATA%\hermes`, the configured archive and unknown
files inside the application directory remain user-owned. They are not MSI
components. Uninstall removes only package-owned files, installer registration,
the shortcut and Run entry; empty package directories may disappear, but
nonempty user directories remain.

## Acceptance and release boundaries

`pwsh -NoProfile -File installer/windows/Test-Static.ps1` runs the cross-platform
source controls using existing Node.js, .NET, PowerShell, actionlint and Bash.
It does not install dependencies or invoke Windows Installer.

`Test-Installer.ps1` refuses to run outside a dedicated GitHub-hosted disposable
Windows job. Before creating synthetic profile state or running any MSI action,
it requires an empty related-product inventory, including advertised products,
and no existing Hermes configuration, registration, shortcut or default
application directory. Do not run it on a workstation or shared runner.
This diagnostic isolation is not a production clean-profile requirement:
ordinary nonconflicting owner data and unknown files must be preserved.

Lifecycle acceptance requires distinct Service/Tray payload versions, changed
assembly bytes, and install, upgrade, implicit-downgrade refusal, explicit
rollback and uninstall. Every installed transition checks the complete payload
inventory, ProductCode/version/location, related products, shortcut, Run entry
and synthetic user-data hashes. The installed-Tray smoke interface must prove
the actual installed executable, its owned Service and canonical WebView
content with SDK discovery disabled. Coordinator and raw silent MSI servicing must
refuse while that application is live, without killing or closing it.

The interrupted-operation component independently observes MSI lifecycle events
and manifest-based `Hermes.Servicing.Evidence.Native` diagnostics in the same
Application log. Native reporter PID, event time and freshly observed process
creation identity must agree with the expected custom-action host; JSON writer
claims alone are insufficient. Missing, ambiguous or unsupported evidence
returns incomplete/Blocked rather than a passing report.

`Native-Evidence.psm1` compiles and registers that test-only publisher in a new
owned directory beneath `RUNNER_TEMP`, refuses an existing publisher and
unregisters it during cleanup. This requires the disposable harness's Windows
SDK tools and elevated registration, not an end-user SDK or production
enrolment. The installed application and coordinator remain SDK-free.

Synthetic acceptance JSON and bounded native reports are persisted before
failure cleanup. Cleanup failure is recorded separately and cannot replace the
original failure. CI uploads only those synthetic reports, never raw MSI logs,
configuration, provider state or arbitrary publish directories. Artifacts expire
after three days.

Main CI publishes the Windows MSI only after its blocking lifecycle gate. Manual
release-workflow dispatch builds an artifact only. An operator-pushed `vX.Y.Z`
tag may create a **draft** preview release after quality gates; the workflow
does not create tags or publish a release automatically. Nothing here promotes
a macOS installer or MAUI surface.
