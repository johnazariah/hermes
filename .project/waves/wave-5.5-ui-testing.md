# Wave 5.5: UI Testing

> Status: **Done**  
> Agent prompt: `.github/prompts/wave5.5-ui-testing.prompt.md`  
> Design reference: [15-rich-ui.md](../design/15-rich-ui.md)

## Goal

Comprehensive automated UI testing at three layers: ViewModel (business logic), Avalonia.Headless (control wiring), and manual smoke test checklist.

## Tasks

| # | Task | Layer | Tests | Status |
|---|------|-------|-------|--------|
| VM | ViewModel tests (12 tests) | ShellViewModel with fakes | Constructor, RefreshAsync, SendMessage, Reminders, Chat, PropertyChanged | Done |
| HL | Headless tests (8 tests) | Avalonia.Headless.XUnit | Funnel sections, 3-column layout, chat controls, toggles, processing visibility | Done |
| SM | Smoke test checklist | Manual | 40+ items across launch, funnel, library, chat, settings, errors | Done |

## Log

(newest on top)
