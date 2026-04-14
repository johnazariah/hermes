# Wave v2d: Release Pipeline & Installer

## Summary

Hermes currently requires the source repo, .NET SDK, and npm to install — limiting adoption to developers. End users need a single downloadable installer: double-click, done. This wave delivers GitHub Actions CI/CD that produces MSI (Windows) and DMG/pkg (macOS) installers automatically on tagged releases.

## Goals

- End user downloads a single file (MSI or DMG) from GitHub Releases and installs with a double-click
- Service starts automatically at login with no manual configuration
- CI runs full test suite on every push/PR, blocking merge on failure
- Release workflow fully automated: tag → build → test → package → publish
- Version number flows from git tag through assembly metadata to the UI
- Upgrade preserves config and archive; uninstall removes service but keeps user data
- Developer install scripts remain functional for dev workflows

## Non-Goals

- Auto-update mechanism (manual download from GitHub Releases)
- Linux installer
- Code signing / notarisation (deferred — installs will trigger OS warnings)
- Microsoft Store / Homebrew distribution
- ARM Windows builds

## Target Domains

| Domain | Status | Relationship | Role |
|--------|--------|-------------|------|
| platform | existing | **modify** | Version injection via MSBuild |
| packaging | **NEW** | **create** | WiX MSI, macOS DMG/pkg, CI/CD workflows |
| service | existing | **consume** | Published as self-contained executable |
| web | existing | **consume** | React frontend bundled into wwwroot |

## Complexity

- **Score**: CS-3 (medium), Total P: 6
- **Confidence**: 0.75
- **Phases**:
  1. CI workflow (build + test on push/PR)
  2. Version injection (git tag → assembly → UI)
  3. Windows MSI installer (extend WiX project)
  4. macOS installer (DMG or pkg)
  5. Release workflow (tag → build → package → GitHub Release)
  6. Release prompt & documentation

## Acceptance Criteria

1. **CI on every push/PR**: Build + test on windows-latest and macos-latest. PR blocked on failure.
2. **Tagged release produces installers**: Tag `v*` triggers release workflow → MSI + macOS installer + zips → GitHub Release.
3. **Windows MSI installs without elevation**: Per-user install to `%LOCALAPPDATA%\Hermes\`. No UAC.
4. **MSI registers auto-start**: Task Scheduler task starts service at logon with restart-on-failure.
5. **MSI creates Start Menu shortcut**: Opens `http://localhost:21741` in default browser.
6. **MSI upgrade preserves data**: Stop → replace → start. Config and archive untouched.
7. **MSI uninstall cleans up**: Removes service + install dir. Preserves config + archive.
8. **macOS installer works**: DMG or pkg installs to `~/.local/lib/hermes/`, creates launchd plist.
9. **Version from git tag**: Assembly version matches tag. `--version` reports correctly.
10. **Version visible in UI**: Dashboard footer or settings shows running version.
11. **Release prompt exists**: `.github/prompts/release.prompt.md` with step-by-step checklist.
12. **Self-contained runs without SDK**: Verified on clean machines.

## Open Questions (RESOLVED)

1. **WiX version**: Inspect existing project to determine. Migrate to v4 if needed.
2. **macOS format**: pkg with postinstall script (better for launchd registration).
3. **macOS Intel**: arm64 only for now. Intel users can build from source.
4. **CI test matrix**: Windows on every PR. macOS only on release builds (cost).
5. **Version display**: Dashboard footer.
6. **Pre-release versions**: `0.1.0-dev.{commit-count}` for non-tagged builds.

## Risks & Assumptions

| Risk | Impact | Mitigation |
|------|--------|------------|
| macOS Gatekeeper blocks unsigned pkg | High | Document right-click → Open workaround; plan signing later |
| WiX v3→v4 migration | Medium | Assess first; budget migration time |
| GitHub Actions macOS minute cost (10x) | Low | macOS only on release, not every PR |
| Self-contained publish size (~100MB) | Low | Acceptable for desktop app |

## Workshop Opportunities

| Topic | Type | Key Questions |
|-------|------|---------------|
| WiX MSI structure | Integration | Task Scheduler via WiX? MajorUpgrade + service stop? Component structure? |
| macOS installer & launchd | Integration | File placement? Plist creation? Uninstall without native uninstaller? |
| Release workflow orchestration | CLI Flow | Artifact flow between jobs? Matrix vs sequential? Partial failure handling? |
