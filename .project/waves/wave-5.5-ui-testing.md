# Wave 5.5: UI Testing

> Status: **Done**  
> Agent prompt: `.github/prompts/wave5.5-ui-testing.prompt.md`  
> Design reference: [15-rich-ui.md](../design/15-rich-ui.md)

## Goal

Comprehensive automated UI testing at three layers: ViewModel (business logic), Avalonia.Headless (control wiring), and manual smoke test checklist.

## Tasks

| # | Task | Layer | Tests | Status |
|---|------|-------|-------|--------|
| VM | ViewModel tests | ShellViewModel with fakes | 18 tests (constructor, refresh, send, reminders, nav, events) | ✅ Done |
| HL | Headless tests | Avalonia.Headless.XUnit | 36 tests (controls, wizard, dialogs) | ✅ Done |
| DLG | Dialog builder extraction | ShellWindow.axaml.cs refactor | 16 dialog tests enabled by extraction | ✅ Done |
| SM | Smoke test checklist | Manual | 40+ items across launch, funnel, library, chat, settings, errors | ✅ Created |

## Log

### April 6, 2026 — Review PASS
- 817 total tests across 3 projects, 0 failures
- ViewModel tests: EXCELLENT — real DB, real state changes, silver thread tested
- Headless tests: GOOD — control existence + dialog structure verified via [AvaloniaFact]
- Dialog builders extracted from ShellWindow.axaml.cs for testability (best practice)
- Smoke test checklist created at .github/prompts/smoke-test.prompt.md
- Minor gap: no button click simulation in headless (existence verified, not interaction)
- Minor gap: no settings save→reload round-trip test

### April 4-6, 2026 — Implementation
- New project Hermes.Tests.App (C#): 18 ViewModel tests (698 lines)
- New project Hermes.Tests.UI (C#): 36 Avalonia headless tests (730 lines)
- ShellWindow.axaml.cs: dialog builders extracted (+119/-52 lines)
- TestAppBuilder.cs: Avalonia headless configuration
- smoke-test.prompt.md: manual walkthrough checklist
