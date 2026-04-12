# Wave v2a: Service Deployment

## Summary

Running `dotnet run` from the source tree locks build output DLLs, preventing development while the service runs. This wave publishes Hermes as a self-contained deployment to a dedicated install directory and registers it as an OS-level service (Windows service via `sc.exe`/`ServiceInstaller.fs`, macOS launchd agent). The source tree is then free for builds, the service survives reboots, and upgrades follow a simple stop → publish → start cycle.

## Goals

- **Unblock concurrent development**: developer can `dotnet build` / `dotnet test` while the service runs from a separate install directory
- **Survive reboots**: service starts automatically on login (Windows service, macOS LaunchAgent)
- **Run as current user**: service needs access to user's filesystem (archive at `~/Documents/Hermes/`), config (`%APPDATA%\hermes\` / `~/.config/hermes/`), and Gmail OAuth tokens
- **Simple upgrade path**: stop service → `dotnet publish` to install dir → start service, scripted in one command
- **Cross-platform install scripts**: `scripts/install.ps1` (Windows) and `scripts/install.sh` (macOS) handle publish + service registration
- **Preserve existing config/data**: config YAML, OAuth tokens, archive directory, and SQLite DB are untouched by install/upgrade — they live outside the install directory

## Non-Goals

- **Auto-update mechanism**: no self-updating service, no update-check endpoint. Upgrades are manual (scripted).
- **Multi-user support**: service runs as the installing user only; no system-wide daemon mode.
- **Linux support**: not targeted in this wave. Scripts and service registration are Windows + macOS only.
- **Installer UI**: no graphical installer (MSI, DMG, etc.). Scripts only.
- **Docker/container deployment**: out of scope.
- **Service health monitoring or watchdog**: beyond basic OS-level restart-on-failure.

## Target Domains

| Domain | Status | Relationship | Role in This Feature |
|--------|--------|-------------|---------------------|
| deployment | **NEW** | **create** | Install scripts, publish configuration, service registration |
| platform | existing | **modify** | ServiceInstaller.fs and SleepGuard.fs already exist — extend/wire for production deployment |
| config | existing | **consume** | Config paths (`%APPDATA%\hermes\`, `~/.config/hermes/`) are read; no changes to config schema |

### New Domain Sketches

#### deployment [NEW]
- **Purpose**: Encapsulates the build → publish → register → upgrade lifecycle for Hermes as an OS service. Owns the install scripts and publish profiles.
- **Boundary Owns**: install scripts (PS1, shell), publish profiles/targets, service registration logic (sc.exe commands, launchd plist generation), upgrade orchestration (stop → publish → start)
- **Boundary Excludes**: service runtime behaviour (owned by platform/hosting), config schema (owned by config), application logic (owned by core)

## Complexity

- **Score**: CS-2 (small)
- **Total P**: 3 → CS-2
- **Confidence**: 0.85

## Acceptance Criteria

1. **Publish produces runnable output**: `dotnet publish -c Release -r win-x64 --self-contained` creates a complete self-contained deployment in the install directory that runs without the .NET SDK installed.

2. **Windows service registration**: Running `scripts/install.ps1` registers Hermes as a Windows service that:
   - Runs as the current user (not LocalSystem)
   - Starts automatically on boot/login
   - Can be started/stopped via `sc.exe start Hermes` / `sc.exe stop Hermes` or `services.msc`
   - Listens on `http://localhost:21741`

3. **macOS launchd registration**: Running `scripts/install.sh` creates a `~/Library/LaunchAgents/com.hermes.service.plist` that:
   - Runs as the current user
   - Starts automatically on login
   - Can be controlled via `launchctl load/unload`
   - Listens on `http://localhost:21741`

4. **Source tree is unlocked**: After installing and starting the service, `dotnet build` and `dotnet test` succeed in the source tree without file-locking conflicts.

5. **Config/data separation**: The installed service reads config from `%APPDATA%\hermes\config.yaml` (Windows) / `~/.config/hermes/config.yaml` (macOS) and uses `~/Documents/Hermes/` for the archive. These directories are not inside the install directory.

6. **OAuth tokens work**: Gmail OAuth tokens stored in the config directory are accessible to the installed service (same user context).

7. **Upgrade path works**: Running `scripts/install.ps1` (or `install.sh`) when the service is already installed stops the running service, publishes new binaries over the existing install, and restarts the service. No data loss.

8. **Uninstall path exists**: `scripts/install.ps1 -Uninstall` (or `install.sh --uninstall`) stops the service, deregisters it, and removes the install directory. Config and archive directories are preserved.

9. **Scripts are idempotent**: Running install scripts multiple times produces the same result without errors.

10. **SleepGuard integration**: The Windows service uses the existing `SleepGuard.fs` to prevent sleep during active sync operations.

## Open Questions

1. **Install directory choice (Windows)**: `C:\Program Files\Hermes\` requires elevation. Should we default to `%LOCALAPPDATA%\Hermes\` instead to avoid the UAC prompt?

2. **Service account (Windows)**: Should the service run as the logged-in user (requires password at install), or as `LocalService` with appropriate file/network permissions granted?

3. **Log location**: Where should service logs go? Options: install directory, config directory, or system log (Event Log / syslog).

4. **Frontend build integration**: Is the React frontend (`Hermes.Web`) build already wired into the `dotnet publish` pipeline, or does the install script need to run `npm run build` separately?

## Risks & Assumptions

| Risk | Impact | Mitigation |
|------|--------|------------|
| Windows service as current user requires password at `sc.exe create` time | Install friction | Use `New-Service` PowerShell cmdlet; document clearly |
| Install dir requires elevation to write | Script must run as admin | Consider `%LOCALAPPDATA%\Hermes\` to avoid elevation |
| macOS launchd environment may lack PATH entries | Service can't find Ollama | Plist includes explicit `EnvironmentVariables` |
| React frontend not included in publish output | UI broken | Ensure `npm run build` runs before `dotnet publish` |

## Workshop Opportunities

| Topic | Type | Why Workshop | Key Questions |
|-------|------|--------------|---------------|
| Install directory & elevation strategy | CLI Flow | Choice between Program Files vs LocalAppData affects script flow and UX | Where to install? Require admin? |
| Windows service identity | Integration Pattern | Running as user vs LocalService has cascading effects on OAuth and file access | Password prompt acceptable? |
