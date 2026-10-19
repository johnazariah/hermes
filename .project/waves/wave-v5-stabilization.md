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
| 1A | Stabilize V5 reflow and reclassification | McManus / Hockney | ✅ Done | #17 closed after merged #19/#20 and independent acceptance mapping |
| 1B | Restore file-first FTS, semantic/hybrid, and Osprey reachability | McManus / Hockney | ⏳ Active | #11 implementation in progress after #17 closure; #6 follows the indexed-content contract |
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

### 2026-10-19 - Test-only coverage consolidation approved for publication

- **Changed:** Independent F# re-review approves the combined #21-#23 salvage, including the separate author's bounded producer-progress correction. Keep one coherent test-only integration, preserve all three historical draft heads as ancestry, and conclude duplicates after the accepted result is merged.
- **Evidence:** Production source is unchanged; all nine touched test modules satisfy UTF-8 BOM/LF. Old provider intentions are represented by current contracts plus missing-case assertions, with no reinstatement of obsolete provider/ingestion code. Sixteen provider, eight Config, four watcher and one controlled cleanup case add 29 cases.
- **Validation:** Final Release build has 0 warnings/errors; full execution is 1,144 total, 1,134 passed, 10 existing skips, 0 failed. Exact root line coverage is 9,154/11,604 = 78.8866%, with Core/Service/UI scope unchanged and all original gates retained.
- **Blockers:** Exact published-head Windows/macOS CI remains required before merge; the stronger #18 wave-close gate and functional search/read-only Osprey work remain open.
- **Next:** Push the reviewed tree normally to existing PR #21 with retained ancestry, mark it ready, verify current-head CI and merge; then conclude the incorporated #22/#23 drafts and prune integration artifacts.

### 2026-10-19 - Coverage progress guard corrected and complete recovery passes

- **Changed:** Independent review found the recovered enumeration failure tests could hang if historical swallow-and-retry behaviour returned. Locked out that author and applied a different author's test-only revision: deadline the producer operation before inspecting its expected exception, cancel/observe owned work and bound cleanup explicitly.
- **Evidence:** The new controlled zero-deadline case proves cancellation joins the fixture-owned producer; exact failure type/message, one request, unchanged progress and normal input completion checks remain. The other recovered provider, Config and FolderWatcher contracts were accepted in substance.
- **Validation:** Release build has 0 warnings/errors; all four bounded enumeration cases pass. Final full execution is 1,144 total, 1,134 passed, 10 existing skips and 0 failed. Exact root line coverage is 9,154/11,604 = 78.8866%; conservative final branches are 7,907/18,295. Production source, assembly scope, skips and gates are unchanged.
- **Blockers:** Final independent re-review and exact published-head CI remain before the coverage consolidation can merge. File-first search/CLI source is separately authored and not yet applied.
- **Next:** Publish the reviewed test-only recovery with all original draft histories retained, conclude duplicate drafts after accepted integration, and continue the financial-document content-search slice.

### 2026-10-19 - Main publication verified; useful coverage drafts consolidated

- **Changed:** Main `55ec588` passed all seven build/test/publish jobs. Pruned its and PR #20's four retained artifacts (186,158,382 bytes); zero artifacts remain and the two warm `main` dependency caches remain at 114,212,113 bytes. No paid capacity or cache expansion was enabled.
- **Evidence:** Old #21's 32 intended contracts are mapped to current coverage and genuinely missing assertions; obsolete ingestion/provider production patches are not restored. Consolidate the useful test-only recovery into existing PR #21, retaining the old #21-#23 histories and explicitly concluding duplicate drafts after integration, rather than repeating three full artifact-producing CI cycles.
- **Validation:** Sixteen provider, eight Config and four FolderWatcher cases add 28 cases. The combined Release build has 0 warnings/errors; all 1,143 cases execute with 1,133 passed, 10 existing skips and 0 failed. Combined root coverage is 9,154/11,604 = 78.8866% line and 7,918/18,295 branch, with Core/Service/UI scope unchanged. The provider-only run's one-line variance is not presented as a line-rate gain.
- **Blockers:** Independent review and published-head CI for the coverage consolidation are pending. File-first content-search implementation is separately authored and not yet applied; historical-index rebuild needs the chosen bounded, local, explicit dry-run/apply CLI wire before acceptance.
- **Next:** Complete independent review, publish the test-only consolidation, verify its exact head and conclude #21-#23; then apply and prove the source-grounded financial-document search/CLI slice without introducing an unauthenticated mutation endpoint.

