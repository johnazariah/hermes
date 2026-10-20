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
| 1B | Restore file-first FTS, semantic/hybrid, and Osprey reachability | McManus / Hockney | ✅ Done | #11/#6 integrated in #25/#26 with published-head and main proof |
| 1C | Enforce local HTTP/MCP trust boundary | McManus / Keaton / Rai | ⏳ Active | #26 delivers MCP/Host/origin boundary; #28 owns browser/REST/Tray authentication and owner pairing |
| 1D | Stabilize canonical React/Tray assets and SSE | Verbal / Fenster / Hockney | ✅ Done | #27 merged as `db5beec`; #8/#9 closed; exact-head and main Windows/Mac, browser and actual WebView2 lifecycle proof |
| 1D | Promote the Windows Service/Tray installer | Copilot / independent reviewer | ⏳ Active | #16; per-user MSI candidate and blocking disposable-Windows lifecycle gate; no promotion before actual acceptance |
| 1A | Repair legacy saved paths without weakening source authority | Copilot / owner | ⬜ Blocked | #24; [strict repair contract](../design/31-legacy-saved-path-repair.md) requires a protected live namespace or an explicit change of contract |
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

### 2026-10-20 - Preserve the retired Mac command's executable refusal

- **Changed:** Restored `scripts/setup-mac.sh`'s original executable bit after the replacement lost it. Direct invocation now reaches the deliberate support-boundary message instead of failing with permission denied.
- **Validation:** Executing the command returns exit 3 and explains the supported Service/browser path without workload, launchd, application or user-data changes. The script bytes are unchanged.
- **Evidence:** The reviewed candidate is committed as `ac124ac`; its actual target-host acceptance and merge remain pending.
- **Blockers:** Windows/native/installed acceptance remains mandatory; strict legacy repair remains disabled.
- **Next:** Preserve the candidate tree while recording already-verified main ancestry, then push it for PR CI.

### 2026-10-20 - Reviewed candidate prepared for target-host CI

- **Changed:** Staged the complete reviewed access/servicing graph and its synthetic regressions, documentation and blocking CI gates. Applied the required UTF-8 BOM consistently to staged F#/C# sources and removed one trailing blank line; code and line structure are otherwise unchanged by formatting.
- **Validation:** Rebuilt the final Release graph and all four diagnostic/Guard projects without warnings/errors. Setup passes 456 cases and Control 48. Exact coverage collection passes 2,159 F# plus 249 Servicing/model cases at 14,533/17,018 lines (85.3978%) and 15,043/24,636 branches (61.0610%); all seven threshold controls pass. The default solution build and 2,159 Debug cases also pass. Staged filename/credential checks find no prohibited files, generated outputs or credential patterns.
- **Preservation:** The committed local baseline and remote main have exactly the same tree, `7b146c3`, before this candidate. Any ancestry-only reconciliation must retain the candidate tree unchanged; no new main content may be discarded.
- **Blockers:** Target-Windows resource/event/MSI/installed-native acceptance remains unexecuted. No release tag is authorised, and strict legacy repair remains disabled.
- **Next:** Push the candidate and open a draft PR for actual standard hosted Windows/macOS acceptance, retaining the merge gate.

### 2026-10-20 - Native writer provenance correction passes source review and local gates

- **Changed:** A fresh author replaces classic diagnostics with manifest-based ETW Application events. Every retained diagnostic binds native reporter identity to a fresh OS process instance and the expected custom-action host. The disposable hosted-Windows harness owns publisher registration/cleanup; production requires no publisher enrolment.
- **Validation:** All 249 Servicing/model and 48 Control cases pass, zero failures/skips. Four diagnostic/Guard projects cross-build without warnings/errors. Static validation passes 19 payload and 28 adverse report controls. The exact coverage recipe passes 2,159 F# plus 249 Servicing/model cases at 14,533/17,018 lines (85.3978%) and 15,130/24,636 branches (61.4142%), retaining all five assemblies and the unchanged 85/60 gates. The default solution builds cleanly and passes all 2,159 Debug cases.
- **Review:** Independent source re-review approves the corrected native provenance, correlation/report and non-authoritative transport. Primary Microsoft documentation confirms native header/XML reporter PID semantics; target execution is still required.
- **Blockers:** Actual Windows resource compilation, registration/delivery, MSI schemas and installed lifecycle acceptance remain pending. Strict legacy saved-path repair remains blocked and disabled. Remote main remains the verified financial merge.
- **Next:** Publish the coherent reviewed candidate for actual hosted Windows/macOS CI and close target-specific failures before merging.

### 2026-10-20 - Portable installer regressions are wired into both CI platforms

- **Changed:** Added explicit restore/build/test steps for the portable Setup and installer Control suites on Windows and macOS. These projects were absent from the default solution and the Windows-only test project. Ignored generated `.artifacts/` output to keep coverage/build products out of commits.
- **Validation:** The exact new local commands build without warnings/errors and pass 456 Setup and 48 Control cases, zero failures/skips. Static packaging validation passes 19 payload cases, 16 adverse report controls and construction/coordinator/workflow checks.
- **Storage:** GitHub reports zero Actions artefacts and two useful current-main npm caches totalling 80,649,421 bytes. No storage deletion or shared-runner operation was needed.
- **Blockers:** The native writer-provenance correction is in progress; actual Windows/native/installed acceptance and publication remain pending.
- **Next:** Integrate and independently review the native provenance correction before publishing the complete candidate for target-host CI.

### 2026-10-20 - Five-assembly coverage passes; native writer provenance needs correction

- **Changed:** Added 37 portable parser/lifecycle/correlation cases and separated Windows-only diagnostic observation from portable authority/failure accounting.
- **Validation:** The exact CI recipe passes all 212 Servicing/model and 2,159 F# cases, zero failures/skips. Native merging retains all five measured assemblies at 14,447/16,932 lines (85.3236%) and 15,110/24,582 branches (61.4677%); all seven exact-threshold controls pass. Control passes 48 cases. Four diagnostic/Guard projects cross-build cleanly, and the default solution builds without warnings/errors and passes all 2,159 Debug cases.
- **Review:** Independent review approves the kernel/process component and non-authoritative Control/Guard seam, but rejects the diagnostic writer binding. A live process matching JSON PID/start claims is not evidence that it reported the event. A fresh author owns the correction under the scoped reviewer lockout.
- **Blockers:** Actual Windows/native/installed acceptance and the writer-provenance correction remain outstanding. Strict legacy saved-path repair remains blocked and disabled. This work is uncommitted; remote main remains the financial merge `e7758a3`.
- **Next:** Bind native reporting provenance independently, then run the complete disposable-Windows acceptance gate before publication.

