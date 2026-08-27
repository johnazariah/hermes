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
| 0 | Activate stabilization wave and transition STATUS | Kobayashi / Scribe | ✅ Done | `fa1a406` |
| 0 | Reconcile stale phase issues with proof | Kobayashi / Hockney | ✅ Done | Closed #1-#5, #7, #10, #12; rewrote #6, #8, #9, #11; created #16, #17 |
| 0 | Capture redacted current-main baseline | Hockney / Fenster / McManus / Verbal / Rai | ✅ Done | Evidence below; personal archive not accessed |
| 0 | Reconcile testing register and support matrix | Hockney / Keaton | ✅ Done | Baseline below and testing register |
| 1A | Stabilize V5 reflow and reclassification | McManus / Hockney | ⬜ Blocked | #17; requires PR #15 review/merge |
| 1B | Restore file-first FTS, semantic/hybrid, and Osprey reachability | McManus / Hockney | ⬜ Blocked | #11 and #6; depends on Phase 1A contracts |
| 1C | Enforce local HTTP/MCP trust boundary | McManus / Keaton / Rai | ⬜ Blocked | #6; depends on final MCP contract |
| 1D | Stabilize canonical React/Tray assets and SSE | Verbal / Fenster / Hockney | ⬜ Blocked | #9 and #8; depends on trust-boundary browser policy |
| 1E | Reach 85% line / 60% branch coverage and close wave | Hockney / Fenster | ⬜ Blocked | #18; depends on Phase 1A-1D |

## Phase 0 Baseline

All runtime probes used an isolated config, empty account/watch lists, Ollama disabled,
port 21742, and a session-only archive. No personal archive, provider credential, email,
or live document was opened. Results record behavior and counts, not content.

| Area | Result | Evidence |
|------|--------|----------|
| V5 reprocessing | **FAIL** | Readiness is controlled by `stage_completions`; current reextract/recomprehend paths clear legacy projections but never invalidate completion rows |
| Reclassification | **FAIL** | File move and DB update are not atomic; a missing source can still be followed by a successful metadata update |
| Email/extracted-content FTS | **FAIL** | FTS schemas index metadata only; three body-content tests are skipped |
| Semantic/hybrid reachability | **FAIL** | `SemanticSearch` has direct tests but no production Core/Service caller |
| Osprey MCP read path | **FAIL** | 18 tools are exposed, including mutations; persisted full comprehension has no dedicated read-only accessor |
| MCP trust boundary | **FAIL** | Foreign-origin preflight returned `Access-Control-Allow-Origin: *`; no mutation authentication is present |
| Chat SSE | **PASS** | Isolated request returned `results` then `done`; `ChatPane` and `ChatPage` are named consumers |
| Pipeline-status SSE | **FAIL** | Unused React hook targets nonexistent `/api/pipeline/state`; no backend consumer/publisher exists |
| Canonical UI routing | **FAIL** | `/` and `/documents` served Blazor; `/index.html` and bundled assets served React |
| React reproducibility | **FAIL** | `npm ci` rejected the out-of-sync lockfile; no-lock install built successfully; lint reported four errors |
| Playwright execution | **FAIL** | 21 source tests exist, but the default runner discovered 0 because no usable configuration is present |
| .NET build/tests | **PASS** | Core, Service, Tests, and Windows Tray built with 0 warnings/errors; 857 passed, 10 skipped, 0 failed |
| Coverage | **FAIL** | Local and latest-main CI agree: 65.0% line and 31.1% branch; required wave close gate is 85% / 60% |
| CI surface signal | **FAIL** | Windows/macOS .NET tests and Tray/MAUI builds passed; coverage alone failed, publish was skipped, and Mac MAUI remains non-blocking |
| Osprey parity | **FAIL** | Six tests are skipped for unavailable fixtures; four additional tests can pass without assertions when fixtures are absent |
| Privacy/integrity | **PASS** | Only an empty isolated SQLite/WAL archive was created; owner checkout status remained unchanged |

## Support Classification

| Surface | Class | Promotion evidence |
|---------|-------|--------------------|
| Core and Service | Supported | Retain blocking cross-platform build/test; add route/MCP integration tests and meet 85% line / 60% branch |
| React in Service | Preview, canonical target | Reproducible lockfile build, tested asset provenance, root/deep-route Playwright smoke |
| Windows Tray | Preview, Windows-only | Build/publish plus launch, health, canonical UI, and shutdown smoke |
| Direct browser UI | Preview | Same tested React artifact as Tray |
| Blazor `Hermes.UI` | Excluded | Must not shadow React; promotion requires explicit product decision and blocking UI tests |
| MAUI Windows/Mac | Excluded | Requires distributable artifacts, target-OS smoke, and non-optional CI |
| Installers/packaging | Excluded/deferred | #16 |

## Phase 0 Acceptance Gates

1. The owner checkout is unchanged and recoverable from the preservation branch.
2. Every stale issue is superseded, rewritten, or split with evidence and successor links.
3. Baseline evidence is read-only, reproducible, redacted, and records PASS/FAIL/UNKNOWN.
4. Source archive hashes are unchanged; no provider sync or external upload occurs.
5. Test counts, skips, non-asserting cases, and coverage agree with the testing register.
6. Supported, preview, and excluded surfaces are explicit.
7. Phase 1A begins only from a reviewed issue with the baseline linked.
8. PR #15 is reviewed and merged before Phase 1A implementation starts.

## Log

### 2026-08-27 - Phase 0 rebaseline completed

- **Changed:** Reconciled all stale phase issues, recorded the current-main baseline, and classified supported and excluded surfaces.
- **Evidence:** PR #15; active issues #6, #8, #9, #11, #16, and #17; baseline and support tables above.
- **Validation:** 867 .NET tests executed (857 passed, 10 skipped), 65.0% line and 31.1% branch coverage, isolated Service probes, React build/lint and Playwright discovery checks.
- **Blockers:** PR #15 review/merge; de-identified corpus replay remains prohibited until owner-approved IDs and queries exist.
- **Next:** After PR #15 review/merge, begin Phase 1A from #17 using synthetic fixtures and bounded dry-run semantics.

### 2026-08-27 - Phase 0 preservation and activation

- **Changed:** Preserved the user-owned dirty documentation/governance state and incorporated it as the V5 documentation baseline.
- **Evidence:** `7604b3f` on the preservation branch; `7d7d01b` on the stabilization branch.
- **Validation:** Preservation diff checked for whitespace and credential-like content; `C:\work\hermes` status remained unchanged.
- **Blockers:** Baseline and issue reconciliation remain.
- **Next:** Reconcile the stale issue set, then capture the privacy-safe current-main baseline.