### 2026-10-19 - Reflow merged and Phase 1A acceptance concluded

- **Changed:** PR #20 merged into `main` as `55ec588`, completing the reviewed reflow/reclassification scope with PR #19. Closed #17 as completed after independently mapping all five acceptance criteria to registered synthetic tests. Updated the obsolete move wording to the approved metadata-only contract; #24 retains separate legacy repair.
- **Evidence:** Published head `b65f4b3` passed all five build/test jobs, including Windows/macOS and both MAUI shells. The main merge tree equals the reviewed published tree. Both original histories and eleven historical reflow journal entries are retained without rewriting published commits.
- **Validation:** Windows and macOS each executed 1,115 cases: 1,105 passed, 10 existing skips, 0 failed. Downloaded Windows root coverage passes the exact unchanged line gate at 9,122/11,604 = 78.6108%, including Core, Service and UI. Dry-run acceptance is the shipped single-document, explicitly bounded synthetic HTTP/MCP contract, not a claim of bulk or personal-corpus replay.
- **Blockers:** Post-merge main publication/storage verification is still running. The broader #18 wave gate, coverage salvage and file-first search/read-only Osprey work remain unfinished; Phase 1A completion does not close the wave.
- **Next:** Verify main publication and prune unneeded artifacts, salvage genuinely additional tests from #21-#23, and implement the source-grounded financial-document search slice in #11. Separate F# authors are preparing coverage-only and search patches without changing the verified published source.

### 2026-10-19 - Reflow publication resumed through the remaining integration gates

- **Changed:** John directed completing the remaining publication and CI steps autonomously rather than stopping at local readiness. Proceed with the existing PR #20 and established `main` target, without force-pushing, rewriting published history or creating another worktree/branch.
- **Evidence:** The independently approved source remains covered by its final per-file manifest. Eleven historical PR #20 journal entries are preserved verbatim alongside every existing current entry; the already-reviewed main-plus-reflow tree will be recorded with both original histories as merge ancestry.
- **Validation:** Final formatted-source Release build has 0 warnings/errors; all 1,115 cases execute with 1,105 passed, 10 existing skips and 0 failed. Exact conservative root coverage is 9,123/11,604 = 78.6194% line and 7,848/18,295 = 42.8970% branch, including Core, Service and UI. Source review is approved; no threshold or instrumentation exclusion is changed.
- **Blockers:** Windows and exact published-head CI remain unverified until the branch is pushed. The 85% / 60% wave-close target and subsequent financial-document search/read-only Osprey work remain outstanding.
- **Next:** Commit the validated reconciliation, record its already-integrated ancestry, push normally to PR #20, verify current-head CI and merge only on passing gates. Prune unneeded Actions artifacts at the integration checkpoint; keep useful dependency caches.

### 2026-10-19 - Reflow integration independently approved and passing locally