### 2026-10-20 - Windows diagnostic components cross-build without claiming runtime acceptance

- **Changed:** Mechanically integrated the actual kernel/process and MSI diagnostic component source, including its non-authoritative Control observation seam and explicitly incomplete evidence model. The underlying authority body and approved root policy remain unchanged.
- **Validation:** Acceptance, process-probe and Windows-test projects cross-build with zero warnings/errors after one explicit `SelectMany` lambda typing correction. An attempted macOS run of four pure report-policy cases cannot resolve the Windows-RID helper assembly; no passing runtime/test evidence is claimed from that attempt.
- **Construction:** The installer now checks real guard/ABI/trust/SDK-free bundles/inventory/root requirements rather than an obsolete unconditional missing-construction message. Static payload and coordinator checks pass. Corrective Windows acceptance covers an owned missing Service DLL and changed owned React index, but has not executed.
- **Blockers:** Five old-engine-outcome diagnostic cases still deliberately report Blocked/exit 3. Independent observer completion and actual disposable-Windows execution remain necessary before publication; the explicit CI acceptance block remains. No new code slice has been merged.
- **Next:** Complete the engine-observer binding and exercise the reviewed Windows construction without promoting incomplete component evidence.

### 2026-10-20 - Corrective repair passes all local regressions and independent root review

- **Changed:** Pre-servicing registered ownership is separated from completed-state payload proof for an enrolled Ready installation. Definitively owned missing/damaged files can reach real repair planning, while missing bindings, unowned collisions, ambiguous paths and incomplete poststates remain refusals. Full verified restoration is still required for Ready.
- **Validation:** All 456 portable Setup cases pass, zero failures/skips; independent root re-review approves the fresh revision and the carried-forward Main/trust/pin-lifetime construction. The complete default solution builds with zero warnings/errors and passes all 2,159 Debug cases. Existing Release/native coverage, exact CI recipe and 61-case browser proof remain green.
- **Windows boundary:** The first actual kernel/MSI diagnostic source deliberately reports five old-engine-outcome cases Blocked with exit 3. It is being cross-built, and the independent observer needed to close those cases is under implementation. Such partial evidence cannot unblock publication.
- **Decision:** The obsolete missing-construction error may be replaced by precise fail-closed build requirements now that the complete construction is compiled and independently reviewed. No runtime, installed-lifecycle, interrupted-recovery, interleaving or publication acceptance gate is removed.
- **Blockers:** Actual dedicated-Windows proof remains outstanding; remote main is still the financial merge. Strict legacy saved-path repair remains blocked and disabled.
- **Next:** Finish and independently exercise the Windows observer binding, then run the reviewed full construction and installed acceptance sequence.

### 2026-10-20 - Exact local 85/60 gate passes and the actual CI recipe is verified

- **Changed:** The pinned native Coverlet tool now collects and merges the actual Core/Service/UI/Servicing reports without changing the source denominator. CI runs the real four-assembly suites and applies integer 85% line / 60% branch gates on both supported platforms. Seven regression controls reject rounding near-misses, omitted assemblies and invalid counters.
- **Validation:** All 2,159 F# cases and 154 portable Servicing cases pass, zero failures/skips. Native merged coverage is 14,127/16,594 lines (85.1332%) and 14,835/24,311 branches (61.0218%). A separately executed, exact workflow recipe passes again with the same line counts and 14,775/24,311 branches (60.7750%). No exclusions or threshold reductions are used. The canonical UI retains 61 actual browser passes and 102 unit passes; installer static validation passes 19 payload cases and contract checks.
- **Installer:** The initial actual aggregate/factory/entry suite passes all 399 portable cases. Independent source review nevertheless finds that corrective repair rejects a definitively owned missing/damaged payload before MSI can restore it. A fresh independent revision separates registered ownership from strict completed-state proof; its new regressions and re-review remain pending. The native interrupted-recovery/interleaving acceptance component is being authored explicitly rather than represented by a placeholder pass.
- **Blockers:** Actual disposable-Windows/native/installed acceptance and final repair-source approval still block MSI construction/publication. Strict legacy repair remains blocked and disabled. Remote main remains the verified financial merge; this local gate milestone is not a claim that the remaining work has shipped.
- **Next:** Verify and independently re-review corrective repair, then execute the complete Windows construction and lifecycle gates before the next merge.

### 2026-10-20 - Typed MCP ownership rejection verified; native coverage approaches the exact gate

- **Changed:** A reproduced staged-source metadata conflict now returns a typed MCP tool error inside the existing writer transaction and publication fence. Legacy guard exceptions, unrelated programming faults, source/history preservation and revision ordering remain intact. Test-only immediate/yielding interpreters exercise real scheduler and transaction callbacks.
- **Validation:** The full Release graph builds without warnings/errors and passes 2,155 F# cases, zero failures/skips. Independent source review approves the publication delta and asynchronous interpreter tests. Native Coverlet JSON merging retains Core/Service/UI/Servicing at 14,127/16,594 lines and 14,550/24,311 branches; the unrounded branch gate needs 37 more covered branches. Servicing passes all 154 portable cases.
- **Installer:** Independently approved OS observation and MSI authoring-validator components pass 328 portable cases. The actual aggregate/factory/entry batch now builds, but its expanded suite is 347 passed and 43 failed out of 390, with zero skips. Synthetic UNC/host path interpretation and early aggregate outcomes need correction before ownership/post-state claims can be accepted; no safety assertion or platform gate is waived.
- **Blockers:** Actual Windows/native/installed lifecycle acceptance and final branch closure remain unfinished. MSI construction is still blocked; no new slice has been published since the financial merge. Strict legacy path repair remains blocked and disabled.
- **Next:** Complete the bounded aggregate correction and MCP effect-conformance variation, then rerun full native coverage and disposable-Windows acceptance.

