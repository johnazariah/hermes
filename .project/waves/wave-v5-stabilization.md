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
| Canonical UI | React served by `Hermes.Service`; Windows Tray is the preview, canonical native-shell target |
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
| 1A | Stabilize V5 reflow and reclassification | McManus / Hockney | ⏳ Active | #17; PR #19 open, deterministic reflow PR pending |
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

### 2026-08-28 - Phase 1A PR A coordinator gate corrections approved

- **Changed:** Closed stale sibling and deep-extraction sidecar overwrites with folder revision fencing; made stage-attempt cleanup recover from transient failures; made schema startup fail fast and shutdown quiesce before disposal; made MCP reflow failures and malformed containers protocol-truthful; and replaced the artificial v11 migration check with a populated deployed-v8 to v12 proof.
- **Evidence:** Deterministic tests force sibling/deep-extraction interleavings, cleanup deletion faults, unresponsive shutdown, malformed MCP value/container types, repeated `tools/list`, populated migration with duplicate active dead letters, and second-run idempotency. Per-algebra SQLite serialization also passed 20 repeated Release stress iterations; EmailSync multi-command identity remains independently owned by blocking PR #21.
- **Validation:** Release Core, Service, and Tests build with 0 warnings/errors; full suite executes 967 tests (957 passed, 10 skipped); local CI-equivalent coverage is 71.71% aggregate line / 35.91% branch and 79.13% Core line / 39.84% branch. Independent `fsharp-dev` review approved race safety, migration/idempotency, connection lifetime, cancellation, and compatibility.
- **Blockers:** PR #20 must not merge before PR #19; it must then rebase and resolve Database/API/MCP/project/docs conflicts. The Windows workflow's authoritative 75% aggregate line check remains red and is not claimed as cleared; issue #18 retains the 85% / 60% wave-close target.
- **Next:** Push the reviewed correction head, keep PR #19 separate, then rebase PR #20 after #19 lands before final merge consideration.

### 2026-08-28 - Phase 1A PR A coordinator gate reassessed

- **Changed:** Confirmed the process-wide shared-connection risk exists on `main` and is already closed in PR #20 by one algebra-scoped semaphore covering every command and transaction; clarified Core-package, aggregate-CI, current merge, and wave-close coverage measures.
- **Evidence:** `main` exposes one raw shared `SqliteConnection` without serialization; PR #20 routes all `Algebra.Database` operations through the same gate, uses separate physical pipeline/reflow connections, and passed 20 Release stress iterations covering same-connection command/transaction exclusion plus two-connection write coordination.
- **Validation:** PR head `83f208c`; macOS .NET, Shell, and Tray checks pass. Windows Release build and all 940 tests pass, then coverage fails at 71.3% aggregate line / 35.5% aggregate branch. The current workflow enforces 75% aggregate line only; the authoritative wave-close target remains 85% line / 60% branch under #18.
- **Blockers:** Coordinator merge gate remains in force pending renewed scope review; no coverage gate is claimed as cleared.
- **Next:** Complete renewed race, migration, lifetime, cancellation, and compatibility review, then return disposition to the master session.

### 2026-08-27 - Phase 1A PR A Release compiler correction

- **Changed:** Replaced a task-expression `for` loop in the stale-stage-output regression with a tail-recursive task helper so F# Release compilation remains statically resumable under warnings-as-errors.
- **Evidence:** PR #20 initial CI identified FS3511 at `ReflowTests.fs`; the production implementation was unaffected.
- **Validation:** Release test-project build passed with 0 warnings/errors; the affected regression passed; the full Release suite executed 940 tests (930 passed, 10 skipped).
- **Blockers:** Awaiting refreshed PR checks; issue #18 retains ownership of the 85% / 60% wave-close gate.
- **Next:** Push the correction and verify cross-platform CI.

### 2026-08-27 - Phase 1A PR A cross-document publication races closed

- **Changed:** Canonicalized artifact lock identities against the archive root, moved deep-extraction merge/read/write under the shared folder fence, fenced every V5 stage-owned output with its captured generation, persisted one canonical comprehension publication per stage generation, and made extracted-content reads stable across reflow.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; deterministic regressions cover relative/absolute folder aliases, sibling publication during an LLM call, stale extract/triage/deep/embed output publication, divergent comprehension retry responses, reextract during content read, and schema-v12 upgrade/idempotency.
- **Validation:** Full build passed with 0 warnings/errors; expanded focused suite passed 277 tests; full suite executed 940 tests (930 passed, 10 skipped); comparable Core coverage is 79.06% line / 39.61% branch (+14.06 / +8.51 points from baseline), with overall collected coverage 70.75% / 35.59%.
- **Blockers:** None for PR A pending renewed independent approval; issue #18 retains ownership of the 85% / 60% wave-close gate, and Phase 1B remains independently owned.
- **Next:** Obtain renewed independent approval and open PR A for review.

