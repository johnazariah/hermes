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

### 2026-10-28 - Approve the bounded readiness source after strict-context correction

- **Implemented:** The fixed metadata bracket checks loaded `ntdll`, the explicit target type and field width at the same owned stop before the unchanged typed flag query. Current/event/source receipts remain bound; the bracket adds no flag authority, forced reload, target write or resume. Ordinary deferred symbol loading may occur.
- **Review correction:** Independent review finds that wrong-part/unknown-source bare diagnostics could fall through the old parser without taint. A fresh author adds a post-legacy refusal fallback, preserving old first-fault classifications. New end-to-end cases prove a later valid flag cannot cause ACK/continuation after either rejected diagnostic. Final source review approves.
- **Validation:** All 556 startup and 142 encryption controls pass, retaining prior intents, alongside unchanged Setup contracts, workflow lint and root build/tests (2,198 Core, zero skips or warnings/errors). Exact inverse hashes pin the untouched original driver/module logic.
- **Limits and next:** These are portable/source results, not native type readiness. Ask interactive approval to publish and execute the single ordinary-CI readiness check. Do not start another encrypted capture, change the query/stop or treat unavailable metadata as a pass.

### 2026-10-28 - Keep the unresolved typed query explicit after its one verification

- **Published and executed:** John approves publication and verification of the qualified query. Revision `e359c43`, ordinary PR CI `40595687726`, actual installer job `122510892026`, passes all 545 controls but does not establish a typed flag value. The original gate remains 500/501, zero skipped.
- **Numeric outcome:** Existing report artifact `12775898703` contains only bounded `startup-debug.json`. First/stage reasons are 1/1, stages 1, evaluator 1, echo mask 64, read kind/source/lexical/trim/wording/token = 5/0/144/5/1/0. This identifies a resolver-family refusal after qualification, not proof of type availability or a particular PDB/network/loader cause. No successful repair is claimed.
- **Unchanged boundaries:** Both application jobs, Tray and compatibility pass; installer lifecycle and publish remain skipped. Encrypted-capture validation/upload are skipped, with no second ciphertext artifact. No raw-line capture, manual rerun, parser tolerance, offset fallback or token-policy change occurs.
- **Next:** The retained author performs read-only mapping of type/symbol prerequisites at the owned CREATE_PROCESS stop and proposes one precise next action for interactive approval before any further implementation or run.

### 2026-10-28 - Qualify the explicit target-type query

- **Changed:** A fresh PowerShell author qualifies the PEB field through `ntdll!_PEB` in both the exact width guard and typed read. Generated source, fixed echo/read comparisons and lexical fingerprints remain consistent. No offset fallback, ignored error, SDK-operation change or acceptance substitution is introduced.
- **Review and controls:** Direct bounded source review verifies the precise driver/module delta and coupled negative controls. All 545 startup cases pass, retaining the previous 542 intents; missing types/member errors, old unqualified sources and stages without an actual typed result remain refusals. The 142 encryption controls, unchanged Setup contracts, workflow lint and root build/tests (2,198 Core) also pass.
- **Limits:** The explicit type may still be unavailable at the owned CREATE_PROCESS stop. Portable controls do not establish native resolution or loader cause. The proposed verification is one ordinary PR CI run, not manual dispatch, so it will not capture another line.
- **Next:** Obtain interactive publication approval for this qualified query revision and read the actual guarded typed-query result before choosing any further change.

### 2026-10-28 - Authenticate the one-line result and approve the typed-query repair

- **Executed:** Reviewed capture revision `398356d`, genuine manual run `40590286308`, actual Windows 2022 job `122492452084`, passes the new controls and ciphertext validator/upload. Original native suite remains 500/501 with zero skipped; the trace still refuses at point 26. Artifact `12774302423` contains only the fixed ciphertext file, 909 bytes, SHA-256 `a8c159e0b0814428ff3a8ef4966b0dde63828a885dbb3c252f430fde3b06d598`.
- **Private inspection:** Local decryption verifies the independently computed reviewed-source digest, revision/run/attempt/recipient identity and GCM authentication before interpreting one strict UTF-8 line. No plaintext file, private-key upload or public raw-line quotation is made.
- **Grounded diagnosis:** The returned line is a genuine C++ type-member-resolution error at the `NtGlobalFlag` access used by the width/read query. It is not a legitimate echo; the old refusal is correct. This explains the diagnostic blockage, not the original PowerShell initialization failure.
- **Interactive next approval:** John explicitly selects "Yes—repair and verify the debugger query". The scope retains target identity, typed DWORD/width/value proof, all protocol/continuation rules and original installer checks; it excludes hard-coded offsets, ignored errors, token/host-policy changes and any second encrypted capture.
- **Next:** A fresh PowerShell author owns a documented explicit typed-query correction and all coupled source/reader controls. Independent review and local qualification precede its one ordinary-CI verification, with encrypted capture disabled in that non-manual context. No runtime fix is yet claimed.

### 2026-10-28 - Approve publication and the single encrypted capture run

- **Qualified:** The fixed first-refusal hook, in-memory hybrid encryption, source/run-bound envelope and separate ciphertext-only upload pass all 142 new controls, 542 existing startup controls, unchanged Setup contracts, workflow lint and root build/tests (2,198 Core, zero skips or warnings/errors). Independent review approves the source; genuine capture remains unproved.
- **Interactive approval:** After qualification, John explicitly selects "Yes—publish and run the single capture". This authorises commit/push to the existing PR branch, one manual public Windows 2022 capture, then local authenticated decryption/inspection of that one line. The PR remains draft/unmerged; no release, wider log, acceptance relaxation or host-policy change follows.
- **Next:** Publish this exact reviewed revision, bind the actual manual run and ciphertext to its independently computed source context, then decrypt locally only after envelope and GCM verification. Report the resulting diagnosis without publishing the raw line.

### 2026-10-28 - Authorise the bounded encrypted first-refusal capture

- **Explicit decision:** In interactive mode, John selects "Yes—authorise this encrypted one-line capture" for the exact proposed scope: only the first refused debugger line from the disposable synthetic test, at most 1 KB, encrypted before upload; the private decryption key remains on the Mac and outside GitHub. No wider log, personal data, host setting or acceptance change is approved.
- **Latest numeric result:** At `a8c2382`, CI `40260910592`, actual installer job `121372888147`, all three token-stage observations are available without errors. Source and post-filter integrity are 12288; post-Medium integrity is 8192; effective restricted-SID count is zero at all stages. The held-source control again exits 37; the original suite remains 500/501 with zero skipped. Both application hosts, Tray and compatibility pass. These measurements do not identify the failing permission.
- **Implementation boundary:** Add a fixed manual-only capture at the existing first-refusal point, encrypt in memory before writing, and expose only a bounded ciphertext artifact. Keep all protocol refusals, token operations, source/job/receipt fences, deadlines and original five exit-37 checks unchanged. Explicit over-limit or encryption failure must not emit plaintext or mask the initiating refusal.
- **Key handling:** A dedicated RSA-3072 keypair is generated locally. Only its public key may enter the reviewed source; the private file is outside the repository with mode 0600. No private-key bytes are printed or sent to a child agent or GitHub.
- **Next:** Implement and locally qualify the producer, manual workflow and ciphertext validation, then obtain approval to publish and execute the single capture in interactive mode.

### 2026-10-26 - Qualify a numeric-only alternative to the blocked raw-line path

- **Alternative:** The user directs continued autonomous work, not a raw-data exception. Public reports concern different Low-integrity/new-logon-session cases and do not establish our cause. The next safe route observes integrity and effective restricted-SID count at the existing source/filter/Medium stages without another process or policy variant.
- **Implemented:** Sampling uses actual held subjects synchronously; flags 5, SID/privilege inputs, Medium setting, original verification/disposal, five arms and parent fences remain unchanged. Read failures are explicit/unavailable, not false zero facts. Output follows original work and token disposal, retaining all original and sampling/reporting failures.
- **Review:** Independent source review approves the four-file scope. A fresh author closes the required culture/numeric-bound test gap without changing runtime. Existing buffer validation, including zero groups, is preserved.
- **Validation:** All four Release/cross-builds and 51 focused cases pass. Full local gates pass 1,384 Servicing / 2,198 Core with zero failures/skips; 541 startup controls/source contracts and workflow lint pass. Same-five coverage is 16,849/19,323 lines (87.1966%), 16,410/25,929 branches (63.2882%), above the prior exact level with coherent shared dependencies.
- **Next:** Publish and observe the existing fixture once on the actual latest host. Do not infer native facts or change filtering/integrity based on portable controls. The encrypted/raw-line proposal remains unapproved and unused.

### 2026-10-26 - Preserve the exact debugger refusal and its privacy boundary

- **Existing evidence only:** Artifact `12621132808` from exact run `40251523225` contains one 2,300-byte `startup-debug.json`. Numeric inspection records first/stage reasons 1/1, stages 1, evaluator 1, echo mask 0 and read fingerprint kind/source/lexical/trim/wording/token = 1/0/128/0/1/0. No new launch or raw debugger inspection occurs.
- **Exact meaning:** Independent source tracing establishes that the bound SDK operation completed and MASM readback was accepted, but the query-stage display containing `.printf`, `HERMES_ARM_STAGE`, the fixed read fragment and the word `error` is not a known pinned-source echo. It remains unclassified with unknown source. The reader deliberately refuses it; collection subsequently times out. This is not evidence to normalise or ignore the line.
- **Stop condition:** These fingerprints do not establish the complete returned representation or distinguish a legitimate rendering from an error. No semantic parser/query/continuation change is justified. The held-source exit 37 narrows the startup issue but does not identify the changed token property.
- **Authority:** A specific request proposes capturing only the first refused line, at most 1 KB, encrypted in transit with a local-only key, and no wider log/query/host/acceptance change. The response is owner-unavailable/autonomous-work, not explicit permission for that privacy exception. No encryption/capture change, new raw artifact or rerun is performed.
- **Next:** Retain numeric-only diagnostics and the current acceptance gate until explicit one-line inspection/capture authority or an equally complete approved source-relative comparison is available. Do not infer the unknown line from lexical bits or manufacture a tolerant parser.

### 2026-10-26 - Observe genuine exit 37 with the held source token

- **Genuine result:** Current required CI `40251523225`, actual job `121344275400`, is bound to `72ef1af`. The source control reports verified High source and child, real resume/exit, **exit 37**, matching image/eight-byte script, stdin EOF, closed pipes/drains, zero output and no operation/cleanup error. This is a conclusive observation, not original acceptance.
- **Original gate:** All 501 cases still execute: 500 pass, the same restricted-PowerShell case fails, zero skipped. The five filtered-token exit-37 requirements are unchanged. Both application jobs, Tray and compatibility builds pass; installed lifecycle remains skipped and publishing does not run.
- **Interpretation:** The held-source version of the same arm-6 recipe works. The failure therefore accompanies the filtering/integrity transformation relative to this recipe; it does not identify which transformed property causes initialization failure.
- **Primary contract:** Microsoft documents LUA_TOKEN 4 as supported on these Windows versions (unsupported only on XP/Server 2003). There is no evidence to drop flags 5 as an unsupported API combination. No token-policy waiver, privilege fallback or host setting is applied.
- **Next:** Trace the existing source-point-26 debugger refusal using its current guarded code path, rather than speculate about token attributes or add a broader event query. A fresh C# specialist owns that source-only investigation; no further native measurement is authorised by this entry.

### 2026-10-26 - Qualify the exact source-token comparison before publication

- **Contract finding:** All five original PowerShell arms use the filtered token. The so-called self controls change process/thread descriptors, not token filtering; they are not a like-for-like unfiltered baseline. Source review establishes no production fix. The coordinator selects one non-accepting held-source-primary comparison under the remediation plan, without claiming a separately selected user API decision.
- **Implemented:** The fixed arm-6 recipe is reused with the existing held source and separate High-role verification. Source ownership covers child cleanup and final source comparison. No new privilege acquisition, token mutation, caller selector or host setting is added. Creation denial is inconclusive only under its strict no-child/verified-source/cleaned conditions. Original five genuine-37 checks and 501-case count remain.
- **Review:** Final independent review approves the eight-file scope. Fresh authors resolve deferred-error loss, reporting pre-emption and genuine exception-graph rewriting. Reporting cannot skip the original gate; the original failure stays primary and retained graphs preserve existing repetitions and envelopes.
- **Validation:** Four portable/Windows-targeted builds pass without warnings/errors. All 143 focused, 1,333 Servicing and 2,198 Core cases pass without failures/skips; root gates and 541 startup controls pass. Same-five coverage is 16,788/19,261 lines (87.1606%), 16,358/25,875 branches (63.2193%), above the preceding local level and fixed gates after shared dependency checks.
- **Prior hosted state:** Evidence rollout `e8d96f3`, CI `40249059413`, passes both application jobs including 1,292 Setup/133 Installer.Control, new reader controls and 61 browser cases each. Original installer acceptance remains red; no portable failure root cause is claimed.
- **Next:** Publish this reviewed source and read the one ordinary latest-host native gate's supplementary observation before choosing a fix. No source-token comparison has yet executed.

### 2026-10-26 - Make the separate portable failure observable without changing its verdict

- **Source trace:** The failed Setup case uses an already-exited fake child and manual clock, not native PowerShell. Its real five-second outer guard includes queued cleanup. That is a grounded coupling, not proof of the Windows cause. The unchanged C# source passes the next ordinary PR CI `40245433431` on both application hosts; no fix or causal claim is made.
- **Implemented evidence:** A mandatory C# author supplies a failure-only scalar projection retaining all original assertions, budget, guard and actual operation/drain tasks. Independent source review approves it. A fresh PowerShell author supplies a strict private-TRX reader and original-exit-preserving CI path; direct bounded review finds no material privacy/exit defect. A separate isolated PowerShell step runs its 102 reader/five flow controls in CI.
- **Exact delivery proof:** Quiet console alone emits no scalar line. A temporary valid row in the exact named theory gives 1,292 passes and one deliberate failure. Real dotnet, the actual extracted CI block and actual reader emit one numeric row, preserve exit 1 and skip the following installer test invocation. The temporary source/data are removed and the assembly rebuilt; the clean block passes 1,292 Setup and 133 Installer.Control cases without failures/skips. Root build/tests pass with zero warnings/errors and 2,198 Core cases.
- **Limits:** Rows are non-atomic method-family advisory, not per-data-case authority. Raw TRX, output, theory arguments and exception data stay private. No production status, token, timeout, retry, skip or acceptance rule changes. Current source-only held-source control proposal remains unapplied and separately under fresh revision after fault-preservation review findings.
- **Next:** Publish the reviewed evidence path, qualify it on the actual supported hosts, then complete independent review/local qualification of the single held-source launch comparison before native execution.

### 2026-10-26 - Reproduce the original startup failure on Windows 2022

- **Actual comparison:** Manual CI `40245430048`, job `121325868766`, is bound to `b921e8b`, has the distinct non-promotion check name and runs the actual Windows 2022 image. Build, 541 bounded protocol controls and fixed debugger prerequisites pass.
- **Unchanged failure:** All 501 native cases execute: 500 pass, one fails, zero skipped. The failing case remains `Actual_restricted_PowerShell_initialises_without_parent_changes`. Detached arms exit 0; hidden/new-console arms exit `0xC0000142`; no arm is accepted as the required 37. Parent/connection invariance and resource closures remain observed. The original post-gate trace is still inconclusive at source point 26/status 5/error 13, and installed lifecycle steps do not run.
- **Conclusion:** A Windows 2025-only difference is not sufficient to explain the failure. This does not prove a particular common host setting, token policy or loader cause. The ordinary latest gate stays blocking; no expected result or host policy is changed.
- **Next:** A fresh C# specialist reviews the common token/launch contract and the exact API parameters against the existing evidence before any source fix or additional measurement is chosen. The separate portable Setup failure is being made observable through closed test evidence, not declared fixed.

### 2026-10-26 - Add a fixed OS comparison without replacing promotion

- **Changed:** A fresh author adds direct manual CI routing to stock public Windows 2022 for the existing installer job. Its diagnostic display name differs from the required Windows-latest promotion name. Ordinary events and reusable callers retain latest; the actual job ID and all eleven acceptance steps remain unchanged. The existing main-push-only publish fence also excludes future manual runs on main.
- **Validation:** Direct bounded source review, actionlint, unchanged Setup contract controls, 72 routing fixtures and 216 publication fixtures pass. Root build passes with zero warnings/errors and all 2,198 Core cases pass. Expression fixtures are not actual GitHub/native proof.
- **Separate new blocker:** CI `40244114037` at `13f48ec` passes macOS/Tray/compatibility, but Windows fails one of 1,277 portable Setup cases before coverage/browser: duplicate None records in `Every_diagnostic_record_is_consumed_and_semantically_bound`. The same C# source passed the previous host run. A local focused run passes all 31 method cases; no root cause or fix is claimed. A fresh C# specialist is tracing this independent transport failure while the OS comparison proceeds.
- **Next:** Publish and dispatch the fixed comparison, confirm the actual Windows image and unchanged checks, and keep both the original installer failure and new portable transport failure blocking merge.

### 2026-10-26 - Complete the approved presence experiment without inventing a fix

- **Genuine execution:** Manual run `40244111382`, actual job `121321953576`, is bound to `13f48ec`. Portable controls, source publication and same-job schema acquisition pass. The diagnostic then executes 79 successfully closed empty searches with only the server birth predicate removed; there are no reader calls or native query errors.
- **Outcome:** Closed presence facts show available 1, outcome 1, reason 9 (WindowExpired), receiptClosed 1, cleanupError 0, polls/queries 79/79 and all acceptance flags zero. Failure is the deliberately non-success diagnostic disposition; no acceptance report is uploaded. The previous manual attempt stopped before native execution and is not counted as a presence experiment.
- **Interpretation:** Removing birth alone does not restore matching. It does not establish missing event delivery, correctness of other predicates or a PowerShell root cause. The approved one-presence-experiment scope is complete; no wider query or host-policy change follows from it.
- **Next chosen comparison:** Run the unchanged startup/installer acceptance commands on a fixed standard public Windows 2022 host, retaining ordinary Windows-latest promotion checks and a distinct diagnostic check name. This avoids further event-filter experiments or shared-host changes. A fresh author is checking the CI-only route against existing admission contracts before publication; no comparison has run yet.

### 2026-10-26 - Remove mixed-clock assumptions from the portable fixture

- **Actual dispatch:** GitHub accepts genuine manual run `40243239129` at `394aabf`; its job `121319330076` refuses the portable freshness fixture before publication or native execution. The same-source push job passes that fixture. Manual dispatch is therefore available, but no candidate-presence query has run.
- **Correction:** A fresh PowerShell author separates the pure freshness predicate's synthetic time axis from observed file hashes, pins and replacement timestamps. Four new cases exercise creation/write times outside the interval. Direct one-file review confirms all production predicates and diagnostic/runtime code are unchanged; no sleep, retry, tolerance or skipped assertion is added.
- **Validation:** Event controls pass 4,360; original and schema controls pass 985/3,120. Root build passes with zero warnings/errors; all 2,198 Core cases pass without failures/skips. The previous non-regressing C# coverage is unchanged.
- **Regression evidence:** At `394aabf`, both supported application jobs in CI `40243231450` pass, as do Tray and compatibility builds; original installer acceptance and its diagnostic still fail. Managed run `40243227201` succeeds with independently checked artifact `12617457940`, one 591-byte/28-field success report preserving every original fact. Strict event run `40243227234` observes 79 clean empty returns and WindowExpired, not precision proof.
- **Next:** Publish the fixture-only correction, then request the still-unperformed approved native diagnostic. Keep preflight failure separate from a diagnostic result.

### 2026-10-26 - Approve the diagnostic source for one genuine invocation

- **Review:** The final independent review approves the combined C#, PowerShell and workflow source. All first-reader-failure, final-field mutation and output-sink cleanup findings are resolved. Prior authors remain excluded from rejected revisions; the coordinator applies authored corrections mechanically.
- **Qualification:** Full local results below pass, including 1,190 Servicing / 2,198 Core, 4,355 event controls and non-regressing five-assembly coverage. This is source approval, not native filtering or installer acceptance.
- **Next:** Publish this exact source and attempt the approved genuine manual workflow dispatch. If GitHub refuses it, no diagnostic has run; do not spoof admission or merge workflow code to evade that prerequisite.

### 2026-10-26 - Qualify the bounded diagnostic before native execution

- **Implemented:** A fixed diagnostic-only command removes only the server-side birth predicate and uses a separate identity-only result path. The original managed control, strict precision/adjacent requirements, installer gate, host policy and artifact contract remain unchanged. Manual workflow selection adds no caller-supplied subject or query.
- **Review corrections:** Independent review rejected lost first-reader failures and then final-field test mutations plus output-sink failures interrupting cleanup. Fresh authors supplied each revision under the lockout protocol. The final scoped correction also fixes one analyser violation and a static test's incorrect expected count; both owned-presence branches must still prove acceptance reports absent.
- **Local evidence:** Portable and Windows-targeted Release builds pass with zero warnings/errors. All 783 focused, 1,190 complete Servicing and 2,198 Core cases pass without failures/skips. Root build/tests pass. Matching shared Release bytes precede the unchanged five-assembly merge: 16,608/19,081 lines (87.0395%), 16,281/25,763 branches (63.1953%), above the prior local level and fixed gates. Original/metadata/event PowerShell controls pass 985/3,120/4,355; targeted workflow lints, encoding and whitespace checks pass.
- **Boundary:** These are local/source results, not native delivery or filtering proof. Final independent source review is pending. The workflow is registered but absent from default main; actual manual branch-dispatch availability is not yet established. No event is invoked, admission spoofed or workflow merged to bypass that question.
- **Next:** Require final source approval, publish the reviewed revision and attempt its one approved fixed manual invocation. If GitHub refuses dispatch, record that no diagnostic ran and resolve the execution prerequisite without weakening acceptance.

### 2026-10-25 - Reconcile coverage evidence and reject an incomplete repair lock

- **Coverage:** All seven exact coverage-gate controls and eight test-execution controls pass again. Existing issue #18 now links actual candidate evidence rather than leaving its last below-threshold main measurements as the apparent current result. It stays open until merge and remote-main verification; the installer failure is still explicit.
- **Repair authority:** Primary `chattr(1)` documentation warns that immutable flags do not affect writes through pre-existing file descriptors. The design now explicitly rejects treating recursive inode flags as sufficient protection. No flags, mounts, ACLs or source files are changed; concrete protected authority is still required.
- **Plan persistence:** The approved diagnostic scope and sequence are recorded above and in the Squad decision inbox. Root build passes with zero warnings/errors; all 2,198 Core cases pass without failures/skips. This commit records decisions and evidence, not a product fix or enabled repair.
- **Next:** Apply and independently qualify the fresh specialist's bounded diagnostic proposal before its one actual invocation.

### 2026-10-25 - Approve and sequence the remaining remediation

- **Authority:** After the explicit five-step remediation proposal, John directs: "make the plan, and then make it so". This approves the specifically described owned-process candidate-presence diagnostic: omit only the server-side process-start-time condition; retain fixed Application Error/1000/v0, PID, executable and time window; read identity fields only and export bounded numeric facts. It does not approve broader discovery, raw event output, host-policy changes or weaker installer acceptance.
- **Plan:** First implement and independently review that bounded diagnostic, then execute it once on disposable public hosted Windows. Use its actual outcome to select a targeted restricted-startup fix rather than guessing. Prove the complete installed lifecycle and preserved data before readying/merging PR #30, then verify remote main and reconcile #16/#18/#28.
- **Legacy repair:** Keep #24 separate from the installer merge and retain its strict uniqueness contract. Establish and independently review a concrete protection mechanism before implementing repair; do not silently replace it with owner-selected assignment or treat an unavailable operator as delivery. Synthetic fixtures only.
- **Unchanged gates:** Current published source is `2e14c04`; both supported application jobs pass, original installer acceptance fails. No shared Pelican experiments, personal corpus access, global WER change, skipped cases, timeout inflation or acceptance substitution is authorised by this plan.
- **Next:** A fresh C# specialist owns the diagnostic source, portable regression coverage and its fixed supervised invocation; the coordinator retains publication and evidence integration.

### 2026-10-23 - Qualify access lifecycle measurements on both actual hosts

- **Published:** Observer source `d025a46e72feeffe58879a5f4e07fe559175f653`, CI `39871680120`, passes both supported application jobs. Windows `120147344119` and macOS `120147344190` each pass 1,039 Servicing and 61 browser cases; Core selections pass 2,194 and 2,198 respectively, without failures/skips.
- **Measured:** The first actual Windows status request has one HTTP 200 response, one body-finished event and no failure. Request/header/body observer offsets are 64/558/565 ms; macOS records 126/338/355 ms with the same counts. No raw request/response data is exported. These are observer offsets, not independent network phase durations.
- **Coverage:** Actual Windows measures 87.1781% line / 62.8232% branch; macOS measures 86.9512% / 62.5312%. Both unchanged gates pass. The earlier Windows timeout does not recur, but no product fix or root cause is claimed.
- **Remaining blockers:** Installer job `120147344097` fails the original native gate and diagnostic; publish is skipped. A renewed request for the exact bounded identity-only diagnostic again receives an unavailable-owner autonomous-work directive, not the required explicit exception. Native expansion remains parked; no query, policy, deadline or acceptance change is made.
- **Legacy repair:** The exact #24 requirements include implementation, not merely design. Its existing blocker comment now links the published design at `d33018a`, correcting stale publication-pending wording without closing the issue. A reviewed concrete protected namespace or an explicit different contract remains necessary; no personal corpus or repair is accessed.
- **Next:** Retain the verified application candidate and existing diagnostic boundaries. Resume native or legacy implementation only when its concrete authority prerequisite is supplied; do not merge or mark the overall backlog complete.

### 2026-10-23 - Measure the access bootstrap without weakening its assertion

- **Evidence:** Existing Windows job `120130588596` logs show the first `/api/access` response completed with HTTP 200 in 42.7537 ms. The browser nevertheless reports its access deadline in the original first smoke case. A slow Service handler is not established; response delivery and client handling need separate observations.
- **Changed:** The existing case observes its first fixed status request through non-intercepting Playwright request/header/body-finished/failure events. One fixed eight-field numeric line records counts, status and monotonic observer offsets. It exports no request URL, headers, body, identity or error text; no fetch wrapper, mock, warm-up request or extra await changes the path under observation.
- **Validation:** Four new formatter controls pass, and the complete unit suite passes 106/106 without skips. Lint, web build, discovery and reproducible assets pass. The original targeted case and complete isolated real-Service suite pass 1/1 and 61/61 locally; both fixture ports close afterwards. Root build passes with zero warnings/errors and all 2,198 Core cases pass without failures/skips.
- **Unchanged-product baseline:** Both supported hosts in documentation-head CI `39869833919` at `621b3f4` pass all 61 browser cases, 1,039 Servicing cases and their complete Core selections. Windows coverage is 87.1887% line / 62.5818% branch; macOS is 86.9512% / 62.8699%. No product change explains this recovery; it does not establish the cause of the earlier timeout. The original installer gate remains red.
- **Boundary:** Original assertions and their ordering, the 10-second access deadline, zero retries and all authentication/native acceptance rules are unchanged. These local successes do not resolve the actual Windows failure. Native diagnostic expansion remains parked.
- **Next:** Publish the observational change and inspect actual Windows lifecycle counts before choosing any product fix.

### 2026-10-23 - Close the current-source stocktake with a separate browser blocker

- **Published source:** `cb574ad39787b3ed38d9a9fcacebfdaa910931b2` is the remote PR #30 head. Remote main remains `e7758a3797dc6ca5d52ae1ac573a2a4802f0a27a`; the PR is draft, open and unmerged. Financial integrity, file-first retrieval, V5 reflow and canonical React/Tray work already merged remain on main.
- **Actual hosts:** CI `39866704579` completes. macOS job `120130588553` passes 1,039 Servicing, 2,198 Core and 61 browser cases, with 86.9512% line / 62.5740% branch coverage. Windows job `120130588596` passes 1,039 Servicing and 2,194 selected Core cases without failures/skips; four governed Unix-only exclusions remain. Tray and both excluded-surface compatibility builds pass.
- **Local commit gate:** The root build is rerun and passes with zero warnings/errors; the root test run passes all 2,198 cases without failures/skips. This evidence-only update changes the wave and testing register, not product code or acceptance.
- **New browser blocker:** Windows passes 60/61 browser cases. `access.spec.ts:12`, assertion at line 18, expects anonymous access but receives the visible unavailable state with `signal timed out`. The access client has a 10-second request deadline. This is distinct from the earlier PDF page-creation failure; the request-timeout cause is not established. Windows coverage collection succeeds, but the subsequent threshold-enforcement step is skipped after browser failure. No retry, timeout increase or authentication weakening is applied.
- **Remaining work:** Installer job `120130588467` still fails the original native gate and its diagnostic; publish is skipped. Open issues are #16, #18, #28 and #24. Native event expansion remains parked at the explicit correlation boundary recorded below. Current Windows browser acceptance, installed lifecycle acceptance and wave closure are incomplete.
- **Next:** Obtain bounded request-lifecycle evidence for the Windows access timeout before choosing a fix. Native promotion separately requires an explicit diagnostic exception or an approved suitable acceptance environment. No merge, release, issue closure or wave transition is claimed.

### 2026-10-23 - Park native promotion at the explicit correlation boundary