### 2026-10-20 - Authenticated canonical browser thread passes end to end locally

- **Changed:** The complete Service/owner-CLI/canonical UI thread now uses production route classification and private owner approval. Null-body revocation and HEAD responses retain their transport semantics and generation fencing; browser fixtures verify native option behaviour and restore their own preferences.
- **Validation:** The Release graph builds without warnings/errors and all 2,133 F# cases pass, with zero failures/skips. All 61 real Chromium journeys pass against exact bundle `1ac59912876db25479f308c61d76c64f40f9af766bbea20808a99b0b4ca97c34`, using an isolated rebuilt Service and actual owner CLI. Lint, build and 102 JavaScript units pass. Servicing passes 154 portable cases and Setup 181, both with zero skips.
- **Review:** Independent re-review approves shared canonical routing, admitted-request drain/database ownership, early CLI/default port parsing, authenticated fixture parity and the final stale-instance adapter guard across Starting/Ready/Stopped. The three concrete Setup I/O corrections are independently approved; additive package queries are observations only.
- **Coverage:** Separate correctly collected F# and Servicing reports have matching resolved source identities and a proven Servicing superset. The union retains Core/Service/UI/Servicing at 14,096/16,579 lines (85.0232%) and 13,660/24,245 branches (56.3415%). The exact branch gate still needs 887 covered branches; no exclusions or threshold reductions are used.
- **Blockers:** Windows native/WebView acceptance, installed SDK-free servicing, executable Setup composition and exact CI coverage aggregation remain outstanding. Newly authored Windows observation/validator components are not yet accepted. Strict legacy path repair remains blocked and disabled. Remote main remains the shipped financial merge; no further slice is published.
- **Next:** Exercise the concrete installer evidence components and close the remaining measured branch paths while preparing actual disposable-Windows acceptance.

### 2026-10-20 - Native entry revision approved; concrete Setup evidence still needs correction

- **Changed:** The fresh native integration acquires real startup admission before normal, installed-diagnostic and fixture-helper effects, validates an enrolled compatible child before native launch, and binds navigation waiting to the current engine ID. The actual helper dispatch is shared with tests without linking executable entrypoints.
- **Validation:** Independent source re-review approves all three native paths. Native tests and the BCL Smoke executable cross-build with zero warnings/errors; shared Servicing passes 154/154 portable cases. Concrete Setup I/O builds cleanly and its policy/persistence/inventory suite passes 96/96, all with zero skips. The canonical React source passes lint, 99 units, build and discovery of 61 browser cases.
- **Review:** Setup I/O review finds false absence behind a dangling descendant junction, mishandled unregistered-component results and loose-file source accepted as complete retained media. A fresh eligible author owns the corrections. Separate F# integration review finds dead production UI routing, HTTP continuations omitted from database lifetime ownership and inconsistent default port parsing; its fresh revision also addresses the first combined graph's 40 compiler diagnostics.
- **Preservation:** Only the two owned, already-verified financial export temporaries were removed, reclaiming approximately 392 MiB; durable validation and publication reports remain. No personal archive, shared runner, Git history or remote artefact was changed.
- **Blockers:** No actual Windows acceptance, authenticated canonical-browser execution or newer full coverage measurement is established. Setup remains a non-publishable library with MSI construction blocked; strict legacy repair remains blocked and disabled. No further code slice has reached main.
- **Next:** Exercise the corrected complete F# Host/CLI/real-route graph before the actual browser journey and final source re-review.

### 2026-10-20 - Real owner IPC exercised; failed Setup publication is reported truthfully

- **Changed:** Integrated the real owner-pipe interpreter, bounded authority actor, strict status-only discovery, HTTP protocol/private adapter and outer Service admission source. The fresh Setup policy revision awaits uncancelled reclosure before disposing exclusive ownership and distinguishes confirmed Closed from Unknown after a failed publication.
- **Validation:** The Release .NET graph builds with zero warnings/errors. The combined runtime/IPC/HTTP/startup/V5 selection passes 79/79, and the strict wire/discovery/real-pipe selection passes 127/127, both with zero failures/skips. Setup passes 72/72 portable cases, including 17 new publication/reclosure controls; independent source re-review approves that policy. The latest native graph cross-builds without warnings/errors.
- **Review:** Independent source review approves the actor and F# startup hook but finds stale-instance response encoding can terminate control, malformed escaped Unicode can evade typed HTTP rejection, and framework protocol errors omit no-store. Native integration review finds missing pre-effect Tray/helper servicing admission, an unenrolled Service override path and obsolete navigation completion. Fresh eligible authors own both rejected revisions; passing existing cases are not treated as acceptance of these paths.
- **Blockers:** Real Host/CLI/existing-route integration, actual authenticated browser and Windows native journeys, production Setup composition and the full Windows installer lifecycle remain unfinished. MSI production stays blocked. Strict legacy saved-path repair remains blocked and disabled; the unchanged coverage close gate remains unmet. No new code slice has been published since the financial merge.
- **Next:** Compile and exercise the complete guarded Host/CLI/real-route graph after applying the independently authored boundary corrections.

### 2026-10-20 - Approved Windows A2 and fixed-root policy recorded as architecture

- **Changed:** Added design reference 33 from the approved A2 construction and fixed-root clarification: OS-known-folder `Hermes\App` only, logical reservation before enrollment/after partial uninstall, positive alias-aware outside-installation classification, standard MSI ownership/rollback, retained-media recovery and ordinary-user data preservation. The installer README links the reference and distinguishes its coordinator contract from diagnostic restrictions.
- **Validation:** Documentation-only change; local reference links checked. No C#/F#/PowerShell/WiX/workflow changes or runtime/installer operations were made, and no source acceptance or promotion is claimed.
- **Blockers:** Fixed-root enforcement and source/development classification remain implementation/source-review work for the eligible language authors; the F# outer hook waits for the typed classification API. Full Setup orchestration and actual Windows acceptance remain outstanding. The unconditional MSI build block is unchanged.
- **Next:** Review the language-authored construction against reference 33 before integrating startup hooks, coordinator operations, native exports and authoritative Windows Installer sequencing.

