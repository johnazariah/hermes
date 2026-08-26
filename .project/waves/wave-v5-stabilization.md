# Wave V5 Stabilization

> **Status:** ⏳ Active
> **Started:** 2026-08-27
> **Baseline:** `a11686d`
> **Architecture:** [30-pipeline-v5-architecture.md](../design/30-pipeline-v5-architecture.md)
> **Preservation:** `7604b3f` on `preserve/pre-v5-stabilization-20260827`

## Goal

Re-establish trustworthy V5 project state before implementation resumes: preserve the
owner's documentation work, reconcile stale issues, capture a privacy-safe current-main
baseline, declare supported product surfaces, and turn every proven gap into bounded
follow-on work.

## Sources of Truth

- This wave owns active tasks, decisions, evidence, and the daily journal.
- GitHub issues own actionable backlog and acceptance criteria.
- [`STATUS.md`](../STATUS.md) is the concise project-state hub.
- [`testing-register.md`](../testing-register.md) owns the test inventory.
- Design documents describe architecture, not task status.

## Confirmed Decisions

| Topic | Decision |
|-------|----------|
| Canonical UI | React served by `Hermes.Service`; Windows Tray is the supported native shell |
| Other UI surfaces | Blazor, MAUI Windows/Mac, and obsolete installers are excluded pending promotion evidence |
| Baseline data | Synthetic fixtures plus an owner-approved, de-identified disposable archive copy |
| MCP trust boundary | Loopback-only, explicit origin allowlist, mutating tools disabled by default and authenticated when enabled |
| SSE | Keep chat SSE with `ChatPane` and `ChatPage`; remove the unused pipeline-status hook in implementation |
| Coverage close gate | 85% line and 60% branch coverage; do not lower CI thresholds |

## Tasks

| Phase | Task | Owner | Status | Evidence / dependency |
|-------|------|-------|--------|-----------------------|
| 0 | Preserve dirty documentation/governance state | Owner / Keaton | ✅ Done | Preservation commit above; owner checkout unchanged |
| 0 | Land curated V5 documentation baseline | Keaton | ✅ Done | `7d7d01b` |
| 0 | Activate stabilization wave and transition STATUS | Kobayashi / Scribe | ⏳ Active | This file and STATUS transition |
| 0 | Reconcile stale phase issues with proof | Kobayashi / Hockney | ⬜ Pending | Requires this wave URL |
| 0 | Capture redacted current-main baseline | Hockney / Fenster / McManus / Verbal / Rai | ⬜ Pending | Synthetic and disposable data only |
| 0 | Reconcile testing register and support matrix | Hockney / Keaton | ⬜ Pending | Requires baseline |
| 1A | Stabilize V5 reflow and reclassification | McManus / Hockney | ⬜ Blocked | Phase 0 evidence and issue approval |
| 1B | Restore file-first FTS, semantic/hybrid, and Osprey reachability | McManus / Hockney | ⬜ Blocked | Phase 1A contracts |
| 1C | Enforce local HTTP/MCP trust boundary | McManus / Keaton / Rai | ⬜ Blocked | Final MCP contract |
| 1D | Stabilize canonical React/Tray assets and SSE | Verbal / Fenster / Hockney | ⬜ Blocked | Trust-boundary browser policy |
| 1E | Reach 85% line / 60% branch coverage and close wave | Hockney / Fenster | ⬜ Blocked | Phase 1A-1D |

## Phase 0 Acceptance Gates

1. The owner checkout is unchanged and recoverable from the preservation branch.
2. Every stale issue is superseded, rewritten, or split with evidence and successor links.
3. Baseline evidence is read-only, reproducible, redacted, and records PASS/FAIL/UNKNOWN.
4. Source archive hashes are unchanged; no provider sync or external upload occurs.
5. Test counts, skips, non-asserting cases, and coverage agree with the testing register.
6. Supported, preview, and excluded surfaces are explicit.
7. Phase 1A begins only from a reviewed issue with the baseline linked.

## Log

### 2026-08-27 - Phase 0 preservation and activation

- **Changed:** Preserved the user-owned dirty documentation/governance state and incorporated it as the V5 documentation baseline.
- **Evidence:** `7604b3f` on the preservation branch; `7d7d01b` on the stabilization branch.
- **Validation:** Preservation diff checked for whitespace and credential-like content; `C:\work\hermes` status remained unchanged.
- **Blockers:** Baseline and issue reconciliation remain.
- **Next:** Reconcile the stale issue set, then capture the privacy-safe current-main baseline.