- **Changed:** Imported the reviewed #20/main reconciliation into the session checkout with per-file baseline/hash guards. Combined schema 15 preserves the source-ownership and immutable-ingestion contracts; audited DAG replay uses the existing physical-connection transaction scope. Effective user-corrected date/amount/vendor now survive invalidation and extraction replay, every failed nested callback cleans up its savepoint, and superseded deep extraction returns a truthful MCP tool-error envelope.
- **Evidence:** A different F# revision author resolved the independent rejection; the reviewer now approves the revised source and execution evidence. All 46 imported source/project/test files match the approved candidate byte-for-byte. A source-only recovery archive, import manifest, TRX and coverage evidence remain in session artifacts; the temporary in-workspace candidate/reference directory was removed.
- **Validation:** Restoring the changed project graph followed by the actual-checkout Release build produced 0 warnings/errors. Candidate and checkout executions both passed 1,105 with 10 existing skips (1,115 total), including all eight new savepoint/real-HTTP financial-correction/MCP-supersession cases. Root line coverage is conservatively 9,123/11,604 (78.61%; checkout 9,124/11,604); checkout branch coverage is 7,867/18,295 (43.00%). Instrumentation now includes Core, Service and UI; no exclusions or threshold reductions were introduced. HTTP tests use synthetic data, fake providers and the previously documented isolated-content-root/polling accommodation.
- **Blockers:** Work remains uncommitted and unpublished on the unchanged session branch; PR #20's remote head is still the historical head. Current Windows/published-head CI and publication authority remain separate gates. The wave-close 85% line / 60% branch target, existing skips/weak fixtures and subsequent file-first search/read-only Osprey work remain unfinished.
- **Next:** Publish the validated #20 integration only with explicit Git authority, then verify its exact published head before merge and proceed to the financial-document search slice. Parent #17 stays open until the remaining acceptance evidence is complete; legacy repair and packaging remain separate.

### 2026-10-19 - Financial-document delivery prioritised; reflow candidate rejected

- **Changed:** Started the #20 integration against `main` `367feb4`, prioritising reliable financial-document ingestion, content search and source-grounded read-only retrieval. Useful coverage recovery accompanies that path; no filing jurisdiction, tax year, deadline or personal-corpus access is assumed. The user was unavailable for the standing publication-permission question, so no new commit/push/merge authority is inferred.
- **Evidence:** An isolated main-plus-#20 candidate reconciles the code/project conflicts, advances the combined schema to 15 and builds Core, Service and Tests without warnings/errors. The tracked source checkout and earlier local journal entry remain preserved; temporary workspace-contained candidate/reference snapshots accommodate the language agent's workspace-only reads. Execution priorities are recorded on [#11](https://github.com/johnazariah/hermes/issues/11#issuecomment-6257466569) and [#20](https://github.com/johnazariah/hermes/pull/20#issuecomment-6257466812).
- **Validation:** Adjacent unchanged-main execution passed 982 with 10 existing skips (992 total), with Core-only line coverage 6086/8107. The candidate full suite is diagnostic only: 1079 passed, 18 failed, 10 existing skips (1107 total). Its root coverage is 8903/11581 across Core, Service and UI, not the former Core-only scope; no acceptance claim is made while tests fail.
- **Blockers:** Independent F# review rejected incomplete nested-savepoint exception/cancellation rollback, reextract erasing effective user-corrected date/amount/vendor, and MCP success envelopes for superseded deep extraction. Legacy and new fixtures also need lawful archive identities/hashes and precise user-tag assertions. The rejected integration author is locked out; a different F# author owns production fixes and fixture reconciliation.
- **Next:** Prove the three corrections through transaction, real correction/replay and HTTP MCP regressions; rerun the independent review and complete build/test/coverage/transport gates before importing or publishing a candidate. Existing 75% merge and 85% line / 60% branch wave-close gates remain unchanged.

### 2026-10-19 - Actions storage pruned and next integration work sequenced