### 2026-10-20 - Financial slice reaches main; checkpoint storage pruned

- **Changed:** #29 merged normally as `e7758a3797dc6ca5d52ae1ac573a2a4802f0a27a`; remote main has the exact reviewed tree. The zero-skip synthetic financial/parity milestone is delivered. #18's three proven fixture/parity items are checked; its final coverage gate remains open.
- **Evidence:** Exact published-head CI passes Windows 1,714/1,714 and Mac 1,718/1,718, with no failures/skips, plus all 45 browser and 21 JavaScript cases per OS. Actual Windows passes 100 native cases and owned-PID 4312 WebView2/graceful-stop proof. Main run `39362778578` passes all seven jobs; both exact published bundles pass all 45 browser cases before upload.
- **Validation:** Integrated Windows root retains Core/Service/UI at 11,108/13,209 lines and 10,581/20,820 branches. Four verified checkpoint artifacts totalling 185,919,374 bytes are pruned; zero Actions artifacts and two useful main npm caches (80,649,421 bytes) remain. No paid/larger runner or shared Pelican runner was used.
- **Separate progress:** The uncommitted authority/wire/email foundations graph passes all 1,849 Mac cases with zero skips; root is 11,754/13,901 lines and 11,275/21,350 branches. Fresh authority-collision review approves bounded recent-token non-resurrection. Browser source review approves the third PDF revision; real authenticated application/native journeys remain unproved. Servicing-library and control-slice portable cases pass, but startup classification and full installer orchestration are incomplete.
- **Decision:** Supported Windows installation uses the existing fixed per-user `LocalAppData\Hermes\App` root; relocation is not added. Independent architecture review approves positive outside-root source/dev classification with mixed/ambiguous placements refused. This changes no configuration/archive location and does not authorise a missing-gate bypass.
- **Blockers:** HTTP/CLI/owner-pipe/native authentication, actual installer lifecycle and the unchanged 85%/60% close gate remain unfinished. Strict legacy saved-path repair remains open, blocked and disabled.
- **Next:** Complete and independently exercise the bounded authority/runtime and installer admission threads without folding unfinished code into the shipped financial slice.

### 2026-10-20 - Exact financial slice passes all cases; unfinished features stay separate

- **Changed:** The bounded financial/Workflow/API slice preserves exact PDF dates and monetary values, recovers after transient dual-write failure, makes correction requests atomic, validates pagination and missing resources, and projects current corrected vendors through linked reminder reads. Separate authentication/UI/installer work is not included in this code slice.
- **Evidence:** An owned export of staged code tree `8a188c010f2a214e6a2f357954e75c12224e0345` excludes private financial fixtures and unfinished features. New reminder controls exercise two real correction requests, latest document/REST/MCP values, unchanged reminders/history and the exact existing provenance-tag effect without broadening ignored fields.
- **Validation:** Fresh baseline React installation/build pass; the exact exported Release graph has zero warnings/errors and all 1,718 cases pass, with zero failures/skips. Root remains Core/Service/UI at 11,108/13,209 lines and 10,698/20,820 branches. Independent Workflow re-review approves the fresh revision; narrow reminder re-review and actual replacement-head Windows/Mac CI remain required.
- **Separate progress:** The reviewed access design has pure domain/model foundations with 42 passing cases; HTTP/CLI/pipe/native integration is not thereby proved. Native client and standalone installed-Tray harness cross-build cleanly; SDK-free harness publication contains no replacement Tray/WebView assemblies. The fresh standard-MSI/coordinator architecture is approved for implementation only, and package creation remains fail-closed.
- **Blockers:** The 85%/60% close gate, authenticated end-to-end journeys and actual installer lifecycle remain unfinished. Strict legacy path repair remains blocked and disabled; no weaker recovery contract or personal-corpus access is assumed.
- **Next:** Publish and integrate only the reviewed financial slice on passing exact-head cross-platform checks while continuing the separate unfinished work.

### 2026-10-20 - Native-authored owner fixture integrated without admitting existing profile state

- **Changed:** Mechanically applied ec93's six-block B owner-fixture follow-up. Reconstruction of both durable native proposals confirms all nineteen authored blocks across fifteen files remain verbatim; no C# content was generated by the installer integration author.
- **Integration:** The native helper, not PowerShell, creates a new attested fixture at the genuine absent owner-config path. PowerShell creates the synthetic archive below the disposable runner's temporary root, retains one job-bound receipt across every transition, requires verified owner state/fixture ID/hashes, and hashes configuration, settings, marker and receipt before and after servicing. No automatic owner-profile deletion was added; original-failure/native-report persistence remains ahead of MSI failure cleanup.
- **Validation:** `pwsh -NoProfile -File installer/windows/Test-Static.ps1` passed the fourteen payload controls plus script/XML/project checks, single fixture-creation-before-baseline and no PowerShell owner-config-write checks, MSBuild source/hash capture, workflow lint, excluded Mac-wrapper syntax and whitespace. The parent's earlier clean cross-build predates this fixture follow-up; no latest native build, discovery, unit run, MSI or Windows execution was performed here.
- **Blockers:** Native source review and dedicated Windows proof remain pending. Servicing A remains unimplemented, and the unconditional MSI production error is unchanged; no unreviewed architectural proposal or custom action was implemented.
- **Next:** Obtain language/source review of the fixture and integration, then validate the latest native graph separately from the still-blocked installer lifecycle.

### 2026-10-20 - Installed-native B proposal mechanically integrated; servicing A remains architectural

