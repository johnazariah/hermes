# Hermes - Project Status

> **Canonical status hub.** Updated 2026-08-12 from `main` at `a11686d`.

## Current State

| Metric | Value |
|--------|-------|
| Runtime | .NET 9, F#, ASP.NET Core |
| Architecture | Pipeline V5 - declarative DAG, per-stage tables, phase-based GPU scheduling |
| Tests | 867 .NET (857 passed, 10 skipped) + 21 Playwright |
| Branch | `main` |
| Pipeline | ingest -> extract -> triage; embed after extract; deep comprehension for gated documents |
| Storage | File-first structured archive; SQLite holds metadata, workflow state, FTS5, and vectors |
| Sources | Gmail, Outlook/Graph, and watched folders |
| UI | React 19 web app with Home, Pipeline, Documents, Search, Chat, Settings, and onboarding |
| Delivery | Windows tray, Windows/macOS MAUI shell, cross-platform CI |
| Agent workflow | Squad 0.11.0 enabled |

## Active Work

No feature wave is active. Pipeline V5, file-first storage, the current React UX, CI repair, and Squad enablement are on `main`.

See: [wave-v5-platform.md](waves/wave-v5-platform.md)

## Roadmap

| Priority | Item | State |
|----------|------|-------|
| Red | Raise line coverage from 65% to the enforced 75% threshold | Blocking green CI |
| Red | Validate V5 and file-first migration against the live archive | Required before broad rollout |
| Red | Replace legacy reclassify/reextract behavior with file-first V5 reflow | Prevent archive and completion drift |
| Red | Osprey integration through MCP | Depends on live comprehension validation |
| Yellow | Structured household profile and eight-step onboarding | Designed, not implemented |
| Yellow | Search/chat and desktop-shell smoke testing with live data | Automated surfaces exist |
| Green | Archive rebuild tooling and legacy `unclassified/` migration | Planned |

## Key References

| Doc | Purpose |
|-----|---------|
| [30](design/30-pipeline-v5-architecture.md) | Current Pipeline V5 architecture |
| [24](design/24-comprehension-stage.md) | Two-phase triage and comprehension |
| [28](design/28-file-first-archive.md) | File-first archive and SQLite boundary |
| [29](design/29-household-onboarding.md) | Target household onboarding |
| [Testing register](testing-register.md) | Current automated-test catalog |

## Blockers

- CI line coverage is 65%, below the workflow's 75% threshold. Tests and platform builds otherwise pass.