- **Changed:** Deleted all four retained Hermes Actions artifacts (185,613,575 bytes) and the two redundant caches scoped to closed PR #19 (114,217,070 bytes). The two useful `main` dependency caches remain (114,212,113 bytes). Future coverage/build retention stays at seven/three days; workflow logs and session-local validation evidence are preserved.
- **Evidence:** PR #19 merged into `main` as `367feb4`; its merge tree matches the reviewed head, and all seven post-merge build/test/publish jobs passed. Live storage now lists zero artifacts and two `main` caches. The next-work briefs are recorded on [PR #20](https://github.com/johnazariah/hermes/pull/20#issuecomment-6257267541) and [issue #18](https://github.com/johnazariah/hermes/issues/18#issuecomment-6257267971).
- **Queue:** First reconcile deterministic reflow #20 with schema 14, the shared connection/transaction scope, metadata-only reclassification and immutable ingestion; then independently review and prove the full request-to-observable-outcome path. Audit #21 for genuinely additional tests rather than restoring superseded production changes, then restack and validate Config #22 and FolderWatcher #23. Follow with file-first search/read-only Osprey (#11/#6), the HTTP/MCP trust boundary (#6), and canonical React/Tray lifecycle work (#9/#8); #18 spans those phases. Legacy repair #24 remains separately designed/reviewed and packaging #16 remains deferred.
- **Validation:** Deletion targets and duplicate cache keys were checked before removal; the resulting artifact/cache inventory was verified. No source code or workflow configuration changed, and no branch switch, rebase, force-push or new implementation was performed.
- **Blockers:** #20 and #21 currently conflict with `main`. #22/#23 are clean only against their old stack parents and have no recorded checks. Historical test counts/coverage are not integration evidence; root/per-assembly coverage, the unchanged 75% merge gate, and the 85% line / 60% branch wave-close target remain explicit.
- **Next:** Execute the bounded F# integration/review brief for #20 against current `main`; keep parent #17 open until its remaining acceptance criteria are proved. This journal entry is local and uncommitted.

### 2026-10-19 - Publication and merge explicitly authorised

- **Changed:** John requested pushing the completed revision, creating a PR and merging it. PR #19 is already open on the approved branch with base `main`, so publication will update that PR rather than create a duplicate or retarget the established integration path.
- **Evidence:** The checkout remains on `johnazariah-17-file-first-reclassification`; its 242 reviewed source/build/test files match the hash-verified cross-platform snapshot. The published head is still `c9e5d4f` before this publication step.
- **Validation:** Fresh Release builds of Core, Service and Tests passed without warnings/errors; the full test run passed 982 with 10 existing skips (992 total). Fresh Core line coverage is 6088/8107 (75.0956%), above the unchanged exact 75% gate. Staged diff, credential/token/private-key checks and prior independent source/test approval passed; the PR body will follow the repository template.
- **Blockers:** Published-head GitHub checks have not run yet. No branch-protection rule was returned for `main`, but the project quality/review gates will still be enforced without bypass.
- **Next:** Commit the approved source/tests/docs and bounded CI-retention concern, push normally, verify checks for the exact resulting head and merge only after the blocking jobs pass. Do not close the stabilization wave or parent #17 prematurely.

### 2026-10-19 - Narrowed local revision approved and validated on macOS and Windows

- **Changed:** Option 1 is implemented locally: ordinary reclassification is metadata-only, legacy repair is separate, email bodies and annotations remain immutable history, failed email storage stays retryable, and real provider/pagination faults remain visible. Independent source and test-slice reviews approve the revision. Coverage-report retention is 7 days and build-bundle retention is 3 days; thresholds and dependency-only caching remain unchanged.
- **Evidence:** The complete suite passes twice on macOS and once on an existing idle Windows CI host: 992 cases, 982 passed, 10 existing skips, 0 failed. Release Core/Service/Tests builds pass on both platforms; Windows Tray also builds with 0 warnings/errors. Windows used a hash-verified source snapshot, temporary SHA-512-verified SDK 9.0.313 and isolated public-package cache. The runner service/workspace were untouched, all Windows temporary files were removed after evidence retrieval, and the runner remains online/idle.
- **Validation:** Exact root/Core-only line coverage is conservatively 6,086/8,107 (75.0709%) on macOS and 6,087/8,107 (75.0833%) on Windows; both clear the unchanged 75% gate. Branch coverage is 40.8685% / 40.9317%. Final isolated HTTP/MCP/schema-v14 smoke passes with the documented content-root/polling accommodation; helper services and the session-only diagnostic tool were stopped/removed. Evidence is in session artifacts, including the Windows source manifest, TRX/coverage archive and adjacent-run gate report.
- **Blockers:** Changes remain uncommitted/unpushed; the published PR head and its GitHub Actions results have not changed. This is manual cross-platform proof, not a remote CI result or merge approval. PRs #20-#23 remain integration-blocked until #19 is published and merged. The 85% line / 60% branch wave-close target and pre-existing skip-quality issues remain owned by later stabilization work.
- **Next:** Publish the approved local revision only with explicit Git publication authority, then verify GitHub checks and integration before unfreezing dependent PRs. No paid compute/storage, live-corpus replay, credential disclosure or shared-runner reconfiguration occurred.