- **Changed:** Mechanically applied all thirteen C#/project/test blocks authored by `ec93e27c-96ce-4a56-b5ac-8f2459a3c202`, with exact-byte reconstruction against the original HEAD confirming verbatim application. The installed apphost now contains the authored diagnostic entry; the external harness has no Tray assembly reference. No C# revision was generated by the installer integration author.
- **Integration:** PowerShell uses the actual installed-Tray CLI, a bounded diagnostic hold, new absolute runner-temporary report/signal paths, native start/hash checks and bounded evidence copying. CI relocates real WebView/ownership proof to the mandatory installed lifecycle gate. The unagreed net48 custom-action proposal is withdrawn.
- **Validation:** `pwsh -NoProfile -File installer/windows/Test-Static.ps1` passed all fourteen synthetic controls, MSBuild inventory metadata/hash capture, PowerShell/XML parsing including native projects, external-harness reference checks, workflow lint, excluded Mac-wrapper syntax and whitespace. Native compilation, native discovery/execution, MSI construction and installation were not run.
- **Blockers:** A is unresolved shared/exclusive admission across every entrypoint, including direct Service launches and an ungated-binary migration decision; no CA project/binary/export/property/sequence contract exists. The MSI project now emits an unconditional error until independently reviewed construction replaces the gate. B also has a reported language-owned conflict: clean-owner diagnostic admission rejects the synthetic owner-config fixture used to prove preservation.
- **Next:** Obtain the language-authored fixture/admission resolution and mandatory C# review, while keeping MSI production blocked until A is constructed and reviewed; only then run dedicated disposable GitHub-hosted Windows acceptance.

### 2026-10-20 - Owner access contract approved; runtime findings remain explicit

- **Changed:** Independent review approves the fresh unified [local HTTP access contract](../design/32-local-http-access.md). It distinguishes preview transport/browser Origin from API audience, uses standard same-owner pipes, closes startup admission until control readiness, and binds native pairing to the current navigation and owned process. Separate F#, JS and C# implementation scopes have started; no authenticated runtime result is claimed.
- **Evidence:** The pre-Service-route-delta Release graph builds with zero warnings/errors. The first focused synthetic run is 151 total: 145 passed, six failed, zero skipped; it includes 102 MCP, 12 parity and 37 new Core cases. Five PDF failures expose numeric/date word splitting despite correct raw letters. One Workflow failure and independent review establish lost continuation and altered exception-cause observability; a fresh author owns that rejected revision.
- **Blockers:** Installer review rejects the original harvesting, payload safety, direct-MSI refusal and installed-Tray evidence. The independent PowerShell/WiX revision passes fourteen static controls but still refuses packaging without a reviewed transaction/startup fence. Actual installed-Tray diagnostics are language-authored but uncompiled; the servicing construction remains an architectural blocker, not a missing export to invent. Strict legacy repair remains blocked as recorded in #24.
- **Next:** Apply and exercise the scoped Service contract corrections, repair the proven PDF word boundaries and Workflow recovery, and integrate authority/native servicing only through their reviewed contracts.

### 2026-10-20 - Independent Windows installer source revision remains native-gated

- **Changed:** Replaced glob harvesting with SDK-source/hash inventory validation, canonical React anti-substitution and deterministic per-file HKCU-keypath components. Added complete lifecycle inventory/registration/shortcut/Run assertions, changed-assembly upgrade requirements, explicit rollback, related-product preflight, unknown-file preservation checks and bounded failure/native evidence saved before MSI cleanup.
- **Evidence:** Independently revised Windows WiX/PowerShell/workflows and installer README; no F#/C# source, private configuration, provider, live application, installer action or Git publication was touched. Packaging now fails closed while the native servicing-lock WiX integration is absent.
- **Validation:** `pwsh -NoProfile -File installer/windows/Test-Static.ps1` passed: fourteen synthetic payload/version/identity controls, script/XML parsing, real MSBuild source metadata/hash capture, workflow lint, excluded Mac-wrapper syntax and diff whitespace. No dependencies were installed.
- **Blockers:** Fresh language-authored native servicing-lock/startup-fence and actual-installed-Tray smoke contracts are still required. Actual Windows ICE-clean MSI build and disposable-profile install/changed-payload upgrade/explicit rollback/uninstall remain unrun; independent reviewer approval remains required.
- **Next:** Integrate the independently authored native contract without relaxing the fail-closed package gate, then obtain source review before the parent runs dedicated GitHub-hosted Windows acceptance.

### 2026-10-20 - Canonical delivery integrated; remaining work retains explicit gates

- **Changed:** #27 is merged as `db5beec014b0d7b932475c5d954edd54b96bfaf6`; #8/#9 are closed against their actual acceptance criteria. Remaining work is synthetic financial parity and coverage (#18), Windows packaging (#16), authenticated browser/REST access (#28), and the separately governed legacy repair (#24).
- **Evidence:** Final published-head Windows runs 1,567 cases: 1,560 passed and seven retained skips; Mac runs 1,571: 1,564 passed and seven skips. Each OS passes all 45 browser cases. Actual Windows passes all 100 native cases and WebView2 smoke with six routes, 30 ownership checks and graceful unforced shutdown. Main run `39339871190` passes all seven jobs; its exact published bundles each pass all 45 browser cases.
- **Validation:** Main root coverage is 10,623/13,116 lines and 9,958/20,728 branches across Core/Service/UI. Seven checkpoint artifacts (186,365,519 bytes) and four obsolete caches (194,863,267 bytes) were pruned; zero Actions artifacts and two warm main caches (80,649,421 bytes) remained.
- **Work in progress:** Synthetic PDF/CSV parity and meaningful workflow/pipeline regression proposals are applied, but the current combined graph fails new-test nullability/type diagnostics. Installer scripts/XML pass syntax checks only; actual WiX validation and disposable Windows installation have not run. The simplified OS-owner-pipe access design and installer candidate are under independent review; neither is accepted runtime evidence.
- **Blockers:** Strict archive-wide uniqueness cannot be guaranteed during saved-path repair while uncontrolled writers may change the live namespace. The documented fail-closed decision preserves the existing requirement; no repair is enabled and #24 remains open. Authentication, the 85%/60% coverage gate and actual installer acceptance remain unfinished.
- **Next:** Resolve the scoped test compilation failures, execute the synthetic parity and regression cases, and complete independent design/installer review before publishing the next bounded slice.

### 2026-10-20 - Real Windows native proof passes; parent SQLite fixture lifetime corrected