### 2026-08-27 - Phase 1A PR A shared-artifact and retry idempotency closed

- **Changed:** Extended publication fencing from document identity to normalized shared artifact folders, made apply requests fail without mutation when a safe folder identity cannot be derived, and added durable per-document learned-pattern evidence plus equivalent-pending-suggestion guards so real comprehension retries remain exactly once after finalization rollback.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; two-document same-folder ordering proves stale work cannot overwrite newer thread bytes, and real triage/deep-comprehend finalization-fault cases prove retries do not inflate learned evidence or duplicate suggestions while evidence from distinct documents still accumulates.
- **Validation:** Full build passed with 0 warnings/errors; focused concurrency and comprehension suite passed 131 tests; full suite executed 932 tests (922 passed, 10 skipped); comparable Core coverage is 78.85% line / 39.12% branch (+13.85 / +8.02 points from baseline), with overall collected coverage 70.23% / 35.03%.
- **Blockers:** None for PR A pending renewed independent approval; issue #18 retains ownership of the 85% / 60% wave-close gate, and Phase 1B remains independently owned.
- **Next:** Obtain renewed independent approval and open PR A for review.

### 2026-08-27 - Phase 1A PR A final concurrency review blockers closed

- **Changed:** Serialized canonical sidecar publication with reflow acceptance, switched write transactions to `BEGIN IMMEDIATE` for safe two-connection fencing, isolated and recorded pipeline-cycle faults, observed terminal background-task failures, and kept transient finalization faults retryable without false dead letters.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; deterministic tests prove stale deep extraction cannot overwrite newer-generation sidecar bytes, a second file-backed writer waits and commits without `SQLITE_BUSY_SNAPSHOT`, and injected finalization/cycle faults remain observable while later retries converge.
- **Validation:** Full build passed with 0 warnings/errors; focused concurrency suite passed 103 tests; full suite executed 928 tests (918 passed, 10 skipped); comparable Core coverage is 78.31% line / 38.70% branch (+13.31 / +7.60 points from baseline), with overall collected coverage 69.65% / 34.60%.
- **Blockers:** None for PR A pending renewed independent approval; issue #18 retains ownership of the 85% / 60% wave-close gate, and Phase 1B remains independently owned.
- **Next:** Obtain renewed independent approval and open PR A for review.

### 2026-08-27 - Phase 1A PR A generation fencing validated

- **Changed:** Added per-document generations and stage-attempt leases so reflow acceptance atomically supersedes in-flight work; stale stage output is discarded before retry, identical active requests still coalesce, different reflow kinds retain independent attribution, and deep extraction, contact backfill, RAC hints, and comprehension publication are generation-fenced without deleting shared sidecars.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; deterministic barrier tests cover reflow during an active processor, duplicate and different-kind overlap, stale-lease recovery, deep-extraction publication, contact linking, and RAC sidecar use.
- **Validation:** Full build passed with 0 warnings/errors; focused reflow/race suite passed 98 tests; full suite executed 924 tests (914 passed, 10 skipped); comparable Core coverage is 77.85% line / 37.90% branch (+12.85 / +6.80 points from baseline), with overall collected coverage 69.29% / 33.87%.
- **Blockers:** None for PR A; issue #18 retains ownership of the 85% / 60% wave-close gate, and Phase 1B remains independently owned.
- **Next:** Obtain final independent approval and open PR A for review.

### 2026-08-27 - Phase 1A PR A stale-artifact consumers closed

- **Changed:** Required current deep-comprehend completion plus document-level comprehension output before deep extraction or contact backfill can consume shared sidecar content, preventing manual classifications or skipped content-rule gates from revalidating stale comprehension.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; 49 net-new synthetic cases from the Phase 0 baseline now include stale-sidecar contact backfill and missing-output deep-extraction regressions while proving the shared sidecar remains untouched.
- **Validation:** Full build passed with 0 warnings/errors; focused stale-artifact suite passed 74 tests; full suite executed 916 tests (906 passed, 10 skipped); comparable Core coverage is 76.45% line / 36.76% branch (+11.45 / +5.66 points from baseline), with overall collected coverage 68.13% / 33.00%.
- **Blockers:** None for PR A; issue #18 retains ownership of the 85% / 60% wave-close gate, and Phase 1B remains independently owned.
- **Next:** Obtain final independent approval and open PR A for review.

### 2026-08-27 - Phase 1A PR A final review blockers closed