- **Managed regression:** At `cb574ad`, run `39866698782`, job `120130567498`, succeeds. Artifact `12464905331` independently verifies the original 591-byte, 28-field managed success contract, every required settlement/cleanup bit and `0x80131623`; the new shared factory does not replace that proof.
- **Native qualification:** Event run `39866698746`, job `120130567334`, yields 80 successfully closed empty fixed-selector returns, no reader or native query fault, and WindowExpired. Its final empty return is rejected after the deadline. The managed/native producer distinction does not establish why no exact candidate qualifies.
- **Boundary:** Exact-one / adjacent-zero-zero native comparison remains unproved. A candidate-presence diagnostic outside the server-side birth predicate would need an explicit diagnostic exception; the escalation returned only an unavailable-owner autonomous-work directive, not that exception. The strict boundary is retained and further native diagnostic expansion is parked rather than inferred or silently broadened.
- **Persistent status:** [Issue #16 update](https://github.com/johnazariah/hermes/issues/16#issuecomment-6353071869) records the actual evidence and blocker. PR #30 remains draft and unmerged; original PowerShell, installed Medium lifecycle and final wave closure remain incomplete. Legacy repair #24 remains separately blocked.
- **Next:** Require an explicit authorised diagnostic boundary or suitable separately approved acceptance environment before resuming native promotion; finish the current-head application/browser CI stocktake without claiming overall completion.

### 2026-10-23 - Qualify a fixed native producer without rewriting the query

- **Grounding:** Pinned .NET 9 `EventReporter` explicitly reports managed FailFast as .NET Runtime/1025; it does not guarantee the Application Error/1000 record sought by this selector. The actual queued/manual/disabled-task WER metadata is not proof of the empty-query cause. The documented Win32 producer invokes WER if enabled but likewise does not promise record delivery.
- **Changed:** Only event control selects a new exact native child route using the existing shared publication/adoption/identity/cleanup path. It preserves WER 227, ready/arm/EOF/cancellation and fixed null/null/zero native arguments, with refusal 66 if the native call returns. Original managed child/probe semantics and status allowlist remain unchanged.
- **Admission:** An incorrect prompt premise about existing child caller-root proof was explicitly withdrawn. The new route copies only validated actual-parent admission metadata; original managed environment construction and all guards remain unchanged. Environment assertions are cooperative, not attestation.
- **Review:** Fresh independent C# review approves the complete seven-file proposal and applied source. Mechanical application and the three new BOM normalisations are verified.
- **Validation:** Portable and Windows-targeted builds and root build pass without warnings/errors. All 512 selected, 1,039 complete Servicing and 2,198 Core cases pass without skips. The exact five-assembly merge measures 16,479/18,952 lines (86.9512%), 16,188/25,680 branches (63.0374%), above unchanged gates. Original/metadata/event controls pass 985/3,120/3,372 and targeted workflow lints remain clean.
- **Boundary:** No new native variant has run. No query/literal/window, global WER/host setting, privilege, original PowerShell requirement or report contract is relaxed.
- **Next:** Publish this one producer-only control and require genuine exact/adjacent results before any original-event observation.

### 2026-10-23 - Ground the next check in empty-query and image-policy evidence

- **Native measurement:** At `1cbc50f`, run `39859837812`, job `120107369986`, returns 78 successfully closed empty fixed-selector results, without native faults or reader calls. Open/Next maxima are 2,276/1,021 microseconds; total delay is 19,637,458 microseconds. Window expiry occurs before the next query, not after rejecting a returned result. This distinguishes repeated emptiness from a stalled call, not production/delivery from filtering semantics.
- **Host evidence:** The job identifies `windows-2025-vs2026` image `20261021.278.1`. Its exact public source tag forces WER queuing and disables WER scheduled tasks. Those build settings are not a runtime snapshot or a demonstrated cause.
- **Changed:** A fixed read-only policy advisory is acquired in the same guarded metadata step. Seven registry values and one known task are mapped to bounded state enums; read/cleanup failures are explicit. No queue, report, event or private file is read, and no host setting is changed. Metadata receipts and all query/acceptance contracts remain unchanged.
- **Validation:** Direct complete-delta review approves the two-file change. Independent original/metadata/event controls pass 985/3,120/3,372; cold modes, targeted actionlint and whitespace checks pass. Root build passes without warnings/errors; all 2,198 Core cases pass without skips.
- **Application state:** Both supported jobs in CI `39859843192` pass, including all 1,000 Servicing cases, Core selections and 61 browser cases on each host. The older Windows page-creation failure does not recur; its cause is not established and no retry/timeout change is claimed.
- **Blockers:** Exact native precision, original PowerShell execution and installed acceptance remain unresolved.
- **Next:** Read the actual same-job policy states before attributing empty results or changing any host/query behaviour.

### 2026-10-23 - Measure unchanged query outcomes without changing authority

- **Changed:** One bounded failure-only numeric row records attempts/returns, cleaned-up empties, category before the authoritative post-query check, slack/expiry location and fixed native/fence/delay phase durations. No predicate, literal, native flag, child/WER setting, window, identity or acceptance check changes.
- **Authority:** Sampling errors cannot interrupt native status interpretation or owned closes, create synthetic authority stops, mask later cancellation/receipt faults or claim a timing failure as an authority rejection. Real coordinator/receipt failures and diagnostic-emission failures are collected before final sampling-reference attachment. Genuine aggregates and duplicate members remain intact.
- **Escalation:** The original three-cycle review ended with a sampling/output shared-reference blocker. The escalation response directed autonomous work; one bounded fresh-author/fresh-review correction was authorised under standing approval, without claiming an explicit user choice. The fresh reviewer approves that complete final boundary; no further review loop is opened.
- **Validation:** All 473 selected, 1,000 complete Servicing and 2,198 Core cases pass without skips. Root and portable/Windows-targeted builds pass without warnings/errors. The exact five-assembly merge measures 16,413/18,886 lines (86.9056%), 16,151/25,664 branches (62.9325%), above unchanged gates. Original/metadata/event controls pass 985/3,120/3,118; cold task-binding modes, source encodings and three targeted actionlint checks pass.
- **Consumer:** The event-only capture cap is 1,023 bytes for the at-most-393-byte row, with exact real-task identity/settlement checks and a derived 1,026-task maximum. The original wrapper remains 511 bytes; success stdout, 28/27-field JSON and success-only three-file upload are unchanged. No raw capture or identifying data is exported.
- **Browser:** The complete unchanged local real-Service suite passes 61/61. The actual Windows pre-body page-creation timeout remains unclassified; no retry or increased UI timeout is added.
- **Blockers:** Native exact/adjacent precision, the Windows browser refusal, original PowerShell and installed acceptance remain unresolved.
- **Next:** Publish this reviewed telemetry-only control and inspect one genuine unchanged-query execution.

### 2026-10-23 - Observe the genuine correlation window expiry

- **Evidence:** At `884bb8c`, event run `39847520229`, job `120064611045`, passes source publication, same-job metadata and actual task binding. Its closed capture now admits seven real tasks and reports reason 9 (WindowExpired), matched/adjacent proof bits zero, control fault 7, cleanup/report faults zero. Capture check 0 and shape 1478 verify the projection contract; no raw input is exported.
- **Interpretation:** No exact event was accepted before expiry. The post-query fence can reject an already-returned result before the accepted-match flag is set, so these facts do not establish no event, repeated empty queries, a stalled call or a comparison bug.
- **Review:** The fixed-query author finds no proven construction, ABI or representation defect. The primary sources do not prove decimal HexInt64 equality or promise this FailFast invocation emits Application Error/1000/v0. No literal, window, fault mode or query is changed on speculation.
- **Additional gate:** CI `39847529433` passes macOS and all Windows .NET cases but Windows browser smoke fails while creating the page for the real PDF test, before its body; 60 browser cases pass. The unchanged isolated local preview runs 9/9 cases in a bounded three-repeat reproduction. This neither reproduces nor fixes the Windows setup failure; no timeout or retry is added.
- **Blockers:** Controlled native precision, the new Windows browser setup failure, original PowerShell execution and installed acceptance remain unresolved.
- **Next:** Add one bounded telemetry-only control of unchanged query outcomes and timing, recording results before the authoritative post-query fence.

### 2026-10-23 - Bind cold capture to the authoritative real task list

- **Actual evidence:** At `941a782`, event run `39844283742`, job `120053314203`, refuses task-container check 12 / field 8. The 407-byte four-row shape is observation/observation/base/event. Its diagnostic task count is unavailable because of that type refusal; it does not show absent operations, and the earlier three-observation fixture does not explain this run.
- **Changed:** Capture builds an exact Object[] from the authoritative task list. Count, order, reference identity and settlement are checked at construction and twice in the wrapper. No enumerable fallback, synthetic completed task, altered success grammar or deadline change is introduced.
- **Validation:** Independent cold fresh-process fixtures pass with four real tasks, both before and after managed metadata/assembly loading. All 1,151 event, 985 existing crash and 3,120 metadata controls pass. Root build passes without warnings/errors and all 2,198 Core cases pass without skips. Direct review approves the complete three-file delta; actionlint and whitespace checks pass.
- **Host state:** Both supported jobs in CI `39844295455` pass at `941a782`; the original native installer gate and diagnostic still fail and publish is skipped.
- **Blockers:** The actual inner event reason and native precision remain unknown. Original PowerShell and installed acceptance remain unresolved.
- **Next:** Publish the owned task-container correction and inspect genuine numeric event facts.

### 2026-10-23 - Distinguish native event-report refusals without raw capture

- **Actual evidence:** At `d043a94`, both supported hosts pass all 911 Servicing cases and their complete Core selections. Windows coverage is 87.0176% / 62.4834%; macOS is 86.7871% / 62.3620%. The original owned-control run `39840795553`, job `120040941956`, succeeds; independently checked artifact `12452019121` preserves all 28 success facts and the documented FailFast status.
- **Refusal:** New event run `39840795712`, job `120040941471`, passes portable controls, fresh publication and same-job metadata acquisition. The execution step refuses at phase 9 / family 1 / reason 12, with unavailable event facts. No failed capture is printed or uploaded, and no event precision or inner cause is inferred.
- **Changed:** A legitimate synthetic 508-byte failure has three observations, beyond the old decoder's two-row bound. The failure-only decoder admits that form while preserving the exact success grammar and 511-byte cap. Separate closed check/field codes, capped capture counts and prefix-shape codes make any remaining advisory refusal distinguishable without text, identity or exception export. Whether this explains the actual old capture remains unknown.
- **Validation:** Direct review approves the complete three-file delta. Independent controls pass 985 existing / 3,120 metadata / 1,081 event assertions; root build passes without warnings/errors and all 2,198 Core cases pass without skips. All three fixed workflows pass actionlint.
- **Blockers:** Actual native event precision, original PowerShell execution and installed acceptance remain unresolved.
- **Next:** Publish this bounded observability correction and inspect the next genuine closed failure facts.

### 2026-10-23 - Preserve genuine outcomes in the fixed owned event probe

- **Changed:** The exact new synthetic command binds strict metadata admission to the retained owned child's held scope. Completed bounded queries require one exact identity match and empty birth-minus/plus-one controls. Reports retain separate base-control and event status; original commands and report shape remain intact.
- **Fidelity:** Expected observer failures retain actual coordinator settlement. Escaping coordinator errors propagate after receipt cleanup instead of publishing default Guard facts. Later receipt faults cannot invent a callback failure. Real-command regressions cover delayed reap, coordinator cleanup, mixed original errors, post-success receipt and publication faults; the nested-error assertion preserves exact object membership without assuming Flatten order.
- **Portability:** Windows-target path qualification is host-independent, with the original native Windows check retained. Official .NET 9 semantics and always-running cases replace OS-skipped fake successes; native equivalence assertions additionally run on Windows.
- **Review:** Fresh independent C# review approves the complete integration and final correction. Direct PowerShell review approves the fixed source-bound supervisor, same-job metadata receipt, strict full-report validation and success-only three-file upload under its pre-armed worker deadline/private job.
- **Validation:** Serial portable and Windows-targeted Release builds and root build pass without warnings/errors. All 384 selected cases, 911 complete Servicing and 2,198 Core cases pass without skips. The exact five-assembly merge measures 16,191/18,656 lines (86.7871%), 16,046/25,538 branches (62.8319%), above unchanged gates. Independent PowerShell controls pass 985/3,120/816; all three fixed diagnostic workflows pass actionlint and changed-source encodings pass.
- **Boundary:** No native event query has run yet. Completed enumeration is not global log uniqueness; synchronous calls still require external supervision. Original PowerShell and installed acceptance remain unresolved.
- **Next:** Publish the reviewed fixed workflow and require genuine exact-birth 1 / adjacent-birth 0/0 evidence before any original-event observation.

### 2026-10-23 - Restore genuine supported-host acceptance of the prerequisite checks

- **Evidence:** Published `42975ef`, CI `39834814349`: supported Windows job `120020216988` and macOS job `120020217043` both succeed. The actual SDK 9.0.319 compilation regression is resolved; the strict pinned-peer assertion passes on this head. The older intermittent runtime failure's cause is not established.
- **Validation:** Both hosts execute all 767 published Servicing cases; Core passes 2,194 selected Windows and all 2,198 macOS cases without failed/skipped executions. The four Windows exclusions remain governed Unix-only cases. Windows coverage artifact `12451645477` independently verifies exactly the five expected assemblies and 15,771/18,186 lines (86.7206%), 15,769/25,286 branches (62.3626%). macOS reports 86.4841% / 62.2953%.
- **Metadata:** Separate run `39834806743`, job `120020191436`, succeeds with the extended fixed-job admission. No job environment is rewritten.
- **Boundary:** Installer job `120020216643` still runs 501 native cases: 500 pass and the original PowerShell case fails, with no skips. Its diagnostic also fails; publish remains skipped.
- **Follow-on:** The unpublished fixed event supervisor passes 816 independent portable controls, plus 985 existing crash and 3,120 metadata controls. Direct review finds no blocking supervision defect. The C# revision needs a host-independent Windows qualification policy to exercise genuine command success on both hosts without skips, while retaining native Windows qualification and all identity/source fences.
- **Next:** Complete and independently review that bounded policy/outcome revision before publishing or invoking the synthetic event command.

### 2026-10-23 - Preserve static compilation of pinned discovery controls

- **Evidence:** At `8cd4bc0`, supported Windows job `119960432973` and macOS job `119960432926` refuse the new diagnostic loop with FS3511. Neither reaches the original pinned-peer assertion.
- **Changed:** A private record replaces only the tagged peer tuple and its destructuring task-loop binder. All four mutations remain sequential and exact, with unchanged typed equality, field/result codes, fixtures and deadlines.
- **Validation:** Independent F# source review approves. Local SDK 9.0.313 builds Debug and Release without warnings; all 54 selected discovery cases pass in each configuration. The root build also passes without warnings and all 2,198 Core cases pass without skips. Actual hosted SDK 9.0.319 execution remains pending.
- **Blockers:** The C# correlation candidate compiles and passes 173 selected cases, but independent review rejects its outcome reporting: repeated fence failures can discard genuine control facts, and later receipt failures can fabricate a callback fault. A fresh author owns that revision; no native event invocation is approved.
- **Next:** Publish the peer-compilation correction after the root quality gate, then inspect its genuine hosted result.

### 2026-10-23 - Admit the fixed installer job for metadata acquisition

- **Changed:** The metadata-only producer admits exactly `startup-event-schema` and `installer`, preserving case-sensitive hosted Windows, repository and server checks. Local guard fixtures do not modify the environment or acquire OS metadata.
- **Validation:** Independent execution passes all 3,120 assertions, including both admitted jobs and missing, case-changed, whitespace and unknown-job refusals. The largest synthetic report remains 2,487 bytes.
- **Boundary:** This enables same-job metadata acquisition; native correlation, source-bound invocation and the original PowerShell gate remain unproved.
- **Blockers:** Exact-head CI `39817704993` fails the new F# diagnostic tests' static state-machine compilation on both supported hosts. The actual pinned-peer Result is still unknown; a language-agent correction is in progress.
- **Next:** Compile and independently review the owned synthetic correlation integration before native invocation.

### 2026-10-23 - Expose the pinned discovery failure without relaxing it

- **Evidence:** Reader head `2a8f7bc`, CI `39809031924`, macOS job `119929933520` fails the pinned Send assertion in `rejectBadPeers` line 83. Its generic Result message hides acceptance versus transport/codec failure; the actual case is unknown. Windows succeeds, and the owned-control workflow still succeeds.
- **Changed:** Closed field/result codes annotate exactly the same typed identity-mismatch equality. PID, birth, Origin and config-key mutations remain sequential and fully pinned. No request/response/identity/token/path is formatted into the assertion.
- **Validation:** Seven new cases prove success and unexpected transport results still fail, disclose only their closed numeric cases and retain all four controls. Fresh independent F# review approves. Root build and 54 selected instrumented discovery cases pass; full Core passes 2,198 without skips.
- **Boundary:** This is observation-only. No fixture lifetime, deadline, production protocol or owner-auth rule is changed, and fake timeout cases establish no actual cause.
- **Blockers:** The real macOS failure needs typed classification or a deterministic reproduction before a fix; original PowerShell and installed acceptance remain unresolved.
- **Next:** Publish the diagnostic with the query component and inspect exact-head supported CI.

### 2026-10-23 - Construct only bounded, explicitly unproven event-query candidates

- **Changed:** The fixed query retains channel/provider/event/version, exact decimal PID/birth, literal image and bounded UTC-window predicates. It uses one synchronous two-slot batch and visits only a single candidate while query/event handles remain owned on their creating thread. Events close before the query; errors and cleanup faults cannot become empty/success.
- **Review:** Independent C# review approves construction, ownership, error handling and scope. Every result remains `Precision=Unproven`; one batch is not completed enumeration, snapshot uniqueness or native UInt64 precision. Provider/schema admission remains a caller assertion, not verification.
- **Validation:** Serial portable/Windows-targeted builds pass with zero warnings/errors; all 98 query cases pass. Combined root build, 767 Servicing and 2,198 Core cases pass without skips. The same five-assembly merge measures 16,354/18,913 lines (86.4696%) and 15,950/25,609 branches (62.2828%), above fixed gates.
- **Boundary:** No query is invoked by a command or callback. Fakes do not prove server-side comparison, and there is no PID-only, rounded-number, broad-channel or fuzzy fallback.
- **Blockers:** Actual controlled exact-birth match and adjacent-birth 0/0 results, current macOS peer-failure classification and original PowerShell/installed acceptance remain pending.
- **Next:** Publish the component and integrate only the separately supervised synthetic correlation control.

### 2026-10-23 - Build the bounded identity-first event-value component

- **Changed:** The fixed reader renders only three identity fields, compares exact unsigned PID/birth and ordinal canonical image path, then permits the three detail fields. Native contexts and buffers are owned; the event is borrowed. Size, ABI, scalar type, pointer range, UTF-16 and cleanup faults refuse explicitly. No query, event enumeration, command integration or whole-event rendering is added.
- **Grounding:** Official Microsoft SDK declarations and five rendering documents establish selective value paths, ordered variants, hex scalar types, array flags, byte sizing and lifetimes. Scalar Count is not treated as a documented zero. A selected value does not establish XPath uniqueness or publisher admission.
- **Validation:** Serial portable/Windows cross-builds pass with zero warnings/errors; all 120 reader cases pass. One invalid InlineData surrogate was corrected to runtime construction with verified code units; valid replacement characters retain positive/mismatch coverage. Fresh independent C# review approves.
- **Coverage:** Root build and 2,191 Core/669 Servicing cases pass without skips. The same five-assembly merge measures 16,199/18,757 lines (86.3624%) and 15,825/25,513 branches (62.0272%), above unchanged gates.
- **Blockers:** The reader has not run natively and has no query/caller integration. Exact owned-control birth-match and adjacent-mismatch filtering must precede baseline event observation. Original PowerShell/installed acceptance remain unresolved.
- **Next:** Publish the reviewed component, then implement the separately bounded exact query prerequisite.

### 2026-10-23 - Verify the lifetime and pool corrections on both supported hosts

- **Evidence:** Exact head `438408d`, CI run `39801163486`: supported Windows job `119901908751` and macOS job `119901908909` both succeed. Tray and excluded compatibility builds also pass.
- **Validation:** Windows executes 549 Servicing and 2,187 selected Core cases without failures/skips, confirming the revised real-pool ownership proof on its failing platform. Four governed Unix-only exclusions remain explicit.
- **Coverage:** Actual artifact `12435329820` reports 15,446/17,857 lines (86.4983%) and 15,570/25,062 branches (62.1259%) over the unchanged five assemblies, above the earlier hosted measurement and both fixed gates.
- **Completed prerequisite:** The same head also has the genuine owned control success in run `39801150608` and its validated 591-byte report. The lifetime and pool-reproducibility work now have persistent target-host evidence.
- **Blockers:** Installer job `119901908019` still fails the original native PowerShell gate and diagnostic; publish is skipped. Event correlation, original PowerShell cause and installed Medium acceptance remain open.
- **Next:** Implement and review the fixed identity-first event-value reader without broad event rendering.

### 2026-10-23 - Complete the genuine owned crash-control prerequisite

- **Evidence:** At published `438408d`, run `39801150608`, job `119901864576`, the dedicated hosted workflow succeeds in 2m3s. All 981 Windows controls, fresh source-bound publication, fixed supervised probe and success-only upload pass.
- **Verified report:** Artifact `12436330969` contains one 591-byte JSON file with exactly 28 fields. Ready, identity, one arm, actual exit, held observation, every task/output settlement and cleanup are one; status/faults/deadline/cancellation are zero. Exit is the documented `0x80131623`. Independent wrapper checks and normal private-job release precede publication.
- **Boundary:** Original-gate/native acceptance remain zero, and synchronous calls remain explicitly non-interruptible. This completes the control lifetime prerequisite under its configured external deadline, not forced-timeout teardown or the original PowerShell/installer gates.
- **Follow-on:** A fresh C# author is implementing only the bounded identity-first event-value reader. Verified Microsoft SDK declarations and rendering documentation are supplied; selective rendering, exact query correlation and baseline observation remain separately gated.
- **Blockers:** Supported-Windows pool repair and exact-head aggregate CI are still pending. Original PowerShell and installed Medium acceptance remain unresolved.
- **Next:** Verify supported CI and review the selective reader before integrating any event query.

### 2026-10-23 - Replace idle-handle identity with owned SQLite pool proof

- **Evidence:** Supported Windows job `119874095957` at `cc58b32` fails the pool-cleanup test's `Assert.Same`: the old native handle is closed and a new handle is returned. The old helper disposed its connection before returning the handle; Reflow cleanup also contained a concrete global pool-clear path. Which mechanism caused that particular failure is not established.
- **Changed:** Both synthetic connections remain actively leased across the ownership assertion. A's scoped eviction, native closure and persisted data are verified; B's pending-retention flag and SQL usability remain intact. An explicit two-fixture collateral-clear negative control must fail. Reflow now clears only its own exact pool.
- **Provider boundary:** Official pinned SQLite 9.0.4 source confirms `_canBePooled`/`DoNotPool` semantics. The test-only reflection probe validates a private instance Boolean and fails closed. It does not use the live-ineligible `CanBePooled` property. Proper Option handling narrows the nullable native handle.
- **Validation:** Fresh independent F# review approves. Root build passes with zero warnings/errors; 67 selected instrumented drain/Reflow cases and all 2,191 Core tests pass without skips. The combined native coverage gates pass unchanged.
- **Blockers:** The supported Windows failure still requires exact-head verification; no retry, global clear, skipped assertion or relaxed pool-ownership claim is accepted.
- **Next:** Push this fixture repair with the reviewed control lifetime correction and inspect both hosted results.

### 2026-10-23 - Bind terminal snapshots to verified live-image evidence

- **Changed:** The measured retained-identity policy stores the first live image proof before arming, bound to the same open handle reference and PID/birth key. Terminal reads require successful completion of that reader's actual wait, fresh native exit facts and rechecked image/source pins. They do not call the post-exit image API or catch its failure as success.
- **Review:** Fresh independent C# review approves the three-file lifetime correction. A separate language author supplies two exact callback-parameter renames for the compiler-proven discard-binding errors; no acceptance predicate is removed.
- **Validation:** Serial portable/Windows-targeted builds pass with zero warnings/errors; all 142 control and 549 full Servicing cases pass. Actual Model coverage hits all 48 policy lines. Real-policy fakes require two live image queries, zero postmortem queries and five fresh snapshots/pin reads, with negative binding/wait/source/transport/callback/cleanup cases.
- **Integration:** Root build and all 2,191 Core tests pass. The combined current candidate measures 15,968/18,525 lines (86.1970%) and 15,707/25,385 branches (61.8751%) over the same five assemblies. The separate SQLite-pool test repair is independently reviewed and will be published alongside this correction.
- **Blockers:** Actual post-correction control completion, supported-Windows pool proof and original PowerShell/installed acceptance remain pending.
- **Next:** Publish both reviewed corrections and require exact-head hosted results.

### 2026-10-23 - Observe the post-exit image query failure

- **Evidence:** At `cc58b32`, run `39793193663`, job `119874067085`, all 981 Windows controls and fresh publication pass. The actual advisory observation is `exitCheck=10 nativeError=31`: `QueryFullProcessImageName` fails during the retained post-exit identity read.
- **Finding:** The synthetic FailFast status remains `0x80131623`; the initiating failure is the image query, not the accepted status. This is now an actual observation rather than the earlier synthetic fixture.
- **Contract:** Image/path/hash proof must be established before arming. The retained process handle, fresh native process/creation/exit facts and rechecked publication pins must continue to bind that proof after termination. Microsoft documents that process code is removed on termination while externally retained kernel objects remain valid.
- **Ownership:** The control author is implementing an explicit live-proof/terminal-snapshot boundary with negative binding tests; catching error 31 and treating cached data as generic success is prohibited.
- **Blockers:** Actual control completion and original PowerShell/installed acceptance remain unresolved.
- **Next:** Independently review that lifetime correction and rerun the control.

### 2026-10-23 - Preserve exit gates while observing their first refusing predicate

- **Changed:** Independent C# authoring adds fixed check IDs for the exit stage, retained native reads, pinned publication and identity predicates. Operational failures retain their location and direct Win32 code; cancellation and unexpected exceptions retain their existing treatment. Failure-only advisory output gains the observation; the success summary and strict 28-field report are unchanged.
- **Review:** The final independent control review approves the six-file delta. The zero-exit-time predicate now precedes chronological ordering but accepts exactly the same identities. Synthetic `check=10/nativeError=31` is test data, not a Windows finding.
- **Validation:** Mechanical application verifies all 27 literal hunks and six exact file hashes. Serial portable/Windows-targeted and root builds pass with zero warnings/errors; 112 control, 519 full Servicing, 2,191 Core and 985 PowerShell controls pass without skips. Lint and diff hygiene pass.
- **Coverage:** The same five assemblies measure 15,908/18,465 lines (86.1522%) and 15,653/25,357 branches (61.7305%), passing unchanged gates. Earlier exact-status head `8e50baf` also has both supported jobs green; its actual Windows coverage is 86.4182%/62.0364%. New-head hosted coverage remains pending.
- **Blockers:** Actual initiating exit-stage failure, successful control completion and original PowerShell/installed acceptance remain unresolved.
- **Next:** Publish the reviewed observations and inspect the actual first failing check.

### 2026-10-22 - Retain the remaining exit-stage refusal after correcting the status

- **Evidence:** Published `8e50baf`, run `39789403070`, job `119861091237`: all 762 Windows controls and fresh publication pass. The wrapper still refuses phase 9; typed facts remain `status=1 exit=2148734499 error=6 cleanupError=0 reportError=0`.
- **Observed:** Recognising the documented status is necessary but does not establish actual control success. Another exit-stage condition or operational failure remains; cleanup/report fault fields are zero. No additional status, weakened predicate or PowerShell cause is inferred.
- **Ownership:** The same control author is tracing only the coupled retained-exit/native-adapter boundary, with a reproducing test or bounded predicate facts required before another change.
- **Blockers:** Actual accepted control completion and the original PowerShell/installed lifecycle gates remain open.
- **Next:** Identify the remaining exit-stage predicate without changing its acceptance requirement.

### 2026-10-22 - Accept only the verified .NET 9 FailFast code

- **Changed:** Independent C# authoring and review approve adding exactly `0x80131623` to the control and PowerShell report allowlists. The four prior values remain; neighbours, ordinary/refusal exits, `0xC0000142` and forced termination remain negative. All other success and original acceptance gates are unchanged.
- **Validation:** Serial portable/Windows-targeted builds and root build pass with zero warnings/errors; 85 control cases, 492 full Servicing cases, 2,191 Core cases and 766 PowerShell controls pass without skips. Workflow lint and diff hygiene pass. Reports bind the observed decimal/hex status to exact source/image hashes with original/native acceptance zero.
- **Coverage:** The unchanged five-assembly merge measures 15,854/18,410 lines (86.1162%) and 15,649/25,347 branches (61.7391%). Both new control branches are hit; unchanged Core/Service files lose 55 hits net against the prior local run. Both gates pass, but the earlier 61.9530% local aggregate is not claimed preserved.
- **Blockers:** Corrected actual control execution and exact-head hosted coverage remain pending. The synthetic control correction establishes no original PowerShell cause.
- **Next:** Publish the reviewed exact-code fix and require actual control completion.

### 2026-10-22 - Identify the control's documented FailFast status

- **Evidence:** At `833a486`, run `39786793545`, job `119852024927`, all 734 Windows controls and fresh publication pass. The typed facts are `available=1 status=1 exit=2148734499 error=6 cleanupError=0 reportError=0`, with native acceptance zero.
- **Finding:** The observed exit is exactly `0x80131623`; the control reports `CrashFault.Exit`. Pinned official .NET 9 `corerror.xml` defines this as `COR_E_FAILFAST`, and `system.cpp` passes that constant from FailFast to fatal-error handling. The existing allowlist omits it; `0x80131506` is the distinct `COR_E_EXECUTIONENGINE`.
- **Changed:** A fresh C# author owns the exact-status correction across the control, PowerShell success validator and matching positive/negative tests. Existing exact codes and all identity, framing, source, settlement and cleanup gates remain.
- **Blockers:** The corrected control still needs independent review and actual hosted success. This finding concerns the synthetic control and establishes no cause for the original PowerShell startup failure.
- **Next:** Validate the narrow constant correction and rerun the genuine control.

### 2026-10-22 - Correct wrapper fixture newlines without relaxing the helper protocol

- **Evidence:** At `b54e466`, Windows run `39785785768`, job `119848496417`, stops in portable controls: 729 pass and five wrapper-output assertions fail. Publication and native execution are skipped.
- **Changed:** Source counter mapping identifies exactly the five wrapper fixture assertions. Forced-CRLF captures reproduce their newline mismatch with exit 1, empty stderr and settled tasks. Expected wrapper lines now use `Environment.NewLine`; fields, order and exactly-once assertions remain exact. The helper's LF grammar and CRLF refusal are unchanged.
- **Validation:** Parent reviews the complete one-file repair; all 738 macOS controls, workflow lint and diff hygiene pass.
- **Blockers:** Windows must confirm this repair before the projected helper refusal can be observed.
- **Next:** Publish the test-only fix and inspect the typed control facts.

### 2026-10-22 - Project the fixed helper summary after report refusal

- **Changed:** A fresh author adds an advisory projection only when phase-9 failure retains completed capture. Exact UTF-8 grammar, actual exit/status coherence, settled tasks, empty stderr and closed numeric fields precede reconstruction; unavailable data becomes a fixed `available=0` row. Raw text is never echoed.
- **Validation:** Parent reviews the complete three-file delta; all 738 macOS controls, workflow lint and diff hygiene pass. Real owned fixtures verify successful and refused projections, malformed input and exactly-once output before cleanup. Strict success/report validation and upload eligibility are unchanged.
- **Blockers:** The actual helper fault is still unknown until the published projection runs on Windows. No control success, event correlation or original acceptance claim follows from portable fixtures.
- **Next:** Publish the projection and fix the demonstrated helper boundary.

### 2026-10-22 - Locate the hosted control refusal at report validation

- **Evidence:** Published `1a0e716`, run `39784272929`, job `119843175079`: all 422 selected Windows controls and fresh source-bound publication pass. The flushed diagnostic is `phase=9 family=1 reason=12 nativeError=0`.
- **Observed:** The path reaches actual private-job admission, fixed controller launch and completed bounded capture before strict report/summary validation refuses. It does not identify which child/result fact failed. No accepted control success, normal scope-release proof or PowerShell cause follows; upload remains skipped.
- **Changed:** A fresh author owns a strict advisory projection of the already captured helper's finite numeric summary. The ended wrapper author is not reused. Raw output remains private, and success validation is unchanged.
- **Supported hosts:** At wrapper head `86d631c`, run `39782062555`, both supported jobs succeed again; its installer job still fails the native gate and diagnostic. Main remains unmerged.
- **Next:** Observe the typed helper refusal and fix only the demonstrated failing boundary.

### 2026-10-22 - Bound wrapper failure facts and verify both supported hosts

- **Changed:** The wrapper flushes one closed numeric phase/failure row before its assigned-scope cleanup. Classification uses a fixed exact-type family, bounded wrapper depth, exact internal reasons and UInt32 Win32 codes; unknowns remain zero. Success, source pins, report validation and upload eligibility are unchanged.
- **Validation:** Parent source review, all 426 macOS portable controls and workflow lint pass. Actual guard-output fixtures prove the diagnostic row and join every capture task. The wrapper author's third cycle is complete.
- **Hosted evidence:** At C# head `5f60e3c`, run `39781578457`, supported Windows job `119833900720` and macOS job `119833900273` both succeed. Windows executes 483 Servicing and 2,187 selected Core cases without skips; the unchanged coverage gate reports 15,347/17,759 lines (86.4182%) and 15,517/25,022 branches (62.0134%). The installer job still fails its native gate and diagnostic; publication remains blocked.
- **Blockers:** The first fixed-control wrapper failed after successful publication without an identifying phase. No actual control success or PowerShell cause is established.
- **Next:** Publish these failure facts and inspect the exact refusing phase on disposable hosted Windows.

### 2026-10-22 - Verify hosted publication and retain the control refusal

- **Evidence:** Published wrapper `86d631c`, hosted run `39782056322`, job `119835528294`. All 318 selected Windows portable controls pass. Fresh current-source private publication succeeds in 74 seconds.
- **Observed:** The fixed wrapper exits 1 after seven seconds and reports `published=0`, `nativeAcceptance=0`. Upload is correctly skipped. The numeric row does not identify whether admission, helper launch, capture or report validation failed; no control success or cause is established.
- **Changed:** The wrapper author is adding bounded closed phase/failure facts before assigned-scope abort. No raw exception, stack, path or child output is authorised for export.
- **Blockers:** Actual containment/control completion and the original PowerShell/installed lifecycle gates remain open. Exact-head supported CI is still running.
- **Next:** Publish the closed failure facts and identify the actual refusing boundary before changing its behaviour.

### 2026-10-22 - Publish the fixed source-bound hosted crash supervisor

- **Changed:** Five PowerShell/workflow files build a fresh complete helper bundle, bind SDK inventory and checkout inputs, load matching assemblies, enter the reviewed current-step private job and launch only the fixed probe. An independent two-minute worker deadline precedes entry; successful completion requires actual helper exit, joined bounded capture, exact report/source agreement and no remaining children.
- **Validation:** Parent review and independent execution pass all 319 macOS portable controls and workflow lint. Synthetic Git controls found and fixed premature root-glob expansion. Negative process fixtures retain and observe actual read/wait operations after teardown. C# source remains the reviewed `5f60e3c`; its root build/tests and full five-assembly coverage remain unchanged.
- **Privacy:** Raw output, publication receipts, SDK inventories and logs remain private. Only the fresh validated numeric/hash report is eligible for three-day upload; original-gate and native-acceptance fields remain zero.
- **Publication:** The reviewed C# component is committed and pushed as `5f60e3c`; exact-head CI is running. The wrapper is ready for its separate commit on the same draft PR branch.
- **Blockers:** No actual hosted crash-control result exists yet. Kernel/deadline behaviour, event correlation, original restricted PowerShell execution and installed Medium acceptance remain open.
- **Next:** Push the wrapper and inspect its exact-head disposable-Windows result before adding event observation.

### 2026-10-22 - Approve nonfatal ownership and measure the complete coverage scope

- **Changed:** A fresh independent revision removes the hosting PowerShell FailFast fallback. Shared finalization state records one terminal result and prevents retries through a consumed SafeHandle; checked close failures still preserve assigned termination intent and surface errors. An unrecoverable finalization can retain a raw kernel resource until process exit.
- **Review:** The final C# review approves the source after separate authors corrected the nested constructor, premature release disarming and fatal pre-assignment fallback. Rejected authors did not contribute to their replacements.
- **Validation:** Serial portable and Windows-targeted builds pass with zero warnings/errors; all 89 job-policy/ownership cases pass. Root build passes, and the existing native Coverlet/flush-hook execution passes 483 Servicing and 2,191 Core tests without skips. The same five assemblies measure 15,854/18,410 lines (86.1162%) and 15,702/25,345 branches (61.9530%), above both unchanged gates.
- **Integration:** The five-file hosted wrapper candidate passes 132 independently rerun portable controls and workflow lint. Its source-checkout and failed-capture lifetime controls are being strengthened before publication.
- **Blockers:** No native control has run. Kernel containment, external-deadline behaviour, event correlation and the original restricted PowerShell/installed Medium gates remain unverified.
- **Next:** Finish wrapper review, publish the exact source and inspect the bounded disposable-Windows result.

### 2026-10-22 - Exercise owned crash lifetimes and reject premature job release

- **Changed:** The fixed crash-control component now implements retained identity, one ready/arm exchange, actual exit observation, scoped post-exit handle retention and joined cleanup. Its portable implementation is measured in the existing Acceptance.Model assembly.
- **Validation:** All 76 crash-control cases and 65 current-step job-policy cases pass; the full portable Servicing suite passes 459/459 without skips. The collector measures Acceptance.Model at 97.05% line and 88.92% branch. These are component measurements; the five-assembly aggregate still requires hosted verification.
- **Review:** Independent review rejects the native job facade: its nested-handle constructor does not compile, and a failed normal close can disable the promised assigned-scope abort. The original author is locked out; a fresh C# author owns both corrections and native-owner failure tests.
- **Integration:** A separate author is wiring current-source publication, bounded private capture and the independent hosted-step deadline. Native execution remains prohibited until the corrected containment and wrapper are reviewed.
- **Blockers:** No native crash-control run or event correlation exists yet. Restricted PowerShell and installed Medium acceptance remain unresolved; draft PR #30 is unmerged and remote main remains `e7758a3`.
- **Next:** Compile and review the corrected native owner, then execute the contained control on disposable public hosted Windows.

### 2026-10-22 - Decompose the unimplemented event route into an owned control

- **Decision:** Implement only the fixed published-apphost crash-control lifetime first: verified retained identity before one stdin arm, actual exit, bounded drains/wait/cleanup and a scoped observation interval that retains its handles after exit. Do not attempt event queries or the PowerShell baseline in this component.
- **Ownership:** A fresh C# author owns this smaller artifact; the ended all-in-one observer author is not reused. Native execution remains limited to disposable hosted Windows, with portable capability/fake tests on other platforms.
- **Grounding:** Microsoft documents WerSetFlags as current-process-only and WerGetFlags as handle-based readback, both in Kernel32. The Microsoft SDK header confirms the no-UI, queue and no-heap constants; do not substitute guessed flags or change host WER policy.
- **Privacy:** The synthetic child must receive only a minimal non-secret environment, avoid reporting UI, heap/snapshots and upload flags, and export no dump/raw child output. No OS-managed WER process becomes an owned child or cleanup target.
- **Preserved:** Original restricted token, integrity, console arms, source/image, exit-37 requirement and native/installer acceptance remain unchanged. A control crash is not PowerShell or installer acceptance.
- **Blockers:** No new control implementation or native lifetime proof exists yet; exact event correlation and installed acceptance remain subsequent work.
- **Next:** Review and validate this lifetime component before adding any selective event reader or baseline query.

### 2026-10-22 - Verify public-wrapper coverage and fixture ports on both supported hosts

- **Evidence:** Published `ea47975`, run `39763777189`: supported Windows job `119774079322` and macOS job `119774079249` both succeed. Windows runs 2,187 selected Core and 318 Servicing tests without skips, plus 102 JavaScript units and 61 real isolated browser journeys; four governed Unix-only exclusions remain explicit.
- **Coverage:** Actual artifact `12419832470` retains the same five assemblies, reports 86.01% line and 61.37% branch, and records hits on all four public-wrapper lines 130-133. Line coverage exceeds the earlier measured level; branch coverage exceeds the previous 61.26% but not the earlier 61.58% peak. The 85%/60% gates are unchanged; issue #18 is not declared closed.
- **Outcome:** The deterministic public-extraction and common-fixture port-ownership tasks now have exact-head hosted proof. The native factory and production application remain unchanged.
- **Native blocker:** Installer job `119774079511`, artifact `12419218935`, retains 541 portable controls and 500/501 native passes. The only native failure is original restricted PowerShell execution. Actual SDK/MASM facts remain bound; the first refusing line records generic error wording only (1), no recognised first token (0), and the read-expression bit. It establishes no loader cause.
- **Limits:** All relevant controlled-query/evaluator cycles are exhausted. The independent event schema is verified but its safe C# observation command remains unimplemented. No raw diagnostic exception was authorised when the owner was unavailable; numeric-only reporting remains in force.
- **Stocktake:** Draft PR #30 remains unmerged, remote main remains `e7758a3`, and #16/#18/#28 still require actual acceptance. Protected-namespace legacy repair #24 remains separately blocked. The overall task is incomplete.
- **Next:** Identify the actual startup/protocol failure through authorised evidence before changing its implementation; retain all original exit, token, integrity, source and installed-lifecycle gates.

### 2026-10-22 - Publish the final closed refusal fingerprint without another query variant

- **Changed:** Two frozen numeric facts distinguish fixed formatter/error wording and the first fixed syntax-token class, only after earlier actual MASM readback in the bound SDK-completed arm frame. This closes the evaluator owner's final cycle without changing the query or debugger commands.
- **Validation:** Parent complete source/test review approves. All 541 startup controls and installer static controls pass. Driver, five program, query and frame hashes are unchanged; existing Kind/Source and refusal facts retain their prior meaning.
- **Privacy:** No line, word, path, identity, arbitrary character or new text hash is exported. Wording and prefix classes describe text, not cause or authority. Source echoes cannot supply flags/completions; first-fault and exact output/report bounds stay enforced.
- **Integration:** The accompanying independently reviewed test-only changes pass a clean root build, 23 instrumented regression cases and 2,191 full Core tests. Native Windows discovery remains 501 and coverage gates remain 85%/60%.
- **Blockers:** Actual first-line classification, genuine restricted PowerShell execution, installed Medium acceptance and the independent event diagnostic remain unresolved. No raw-line exception or cause claim follows.
- **Next:** Push the two reviewed commits together and inspect exact-head supported-host coverage plus the bounded native diagnostic; do not infer success from portable fingerprints.

### 2026-10-22 - Publish measured public extraction and non-reissued fixture ports

- **Changed:** A deterministic generation-7 test directly exercises the public extraction wrapper and checks exact bytes, enrichment, sidecar/FTS publication and unchanged stored document/generation/ledger. The actual operation is directly awaited before fixture disposal; there is no local cancellation/deadline claim for this uncancellable API.
- **Measured:** Filtered Cobertura XML records a hit on each Stages line 130-133. Canonical typed SQL boxing fixes the compiler-verified nullness mismatch without casts or suppressions. Fresh independent F# review approves the final source.
- **Additional finding:** A full run exposed `AddressAlreadyInUse` during a canonical-UI fixture's real cleanup rebind. The source does not establish who occupied the port. The shared allocator now atomically claims before probing and never reissues its ports within the process; finite allocation still surfaces foreign/nonparticipating collisions.
- **Regression:** A real joined host and an owned contender prove collision remains visible, the issued candidate cannot be reclaimed, and the original rebind succeeds while a different claimed port is listening. The original final rebind assertion remains unchanged; independent F# review approves.
- **Validation:** Root build passes with zero warnings/errors; all 23 instrumented host/canonical-UI/wrapper cases and 2,191 full Core tests pass without skips. Exact wrapper-line XML, BOM/LF and diff hygiene were checked. No production, native-factory or coverage-threshold change.
- **Blockers:** Exact-head hosted aggregate coverage and port behaviour remain unproved. The genuine restricted PowerShell/installed lifecycle and event-diagnostic implementation remain unresolved.
- **Next:** Publish these test-only revisions and verify supported Windows/macOS results without lowering gates or treating retries as fixes.

### 2026-10-22 - Verify the authentication fix on both supported hosts

- **Evidence:** Published `606debb`, run `39757060356`: supported Windows job `119751624143` and macOS job `119751624157` both succeed, including instrumented Core/Servicing, real isolated browser smoke and unchanged 85%/60% gates. Windows executes 2,185 selected Core tests and 318 Servicing tests without skips; the four governed Unix-only exclusions remain explicit.
- **Outcome:** The observed startup-failure HTTP timeout is resolved on its actual instrumented Windows acceptance run. The dedicated host lifetime, bounded startup request and failure-path task ownership retain the original teardown proof. Its reproducibility task is now done.
- **Coverage:** Actual Windows artifact `12417595393` reports 85.98% line and 61.26% branch over the same five assemblies. Both gates pass, but this is below the earlier measured level. Production code is unchanged; exact hit comparison loses Stages lines 130-133 and gains ServiceAccessHost line 384.
- **Decision:** Add a deterministic regression for the missed stage business path rather than lowering thresholds, editing coverage or assuming all branch variance is meaningful. A fresh F# owner is scoped only to those stage lines.
- **Blockers:** Genuine restricted PowerShell/installed acceptance remains blocked. The event route still lacks an implementation despite verified metadata; no root-cause claim or merge follows from these host successes.
- **Next:** Review and measure the deterministic stage regression, preserving the now-verified authentication behaviour.

### 2026-10-22 - Publish the owned authentication-startup fixture revision

- **Changed:** The fixture joins a dedicated synchronous host task and gives startup health a separate linked 20-second budget; ordinary two-second requests and all production deadlines remain unchanged. The original real 503-before-failure and exact failure/quiescence/authority/port-release proof is preserved.
- **Ownership:** Separately started HTTP operations are cancelled and actually observed before fixture cleanup. A token-cancellation handshake holds settlement; the original verification exception and identical unrelated transport exception remain observable even when HttpClient wraps cancellation.
- **Review:** Two candidates were rejected for unjoined failure paths and a cancellation/exception race. Their authors were locked out; independent revisions were mechanically applied. Fresh final F# review `7be2846d` approves correctness and mandatory idioms.
- **Validation:** Root build passes with zero warnings/errors; all 17 instrumented host/drain tests and 2,189 full Core tests pass without skips. Exact authored replacement bytes, BOM/LF and diff hygiene were verified. Five new discovered cases exercise real forwarding, transport failure and three held-unwind paths.
- **Preserved:** No production authentication, native child, installer, coverage-threshold or 501-case Windows-gate changes. Existing assertions remain; no timeout is treated as success.
- **Blockers:** Instrumented hosted Windows must verify the observed CI failure is resolved. Genuine restricted PowerShell and installed acceptance remain blocked; the independent event diagnostic remains unimplemented.
- **Next:** Publish the exact fixture source and inspect the supported Windows coverage job before closing its reproducibility task.

### 2026-10-22 - Retain verified schema and address the actual authentication CI failure

- **Evidence:** Metadata-only run `39749138163`, job `119724986791`, artifact `12412462737` succeeds at `81239bc`. Application Error event 1000/version 0 has 15 fields and all six known names uniquely present. PID is HexInt32, creation time HexInt64, and image/exception/module fields are UnicodeString; template hash is `cd0a3d85a95e98a2909d1a77bada29db34ed1abbe37a058d6c06e9aca599557e`.
- **Authoring gap:** The C# owner delivered no patch after its final cycle. Owned crash-control lifetime, selective identity-first event reading and fail-closed orchestration remain unimplemented. These are unfinished implementation obligations, not demonstrated Windows/API limitations. No event query or fallback was introduced.
- **New failure:** Supported Windows run `39749145727`, job `119725013267`, fails the authentication test that proves control-startup failure tears down an already-bound HTTP listener. Its HTTP request exceeds the fixture's two-second client timeout under coverage. Servicing 318, Setup 1,277 and Control 133 tests pass; macOS and Tray pass.
- **Decision:** A fresh F# owner is investigating deterministic fixture/barrier or budget separation while preserving the real before/failure/teardown proof. Do not skip assertions, swallow timeouts or change production deadlines.
- **Blockers:** Authentication CI reproducibility and genuine restricted PowerShell/installed acceptance remain unresolved. The independent event diagnostic is not implemented.
- **Next:** Review the F# fix and verify its exact assertions under instrumented Windows before making any completion or merge claim.

### 2026-10-22 - Publish the fixed publisher-metadata probe

- **Changed:** A dedicated short hosted Windows job reads only Application Error event-1000 publisher templates. Exact known field names/types become finite numeric codes and template hashes; no event bodies, messages, resource paths or raw templates are exported.
- **Validation:** Parent complete three-file source/test/workflow review approves. Portable metadata controls pass 3,066 assertions; largest fixture report is 2,487 bytes. Existing installer static controls and workflow actionlint pass. Root build passes with zero warnings/errors; all 2,184 tests pass without skips.
- **Bounds:** Eight versions, 16 KiB per template and a 4,096-byte final report. DTD/entities, malformed/duplicate/unsupported critical metadata, reflected/reparse destinations and pre-existing files refuse. Only successfully published bounded reports may upload, with three-day retention.
- **Boundary:** This proves portable parsing, privacy, publication and workflow contracts, not the actual OS schema or lossless event-query comparison. The independent C# diagnostic remains unimplemented pending those facts.
- **Blockers:** Actual provider template fields/types and exact creation-FILETIME server comparison remain unverified.
- **Next:** Inspect the metadata-only hosted artifact and provide verified schema facts to the C# owner before any event query is authored.

### 2026-10-22 - Verify publisher metadata before authoring event correlation

- **Found:** The C# owner confirms retained PID, creation FILETIME, full image and the fixed untraced NoWindow factory are accessible without changing native creation. It stopped without edits because Application Error event-1000 field types and exact server-side birth comparison were not verified.
- **Decision:** First collect only fixed publisher/template metadata on a short disposable hosted Windows job. Map known field names/types to finite numeric codes and template hashes; do not read event instances, messages, paths or user data.
- **Grounding:** Microsoft documents EventMetadata.Template as the provider's event-data structure. Its Windows Event Log XPath subset does not support node sets, so generic XPath conversion assumptions cannot establish lossless creation-time matching.
- **Boundary:** No event query, process launch, provider registration or raw template export is authorised by this probe. Missing/duplicate/unsupported critical metadata must be explicitly unavailable. The later diagnostic remains unimplemented until schema and comparison requirements are satisfied.
- **Blockers:** Actual fixed provider schema and exact native comparison semantics remain unproved.
- **Next:** Review the independently authored metadata-only probe and its portable refusal/report controls, then inspect actual OS schema facts before returning to C# implementation.

### 2026-10-22 - Confirm MASM and pursue independent untraced failure evidence

- **Evidence:** Published `2cd19fa`, run `39744910451`, job `119710849575`, artifact `12411132993`, passes 538 controls and 500/501 native cases without skips. Exact post-SDK readback records MASM (1), but the same unknown read-expression line still causes protocol refusal. No typed field, query completion or target continuation is accepted.
- **Conclusion:** Deterministic evaluator configuration is now measured, but it does not explain the refusal. Leave the CDB query/default controls alone.
- **Decision:** A fresh C# owner is implementing independent evidence from one genuine, fixed, untraced restricted PowerShell launch and Windows Application Error structured events. This is not another debugger-program variant.
- **Boundary:** Event access must be server-filtered to the retained child PID and creation time, then match its actual image and bounded time window. No broad logs, raw XML/message/path/identity export, provider registration or changed token/console/permissions. Missing exact correlation must be explicitly unavailable, never a pass.
- **Meaning:** Even an exactly associated event is diagnostic association, not a proven root DLL cause. It cannot replace original PowerShell/native/Medium/MSI acceptance.
- **Blockers:** The new diagnostic's safe correlation/implementation and actual Windows outcome remain unproved; installed acceptance remains blocked.
- **Next:** Review the independently authored fixed-command diagnostic and portable refusal/lifetime tests before wiring a separate hosted observation.

### 2026-10-22 - Publish the post-SDK evaluator binding and exact readback

- **Changed:** One fixed `.expr /s masm` command establishes private debugger parsing immediately after SDK return and before the unchanged typed query. A frozen closed scalar records exact documented stdout readback only within the valid SDK-completed arm frame.
- **Validation:** Parent complete source/test review approves the second independently authored evaluator cycle. All 538 controls and installer static controls pass. Root build has zero warnings/errors; all 2,184 tests pass without skips.
- **Preserved:** Removing exactly the configuration insertion recovers prior whole driver/arm hashes. Four metadata goldens, typed query bytes, frame hash, literal input, token/console/permission facts and all authority predicates are unchanged.
- **Evidence:** Real process transport now carries the fixed readback before scalar fixtures. Echo/forgery/context/stderr/late/fault/budget controls cannot supply readback. Readback is advisory and never supplies typed values, completions or continuation; maximum reports remain below 4,096 bytes.
- **Blockers:** Actual post-SDK evaluator, protocol refusal, genuine PowerShell exit 37 and installed Medium acceptance remain unresolved.
- **Next:** Inspect exact-source Windows readback and query facts without inferring the previous evaluator state or a loader cause.

### 2026-10-22 - Bind the evaluator at the typed-read boundary

- **Evidence:** Published `5246c80`, run `39741809646`, job `119700576760`, artifact `12410301571`, reaches the new fixed startup configuration and SDK completion 1, then retains the same unknown query refusal and lexical read-expression bit. Startup pinning did not resolve the protocol boundary.
- **Decision:** Establish the private debugger's MASM dialect immediately after opaque SDK extension execution and before the unchanged typed query, using one fixed `.expr /s masm` command. Retain the startup environment/argv correction.
- **Observation:** Capture only the documented exact evaluator readback as a closed advisory scalar within the already-bound SDK-completed arm frame. It must not supply typed flags, completion or continuation authority.
- **Boundary:** No C++ expression, four-byte guard, SDK operation, query marker, literal input or native token/console/permission contract changes. The previous actual default and its role in the failure remain unknown.
- **Blockers:** Actual post-SDK evaluator, typed readback, genuine PowerShell execution and installed Medium acceptance remain unresolved.
- **Next:** Review the point-of-use configuration and exact readback controls before another disposable Windows run.

### 2026-10-22 - Publish the fixed debugger-evaluator bootstrap

- **Changed:** The private CDB child clears inherited `_NT_EXPR_EVAL` and receives a fixed `-ee masm` pair before startup/target arguments. The host environment, generated programs, query expressions and target authority remain unchanged.
- **Validation:** Parent full source/test review approves the independently authored two-line correction. All 533 controls and installer static controls pass. Root build has zero warnings/errors; all 2,184 tests pass without skips.
- **Evidence:** Real ProcessStartInfo preparation controls cover simulated inherited C++/MASM/invalid selectors, exact complete argv and prior settings. Mutated/missing/duplicate/misplaced options fail expectations. Reverting the two bootstrap edits recovers the prior driver hash; five program goldens and frame hash stay fixed.
- **Boundary:** This proves deterministic launch configuration, not the actual runtime evaluator or a cause of the existing refusal. No process was started by the portable controls and no raw diagnostics were exported.
- **Blockers:** Actual fixed-dialect Windows behaviour, genuine PowerShell execution and installed Medium acceptance remain unresolved.
- **Next:** Inspect the exact-source disposable Windows result before attributing the refusal to inherited configuration.

### 2026-10-22 - Bind the debugger's assumed expression dialect

- **Found:** The fixed CDB command programs assume MASM for command parameters and use explicit C++ subexpressions for typed access, but startup neither clears `_NT_EXPR_EVAL` nor selects an evaluator. That leaves command interpretation dependent on inherited child environment despite source pins.
- **Grounding:** Microsoft's Evaluating Expressions and General Environment Variables references document that environment selection and the `-ee` startup option. They warn that C++ is not well-suited to parsing general debugger-command parameters and recommend MASM with explicit evaluator switches where needed.
- **Decision:** Independently harden the private debugger bootstrap by removing the inherited selector from its child environment and passing the fixed `-ee masm` pair. This is a separate deterministic-configuration correction, not another literal/query variant.
- **Boundary:** Do not change any generated command program, expression, SDK call, echo acceptance or native gate. No host environment mutation, selector or fallback is added. The actual inherited value and its relationship to the refusal remain unknown.
- **Blockers:** The original protocol refusal and PowerShell startup are not yet resolved.
- **Next:** Review real ProcessStartInfo preparation controls, then test the fixed dialect on disposable hosted Windows.

### 2026-10-22 - Escalate the unchanged startup protocol blocker after the final control

- **Evidence:** Published `02604a5`, run `39738837797`, job `119690882458`, artifact `12408986181`, passes 530 controls and 500/501 native cases without skips. The original restricted PowerShell exit-37 case still fails.
- **Observed:** The new driver and arm-program hashes are present. SDK completion remains 1; the controller still refuses an unclassified line containing the exact read expression. Query kind/source remain unknown/unavailable, trimmed family is 0 and lexical mask is 128. No typed candidate, query completion, arm end or target continuation is accepted.
- **Conclusion:** Neither literal execution nor the explicit optional delimiter resolved this protocol boundary. The three-cycle control is exhausted; no fourth variant, query rewrite or cause claim is authorised by these results.
- **Escalation:** The actual first refusing line remains unidentified under numeric-only reporting. A bounded redacted-line exception requires an explicit owner decision; none was received. Keep that boundary and the original token, integrity, exit, source and acceptance gates intact.
- **Stocktake:** All work is on draft PR #30, not main. Issues #16/#18/#28 remain open pending installed acceptance; #24 remains separately blocked. Whole-Medium routing/provider separation and installed authentication, servicing/recovery and preserving-uninstall proof are still required.
- **Blockers:** `restricted-powershell-startup` is now explicitly blocked in session tracking rather than presented as active implementation. Windows supported tests, Tray and compatibility jobs pass; macOS supported tests were still running at the last read.
- **Next:** Obtain authorised evidence that identifies the actual protocol refusal before making another semantic change. Do not mark the overall task complete or merge this candidate.

### 2026-10-22 - Publish the one-character typed-read delimiter control

- **Changed:** One explicit semicolon terminates the unchanged typed printf expression before the nested conditional closes. Exact source constants and affected hashes follow that single byte. Test-only path-shim cleanup now proves restoration of the original cmdlet.
- **Validation:** Parent full source/test review approves the final independently authored cycle. All 530 controls and installer static controls pass. Root build passes with zero warnings/errors; all 2,184 tests pass without skips.
- **Preserved:** Removing exactly the semicolon recovers prior whole arm-program and driver hashes. Four metadata goldens, frame hash, both C++ expressions, four-byte guard, SDK operation, completion positions, literal input and all authority/cleanup/budget rules are unchanged.
- **Boundary:** The delimiter is documented as optional. The change remains an experiment, not a claimed syntax diagnosis. Missing fields/completions and wrong flags still prevent continuation; tampered source refuses before input.
- **Blockers:** Actual query behaviour, genuine PowerShell exit 37 and installed Medium lifecycle remain unresolved.
- **Next:** Run the exact published source on disposable Windows; retain an unresolved result as blocking rather than inventing a fourth control variant.

### 2026-10-22 - Isolate the typed-read statement delimiter

- **Evidence:** Published `00d3dc3`, run `39736112447`, job `119682051422`, artifact `12407602118`, passes 527 controls and 500/501 native cases without skips. Accepted SDK completion remains 1. The first query mask is 128: the exact read expression occurs, but the exact sizeof fragment, fixed diagnostic wording, caret and edge-whitespace bits do not. The original query kind/source remain unknown/unavailable.
- **Decision:** The final literal-control cycle tests one explicit semicolon after the unchanged typed printf expression, before the nested conditional closes. All expressions, the four-byte guard, SDK call, completion positions and target authority stay fixed.
- **Grounding:** Microsoft's `.if` reference explicitly says the final command before a closing brace does not require a semicolon. This is therefore an optional-delimiter control, not a claimed missing-semicolon bug or syntax diagnosis.
- **Boundary:** Only the changed program byte and its exact source/hash fixtures may change. There is no additional evaluation, read, stage, input, fallback or widened echo acceptance. Numeric-only reporting remains in force.
- **Blockers:** The actual refusing text, typed flag readback, PowerShell exit 37 and installed Medium lifecycle remain unresolved.
- **Next:** Review the one-character control and compare actual Windows evidence; do not invent a fourth variant if it remains unclassified.

### 2026-10-22 - Retain the literal result and expose only closed first-line display facts

- **Evidence:** Published `b538314`, run `39734265979`, job `119676198634`, artifact `12406373436`, passes 524 controls and 500/501 native cases without skips. Both supported OS jobs and Tray pass. Literal execution retains SDK completion mask 1 but reaches the same unknown query refusal, with no typed candidate, query completion or target continuation.
- **Meaning:** This control did not resolve the refusal and establishes no condensation, type-information, memory, syntax or loader cause.
- **Changed:** The literal-control owner adds a frozen numeric mask for fixed diagnostic wording and exact pinned source fragments, plus a separate existing-family observation after ASCII HorizontalTrim. Original Kind/Source, protocol/stage facts and marker acceptance remain unchanged.
- **Validation:** Parent full source/test review approves. All 527 controls and installer static controls pass; root build has zero warnings/errors and all 2,184 tests pass without skips. Literal input, driver bytes and all five program goldens remain unchanged.
- **Boundary:** The mask describes text only. It cannot supply a diagnosis, typed field, completion or continuation. No raw line, identity or path is exported; first-fault, output-budget and report-size limits remain enforced.
- **Blockers:** Actual first-line wording family, flag readback, genuine PowerShell startup and installed Medium acceptance remain unresolved.
- **Next:** Inspect the bounded first-line facts from one exact-source Windows run before changing the query.

### 2026-10-22 - Publish the fixed literal arming-input control

- **Changed:** The sole Arm input uses literal-line `$$<` execution of the same fixed canonical filename. There are no script arguments, additional commands or mode selectors. Target/Side inputs, exact-source echo handling, all five program bytes and driver bytes are unchanged.
- **Validation:** Parent complete source/test review approves the finite input change. All 524 controls and installer static controls pass. Real BCL pipes cover filename spaces, pending-task identity, replay refusal, target continuation and EOF. Root build passes with zero warnings/errors; all 2,184 tests pass without skips.
- **Coverage:** Actual code-head `38d4503` report `12405604020` retains the same five assemblies: 14,739/17,137 lines (86.0%) and 15,193/24,668 branches (61.58%). The 85%/60% gates are unchanged.
- **Preserved:** SDK/query text, four-byte/flag-bit requirements, target/receipt/source/stopped proof, first-fault observations, output limits and owned cleanup remain mandatory.
- **Blockers:** Literal-mode Windows behaviour, typed readback, PowerShell exit 37 and installed Medium acceptance remain unproved.
- **Next:** Exercise the exact source on disposable hosted Windows and compare SDK/query completion and refusal facts.

### 2026-10-22 - Isolate literal-line execution without changing the arming program

- **Decision:** Continue within numeric-only reporting by testing the same pinned arming program with documented literal-line execution (`$$<`) instead of no-echo condensed execution (`$$>a<`). Literal mode also echoes commands; this coupled property is explicit, not a claimed query fix.
- **Boundary:** The sole input remains one fixed canonical filename, with no script arguments, selector, extra command or fallback. All five program files, selected signed SDK, typed query, stage/flag requirements and target authority remain unchanged. Existing exact-source echo handling must not broaden.
- **Evidence:** Run `39731835507` is now complete: both supported OS jobs and Tray pass; installer acceptance retains the original PowerShell failure and unknown query refusal. The published source remains `26af6f5` before this control.
- **Storage:** Retained reports total 31,834,246 bytes across 158 artifacts; two npm caches total 80,649,421 bytes. Three-day bounded-report retention remains in force; no raw diagnostic upload or infrastructure change.
- **Blockers:** Literal execution has not yet been exercised; flag readback, genuine PowerShell startup and installed Medium acceptance remain unresolved.
- **Next:** Review the independently authored finite input change and its unchanged-source controls, then exercise it on disposable Windows.

### 2026-10-22 - Keep the unclassified typed-query refusal blocking

- **Evidence:** Published `38d4503`, run `39731835507`, job `119668402994`, artifact `12404979359`, passes 520 controls and 500/501 native cases without skips. The original restricted PowerShell case still fails. SDK completion remains accepted, but the new query-display family is unknown (kind 1) and its exact-source region is unavailable (0).
- **Observed:** Native acknowledgement, Resume receipt and one held-target SDK invocation remain bound. No typed candidate, query completion, arm end or target continuation is accepted. The report establishes no type-information, memory, syntax or loader cause.
- **Decision:** Do not change the query, loosen accepted text, substitute exit 0 or promote the installer on this evidence. A proposed single redacted error-line exception needs an explicit decision; the owner was unavailable, so the numeric-only boundary remains in force.
- **Stocktake:** Remote main remains `e7758a3`; draft PR #30 contains the authentication/installer work but is unmerged. Issues #16, #18 and #28 remain open pending actual acceptance. Strict legacy repair #24 remains separately blocked by its namespace-authority contract.
- **Blockers:** The actual typed-query error, genuine PowerShell execution, whole-Medium harness/provider separation and installed lifecycle proof remain unresolved. macOS, Tray and both excluded compatibility jobs pass; the supported Windows job was still running at the last read.
- **Next:** Obtain an authorised, bounded diagnostic that identifies the actual query refusal before making another semantic change.

### 2026-10-22 - Prove SDK-stage return and classify the next typed-query refusal

- **Evidence:** Published `66c931d`, run `39729152304`, job `119659939540`, passes 513 controls and 500/501 native cases without skips. Both supported OS jobs and Tray pass. The raw absolute module token reaches accepted SDK completion (mask 1), after native acknowledgement, checked Resume and held-target stop proof.
- **Observed:** The following text triggers the existing unknown-printf refusal. Stage shape 4 contains `.echo`; query completion, typed candidate, arm end and target continuation remain absent. This establishes SDK-stage return, not successful flag readback or the PowerShell loader cause.
- **Changed:** A fresh query-observation owner adds frozen numeric display-family and exact-source-region facts at that first refusal, gated by accepted SDK completion and the unchanged bound arm frame. Commands, marker acceptance and existing refusal facts are unchanged.
- **Validation:** Parent source review approves. All 520 controls and installer static controls pass; five program goldens, driver bytes and earlier predicate hashes are unchanged. Root build passes with zero warnings/errors; all 2,184 tests pass without skips.
- **Blockers:** Actual typed-query diagnostic family, flag readback, genuine PowerShell exit 37 and installed Medium lifecycle remain unresolved.
- **Next:** Publish the unchanged-command observation and inspect its bounded Windows report.

### 2026-10-22 - Publish the raw absolute SDK-module encoding control

- **Changed:** The SDK invocation and its exact publication snapshots use the selected canonical module path without `.dll`, quotes or doubled backslashes. `.load`, query bytes, four metadata programs, held-target authority and first-refusal acceptance are unchanged.
- **Validation:** Parent source review approves the narrow control. `Test-NativeStartupTrace.ps1` passes 513/513 controls; `Test-Static.ps1` passes. The root build passes with zero warnings/errors, and `dotnet test --no-build --nologo` passes all 2,184 tests without skips.
- **Evidence:** Five new cases cover Windows-path serialization with spaces, module length, invalid paths, snapshot/command tampering and physical transport. Prior run `39727088056` is complete: both supported OS jobs and Tray pass; the installer retains the original PowerShell failure.
- **Blockers:** Raw module syntax on the actual SDK, SDK/query completion, typed flag readback and PowerShell startup remain unproved. No native or installed-Medium acceptance is claimed.
- **Next:** Run this exact source on disposable hosted Windows and inspect the bounded stage observations.

### 2026-10-22 - Identify a caret-bearing SDK-stage diagnostic and isolate its module token

- **Evidence:** Published `ca38f9b`, run `39727088056`, job `119653373758`, passes 508 controls and 500/501 native cases without skips. First stage subreason is record grammar, not the arm-frame guard. Shape 36 records a leading caret and `.echo` text; it is not a trimmed valid completion marker.
- **Meaning:** The refusal concerns diagnostic/source text rather than a valid stage marker failing identity checks. Exact error wording and the loader cause remain unknown; no SDK completion is accepted.
- **Decision:** The final narrow SDK-stage control compares the existing quoted/double-escaped module token with the raw canonical absolute module path without `.dll`. The qualified gflag operation, selected signed/pinned file, containment, query, no-echo input and target authority stay fixed.
- **Boundary:** Microsoft documents the full-path module form but gives no matching quoted-module example. Raw paths with spaces are a runtime control, not a claimed syntax fix; there is no basename/unqualified fallback or short-path alias.
- **Blockers:** Actual SDK-token acceptance, typed flag readback and PowerShell startup remain unresolved.
- **Next:** Review and publish the single SDK-token control, then inspect contained completion before changing anything else.

### 2026-10-22 - Classify the first stage refusal without changing marker acceptance

- **Changed:** Numeric first-stage subreason distinguishes grammar/source display, the existing frame guard, state mismatch, duplicate and ordering refusal. Closed shape bits identify prompt/prefix/echo and fixed diagnostic forms, plus exact record equality after observational HorizontalTrim.
- **Validation:** Parent review, all 508 controls and installer static controls pass. Eight new cases cover grammar versus scope, first-fault retention, positive-path preservation, post-budget sentinels, privacy and report maximum.
- **Preserved:** Driver bytes, all five program goldens and the arm-frame predicate are unchanged and hash-verified. No SDK/query, marker grammar, authority, input or continuation change.
- **Blockers:** Actual stage subreason and loader cause remain unknown.
- **Next:** Publish the observational subreason fields and inspect the first native refusal.

### 2026-10-22 - Stage isolation reaches a stricter stage-record refusal

- **Evidence:** Published `5d7864e`, run `39725233665`, job `119647500352`, passes 500 controls and 500/501 native cases without skips. Only the original PowerShell case fails.
- **Observed:** Neither scoped completion is accepted. The first refusal is stage grammar/scope/sequence (reason 15), with arm-begin phase and held target-create frame. Typed data and target continuation remain absent. A controller Kill request precedes the later `0xFFFFFFFF` exit.
- **Unknown:** This result does not distinguish an actual completion marker failing its frame checks from diagnostic text containing the fixed `.echo` marker.
- **Action:** The contained-stage owner is adding the first stage-specific subreason and closed line/frame facts. Accepted marker grammar, SDK/query text, source pins and all authority gates remain unchanged.
- **Blockers:** The refusing stage line, SDK/query completion and PowerShell loader cause remain unresolved.

### 2026-10-22 - Isolate SDK and query returns with scoped completion markers

- **Changed:** A fresh narrow owner separates the unchanged qualified SDK command and typed read into independent error-contained blocks. Fixed non-printf completion markers are emitted only after their respective operations return.
- **Validation:** Parent complete source/test review, all 500 controls and installer static controls pass. Eleven new cases cover missing SDK completion despite flag 2, retained SDK facts on query failure, unavailable fields, successful full transport, stage forgery/replay/scope/order, source tampering and budget/report bounds.
- **Preserved:** Other four program files, SDK/query text, selected signed source, no-echo invocation, one held-target SDK action, finite input roles, immutable snapshots and all original acceptance gates.
- **Grounding:** Primary `.echo` documentation confirms a semicolon terminates its unquoted text, so the fixed marker remains inside the containing block. No SDK syntax or token/console/permission change is inferred.
- **Blockers:** Actual contained-stage results and the loader cause remain unknown.
- **Next:** Publish stage isolation and inspect whether the SDK command, the typed query or both complete on Windows.

### 2026-10-22 - Separate SDK command completion from typed-query completion

- **Evidence:** Published `a5d34fc`, run `39722302391`, job `119638312385`, passes 489 controls, both supported OS jobs, Tray and 500/501 native cases without skips. No-echo invocation leaves the same first unknown printf refusal: arm-begin phase, no typed candidate/end, lexical mask zero. The control has not resolved the boundary.
- **Decision:** Isolate the unchanged qualified SDK setter and unchanged typed read in separate contained stages. Fixed non-printf completion markers will distinguish a returned SDK command from a completed query; missing SDK completion must refuse even if a flag value is present.
- **Grounding:** Microsoft's `.catch` reference says a command error skips the remaining commands inside that block and resumes after it. The current shared block obscures which component stopped. C++ documentation supports the existing sizeof/member syntax; no replacement expression is assumed.
- **Preserved:** One held-target SDK action, exact selected module/source pins, no-echo invocation, metadata-only automatic callbacks, typed flag bit 2 and all original acceptance gates.
- **Blockers:** SDK DSL failure, flag readback and PowerShell loader cause remain unknown. The preceding source owner has completed three cycles and is not reused.
- **Next:** Have a fresh narrowly scoped owner implement and validate SDK/query stage isolation before another semantic change.

### 2026-10-22 - Pin no-echo invocation while preserving condensed execution

- **Changed:** The single fixed arming input uses documented `$$>a<` with one quoted pinned filename and no arguments. All five program-file bytes/hashes, SDK grammar and target authority remain unchanged; there is no command/path/PID selector.
- **Validation:** Parent review, all 489 controls and installer static controls pass. Three new controls prove frozen program goldens, real-pipe quoting/spaces/replay/continuation and closed lexical facts with no post-budget analysis.
- **Observed shapes:** The first-refusal mask distinguishes prompt prefix, leading catch/if/printf, fixed syntax/export/resolver text and exact source after HorizontalWhitespaceCollapse. It remains observational; unknown lines still refuse.
- **Preserved:** Held-target stop, typed flag bit 2, historical ARMED, finite writer roles, exact output/report bounds, owned cleanup and original acceptance gates.
- **Blockers:** Actual no-echo execution, the refusing line's meaning and PowerShell loader cause remain unproved. The source owner has completed its third/final cycle; no fourth is assigned.
- **Next:** Publish the fixed no-echo control and inspect native readback without loosening the parser.

### 2026-10-22 - Isolate command echo without changing the arming program

- **Evidence:** Published `8145a02`, run `39719102387`, job `119628177960`, passes 486 controls and 500/501 native cases without skips. First refusal is unknown printf/source echo (reason 1), after arm begin and before any typed candidate or arm end. It is not classified as the fixed syntax shape. Target continuation remains blocked.
- **Termination:** The controller records a Kill request before CDB exits `0xFFFFFFFF`. That exit is observed after the bounded stop; it does not identify the original refusal.
- **Decision:** Microsoft's script-file reference documents that `$$>a<` suppresses command echo while retaining condensed command-block execution. The next control uses one fixed pinned filename and no script arguments. All five program files, SDK invocation and authority stay unchanged.
- **Correction:** An initially considered literal `$$<` control would also change execution semantics. It is superseded before publication by the narrower no-echo control.
- **Preserved:** Unknown printf and diagnostic output still refuse; no display text is promoted into SDK or target authority. Bounded lexical observations will distinguish any remaining refusal shape without exporting it.
- **Blockers:** Source review, actual no-echo behaviour and the PowerShell loader cause remain pending.

### 2026-10-22 - Capture the first scoped refusal without changing its decision

- **Changed:** First-refusal reason, program phase and active/held block kinds are latched as numeric facts. A fixed syntax-shaped printf class separates one display shape from other unknown echoes. Phase distinguishes seen markers and parsed DWORDs from actual flag-2 verification.
- **Validation:** Parent review, all 486 controls and installer static controls pass. Seven new cases cover first-fault retention, grammar/identity/sequence categories, post-budget freezing, privacy/report bounds and owned termination attempts.
- **Preserved:** SDK command text, qualified module, source pins, target identity/stop proof, continuation roles and original native acceptance gates are unchanged.
- **Exit observation:** A Kill request on the retained debugger handle is separate from its later exit value and is recorded even if the attempt fails. Neither fact supplies target-exit semantics.
- **Blockers:** Actual parser branch and PowerShell loader cause remain unknown.
- **Next:** Publish this observability-only change and inspect the first native refusal before making a semantic correction.

### 2026-10-22 - Reach the held-target arming program and stop on an unclassified parser refusal

- **Evidence:** Published `bfb8652`, run `39716029778`, job `119618229668`, passes 479 controls, both supported OS jobs, Tray and 500/501 native cases without skips. Only the original PowerShell case fails.
- **Observed:** Native acknowledgement and checked Resume are present. Exactly one controller arming invocation is written; the arming program emits its SDK-chain header. A scoped protocol fault then blocks target continuation. No flag readback or ARMED evidence is accepted, and no side continuation is issued.
- **Unknown:** The reduced report does not identify the first refusing parser branch. CDB exits `0xFFFFFFFF` after the bounded run; controller termination versus debugger-origin failure is not yet distinguished. Parent/Final fields are unbound, not proof of parent mutation.
- **Action:** The scoped revision owner is adding first-refusal reason/phase and fixed SDK grammar facts, plus a separate controller-termination observation. No SDK syntax, parser authority or continuation rule changes are approved from this result.
- **Blockers:** Actual flag readback, the parser refusal and PowerShell loader cause remain unresolved.

### 2026-10-22 - Approve controller-gated arming at the target's pre-user-mode stop

- **Changed:** ScopedCreateV1 has five pinned command files and metadata-only automatic callbacks. The controller alone may invoke the fixed SDK arming program after held-target/current/event/Ready/receipt/source/stopped-state verification. Actual typed flag bit 2 precedes historical ARMED and target continuation; other subjects receive only bounded fixed continuations.
- **Corrected:** Fresh revision removes report-time row parsing after output exhaustion and the unsupported `.chain` authority dependency. Immutable parser snapshots retain native facts; exact full-path-qualified SDK source, signed pinned file/hash and target-bound readback provide selection proof. Unknown chain formatting remains observational.
- **Validation:** Parent complete source/test review, all 479 controls, installer static controls, warning-free root build and 2,184 tests pass. A parser counter stays zero after exact exhaustion with populated Final; a genuine process-pipe fixture covers the full fixed-program/readback/continuation path.
- **Preserved:** No arbitrary command/PID input, automatic SDK write, context-selector fallback, privilege change or native factory change. Nonzero CDB exit remains blocking; advisory failure evidence and numeric exit values stay distinct. Original 501-case, MSI/authentication/exit-37 gates remain.
- **Blockers:** Actual hosted target arming, loader cause and installed Medium acceptance are unproved.
- **Next:** Publish the independently revised scoped trace and inspect actual flag readback before changing startup behaviour.

### 2026-10-22 - Hold scoped arming for budget-safe reporting and SDK selection proof

- **Review:** The unpublished ScopedCreateV1 candidate passes 472 controls and static controls, but parent review rejects two boundaries. Its report reparses Final after exact output exhaustion, and its SDK-binding gate requires a `.chain` row format unsupported by the measured SDK.
- **Evidence:** The existing budget cases have no populated Final, so they miss the report-time parse. Actual prior SDK output has a chain header and eight lines with no supported full-path row; synthetic positive readbacks supply that row.
- **Action:** A fresh source owner is replacing report-time parsing with previously validated immutable facts and proving the selected SDK action without treating unknown display text as loaded-module evidence. The rejected author is locked out of this revision.
- **Preserved:** Automatic callbacks remain metadata-only. Only the controller's verified held-target create stop can authorise the pinned arming program; typed flag readback, finite input roles, native gates and owned cleanup remain mandatory.
- **Blockers:** Scoped source approval and actual Windows arming remain pending. The published production head is still the safe metadata-only inventory.

### 2026-10-22 - Safe inventory identifies one additional subject and complete native cleanup

- **Evidence:** Published `dca3b7f`, run `39710128463`, job `119599220494`, passes 420 controls and 500/501 native cases without skips. The observation-only ledger binds three subjects: one target creation/exit, the helper exit and one other creation/exit. There are no repeats, malformed frames, PID mismatches, limit faults or partial callbacks.
- **Native facts:** The bound helper reports checked Resume, target exit `0xC0000142`, no native fault, complete cleanup and unchanged parent. Owned input closure and debugger/stream cleanup also complete. No initial-breakpoint callback is observed for the target or other subject. Inventory remains incomplete with controller error 13; the debugger exit value is not yet reported.
- **Decision:** A future target-scoped trace will use the actual target's create-process debug stop, which Microsoft documents as preceding user-mode execution. Automatic callbacks stay metadata-only. Only after held-target/current/event/source/receipt/stopped-state verification may the controller invoke a fixed pinned arming program; flag bit 2 must be read back before target continuation.
- **Scope:** Other subjects never receive SDK flag writes. Continuation commands remain fixed and bounded; no arbitrary command or PID interface is introduced. This deliberately changes diagnostic staging/input roles, not product/MSI/authentication or genuine exit-37 acceptance.
- **Blockers:** The other image, absent initial-breakpoint dispatch, debugger exit semantics and PowerShell loader cause are still unknown.
- **Next:** Implement and independently review controller-authorised arming at the held target's pre-user-mode create stop.

### 2026-10-22 - Approve metadata-only inventory after real stdin-lifetime correction

- **Changed:** Production now explicitly selects an observation-only debugger inventory. Four pinned metadata command files contain no SDK setter or PEB/memory read. A private 16-subject ledger distinguishes complete target/helper/other callbacks, repeats and malformed/mismatched frames; native Final facts retain source/row binding. Inventory cannot arm or accept a trace.
- **Corrected:** After rejection, a fresh owner fixes all three writer/stream disposal orders. Both close attempts, real failures, cancellation and pending-task bounds remain checked. Six actual BCL-pipe cases prove EOF, exact fixed input and genuine flush-fault refusal without fake disposal or peer rescue.
- **Validation:** Parent review, all 420 controls, installer static controls, warning-free root build and 2,184 tests pass. The prior 385 intents and all 29 inventory controls remain; report and output bounds are unchanged.
- **Preserved:** Fixed native subject/context, lower readiness permit, no arbitrary PID action, original 501-case gate and every product/MSI/authentication/exit-37 requirement.
- **Blockers:** Actual event classification, target-scoped trace production, the PowerShell loader cause and installed Medium acceptance remain unproved.
- **Next:** Publish the corrected metadata-only diagnostic and inspect the additional event relations without flag writes.

### 2026-10-22 - Catch a real stdin-lifetime failure before publishing the inventory

- **Found:** The observation-only candidate closes its input stream before disposing the owning StreamWriter. A real in-process anonymous-pipe reproduction throws ObjectDisposedException in that order; writer-first disposal reports no error. The candidate would treat this as a fatal closure fault before collecting events.
- **Review:** Parent review rejects the input-lifetime boundary despite 414 synthetic controls and static controls passing. The fake writer's disposal override does not close its real base and misses this behaviour. The same ordering occurs in the coupled ARMED and fallback-close paths.
- **Action:** A different source owner is correcting all three owned stdin paths and adding genuine BCL pipe regressions. The rejected author may not revise or advise this artifact. No inventory candidate has been published.
- **Preserved:** Metadata-only production, no flag setter or PEB reads, immutable source/row bindings, the 16-subject ledger, strict trace refusal and original acceptance gates remain required.
- **Coverage:** Published `8811ec8` passes both supported OS jobs and Tray; its five-assembly report records 14,739/17,137 lines and 15,205/24,668 branches, above 85%/60%.
- **Blockers:** Safe input closure, event classification, genuine PowerShell exit 37 and installed Medium acceptance remain pending.

### 2026-10-22 - Native Resume is proved; classify extra debugger events without flag writes

- **Evidence:** Published `8811ec8`, run `39705155167`, job `119583352201`, passes 385 controls and 500/501 native cases without skips. The actual v2 acknowledgement and checked Resumed receipt are both present. Ready/check mask 131 and later create/joint mask 3655 establish the intended lower-permit progression.
- **Observed:** The stream now contains two create and three exit callback blocks. Only the first create event binds before a protocol fault; no accepted initial breakpoint, flags or ARMED evidence follows. The parent remains unchanged; cleanup is incomplete. The extra event's identity and the PowerShell loader cause are unknown.
- **Safety decision:** The next run is explicitly observation-only: no SDK flag setter or PEB read, no possible accepted trace, and only bounded numeric target/helper/other event relations. The existing global IBP callback must not write flags for an unclassified additional process.
- **Scope:** This does not reject the independently approved native-readiness permit. It requires fresh target-scoped producer work before flag-setting traces resume; the original producer author is locked out of that revision.
- **Blockers:** Original PowerShell exit 37, target-scoped trace production and installed Medium acceptance remain pending.
- **Next:** Inventory the owned debugger's event stream read-only, then bind any future setter to the actual held target.

### 2026-10-22 - Implement the native-readiness permit without changing trace authority

- **Changed:** A fresh owner extracts native readiness from actual CPR binding. Lower permission may precede CPR; already-observed wrong callbacks, stale Final/exit, faults, missing source/registration proof and replay still refuse. Stronger initial-breakpoint/ARMED proof continues to require matching post-Resume CPR.
- **Validation:** Parent source review, all 385 controls, installer static controls, warning-free root build and 2,184 tests pass. The prior safety and transport intents remain, with missing-CPR assertions explicitly migrated to trace authority and twelve new staging/boundary cases.
- **Independent review:** A fresh C# reviewer approves the unchanged helper composition: actual retained native handles, fixed source, verified context before Ready, identity re-read, ResumeThread previous count exactly one and receipt before Wait. Ready rows depend on that trusted producer; they do not independently attest a token or debugger identity.
- **Preserved:** Exact v2 wire, callback bytes/modes, one fixed debugger continuation, output/report bounds, owned cleanup and all original product/MSI/authentication/exit-37 gates.
- **Blockers:** Actual Windows progression and the PowerShell loader cause remain unproved; this is source approval only.
- **Next:** Publish the coherent policy change and inspect whether native Resume now reaches matching CPR and initial-breakpoint evidence.

### 2026-10-22 - Separate native creation readiness from debugger event proof

- **Evidence:** Published `1006df2`, run `39702721604`, job `119575510730`, passes 373 controls and 500/501 native cases with no skips. Exact literal CPR readback verifies, but the 3/199/95 phase transition, fast consumer classes and missing acknowledgement are unchanged. The script-mode control has not resolved the boundary.
- **Decision:** Within the existing authority to execute this one fixed inert diagnostic, native Ready may grant its one checked Resume before a debugger-create callback is observed. Ready must still bind the held child identity, image, Medium context, debug port, helper, pinned sources and verified event registration, with no current Final, exit or open callback. An already-observed mismatched create callback refuses.
- **Trace boundary:** Actual matching create-event proof remains mandatory before initial-breakpoint flag/stopped-state verification and trace arming. Native readiness is not accepted trace evidence. No original exit-37, installed lifecycle, MSI or owner-authentication gate changes.
- **Rationale:** Pre-Resume callback delivery has not been demonstrated on this SDK. Removing that dependency from lower synthetic execution permission tests the staging assumption without inventing a Windows event-order guarantee or accepting a stale event.
- **Blockers:** The loader cause remains unknown. The revised permission policy needs fresh source implementation, explicit regression updates and independent review before publication.
- **Next:** Implement the bounded native-readiness permit while retaining full post-Resume event and pre-DllMain trace proof.

### 2026-10-22 - Pin the CPR-only literal-script experiment

- **Changed:** A fresh owner changes only the create callback invocation/readback from condensed `$$><` to literal `$$<`. EPR/IBP invocation and all callback-file bytes remain unchanged; no debugger continuation or permission is added.
- **Validation:** Parent review, all 373 controls, installer static controls, warning-free root build and 2,184 tests pass. Four new regressions bind the changed root hash, unchanged callback hashes, exact readback and tamper refusal. Every prior intent remains.
- **Boundary:** This tests the documented script execution distinction; it does not establish native event-delivery or PowerShell loader causality. Current Final/exit observations still override historical eligibility.
- **Blockers:** Actual CREATED progress, pre-DllMain arming and genuine PowerShell execution remain unproved.
- **Next:** Publish the single-factor control and inspect actual phase ordering before proceeding.

### 2026-10-22 - Windows process pipes progress; the stale event is consumed quickly

- **Evidence:** Published `ad65fc1`, run `39700979150`, job `119569855399`, passes all 369 controls, including both actual Windows process-pipe cases. Native results remain 500/501 with only the original restricted PowerShell case failing and no skips.
- **Observed:** Both read-initiation maxima are at most 10 ms. Create-read age, parsing, receive delay and first-check delay are at most 100 ms; control-file work is at most 10 ms. Ready/create/check masks remain 3/199/95. The measured consumer work does not account for the acknowledgement timeout.
- **Decision:** A fresh, narrower source control will invoke only the create callback as literal script lines rather than a condensed command block. Microsoft documents that distinction. The callback's fixed bytes and final debugger `g`, all other callbacks and every permission/arming gate remain unchanged.
- **Boundary:** This is an execution-mode experiment, not proof of a CDB buffering mechanism or a PowerShell loader cause.
- **Blockers:** No CREATED acknowledgement, native Resume or child initial breakpoint is observed; the diagnostic still reports 1460/cleanup 5.
- **Next:** Review and run the CPR-only literal-script control on disposable hosted Windows.

### 2026-10-22 - Exercise actual process pipes and classify the remaining collection delay

- **Changed:** Two finite, source-pinned PowerShell producers exercise the production collector over genuine process handles, including non-owning FileStream adapters on macOS. Both stdout/stderr and live control rows progress before producer exit. Closed monotonic classes distinguish read initiation, read age, parsing, batching, control reads and the first publication check.
- **Validation:** Parent source review, all 369 controls and installer static controls pass. The prior 361 intents remain, including rejection when historical eligibility is overtaken by Final/exit. No blocking defect is reproduced locally.
- **Boundary:** macOS pipe progress is not Windows/CDB proof; read age does not establish producer emission time. No admission, timeout, fixed continuation, budget or cleanup requirement changes.
- **Blockers:** Actual Windows collection delay, genuine restricted PowerShell exit 37 and installed Medium acceptance remain unresolved. The scoped source owner has finished its three cycles; no fourth is assigned.
- **Next:** Inspect actual Windows duration classes and process-pipe controls before choosing a narrower correction.

### 2026-10-22 - Ready is observed live; CREATED becomes stale before the driver check

- **Evidence:** Published `cba34d3`, run `39698114579`, job `119560393829`, passes 361 pure controls and 500/501 native cases with no skips. Only the original PowerShell exit-37 case fails.
- **Observed:** The first 173-byte Ready row is read before the complete create callback. At callback closure, the retained parser state satisfies CREATED eligibility (mask 199); at the first actual driver check, fresh Final and target-exit observations make it ineligible (mask 95). No acknowledgement or Resume occurs.
- **Meaning:** Ready is not merely hidden until writer disposal. The remaining discontinuity lies between event consumption and the live publication check; the evidence does not identify CDB buffering, a blocking read initiation or a PowerShell loader cause.
- **Action:** The source owner is adding a controlled actual-process pipe regression and bounded monotonic classifications. Historical eligibility must not override current Final/exit observations.
- **Blockers:** The diagnostic still times out with error 1460 and cleanup fault 5. Genuine PowerShell execution and installed Medium acceptance remain unproved.

### 2026-10-22 - Distinguish live CREATED eligibility from late combined observations

- **Evidence:** Published `0aec2bc`, run `39695158774`, job `119550629480`, passes both supported OS jobs, Tray, all 335 pure controls and 500/501 native cases without skips. Only the original PowerShell case fails. The diagnostic still records no CREATED acknowledgement, error 1460 and cleanup fault 5; the raw-reader correction has not established that timeout's cause.
- **Changed:** First-observed numeric snapshots separate Ready, create callback closure, their first joint candidate and the driver's actual publication check. Bounded control-file read samples distinguish missing/empty output from rows first encountered with Final. No predicate, permission, continuation or timeout changes.
- **Validation:** Parent review, all 361 pure controls, installer static controls, warning-free root build and 2,184 tests pass. The report remains within 4,096 bytes; all prior intents and exact output exhaustion remain covered.
- **Source facts:** Independent C# review confirms Ready is live-flushed before the 45-second acknowledgement wait, after native creation and identity checks. The controller's read/write sharing is compatible with the held writer. No C# until-close buffering defect or CDB-specific producer-buffering fact is established.
- **Coverage:** Current published five-assembly evidence is 14,739/17,137 lines and 15,213/24,668 branches, above the unchanged 85%/60% gates.
- **Blockers:** Actual observation ordering, genuine PowerShell exit 37 and installed Medium acceptance remain pending; PR #30 remains draft and unmerged.
- **Next:** Publish the closed ordering observations and use their actual first-check facts to select the next correction.

### 2026-10-22 - Read available debugger bytes without waiting for a character block

- **Changed:** A fresh owner replaces buffered character reads with retained asynchronous raw-pipe reads and strict incremental UTF-8 decoding. The controller's CREATED, checked Resume receipt, actual initial breakpoint, stopped prompt and one fixed continuation remain unchanged.
- **Validation:** Parent source review and all 335 controls pass, preserving the prior 312 intents and adding 23 cases, including 20 real anonymous-pipe cases. Open-writer tests exercise actual production progress at 1,024/2,048-byte boundaries, split markers/prompt, continuation-dependent output, UTF-8, exact budget and cleanup. Installer static controls pass.
- **Evidence:** The independently reproduced buffered-reader stall motivates this correction; no Windows failure cause is inferred from portable tests.
- **Blockers:** Actual Windows CREATED progress, pre-DllMain arming, genuine PowerShell exit 37 and installed Medium acceptance remain pending.
- **Next:** Publish the pipe correction and inspect the fixed owned Windows diagnostic.

### 2026-10-22 - Hosted controls pass; real pipe reads expose a progress hazard

- **Evidence:** Published `bab25c3`, run `39692007108`, job `119540197376`, executes 501 cases: 500 pass, only the original restricted PowerShell case fails, and none skip. All 58 startup controls and four real adapters pass. Both supported OS jobs and Tray pass.
- **Observed:** The diagnostic verifies create/exit filters and the initial-breakpoint registration, but records no CREATED acknowledgement, Resumed receipt or child initial callback. It times out with error 1460 and cleanup fault 5; the parent remains unchanged. No loader cause is established.
- **Found:** An independent in-process anonymous-pipe probe shows the current 4,096-character `StreamReader.ReadAsync` can retain 1,024 or 2,048 available characters until EOF. This can obstruct control-marker and non-newline-prompt progress. Whether it caused the hosted timeout remains to be demonstrated.
- **Action:** Parent review rejects that reader boundary. A fresh source owner is revising it and adding real-pipe regressions; the original author is locked out of this revision.
- **Coverage:** The current five-assembly report records 14,735/17,137 lines and 15,110/24,668 branches, above the unchanged 85%/60% gates.
- **Blockers:** Genuine PowerShell exit 37, reliable pre-DllMain evidence and the installed Medium lifecycle remain pending. PR #30 stays draft; remote main remains `e7758a3`.
- **Next:** Prove available-byte progress through the production reader before repeating the owned Windows diagnostic.

### 2026-10-22 - Separate synthetic diagnostic execution from pre-DllMain trace arming

- **Evidence:** Published `f5979cc`, run `39676486281`, still produces no typed loader-flag value at process creation. Both supported OS jobs and Tray pass; native acceptance remains 460/461 with only the original PowerShell exit-37 case failing.
- **Decision:** CREATED permits one owned synthetic diagnostic's checked Win32 Resume. Accepted trace evidence requires the actual matching child initial breakpoint, stopped state, typed flag bit 2 and Resumed receipt before one fixed debugger continuation. Child `ibp` dispatch is discovered on the actual SDK, not assumed for every version.
- **Changed:** The v2 helper revalidates retained identity and emits Ready/Resumed/Final rows. Four pinned debugger command files and the controller keep CREATED, historical ARMED and the single `g\n` write distinct. No product, MSI or owner-authentication gate is relaxed.
- **Validation:** Fresh independent C# review approves source and original-intent preservation. Root build and 2,184 tests, 1,277 portable Setup tests, warning-free Windows cross-build, all 312 pure PowerShell controls and installer static controls pass. Discovery is 501: unchanged baseline 439, startup 58 and real adapter 4; CI requires every group without skips.
- **Blockers:** Actual pre-DllMain stop/flag readback and the PowerShell failure cause remain unproved. Whole-Medium installed acceptance is still pending; PR #30 remains draft and unmerged.
- **Next:** Publish the coherent helper/controller pair and inspect actual Windows callback, continuation and cleanup evidence before changing the failing startup path.

### 2026-10-22 - Use the debugger's native PEB type for the fixed field read

- **Evidence:** Published `12c592e`, run `39674762741`, job `119481586348`, contains query errors and closes create/exit callbacks. No typed value is produced, so the actual target remains unarmed; this is unavailable evidence, not measured zero.
- **Changed:** A fresh source owner replaces only the explicit module cast with documented native `PEB*` pseudoregister field access. The four-byte guard, `.catch`, identity framing, output limits and flag-2-before-resume remain unchanged.
- **Validation:** Parent source/primary-documentation review and all 212 pure controls pass. The pinned create callback is 292 bytes; no memory write or new resume authority is added.
- **Blockers:** Actual field access at creation and the previous query's failure cause remain unproved.
- **Next:** Publish this single expression control and inspect actual flag availability before choosing any protocol change.

### 2026-10-22 - Contain typed-query errors without acknowledging missing flags

- **Evidence:** Published `569577c`, run `39673160539`, job `119476024112`, passes 210 pure controls and 460/461 native cases. The target create event binds, but the typed query produces no value or closing marker; no unavailable result is treated as zero.
- **Changed:** Only the typed query is wrapped in documented debugger `.catch`. The fixed closing marker and debugger continuation stay outside; actual Win32 suspension and flag-2 acknowledgement remain unchanged.
- **Validation:** Parent review, all 212 pure controls and installer static controls pass. The pinned create script is 322 bytes; negative modelling preserves unavailable flags, no arming and explicit failure.
- **Blockers:** Native query-error containment and symbol/type/memory availability remain unproved. A blocking symbol-server operation is still controlled by the existing outer deadline and owned cleanup.
- **Next:** Publish and distinguish a contained unavailable query from actual target-bound flag readback on Windows.

### 2026-10-22 - Query the typed child flag instead of relying on extension text

- **Evidence:** Published `c1fcd22`, run `39669489732`, job `119463254848`, passes 190 pure controls and 460/461 native cases. The quoted-load correction clears every known library-load error; actual target-bound flag text still does not appear.
- **Changed:** A fresh source owner adds a read-only public-symbol field query guarded by a four-byte width check. Version-2 create blocks require that typed PID/DWORD record; extension text cannot substitute, and no offset, whole-PEB/environment dump or extra write is used.
- **Preserved:** Held child/helper/birth/event identity, pre-resume flag bit 2, historical arming, source pins, budget and cleanup requirements remain mandatory. Missing/invalid field reads cannot arm.
- **Validation:** Parent producer/parser/arming review and all 210 pure controls pass, including the exact pinned 311-byte script and adverse phase/framing/identity cases.
- **Blockers:** Symbol-resolved field access at the actual creation event has not run on Windows; no PowerShell initialisation cause is inferred.
- **Next:** Publish and require the actual target-bound DWORD value before acknowledging resume.

### 2026-10-22 - Quoted extension-load encoding follows the documented parser contract

- **Changed:** A fresh source owner encodes literal backslashes in the existing quoted `.load` argument. The canonical selected SDK file and all read/signature/source pins remain unchanged; callbacks, debugger options and flag-2-before-resume remain strict.
- **Grounding:** Microsoft maintainer guidance in [microsoft/WinDbg-Feedback#26](https://github.com/microsoft/WinDbg-Feedback/issues/26#issuecomment-512359949), directly fetched and verified, requires escaping backslashes in quoted load paths. This differs from the unrelated executable-path report previously rejected as evidence.
- **Validation:** Parent review, 190 pure controls and installer static controls pass. New controls verify every SDK layout, literal numeric/hex-looking directory components, exact publication hashes and tamper refusal.
- **Blockers:** Actual extension loading and loader-flag readback are still unproved; no DLL-initialisation cause is inferred.
- **Next:** Publish this single runtime encoding change and inspect actual target-bound loader-flag execution.

### 2026-10-22 - SDK lookup remains unknown after exact source comparisons

- **Evidence:** Published `8541eeb`, run `39664685599`, passes both supported OS jobs and Tray. Native execution remains 460/461 with zero skips and only the original PowerShell assertion failing. The SDK reports error 2, no known source/name relation, and a chain header without a supported selected row.
- **Changed:** Five bounded numeric facts distinguish fixed module stems/suffixes, untrusted lexical source-display relationships and chain-format support. No path resolution, load-command correction or new authority is introduced.
- **Validation:** Parent review and all 186 pure controls pass, including unknown forms, source-independent classification, output exhaustion, report limits and non-arming cases.
- **Blockers:** Missing-library identity, actual selected-extension load and genuine PowerShell execution remain unresolved.
- **Next:** Read these lexical/format facts once on Windows and use the result to select a concrete command-binding correction.

### 2026-10-22 - Actual Windows proves the four strict adapter cases

- **Evidence:** Published `e79d7ff`, run `39663397530`, job `119441985422`, executes 461 native cases: 460 pass, one fails, zero skip. Only the original restricted PowerShell assertion fails; all four owned-child adapter cases pass after the startup correction.
- **Closed boundary:** Real hosted-High process identity, clean/noisy pipe handling, checked deadline reaping, closed streams and actual drain settlement now have target-host evidence. This closes the Setup transport work, not installed Medium or MSI acceptance.
- **Changed:** The next diagnostic adds only root `.chain` readback and closed selected-module/missing-library/display observations. It does not alter extension loading, source trust or flag-2-before-resume.
- **Validation:** Parent review and all 178 pure controls pass. No raw chain rows or library paths enter reports.
- **Blockers:** Genuine PowerShell execution and actual extension-loading identity remain unresolved; cross-platform current-head jobs are still finishing.
- **Next:** Publish and read the selected extension's actual chain relation before changing command binding.

### 2026-10-22 - Loaded-image readiness preserves proof and owns failed startup

- **Changed:** The real owned-start path waits only for the documented transient null main module, using monotonic bounded observations of the same process. Loaded-module/PID/time/path/hash proof remains mandatory afterward.
- **Review closure:** Independent C# review rejects the first candidate's non-stderr cleanup hole. Fresh owner `5470b095` supplies capture-independent bounded kill/reap/dispose; reviewer `d822adb9` approves. Rejected author `4813d703` does not revise or advise.
- **Validation:** All 1,277 portable Setup cases pass, including 24 actual-gate readiness controls and 36 actual-bind failure/cleanup controls. Root/Windows builds have zero warnings/errors; 2,184 Core cases pass.
- **Blockers:** No Mac execution of Windows native bodies occurred. All four real adapter cases and the original PowerShell assertion still require hosted proof.
- **Next:** Publish and rerun the complete 461-case Windows gate without changing its original or added case requirements.

### 2026-10-22 - Parser fixtures separate semantic checks from scheduler deadlines

- **Changed:** A fresh C# owner gives each default fake-child invocation its own manual clock. Production and real-adapter budgets are unchanged; dedicated timeout/cancellation tests still expire explicitly and all outer watchdogs remain.
- **Validation:** Fresh C# review approves. The added real-consumer regression proves one invocation's expiry cannot change another's strict summary rejection. All current portable cases pass and root build/test remain clean.
- **Uncertainty:** The macOS log lacks the original assertion detail. This removes the identified wall-clock scheduling hazard without attributing that historical failure or weakening its expected result.
- **Next:** Publish the deterministic fixture alongside the independently reviewed startup-binding correction and require fresh macOS evidence.

### 2026-10-22 - Bound library-load evidence precedes any extension-loading change

- **Evidence:** Published `5de8751`, run `39657808901`, passes 156 pure controls. Native execution is 457 passed/four failed of 461: the original PowerShell assertion plus three startup-image binding failures. The SDK reports a LoadLibrary failure, with no accepted flag readback.
- **Changed:** The advisory report binds its first numeric Win32 error to a closed selected-extension path shape. Expected identity comes from verified publication; raw paths and messages remain private.
- **Validation:** Parent review and all 168 pure controls pass. Published coverage is 86.0% line / 61.51% branch with unchanged five-assembly scope.
- **Review boundary:** The proposed null-only image-readiness gate passes 24 new portable cases, but independent C# review rejects its non-stderr constructor-failure cleanup. A fresh revision owner is assigned; it remains uncommitted. A separate owner investigates the macOS portable report-test failure.
- **Blockers:** Actual extension error/path shape, real adapter acceptance and genuine PowerShell execution remain unresolved.
- **Next:** Publish the bounded library diagnostics while correcting startup cleanup and deterministic portable-test timing.

### 2026-10-22 - Real callbacks bind the target but loader-flag arming remains absent

- **Evidence:** Published `84357dc`, run `39654782472`, job `119412052673`, executes create and exit callback files. Current/event PIDs match the held target, helper final state is present and parent state is unchanged. Flag readback stays absent; arming and complete cleanup remain false.
- **Changed:** Closed numeric SDK-message bits classify library/export/PEB/symbol failures from bounded stdout/stderr. They add no identity, flag or acceptance authority; unknown message shapes remain unavailable.
- **Validation:** Parent review, all 156 pure controls and full installer static controls pass. Pins, commands, single-resume requirements, output budgets and genuine PowerShell assertion are unchanged.
- **Blockers:** The actual SDK refusal category and PowerShell initialisation cause remain unproved.
- **Next:** Publish and read the specific SDK-command category before changing extension loading or debugger event sequencing.

### 2026-10-22 - Four real Windows adapter cases join the unchanged native baseline

- **Changed:** A fresh C# owner adds controlled real-process completion/noise/deadline cases through the actual owned-child adapter. Pinned constant scripts, source image/PID identity, closed streams, settled drains and parent preservation are required; only the owned child can be terminated.
- **Validation:** Fresh C# review approves. Cross-build is warning-free after the author's analyzer-compatible equality correction; discovery is exactly 461: original 457 plus four adapter cases. All 1,216 portable Setup cases and installer static controls pass.
- **Preserved:** CI separately requires all original cases and all four new cases, plus the existing 18 startup/439 split. The original PowerShell exit-37 failure remains blocking.
- **Blockers:** No Windows native body ran on the Mac. Actual hosted execution and installed Medium acceptance remain outstanding.
- **Next:** Publish the exact-count gate and require the four actual adapter cases to pass on disposable public Windows.

### 2026-10-22 - MCP drain setup no longer depends on discarded HTTP approval

- **Evidence:** Supported Windows run `39652529830` fails the `/mcp` drain case during initial owner approval. Source shows that credential was immediately discarded; the actual drain assertion had not started.
- **Changed:** A fresh F# owner makes the real request helper issue HTTP authority only for the protected API route. MCP keeps its independent policy, readiness and admission. Production authorization, deadlines and drain behavior are unchanged.
- **Validation:** Fresh F# source review approves; 12 host cases and all 2,184 Core cases pass. Root build has zero warnings/errors. Two added cases verify one API issuance and zero MCP issuance through the shared helper.
- **Blockers:** The underlying pipe timeout remains unattributed; API tests still require genuine owner approval and Windows current-head validation remains pending.
- **Next:** Publish this fixture correction with the next bounded native diagnostic and require the supported Windows suite to pass.

### 2026-10-22 - Measured callback path mutation selects literal backslash escaping

- **Evidence:** Published `21f3ad2`, run `39652529830`, job `119404282439`, passes 143 pure controls and 456/457 native cases. Both callbacks report transform 9: quoted source with all four path separators removed. No callback event, loader flag or arming is observed.
- **Changed:** A fresh source owner escapes literal backslashes only in the two validated `sxe -c` callback paths. Logical source/readback identity, extension loading, pins, child context and resume requirements stay unchanged.
- **Validation:** Parent review, all 147 pure controls and installer static controls pass. New controls use real generation/publication with Windows paths, spaces and adversarial source mutations.
- **Blockers:** This corrects a measured command-serialization defect; actual callback execution and the PowerShell DLL-initialisation cause are not yet established.
- **Next:** Publish and inspect actual create-event/PID/loader-flag binding before selecting any child-startup correction.

### 2026-10-22 - Component output and owned pipe cleanup pass fresh source review

- **Changed:** The real component consumer reads both bounded pipes, requires the complete report/diagnostic/exit contract and independently checks owned-child cleanup. It retains actual drain operations and explicitly closes readers; unsettled cleanup fails with the original fault preserved.
- **Review closure:** Fresh C# owner `638970f0` replaces the rejected cleanup/status implementation. Independent reviewer `b59dd5c4` approves both prior P2 corrections; original author `ad694006` remains locked out of revision and advice.
- **Validation:** All 1,216 portable Setup cases pass, including 129 source-linked real-consumer cases. Both Windows-targeted projects and root build pass with zero warnings/errors; 2,182 Core cases pass.
- **Blockers:** Source approval and portable fakes do not establish real Windows pipe execution. The installed Medium lifecycle and original PowerShell exit-37 gate remain pending.
- **Next:** Publish this source-approved component correction and require real Windows adapter execution alongside the grounded debugger-path correction.

### 2026-10-22 - Compare transformed callback readback without admitting it

- **Evidence:** Published `3b1e8cc`, run `39647900245`, passes both supported OS jobs, Tray and 131 pure controls. Native execution remains 456/457 with zero skips. Both callback payloads are non-empty but differ from source; child markers, events and arming remain absent.
- **Changed:** Six bounded numeric derivations compare fixed command transformations and lengths. No raw payload is published and no transformation satisfies arming.
- **Review closure:** Parent review rejects the first case-insensitive C-escape hypothesis. A fresh owner supplies ordinal matching and mixed-case Windows-path regressions; the rejected author does not revise or advise.
- **Validation:** Parent review, all 143 pure controls and full installer static controls pass. Generated commands, source pins, token context, budgets and the original exit-37 requirement are unchanged.
- **Blockers:** Stored-command mutation versus debugger display escaping is not yet identified; the component transport remains under independent revision.
- **Next:** Publish the numeric derivation and select a correction only from the actual measured transformation.

### 2026-10-22 - Setup status rejects impossible Busy and reboot combinations

- **Changed:** The standalone reader rejects reboot reports carrying MSI 1618/1605 and Busy exits with contradictory non-null MSI results. Pre-call refusals, real MSI 1618 Busy and verification-triggered reboots with MSI 0/1603 remain valid.
- **Validation:** Parent review, full installer static controls and 56 added operation/readiness negatives pass. Root build is warning-free and 2,182 Core cases pass.
- **Review boundary:** The separate component candidate passes 1,179 portable cases, including 92 real-consumer cases, and both Windows-targeted builds. Fresh C# review nevertheless rejects incomplete pending-read cleanup and contradictory status admission. A fresh revision owner is assigned; that candidate stays uncommitted.
- **Blockers:** Genuine PowerShell startup and Medium installed acceptance still block PR #30.
- **Next:** Publish the approved standalone correction and bounded debugger derivation while the fresh C# owner fixes the rejected component candidate.

### 2026-10-22 - Standalone Setup rejects incomplete or contradictory output frames

- **Changed:** A fresh PowerShell owner tightens the real standalone reader against complete writer frames. All stdout/stderr records are consumed; duplicate fields/reports, noise, duplicate summaries and reordered records fail. Optional refusal evidence binds to a valid failure report, exit and permitted code.
- **Validation:** Parent review and full installer static controls pass, including the actual synthetic child-pipe cases, valid outcome layouts, native-preflight/MSI-context parsers and final consumers. All 131 CDB pure controls also pass.
- **Blockers:** The separate C# component consumer is still being corrected. Genuine restricted PowerShell execution and actual Medium installed acceptance remain unproved.
- **Next:** Apply the C# author's component patch, verify the real source-linked portable path and obtain fresh C# review before publishing the consumer corrections.

### 2026-10-22 - Registered debugger filters still lack matching callback readback

- **Evidence:** Published `c5955ee`, run `39645449224`, passes both supported OS jobs and Tray. Native execution passes 456/457 with zero skips; both event filters report break status but no exact immediate callback readback. The derived report still has no child event or verified arming.
- **Changed:** Closed numeric classifications distinguish the next callback-listing line without exposing it or changing debugger commands, identity binding or resume authority.
- **Validation:** Parent review, all 131 pure controls, warning-free isolated root build and 2,182 Core cases pass. Actual published coverage remains 86.0% line / 61.48% branch in the original five-assembly scope.
- **Decision:** Tighten both real Setup acceptance consumers against the writer's complete finite output: reject unconsumed/duplicate records and bind schema, operation, outcome, exit and optional failure evidence. This implements the owner's standing approval of recommendations; fresh C# and PowerShell owners handle the separate consumers.
- **Blockers:** The callback's absence versus an unfamiliar listing format remains unresolved. Genuine PowerShell execution and the Medium installed lifecycle still block merge.
- **Next:** Read the actual closed callback-line classification and finish the independent Setup-consumer corrections.

### 2026-10-22 - Observe CDB filter registration before changing event control

- **Evidence:** Published `70e73e3`, run `39642355661`, job `119368876694`, passes 116 pure controls and 456/457 native cases. Its bounded report has the initial helper marker and target row, but zero child callback/event/flag observations. Explicit event breaks did not establish arming.
- **Changed:** Two numeric observations classify the existing filter listing and exact pinned callback readback. Generated debugger commands, child context, arming, budgets and cleanup remain unchanged.
- **Validation:** Parent review, 124 pure controls, installer static controls, warning-free isolated IL-only root build and 2,182 Core cases pass.
- **Blockers:** Genuine PowerShell exit 37, loader cause and complete installed acceptance remain unresolved; PR #30 stays draft and unmerged.
- **Next:** Publish this readback and distinguish missing callback registration from a registered callback that never executes.

### 2026-10-22 - Owned debugger callbacks request explicit event breaks

- **Evidence:** Published `3c89311` run `39639654798`, job `119359542076`, passes 110 pure controls and 456/457 native cases. Pinned callback sources remove malformed-marker noise, but only the initial helper marker is observed; child marker/event/flag counts remain zero. No arming, initialization or complete cleanup is inferred.
- **Changed:** Create/exit callbacks request explicit break status, retain their final automatic continuation and add a read-only filter listing. No other command, initial-break, source, identity, token, budget or cleanup behavior changes.
- **Validation:** Parent direct review and all 116 pure controls pass, including exact registration source/hash, altered status and readback-without-arming negatives. Isolated IL-only root build, 2,182 Core and static controls pass. Native discovery remains 457.
- **Blockers:** This is one owned-debugger control-factor experiment, not a proven cause or an acceptance substitute.
- **Next:** Publish and require actual callback markers/current-event binding under explicit break control before selecting another debugger change.

### 2026-10-22 - Fixed event files remove nested CDB callback quoting

- **Evidence:** Published `722b636` run `39635220944`, job `119343742443`, passes 88 pure controls and 456/457 native cases. CDB observes one valid initial helper marker, five malformed marker lines and four echoes; child callback/event/flag counts are zero. Target resume remains withheld and cleanup fault 5 remains visible.
- **Changed:** Each fixed callback now invokes an owned script file containing plain commands. Root/create/exit files are create-new/read-only and all length/hash verified under retained read pins before CDB launch. The fixed command generator uses the same selected SDK extension; no arbitrary file or command selector is introduced.
- **Preserved:** PID/creation/event/flag binding, historical arming, budgets, timeout, cleanup, token authority and the original PowerShell exit-37 requirement remain strict. This changes command transport, not acceptance or a claimed initialization cause.
- **Validation:** Parent direct review, 110 pure controls and installer static controls pass. Local root build first encounters a Roslyn MissingMethodException; isolated serial IL-only build passes warning-free, then 2,182 Core cases pass. No source workaround was made.
- **Next:** Publish and require actual create/exit markers and loader-flag readback from the pinned event files before authorizing the child's resume.

### 2026-10-22 - CDB runs; strict child-arming failure becomes distinguishable

- **Evidence:** Published `578af37` run `39631051470`, job `119329001632`, passes 62 pure controls and 456/457 native cases. SDK extension/source trust passes and CDB runs. The actual helper matches, but target/arming do not; after its deadline the report retains status 5, error 13, cleanup fault 5, unchanged parent and incomplete cleanup. No startup cause is claimed.
- **Changed:** Bounded numeric observations distinguish real marker/event/flag readback counts, malformed lines and current/event-target snapshots without printing raw debugger data. They are advisory only and cannot satisfy the strict binding or loader-failure predicates.
- **Validation:** Parent direct review and all 88 pure controls pass, including privacy, saturation, unknowns, byte-budget cutoff and report-size bounds. Isolated root build, 2,182 Core and installer static controls pass. No driver, CDB command, arming, timeout or cleanup condition is changed.
- **Blockers:** The child correctly does not resume without a verified arming acknowledgement. The actual parser/context boundary remains unknown, and incomplete cleanup stays visible.
- **Next:** Publish the structural observations and distinguish command execution, current-process mismatch and unrecognized event/readback grammar before changing the debugger protocol.

### 2026-10-22 - SDK extension location is bound through trust checks to CDB

- **Evidence:** Published `dd23a3d` run `39628746029`, job `119320895584`, passes 49 pure controls and 456/457 native cases. Trace point 6/tool role 6 identifies extension-file pinning, before signature validation; helper and CDB execution remain unreached.
- **Changed:** The fixed SDK directory supplies exactly one `exts.dll` from closed `winxp`, `winext` or root layouts. The same selected file must pass the original pin and valid Microsoft signature checks and becomes the explicit CDB `.load` path. Missing/ambiguous/noncandidate locations refuse.
- **Validation:** Parent direct review and all 62 pure controls pass, including actual resolver-to-pin/signature/command binding and failures. Isolated root build, 2,182 Core and installer static controls pass. No trust relaxation or native-discovery count change occurs.
- **Blockers:** The installed SDK layout and actual loader trace are not yet established.
- **Next:** Publish the layout correction and require the same selected extension to reach CDB before attributing any startup cause.

### 2026-10-22 - Native debugger preflight exposes its exact closed refusal point

- **Evidence:** Published `b4e15a0` run `39626230554`, job `119312097915`, passes 37 pure controls and 456/457 native cases, with only the original PowerShell assertion failing. Tools remain present. The advisory trace refuses before any debugger output: debugger hash present, helper/command hashes absent, no target/arming/exit evidence, status 8/error 13 and cleanup fault zero.
- **Changed:** Numeric preflight point/tool-role fields precede each SDK trust and publication operation. The first fault location is latched separately from cleanup faults; malformed markers cannot leak raw exception, certificate, path or command content. No trust condition is loosened.
- **Validation:** Parent direct review covers the PowerShell-only delta. All 49 pure controls, installer static controls, isolated root build and 2,182 Core cases pass. The 457-case native discovery contract is unchanged.
- **Blockers:** CDB has not yet reached the owned child, so this is not an initialization trace or a PowerShell correction.
- **Next:** Publish the closed preflight markers and identify the actual refused tool or source operation before repairing that boundary.

### 2026-10-22 - Owned native-loader trace is wired behind the still-failing gate

- **Changed:** A fixed debug-only helper creates one existing restricted PowerShell child. CDB child creation/exit events, actual debug-port/identity checks and a PID/creation-bound acknowledgement arm live per-process loader snaps before the owned single resume. No global IFEO flag, guessed offset, privilege override, token/ACL change or arbitrary target command is added.
- **Boundary:** Native initialization text must have a recognized failure grammar and match historical arming before exit. Module loads/exit-only output cannot become root evidence. The normal PowerShell exit-37 gate remains strict and blocks servicing/publication even if diagnostics complete.
- **Review:** Initial output-budget enforcement and its first exact-limit revision were independently rejected. Both authors are locked out; fresh owners implemented combined accepted UTF-8 byte accounting and immediate bounded cleanup at or above 8 MiB, including zero-remaining states. Final independent review approves source only.
- **Validation:** Parent passes 37 pure protocol/driver controls, warning-free root and Windows cross-builds, 2,182 Core and installer static controls. Eighteen diagnostic cases require exact Windows discovery of 457 (old 439 retained); native debugger execution is still pending.
- **Privacy:** Debugger/helper/source files are pinned; raw output is bounded and private. Only the numeric/hash advisory report is retained for three days, never raw trace, dumps or binaries. Missing binding, unknown text, timeout and incomplete cleanup remain failures.
- **Next:** Run this wired diagnostic on the confirmed installed debugger and use the first genuine initialization failure to select the correction.

### 2026-10-22 - Native startup tracing replaces further permission variants

- **Changed:** The disposable Windows installer job now reports only presence/absence for fixed SDK debugger prerequisites: CDB, GFlags and DbgEng. The read-only inventory runs even when the strict PowerShell test fails; no assertion, token, registry, privilege or artifact policy changes.
- **Validation:** Isolated root build and all 2,182 Core cases pass; installer static controls and workflow lint pass. Native discovery remains 439 and the original exit-37 requirement remains blocking.
- **Blockers:** Actual debugger availability is pending. PowerShell still has no proved script execution, despite the measured context, image, security descriptors and clean stdio lifecycle.
- **Next:** Select a primary-supported, owned-process-only loader trace using an existing debugger if available. No tracing, global loader flag or debugger privilege is assumed from this inventory.

### 2026-10-21 - Token-object observation closes another hypothesis without changing authority

- **Evidence:** Published `cf8aa10` run `39611721626`, job `119262016763`, passes 438/439 native cases with zero skips. Every actual child token object has DACL mask 0x6001F, an explicit Medium label (8192), policy 1, flags zero and successful query/cleanup; no higher token-object label is observed.
- **Outcome:** All 28 added cases pass, but the original PowerShell exit-37 test remains failing. Detached still returns zero; no-window/new-console return 0xC0000142. Image matches, exact source, empty/closed streams, actual restricted context and parent preservation remain measured. No token-object correction is justified by these results.
- **Stocktake:** Both supported OS jobs and Tray pass on this source head; installer fails and publication is skipped. Remote main is still the shipped financial slice `e7758a3`; PR #30 is draft and unmerged. Authentication/coverage source and installed acceptance remain distinct. Strict legacy repair #24 remains disabled/blocked.
- **Storage:** 90 bounded report artifacts total 19,813,156 bytes; two npm caches total 80,649,421 bytes. Three-day report retention remains; no MSI/DLL/raw-TRX uploads or shared-runner servicing.
- **Blockers:** PowerShell has not executed its fixed script, so whole-Medium lifecycle, administrative provider split, installed native authentication, meaningful refusal causes, strict component transport/cleanup and standalone first-run proofs remain unfinished.
- **Next:** Stop adding permission/console variants; obtain a bounded native-startup trace of the owned disposable child before selecting another correction. No merge, release or completion claim is authorised by the passing source gates.

### 2026-10-21 - Actual child-token object security becomes observable

- **Changed:** Only the owned actual child-primary open adds READ_CONTROL to TOKEN_QUERY. Its retained handle supplies token target 3 to existing no-audit Authz, bounded kernel DACL/LABEL queries and decoders; the same actual token remains the subject.
- **Boundary:** Kernel-object security is distinct from default-DACL templates and integrity attributes. No setter, duplication, impersonation, privilege or existing-object permission change is added. Unknown/failed observations cannot become denial or inferred labels.
- **Validation:** Fresh independent C# review approves 28 new cases and all old assertion retention, requiring exactly 439. Byte-exact mechanical application preserves BOM/LF; isolated root build, 2,182 Core, Windows cross-build and all static controls pass.
- **Visibility:** Token results flow through existing failing-test rows in all five PowerShell arms. Primary-open failure remains numeric, prevents resume and preserves cleanup; every exit-37/full-self/parent check stays strict.
- **Blockers:** Actual token-object data and PowerShell script execution remain unproved. No root correction, installed lifecycle or merge follows from this observation.
- **Next:** Publish and inspect actual token DACL rights and labels without changing token authority.

### 2026-10-21 - Hidden private console does not execute the PowerShell fixture

- **Evidence:** Published `bb5f79c` run `39607587761`, job `119247700844`, passes 410/411 native cases with zero skips. The new hidden-console arm also exits 0xC0000142; detached arms still return zero with either process/thread descriptor.
- **Boundary:** All five arms match the image path, validate eight source bytes, verify the restricted context, close pipes/drains and record empty output/error streams. Parent security/connection and owned cleanup pass. No tested console or process/thread-DACL policy establishes script execution.
- **Blockers:** The original exit-37 assertion remains failing. No launch policy or permission expansion is promoted, and no installed lifecycle follows this failed gate.
- **Next:** A fresh specialist observes the actual child token kernel object's DACL and label. This is distinct from the already-set token integrity and its default DACL template; the proposal adds only READ_CONTROL to the owned token query, not a setter, privilege or new token mechanism.

### 2026-10-21 - Hidden private-console comparison is source-ready

- **Evidence:** Published `de69d69` run `39605327219`, job `119239923925`, records all four image-matched, eight-byte-source PowerShell arms with complete pipe/drain EOF and zero output/error bytes. Detached returns zero and no-window 0xC0000142 with both partial and full self grants; 406/407 native cases pass, none skip.
- **Changed:** One additional owned PowerShell arm requests a new hidden console (creation 0x00080014, startup 0x101/SW_HIDE), with the original descriptor and all existing token/source/stdio/job/cleanup controls. It allocates no shared desktop object or global setting; existing five exit-37 checks remain strict.
- **Validation:** Independent C# review approves four new policy cases, requiring exactly 411. Windows cross-build/static controls pass. Root's first attempt encounters a Razor/Roslyn shared-server MissingMethodException; isolated build with shared servers disabled passes warning-free, then all 2,182 Core cases pass. No source change masks that tooling failure.
- **Blockers:** Actual new-console allocation/visibility, script execution and full lifecycle remain unproved.
- **Next:** Publish this last bounded console-policy comparison and require actual exit 37 before selecting a launch correction.

### 2026-10-21 - PowerShell descriptor factor joins the closed console comparison

- **Evidence:** Published `af04f41` run `39603408632`, job `119233368753`, passes 406/407 without skips. Both original arms match the actual image path, validate eight script bytes, close stdin/pipes/drains and record zero stdout/stderr bytes with EOF/error zero. DETACHED returns zero; CREATE_NO_WINDOW returns 0xC0000142.
- **Changed:** Two extra arms reuse the existing owned-self descriptor with each console policy. All four observations precede strict exit-37 assertions; new descriptor arms also require full actual self grants and the original Medium label policy. No token or existing ACL mutation occurs.
- **Validation:** Fresh independent C# review approves the small two-file patch. Discovery stays 407; warning-free root/Windows cross-builds, 2,182 Core and static controls pass.
- **Blockers:** PowerShell with full process/thread self access had not been measured. This is a closed descriptor counterfactual, not a promoted correction or proof of which startup operation needs a right.
- **Next:** Publish and compare the complete console/descriptor matrix under the unchanged context and cleanup requirements.

### 2026-10-21 - PowerShell image and final stream observations become visible

- **Changed:** A read-only query matches the actual suspended image path to the fixed application. Existing final I/O facts flow as immutable numeric ITest rows after cleanup. The same token runs DETACHED and a PowerShell-only CREATE_NO_WINDOW comparison; both exits remain required to equal 37.
- **Preserved:** No payload, image path, SID, handle or environment values are printed. Ordinary cmd policy, the exact script lease, authority, controlled inheritance and parent/cleanup checks remain unchanged. Image-path matching is not hash attestation or an actual argv query.
- **Validation:** Fresh independent C# review approves 13 new cases, requiring exactly 407. Warning-free root/Windows cross-builds, 2,182 Core and all static controls pass. One overlapping patch-context boundary was split mechanically without changing authored source.
- **Evidence:** The actual runner image's published software profile identifies PowerShell 7.6.7. Inspection of that tag's host/parser source supports retaining explicit exit 37 and found no demonstrated fixed-switch/file/EOF contract defect; it does not attest the launched binary.
- **Blockers:** Actual comparison data and exact script execution remain pending. No console cause, whole-Medium lifecycle, installed acceptance or merge success is inferred.
- **Next:** Publish and read both actual image-match/stream/exit rows before selecting any PowerShell correction.

### 2026-10-21 - Controlled stdio alone does not establish PowerShell script execution

- **Evidence:** Published `eece787` run `39599263190`, job `119219281839`, passes 393 of 394 native cases with zero skips. All 47 new stdio cases pass; the same genuine PowerShell case still returns 0 rather than 37.
- **Boundary:** Actual restricted context, job/process cleanup and parent security/connection still pass. Controlled handles are not proved to be a remedy. The numeric I/O diagnostic currently goes to Console.Error and is absent from minimal failing-test output, so its stream facts are not inferred.
- **Blockers:** Exact fixed-script execution and whole-lifecycle Medium routing remain unproved. No installed lifecycle follows the failed native gate; zero is not promoted to success.
- **Next:** A fresh C# owner traces the fixed image/arguments/script/stream contract and wires only the minimal closed diagnostic or causal counterfactual into visible failing-test evidence, preserving exit 37.

### 2026-10-21 - Fixed PowerShell probe receives controlled stdio

- **Changed:** The final scoped implementation owns three stdio pipes, an explicit inherited-handle list and extended startup information only for the fixed PowerShell probe. Parent copies close before resume, stdin has EOF, concurrent bounded drains report numeric counts/status and checked cleanup retains failures.
- **Scope:** This is a complete wired probe thread, not the previously overbroad whole-harness bundle. Provider bootstrap, general harness requests/dispatch, final context receipts and actual lifecycle routing remain deferred. Normal cmd, token authority, source lease and the PowerShell exit-37 assertion stay unchanged.
- **Validation:** Fresh independent C# review approves all five targets and 47 new cases, requiring exactly 394. Mechanical application changes only authorised new-file BOM prefixes; parent byte audit, warning-free root/Windows cross-builds, 2,182 Core and all static controls pass.
- **Blockers:** Controlled stdio is not yet proved to cure the observed PowerShell exit 0. Actual 394-case execution, Medium observation and the installed lifecycle remain pending.
- **Next:** Publish and read the real fixed-script exit and bounded pipe/drain facts before generalising this launcher.

### 2026-10-21 - Fixed PowerShell does not yet execute the exit fixture

- **Evidence:** Published `28d9183` run `39593010501`, job `119198389679`, passes 346 of 347 native cases with zero skips. All original 324 and all 22 read-only reporter cases pass; only the new PowerShell case fails.
- **Outcome:** PowerShell's actual pre-resume restricted context, owned cleanup and parent security/connection checks pass, but its process returns 0 instead of the fixture's required 37. The script execution prerequisite is not established; zero is not accepted as success.
- **Boundary:** The fixed probe currently inherits no stdio handles. That is an observed launch configuration, not an identified cause. The real lifecycle still has no Medium routing or observer/channel/cache proof.
- **Next:** The fresh whole-harness owner includes the existing PowerShell probe in the shared controlled-stdio launch path while retaining its exact source lease, actual context and exit-37 assertion. No fallback, skip or assertion relaxation is authorised.

### 2026-10-21 - All restricted-child tests pass; next context prerequisites are wired

- **Evidence:** Published `e563f80` run `39590722505`, job `119190696185`, passes all 324 native cases without failures/skips. Both real MSI versions again pass complete native ICE with 699 exact prerequisite-bound warnings each.
- **Boundary:** Saved acceptance still records Install and Recover as RecoveryRequired/1603/TransactionFenceMissing, with a High Administrator caller and session/elevated hint. The original high harness has not yet been routed through the proved restricted context; cleanup correctly remains incomplete.
- **Changed:** A closed fixed-PowerShell startup fact uses the same restricted child path and a leased exact-byte exit script. Independent reporter acquisition replaces an all-access cache request with a live non-inherited read-only kernel pin, preserving loaded-image/time/hash and writer bindings.
- **Validation:** Independent C# review approves both source artifacts and counts 347 cases (324 plus 1 genuine PowerShell and 22 reporter cases). Root/Windows cross-builds have zero warnings/errors; 2,182 Core and all static controls pass. New C# files follow project BOM/LF rather than the observer author's conflicting no-BOM formatting note.
- **Blockers:** Actual PowerShell startup, Medium observer/channel/cache access, fixed whole-harness launcher and separate administrative provider bootstrap remain pending. No MSI, installed UI or merge success is inferred.
- **Next:** Publish and require all 347 cases before using these prerequisites to route the actual lifecycle.

### 2026-10-21 - Detached execution genuinely starts the restricted child

- **Evidence:** Published `d5fd646` run `39589130087`, job `119185383021`, records arm 4 exiting 37 with verified restricted context, original partial self grants, unchanged Medium labels and complete cleanup. All CREATE_NO_WINDOW arms still exit 0xC0000142, including the full-self-DACL arm; parent security/connection remain unchanged.
- **Changed:** The eligible C# owner promotes constant CREATE_SUSPENDED|DETACHED_PROCESS for every fixed inert child, removing the temporary selector/factory/duplicate arm. No permission, token, default descriptor, environment, job or inheritance policy changes. All original exit-37/full-self/parent/cleanup assertions remain.
- **Validation:** Fresh independent source review approves the two-file correction; warning-free root/Windows cross-builds, 2,182 Core cases and all static controls pass. Source discovery remains 324; actual promoted-head acceptance is pending.
- **Blockers:** A successful fixed cmd arm is not PowerShell, MSI or installed UI proof. Whole-lifecycle Medium routing and separate administrative provider bootstrap remain unimplemented.
- **Next:** Publish the normal detached path and require all original native startup assertions to pass before wiring the separately scoped lifecycle launcher.

### 2026-10-21 - Detached execution isolates one creation-policy factor

- **Changed:** One extra owned arm uses CREATE_SUSPENDED|DETACHED_PROCESS (0x0000000C) instead of baseline CREATE_SUSPENDED|CREATE_NO_WINDOW (0x08000004). Command, automatic desktop, baseline descriptor, environment, restricted token, job and cleanup stay unchanged.
- **Visibility:** Existing failing A/B output records arm 4 before the owned-descriptor arm's assertion. All original baseline/genuine/counterfactual exit-37 requirements remain; no discovered test is added and the exact gate stays 324.
- **Validation:** Fresh independent C# review approves the literal two-file patch; root and Windows cross-builds pass without warnings/errors, 2,182 Core cases pass and all installer static controls pass.
- **Blockers:** Actual detached startup is pending. Full self DACL grants were already measured without curing initialisation, so that descriptor is not promoted.
- **Next:** Compare the actual detached outcome. The separate context-routing map recommends one future Medium PowerShell lifecycle with administrative provider bootstrap outside it; routing and its remaining evidence/cleanup/first-run gaps remain conditional and unimplemented.

### 2026-10-21 - Full owned-object self grants do not cure initialisation

- **Evidence:** Published `67692d5` run `39587085089`, job `119178550755`, gives counterfactual arm 3 full process/thread self DACL masks 0x1FFFFF, successful Authz replies and closed resources. Both labels remain Medium (8192), policy 3 and flags zero.
- **Outcome:** The child still exits 0xC0000142 instead of 37. All baseline and counterfactual contexts/cleanup and parent security/connection checks remain intact; token ownership remains open until its normal disposal. Supplying this owned descriptor is demonstrably insufficient and is not promoted as a correction.
- **Validation:** All 30 new cases pass: 322 of 324 native cases pass, the original two test cases remain failing, and none skip. No installer lifecycle follows the failed gate.
- **Next:** A fresh specialist isolates one process-creation policy factor: detached execution versus CREATE_NO_WINDOW for the same fixed inert command, without changing descriptors, authority or baseline requirements.

### 2026-10-21 - One owned-only descriptor counterfactual retains all baseline failures

- **Changed:** A source-approved extra arm authors a private descriptor from the actual filtered token's User/Owner/Group/default DACL and restricting profile. It preserves existing ACEs/denials and appends one user-only GENERIC_ALL ACE for kernel mapping at creation of the new owned process/thread. No token default or existing ACL changes.
- **Evidence:** Unsupported restricting profiles refuse before launch; bounded SID/DACL reads and private token-stamp checks remain internal. The arm requires exit 37, full self grants, unchanged Medium label policy and complete cleanup. Original genuine and both baseline exit-37 assertions remain blocking.
- **Validation:** Independent C# review approves all eight targets and counts 30 new cases, requiring exactly 324. Mechanical application preserves exact bytes/BOM/LF; warning-free root/Windows cross-builds, 2,182 Core and all static controls pass.
- **Blockers:** This remains an unexecuted counterfactual, not a correction or identified DLL cause. Previous actual self grants were partial and both baseline starts failed.
- **Next:** Publish and compare the extra arm's actual grant masks and exit with the unchanged baseline arms.

### 2026-10-21 - Actual child-token access to its own objects is partial

- **Evidence:** Published `e029b8e` run `39583539488`, job `119166730148`, observes process mask 0x161411 and thread mask 0x161848 under the actual verified child token in all three arms. Authz API/reply errors are zero and resources close; these are partial masks, not full 0x1FFFFF access.
- **Boundary:** Both owned objects have explicit Medium labels (8192), policy 3 (NoWriteUp and NoReadUp), no inheritance flags and error zero. The subject is also Medium. Parent USER-object grants/Low labels remain unchanged. No particular required initialisation operation or loader cause is identified.
- **Validation:** All 35 new cases pass: 292 of 294 native cases pass, the same two 0xC0000142 failures remain, and none skip. Context, resume, parent security/connection and cleanup still pass. The failed native gate prevents installer lifecycle execution.
- **Next:** The eligible C# owner traces the private filtered-token default DACL/restricting-SID origin and a minimal owned-only correction or causal counterfactual, preserving the original exit-37 requirements.

### 2026-10-21 - Owned-child security inventory avoids guessed generic mappings

- **Decision:** The process/thread AccessCheck approach stopped without code because the available primaries did not establish its generic mappings. A fresh specialist instead uses a documented no-audit Authz context from the actual verified child primary token, retaining TOKEN_QUERY only.
- **Changed:** Before the existing resume, bounded read-only queries collect owned process/thread DACLs and labels. Generic/object/callback ACEs remain unsupported; native failure, per-reply denial and malformed output stay distinct. Both A/B arms emit numeric results while original exit-37, parent-preservation and cleanup requirements remain.
- **Validation:** Independent C# source review approves the complete patch; mechanical application is byte-exact with BOM/LF preserved. Root/Windows cross-builds have zero warnings/errors; 2,182 Core cases and all installer static controls pass. Thirty-five new cases require exactly 294 native cases.
- **Blockers:** Actual owned-object observations and successful child initialisation are pending. Last completed head `6605bd6` passes both supported OS jobs, Tray and measured 86.0% line / 61.46% branch coverage on the unchanged denominator, but retains two startup failures.
- **Next:** Publish and read the actual child-token DACL inventory and object labels; no permission change or loader-root claim follows from source approval.

### 2026-10-21 - Both parent USER objects carry explicit Low integrity labels

- **Evidence:** Published `6605bd6` run `39578854205` returns a present label on both station and desktop: RID 4096 (Low), policy 1 (NoWriteUp), inheritance flags zero and error zero. The restricted subject is Medium (8192); these objects are not higher-integrity write-up targets. DACL grants remain full.
- **Validation:** All 65 new cases pass; the native project records 257 passes, the same two startup failures and zero skips. NULL and explicit-parent children still exit 0xC0000142, with verified contexts, complete cleanup and unchanged parent security/connection. Portable Control 133 and Setup 1,087 also pass.
- **Blockers:** Child initialisation still fails. No installed lifecycle follows the failed native gate, and the label observations do not establish overall kernel access or identify the failing DLL.
- **Next:** A fresh C# specialist traces the documented default-security origin and a read-only self-access discriminator for the owned suspended process/thread objects.

### 2026-10-21 - Read-only parent USER labels reach the blocking startup experiment

- **Changed:** A fresh C# specialist adds label-only queries on the existing parent station/desktop. Bounded numeric results distinguish present, missing, unsupported, failed and malformed labels while retaining policy and inheritance flags. No ACL, privilege, parent connection or child setting changes.
- **Evidence:** Independent source review approves the exact four-file patch; the mechanical audit confirms unchanged non-target files and preserved BOM/LF. Sixty-five new cases require exactly 259 native tests, retaining the original exit-37 assertions and failing A/B visibility.
- **Validation:** Root build and Windows cross-build pass with zero warnings/errors; all 2,182 Core tests and installer static/transport/provenance/workflow controls pass. Native bodies were not executed on the Mac.
- **Blockers:** Actual label observations and successful restricted-child startup remain pending. The last Windows run still has two blocking 0xC0000142 outcomes despite full DACL grants; effective MIC access and loader causation are unresolved.
- **Next:** Publish the reviewed observation and inspect the actual label state before choosing the startup correction.

### 2026-10-21 - Actual restricted token receives full DACL rights on parent USER objects

- **Evidence:** Published `ca94274` run `39575648958` observes full owner/group/DACL grants: station mask 0xF037F and desktop mask 0xF01FF, both known decisions/error zero. The query-only restricted duplicate is verified and closed; the station is interactive.
- **Boundary:** MIC is unmeasured. DACL grants do not establish full kernel access, child connection, DLL cause or successful startup. NULL and explicit-existing-parent children still exit 0xC0000142 with verified contexts, successful cleanup and unchanged parent.
- **Validation:** 192 of 194 native cases pass; the original genuine and A/B success assertions remain failing, with zero skips. No installer operation follows the failed gate.
- **Next:** A fresh specialist owns only the remaining startup-object integrity/default-security or causal read-only loader fact; no existing ACL, host policy or success requirement changes.

### 2026-10-21 - Actual restricted-token DACL access is queried without host changes

- **Changed:** The source-approved read-only observation queries existing parent station/desktop owner/group/DACL access using an owned query-only impersonation duplicate. It assigns no token, launches no extra child and changes no ACL or parent connection; MIC remains explicitly unmeasured.
- **Validation:** Parent passes warning-free root/Windows cross-builds, 2,182 Core and all static controls. Thirty-six new fake/native cases require exactly 194, retaining all old 158 assertions and zero skips.
- **Visibility:** A fresh specialist attaches the same numeric observation to the still-failing A/B test so passing-test output cannot hide the access result. Parent preservation and original exit-37 assertions remain.
- **Boundary:** Query failure/unsupported mapping is not denial; any DACL grant is not full kernel access, child connection or loader-root proof.
- **Next:** Publish and read only the actual decision/mask/error scope before selecting a startup correction or requesting a separately bounded owned-object context.

### 2026-10-21 - Existing desktop selection alone does not cure initialization

- **Evidence:** Published `ddf1f19` run `39571448965` records NULL and explicit-existing-parent arms both exiting 0xC0000142 after verified restricted context/resume, with complete cleanup. Parent final security and connection comparisons are one.
- **Validation:** 156 of 158 native cases pass; the original NULL genuine case and the A/B success assertion remain failing. Both supported OS jobs and Tray pass. No error exit or missing assertion is accepted to change that result.
- **Boundary:** Explicit desktop-name selection alone is insufficient. Neither ACL denial, a particular DLL nor environment causality is established. Ordinary MSI acceptance remains unexecuted after the failed native gate.
- **Next:** Read only the actual restricted-token access/startup facts needed to choose a correction; do not change existing desktop ACLs, credentials, token policy or installer success requirements.

### 2026-10-21 - Existing-desktop binding is tested without relaxing startup success

- **Changed:** The source-approved inert A/B observation varies only NULL versus explicit existing parent station/desktop binding. OS names stay internal; token, pre-resume verification, job, cleanup and all ordinary acceptance settings remain.
- **Validation:** Parent passes warning-free root/Windows cross-builds, 2,182 Core and all static controls. Twenty parser/capability cases plus one genuine counterfactual require exactly 158 native cases; all original 137 assertions remain, including exit 37.
- **Boundary:** Numeric outcomes from both arms must precede their success assertion. No ACL denial, DLL cause, permanent default or successful child/MSI execution is inferred before the Windows result.
- **Next:** Publish one A/B result, then choose only the correction supported by that observed difference while retaining parent-security/connection and cleanup checks.

### 2026-10-21 - Real restricted child verifies but fails loader initialization

- **Evidence:** Published `d0dc2c5` run `39567284420` executes 137 native cases: 136 pass, one genuine child case fails. Its numeric result verifies actual child context before resume, unchanged parent, successful disposal/closed-use refusal and no incomplete cleanup.
- **Failure:** The child exits with 3221225794 / 0xC0000142, not intended exit 37. This is a loader-initialization failure, not a proved desktop, DLL or environment cause. Successful creation/verification/cleanup is not successful child execution.
- **Boundary:** No MSI/profile operation is performed by the inert child. Ordinary acceptance does not run after this failed gate; no installer-acceptance artifact is created.
- **Next:** The specialist's final focused cycle checks documented startup-context semantics or supplies only the missing bounded observation, without changing host ACL/UAC policy or accepting the failing exit as success.

### 2026-10-21 - Inert restricted-child lifetime is bounded before harness integration

- **Changed:** The source-approved acceptance primitive creates only a fixed inert child suspended, assigns its owned job, verifies actual child token context before one resume and tracks exit/cleanup. No MSI, owner-profile or installer routing is added.
- **Validation:** Parent passes warning-free root/Windows cross-builds, 2,182 Core and all static controls. Forty-five fake/kernel cases raise the exact Windows gate to 137 while preserving all old 92 assertions. Native bodies were not executed on the Mac.
- **Isolation:** Token-sensitive classes share a nonparallel xUnit collection. The new parent-security check compares final privilege state rather than the documented API's mutable ModifiedId; the old factory assertion remains unchanged.
- **Boundary:** Restricted child creation, verified token, native exit 37 and owned cleanup are not claimed before Windows execution. Ordinary acceptance still runs in its old context and remains blocked.
- **Next:** Publish and obtain genuine child proof before adding ordinary acceptance environment/stdio routing and separating administrative provider bootstrap.

### 2026-10-21 - Kernel proves the restricted-owner primary token works

- **Evidence:** Published `9665a3b` run `39563793563` executes/passes all 92 native cases with zero skips, including the genuine high/default/no-linked filtering case. The filtered primary is non-elevated, medium, Administrator-disabled/deny-only and same owner/session; the caller thread remains unimpersonated.
- **Preservation:** The genuine case verifies unchanged parent context/token identity/modification, owned-token disposal and refused closed-token reuse. No process, profile or MSI operation is performed by the factory.
- **Boundary:** Ordinary acceptance still uses its original high context and remains RecoveryRequired/1603; both complete 699-warning MSI validations pass. A verified token alone is not a verified child process or installation.
- **Next:** A fresh specialist owns only suspended restricted-child creation, actual child-context verification before resume, owned lifetime/exit/cleanup and an inert Windows proof before harness integration.

### 2026-10-21 - Restricted-owner token creation is isolated from process launch

- **Changed:** The source-approved acceptance-only factory creates and verifies an owned restricted primary token without changing the parent or launching anything. It checks every required owner/session/elevation/integrity/group predicate and fails closed on unknowns.
- **Validation:** Parent passes warning-free root/Windows cross-builds, 2,182 Core and all static controls. The 33 new fake/kernel cases raise the exact Windows gate to 92, preserving all prior 59 definitions. Native bodies were not run on the Mac.
- **Boundary:** Actual LUA filtering on the observed high/default/no-linked host is not yet established. Ordinary Setup acceptance still uses the original context; no production guard, property, package, account, ACL or UAC policy changes.
- **Next:** Publish and require the genuine numeric kernel proof, parent-unchanged result and disposal checks before implementing child execution.

### 2026-10-21 - Actual Setup caller is high-integrity enabled Administrator

- **Evidence:** Published `aebbae9` run `39558881956` measures both original Install and separate Recover callers before MSI: process observed, elevated one, elevation type Default, integrity RID 12288, Administrator enabled, thread token absent and linked token absent. No limited linked token is inferred or available in that observation.
- **Validation:** All 59 native cases execute/pass with zero skips; both compiled LUA/language/admin-row checks, both complete 699-warning validations, both supported OS jobs and Tray pass.
- **Boundary:** The LUA package does not demote a high caller. The guard remains strict; RecoveryRequired/1603/Ready-false and unsafe-completed-cleanup reporting remain. Token measurements authorize no servicing or recovery state.
- **Next:** A fresh specialist owns a test-only same-owner non-elevated execution context, keeping privileged provider bootstrap separate and proving child context before effects rather than relaxing production checks.

### 2026-10-21 - MSI caller privilege context is measured without mutation

- **Changed:** The source-approved read-only observation samples actual Setup process/thread token context immediately before the MSI API. Numeric status distinguishes absent, failed and unobserved; identity comparisons stay internal and no token/privilege/property is changed.
- **Integration:** Internal bounded diagnostic version 2 carries context through existing MSI evidence/save/final consumers while version 1 and the public five-field schema remain compatible. Original Install/recovery data and chronological first programming-fault priority are preserved.
- **Validation:** Parent passes 133 Control, 1,087 Setup, 2,182 Core, warning-free root/Windows cross-builds and all static controls. The new genuine Windows query test is inert with respect to MSI; the exact native gate becomes 59 without changing the existing 58 cases.
- **Boundary:** The package already passes the LUA WordCount-10 gate. That does not prove runtime caller privilege; no unprivileged launcher or guard relaxation is introduced before actual token evidence.
- **Next:** Publish, obtain the original Install's numeric process/thread/linked-token context, then select the minimal correct acceptance execution context.

### 2026-10-21 - Saved native hint selects the elevation context predicate

- **Evidence:** Published `910542b` run `39554481075` saves separate Install and failure-Recover hints with source 1/observed 1/point 2/reason 12. This untrusted active-callback hint selects the existing MsiRunningElevated context predicate; it is not native provenance, a fence, or independent caller-token proof.
- **Validation:** All 58 native cases execute/pass with zero skips, preserving the original 57 definitions. Both compiled language/admin-row checks, both complete 699-warning validations, both supported OS jobs and Tray pass.
- **Boundary:** RecoveryRequired/1603/Ready-false, absent own receipts and unsafe-completed-cleanup reporting remain. The elevation predicate is not relaxed and no runtime property is forged.
- **Next:** A fresh specialist checks actual operator token, MSI package privilege semantics and the disposable acceptance context before choosing a least-privilege harness correction.

### 2026-10-21 - Native preflight context hints have a complete evidence thread

- **Changed:** The source-approved numeric observation precedes session reading and distinguishes context/private-exchange points without becoming a fence or native-provenance claim. Original criteria and private signal/permit behaviour remain; the hint is explicitly untrusted active-call data.
- **Integration:** Setup conversion, bounded parser, separate Install/recovery stage saving and final publication validation consume the hint. Invalid or mixed input preserves the original native result without raw stderr or accepted partial metadata.
- **Validation:** Parent passes 111 Control, 1,084 Setup, 2,182 Core, warning-free root/Windows cross-builds and all static controls. The new real-MSI invalid-handle fact raises the required Windows count to 58 without changing the original 57 definitions; it is not published-DLL entry proof.
- **Boundary:** No native refusal reason or successful installed chain is claimed before actual Windows execution. Ready and recovery barriers remain unchanged.
- **Next:** Publish and read the original Install's bounded native hint to select the exact preflight correction.

### 2026-10-21 - Actual MSI observation ends at preflight without an own receipt

- **Evidence:** Published `6804016` run `39549903525` preserves original Install observation action 1399/error 0/readFailed 0/preflight 0/fence 0, separately from an equal failure-Recover observation. Protocol and diagnostic validation succeed.
- **Boundary:** ActionStart identifies the last recognised action, not guard entry/completion. Error zero means no numeric MSI error observed, not success or a proved load failure. Neither own receipt exists; RecoveryRequired/1603/Ready-false and unsafe-completed-cleanup reporting remain.
- **Validation:** All 57 native cases, both compiled language/admin-row checks and both complete 699-warning validations pass. No readiness or native barrier is inferred.
- **Next:** A fresh specialist traces only native preflight entry, context reading and private exchange to establish a strict root correction or one bounded native refusal reason.

### 2026-10-21 - Bounded native MSI evidence preserves callback and recovery semantics

- **Changed:** The source-approved observer retains only first numeric MSI error/action and actual own-fence flags, freezes before cleanup and stores Install/recovery separately. Public schema, native outcomes, private INFO authority and recovery barriers remain unchanged.
- **Review closure:** A fresh C# revision fixes swallowed diagnostic programming faults at the real callback dispatch/catch boundary. Managed handoff follows disposal, End and receipt publication; pre-existing native/open/dispose failures retain their faulted-receipt contract. The rejected author remains locked out.
- **Validation:** Parent passes 77 Control, 1,061 Setup, 2,182 Core, warning-free root/Windows cross-builds and all static controls. Strict paired-frame, save-time and publication consumers reject contradictions or mixed metadata without changing the original native failure.
- **Boundary:** This is observation, not a fence or native failure correction. RecoveryRequired/1603 remains the current actual result and publication stays blocked.
- **Next:** Publish and obtain the original Install's bounded MSI action/error/fence record from normal Windows execution before selecting a native bridge correction.

### 2026-10-21 - Real MSI execution reaches an unestablished transaction fence

- **Progress:** Published `8bdf0dc` run `39544546936` passes media/guard admission and invokes the real MSI engine. First Install now returns RecoveryRequired with MSI exit 1603 and TransactionFenceMissing, rather than a pre-engine media refusal.
- **Evidence:** Failure Recover also returns RecoveryRequired/1603/TransactionFenceMissing; Ready remains false. Coordinator cleanup explicitly cannot establish a safe completed state. No successful installed or native-barrier outcome is inferred.
- **Validation:** All 57 native cases, both compiled language/admin-row checks, both complete 699-warning validations, both supported OS jobs and Tray pass. The temporary observation seam is removed; publication remains blocked.
- **Next:** A fresh C# specialist traces only the real MSI/native preflight-fence/private-callback path and obtains the first bounded failure reason without weakening recovery barriers.

### 2026-10-21 - Strict NativeAOT compatibility replaces the temporary observation

- **Changed:** The source-approved C# correction retains legacy strict-two support and recognises only the observed closed three-entry callback/runtime-DATA profile. Names, ordinals, targets, backing, roles and exact object layout remain checked; callback authority and whole-image SHA approval are unchanged.
- **Validation:** Parent passes 1,049 Setup cases, including 90 new permutation/adverse/hash/composition controls, and 2,182 Core. Root/Windows cross-builds are warning-free and all static controls pass.
- **Cleanup:** Both temporary PowerShell files and the publication/test hooks are removed. Normal publication/acceptance flow is restored; actual one-bit evidence remains in the retained job/session log.
- **Boundary:** This intentionally corrects producer compatibility; it does not claim that embedded-MSI admission, approved-hash verification or installation has passed yet.
- **Next:** Publish and run normal exact-head Windows acceptance through guard admission and the complete installed lifecycle.

### 2026-10-21 - Actual NativeAOT producer matches the closed three-entry profile

- **Evidence:** Published `d4d40bb` run `39542344273` reports the single profile-match bit as one before native loading/hash selection. The pinned published guard has exactly the two correlated executable callbacks and the source-defined bounded non-executable runtime data object, with the complete closed profile matched.
- **Validation:** All 57 native cases, both compiled language/admin-row checks and both complete 699-warning validations still pass. The unchanged C# two-entry policy still refuses first Install.
- **Boundary:** This observation does not prove embedded-MSI Binary identity or approved-hash membership; neither is bypassed. No raw binary, export names, addresses or values were emitted.
- **Next:** A fresh C# specialist owns strict producer compatibility, preserved exact hash/callback authority and removal of the temporary four-file observation seam.

### 2026-10-21 - One closed producer-profile observation precedes native loading

- **Changed:** The source-approved temporary build probe emits only a zero/one match for a complete candidate callback/runtime-data profile before native loading or hash selection. A negative result fails publication; C# admission remains unchanged.
- **Validation:** Parent passes all static controls, warning-free root build, 2,182 Core and unchanged 959 Setup cases. Synthetic image and exception-boundary controls distinguish expected negatives from programming/cancellation failures. The specialist corrects PowerShell getter access without broadening the catch filter.
- **Boundary:** No actual profile, export count/name set, embedded Binary equality or approved hash is claimed yet. The source-defined runtime object does not alone prove Windows export selection.
- **Next:** Publish one actual observation, then remove or replace the temporary parser as part of the evidence-led strict compatibility correction.

### 2026-10-21 - Actual export function count fails the two-entry contract

- **Evidence:** Published `a085bfd` run `39539351996` returns stage 10/cause 55 with zero auxiliary fields and no protocol/diagnostic error. NumberOfFunctions was successfully read and differs from two; this is not a count-read exception. The count value, names and approved-hash outcome remain unobserved.
- **Validation:** All 57 native cases, both compiled language/admin-row checks, both complete 699-warning validations, both supported OS jobs and Tray pass. First SDK-free Install still refuses and publication is skipped.
- **Producer:** The actual SDK is 9.0.319; official release metadata maps it to runtime 9.0.21. Unchanged pinned NativeAOT export-writer, unmanaged-entry-root and debug-header primaries are available to a fresh specialist.
- **Next:** Establish a closed producer-compatible export profile or one precise missing actual predicate, preserving callback authority and exact hash approval.

### 2026-10-21 - Guard PE inspection retains exact rejection-site evidence

- **Changed:** The independently source-approved instrumentation reports only the first existing PE predicate/read site. Count mismatches remain distinct from count-read exceptions; original policy, exception identity/type/message and privacy limits remain.
- **Validation:** Parent passes 959 Setup cases, including 38 new policy-oracle/boundary controls, and 2,182 Core. Root/Windows cross-builds are warning-free; all static controls pass. A fresh C# author repairs a malformed CLR fixture while retaining its exact intended assertion.
- **Boundary:** Frozen pre-instrumentation policy comparison is not producer proof. No actual export count, names, binary hash equality or approval outcome is inferred yet.
- **Next:** Publish and read the first exact predicate from normal Windows execution; any producer-compatibility correction requires fresh ownership and evidence.

### 2026-10-21 - Real guard rejection occurs before hash approval

- **Evidence:** Published `0bb6699` run `39536905926` returns stage 10/cause 42 with zero auxiliary fields and no protocol/diagnostic error. Guard input bounds return; GuardImageInspection.Read rejects before the approved-hash check is reached.
- **Validation:** All 57 native cases, both compiled language/admin-row checks, both complete 699-warning validations, both supported OS jobs and Tray pass. First SDK-free Install remains Refused; publication is skipped.
- **Boundary:** No particular PE predicate, export count, embedded hash equality or approval mismatch is inferred. Recover reports NoRecoveryPending and cleanup has no failures. Draft PR #30 now records the current evidence; main remains unchanged.
- **Next:** Identify only the first existing PE rejection predicate with fixed numeric evidence, preserving the strict image and callback policy.

### 2026-10-21 - Guard rejection preserves strict inspection and hash boundaries

- **Changed:** The source-approved diagnostic distinguishes PE inspection from the later approved-hash check with only two fixed numeric causes. Input bounds and all PE/export/hash/ABI/callback policy, catches and evidence priority remain unchanged.
- **Validation:** Parent passes 921 Setup cases, including 19 additional boundary/precedence/identity controls, and 2,182 Core. Root/Windows cross-builds are warning-free and all static controls pass. The author corrects collection assertion syntax without changing its comparer or suppressing analyzers.
- **Boundary:** The previously reported guard SHA does not establish embedded Binary equality or compiled-profile membership. No native export cardinality or particular PE failure is inferred from cause 35.
- **Next:** Publish and obtain the one remaining inspection-versus-approval fact from normal exact-head Windows execution.

### 2026-10-21 - Actual admin sequence passes; guard-image admission is next

- **Evidence:** Published `e071062` run `39535027107` passes actual unconditional InstallAdminPackage-at-3900 readback and language readback for both MSI versions, both complete 699-warning validations and all 57 native cases.
- **Progress:** Install now reaches stage 10/cause 35. Sequence, directory, registry, component and feature checks return; ReadGuard, including its existing expected-exception conversion, rejects. No particular PE/profile predicate is yet observed.
- **Boundary:** First SDK-free Install remains Refused/MediaInvalid; Recover reports NoRecoveryPending and cleanup has no failures. PR #30 remains draft and main is unchanged. Retained reports total about 11.9 MB; the two caches remain about 80.6 MB.
- **Next:** A fresh C# specialist traces only the real guard-image/profile/build contract without weakening approved hashes, ABI or callback authority.

### 2026-10-21 - Strict sequence policy recognises InstallAdminPackage

- **Changed:** Pinned WiX requires unconditional InstallAdminPackage at 3900, while two validator entries used AdminPackage. The source-approved correction changes only those names and adds actual compiled-row readback before ICE/receipt completion; all positions, conditions, guards and advertisement refusal remain.
- **Validation:** A pinned producer-data/actual-authoring reconstruction supplies independent positive baselines and 38 new adverse controls. Parent passes 902 Setup, 2,182 Core, warning-free root/Windows cross-builds and all static controls. The duplicate test explicitly retains its earlier inventory refusal.
- **Boundary:** Cause 30 alone did not observe a particular row; source proves a sufficient producer mismatch. Neither the reconstruction nor source approval certifies the installed lifecycle.
- **Next:** Publish and require normal exact-head Windows row readback plus resumed complete lifecycle acceptance.

### 2026-10-21 - Compiled language and property admission pass on real Windows

- **Evidence:** Published `0fb23eb` run `39532858534` passes exact ProductLanguage 1033 readback for both real MSI versions, then both complete 699-warning native validations. All 57 native cases, both supported OS jobs and Tray pass.
- **Progress:** Install now reaches stage 10/cause 30: Properties, Search, Upgrade and Actions validation return; ValidateSequences rejects. The original failed candidate's language value is still not inferred retrospectively.
- **Boundary:** First SDK-free Install remains Refused/MediaInvalid. Recover reports NoRecoveryPending, cleanup has no failures, publication is skipped and PR #30 remains draft.
- **Next:** A fresh C# specialist compares only the sequence contract with the pinned WiX standard-action/binder primaries and existing authored servicing positions.

### 2026-10-21 - Producer language explicitly matches the strict property contract

- **Changed:** Fresh specialist authoring pins Package Language to 1033. The finalizer requires exactly one matching ProductLanguage row in the fresh read-only MSI before ICE/receipt completion. The C# validator and all refusal policy remain unchanged.
- **Evidence:** Pinned WiX compiler/binder and concrete resolver sources establish that explicit language bypasses nullable fallback. The old failed MSI's actual language is not claimed. Independent source review approves the three-file correction.
- **Validation:** Parent passes warning-free root build, 2,182 Core, unchanged 864 Setup cases and all static controls, including actual-helper source/compiled-row adverse controls.
- **Next:** Publish and require actual compiled readback plus the complete normal Windows lifecycle; source alignment alone does not close installed acceptance.

### 2026-10-21 - Actual property refusal is a scalar comparison

- **Evidence:** Published `16aa71c` run `39530787351` returns stage 10/cause 41 with zero auxiliary fields and no protocol/diagnostic error. The property count passes and the current row name is known; its scalar comparison rejects. Short-circuiting does not prove every later row was visited.
- **Validation:** All 57 native Windows cases, both complete 699-warning MSI validations, both supported OS jobs and Tray pass. First SDK-free Install still refuses; publication is skipped.
- **Boundary:** Pinned compiler/binder sources derive the same nine property names and eight values; the language path depends on a nullable resolved LCID while the validator requires 1033. The original candidate's actual language value is not logged or inferred.
- **Next:** A fresh specialist closes the concrete resolver default and pins the producer's intended language, with an actual compiled-value gate and strict controls rather than a relaxed validator.

### 2026-10-21 - Property rejection retains its exact predicate and strict contract

- **Changed:** The source-approved refinement identifies only count, closed-name, secure-set or scalar rejection with fixed numeric codes. The single original Require and its short-circuit/comparison order remain; no names, values, counts or rows are logged.
- **Validation:** Parent passes 864 Setup cases, 2,182 Core, warning-free root/Windows cross-builds and all static controls. A fresh C# author fixes a missing test import without changing assertions. The new synthetic controls do not claim real producer agreement.
- **Boundary:** The pinned primary sources inspected so far do not prove the final Property-row mismatch; no acceptance correction is made.
- **Next:** Publish and read the exact property predicate from the normal Windows lifecycle before changing its closed contract.

### 2026-10-21 - Actual remaining rejection is ValidateProperties

- **Evidence:** Published `a6f9552` run `39528781933` returns stage 10/cause 26 with zero auxiliary fields and no protocol/diagnostic error. Manifest and payload validation return; ValidateProperties rejects.
- **Validation:** All 57 native Windows cases and both complete 699-warning MSI validations pass again. The first SDK-free Install still refuses; Recover reports NoRecoveryPending and cleanup has no failures.
- **Boundary:** No property values or table rows are logged, and no property policy is relaxed. The fresh specialist now compares only the property contract with the producer/finalizer.
- **Next:** Correct a proved producer/validator mismatch, or obtain only the exact remaining property predicate.

### 2026-10-21 - Remaining validator gains per-call numeric evidence

- **Changed:** A fresh C# specialist adds 14 fixed top-level validator source points, not guard-by-guard observations. Existing call/catch/order, exception identity, native evidence priority, policy and bounds remain. Independent source review approves the seven-file refinement.
- **Validation:** Parent passes 838 Setup cases, including 19 new real-denial/conversion/precedence controls, and 2,182 Core. Root/Windows cross-builds are warning-free; all static controls pass.
- **Boundary:** No producer/approval mismatch is proved from source. This diagnostic identifies the next existing validator call; it does not change media acceptance or certify installation.
- **Next:** Publish and consume one normal exact-head Windows result before choosing the root correction.

### 2026-10-21 - Real refusal is inside the remaining authoring validator

- **Evidence:** Exact published `497b461` run `39526687201` returns stage 10/cause 22 with zero fault/error fields and no protocol/diagnostic error. Candidate media read, authoring read and inventory validation return; the remaining validator, including its expected-exception conversion, rejects.
- **Validation:** All 57 native Windows cases and both complete 699-warning MSI validations pass again. Both supported OS jobs and Tray pass; the installed lifecycle still fails at first Install and publication is skipped.
- **Boundary:** Recover reports NoRecoveryPending; cleanup has no failures. Main remains unchanged and PR #30 remains draft.
- **Next:** A fresh C# specialist traces only the remaining validator steps, without further table-inventory probes or relaxed admission.

### 2026-10-21 - Candidate-media refusal gains five bounded source points

- **Changed:** A fresh C# author supplies five coarse numeric boundaries, preserving original exception identity/native evidence and all acceptance checks. Independent source review approves the bounded diagnostic; only tagged candidate MediaInvalid emits the new values.
- **Validation:** Nineteen new controls exercise real refusal/order/no-mutation, output privacy, exception priority and optional writer failure. A fresh C# author corrects nine test inputs that initially stopped at argument parsing; all assertions remain. Parent passes 819 Setup, 2,182 Core, warning-free root/Windows cross-builds and all static controls.
- **Boundary:** Codes identify media read, authoring read, inventory, remaining validator or resource construction; an inner predicate may still be unknown. Actual installed acceptance remains blocked.
- **Next:** Publish the diagnostic and read the next normal exact-head Windows refusal before choosing a media correction.

### 2026-10-21 - All native Signature cases pass; remaining media refusal isolated

- **Evidence:** Published `ebbab1f` passes all 57 native Windows cases, zero skips, in run `39523162657`. Both real MSI versions again pass complete native ICE with 699 exact warnings each. Both supported OS jobs and Tray pass; publication remains blocked.
- **Boundary:** SDK-free Install still returns Refused/MediaInvalid with no native lifecycle reports. Recover reports NoRecoveryPending and cleanup has no failures. A fresh C# specialist owns the remaining media source-point trace without relaxing admission.
- **Publication:** PR #30 remains draft and now records the current evidence; remote main remains the financial slice `e7758a3`. Retained reports total about 10.6 MB, with three-day expiry; two caches total about 80.6 MB.
- **Next:** Identify the first remaining media predicate, then resume normal exact-head Windows lifecycle acceptance.

### 2026-10-21 - Proved Upgrade key-header defect receives a fixture-only correction

- **Root evidence:** Actual `0e8c81b` header bytes select UpgradeCode/ActionProperty. Pinned WiX6 makes UpgradeCode/VersionMin/VersionMax/Language/Attributes the five keys and marks ActionProperty non-key.
- **Changed:** The source-approved fixture-only correction selects that genuine ordered key and shares the unchanged emitter with actual-file byte controls. No production, types, row inputs, assertion or 57-case selection change is made.
- **Validation:** Parent passes warning-free root/cross-build, 2,182 Core, 800 Setup and all static controls. Four new portable cases verify the actual single/composite and empty/populated header emission.
- **Next:** Publish and resume the normal exact-head Windows project/lifecycle; no native import success is inferred from source approval.

### 2026-10-21 - Exact rejected key-header bytes are the remaining native fact

- **Actual result:** Run `39519627685` at `7786a0f` reports 2216 / line 3 for all failing imports. Intended header computation alone does not justify changing emitted data.
- **Changed:** The source-approved test-only capture reads the actual third line and canonical schema table, with a 4096-byte total header bound and no row reads, after native error capture. Original exceptions and all 57 cases remain unchanged.
- **Validation:** Seventeen byte-exact portable cases pass; parent passes warning-free root/cross-build, 2,182 Core, 796 Setup and all static controls.
- **Next:** Publish, read the bounded native header, and select only a proved fixture-format correction.

### 2026-10-21 - Actual MSI 2216 narrows the missing fact to its line-number field

- **Actual result:** Run `39517899783` at `b86d1ee` emits numeric 2216 for all twelve common Import failures. Primary Microsoft documentation defines format error/Line field 4, not a file-open or duplicate-key diagnosis.
- **Changed:** The reviewed one-file continuation captures only that integer from the same record after confirming 2216, retaining original exceptions, associated xUnit output and fixed unknown handling.
- **Validation:** Parent passes warning-free root/cross-build, 2,182 Core and static controls. Production schema, assertions and all 57 cases are unchanged.
- **Next:** Publish, read only the failing native line number, and select the fixture-only correction from actual evidence.

### 2026-10-21 - Scalar native Import diagnostic is wired to xUnit output

- **Evidence:** Run `39516442512` at `3648771` repeats the common Import failure without any scalar/unknown diagnostic in the job log. No numeric error was inferred.
- **Changed:** A source-approved one-file continuation routes the unchanged scalar through per-test xUnit output, assigned before fixture creation. Capture/release/original exception, assertions, schema and all 57 tests are unchanged.
- **Validation:** Parent passes warning-free root/cross-build, 2,182 Core, metadata-only discovery and static controls. No native body ran locally and observed visibility remains for Windows.
- **Next:** Publish and read only the native Import identifier; no format or admission correction is chosen before that result.

### 2026-10-21 - Native fixture Import retains one numeric MSI error field

- **Actual failure:** Run `39514317262` at `7309a39` passes the prior 45 native cases; all 12 new cases fail common scratch Import before reader assertions. No production contract defect or justified IDT correction follows from the generic Automation message.
- **Changed:** A source-approved one-file test diagnostic captures only LastErrorRecord integer field 1, releases it, and preserves the original exception even if optional capture/output fails.
- **Validation:** Parent passes warning-free root build and 2,182 Core cases; portable Setup 779, cross-build and all static/payload 22/materialisation 7/actionlint checks pass. Assertions, production admission and all 57 test selections are unchanged.
- **Next:** Publish and read the numeric native Import identifier, then choose the fixture-only correction from that actual code.

### 2026-10-21 - Strict empty Signature correction is approved; normal lifecycle restored

- **Changed:** Candidate/retained/cache admission recognises only absent or exact-schema, zero-row Signature metadata. The FileName type is pinned to primary WiX6 nonlocalized s255/3583; any row or schema deviation remains a refusal and raw-HKCU searches are unchanged.
- **Review:** Fresh-author localization closure and integrated seven-file source receive final independent approval. The original author lockout remains respected.
- **Validation:** Parent passes warning-free root build, 2,182 Core and 779 portable Setup cases. The applier passes 318 Servicing, 48 Control and native-project cross-build; static/payload 22/materialisation 7/actionlint pass. Twelve native cases remain for actual Windows.
- **Cleanup:** All temporary probe code/hooks and the pre-fixture stop are removed, preserving the bounded actual evidence and restoring normal lifecycle/all publication gates.
- **Next:** Publish the strict correction with cleanup and run the normal exact-head Windows lifecycle; no installed acceptance is inferred from source or managed cases.

### 2026-10-21 - One fetch proves the validated Signature table is empty

- **Actual result:** Run `39509813811` at `ad46db9` preserves source/package hashes and returns query/fetch success with isEmpty=true, no native error. No row contents, profile fixture or installation are read/created.
- **Contract fact:** The same candidate's only unmatched table is Signature, and it contains no rows. This is a sufficient strict inventory rejection, not complete media acceptance or original reachability proof.
- **Ownership:** A fresh C# author now supplies a narrowly constrained present-but-empty schema correction. Arbitrary Signature data or relaxed search/ownership rules are not authorised.
- **Next:** Remove the temporary probe/stop, review the strict C# correction and resume exact-head Windows lifecycle acceptance.

### 2026-10-21 - Selected observation narrows to Signature emptiness only

- **Actual fact:** The first bounded native query finds 24 tables, matching source/receipt hashes, with only Signature outside the strict C# inventory. Table presence does not prove emptiness.
- **Changed:** The reviewed three-file seam now makes one read-only query/fetch for the empty/nonempty bit. It never reads record contents; failures, including close failures, remain unknown rather than inferred empty.
- **Validation:** Independent source review and executed controls pass without blockers. Parent passes warning-free root build, 2,182 Core and all static controls. Conservative CloseView handling stays unchanged.
- **Boundary:** No C# table allowance or install attempt is made. The existing source/package binding and pre-fixture terminal stop remain intact.
- **Next:** Publish one exact-head fetch, consume only that bit, remove the temporary stop and base the strict media correction on its actual result.

### 2026-10-21 - Temporary read-only candidate table observation is approved

- **Scope:** A single source/hash-bound native `_Tables.Name` query will test the exact C# closed-inventory count/membership predicate on the published 0.1.0 MSI. No contents, binary or owner paths are emitted.
- **Safety:** The workflow deliberately stops before fixture/installation and retains bounded JSON even on failure. This cannot satisfy lifecycle acceptance or distribution; removal of the temporary stop is required after observation.
- **Validation:** Independent review verifies source union/blobs, read-only mode, held leases, privacy, stop/upload wiring and real synthetic controls without blockers. Parent passes warning-free root build, 2,182 Core and all static controls.
- **Boundary:** This observation proves or eliminates only the selected predicate, not full media admission or whether the original execution reached it. The upstream duplicate-row guard is separate.
- **Next:** Publish one bounded Windows probe, inspect unmatched names, and select any strict media correction from actual data.

### 2026-10-21 - Actual independent package media passes; candidate MediaInvalid is next

- **Evidence:** Run `39502695162` at `5b06889` passes native 45/45 and both MSI validations. Each version's real intermediate and published-package role records show one link, with validated identity/hash checks.
- **Outcome:** Install moves past the non-unit-link layer and now returns MediaInvalid. Recover is NoRecoveryPending and cleanup is clean; no installed success is claimed.
- **Ownership:** A fresh C# specialist traces this reachable compiled-candidate media contract. Materialisation and admission guards remain unchanged.
- **Blockers:** Actual media acceptance and the full installed lifecycle remain unproved; PR #30 stays draft, main/distribution gated.
- **Next:** Identify the precise native media contract mismatch or its smallest read-only compiled-MSI observation.

### 2026-10-21 - Proved WiX copy default receives independent materialisation fix

- **Root boundary:** C# tracing confirms candidate source MSI is checked before pin/hash/database reads. Executing the pinned WiX6 copy target proves its default hard-link aliasing; a later intermediate rewrite can alter an earlier output.
- **Changed:** The source-approved five-file non-C# correction enforces independent copies/effective policy, fresh outputs, isolated intermediates and ICE-receipt-bound identity/hash checks. C# and its unit-link admission predicate are unchanged.
- **Validation:** Independent review executes all seven materialisation controls and the real SDK Copy task, including refusing a global hard-link override before writing output. Parent passes all static controls, warning-free root build and 2,182 Core cases.
- **Boundary:** No historical Windows count of two or successful installation is claimed. Non-blocking review suggestions do not reopen this patch.
- **Next:** Publish and run the exact-head Windows lifecycle with correctly materialised candidate media.

### 2026-10-21 - One native probe identifies image link-count rejection

- **Evidence:** Run `39499387931` at `19f91ad` passes native Windows 45/45 and both MSI validations. First Install reports v1 cause 12 with no OS error: the existing image link-count-not-one invariant. Recover is NoRecoveryPending and cleanup is clean.
- **Boundary:** The record does not yet identify the file/input role or zero-versus-multiple links. The guard is not relaxed and no installation success is claimed.
- **Ownership:** A fresh C# scope traces reachable input roles; a separate fresh non-C# scope verifies SDK/publication materialisation. They do not duplicate each other's trace.
- **Preserved contract:** Any proved publication correction must retain content hashes, identities, validation receipts, baseline/upgrade independence and all alias/admission checks.
- **Next:** Correct the proved input materialisation boundary, or obtain only the bounded role/count fact needed to establish it.

### 2026-10-21 - Finite source-point seam is approved for one Windows probe

- **Changed:** Nine existing managed rejection points now retain a typed discriminator through the same v1 cause field. Managed tags carry no OS error; genuine native evidence takes precedence. Exception type/reason, checks/order, catches and publication/admission policy are unchanged.
- **Review:** Fresh independent source review approves only this finite evidence seam and compatible parser bounds. No root fix or broader diagnostics design is introduced.
- **Validation:** Parent passes warning-free root build, 2,182 Core, 732 Setup, 318 Servicing and all static controls. Legacy 2,599/57 parser controls plus nine legal and 367 adverse new records pass; the applier also passes 48 Control and Windows cross-build.
- **Blockers:** The first-install invariant remains unknown until the next native record. No installed success, absent-MSI-call claim or acceptance waiver is made; PR #30/main/distribution remain gated.
- **Next:** Publish and run one exact-head Windows probe, then choose the behavioural correction from its invariant.

### 2026-10-21 - Bare admission constructors need a finite source-point discriminator

- **Evidence:** Run `39494190534` at `bb9c02f` passes both supported jobs, native 41/41 and both MSI validations. Install emits Entry/ExpectedException/HRESULT `0x80131500`; Recover remains NoRecoveryPending with clean cleanup.
- **Source conclusion:** On the inspected production route this is the outer AdmissionException without retained native evidence. Several existing bare InvalidState invariant branches still produce the identical record; no behavioural fix is justified.
- **Scope decision:** The prior author stops after its final cycle. A fresh narrow task tags only those existing rejection points with a finite typed cause, reusing v1 fields and capture. Causes 1–9 cannot honestly carry that distinction, so matching bounded vocabulary/parser controls are necessary; no new wire version/fields or OS-error reinterpretation is authorised.
- **Preserved contract:** Exception type/reason, catches, path/link/ancestry/publication checks and refusal policy remain unchanged.
- **Next:** Review only the source-point seam, publish it and use one exact-head Windows run to identify the invariant before selecting a behavioural correction.

### 2026-10-21 - Existing v1 evidence now covers Unavailable return sites

- **Changed:** The source-approved seven-file continuation emits retained Unavailable metadata and annotates existing admission/outer-I/O return sites. Native error precedence, Busy classification, report/exit/readiness, catch boundaries and optional-writer handling remain unchanged.
- **Scope:** The broader v2 proposal was discarded without application. No new schema, native-field suite, tracing framework or PowerShell parser change is introduced.
- **Validation:** Parent passes warning-free root build, 2,182 Core, 712 Setup and all static controls. The applier passes 48 Control, 297 Servicing and Windows cross-build, with zero failures/skips. Narrow independent review approves source only.
- **Blockers:** The Unavailable origin and installed lifecycle remain unproved; a handling-boundary record is not a root-cause or absent-MSI-call claim. PR #30 remains draft and main/distribution gated.
- **Next:** Publish the minimal v1 continuation and use the next exact-head Windows record to select the behavioural correction.

### 2026-10-21 - All 41 native shortcut/kernel cases pass; next refusal is Unavailable

- **Evidence:** Run `39489798798` at `aa4f3df` passes the complete Windows project 41/41 with zero skips, including real COM/missing-ancestor and adversarial physical-path cases. Both MSI versions pass complete native validation and mutation controls.
- **Outcome:** First Install advances from shortcut InvalidEvidence to Unavailable. Failure-Recover reports NoRecoveryPending, with no cleanup failures. Refusal/ready=false/null MSI result remain explicit; no successful install or proof of absent earlier MSI calls is claimed.
- **Ownership:** A fresh C# specialist traces this distinct Unavailable origin without reopening proved shortcut or validation contracts.
- **Blockers:** Full installed authentication, repair/upgrade/recovery/uninstall acceptance remains outstanding. PR #30 is draft; main/distribution remain gated.
- **Next:** Locate the new Unavailable refusal and continue the exact-head Windows lifecycle.

### 2026-10-21 - Shortcut absence correction is source-approved

- **Changed:** The concrete shortcut observer proves physical absence before COM loading, using two physical identity and missing-metadata observations. Unsafe/unknown/reparse/collision states remain refusals; present-file loading failures retain numerical unreadable evidence.
- **Review:** Fresh independent review approves only the five-file correction. It does not establish the original throwing call or installed success.
- **Validation:** Parent passes warning-free solution build, 2,182 Core and 693 Setup cases and all static controls. The applier passes 26 focused, 48 Control and 297 Servicing cases plus the Windows-project cross-build, preserving BOM/LF and unrelated bytes.
- **Blockers:** New native cases and actual SDK-free installation/recovery remain unexecuted for this candidate. PR #30 stays draft; main/distribution remain gated.
- **Next:** Publish the correction and run the exact-head complete Windows project and installer lifecycle.

### 2026-10-21 - Native numeric evidence locates initial shortcut observation

- **Evidence:** Run `39486421130` at `82cbb13` again passes both real MSI validations and native/mutation controls. Install and failure-Recover both retain numeric evidence `8/4/5/2/2147942403`: initial inventory, shortcut observation, HRESULT `0x80070003`.
- **Preserved outcome:** Original 1603 / Refused / ready=false / null MSI result and cleanup refusal remain explicit, with no report/diagnostic protocol error. No installed success is inferred.
- **Ownership:** A fresh C# specialist traces the exact observer and physical-absence boundary. Missing-path errors must not hide inaccessible, redirected or dangling ancestors.
- **Blockers:** Exact path/API classification and full installed lifecycle remain unaccepted; PR #30 is draft and main/distribution gated.
- **Next:** Apply a proved shortcut-observation correction, then rerun the dedicated Windows lifecycle.

### 2026-10-21 - Diagnostic-only Setup refusal evidence receives source approval

- **Changed:** Fixed numerical stage/cause/fault/native-error evidence now reaches the bounded acceptance report without paths, messages, identities or tokens. Install and subsequent recovery evidence remain distinct; the original report, refusal and readiness contract is unchanged.
- **Review:** A fresh author closes an optional-writer failure that could mask the original refusal. Final independent review approves the diagnostic-only integration; prior author lockouts remain enforced.
- **Validation:** Parent passes warning-free solution build, 2,182 Core and 667 Setup cases plus all static controls. The applier passes 53 focused, 48 Control and 297 Servicing cases. Parser controls cover 2,599 numerical combinations/boundaries, 57 adverse inputs, real synthetic child stderr and actual stage persistence.
- **Blockers:** The native first-install origin remains unknown until fresh target evidence. No behavioural correction, successful installation or proof of absent MSI calls is claimed; PR #30 stays draft and main/distribution gated.
- **Next:** Publish the bounded evidence integration and repeat dedicated hosted-Windows lifecycle acceptance.

### 2026-10-21 - Both real MSI packages pass full validation; install evidence is refused

- **Native progress:** Run `39479960150` at `4532cd7` passes native Windows 31/31 and actual copied-MSI mutation refusals. Baseline 0.1.0 and upgrade 0.1.1 both build and pass complete authenticated native ICE with 699 prerequisite-checked policy warnings each, zero build warnings/errors.
- **Current failure:** The owner fixture is created, then SDK-free Setup Install returns 1603 / Refused / InvalidEvidence with no MSI exit code. Cleanup recovery reports the same refusal and cannot establish completed safe state. The bounded acceptance report preserves both outcomes.
- **Ownership:** A fresh C# specialist traces the actual refusal origin or provides only the bounded evidence needed to locate it. No speculative guard/elevation/path relaxation is authorised.
- **Blockers:** No successful installation, installed authentication, recovery or uninstall proof exists yet. PR #30 remains draft and main/distribution gated.
- **Next:** Locate and correct the specific first-install refusal, then continue the full target-host lifecycle.

### 2026-10-21 - Real native ICE advances to a blank-line binding correction

- **Evidence:** Run `39477575313` at `8c17a15` passes both supported jobs, Tray, native Windows 31/31 and actual copied-MSI filename/reparenting refusal. Authenticated full ICE emits one ICE61, one ICE63 and 697 ICE91 warnings, without the original ICE64/72 errors. The wrapper then refuses an empty `Lines` element before classification; no installation succeeds.
- **Changed:** The eligible author corrects only line-array binding and shared stdout/stderr capture. Independent streams cannot concatenate into a forged diagnostic, and empty output still fails completeness.
- **Validation:** Narrow independent review approves the correction. Six valid and ten rejected real-child-process controls pass, as do all static/payload 22/adverse 28/provenance/path/policy/actionlint checks. Parent passes warning-free solution build and all 2,182 Core cases.
- **Blockers:** Fresh-head classifier receipt, complete installed lifecycle and native recovery remain unaccepted. C#/trust/hash/normal-ICE contracts are unchanged; PR #30 remains draft and main/distribution gated.
- **Next:** Publish the bounded adapter correction and continue actual Windows acceptance.

### 2026-10-21 - Complete ICE correction receives integrated source approval

- **Changed:** Fresh authors close validator provenance, actual MSI file/path binding and C# target-name/resource consistency. Media, authoring and catalog consumers share strict bare/`short|long` interpretation; exact identities, fixed root, native guards and empty-directory-only cleanup remain enforced.
- **Review:** Final independent source verdict approves the integrated correction and closes all three findings. Prior author lockouts remain respected; no target success is inferred.
- **Validation:** Parent passes warning-free solution build, 2,182 Core and 614 Setup cases, all static/payload 22/adverse 28/provenance/path/policy controls and workflow lint. Mechanical application audit and 114 focused/48 Control passes also succeed. The AUX fixture is corrected without relaxing reserved-name refusal.
- **Blockers:** Actual hosted MSI mutation controls, complete ICE, native readback/advertisement refusal and full installed lifecycle remain unaccepted. PR #30 stays draft; main/distribution remain gated.
- **Next:** Publish this complete source-approved candidate and execute independent Windows acceptance alongside both supported platform gates.

### 2026-10-21 - Validator provenance and path findings close; alias integration remains

- **Source progress:** Independent cycle-2 review closes the fresh policy's pinned-validator provenance and complete actual MSI file/path binding findings. Exact default ICE execution, strict diagnostic handling and unchanged-input binding remain required.
- **Remaining source blocker:** Legitimate MSI `short|long` names are accepted by the gate/media reader but still compared or propagated raw by C# consumers. A fresh language specialist owns the rejected target-name/resource-resolution scope; prior authors do not advise it.
- **Validation:** Parent directly passes static/payload 22/adverse 28/policy/provenance/path controls, warning-free solution build, 2,182 Core, 537 Setup and 48 Control cases. Hosted actual mutation/ICE/install controls remain unexecuted for this tree.
- **Blockers:** Final source integration approval and complete Windows lifecycle acceptance remain outstanding. PR #30 stays draft; main/distribution are unchanged.
- **Next:** Integrate and review the fresh alias correction, then publish the complete candidate for actual Windows proof.

### 2026-10-21 - C# authoring contract is source-approved; ICE policy needs fresh revision

- **Changed:** The language specialist's nine-file patch is mechanically applied and independently source-approved: exact Type19 advertisement refusal with separate SQL-null observation, plus complete manifest-ancestor removal identities and preserved-order descendant ownership. Native guard/sequence, registry/keypath, media and rollback guarantees remain intact.
- **Validation:** Setup Release builds pass with zero warnings/errors; 537 Setup and 48 Control tests pass with zero failures/skips. Byte-for-byte patch application is verified; only the required BOM is added to two new C# test files afterwards, preserving source payload.
- **Review blocker:** Independent review rejects the separate Stage1 policy's executable provenance and actual filename/path binding. The original policy author is locked out; a fresh eligible author owns correction and its proof tests/documentation.
- **Native boundary:** No new Windows/MSI/profile execution or complete installer success is claimed. PR #30 stays draft; main and distribution remain gated.
- **Next:** Review the fresh Stage1 revision, then run the complete corrected native validation and installed lifecycle.

### 2026-10-21 - Actual package identity passes and full ICE reaches authoring gaps

- **Evidence:** Exact-head run `39467659071` at `cb020df` passes both supported OS jobs, all 61 browser journeys per OS, Tray and native Windows 31/31. Windows coverage is 85.9862% line / 61.5899% branch; macOS is 85.7118% / 61.2494%, retaining the complete denominator.
- **Native progress:** The first MSI passes selected-GUID retention and fresh read-only persisted PackageCode verification. Full ICE now executes: ICE64 finds two missing profile ancestor cleanup rows; ICE72 rejects the advertised-sequence DLL. Elevated ICE61/63/91 warnings expose the removal-range, fence-placement and fixed-per-user authoring boundary.
- **Ownership:** Fresh installer and C# specialists own the corrections and independent authored-table checks. The original installer-author lockout remains respected; no validation suppression, unknown-warning acceptance or weaker servicing contract is authorised.
- **Blockers:** Complete ICE and actual installation/native recovery remain unaccepted. Main stays at the verified financial slice; PR #30 remains draft and distribution gated.
- **Next:** Integrate the fresh reviewed authoring correction and rerun the independent native lifecycle.

### 2026-10-21 - Independent native acceptance no longer waits on Mac runner allocation

- **Evidence:** Run `39464582678` passes Windows. The supported Mac job has zero executed steps and is cancelled by hosted-runner allocation failure; one unchanged failed-job retry is requested. MSI acceptance never starts.
- **Decision/change:** Run self-contained installer acceptance independently: it builds all inputs on its own disposable Windows host and consumes no other job's artefacts. Keep publication dependent on both supported platform jobs, Tray and complete installer acceptance. No acceptance, promotion or merge condition is removed.
- **Validation:** Static graph controls enforce independent input construction and the unchanged complete publication dependencies. All 22 payload and 28 adverse controls and workflow lint pass. The solution builds with zero warnings/errors and all 2,182 F# cases pass.
- **Blockers:** Setter retention, committed PackageCode, complete ICE and the full installed lifecycle remain unaccepted; Mac infrastructure must also recover before merge.
- **Next:** Publish independent scheduling and capture actual native committed-file verification without waiting for unrelated runner capacity.

### 2026-10-21 - Null GUID is located in cached summary readback

- **Evidence:** Run `39460550649` passes both supported jobs, Tray and native Windows 31/31. Explicit context locates the conversion at finaliser line 80, `summary-package-readback`: the writable summary's PackageCode getter returns null after persist/commit. Product/scope, all 697 file/component identities and summary platform/WordCount have passed.
- **Changed:** Require the setter to retain the selected GUID, close writable handles after commit and verify the persisted MSI through fresh read-only database/summary handles. Missing/malformed/different identities still fail; finalisation remains before complete ICE.
- **Validation:** Both exact GUID guards pass matching identity and reject four adverse values. Static ordering/context controls, 22 payload and 28 adverse report cases pass. The solution builds with zero warnings/errors and all 2,182 F# cases pass.
- **Blockers:** Actual Windows must prove setter retention and persisted identity before ICE and installed lifecycle acceptance can advance. Main/distribution remain gated; no component identity defect or successful install is claimed.
- **Next:** Publish committed-file verification and continue actual Windows finalisation/lifecycle acceptance.

### 2026-10-21 - Initial finaliser diagnostic phase is kept in script scope

- **Changed:** Review catches the initial diagnostic phase assignment in helper-local scope. Move it before the outer MSI try block and enforce its exact scope/order, preventing an early failure from being masked by strict-mode variable lookup.
- **Validation:** Package-layout and original-exception-identity controls pass. The solution builds with zero warnings/errors and all 2,182 F# cases pass.
- **Blockers:** Native finalisation and the installed lifecycle remain unaccepted; no identity rule or acceptance gate changes.
- **Next:** Cancel the superseded diagnostic run and execute the corrected exact-head native gate.

### 2026-10-21 - Supported platforms pass; finaliser failure gets explicit context

- **Evidence:** Run `39456791213` passes both complete supported platform jobs, including all 61 browser journeys, plus all 31 Windows servicing cases. Normal error view still omits the native null-GUID conversion's source line; actual installation remains unstarted.
- **Changed:** A failure-context catch records the fixed finalisation phase, invocation line and bounded stack frames, then rethrows. Identity/scope/inventory/summary checks, mutation ordering, complete ICE and lifecycle gates are unchanged.
- **Validation:** The exact catch body reports a controlled phase and preserves the original exception object. All 22 payload and 28 adverse report controls pass; the solution builds with zero warnings/errors and all 2,182 F# cases pass.
- **Blockers:** The actual native failing phase is still unknown. No package promotion, installed-runtime acceptance or main merge is claimed; strict legacy repair remains disabled.
- **Next:** Capture explicit native phase/stack evidence and fix the demonstrated finalisation cause.

### 2026-10-21 - Browser evidence capture and native-message ordering are made causal

- **Evidence:** Run `39452815857` first exposes a Windows native-navigation fixture racing Web Crypto. Both attempts fail macOS palette sync while Chromium's debugging protocol cannot retrieve the real response body; installer acceptance is skipped, so no new finaliser location is available.
- **Changed:** Native unit cases await actual posted-message notifications under a bounded test deadline. Sync acceptance buffers the real Service response before forwarding it unchanged, with no redirects or retries. All authority, wire, acknowledgement, one-request/one-cycle, persisted-content and visible-result assertions remain.
- **Validation:** All 102 JavaScript units, lint, build and 61-case discovery pass. Both focused real-Service sync cases and all 61 browser cases pass locally. The canonical bundle hash is unchanged; the solution builds without warnings/errors and all 2,182 F# cases pass.
- **Blockers:** Actual finalisation/ICE and the full installed lifecycle remain unaccepted. The unchanged diagnostic rerun is retained as evidence, not treated as a repair or substituted for current-head proof.
- **Next:** Publish the fixture corrections and continue the exact-head native finalisation/lifecycle gate.

### 2026-10-21 - MSI conversion failure needs its exact source location

- **Evidence:** Hosted run `39448288911` passes both supported platform jobs, Tray and all 31 native Windows servicing cases. MSI finalisation now reports a null-to-GUID conversion without identifying the cast; the failing phase is not yet established.
- **Changed:** The finaliser selects the normal PowerShell error view so conversion failures retain their source location. No identity, inventory, scope, summary, ICE or lifecycle check changes.
- **Validation:** A controlled null-GUID child script retains its exact failing line and non-zero exit. Package-layout controls pass; the solution builds with zero warnings/errors and all 2,182 F# cases pass.
- **Blockers:** Actual finalisation, ICE and the complete installed-native/MSI lifecycle remain unaccepted. Main and distribution remain gated; strict legacy repair remains disabled.
- **Next:** Capture the exact native failing cast and correct its demonstrated cause.

### 2026-10-21 - MSI row helper output is isolated from bookkeeping results

- **Evidence:** Hosted run `39443249566` reports exactly `ProductCode: rows=3`, not a proven GUID mismatch. All platform, payload and native-kernel gates remain green.
- **Changed:** The MSI row helper explicitly discards `Execute`/`Close` bookkeeping output; the related identity reader discards `Execute` output too. Returned rows, strict cardinality/value checks and all finalisation/ICE requirements remain unchanged; exceptions are not suppressed.
- **Validation:** Synthetic pipeline controls reproduce the three-output symptom, preserve exactly one row after suppression and prove query exceptions still fail. Static contracts require the actual casts. All 22 payload and 28 adverse controls pass. The solution builds without warnings/errors and all 2,182 F# cases pass.
- **Blockers:** The next Windows run must confirm actual COM row output and continue through finalisation, ICE and the complete installed lifecycle. No package is promoted and main stays gated.
- **Next:** Publish row-only automation output and exercise the real Windows finalisation again.

### 2026-10-21 - MSI identity disagreement is isolated to its property comparison

- **Evidence:** Hosted run `39437427176` preserves all passing platform, payload and native-kernel gates. Visible finalisation output now identifies the strict built-MSI product-property comparison as the failure, before summary finalisation or ICE validation.
- **Changed:** The same refusal reports which selected build-identity property, row count, actual type and expected/actual value differ. The comparison and all subsequent identity/scope/file/component/summary checks are unchanged; no owner or archive data is queried.
- **Validation:** An exact-branch controlled non-MSI probe retains equality success and mismatch failure with precise comparison evidence. Static controls pass 22 payload and 28 adverse cases. The solution builds without warnings/errors and all 2,182 F# cases pass.
- **Blockers:** The mismatched property/value or row shape is not yet established. No package identity correction, actual installation or installed-runtime acceptance is claimed.
- **Next:** Capture the precise native comparison result, fix its demonstrated cause and retain full finalisation/ICE/lifecycle gates.

### 2026-10-21 - All native kernel cases pass; MSI finalisation error is made visible

- **Evidence:** Hosted run `39431966923` passes both supported platform jobs and all 31 actual Windows servicing cases, zero skips. Lifecycle construction reaches package-identity finalisation before normal ICE validation, then its child command exits 1. Quiet MSBuild output retains only the wrapper error; bounded acceptance evidence records failure during build and no cleanup failure.
- **Changed:** The build wrapper uses minimal verbosity and gives finalisation stdout/stderr high importance. Ordering, package identity rules, complete validation and failure exit remain unchanged. No speculative finalisation fix is applied.
- **Validation:** A controlled local non-MSI MSBuild failure proves quiet logging hides the original exception and the new setting exposes it without converting failure to success. Static validation passes all 22 payload and 28 adverse controls and requires visible diagnostics. The solution builds warning-free and all 2,182 F# cases pass.
- **Blockers:** The original Windows finalisation error must be observed and fixed before the first actual install and full installed-native/MSI lifecycle. Main and distribution remain gated; strict legacy repair stays disabled.
- **Next:** Publish visible build diagnostics, capture the actual finaliser failure and correct its established cause.

### 2026-10-20 - Real kernel suite reaches one incorrect native-spelling premise

- **Evidence:** Hosted run `39427186591` passes the complete payload-construction step, then runs all 31 Windows servicing cases. Thirty pass; the intermediate-directory trailing-space row fails its native alias precondition with exact Win32 error 3 before inventory validation.
- **Changed:** A C# specialist separates positive Win32 aliases from the native-missing spelling. All four rows still require strict invalid-inventory refusal and unchanged bytes; known aliases retain open/content proof, while the missing case requires exact path-not-found. No production path policy or case is removed.
- **Validation:** The Windows test project cross-builds without warnings/errors and all 297 portable cases pass. The solution builds cleanly. A transient local post-shutdown port-bind failure was investigated through the focused six-case Host selection and a full unchanged-code rerun; all 2,182 cases pass. No CI retry or safety assertion is weakened.
- **Blockers:** Windows must rerun the 31-case project and proceed through the complete installed-native/MSI lifecycle before main merge or distribution. Strict legacy repair stays disabled.
- **Next:** Publish the precise native expectation and continue actual installed acceptance.

### 2026-10-20 - Both supported platform gates pass; real payload mismatch is repaired

- **Evidence:** Hosted run `39423147741` passes both complete supported jobs. Windows runs 2,178 F# and 297 Servicing/model cases plus 61 actual browser journeys at 85.9862% line / 61.6588% branch coverage; Mac runs 2,182 and 297 plus 61 journeys at 85.7118% / 61.2534%. The full denominator remains 17,126 lines / 24,668 branches.
- **Changed:** Actual installer construction publishes the native guard and helpers, then finds a strict inventory mismatch. A non-executing local cross-publish establishes the missing SDK-declared endpoint manifest: 419 Service files against 418 inventory entries. Capture now hashes that SDK intermediate with its declared output identity, never candidate bytes, and avoids duplicate resolved entries.
- **Validation:** All 22 payload and 28 adverse report controls pass. The genuine Service/Tray Windows cross-publish passes unchanged strict validation for all 697 runtime files. The full solution builds without warnings/errors and passes all 2,182 F# cases. No Windows binary, MSI or profile operation ran locally.
- **Blockers:** The actual Windows construction and complete installed-native/MSI lifecycle still must pass before merge or distribution. Strict legacy repair remains blocked and disabled.
- **Next:** Publish the provenance-preserving inventory correction and continue the actual installer lifecycle gate.

### 2026-10-20 - Windows behaviour passes; coverage shutdown handshake is repaired

- **Evidence:** Hosted run `39418524069` passes the Windows Core test phase and all 61 actual browser journeys. Setup 456, Control 48, Servicing/model 297 and Tray 319 also pass. Only coverage fails, reporting 3,592/17,126 lines with in-process modules missing broad hit data; the Mac graph remains green.
- **Changed:** Primary Coverlet 6.0.2 documentation identifies VSTest's early host shutdown as a known hit-loss problem. CI now enables only the pinned in-process flush handshake while retaining native instrumentation, JSON merging, all five assemblies and exact 85/60 gates. Fresh named TRX reports are independently validated without publishing their contents.
- **Validation:** A controlled tracker log proves the in-process collector actually flushes instrumented modules. The exact workflow passes 297 Servicing/model and 2,182 F# cases at 14,683/17,126 lines (85.7351%) and 15,197/24,668 branches (61.6061%). Seven coverage and eight execution-metadata controls pass; the full solution build is warning-free and all 2,182 Debug cases pass. Static installer controls retain 19 payload and 28 adverse report passes.
- **Blockers:** Windows must confirm complete hit retention before the installed-native/MSI lifecycle can run. Main and distribution stay gated; strict legacy repair remains disabled.
- **Next:** Publish the collection repair and rerun actual Windows coverage followed by the installed lifecycle gate.

### 2026-10-20 - Windows startup recovers; surplus-write classification matches the runtime

- **Evidence:** Hosted run `39415995186` passes the repaired startup/canonical preflight, MCP and drain cases. Its sole Windows failure is the pipe fixture's surplus-write classifier after valid first-response and closure proof. The complete Mac job, Tray and compatibility jobs pass; installed acceptance remains skipped.
- **Changed:** Primary .NET 9.0.15 source establishes the exact three HRESULTs that its pipe implementation maps to closed-pipe results. The fixture accepts only those uncancelled Windows outcomes after its existing proof and expands positive/negative cases; production code, deadlines, dispatch policy and task cleanup are unchanged.
- **Validation:** Independent F# review approves the narrow classification/matrix delta. All 26 focused and 2,182 full F# cases pass locally, zero failures/skips; the solution builds without warnings/errors. Exact native collection passes 297 Servicing/model cases and retains five assemblies at 14,683/17,126 lines (85.7351%) and 15,134/24,668 branches (61.3507%).
- **Blockers:** Current-head Windows coverage/browser and the complete installed-native/MSI lifecycle remain unaccepted. Main and distribution stay gated; strict legacy repair stays disabled.
- **Next:** Push this final known Core fixture correction and run the actual Windows and installed lifecycle gates.

### 2026-10-20 - Profile and full-duplex fixture candidate passes all local gates

- **Changed:** The isolated native-profile helper and full-duplex pipe fixture are complete. A fresh compiler specialist replaces only two problematic resumable loops with deferred, monomorphic sequential checks; it preserves every matrix case, ordering and fault identity without suppression or parallel execution.
- **Validation:** The solution builds with zero warnings/errors and passes all 2,182 F# cases, zero failures/skips. All 11 profile/actual-child and 26 pipe-focused cases pass locally. Exact native collection retains five assemblies at 14,683/17,126 lines (85.7351%) and 15,258/24,668 branches (61.8534%) while passing 297 Servicing/model cases and unchanged 85/60 gates.
- **Review:** Independent fixture source approval retains all first-response/identity/exact-dispatch/closure and cancellation-settlement requirements. The profile fix changes only owned temporary directories and child environment; production admission and pipe semantics remain unchanged.
- **Blockers:** Actual Windows must verify the known-folder and pipe corrections. Installed-native/MSI acceptance and main merge remain gated; strict legacy repair remains disabled.
- **Next:** Push the clean fixture candidate and run the complete Windows/macOS acceptance graph.

### 2026-10-20 - Native profile cause is confirmed; pipe compiler issue is escalated

- **Evidence:** Hosted run `39411699226` proves the pipe/pool changes removed all drain failures, while eight children report the exact numeric cause `Composition / KnownFolder / EmptyKnownFolder`. Windows Servicing/model 297, Setup 456, Control 48 and Tray 319 pass; the Mac job remains green.
- **Changed:** Both child launchers now share a helper that materialises only their owned synthetic Local/Roaming folders and matching child environment. A separate pipe-fixture revision uses concurrent read/write issuance rather than waiting for surplus-write completion; production admission and pipe code are unchanged.
- **Validation:** All 11 profile/actual-child cases pass locally. Independent source review approves both fixture revisions. The pipe matrix remains unexecuted because two FS3511 compiler warnings persist after the original author's three focused attempts.
- **Blockers:** A fresh specialist owns the narrow static-compilation correction. No suppression, timeout increase, assertion weakening or unpublished-build success is claimed. Windows and installed/native acceptance still gate merge.
- **Next:** Obtain a warning-free fixture build, execute the local matrix and rerun the real Windows gate.

### 2026-10-20 - Typed startup evidence preserves admission and passes independent review

- **Changed:** Added a first-failure capture with fixed numeric fields to current-user composition and existing native path/gate operations. Immediate native error capture and a path-free line identify the failing operation without new probes, caller callbacks or changed admission decisions. The F# adapter preserves its first marker and exit 78 and emits evidence only afterward for Unavailable.
- **Review:** Independent C# and F# reviews approve the narrow interpreter and adapter changes. Mechanical application is audited against all eight literal handoff sections; no unrelated code, imported paths or lease/generation policy changed.
- **Validation:** All 297 Servicing/model and 2,170 F# cases pass locally, zero failures/skips; the focused startup/pipe/drain selection passes 33. Setup passes 456 and Control 48. Four native helper/Guard cross-builds and the solution are warning-free. Static validation passes 19 payload and 28 adverse report controls. Exact five-assembly coverage is 14,683/17,126 lines (85.7351%) and 15,225/24,668 branches (61.7196%), with unchanged 85/60 gates.
- **Blockers:** The eight real Windows startup refusals still have no established cause. The next target run must supply the numeric evidence and verify the pipe/pool fixture corrections. Main, installer distribution and strict legacy repair remain gated.
- **Next:** Publish the reviewed diagnostic candidate, then correct the operation actually reported by Windows rather than relaxing admission.

### 2026-10-20 - Pipe and SQLite fixture causes are established; startup stays unresolved

- **Evidence:** Hosted run `39407281492` confirms the MCP path correction and leaves 12 failures. Three drain stacks fail only at fixture directory deletion, the pipelining stack fails at its initial two-frame write, and eight children exit 78 before canonical preflight with only `HERMES_STARTUP_UNAVAILABLE`.
- **Changed:** A fresh F# revision clears only each completed fixture's own SQLite pool and gates synthetic pipe dispatch on explicit write completion. Strong lifetime, first-response, exact-dispatch and closure assertions remain; new regressions cover pool isolation and latch cancellation. Production database/pipe behaviour is unchanged.
- **Validation:** All 21 focused and 2,165 full Release cases pass locally, zero failures/skips. The first mechanical application hit a repeated dispatch hunk; anchoring it to the intended test restored the untouched partial-write case before these successful runs.
- **Blockers:** The startup message conflates distinct native/composition failures. A C# specialist is adding bounded typed evidence without paths, environment values or changed admission decisions; no root cause is guessed and no Windows success is claimed.
- **Next:** Integrate refusal evidence, identify the actual startup operation on Windows and retain the complete installed/native gate before merge.

### 2026-10-20 - Windows enrolled gate passes; Core diagnostics remain fail-closed

- **Evidence:** Hosted run `39403692526` at `9d40f4c` passes Windows Servicing/model 251, Setup 456, Control 48 and Tray 319. The complete Mac job remains green. Windows then exposes 14 F# failures in child startup/canonical preflight, owner-pipe closure, request drain and two MCP ownership scenarios, so installed acceptance stays skipped.
- **Changed:** A fresh F# specialist supplies one fully qualified archive root throughout the MCP fixture and exercises both ownership cases through immediate/yielding interpreters without weakening assertions. Separate bounded early-child diagnostics and sanitised CI failure messages expose the missing Windows evidence while retaining existing deadlines, authority checks and failing exits.
- **Validation:** All 21 focused and 2,163 full F# cases pass locally, zero failures/skips; the solution builds without warnings/errors. The exact recipe passes at 14,533/17,018 lines (85.3978%) and 15,104/24,636 branches (61.3087%), with all five assemblies and unchanged 85/60 gates. Synthetic workflow checks prove message/frame caps, authority masking and no raw captured-output publication.
- **Blockers:** Twelve Windows lifecycle/boundary failures still require actual detailed evidence; the MCP fixture correction also awaits target confirmation. Main and installer publication remain gated. Strict legacy repair stays disabled.
- **Next:** Run the corrected fixture and privacy-preserving diagnostics on Windows, then fix established lifecycle causes rather than guessing or extending budgets.

### 2026-10-20 - Hosted Mac acceptance passes; Windows enrolled fixture is corrected

- **Evidence:** At head `4caf4fe`, hosted run `39402069802` passes the complete macOS job: 102 JavaScript units, 61 actual browser journeys, Setup 456, Control 48 and exact coverage of 14,533/17,018 lines (85.3978%) and 14,997/24,636 branches (60.8743%). Windows passes Tray 319, Setup 456 and Control 48, then repeats the five enrolled-service fixture failures before Core/browser acceptance.
- **Changed:** A fresh C# specialist identifies mixed native/protocol separators in fixture paths. The correction uses native absolute image paths, refuses unexpected metadata inputs, adds both apphost-spelling cases and retains the strict loaded-generation/order assertions. Production policy is unchanged. Coverage diagnostics now retain an available partial report without treating it as combined proof.
- **Validation:** All 22 focused and 251 portable Servicing/model cases pass locally. The updated exact recipe retains the five-assembly denominator and passes at 14,533/17,018 lines (85.3978%) and 15,126/24,636 branches (61.3980%). The default solution builds without warnings/errors and passes all 2,159 Debug cases.
- **Blockers:** Current-head Windows Core/browser and complete installed-native/MSI acceptance remain pending. The draft PR and main merge remain gated; strict legacy repair stays disabled.
- **Next:** Publish the fixture correction and run the actual Windows instrumented phase before proceeding to installed acceptance.

### 2026-10-20 - First hosted candidate exposes a pairing-test scheduling assumption

- **Evidence:** The reviewed candidate is pushed at `4683cf3` and draft [PR #30](https://github.com/johnazariah/hermes/pull/30) targets main. Its canonical main ancestry was recorded with the candidate tree unchanged. Hosted run `39400527124` passes Tray and both compatibility jobs, but macOS fails one of 102 JavaScript units before backend/browser acceptance.
- **Changed:** The late-pairing cancellation test now waits for actual deferred-fetch entry instead of assuming one event-loop turn completes Web Crypto. It still cancels an outstanding create, delivers its late response and asserts that no pairing is published.
- **Validation:** The focused regression, lint and all 102 units pass locally. Production code and the canonical asset bundle are unchanged.
- **Blockers:** Current-head Windows/macOS and installed-native/MSI acceptance remain pending; main is unchanged and strict legacy repair remains disabled.
- **Next:** Publish the test-only correction and rerun the complete target-host gates.

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