### 2026-10-18 - Revision 2 approved; targeted contracts narrow the coverage gap

- **Changed:** The fresh revision preserves immutable processed body history, uses full-provider-ID SHA-256 filenames, and propagates actual Gmail/Outlook transport and enumeration failures. Independent source-correctness approval stands. Ten additional real-SDK positive contracts cover non-empty lists/pages, full bodies, exact binary attachment mapping, committed source identity and idempotent retry without production changes.
- **Evidence:** Source review approved revision 2; final HTTP/MCP/schema-v14 smoke passed using an isolated content root and polling. The original native macOS watcher stall was recorded on issue [#8](https://github.com/johnazariah/hermes/issues/8#issuecomment-6250445807) as an environmental lifecycle investigation, not a production fix. All helper services were stopped.
- **Validation:** The positive contract suite passes 10 cases. Full execution is 980 total, 970 passed, 10 existing skips, 0 failed. Root/Core-only coverage is 6,054/8,107 lines (74.6762%) and 5,121/12,665 branches (40.43%); the exact unchanged 75% line gate still fails.
- **Blockers:** Twelve further bounded optional-metadata/commit-error cases need a compiler-only JsonElement interop correction before execution. Their results and the remaining line-coverage gap are not waived. Windows/CI/publication and the 85% / 60% wave-close target remain separate gates.
- **Next:** Validate the final targeted cases, measure the exact gate again, then obtain scoped independent review of the test-only additions. No source publication, merge, paid usage or live-corpus replay occurred.

### 2026-10-18 - Autopilot authorised; storage retention bounded

- **Changed:** John granted the bridge, enabled autopilot and approved the recommended path without routine questions. CI coverage artifacts now retain for 7 days and build bundles for 3 days; Windows coverage upload runs even when the threshold step fails. Dependency-only npm caching and existing coverage thresholds are unchanged; no paid cache expansion was enabled.
- **Evidence:** `.squad/decisions/inbox/copilot-autopilot-and-storage-retention.md`; `.github/workflows/ci.yml` parsed successfully and its retention values/upload condition were checked locally. No Actions workflow was triggered and no retained artifact was deleted.
- **Blockers:** Revision 2's independently authored immutable-history and provider-error corrections are applied but still being validated. Core and Service Release builds pass; the new test fixture currently fails F# parsing of a generated `@odata.nextLink` field. This is not a readiness claim.
- **Next:** Complete the fresh author's bounded correction/validation cycles, rerun independent review, and retain the no-paid-usage, immutable-history and non-destructive-data constraints.

### 2026-10-18 - Owner chose immutable email-body history

- **Changed:** John selected retaining earlier email-body documents, extracted data, tags and notes as immutable archive history, rather than replacing a previous document and transferring annotations. This supersedes latest-body retirement for new ingestion.
- **Evidence:** `.squad/decisions/inbox/copilot-immutable-email-body-history.md`; the decision follows independent review finding that deleting a processed previous body violates dependent foreign keys and risks losing document annotations.
- **Blockers:** Revision 1 remains rejected; its author is locked out of the next correction. Provider error propagation, collision-resistant new-message filenames, historical-state preservation and renewed independent proof remain required. No historical reconstruction, path rewrite or corpus replay is approved.
- **Next:** Assign a fresh author the three bounded review corrections under the immutable-history and whole-email retry decisions; remove obsolete replacement assumptions rather than adding destructive cascades.

### 2026-10-18 - Whole-email correction passes locally; independent review rejected

- **Changed:** The owner-approved whole-email correction, preserved cancellable rate-limit handling and legacy/backfill retry regressions now compile. The final revision remains uncommitted and is not approved for integration.
- **Evidence:** Release Core/Service/Tests builds have 0 warnings/errors; the focused atomic/rate/legacy/body/retirement run passes 29 cases. Full execution is 943 total, 933 passed, 10 existing skips, 0 failed. Comparable Core-only coverage is 70.16% line (5,762/8,212) and 36.75% branch (4,754/12,935), above original-head 68.50% / 33.58%. Final-head isolated HTTP/MCP and synthetic schema-v13 upgrade probes passed; helper services were stopped.
- **Validation:** A fresh independent F# reviewer rejected the revision on three P1 gaps: processed-body retirement violates non-cascading foreign keys, shortened provider IDs can cause deterministic same-day filename collisions, and production providers can hide transport errors as empty successful results. Passing current tests does not prove those scenarios.
- **Blockers:** The current author is locked out of the next revision. Corrections need a different independent author and regression proof; retirement handling needs an explicit non-destructive policy for existing user annotations. The 75% merge gate and 85% / 60% wave-close target remain unmet/unproven; PRs #20-#23 stay frozen.
- **Next:** Resolve whether prior email-body documents are retained as immutable history or replaced with deliberate annotation transfer, then assign the three bounded corrections to a fresh author. No commit, push, merge or live-corpus access is authorised.

### 2026-10-18 - Public CI billing clarified; storage limits proposed

- **Changed:** Corrected Copilot's overly restrictive interpretation of the budget directive. Hermes is public, and standard GitHub-hosted compute is free without consuming the private-repository minutes allowance; self-hosting is not required to save Hermes compute minutes. No paid compute/storage remains the constraint.
- **Evidence:** GitHub repository visibility and [official billing documentation](https://docs.github.com/en/billing/concepts/product-billing/github-actions). Read-only storage inventory found no retained Hermes Actions artifacts and zero cache bytes. Published bundles currently retain for 14 days; coverage uploads inherit repository retention.
- **Blockers:** Proposed 7-day coverage retention, 3-day bundle retention and dependency-only caching without paid cache expansion have not been approved or implemented. Existing CI/review/coverage gates and the prohibition on unapproved commit/push/merge remain.
- **Next:** Preserve the billing distinction in CI planning and obtain approval before changing retention or shared-runner registrations; continue local correction of the owner-approved whole-email ingestion policy.

### 2026-10-18 - Owner chose whole-email retry after ingestion failure

- **Changed:** John selected keeping the whole email unprocessed and retrying it next sync when its body or an attachment cannot be saved, rather than adding separate per-part retry tracking. This applies to new attempts; no historical-message reset or corpus replay is approved.
- **Evidence:** `.squad/decisions/inbox/copilot-email-ingestion-acknowledgement.md`; the independent author's producer trace found that a swallowed failure could retain a processed-message marker while advancing the watermark. A regression test has been authored and must not be skipped or weakened.
- **Validation:** The canonical named-lambda route correction now passes actual isolated HTTP/MCP probes: category PUT/idempotence, legacy and canonical batches, deprecated single POST, repeated tool discovery, MCP success/error, and removed repair rejection. Source bytes/path/hash, extraction/embedding timestamps and stage-completion rows remained unchanged. The helper Service was stopped.
- **Blockers:** Whole-email commit/acknowledgement, staged-source protection against deletion/movement, baseline coverage recovery and renewed independent review remain incomplete. No hosted CI or push occurred.
- **Next:** Implement and prove atomic new-attempt registration, truthful failures, post-commit callbacks/retirement and next-sync retry without a new per-attachment retry subsystem.

### 2026-10-18 - Narrowed revision builds; validation gates remain

- **Changed:** Applied a fresh independent F# author's revision removing the repair modules/operator and restoring callable compatibility adapters. The retained ownership work adds transaction-scoped database execution, canonical-path guards and guarded ingestion/artifact writes. Changes remain uncommitted on the approved PR #19 branch.
- **Evidence:** Core, Service and Tests Release builds pass with 0 warnings/errors; 152 focused cases pass. The full local suite executes 917 cases (907 passed, 10 existing skips). An isolated Service using only a synthetic session archive is responsive and ingests the fixture.
- **Validation:** Revised Core-only Cobertura is 67.54% line (5,366/7,944) and 32.33% branch (4,023/12,442), below original-head 68.50% / 33.58%; this is a blocking regression, not a completed coverage gate. Real HTTP testing also found a valid canonical category PUT returning 400 before handler execution. The isolated helper Service was stopped after the probe.
- **Blockers:** Producer ownership/interleaving evidence, canonical HTTP route binding, restored baseline coverage, renewed independent review and existing CI gates remain. No hosted workflow, commit, push, merge or live-corpus replay occurred.
- **Next:** Have the independent author correct the route binding and add targeted producer/staged-source tests, then rerun local transport and same-scope coverage evidence before readiness review.

### 2026-10-18 - Existing runner hosts inventoried over SSH

- **Changed:** The owner completed the one-time SSH login; the existing agent-held public key was installed and passwordless, strict-host-key-checked access to Proxmox was verified. No VM or runner service was created, stopped or reconfigured.
- **Evidence:** Proxmox `p43-proxmox` has running Ubuntu CI VMs 200 (`pelican-ci-1`, `192.168.1.220`) and 201 (`pelican-ci-2`, `192.168.1.221`), plus running Windows CI VM 202 (`pelican-win-ci`). GitHub reports `proxmox-linux-ci-1`, `proxmox-linux-ci-2` and `macbook-arm64-pelican` online and idle in `johnazariah/pelican`. Hermes still has zero registered runners.
- **Blockers:** Pelican repository-scoped runner registrations are not automatically available to Hermes. Windows VM inventory is incomplete because its QEMU guest agent is not running. Online/idle is an observation, not permission to stop or reassign a shared runner.
- **Next:** Confirm reuse through additional Hermes registrations on existing hosts while preserving Pelican service availability and real Windows/macOS validation. No hosted workflow is to be triggered.

### 2026-10-18 - Self-hosted CI budget constraint recorded

- **Changed:** John directed that no GitHub-hosted Actions minutes be used, with existing runners on Proxmox `192.168.1.222` preferred over provisioning more. Local validation continues; no workflow dispatch, rerun or push is authorised while workflows still target hosted runners.
- **Evidence:** `.squad/decisions/inbox/copilot-self-hosted-ci-budget.md`; read-only repository runner inventory returned zero registered Hermes runners. Current CI and Squad workflows still target GitHub-hosted runners, so changing labels or closing issues can also trigger hosted work.
- **Blockers:** Existing SSH-key access for the configured `johnaz` login and standard Proxmox `root` login was rejected. Runner host inventory, usable operating systems, labels and repository access remain unverified. Existing CI/review/coverage gates are not waived.
- **Next:** Obtain the correct existing SSH login or host alias, inventory reusable runners without modifying them, and propose routing that preserves real platform evidence before any push.

### 2026-10-18 - Metadata-only reclassification scope approved

- **Changed:** John approved Option 1 for PR #19: finish metadata-only reclassification with compatible public outcomes and global canonical-path ownership safeguards, and remove legacy repair from this PR. Recovery is deferred to issue #24 for separate design and review; existing broken paths are not declared repaired.
- **Evidence:** Owner decision on [#17](https://github.com/johnazariah/hermes/issues/17#issuecomment-6248839165), scope note on [PR #19](https://github.com/johnazariah/hermes/pull/19#issuecomment-6248839662), and `.squad/decisions/inbox/copilot-pr19-metadata-only-scope.md`. The obsolete `status:needs-decision` label was removed. The owner explicitly approved switching this checkout to the existing PR #19 branch.
- **Validation:** Before implementation, original head `c9e5d4f` passed Release Core/Tests and Service builds with 0 warnings/errors; the full suite ran 951 cases (941 passed, 10 skipped). Comparable Core-only Cobertura measured 68.50% line (5,888/8,595) and 33.58% branch (4,215/12,549).
- **Blockers:** The scope decision does not approve the implementation or waive review/CI. A fresh independent F# revision is required under the existing reviewer lockout. PRs #20-#23 remain frozen pending the narrowed change and required rebases/revalidation.
- **Next:** Remove the repair operator and scan/cursor machinery while proving retained reclassification, compatibility and ownership requirements. No commit, push, merge or live-corpus replay is authorised.

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

### 2026-08-29 - File-first repair correction approved

- **Changed:** Legacy path repair now uses atomic canonical ownership, archive containment, bounded resumable pages, snapshot restarts, and truthful apply outcomes; React reclassification clears hidden selections across direct and browser-history filter changes and surfaces malformed success responses as errors.
- **Evidence:** PR #19 correction; `Database.fs`, `LegacyReclassification.fs`, `McpLegacyReclassification.fs`, `DocumentsPage.tsx`, and focused database, MCP, legacy-repair, and Playwright tests.
- **Validation:** Release solution build passed with 0 warnings/errors; 951 .NET cases ran (941 passed, 10 skipped); Core coverage reached 68.50% line / 33.58% branch; changed-file ESLint passed; all 9 React reclassification Playwright cases passed; independent F# and React reviews approved the scoped correction.
- **Blockers:** The pre-existing repository-wide 75% CI line gate remains owned by issue #18; Service-hosted React routing remains Phase 1D scope.
- **Next:** Commit the independently approved correction for mechanical integration into PR #19 and branch-level CI review.

### 2026-08-29 - Reclassification filter race prevented

- **Changed:** Desktop and mobile document filters are disabled while reclassification is pending, preventing completion from restoring failed IDs into a different, invisible result view.
- **Evidence:** `DocumentsPage.tsx`, `reclassification.spec.ts`, and the rebuilt React Service assets.
- **Validation:** React TypeScript/Vite build and changed-file ESLint passed; all 6 reclassification Playwright cases passed against Vite preview, including delayed-mutation filter lockout.
- **Blockers:** None.
- **Next:** Return the correction for independent reviewer approval without committing or pushing.

### 2026-08-27 - Reclassification batch counts corrected

- **Changed:** Batch reclassification now reports changed, unchanged, and failed outcomes separately so idempotent no-ops do not inflate the successful-change count.
- **Evidence:** PR #19 follow-up; `ReclassificationApi.fs`, `ReclassificationApiTests.fs`, and the additive React response type.
- **Validation:** Full solution build passed; 898 .NET tests ran (888 passed, 10 skipped); Core coverage reached 66.88% line / 31.27% branch; React build and focused ESLint passed; independent F# review approved.
- **Blockers:** The pre-existing 75% CI line-coverage gate remains tracked by issue #18.
- **Next:** Re-run PR #19 checks and continue review without merging.

### 2026-08-27 - Metadata-only reclassification completed

- **Changed:** Reclassification now updates compatibility category, provenance, generated category tags, and FTS metadata atomically without moving source bytes; REST, MCP, Classifier, and React expose explicit outcomes, and bounded legacy repair changes only SHA-proven path metadata.
- **Evidence:** Issue #17; `Reclassification.fs`, `LegacyReclassification.fs`, `ReclassificationApi.fs`, `ApiServer.fs`, `McpTools.fs`, and the three new reclassification test modules.
- **Validation:** Full solution build passed with 0 warnings/errors; 898 .NET tests ran (888 passed, 10 skipped); Core coverage reached 66.84% line / 31.22% branch; React build and changed-file ESLint passed.
- **Blockers:** The pre-existing out-of-sync React lockfile and four unrelated full-lint errors remain assigned to later stabilization work.
- **Next:** Review and merge PR B, then rebase dependent Phase 1A work without expanding into replay or Phase 1B search.

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