- **Changed:** Corrective head `86ef314` passes actual Windows native units (100/100, zero skips) and desktop/WebView2 smoke. The report identifies owned PID 1436, all six canonical routes, module byte proof and 30 live identity checks, followed by graceful unforced exit 0 and successful synthetic cleanup.
- **Evidence:** The native first probe records explicit connection refusal in 2,083 ms, beyond the earlier two-second request budget. Mac full .NET and all 45 browser cases pass. Windows Service selection runs 1,567 cases: 1,559 pass, seven existing skips and one remaining restart-fixture cleanup error on `db.sqlite`.
- **Correction:** The parent test helper's pooled SQLite handle outlives disposal on Windows. A fresh F# change uses an explicitly nonpooled connection with archive authority and scoped disposal. Independent review approves it; restart/PID/ledger/watermark assertions, production pooling and cleanup failure behavior are unchanged.
- **Validation:** Local full execution is 1,571 total, 1,564 passed, seven retained skips and zero failed, with a clean build. Exact root is 10,624/13,116 lines and 9,964/20,728 branches, preserving non-regression.
- **Blockers:** The revised Windows restart/cleanup case and all current-head checks must pass before merge; the successful native job does not override the remaining failed gate.
- **Next:** Push the reviewed fixture-only correction and conclude the exact-head Windows/Mac gates.

### 2026-10-20 - Bounded Windows transport corrections approved for replacement CI

- **Changed:** A fresh F# fixture revision distinguishes individual probe timeouts from its unchanged 20-second startup deadline; cancellation and PID/status failures still propagate. The eligible native revision author adds bounded five/eight-second connection/request policy, explicit connection-refusal-only free classification, and safe first/last transport diagnostics. Uncertain endpoints and real listeners remain occupied.
- **Evidence:** Independent F# and C# reviewers approve their narrow deltas. The native tests add real exclusive non-listening socket, active/silent listener, wrong identity and cancellation controls; neither source review nor budget adjustment is claimed to establish the first Windows failure's root cause.
- **Validation:** Eight focused readiness/process cases pass locally, as does the full 1,571-case Mac suite: 1,564 passed, seven retained skips, zero failures. Exact root coverage is 10,623/13,116 lines and 10,018/20,728 branches, preserving scope and non-regression. Native projects cross-build cleanly; actual replacement Windows unit/desktop/runtime proof remains pending.
- **Blockers:** Draft #27 is not merged. The original 84 Windows native passes do not prove the revised transport or desktop thread; the failed gate remains red until the corrective head passes.
- **Next:** Publish the approved bounded delta, collect actual current-head Windows/Mac evidence and integrate only on passing ownership, UI and lifecycle checks.

### 2026-10-20 - Published canonical slice passes Mac; Windows runtime findings remain gated

- **Changed:** The independently approved canonical UI/lifecycle tree was pushed normally at `83f627874` and opened in draft #27, targeting main with only #8/#9 closing references. Main remains `5aa929be`; no failed head is integrated.
- **Evidence:** Actual Mac CI passes the full 1,568-case .NET suite and all 45 browser cases. Windows executes the truthful 1,564-case selection: 1,555 pass, seven retained skips and two readiness-helper failures. The separate actual Windows native suite passes all 84 cases with no skips; both compatibility shell builds also pass.
- **Blockers:** Two owned-process tests abort when the two-second HTTP probe times out before their overall startup deadline. Native desktop smoke reports `ForeignListener` before child launch, so WebView/root/native shutdown proof is not established. This is not treated as flaky or bypassed. A fresh F# fixture author owns bounded readiness semantics; the eligible native revision author owns safe preflight diagnosis without assuming an uncertain port is free.
- **Next:** Resolve the Windows findings with meaningful regression controls, publish the reviewed delta and rerun actual replacement-head gates before merge and artifact pruning.

### 2026-10-20 - Canonical React and owned lifecycle source approved for publication

- **Changed:** Fresh independent revisions close browser source-identity collisions, failed-journey state and palette sync feedback; strict module preflight and pipe-only admission; native terminal ownership proof and guarded external-link callbacks. A separate inventory author marks the four Unix-only cases and excludes them from Windows selection without adding skips or weakening gates.
- **Evidence:** Browser, F# and C# reviewers approve their final scopes. Real Settings and palette requests each wake the single producer, register a new synthetic document, retain exact archived bytes and display its detail. Native smoke now binds every proof and final graceful stop to the same owned attempt/PID, rejecting foreign replacements and stale stop results.
- **Validation:** Final Mac Release graph has 0 warnings/errors; all 1,568 cases run with 1,561 passed, seven retained skips and zero failures. Exact root coverage is 10,624/13,116 lines and 9,967/20,728 branches across Core/Service/UI, restoring line non-regression. Windows discovery selects 1,564 cases; its runtime proof remains pending. All native projects cross-build cleanly; clean npm install, 21 unit cases, lint, build, reproducibility/discovery and all 45 actual browser cases pass.
- **Artifact proof:** The exact locally published Service/bundle passes all 45 browser cases. Republish removes a stale synthetic asset; missing `dist` fails both build and `publish --no-build` with actionable diagnostics. The bounded synthetic browser preview was inspected and stopped. Legacy screenshot files are preserved without viewing them or including them in new upload paths.
- **Blockers:** Actual current-head Windows unit/desktop/WebView2 smoke, Mac CI, integration, main bundles and storage cleanup remain. No source is merged or product surface promoted solely from cross-build/local evidence; broader session authentication and the #18 wave-close gate stay separate.
- **Next:** Commit and publish the approved tree, run the actual cross-platform/native gates, and integrate only the exact passing head.

### 2026-10-20 - Canonical React delivery started from verified retrieval main