- **Changed:** Made stage success/failure finalization atomic, made status reads snapshot-consistent, retired stale-DAG operations without executing them, derived legacy stage from the completion ledger, removed comprehension-owned current state during invalidation, gated extracted/comprehension artifact readers on current completions, and retained the legacy `requeued` response field.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; 47 net-new synthetic cases from the Phase 0 baseline now include fault-injected finalization rollback, stale-DAG handling, two-connection status coherence, derived-state ownership, compatibility projection, artifact currentness, and legacy API contracts.
- **Validation:** Full build passed with 0 warnings/errors; focused review suite passed 169 tests with 1 expected platform skip; full suite executed 914 tests (904 passed, 10 skipped); comparable Core coverage is 76.39% line / 36.72% branch (+11.39 / +5.62 points from baseline), with overall collected coverage 68.08% / 32.96%.
- **Blockers:** None for PR A; issue #18 retains ownership of the 85% / 60% wave-close gate, and Phase 1B remains independently owned.
- **Next:** Obtain final independent approval and open PR A for review.

### 2026-08-27 - Phase 1A PR A concurrency correction validated

- **Changed:** Made reflow acceptance a single transaction from operation creation through invalidation, serialized all use of each SQLite connection, isolated production REST/MCP reflow traffic on a dedicated connection, and prevented pending or stale failed operations from corrupting stage claims/completions.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; 35 net-new synthetic cases now cover DAG policy, atomic visibility/rollback, connection serialization, operation identity, retries, stale-ledger recovery, chunk replacement, migration, and supported surfaces.
- **Validation:** Full build passed with 0 warnings/errors; focused concurrency/reflow/API/MCP suite passed 109 with 1 expected platform skip; full suite executed 902 tests (892 passed, 10 skipped); comparable Core coverage is 71.17% line / 34.05% branch (+6.17 / +2.95 points from baseline), with overall collected coverage 63.34% / 30.49%.
- **Blockers:** None for PR A; issue #18 retains ownership of the 85% / 60% wave-close gate, and Phase 1B remains independently owned.
- **Next:** Complete final independent review and open PR A for review.

### 2026-08-27 - Phase 1A PR A DAG reflow implemented

- **Changed:** Added typed dry-run/apply reextract, recomprehend, and reembed operations with DAG-closure validation, atomic invalidation, v9 audit state, truthful retries, and REST/MCP/pipeline/activity/dead-letter observability.
- **Evidence:** Issue #17 PR A scope on `johnazariah-17-v5-file-first-correctness`; 29 synthetic cases cover policy closure, exact preservation/invalidation, rollback, retry, chunk replacement, migration, and supported surfaces.
- **Validation:** Full build passed with 0 warnings/errors; 896 .NET tests executed (886 passed, 10 skipped); comparable Core coverage 71.3% line / 33.5% branch (+6.3 / +2.4 points from baseline).
- **Blockers:** None for PR A; reclassification remains separately scoped, and issue #18 retains ownership of the 85% / 60% wave-close gate.
- **Next:** Prepare PR A for review without claiming Phase 1B search or whole-wave coverage completion.

### 2026-08-27 - Phase 0 review correction

- **Changed:** Preserved the original completion entry and appended the PR review gate, issue #18, documentation truth corrections, and final privacy scrub.
- **Evidence:** PR #15; active issues #6, #8, #9, #11, #16, #17, and #18; baseline and support tables above.
- **Validation:** Source-of-truth and privacy review completed across the full branch diff; no Phase 1 implementation is included.
- **Blockers:** PR #15 review/merge; de-identified corpus replay remains prohibited until owner-approved IDs and queries exist.
- **Next:** After PR #15 review/merge, begin Phase 1A from #17 using synthetic fixtures and bounded dry-run semantics.

### 2026-08-27 - Phase 0 rebaseline completed

- **Changed:** Reconciled all stale phase issues, recorded the current-main baseline, and classified supported and excluded surfaces.
- **Evidence:** PR #15; active issues #6, #8, #9, #11, #16, and #17; baseline and support tables above.
- **Validation:** 867 .NET tests executed (857 passed, 10 skipped), 65.0% line and 31.1% branch coverage, isolated Service probes, React build/lint and Playwright discovery checks.
- **Blockers:** Phase 1B-1E remain dependency-blocked; de-identified corpus replay remains prohibited until owner-approved IDs and queries exist.
- **Next:** Begin Phase 1A from #17 using synthetic fixtures and bounded dry-run semantics.

### 2026-08-27 - Phase 0 preservation and activation

- **Changed:** Preserved the user-owned dirty documentation/governance state and incorporated it as the V5 documentation baseline.
- **Evidence:** `7604b3f` on the preservation branch; `7d7d01b` on the stabilization branch.
- **Validation:** Preservation diff checked for whitespace and credential-like content; `C:\work\hermes` status remained unchanged.
- **Blockers:** Baseline and issue reconciliation remain.
- **Next:** Reconcile the stale issue set, then capture the privacy-safe current-main baseline.
