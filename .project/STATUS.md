# Hermes - Project Status

> **Canonical status hub.** Updated 2026-08-27 for the V5 stabilization transition.

## Current State

| Metric | Value |
|--------|-------|
| Runtime | .NET 9, F#, ASP.NET Core |
| Architecture | Pipeline V5 - declarative DAG, per-stage tables, phase-based GPU scheduling |
| Tests | 867 .NET (857 passed, 10 skipped in latest CI) + 21 registered Playwright |
| Baseline | `main` at `a11686d` |
| Pipeline | ingest -> extract -> triage; embed after extract; deep comprehension for gated documents |
| Storage | File-first structured archive; SQLite holds metadata, workflow state, FTS5, and vectors |
| Sources | Gmail, Outlook/Graph, and watched folders |
| UI | React 19 web app with Home, Pipeline, Documents, Search, Chat, Settings, and onboarding |
| UI classification | React web UI + Windows Tray: preview, canonical target |
| Excluded UI | Blazor, MAUI Windows/Mac, and obsolete installers |
| Agent workflow | Squad 0.11.0 enabled |

## Active Work

**V5 Stabilization** - evidence-led rebaseline, issue reconciliation, privacy-safe
current-main validation, and bounded follow-on work.

See: [wave-v5-stabilization.md](waves/wave-v5-stabilization.md)

## Roadmap

| Priority | Item | State |
|----------|------|-------|
| Red | Phase 0 rebaseline and stale-issue reconciliation | Active |
| Red | V5 reflow and file-safe reclassification | Blocked on Phase 0 evidence |
| Red | File-first FTS, semantic/hybrid, and Osprey MCP | Blocked on Phase 1A contracts |
| Red | Local HTTP/MCP trust boundary | Loopback + allowlist + authenticated mutation |
| Red | Coverage | 65% line observed; wave close requires 85% line / 60% branch |
| Yellow | Canonical React/Tray asset and smoke-test path | Phase 1D |
| Green | Packaging, MAUI, and installer promotion | Deferred |

## Key References

| Doc | Purpose |
|-----|---------|
| [30](design/30-pipeline-v5-architecture.md) | Current Pipeline V5 architecture |
| [24](design/24-comprehension-stage.md) | Two-phase triage and comprehension |
| [28](design/28-file-first-archive.md) | File-first archive and SQLite boundary |
| [29](design/29-household-onboarding.md) | Target household onboarding |
| [V5 stabilization wave](waves/wave-v5-stabilization.md) | Active tasks, decisions, evidence, journal |
| [Testing register](testing-register.md) | Automated-test catalog |

## Blockers

- CI is red at 65% line coverage. Phase 0 must establish truthful branch coverage,
  skip behavior, production reachability, and archive-integrity evidence before Phase 1A.