- **Changed:** #26 merged as `5aa929be`; all five replacement-head and seven main build/test/publish jobs passed. Closed #6 against its real transport criteria. Pruned five checkpoint artifacts (186,750,531 bytes); zero artifacts and two warm main caches (114,212,113 bytes) remain. Phase 1B is complete.
- **Evidence:** The next #9/#8 candidate repairs the npm lockfile, emits a deterministic React manifest into `Hermes.Web/dist`, replaces Blazor root mapping with deployed-bundle-only hosting, wires Pipeline/Chat navigation, shares a tested SSE consumer and removes the dead pipeline-status hook. Separate language authors supplied Service lifecycle/manual-sync and native owned-process/Tray smoke proposals.
- **Validation:** Clean `npm ci`, lint, TypeScript/Vite build and 11 SSE unit cases pass. Repeated builds produce identical asset digests. Browser discovery is 33 supported cases, with 12 historical Blazor definitions explicitly excluded. The initial real browser run passed 21/30; its nine navigation/fixture failures are corrected but await rerun. The first host full run was 1,491 total, 1,483 passed, 1 failed and 7 retained skips; the isolated deployment metadata correction now passes its targeted actual-process case.
- **Blockers:** Native/lifecycle patches, complete browser/.NET coverage reruns, independent reviews and actual Windows desktop proof remain. The candidate is not published. Broader session authentication, old parity fixtures and the 85%/60% wave-close gate are not claimed complete.
- **Next:** Finish the full browser-to-Service and Tray-to-owned-Service thread, then publish and merge only after current-head cross-platform gates pass.

### 2026-10-19 - Windows live-file hash fixture corrected without weakening assertions

- **Changed:** The first published #6 head `ec3fc784` passed macOS and all three desktop builds, but Windows failed two physical read-immutability cases because `File.ReadAllBytesAsync` conflicts with the open SQLite writer's sharing mode. The eligible revision author supplied a test-only asynchronous read stream with explicit `FileShare.ReadWrite`; production/schema remain unchanged.
- **Evidence:** The same existing database/WAL filenames and every byte remain hashed. Two controlled tests hold live writer handles, assert exact filename-to-digest maps, and detect same-length database changes and empty-to-nonempty WAL changes. Existing actual HTTP/MCP physical-hash assertions are untouched; there is no checkpoint, shutdown, retry or skip.
- **Validation:** Local Release graph has 0 warnings/errors. Full execution is 1,389 total, 1,382 passed, 7 retained skips, 0 failed; exact root coverage remains 10,283/12,753 line, with 9,557/20,265 branches. This local result does not substitute for Windows proof.
- **Blockers:** Narrow independent fixture re-review and replacement-head actual Windows/macOS CI remain. The failed gate is not bypassed or treated as flaky.
- **Next:** Publish the approved test-only delta and merge only after the replacement head passes all required checks.

### 2026-10-19 - Typed retrieval source independently approved for publication

- **Changed:** Independent F# re-review approves the separate-author revision and closes all three initial findings. The source preserves stage-specific provenance, legal keyword callers, explicit vector dependency failures, unknown-tool compatibility and authenticated opt-in mutation permissions.
- **Evidence:** The final post-BOM graph builds with 0 warnings/errors; its full instrumented run is 1,387 total, 1,380 passed, 7 retained skips, 0 failed. Exact root counters are 10,283/12,753 lines and 9,558/20,265 branches across the unchanged Core/Service/UI scope. The testing register includes the executed 113-case addition and final disposition.
- **Blockers:** Published-head Windows/macOS proof, integration and main-bundle/storage verification remain. Approval does not waive these gates or establish broader REST session authentication or unattributed shared enrichment.
- **Next:** Commit the reviewed tree, retain verified main ancestry, open the templated PR and integrate only after exact-head CI passes.

### 2026-10-19 - Retrieval provenance revision passes the preserved suite

- **Changed:** A separate F# author corrected all three initial findings. Schema 17 records an explicit original-publication link atomically during embedding-only reflow; reads distinguish current document generation from canonical publication generation. Keyword callers retain positive Int32 limits, vector modes remain bounded, and unknown tools are resolved independently of mutation permissions.
- **Evidence:** Eleven new test modules contribute 113 cases above the integrated baseline. Real HTTP/MCP and physical SQLite/WAL assertions cover full persisted payslip fields, manual metadata, original digests, repeated reembed before/after actual embedding, sibling isolation, source/read races, invalid origins, additive migration and full rollback on carry failure. Existing snippet and removed-tool compatibility assertions remain unchanged.
- **Validation:** Both revised and final BOM-normalised Release graphs build with 0 warnings/errors. The final full run is 1,387 total, 1,380 passed, 7 retained skips, 0 failed; exact root coverage is 10,283/12,753 line and 9,558/20,265 branch across Core/Service/UI, improving the integrated line baseline without exclusions or gate changes.
- **Blockers:** Independent re-review and actual published-head Windows/macOS CI remain. Shared enrichment without document attribution and historical generation gaps remain explicitly unavailable; broader REST/browser/Tray session authentication and the #18 wave-close gate are separate.
- **Next:** Conclude independent review, publish the reviewed tree, verify exact-head cross-platform CI and merge, then verify main bundles and prune the checkpoint's artifacts.

### 2026-10-19 - File-first search integrated; retrieval currentness revision required

- **Changed:** PR #25 merged into `main` as `df0cad9b`, completing #11. Its reviewed portable fixture correction passed actual Windows/macOS checks; all seven main build/test/publish jobs passed. Pruned five failed/success/main artifacts totalling 186,563,632 bytes; zero artifacts and the two useful main caches (114,212,113 bytes) remain.
- **Evidence:** The next #6 candidate wires typed search modes, source-grounded persisted reads, default read-only MCP capability discovery and explicit Host/origin checks through the real HTTP composition. Shared enrichment without document attribution remains explicitly unavailable. No personal archive or provider was accessed.
- **Validation:** Release full graph has 0 warnings/errors and 59 new focused cases pass. The preserved full run executes 1,333 cases: 1,324 passed, 2 failed, 7 retained skips. Diagnostic root coverage is 10,167/12,677 line and 9,470/20,207 branch with Core/Service/UI scope unchanged.
- **Blockers:** Independent review rejects loss of still-current comprehension after `reembed`, the new cap on existing large-limit keyword callers and incorrect classification of removed/unknown tools as policy-denied. The initial author is locked out; a separate F# author owns the revision and corrected real-transport regressions. This candidate remains unpublished.
- **Next:** Prove stage-specific publication provenance and compatibility without weakening read fences or authorization, then repeat full coverage/review and publish only on passing exact-head CI.

### 2026-10-19 - Published search Windows fixtures corrected without relaxing production

- **Changed:** The first published search head passed macOS and all builds, but Windows had 20 failures: 19 cascaded from drive-qualified canonical paths not matching fake `/archive` keys, and one asserted a hardcoded Unix separator. The actual Windows CLI-process tests using real files passed. Applied the eligible author's test-only fake-path alias/home-expectation correction; no production file changed.
- **Evidence:** The fake normaliser aliases only current-DOS-drive absolute paths to existing rooted keys, retaining other volumes, UNC, relative, doubled and unrelated roots. Denied/oversized read injections compare the same normalised identity. Three portable regressions exercise text/bytes/size/hash, directory/list/move/delete and distinct keys.
- **Validation:** Release build has 0 warnings/errors; all 38 focused fixture/failure/authority cases pass locally. Full local execution is 1,274 total, 1,267 passed, 7 retained skips, 0 failed; exact root coverage remains 9,712/12,166 line and 8,868/19,432 branch, with source/gates/instrumentation unchanged.
- **Blockers:** Narrow independent fixture re-review and actual replacement-head Windows/macOS CI remain required. A local pass is not Windows proof, and the red published gate is not bypassed.
- **Next:** Commit the reviewed test-only delta, push normally, run current-head CI and integrate the search/operator feature only on passing checks.

### 2026-10-19 - File-first keyword search and local operator approved for publication

- **Changed:** Independent review now approves the final #11 source. The last separate-author revision resolves preferred content snippets for selected identities independently of ranking prefixes; ordinary, unified, email and paged keyword searches share the corrected query. Earlier operator-authority, pagination and source-identity race findings remain closed.
- **Evidence:** Actual intake/V5 publication, HTTP search, existing document-query routes, HTTP MCP and actual CLI-process tests exercise the full thread. Source identities/snippets stay stable across page sizes and legal source reassignment cannot certify stale output. Schema 16 is additive across fresh/partial and v8/v12/v14/v15 startup; strict operator configuration never substitutes a default archive.
- **Validation:** Release full graph has 0 warnings/errors. Final full execution is 1,271 total, 1,264 passed, 7 retained skips, 0 failed; 130 new cases replace only the three obsolete body-text skips. Exact root coverage is 9,712/12,166 = 79.8290% line and 8,861/19,432 branch, including Core/Service/UI and improving the prior baseline without exclusions or weaker gates.
- **Performance:** Synthetic Core/in-memory SQLite + MemFs only: 1,000 immutable sources, batches at most 100, 40 queries after one warmup; indexing 2,327.237 ms, query p50/p95 0.141/0.167 ms. Hashes, claims, IDs and snippets are asserted; no production threshold, private corpus or live-provider claim is made.
- **Blockers:** The source remains uncommitted/unpublished; exact-head Windows/macOS CI must still pass before merge. Semantic/hybrid/persisted read-only Osprey and the wider trust-boundary work remain separate #6 scope; the full wave gate remains #18.
- **Next:** Commit the approved search/operator tree, preserve the merged main ancestry, open its PR with the repository template, verify the actual head and merge only after passing gates. Prune the integration's unneeded artifacts at the checkpoint.

### 2026-10-19 - Search safety findings closed; source-snippet stability still gated

- **Changed:** The separate revision author corrected explicit operator archive authority, SQL pagination after ranking/deduplication, and generation/completion invalidation for legal source-identity reassignment. Independent re-review closes those three findings, including deterministic V5 interruption points before index, output and completion publication.
- **Evidence:** Forty-two new revision/benchmark cases pass; the revised full run executes 1,270 cases with 1,263 passed, 7 retained skips and 0 failed. Root coverage is 9,706/12,162 lines and 8,854/19,466 branches with assembly scope/gates unchanged. A synthetic 1,000-source Core/MemFs benchmark records bounded rebuild 2,852.734 ms and 40-query p50/p95 0.078/0.094 ms, with hashes/claims/identity assertions.
- **Blockers:** Re-review found a remaining P2: a metadata-strong document's preferred source snippet can depend on the content prefix selected by page size. Locked out that revision author and assigned a third F# author the minimal shared-query/snippet correction and real SQLite/HTTP/MCP skew regression. No search code is published.
- **Validation:** Coverage main `d830c610` has now passed all seven jobs after an infrastructure-only Windows retry; pruned its four retained artifacts (186,158,183 bytes). Zero artifacts remain and the two useful main caches remain at 114,212,113 bytes. The stalled restore/cancellation race was resolved without source or gate changes.
- **Next:** Close the final snippet-stability finding, rerun focused/full gates and independent review, then publish the file-first search/operator slice with accurate synthetic scope and no personal-corpus access.

### 2026-10-19 - Coverage integrated; search candidate quarantined after independent review

- **Changed:** PR #21 merged into `main` as `d830c610`, adopting the reviewed test-only recovery. PR #22 is incorporated through retained ancestry; closed #23 as superseded with all four watcher cases adopted. The functional #11 candidate now wires content FTS, real HTTP/MCP queries, currentness invalidation and a bounded local dry-run/apply CLI, but remains unpublished.
- **Evidence:** Published coverage head `b46cc3a` passed all five Windows/macOS build/test jobs; both OS suites executed 1,144 cases with 1,134 passed, 10 existing skips and 0 failed. Windows root line coverage is 9,155/11,604. Main's hosted Windows restore stalled; a transport timeout was distinguished from CI failure, then only the unchanged Windows job and dependants were retried after cancellation recovery. Main publish/storage verification remains pending.
- **Validation:** Initial search full-graph build has 0 warnings/errors; 87 new focused cases pass, and the full 1,228-case run has 1,221 passed, 7 skipped and 0 failed. Root line coverage is 9,632/12,095 with Core/Service/UI scope retained. These are diagnostic results, not acceptance.
- **Blockers:** Independent review rejected operator YAML fallback selecting an unintended default archive, pagination losing results beyond the first 100, and in-flight source-A extraction republishing under source-B identity. Locked out the first author, including benchmark work; a fresh F# author owns all three corrections, their regressions and bounded synthetic performance measurement.
- **Next:** Resolve the independent findings without weakening source/metadata safety, re-review and run the real HTTP/CLI/full-suite gates before publishing #11. Do not use personal financial data or claim tax-document readiness while this candidate is quarantined.

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
