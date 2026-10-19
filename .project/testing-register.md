# Hermes — Testing Register

> **Rule**: Update this file whenever tests are added, removed, or modified.

## Canonical React and owned lifecycle — approved for publication

The #9/#8 candidate replaces the conflicting Blazor root with a validated
deployed React bundle, makes build/publish provenance explicit, wires the
existing Chat and Pipeline routes, and restores real browser execution.
Owned Service control and real manual sync are integrated. Separate-author
browser, F# and C# revisions are independently approved; actual published-head
Windows/native and Mac gates remain required.

| Measure | Executed result |
|---------|-----------------|
| Clean `npm ci` after lockfile repair | Passed; 308 packages |
| React lint and TypeScript/Vite build | Passed |
| JavaScript unit cases after independent revision | 21 passed; protocol/source-identity and asset-provenance cases |
| Repeated React build | Byte-identical manifest and asset digests |
| Browser discovery after independent revision | 45 cases across six suites; existing nine reclassification cases retained |
| Historical Blazor promotion definitions | 12 explicitly excluded; not counted as executed |
| First real isolated-Service browser execution | 30 total; 21 passed, 9 failed |
| Corrected real isolated-Service browser execution | 33 passed; 0 failed/skipped |
| Independently revised browser execution | 43 passed; 0 failed/skipped, against the fresh copied bundle |
| Real UI-to-producer sync execution | 45 passed; 0 failed/skipped, including Settings and palette admission-to-archive-to-document proof |
| Canonical Service host full graph | 0 warnings/errors |
| Initial full .NET execution with host cases | 1,491 total; 1,483 passed, 1 failed, 7 retained skips |
| Combined host/lifecycle full execution | 1,519 total; 1,512 passed, 0 failed, 7 retained skips |
| Initial combined root coverage | 10,523/13,082 lines; 9,920/20,720 branches; rejected line regression |
| Fresh Service safety/producer execution | 1,568 total; 1,561 passed, 0 failed, 7 retained skips |
| Fresh exact root coverage | 10,624/13,116 lines; 10,026/20,728 branches; line non-regression restored against 10,284/12,753 |
| Windows native projects | Cross-compiled with 0 warnings/errors; actual Windows execution pending |
| Independent browser revision | Approved; three findings corrected by a separate author |
| Independent native source review | Approved after a separate author corrected ownership-oracle and external-link callback findings |
| Independent F# source review | Approved after fresh production and platform-inventory revisions |
| Final Mac full execution | 1,568 total; 1,561 passed, 7 retained skips, 0 failed |
| Final post-normalisation root | 10,624/13,116 lines; 9,967/20,728 branches; Core/Service/UI retained |
| Explicit platform discovery | All 1,568; Unix-only 4; Windows selection 1,564 |
| Final frontend gates | Clean npm install, 21 unit cases, lint, build, identical repeated bundle and all 45 browser cases passed |
| First published-head Mac CI | Passed full 1,568-case suite and all 45 browser cases |
| First published-head Windows selection | 1,564 total; 1,555 passed, two readiness-probe failures, seven retained skips |
| First actual Windows native suite | 84 passed; zero failed/skipped |
| First native desktop smoke | Failed before child launch with `ForeignListener`; no WebView/runtime proof claimed |
| Approved readiness correction | Eight focused cases passed; full Mac 1,571 total, 1,564 passed, seven retained skips, zero failed |
| Readiness correction exact root | 10,623/13,116 lines; 10,018/20,728 branches; non-regressing Core/Service/UI scope |
| Approved native transport correction | Cross-build clean; five-second connect/eight-second request budgets, explicit refusal-only free classification and safe probe diagnostics |
| Corrective-head actual Windows native suite | 100 passed; zero failed/skipped |
| Corrective-head actual desktop/WebView2 smoke | Passed six routes and 30 live ownership checks; owned PID 1436, graceful unforced exit 0 and successful cleanup |
| Corrective-head Windows Service suite | 1,567 selected; 1,559 passed, one restart-fixture SQLite cleanup failure, seven retained skips |
| Final parent SQLite fixture correction | Independently approved explicit nonpooled connection; full local 1,571 total, 1,564 passed, seven skips, zero failed |

The first browser run exposed decorative icons in accessible navigation names,
a missing synthetic extraction artifact and an incorrect fake Ollama endpoint.
Corrections preserve the real Service/UI assertions; the replacement 33-case
run passes against the isolated Service. Discovery fails unless every supported suite
matches its expected nonzero count.

The deployment test established that referencing the original test deps file
anchors the child application's base at the original test output. Its fixture
now copies runtime metadata alongside the owned deployed assemblies. A fresh
physical-identity probe verifies the reported deployed directory, while decoy
bundles and the archive-must-not-be-created assertions remain intact.
No owner archive, live provider or external account is used.

Independent browser review closed mixed document/email identity collisions,
failed-journey state and command-palette sync feedback through separate-author
regressions. The successful 43-case run used the revised manifest copied to
Service output; an earlier run had exercised the older output bundle and is
not counted as revised-source proof.

Two additional cases stage owned synthetic intake files only after the initial
producer cycle. Both Settings and command-palette actions make a real HTTP
request, observe exactly one completed producer cycle, verify the new database
row and original archived bytes, and open the visible document. The automatic
interval is 60 minutes; neither sync nor its response is mocked.

Separate-author revisions close mixed valid/undeclared module acceptance,
non-pipe Unix admission, stale native ownership proof and escaping browser
callbacks. Direct `ProducerCycle` assertions exercise copied-folder registration,
canonical paths, claims, duplicates, history, providers, watermarks and
cancellation; the exact root line regression is closed without changing
assembly scope, skips or thresholds.

The final F# inventory revision marks all four descriptor cases `Platform=Unix`
and selects Windows tests with `Platform!=Unix`; accidental unfiltered Windows
execution fails explicitly. Expected Windows selection is 1,564 cases, including
seven existing skips; its runtime result must come from actual Windows CI.
The final Mac run executes all 1,568 cases. These selections retain the Windows
owned-pipe/process tests and do not alter production coverage scope or gates.
Actual Windows native tests/desktop proof and published-head cross-platform
CI remain required.

The exact locally published Service and bundle also pass all 45 browser cases.
Publication removes a deliberately inserted stale asset. Both build and
`publish --no-build` fail with an actionable prerequisite when `dist` is absent;
the generated bundle is restored after the controlled check.
Playwright output is confined to `test-results/canonical`; the runner-deleted
tracked legacy screenshots were restored without viewing them, and the final
45-case run leaves their bytes unchanged. They are not browser artefacts for
upload.

The first published Windows gate exposed per-probe HTTP cancellation escaping
the bounded Service-startup poll. Separately, the native preflight reported
`ForeignListener` before it owned a child. Both failures remain blocking;
the successful native unit suite does not substitute for desktop/WebView
execution. Fresh platform corrections require safe transport diagnostics and
replacement-head proof, not an unchanged retry or weaker ownership oracle.

The corrective native run supplies real WebView2/runtime, route/module and
same-owned-PID graceful shutdown proof. Its first probe measured explicit
connection refusal after 2,083 ms, beyond the old two-second request budget.
The remaining Windows restart-fixture failure is parent SQLite pooling during
directory cleanup. Its helper now opens an explicitly nonpooled connection
with archive authority, without global pool clearing, cleanup retries or
changes to production pooling and preservation assertions. Replacement-head
Windows proof remains required.

## Integrated typed retrieval and MCP policy — PR #26

The issue #6 feature adds shared keyword/semantic/hybrid HTTP and MCP
contracts, a non-generative persisted-comprehension accessor, explicit MCP
capabilities and a local Host/origin boundary. The separate author's revision
passes the full preserved suite and independent re-review. PR #26 merged as
`5aa929be`; replacement-head Windows/macOS checks and all seven main
build/test/publish jobs passed.

| Measure | Executed result |
|---------|-----------------|
| Release full graph | 0 warnings/errors |
| New HTTP/search/persisted/policy/race selection | 59 passed, 0 failed/skipped |
| Initial preserved full execution | 1,333 total; 1,324 passed, 2 failed, 7 retained skips |
| Diagnostic root lines / branches | 10,167/12,677 lines; 9,470/20,207 branches |
| Revised and final formatted full execution | 1,387 total; 1,380 passed, 0 failed, 7 retained skips |
| Final root lines / branches | 10,283/12,753 lines; 9,558/20,265 branches |
| Final portable-fixture full execution | 1,389 total; 1,382 passed, 0 failed, 7 retained skips |
| Portable-fixture root lines / branches | 10,283/12,753 lines; 9,557/20,265 branches |
| Instrumented assemblies | Core, Service, UI; unchanged |
| Independent F# review | Approved after a separate author corrected all three findings |
| First published head `ec3fc784` | macOS and three desktop builds passed; Windows failed two live-file hash fixture cases |
| Replacement Windows/macOS CI | Passed at `b6bc5b46`; both OS suites 1,389 total, 1,382 passed, 7 skipped |
| Published Windows root lines / branches | 10,283/12,753 lines; 9,506/20,265 branches |
| Main Windows root lines / branches | 10,284/12,753 lines; 9,455/20,265 branches |

The revision restores legal large-limit keyword calls and distinguishes unknown
tools from recognised unauthorised mutations. Schema 17 records explicit origin
links during accepted embedding-only reflow, leaving canonical publications
unchanged. Real HTTP/MCP reads retain full attributable payslip JSON and manual
corrections before and after actual embedding; separate document/publication
generations preserve the read fence and original provenance.

| New module | Executed cases |
|------------|----------------|
| `TypedSearchHttpTests` | 21 |
| `PersistedComprehensionHttpTests` | 8 |
| `LocalMcpBoundaryHttpTests` | 10 |
| `RetrievalPolicyTests` | 21 |
| `QueryEncodingRaceHttpTests` | 4 |
| `PersistedReadRaceHttpTests` | 2 |
| `ReembedReadHttpTests` | 3 |
| `ComprehensionProvenanceTests` | 11 |
| `ComprehensionProvenanceSchemaTests` | 2 |
| `KeywordLimitHttpTests` | 23 |
| `UnknownToolHttpTests` | 8 |
| `RetrievalPhysicalHashTests` | 2 |

These 115 cases supplement the integrated 1,274-case baseline. Physical SQLite/
WAL and archive hashes remain unchanged during reads; migration, repeated
reembed, sibling isolation, invalidation, corrupt/missing origins, source
changes, interrupted reads and transactional carry failure are covered.
Historical gaps without provenance remain unavailable rather than guessed.
The seven existing skips, instrumentation and coverage gates are unchanged;
exact root line coverage improves from 9,712/12,166 to 10,283/12,753.
The first Windows run exposed `File.ReadAllBytesAsync` sharing incompatibility
with an open SQLite writer. The test-only helper now opens read access with
`FileShare.ReadWrite`, hashes the same complete database/WAL bytes and disposes
its streams. Two controlled cases retain live writer handles, verify exact
filenames/digests and detect changed database/WAL bytes. The original physical
HTTP/MCP assertions are unchanged; no checkpoint, close, retry or skip hides
mutation. Fixture writers are quiescent during each hash pass.

Production and narrow fixture reviews are approved; actual replacement-head
and main CI evidence is retained. The five checkpoint artifacts were pruned
after verification; the two useful main dependency caches remain.
Broader REST/browser/Tray session authentication and the 85%/60% wave-close
target remain separate.

## Integrated file-first search/operator — PR #25

The schema-16 feature wires body/extracted-content FTS, real HTTP/MCP
queries and an explicit bounded local `reindex-files` dry-run/apply operator.
PR #25 merged into `main` as `df0cad9b` after independent source and portable
fixture approval. All five published-head checks and all seven main
build/test/publish jobs passed.

| Measure | Executed result |
|---------|-------------------|
| Release full graph | 0 warnings/errors |
| Initial file-keyword/startup/local-operator selection | 87 passed, 0 failed/skipped |
| Independent revision/benchmark selection | 42 passed, 0 failed/skipped |
| Final rank-skew/HTTP/pagination selection | 19 passed, 0 failed/skipped |
| Final portable-fixture selection | 38 passed, 0 failed/skipped |
| Final full local execution | 1,274 total; 1,267 passed, 7 skipped, 0 failed |
| Final local root lines / branches | 9,712/12,166 lines; 8,868/19,432 branches |
| Instrumented assemblies | Core, Service, UI |
| Obsolete skips replaced | Three body-text cases replaced by real file-first HTTP/MCP/reindex assertions |
| Independent F# review | Approved after separate authors corrected safety and source-snippet findings |
| First published-head CI | macOS passed; Windows had 20 fixture/path assertions fail |
| Portable fixture correction / replacement CI | Passed on actual Windows and macOS; 1,274 total, 1,267 passed, 7 skipped |
| Published Windows exact root lines | 9,712/12,166 = 79.829032% |
| Post-merge main | All seven build/test/publish jobs passed |

Independent review closed explicit operator authority, complete pagination and
in-flight source-identity publication findings. A third author's shared-query
revision resolves source snippets independently of the ranking prefix for
selected identities. A real SQLite skew corpus proves distinct metadata/source
rankings and stable preferred evidence across Core, HTTP and HTTP MCP limits
and offsets, with unchanged database/FTS/file hashes.

The feature adds 133 executed cases; replacing only the three obsolete skipped
body-text cases changes the prior 1,144 total to 1,274, with the other seven skips
retained. Exact root line coverage is 79.8290%, above the unchanged merge gate
and prior combined baseline; the stronger 85%/60% wave-close target is not met.

The first Windows run exposed in-memory filesystem aliasing rather than a
production authority failure: drive-qualified canonical paths did not share
the existing fake rooted keys. A test-only normaliser correction retains other
volumes, UNC, relative and unrelated roots; three portable cases cover byte/text
reads/writes, sizes/hashes, directory/list/move/delete and non-alias identities.
Fault injection uses the same normaliser, and home expansion uses the actual
platform separator contract. Production guard/hash logic is unchanged; actual
Windows CLI-process tests already passed; replacement full Windows/macOS CI
subsequently proved the repaired fixtures.

The synthetic Core/in-memory SQLite + MemFs measurement indexed 1,000 distinct
valid sources in batches of at most 100 in 2,327.237 ms; 40 queries after one
warmup measured p50 0.141 ms and p95 0.167 ms. Source hashes, claims, IDs and
snippets were checked. These are measured synthetic timings, not a production
threshold or owner-corpus benchmark. No personal archive/provider was accessed.

## Integrated coverage baseline — PR #21

PR #21 merged into `main` as `d830c610`. The useful test contracts from old
drafts #21-#23 are reconciled onto reflow without restoring their superseded
production changes. Sixteen provider
cases, eight Config cases and four FolderWatcher cases add 28 executed cases;
one controlled deadline/cleanup regression adds a twenty-ninth case.
Existing replay, concurrent identity and failure-logging assertions are stronger.

| Measure | Result |
|---------|--------|
| Release build | Passed; 0 warnings/errors |
| Provider-focused selection | 32 passed, 0 failed/skipped |
| Config/FolderWatcher selection | 12 passed, 0 failed/skipped |
| Provider-only full execution | 1,131 total; 1,121 passed, 10 existing skips, 0 failed |
| Final bounded-enumeration selection | 4 passed, 0 failed/skipped |
| Final combined full execution | 1,144 total; 1,134 passed, 10 existing skips, 0 failed |
| Combined root line coverage | 9,154 / 11,604 = 78.8866% |
| Final root branch coverage | 7,907 / 18,295 |
| Assembly scope | Core, Service, UI; unchanged |
| Production source changes | None |
| Independent F# review | Approved after a different author corrected bounded producer progress |
| Published-head Windows/macOS CI | Passed all five build/test jobs at `b46cc3a`; both OS suites 1,134 passed, 10 skipped, 0 failed |
| Published Windows exact root lines | 9,155 / 11,604 |

Provider-only line coverage was 9,122/11,604, within the known one-line
adjacent-run variance; no provider-only line-rate improvement is claimed.
The combined recovered coverage exceeds both conservative local and published
reflow baselines without exclusions or lower thresholds. All ten existing skips
remain; broader fixture quality and 85%/60% wave closure remain #18 work.

New modules: `EmailEnumerationContractTests` (4), `EmailChannelFailureContractTests`
(4), `GmailPayloadContractTests` (5), `OutlookSparseMappingTests` (3),
`ConfigSalvageTests` (8), `FolderWatcherScanContractTests` (4). One new empty-sender
row extends `EmailProviderOptionalContractTests`; existing producer/optional/
commit-contract modules retain their registered cases.

Independent review rejected an unbounded producer wait in the first candidate.
A different author added an operation deadline outside the expected-exception
assertion, owned cancellation and a bounded join that reports cleanup failure.
The controlled timeout case proves the fixture-owned producer stops before the
test returns. No claim is made that cancellation can forcibly stop arbitrary
noncooperative code.

## Integrated reflow baseline — PR #20

PR #20 merged into `main` as `55ec588`, following metadata-only reclassification
in PR #19. Its published head `b65f4b3` passed all five build/test jobs; Windows
and macOS each executed 1,115 cases (1,105 passed, 10 existing skips, 0 failed).
The merge tree is identical to the independently approved published tree.

| Measure | Executed result, 2026-10-19 |
|---------|----------------------------|
| Release build of test project and Core/Service/UI references | Passed; 0 warnings/errors after restoring the changed project graph |
| Candidate and actual-checkout full execution | 1,115 total; 1,105 passed, 10 existing skips, 0 failed |
| Final formatted-checkout root line coverage | 9,123 / 11,604 = 78.6194% |
| Adjacent earlier-checkout root line coverage | 9,124 / 11,604 = 78.6281% |
| Final formatted-checkout root branch coverage | 7,848 / 18,295 = 42.8970% |
| Adjacent earlier-checkout root branch coverage | 7,867 / 18,295 = 43.0008% |
| Instrumented assemblies | Hermes.Core, Hermes.Service, Hermes.UI |
| Final package covered/valid lines | Core 8,811/10,252; Service 312/921; UI 0/431 |
| Exact unchanged 75% root gate | Passed without rounding, exclusions or threshold changes |
| Independent F# review | Approved after a different author corrected three substantive findings |
| Published Windows/macOS CI | Passed at `b65f4b3`; both OS suites 1,105 passed, 10 skipped, 0 failed |
| Published Windows exact root line coverage | 9,122 / 11,604 = 78.6108%; Core, Service and UI included |

These broader root rates must not be described as Core-only coverage or as
meeting the 85% line / 60% branch wave-close target. The ten existing skips and
four potentially non-asserting Osprey fixture cases remain separate backlog.
Touched F# files were normalised mechanically to UTF-8 BOM/LF, with reviewed
body content preserved and both pre-format/final hashes retained. The final
build and 1,115-case run passed after formatting; the table retains the
conservative adjacent-run counts rather than selecting the higher sample.
Real HTTP regressions used an isolated synthetic archive, fake providers, an
isolated content root and the documented polling-file-watcher accommodation;
no personal corpus or live provider was accessed.

New/reconciled reflow modules and their executed case counts:

| Module | Cases | Principal contract |
|--------|-------|--------------------|
| ReflowTests | 46 | DAG invalidation, generations, publication races, retry and failure recovery |
| ApiReflowTests | 13 | Real HTTP requests, idempotence, malformed inputs and reclassification compatibility |
| ServiceLifetimeTests | 9 | Startup failure, observed workers, quiescence and disposal |
| ReflowMigrationIntegrationTests | 2 | v14 source history/claims and v12-layout reflow preservation through schema 15 |
| DatabaseBoundaryIntegrationTests | 3 | Shared physical-connection gate, nested result rollback and whole-transaction claims |
| ReflowSourceHistoryIntegrationTests | 3 | Immutable earlier bodies/annotations, metadata-only category changes and guarded artifacts |
| TransactionSavepointTests | 3 | Ordinary failure, IO failure and cancellation cleanup before an outer commit |
| CorrectedExtractionHttpTests | 2 | Latest field-specific HTTP corrections survive invalidation/replay and remain effective in REST/MCP reads |
| DeepExtractionHttpTests | 3 | Actual HTTP MCP supersession errors and a successful fake-provider publication control |

The eight cases in the final three modules directly cover the independent
review findings. The existing comprehension sibling-race tests now use distinct
source identities in one shared folder. Artifact-write fault injection targets
the actual protected byte-write capability and asserts the exact failed outcome;
ownership guards and substantive race assertions were not weakened.

## Summary — historical original PR head inventory

The inventory below describes original PR head `c9e5d4f`, not the in-progress
Option 1 revision. Revision discovery and coverage must be recorded from an
executed test run; source-count changes are not execution evidence.

| Category | Count |
|----------|-------|
| Unit | 590 |
| Property | 5 |
| Integration | 350 |
| ManualTest | 6 |
| **Total** | **951** |

> 913 unique test methods; 951 total test cases
> (Theory/InlineData tests contribute multiple cases per method)

## Execution Baseline

Current-main baseline captured on 2026-08-27 from `a11686d` source plus
documentation-only rebaseline commits:

| Measure | Result |
|---------|--------|
| .NET discovered | 867 |
| .NET passed / skipped / failed | 857 / 10 / 0 |
| Line / branch coverage | 65.0% / 31.1% |
| Supported .NET builds | Core, Service, Tests, Windows Tray: 0 warnings, 0 errors |
| Playwright source definitions | 21 (9 React, 12 Blazor) |
| Playwright default discovery | 0; no usable runner configuration |
| React locked install | Failed: `package-lock.json` is out of sync |
| React build after no-lock install | Passed |
| React lint | Failed: 4 errors |

### PR #17 PR B validation

The metadata-only reclassification branch adds 31 cases without changing the
current-main skip set: 898 discovered, 888 passed, 10 skipped, and 0 failed.
Core-only coverage is 66.84% line / 31.22% branch, above the 65.0% / 31.1%
Phase 0 baseline. The full solution builds with 0 warnings and 0 errors.

Skip-quality findings:

- 3 email-body FTS tests are skipped because file-first content is not indexed.
- 1 MCP path-safety test is skipped for platform-dependent path behavior.
- 6 Osprey parity tests are skipped because fixtures are unavailable.
- 4 other Osprey parity tests use `Option.iter` and pass without assertions when
  their fixtures are unavailable; only the two PPTX fixtures are source-controlled.

The active follow-on issues are #6, #8, #9, #11, #16, #17, and #18. The V5
Stabilization wave closes only at 85% line and 60% branch coverage with truthful
skip and Playwright execution evidence.

### PR #17 correction validation

The lockout correction adds 53 cases: 8 database/canonical-path cases,
26 legacy-repair cases, and 19 MCP cursor/operator cases. The full suite
discovers 951 cases: 941 passed, 10 skipped, and 0 failed. Full-solution
Release build passes with 0 warnings and 0 errors. Core coverage is 68.50%
line / 33.58% branch, improving on the Phase 0 baseline without claiming
ownership of the repository-wide coverage recovery.

The canonical React preview adds 9 Playwright cases covering changed, no-op,
partial, transport-failure, pending/validation, pending and ordinary browser
history navigation, stale-selection prevention, and success-shaped API error
responses. They pass against the Vite preview surface. The Service root still
serves the excluded Blazor surface; canonical Service-hosted React routing
remains Phase 1D scope.

---

## PR #19 Option 1 revision — historical pre-publication evidence

Latest fully executed evidence for uncommitted revision 2 and its contract
additions over 2026-10-18/19:

| Measure | Result |
|---------|--------|
| macOS Release Core, Service and Tests builds | Passed; 0 warnings and 0 errors |
| Windows Release Core, Service, Tests and Tray builds | Passed; 0 warnings and 0 errors |
| Focused history/provider/ownership/reclassification cases | 123 passed; 0 failed or skipped |
| macOS full .NET execution, repeated | 992 total; 982 passed, 10 existing skips, 0 failed |
| Windows full .NET execution | 992 total; 982 passed, 10 existing skips, 0 failed |
| Conservative macOS root/Core-only line coverage | 6,086 / 8,107 = 75.0709% |
| Conservative macOS root/Core-only branch coverage | 5,176 / 12,665 = 40.8685% |
| Windows root/Core-only line coverage | 6,087 / 8,107 = 75.0833% |
| Windows root/Core-only branch coverage | 5,184 / 12,665 = 40.9317% |
| Original-head comparable coverage | 68.50% line / 33.58% branch |
| Final-head HTTP/MCP probes | Passed against an isolated synthetic archive |
| Synthetic schema upgrade | v14 persisted; source identity, completion rows and foreign-key integrity preserved |
| Independent review | Source and additive test slices approved; runtime/transport proof satisfied |

The current Cobertura root contains only `Hermes.Core`; root and package rates
match. The exact unchanged 75% root line gate passes on two adjacent macOS runs
and the Windows run, without exclusions or threshold changes. This does not
establish Service-inclusive coverage, a published GitHub Actions result or the
85% line / 60% branch wave-close target. No commit, push or merge occurred.
Helper services were stopped.

Windows execution used a SHA-256-verified snapshot of the current source and
test files in a dedicated temporary directory on the existing idle CI host.
Microsoft SDK 9.0.313 was installed only there after global `dotnet` was absent,
with its official SHA-512 checked. Restores used only the public NuGet feed and
an isolated package cache; build parallelism was bounded. The runner service
and its workspace were untouched. Verified evidence was retrieved before the
temporary source, SDK and packages were removed.

The Windows TRX logger produces two byte-identical coverage attachments; both
were hashed before report selection. Its summary says `notExecuted=0` although
10 individual results have outcome `NotExecuted`, so skip inventory uses actual
outcomes plus console results rather than that summary attribute alone.

Final HTTP/MCP smoke used an isolated content root and
`DOTNET_USE_POLLING_FILE_WATCHER=1`. A managed stack located the earlier local
startup stall in native macOS configuration-file watching during
`WebApplication.CreateBuilder`, not the database gate. Polling is a recorded
smoke accommodation, not a production fix.

Executed commands include `dotnet build` for each supported local project with
`-c Release --no-restore`, and `dotnet test tests/Hermes.Tests/Hermes.Tests.fsproj
-c Release --no-build --no-restore --collect:"XPlat Code Coverage"`. Evidence is
retained in session artifacts under `pr19-final-contracts-full-coverage`,
`pr19-coverage-stability`, `pr19-revision2-transport.json`,
`pr19-windows-evidence.zip`, `pr19-windows-validation.json` and the isolated
`pr19-api` archive. `pr19-coverage-stability.json` records both exact local gates.

The notes below preserve the intermediate failures and corrections; the latest
executed figures above supersede their pending-validation statements.

Revision-1 independent review identified missing regression proof for replacement after V5
outputs/completions exist, same-day provider IDs sharing a shortened filename
prefix, and production provider transport failures becoming successful empty
responses. Existing green synthetic-producer tests do not cover those cases.
That author was locked out of revision 2 under reviewer protocol. A distinct
author implemented immutable history, full-provider-ID filenames and
production-provider/enumerator fault propagation. The history, naming,
production transport, pagination and throttling cases now execute successfully.

The 10 non-empty/binary SDK success cases and 12 optional-metadata/commit-boundary
cases compile, execute and receive scoped independent approval. Their assertions
cover real interpreter outputs, exact bytes/provenance, idempotent registration,
legitimate sparse data, explicit post-commit errors and initial cursor failure.
They introduce no skips, suppressions, coverage exclusions or production changes.

The owner selected metadata-only reclassification and deferred saved-path
recovery to issue #24. The revision removes the repair modules, exposed MCP
operator, cursor/scan tests, and repair-only document-change epoch machinery.
Canonical-path helpers and transactional category/provenance/tag/FTS behavior
remain in scope.

Original-head validation relayed by the parent on 2026-10-18:

- Release Tests/Core and Service builds: 0 warnings, 0 errors.
- Full .NET run: 951 total, 941 passed, 10 skipped, 0 failed.
- Cobertura contains only `Hermes.Core`: 5888/8595 lines (68.50%) and
  4215/12549 branches (33.58%). These are Core-only, not Service or aggregate.

The first bounded patch removes 44 legacy-reclassification cases, 19 legacy
MCP cases, and 2 epoch cases. It adds 6 compatibility integration cases.
Existing reclassification tests now explicitly call the detailed API, while
the original `Task<Result<unit,string>>` callable contract is restored.
MCP tool discovery now excludes the repair tool.

Patch A's first Release Tests build failed with FS0597 in the new MCP test's
chained JsonElement indexer. No Service build or focused run followed that
failure. Patch B separates the indexer expression; its validation is pending.

Patch B's first Core Release build failed on nullability/type inference,
ambiguous body-ingestion records, and the Classifier task-expression branch.
The compiler-correction patch adds explicit types, checked nullable values,
non-null SQL parameter boxing, and the missing branch. It also corrects the
same inference patterns in the new tests; no cases or assertions are removed.
After that correction, Core and Service Release builds each passed with
0 warnings and 0 errors. The Tests build then found one remaining caller
asserting detailed fields through the legacy tuple API and ambiguous JSON
helper overloads. Compiler correction round 2 selects the detailed API in
that test and explicitly types both JSON helpers; no cases or assertions change.
After compiler correction round 2, Core, Service, and Tests Release builds
each passed with 0 warnings and 0 errors. The focused run passed 152/152.
The full revised suite discovered 917 cases: 907 passed, 10 existing skips,
and 0 failed.

Comparable Core-only Cobertura then reported 5366/7944 covered lines
(reported 67.54%) and 4023/12442 covered branches (32.33%), below original
head's 5888/8595 lines (68.50%) and 4215/12549 branches (33.58%).
This regression is unresolved; these results do not establish completion.

The parent's isolated Service transport probe found that a valid canonical
PUT category request returned HTTP 400 with an empty body before handler work,
while health and document-list requests succeeded. The canonical route now
uses an explicit named id/ctx lambda instead of a partially applied delegate.
After the named-lambda correction, the parent's real isolated HTTP/MCP probes
passed canonical PUT/idempotence, legacy partial batch move, the React batch
contract, deprecated single POST, repeated 18-tool discovery, MCP success/error,
and removed-repair rejection. Source bytes/path/hash, extraction/embedding
timestamps, and completion rows remained identical throughout those probes.

Patch B adds non-destructive database ownership guards, staged-source claims,
serialized transaction capabilities, guarded source/artifact mutations, and
post-commit thread-body retirement. Existing duplicate rows are not rewritten
or deleted. The original Classifier tuple signature is restored as an adapter;
partial failure raises a structured exception containing the detailed batch.

Added deterministic coverage includes canonical aliases, direct/full-row and
independent-connection writers, existing duplicates, unregistered connections,
staged-source collisions and retries, generated-write collisions, protected
deletion/movement, exact-identity re-download, symlink containment, thread-body
commit/failure behavior, and Classifier compatibility.

The producer test patch adds 12 synthetic channel-producer integration cases
covering paginated and overlapping ingestion, source registration/claims,
acknowledged-message replay, empty enumeration, latest-body replacement,
date handling, attachment/sidecar collision refusal, and watermark failures.
The fixtures use memory-only providers, SQLite, and filesystems, without
external sync, model calls, sleeps, or source-document replay.

Execution confirmed the producer failure-accounting gap: the focused run had
26 cases, 25 passed and one failed. Full execution had 933 total, 922 passed,
10 skipped and the same one failure. The failed replacement advanced the
watermark from 2025-02-28T12:00:00+00:00 to 2025-03-15T10:30:00+00:00.

Comparable Core-only coverage recovered to 5571/8036 lines (69.32%) and
4457/12614 branches (35.33%), above original head's 68.50%/33.58%.
This is not aggregate coverage or evidence that the 75% CI or 85%/60% wave
gates have been met.

The owner explicitly selected whole-email retry for new failed attempts.
The correction fetches provider data before the write gate, then acknowledges
the message and registers all documents/claims in one transaction. Storage
failure rolls back acknowledgement and registration, reports an error rather
than a duplicate, and retains the watermark. Other committed messages remain
valid. Legacy sync/backfill storage attempts also use message transactions;
backfill errors propagate without advancing its page state.

Body registration no longer performs retirement cleanup. Cleanup and channel
callbacks run after the actual outer commit. Post-commit errors are reported
with committed counts rather than pretending committed storage rolled back.
No historical acknowledgement reset, message replay, or per-attachment retry
subsystem is introduced. Failed-attempt filesystem artifacts are not guessed
away; ordinary retry checks their bytes through the existing source guards.

New regressions cover late attachment failure after body registration, rollback
of the old body's retirement, retry of the same email, mixed success/failure,
provider fetches outside the gate, and public-DB callbacks after full commit.
The earlier staged-channel phase test is replaced by standalone staged-claim
protection plus actual whole-message reader/writer blocking and rollback tests.
The sidecar-collision expectation now requires rollback of the failed email's
claim, not a partially committed email. These corrections are not yet validated.

The first whole-email Core build stopped on reserved identifier `process`
(FS0046). The correction renames it, explicitly types the new SQL/filesystem/
clock helpers, and checks nullable attachment filename results without warning
suppressions.

HTTP 429 handling retains the existing per-consumer 60-second cancellable
backoff outside the write gate. An internal fake-delay boundary adds two
deterministic cases proving the next message is not fetched during backoff,
the database gate is free, and cancellation stops further fetching without
acknowledging the rejected email. No real backoff sleep or provider call is used.

Source inspection confirms legacy processAttachment has no storage-error
catch; processMessage catches outside transaction.Run. Two additional public
producer cases fail the second attachment INSERT after the first has succeeded,
then prove legacy acknowledgement/document/claim rollback, unchanged processing
counts and watermark, backfill error propagation and retained page state, and
successful whole-email retry with valid foreign keys. These new cases have not
yet been compiled or executed.

An additional staged-source invariant correction makes ordinary deletion and
movement reject source claims, not just document owners. Post-commit body
cleanup uses an internal single-use authorization minted while the old
document remains its verified exclusive owner, in the deleting transaction.
A non-destructive retirement reservation blocks source reacquisition and
document registration until that authorization is consumed. No existing
paths or duplicate rows are rewritten, and no pending-producer identity,
cleanup permission, migration replay, or repair behavior is guessed.

Four further deterministic cases cover a real channel producer paused after
its source transaction commits but before document INSERT (delete and move),
retirement reservation enforcement, and stale-authorization refusal after
a new producer stages another source at the same path. Existing body-retirement
success/failure tests remain required to prove legitimate cleanup and rollback.
These additions have not yet been compiled or executed.

Validation of the whole-email correction, final inventory reconciliation,
and a fresh same-scope coverage comparison remain outstanding.
Main caller evidence identifies only
the legacy batch move caller; no in-repo single-client migration is needed,
while unknown external single-client acceptance remains unproven.
No corpus replay or repair execution is authorised.

---

## ConfigTests.fs (15 tests)

| Test | Category |
|------|----------|
| Config_ParseYaml_ValidYaml_ReturnsConfig | Unit |
| Config_ParseYaml_EmptyYaml_ReturnsDefaults | Unit |
| Config_ParseYaml_WithAccounts_ParsesAccountList | Unit |
| Config_ParseYaml_WithWatchFolders_ParsesPatterns | Unit |
| Config_Load_MissingFile_ReturnsError | Unit |
| Config_Load_ValidFile_ReturnsConfig | Unit |
| Config_Init_CreatesConfigAndRules | Unit |
| Config_Init_SkipsExistingFiles | Unit |
| Config_ExpandHome_TildePath_ExpandsToUserHome | Unit |
| Config_ExpandHome_AbsolutePath_ReturnsUnchanged | Unit |
| Config_ParseYaml_ChatAzureOpenAI_ParsesProvider | Unit |
| Config_ParseYaml_ChatOllama_ParsesProvider | Unit |
| Config_ParseYaml_NoChatSection_DefaultsToOllama | Unit |
| Config_ChatProviderKind_FromString_ParsesVariants | Unit |
| Config_ParseYaml_NeverThrows | Property |

## DatabaseTests.fs (24 cases)

| Test | Category |
|------|----------|
| Database_InitSchema_CreatesAllTables | Integration |
| Database_InitSchema_SetsSchemaVersion | Integration |
| Database_InitSchema_IsIdempotent | Integration |
| Database_SchemaVersion_BeforeInit_ReturnsZero | Integration |
| Database_TableExists_NonexistentTable_ReturnsFalse | Integration |
| Database_FTS5_InsertTrigger_PopulatesFtsOnInsert | Integration |
| Database_FTS5_SearchByVendor_FindsDocument | Integration |
| Database_InitArchive_CreatesDirectoriesAndDatabase | Integration |
| Database_FromPath_CreatesParentDirectories | Unit |
| Database_InitSchema_CreatesAllIndexes | Integration |
| Database_InitSchema_V3_CreatesRemindersTable | Integration |
| Database_InitSchema_V3_SyncStateHasBackfillColumns | Integration |
| Database_InitSchema_V3_SchemaVersionIs3 | Integration |
| Database_InitSchema_V3_IdempotentRunTwice | Integration |
| Database_InitSchema_V3_ReminderIndexesExist | Integration |
| Database_SchemaVersion_FreshDb_ReturnsLatest | Integration |
| Database_InitSchema_Idempotent_CanRunTwice | Integration |
| Database_FreshSchema_HasAllTables | Integration |
| Database_CanonicalArchivePath_RejectsRelativeEscape (3 cases) | Unit |
| Database_CanonicalArchivePath_RejectsRootedEscape | Unit |
| Database_CanonicalArchivePath_AllowsContainedDotDotSegments | Unit |
| Database_CanonicalArchivePath_ArchiveRootIsStableAndSelfContained | Unit |

## RulesTests.fs (32 tests)

| Test | Category |
|------|----------|
| Rules_ParseYaml_ValidRules_ParsesCorrectly | Unit |
| Rules_ParseYaml_EmptyYaml_ReturnsEmptyRules | Unit |
| Rules_ParseYaml_CustomDefaultCategory_Respected | Unit |
| Rules_Classify_DomainMatch_TakesPriority | Unit |
| Rules_Classify_FilenameMatch_WhenNoDomainMatch | Unit |
| Rules_Classify_SubjectMatch_WhenNoFilenameMatch | Unit |
| Rules_Classify_DefaultRule_WhenNoMatch | Unit |
| Rules_Classify_NoSidecar_MatchesFilenameOnly | Unit |
| Rules_Classify_NoSidecar_NoFilenameMatch_DefaultsToUnsorted | Unit |
| Rules_Classify_CaseInsensitive_FilenameMatch | Unit |
| Rules_Classify_CaseInsensitive_SubjectMatch | Unit |
| Rules_Cascade_DomainBeatsFilename | Unit |
| Rules_Cascade_FilenameBeatsSubject | Unit |
| Rules_Cascade_SubjectBeatsDefault | Unit |
| Rules_ParseDefaultRulesYaml_Succeeds | Unit |
| Rules_ParseContentRules_ValidYaml_ReturnsRules | Unit |
| Rules_ParseContentRules_EmptyYaml_ReturnsEmpty | Unit |
| Rules_ParseContentRules_NoContentRulesSection_ReturnsEmpty | Unit |
| Rules_ParseContentRules_ContentAnyAndHasAmount | Unit |
| Rules_ParseContentRules_ZeroConfidence_DefaultsToHalf | Unit |
| Rules_ClassifyWithRules_EmptyRules_ReturnsDefault | Unit |
| Rules_ClassifyWithRules_EmptyRules_CustomDefault_Respected | Unit |
| Rules_ClassifyWithRules_SubjectMatchOnly_WhenNoSender | Unit |
| Rules_ClassifyWithRules_EmptySubject_SkipsSubjectRules | Unit |
| Rules_FromFile_ValidYaml_LoadsRules | Unit |
| Rules_FromFile_MissingFile_UsesDefaults | Unit |
| Rules_FromFile_Reload_UpdatesRules | Unit |
| Rules_ParseYaml_InvalidYaml_ReturnsError | Unit |
| Rules_ParseYaml_NullRules_ReturnsEmptyList | Unit |
| Rules_ParseYaml_InvalidRegex_SkipsRule | Unit |
| Rules_ParseYaml_MissingMatch_SkipsRule | Unit |
| Rules_FromFile_ReadException_ReturnsDefaults | Unit |

## ClassifierTests.fs (26 tests)

| Test | Category |
|------|----------|
| Classifier_ParseSidecar_ValidJson_ReturnsSidecar | Unit |
| Classifier_ParseSidecar_MissingOptionalFields_ReturnsNone | Unit |
| Classifier_ParseSidecar_InvalidJson_ReturnsError | Unit |
| Classifier_IsDuplicate_NoExistingDoc_ReturnsFalse | Integration |
| Classifier_IsDuplicate_ExistingDoc_ReturnsTrue | Integration |
| Classifier_IsDuplicate_DifferentHash_ReturnsFalse | Integration |
| Classifier_ComputeSha256_ReturnsConsistentHash | Unit |
| Classifier_ComputeSha256_DifferentContent_DifferentHash | Unit |
| Classifier_TryLoadSidecar_WithMetaFile_ReturnsSome | Unit |
| Classifier_TryLoadSidecar_NoMetaFile_ReturnsNone | Unit |
| Classifier_ProcessFile_ClassifiesAndMovesFile | Integration |
| Classifier_ProcessFile_WithSidecar_UsesMetadataForClassification | Integration |
| Classifier_ProcessFile_DuplicateHash_SkipsFile | Integration |
| Classifier_ProcessFile_MissingFile_ReturnsOk | Integration |
| Classifier_ProcessFile_UnmatchedFile_GoesToUnsorted | Integration |
| Classifier_ProcessFile_InsertsDocumentRecord | Integration |
| Classifier_ProcessFile_SubjectBasedClassification | Integration |
| Classifier_ProcessFile_SidecarEmailDate_StoredInDb | Integration |
| Classifier_SuggestRules_FindsUnsortedMatchInCategory | Integration |
| Classifier_SuggestRules_NoUnsortedDocs_ReturnsEmpty | Integration |
| Classifier_ReclassifyUnsortedBatch_ContentRuleMatch | Integration |
| Classifier_Reconcile_NewFileOnDisk_DetectedAsNewOnDisk | Integration |
| Classifier_Reconcile_FileInDb_NoAction | Integration |
| Classifier_Reconcile_EmptyArchive_ReturnsEmpty | Integration |
| Classifier_TryLoadSidecar_InvalidJson_ReturnsNone | Unit |
| Classifier_TryLoadSidecar_ReadThrows_ReturnsNone | Unit |

## EmailSyncTests.fs (28 tests)

| Test | Category |
|------|----------|
| EmailSync_SanitiseFileName_RemovesInvalidChars | Unit |
| EmailSync_SanitiseFileName_CollapsesUnderscores | Unit |
| EmailSync_SanitiseFileName_EmptyReturnsAttachment | Unit |
| EmailSync_SanitiseFileName_WhitespaceOnlyReturnsAttachment | Unit |
| EmailSync_SanitiseFileName_NormalNameUnchanged | Unit |
| EmailSync_BuildStandardName_IncludesDateSenderName | Unit |
| EmailSync_BuildStandardName_NoDate_UsesUndated | Unit |
| EmailSync_BuildStandardName_NoSender_UsesUnknown | Unit |
| EmailSync_ComputeSha256_DeterministicHash | Unit |
| EmailSync_ComputeSha256_DifferentDataDifferentHash | Unit |
| EmailSync_BuildSidecar_ContainsAllFields | Unit |
| EmailSync_SerialiseSidecar_ProducesValidJson | Unit |
| EmailSync_LoadSyncState_NoState_ReturnsNone | Integration |
| EmailSync_LoadSyncState_AfterSync_ReturnsSome | Integration |
| EmailSync_SyncAccount_NoMessages_ReturnsZeroCounts | Integration |
| EmailSync_SyncAccount_WithAttachments_DownloadsAndRecords | Integration |
| EmailSync_SyncAccount_DuplicateHash_SkipsDownload | Integration |
| EmailSync_SyncAccount_SmallAttachment_FilteredByMinSize | Integration |
| EmailSync_SyncAccount_AlreadyProcessedMessage_Skipped | Integration |
| EmailSync_DryRun_ListsMessagesWithAttachments | Integration |
| EmailSync_DryRun_NoMessages_ReturnsEmpty | Integration |
| EmailSync_SyncAccount_UpdatesSyncState | Integration |
| Backfill_DisabledConfig_Skips | Integration |
| Backfill_EmptyPage_CompletesImmediately | Integration |
| Backfill_LoadBackfillState_EmptyDb_ReturnsDefaults | Integration |
| EmailSync_SyncAll_EmptyMessages_ReturnsResultPerAccount | Integration |
| EmailSync_SyncAll_MultipleAccounts_SyncsEach | Integration |
| EmailSync_DryRunAll_EmptyMessages_ReturnsEmpty | Integration |

## FolderWatcherTests.fs (29 tests)

| Test | Category |
|------|----------|
| FolderWatcher_MatchesAnyPattern_StarDotPdf_MatchesPdf | Unit |
| FolderWatcher_MatchesAnyPattern_StarDotPdf_DoesNotMatchTxt | Unit |
| FolderWatcher_MatchesAnyPattern_WildcardStatement_MatchesContaining | Unit |
| FolderWatcher_MatchesAnyPattern_MultiplePatterns_MatchesAny | Unit |
| FolderWatcher_MatchesAnyPattern_EmptyPatterns_MatchesAll | Unit |
| FolderWatcher_MatchesAnyPattern_CaseInsensitive | Unit |
| FolderWatcher_MatchesAnyPattern_QuestionMark_MatchesSingleChar | Unit |
| FolderWatcher_BuildStandardName_FormatsCorrectly | Unit |
| FolderWatcher_BuildStandardName_SanitisesInvalidChars | Unit |
| FolderWatcher_SanitiseFileName_CollapsesUnderscores | Unit |
| FolderWatcher_SanitiseFileName_EmptyString_ReturnsFallback | Unit |
| FolderWatcher_IsDuplicate_NoExistingDoc_ReturnsFalse | Integration |
| FolderWatcher_IsDuplicate_ExistingDoc_ReturnsTrue | Integration |
| FolderWatcher_BuildSidecar_HasCorrectSourceType | Unit |
| FolderWatcher_SerialiseSidecar_ProducesValidJson | Unit |
| FolderWatcher_ProcessFile_CopiesMatchingFile | Integration |
| FolderWatcher_ProcessFile_SkipsNonMatchingPattern | Integration |
| FolderWatcher_ProcessFile_DetectsDuplicate | Integration |
| FolderWatcher_ProcessFile_MissingFile_ReturnsSkipped | Integration |
| FolderWatcher_ProcessFile_UsesSafeCopyRename | Integration |
| FolderWatcher_AddWatchFolder_AddsToConfig | Unit |
| FolderWatcher_AddWatchFolder_EmptyPatterns_DefaultsToStar | Unit |
| FolderWatcher_AddWatchFolder_Duplicate_ReturnsError | Unit |
| FolderWatcher_RemoveWatchFolder_RemovesFromConfig | Unit |
| FolderWatcher_RemoveWatchFolder_NotFound_ReturnsError | Unit |
| FolderWatcher_ListWatchFolders_ReportsStatus | Unit |
| FolderWatcher_ScanFolder_ProcessesAllMatchingFiles | Integration |
| FolderWatcher_GlobToRegex_StarDotPdf | Unit |
| FolderWatcher_GlobToRegex_WildcardMiddle | Unit |

## EmbeddingTests.fs (61 tests)

| Test | Category |
|------|----------|
| Embeddings_ChunkText_EmptyString_ReturnsEmpty | Unit |
| Embeddings_ChunkText_WhitespaceOnly_ReturnsEmpty | Unit |
| Embeddings_ChunkText_ShortText_ReturnsSingleChunk | Unit |
| Embeddings_ChunkText_ExactlyChunkSize_ReturnsSingleChunk | Unit |
| Embeddings_ChunkText_LongText_ProducesOverlappingChunks | Unit |
| Embeddings_ChunkText_Overlap_ChunksShareContent | Unit |
| Embeddings_ChunkText_SentenceBoundary_SplitsOnSentence | Unit |
| Embeddings_ChunkText_AllContent_IsCovered | Unit |
| Embeddings_BlobRoundTrip_PreservesData | Unit |
| Embeddings_BlobRoundTrip_EmptyArray | Unit |
| SemanticSearch_CosineSimilarity_IdenticalVectors_ReturnsOne | Unit |
| SemanticSearch_CosineSimilarity_OrthogonalVectors_ReturnsZero | Unit |
| SemanticSearch_CosineSimilarity_OppositeVectors_ReturnsNegOne | Unit |
| SemanticSearch_CosineSimilarity_EmptyVectors_ReturnsZero | Unit |
| SemanticSearch_CosineSimilarity_DifferentLengths_ReturnsZero | Unit |
| SemanticSearch_CosineSimilarity_ZeroVector_ReturnsZero | Unit |
| SemanticSearch_RRF_EmptyLists_ReturnsEmpty | Unit |
| SemanticSearch_RRF_SingleList_ScoresCorrectly | Unit |
| SemanticSearch_RRF_OverlappingLists_BothContribute | Unit |
| SemanticSearch_RRF_PreservesAllDocuments | Unit |
| SemanticSearch_RRF_TopRankedInBothLists_WinsOverall | Unit |
| Embeddings_EmbedDocument_ChunksAndStores | Integration |
| Embeddings_EmbedDocument_UpdatesDocumentMetadata | Integration |
| Embeddings_EmbedDocument_EmptyText_ReturnsZero | Integration |
| Embeddings_EmbedDocument_FailingClient_ReportsErrors | Integration |
| SemanticSearch_KeywordSearch_FindsMatchingDocuments | Integration |
| Embeddings_ChunkText_ShortText_SingleChunk | Unit |
| Embeddings_ChunkText_LongText_MultipleChunks | Unit |
| Embeddings_ChunkText_OverlapPresent | Unit |
| Embeddings_BlobRoundTrip_PreservesValues | Unit |
| Embeddings_InitSchema_CreatesChunkTable | Integration |
| Embeddings_StoreChunk_InsertsRow | Integration |
| Embeddings_EmbedDocument_StoresChunksAndUpdatesDoc | Integration |
| Embeddings_EmbedDocument_EmptyText_ReturnsOkZero | Integration |
| Embeddings_EmbedDocument_FailingClient_ReturnsError | Integration |
| Embeddings_BatchEmbed_UnavailableClient_ReturnsError | Integration |
| Embeddings_BatchEmbed_NoDocs_ReturnsOkZero | Integration |
| Embeddings_BatchEmbed_WithDocs_EmbedsSuccessfully | Integration |
| Embeddings_BatchEmbed_WithLimit_RespectsLimit | Integration |
| Embeddings_BatchEmbed_Force_ReEmbedsAlreadyEmbedded | Integration |
| Embeddings_BatchEmbed_ProgressCallback_Called | Integration |
| Embeddings_StoreChunk_NoEmbedding_InsertsNullBlob | Integration |
| Embeddings_BlobRoundTrip_SingleElement_Preserves | Unit |
| Embeddings_BlobRoundTrip_768Dims_Preserves | Unit |
| Embeddings_BlobRoundTrip_NegativeValues_Preserves | Unit |
| Embeddings_ChunkText_OnlyWhitespace_ReturnsEmpty | Unit |
| Embeddings_ChunkText_VerySmallChunkSize_Works | Unit |
| Embeddings_ChunkText_ZeroOverlap_NoOverlap | Unit |
| Embeddings_ChunkText_LargeOverlap_StillProgresses | Unit |
| Embeddings_BlobRoundTrip_1536Dims_Preserves | Unit |
| Embeddings_ChunkText_SingleChar_ReturnsSingleChunk | Unit |
| Embeddings_ChunkText_ExactlyAtBoundaryPlusOne_ProducesTwoChunks | Unit |
| Embeddings_BatchEmbed_WithProgressCallback_ReportsCorrectTotal | Integration |
| Embeddings_BatchEmbed_SkipsDocsWithNullText | Integration |
| Embeddings_BatchEmbed_SkipsDocsWithEmptyText | Integration |
| Embeddings_EmbedDocument_WhitespaceOnlyText_ReturnsOkZero | Integration |
| Embeddings_EmbeddingToBlob_ByteLength_IsCorrect | Unit |
| Embeddings_StoreChunk_WithEmbedding_SetsEmbeddedAt | Integration |
| Embeddings_EmbedDocument_ShortText_SingleChunk | Integration |
| Embeddings_StoreChunk_WithEmbedding_StoresBlob | Integration |
| Embeddings_BlobRoundtrip_PreservesValues | Property |

## SearchTests.fs (30 tests)

| Test | Category |
|------|----------|
| Search_SanitiseQuery_RemovesFtsOperators | Unit |
| Search_SanitiseQuery_QuotesTokens | Unit |
| Search_SanitiseQuery_EmptyInput_ReturnsEmpty | Unit |
| Search_SanitiseQuery_WhitespaceOnly_ReturnsEmpty | Unit |
| Search_SanitiseQuery_SpecialChars_Stripped | Unit |
| Search_SanitiseQuery_SingleToken_Quoted | Unit |
| Search_BuildQuery_BasicQuery_IncludesMatchAndBm25 | Unit |
| Search_BuildQuery_WithCategory_AddsFilter | Unit |
| Search_BuildQuery_WithSender_AddsLikeFilter | Unit |
| Search_BuildQuery_WithDateRange_AddsFilters | Unit |
| Search_BuildQuery_WithAccount_AddsFilter | Unit |
| Search_BuildQuery_EmptyQuery_ReturnsNoResults | Unit |
| Search_BuildQuery_LimitIsParameterised | Unit |
| Search_MapRow_MapsAllFields | Unit |
| Search_MapRow_HandlesMissingFields | Unit |
| Search_MapRow_HandlesDbNullValues | Unit |
| Search_Execute_FindsMatchingDocument | Integration |
| Search_Execute_FilterByCategory_ExcludesOthers | Integration |
| Search_Execute_NoMatch_ReturnsEmpty | Integration |
| Search_Execute_ReturnsSnippet | Integration |
| Search_Execute_EmptyQuery_ReturnsEmpty | Integration |
| Search_Execute_MultipleResults_RankedByRelevance | Integration |
| Search_Execute_WithSourceTypeFilter_FiltersCorrectly | Integration |
| Search_Execute_WithCategoryFilter_FiltersCorrectly | Integration |
| Search_Execute_WithSenderFilter_FiltersCorrectly | Integration |
| Search_Execute_WithDateRange_FiltersCorrectly | Integration |
| Search_SanitiseQuery_SpecialChars_Cleaned | Integration |
| Search_ExecuteUnified_ReturnsResults | Integration |
| Search_SanitiseQuery_SpecialChars_SomeCleaned | Unit |
| Search_DefaultFilter_HasCorrectDefaults | Unit |

## McpTests.fs (59 tests)

| Test | Category |
|------|----------|
| McpServer_ParseRequest_ValidRequest_ReturnsOk | Unit |
| McpServer_ParseRequest_MissingMethod_ReturnsError | Unit |
| McpServer_ParseRequest_InvalidJson_ReturnsError | Unit |
| McpServer_SerialiseResponse_ContainsJsonRpcVersion | Unit |
| McpServer_SerialiseResponse_ErrorResponse_ContainsErrorField | Unit |
| McpServer_Dispatch_Initialize_ReturnsCapabilities | Integration |
| McpServer_Dispatch_ToolsList_ReturnsAllTools | Integration |
| McpServer_Dispatch_UnknownMethod_ReturnsError | Integration |
| McpServer_Dispatch_ToolsCallUnknownTool_ReturnsError | Integration |
| McpServer_Dispatch_ToolsCallSearch_ReturnsContent | Integration |
| McpServer_Dispatch_ToolsCallStats_ReturnsStats | Integration |
| McpServer_Dispatch_ToolsCallListCategories_ReturnsCategories | Integration |
| McpTools_IsPathSafe_RelativePath_ReturnsOk | Unit |
| McpTools_IsPathSafe_PathTraversal_ReturnsError | Unit |
| McpTools_IsPathSafe_DotDotInMiddle_ReturnsError | Unit |
| McpTools_IsPathSafe_AbsolutePath_ReturnsError | Unit |
| McpTools_IsPathSafe_WindowsAbsolutePath_ReturnsError | Unit |
| McpTools_IsPathSafe_EmptyPath_ReturnsError | Unit |
| McpTools_IsPathSafe_WhitespacePath_ReturnsError | Unit |
| McpTools_Search_EmptyQuery_ReturnsError | Integration |
| McpTools_GetDocument_MissingIdAndPath_ReturnsNotFound | Integration |
| McpTools_GetDocument_ValidId_ReturnsDocument | Integration |
| McpTools_ReadFile_PathTraversal_ReturnsError | Unit |
| McpTools_ReadFile_MissingPath_ReturnsError | Unit |
| McpServer_ProcessMessage_CompleteRoundTrip_ValidJsonRpc | Integration |
| MCP_ListReminders_ReturnsActiveReminders | Integration |
| MCP_UpdateReminder_MarkComplete_ChangesStatus | Integration |
| McpServer_GetDocumentContent_Markdown_ReturnsStructuredContent | Integration |
| McpTools_ListDocumentsFeed_ReturnsDocuments | Integration |
| McpTools_ListDocumentsFeed_EmptyDb_ReturnsEmptyArray | Integration |
| McpTools_GetFeedStats_ReturnsStats | Integration |
| McpTools_GetProcessingQueue_ReturnsQueueInfo | Integration |
| McpTools_ReextractDocument_ValidId_ReturnsSuccess | Integration |
| McpTools_ReextractDocument_MissingId_ReturnsError | Integration |
| McpTools_ReclassifyDocument_MissingId_ReturnsError | Integration |
| McpTools_ReclassifyDocument_MissingCategory_ReturnsError | Integration |
| McpTools_GetDocumentContent_MissingId_ReturnsError | Integration |
| McpTools_GetDocumentContent_ValidId_ReturnsContent | Integration |
| McpTools_ReadFile_ValidPath_ReturnsContent | Unit |
| McpTools_ListDocumentsFeed_WithCategory_FiltersCorrectly | Integration |
| McpTools_UpdateReminder_Snooze_ChangesStatus | Integration |
| McpTools_UpdateReminder_Dismiss_ChangesStatus | Integration |
| McpTools_UpdateReminder_UnknownAction_ReturnsError | Integration |
| McpTools_UpdateReminder_MissingFields_ReturnsError | Integration |
| McpTools_UpdateReminder_Paid_IsAlias_ForComplete | Integration |
| McpTools_ReclassifyDocument_ValidDoc_Reclassifies | Integration |
| McpTools_ReadFile_MissingPathArg_ReturnsError | Unit |
| McpTools_ReadFile_NonexistentFile_ReturnsError | Unit |
| McpTools_GetProcessingQueue_WithDocs_ReturnsJsonObject | Integration |
| McpServer_Dispatch_ToolsCallGetDocument_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallListDocuments_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallGetProcessingQueue_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallReadFile_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallListReminders_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallGetFeedStats_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallGetDocumentContent_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallReclassify_ReturnsResult | Integration |
| McpServer_Dispatch_ToolsCallReextract_ReturnsResult | Integration |
| McpServer_Dispatch_DeepExtract_NoDeps_ReturnsError | Integration |

## EmailBodyTests.fs (24 tests)

| Test | Category |
|------|----------|
| EmailSync_StripHtml_RemovesTags | Unit |
| EmailSync_StripHtml_DecodesEntities | Unit |
| EmailSync_StripHtml_CollapsesWhitespace | Unit |
| EmailSync_StripHtml_EmptyInput_ReturnsEmpty | Unit |
| EmailSync_StripHtml_PlainText_Unchanged | Unit |
| EmailSync_StripHtml_ComplexHtml_ProducesCleanText | Unit |
| EmailSync_StripHtml_DecodesNbsp | Unit |
| MessagesFts_InsertTrigger_IndexesBodyText | Integration |
| MessagesFts_SearchBySubject_FindsMessage | Integration |
| MessagesFts_SearchBySender_FindsMessage | Integration |
| MessagesFts_NoMatch_ReturnsZero | Integration |
| MessagesFts_UpdateTrigger_ReindexesOnUpdate | Integration |
| Search_ExecuteEmailSearch_FindsMessageByBody | Integration |
| Search_ExecuteEmailSearch_ReturnsSnippet | Integration |
| Search_ExecuteEmailSearch_NoMatch_ReturnsEmpty | Integration |
| Search_ExecuteUnified_MergesDocumentAndEmailResults | Integration |
| Search_ExecuteUnified_RespectsLimit | Integration |
| Search_ExecuteUnified_SortedByRelevance | Integration |
| Database_SchemaV2_HasBodyTextColumn | Integration |
| Database_SchemaV2_HasThreadIdColumn | Integration |
| Database_SchemaV2_HasMessagesFts | Integration |
| Database_SchemaV2_VersionIs2 | Integration |
| EmailSync_SyncAccount_FetchesBodyWhenMissing | Integration |
| EmailSync_SyncAccount_SkipsBodyFetchWhenPresent | Integration |

## MarkdownTests.fs (26 tests)

| Test | Category |
|------|----------|
| Markdown_RenderFrontmatter_ContainsAllFields | Unit |
| Markdown_RenderFrontmatter_OmitsNoneFields | Unit |
| Markdown_ExtractDate_FindsDates | Unit |
| Markdown_ExtractDate_NoDate_ReturnsNone | Unit |
| Markdown_ExtractAmount_FindsAmounts | Unit |
| Markdown_ExtractAmount_MultiplePicks_LargestAmount | Unit |
| Markdown_ExtractAbn_FindsAbnAcn | Unit |
| Markdown_ExtractVendor_FirstLine | Unit |
| Markdown_CsvToMarkdown_BasicTable | Unit |
| Markdown_CsvToMarkdown_QuotedFields | Unit |
| Markdown_CsvToMarkdown_Empty_ReturnsPlaceholder | Unit |
| Markdown_TextToMarkdown_PreservesParagraphs | Unit |
| Markdown_TextToMarkdown_EmptyReturnsPlaceholder | Unit |
| Markdown_ChunkByHeadings_SplitsOnHeadings | Unit |
| Markdown_ChunkByHeadings_ShortTextSingleChunk | Unit |
| Markdown_ChunkByHeadings_LongSectionFallsBackToCharSplit | Unit |
| Markdown_BuildConversion_IncludesFrontmatterAndBody | Unit |
| Markdown_WriteSidecar_CreatesFile | Unit |
| Markdown_ProcessDocument_WritesMarkdownSidecar | Integration |
| Markdown_ProcessDocument_NoText_ReturnsError | Integration |
| Markdown_ProcessDocument_NonexistentDoc_ReturnsError | Integration |

## ReminderTests.fs (15 tests)

| Test | Category |
|------|----------|
| Reminders_DetectBill_InvoiceWithDueDate_CreatesReminder | Unit |
| Reminders_DetectBill_WrongCategory_ReturnsNone | Unit |
| Reminders_DetectBill_OldDate_ReturnsNone | Unit |
| Reminders_DetectBill_NoAmount_ReturnsNone | Unit |
| Reminders_DetectBill_FutureDate_Within60Days_CreatesReminder | Unit |
| Reminders_DetectBill_FutureDate_Beyond60Days_ReturnsNone | Unit |
| Reminders_EvaluateNew_InsertsReminders | Integration |
| Reminders_EvaluateNew_DeduplicatesExisting | Integration |
| Reminders_MarkCompleted_ChangesStatus | Integration |
| Reminders_Snooze_HidesUntilExpiry | Integration |
| Reminders_Dismiss_PermanentlyRemoves | Integration |
| Reminders_GetSummary_CorrectCounts | Integration |
| Reminders_UnsnoozeExpired_WithExpiredSnooze_UnsnoozesReminder | Integration |
| Reminders_UnsnoozeExpired_NoSnoozedReminders_ReturnsZero | Integration |
| Reminders_UnsnoozeExpired_BeforeExpiry_ReturnsZero | Integration |

## ChatTests.fs (31 tests)

| Test | Category |
|------|----------|
| Chat_FormatResults_EmptyList_ReturnsEmptyString | Unit |
| Chat_FormatResults_SingleResult_IncludesCategoryAndName | Unit |
| Chat_FormatResults_IncludesSenderWhenPresent | Unit |
| Chat_FormatResults_IncludesAmountWhenPresent | Unit |
| Chat_FormatResults_TruncatesTo10Results | Unit |
| Chat_SystemPrompt_IsNotEmpty | Unit |
| Chat_Query_KeywordMode_ReturnsResults | Integration |
| Chat_Query_AiMode_ReturnsAiSummary | Integration |
| Chat_Query_EmptyQuery_ReturnsEmpty | Integration |
| Chat_ProviderFromConfig_Ollama_ReturnsOllamaProvider | Unit |
| Chat_Query_AiError_ReturnsErrorMessage | Integration |
| Chat_Query_NoResults_AiSummaryIsNone | Integration |
| Chat_Query_MultipleResults_ReturnsAll | Integration |
| Chat_Query_WithFakeChatProvider_ReturnsCustomResponse | Integration |
| Chat_ProviderFromConfig_AzureOpenAI_WithValidConfig_ReturnsAzureProvider | Unit |
| Chat_ProviderFromConfig_AzureOpenAI_EmptyEndpoint_FallsBackToOllama | Unit |
| Chat_ProviderFromConfig_AzureOpenAI_EmptyApiKey_FallsBackToOllama | Unit |
| Chat_BuildUserPrompt_IncludesQueryAndContext | Unit |
| Chat_FormatResults_IncludesSubjectAndDateAndVendor | Unit |
| Chat_BuildUserPrompt_EmptyContext_StillIncludesQuery | Unit |
| Chat_FormatResults_NoOptionalFields_FormatsCleanly | Unit |
| Chat_Query_AiEnabled_NoResults_SkipsAiCall | Integration |
| Chat_SystemPrompt_ContainsDocumentTypes | Unit |
| Chat_FormatResults_MultipleResults_NumbersCorrectly | Unit |
| Chat_OllamaProvider_ConnectionRefused_ReturnsError | Unit |
| Chat_AzureOpenAIProvider_ConnectionRefused_ReturnsError | Unit |
| Chat_ProviderFromConfig_WhitespaceEndpoint_FallsBackToOllama | Unit |
| Chat_BuildUserPrompt_ContainsAnswerBrieflyInstruction | Unit |
| Chat_FormatResults_ResultWithOnlySenderAndSnippet_FormatsCorrectly | Unit |
| Chat_Query_NoResults_AiDisabled_ReturnsEmptyResults | Integration |
| Chat_Query_WithResults_AiDisabled_ReturnsResultsNoSummary | Integration |

## StatsTests.fs (22 tests)

| Test | Category |
|------|----------|
| Stats_GetIndexStats_EmptyDb_ReturnsZeros | Integration |
| Stats_GetIndexStats_WithDocuments_ReturnsCorrectCounts | Integration |
| Stats_GetCategoryCounts_NonexistentDir_ReturnsEmpty | Unit |
| Stats_GetAccountStats_EmptyDb_ReturnsEmpty | Integration |
| Stats_GetAccountStats_WithSyncState_ReturnsAccounts | Integration |
| Stats_GetIndexStats_WithExtractedAndEmbedded_CountsCorrectly | Integration |
| Stats_GetIndexStats_NonExistentDbPath_SizeIsZero | Integration |
| Stats_GetAccountStats_MultipleAccounts_ReturnsAll | Integration |
| Stats_GetCategoryCounts_WithRealDir_ReturnsCounts | Integration |
| Stats_GetCategoryCounts_EmptySubdirs_ExcludesEmpty | Integration |
| Stats_GetAccountStats_NullSyncAt_ReturnsNone | Integration |
| Stats_getPipelineCounts_EmptyDbAndFs_AllZeroes | Unit |
| Stats_getPipelineCounts_FilesInIntake_CountsIntake | Unit |
| Stats_getPipelineCounts_DocumentsNeedingExtraction_CountsExtracting | Unit |
| Stats_getPipelineCounts_UnsortedExtractedDocs_CountsClassifying | Unit |
| Stats_GetExtractionQueue_EmptyDb_ReturnsEmpty | Integration |
| Stats_GetExtractionQueue_ReturnsUnextractedDocs | Integration |
| Stats_GetExtractionQueue_RespectsLimit | Integration |
| Stats_GetRecentClassifications_EmptyDb_ReturnsEmpty | Integration |
| Stats_GetRecentClassifications_ReturnsClassifiedDocs | Integration |
| Stats_GetTierBreakdown_EmptyDb_AllZeroes | Integration |
| Stats_GetTierBreakdown_CountsByTier | Integration |

## DomainTests.fs (42 tests)

| Test | Category |
|------|----------|
| Domain_SourceType_RoundTrip | Unit |
| Domain_SourceType_UnknownString_ReturnsError | Unit |
| Domain_ReminderStatus_RoundTrip | Unit |
| Domain_ChatProviderKind_RoundTrip | Unit |
| Domain_ChatProviderKind_AlternateFormats_ParseCorrectly | Unit |
| Domain_ChatProviderKind_UnknownString_ReturnsError | Unit |
| ClassificationRule_Describe_DefaultRule_ContainsDefault | Unit |
| ClassificationRule_Describe_FilenameRule_ContainsName | Unit |
| ClassificationRule_Describe_SubjectRule_ContainsSubject | Unit |
| ClassificationRule_Describe_DomainRule_ContainsDomain | Unit |
| TaskResult_Map_OkValue_Transforms | Unit |
| TaskResult_Map_Error_PreservesError | Unit |
| TaskResult_Bind_OkToOk_Chains | Unit |
| TaskResult_Bind_ErrorShortCircuits | Unit |
| TaskResult_MapError_Error_Transforms | Unit |
| TaskResult_MapError_Ok_PreservesOk | Unit |
| Prelude_FoldTask_EmptyList_ReturnsInit | Unit |
| Prelude_FoldTask_AccumulatesValues | Unit |
| RowReader_OptString_Present_ReturnsSome | Unit |
| RowReader_OptString_Missing_ReturnsNone | Unit |
| RowReader_OptString_DBNull_ReturnsNone | Unit |
| RowReader_OptInt64_Present_ReturnsSome | Unit |
| RowReader_OptInt64_Missing_ReturnsNone | Unit |
| RowReader_OptInt64_FromInt_Converts | Unit |
| RowReader_String_Present_ReturnsValue | Unit |
| RowReader_String_Missing_ReturnsFallback | Unit |
| RowReader_Int64_Present_ReturnsValue | Unit |
| RowReader_Int64_FromInt_Converts | Unit |
| RowReader_Float_Present_ReturnsValue | Unit |
| RowReader_Float_Missing_ReturnsFallback | Unit |
| RowReader_OptFloat_Present_ReturnsSome | Unit |
| RowReader_OptFloat_FromInt64_Converts | Unit |
| RowReader_OptDateTimeOffset_ValidString_ReturnsSome | Unit |
| RowReader_OptDateTimeOffset_Invalid_ReturnsNone | Unit |
| RowReader_Raw_ReturnsOriginalMap | Unit |

## ExtractionFieldTests.fs (47 tests)

| Test | Category |
|------|----------|
| Extraction_TryExtractDate_FindsDates | Unit |
| Extraction_TryExtractDate_NoDate_ReturnsNone | Unit |
| Extraction_TryExtractDate_EmptyString_ReturnsNone | Unit |
| Extraction_TryExtractAmount_FindsAmounts | Unit |
| Extraction_TryExtractAmount_NoAmount_ReturnsNone | Unit |
| Extraction_TryExtractAbn_FindsAbnAcn | Unit |
| Extraction_TryExtractAbn_NoAbn_ReturnsNone | Unit |
| Extraction_TryExtractVendor_FindsCompanyName | Unit |
| Extraction_IsLikelyScanned_ShortText_ReturnsTrue | Unit |
| Extraction_IsLikelyScanned_LongText_ReturnsFalse | Unit |
| Extraction_IsPdf_DetectsCorrectly | Unit |
| Extraction_IsImage_DetectsCorrectly | Unit |
| Extraction_AnalyseText_PopulatesFields | Unit |
| Extraction_ProcessDocument_PdfFile_ExtractsAndUpdatesDb | Integration |
| Extraction_ProcessDocument_MissingFile_ReturnsError | Integration |
| Extraction_ProcessDocument_ExtractorFails_ReturnsError | Integration |
| Extraction_ExtractBatch_ProcessesUnextractedDocs | Integration |
| Extraction_ExtractBatch_RespectsLimit | Integration |
| Extraction_ExtractBatch_CategoryFilter_OnlyProcessesMatching | Integration |
| Extraction_ExtractFromBytes_PlainText_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_Markdown_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_LogFile_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_Csv_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_UnsupportedType_ReturnsError | Unit |
| Extraction_ExtractFromBytes_Image_CallsImageExtractor | Unit |
| Extraction_ExtractFromBytes_ImagePng_CallsImageExtractor | Unit |
| Extraction_ExtractFromBytes_ImageError_ReturnsError | Unit |
| Extraction_StripMarkdown_RemovesHeadings | Unit |
| Extraction_StripMarkdown_RemovesBoldItalic | Unit |
| Extraction_StripMarkdown_RemovesTableSyntax | Unit |
| Extraction_StripMarkdown_RemovesFrontmatter | Unit |
| Extraction_StripMarkdown_EmptyInput_ReturnsEmpty | Unit |
| Extraction_AnalyseText_WithMarkdown_SetsMarkdownField | Unit |
| Extraction_AnalyseText_WithoutMarkdown_MarkdownIsNone | Unit |
| Extraction_ProcessDocument_StoresMarkdownSeparately | Integration |

## PdfStructureTests.fs (34 tests)

| Test | Category |
|------|----------|
| PdfStructure_WordsToLines_GroupsByYProximity | Unit |
| PdfStructure_WordsToLines_SingleWord_ReturnsSingleLine | Unit |
| PdfStructure_WordsToLines_SameLineDifferentX_SortsByXAscending | Unit |
| PdfStructure_WordsToLines_ThreeDistinctLines_TopToBottom | Unit |
| PdfStructure_LinesToText_PreservesReadingOrder | Unit |
| PdfStructure_LinesToText_EmptyLines_ReturnsEmpty | Unit |
| PdfStructure_LinesToText_SingleLine_NoTrailingNewline | Unit |
| PdfStructure_DetectBodyFontSize_ReturnsMostCommonSize | Unit |
| PdfStructure_DetectBodyFontSize_EmptyLines_ReturnsDefault | Unit |
| PdfStructure_DetectHeadings_LargeFont_ReturnsH1 | Unit |
| PdfStructure_DetectHeadings_BoldFont_ReturnsH2 | Unit |
| PdfStructure_DetectHeadings_AllCaps_ReturnsH3 | Unit |
| PdfStructure_DetectHeadings_BodyText_ReturnsNone | Unit |
| PdfStructure_FindColumnBoundaries_ThreeColumns_ReturnsThreeBoundaries | Unit |
| PdfStructure_IsTableRegion_AlignedRows_ReturnsTrue | Unit |
| PdfStructure_IsTableRegion_ParagraphText_ReturnsFalse | Unit |
| PdfStructure_ExtractTableCells_AssignsWordsToCorrectColumns | Unit |
| PdfStructure_DetectTables_BankStatement_ExtractsTransactionTable | Unit |
| PdfStructure_IsContinuation_SameColumns_ReturnsTrue | Unit |
| PdfStructure_IsContinuation_DifferentColumns_ReturnsFalse | Unit |
| PdfStructure_MergeMultiPageTables_CombinesRows_KeepsSingleHeader | Unit |
| PdfStructure_DetectKV_ColonSeparated_ReturnsKV | Unit |
| PdfStructure_DetectKV_GapSeparated_ReturnsKV | Unit |
| PdfStructure_DetectKV_ParagraphText_ReturnsNone | Unit |
| PdfStructure_IsCidEncoded_WithCidSequences_ReturnsTrue | Unit |
| PdfStructure_IsCidEncoded_NormalText_ReturnsFalse | Unit |
| PdfStructure_CalculateConfidence_FullyDecoded_ReturnsHigh | Unit |
| PdfStructure_CalculateConfidence_MostlyCid_ReturnsLow | Unit |
| PdfStructure_ExtractStructured_GeneratedPdf_ReturnsContent | Unit |
| PdfStructure_ToMarkdown_HeadingsAndTables_WellFormed | Unit |
| PdfStructure_ToMarkdown_Frontmatter_IncludesMetadata | Unit |
| PdfStructure_ExtractLetters_InvalidBytes_ReturnsEmptyList | Unit |
| PdfStructure_ExtractLines_GeneratedPdf_ExtractsText | Integration |
| PdfStructure_LinesToText_LineCount_MatchesNewlineCount | Property |

## DocumentFeedTests.fs (21 tests)

| Test | Category |
|------|----------|
| DocumentFeed_ListDocuments_SinceId0_ReturnsAll | Integration |
| DocumentFeed_ListDocuments_SinceId_ReturnsOnlyNewer | Integration |
| DocumentFeed_ListDocuments_FilterByCategory | Integration |
| DocumentFeed_ListDocuments_Limit_RespectsLimit | Integration |
| DocumentFeed_GetFeedStats_ReturnsCorrectCounts | Integration |
| DocumentFeed_GetContent_Markdown_ReturnsExtractedText | Integration |
| DocumentFeed_GetContent_Text_StripsFrontmatter | Integration |
| DocumentFeed_GetContent_InvalidId_ReturnsError | Integration |
| DocumentFeed_GetContent_Raw_ReturnsFileContent | Integration |
| DocumentFeed_GetContent_Raw_MissingFile_ReturnsError | Integration |
| DocumentFeed_ParseFormat_Text_ReturnsSome | Unit |
| DocumentFeed_ParseFormat_Markdown_ReturnsSome | Unit |
| DocumentFeed_ParseFormat_Raw_ReturnsSome | Unit |
| DocumentFeed_ParseFormat_CaseInsensitive | Unit |
| DocumentFeed_ParseFormat_Unknown_ReturnsNone | Unit |
| DocumentFeed_FeedDocToJson_IncludesAllFields | Unit |
| DocumentFeed_FeedDocToJson_OmitsNoneFields | Unit |
| DocumentFeed_FeedStatsToJson_IncludesFields | Unit |
| DocumentFeed_GetContent_Markdown_NoExtractedText_ReturnsError | Integration |
| DocumentFeed_ListDocuments_EmptyDb_ReturnsEmpty | Integration |
| DocumentFeed_GetFeedStats_EmptyDb_ReturnsZeros | Integration |

## ContentClassifierTests.fs (25 tests)

| Test | Category |
|------|----------|
| ContentClassifier_Evaluate_PayslipKeywords_MatchesPayslips | Unit |
| ContentClassifier_Evaluate_BankStatementHeaders_MatchesBankStatements | Unit |
| ContentClassifier_Evaluate_NoMatch_ReturnsNone | Unit |
| ContentClassifier_Classify_MultipleMatches_ReturnsBestConfidence | Unit |
| ContentClassifier_Classify_NoRulesMatch_ReturnsNone | Unit |
| ContentClassifier_BuildPrompt_TruncatesTo2000Chars | Unit |
| ContentClassifier_BuildPrompt_ShortText_NoTruncation | Unit |
| ContentClassifier_ParseResponse_ValidJson_ReturnsCategory | Unit |
| ContentClassifier_ParseResponse_MarkdownCodeBlock_ExtractsJson | Unit |
| ContentClassifier_ParseResponse_InvalidJson_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_MissingCategory_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_EmptyCategory_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_ValidJson_ReturnsValues | Unit |
| ContentClassifier_ParseResponse_NoReasoning_DefaultsToEmpty | Unit |
| ContentClassifier_BuildPrompt_IncludesCategoriesAndContent | Unit |
| ContentClassifier_BuildPrompt_LongText_Truncates | Unit |
| ContentClassifier_BuildPrompt_AlwaysTruncatesTo2000 | Property |
| ContentClassifier_EvaluateRule_HasAmount_MatchesWhenPresent | Unit |
| ContentClassifier_EvaluateRule_HasAmount_NoMatchWhenAbsent | Unit |
| ContentClassifier_Classify_EmptyText_ReturnsNone | Unit |
| ContentClassifier_Classify_SingleMatchingRule_ReturnsCategory | Unit |
| ContentClassifier_ParseResponse_GarbageInput_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_MissingConfidence_ReturnsNone | Unit |
| ContentClassifier_ParseResponse_ExtraWhitespace_ParsesCorrectly | Unit |
| Classifier_ReclassifyUnsortedBatch_NoCandidates_ReturnsZeros | Integration |

## SemanticSearchTests.fs (25 tests)

| Test | Category |
|------|----------|
| SemanticSearch_CosineSimilarity_IdenticalVectors_Returns1 | Unit |
| SemanticSearch_CosineSimilarity_OrthogonalVectors_Returns0 | Unit |
| SemanticSearch_CosineSimilarity_OppositeVectors_ReturnsNeg1 | Unit |
| SemanticSearch_CosineSimilarity_ZeroVector_Returns0 | Unit |
| SemanticSearch_ReciprocalRankFusion_CombinesAndDeduplicates | Unit |
| SemanticSearch_ReciprocalRankFusion_DocInBothLists_RanksHigher | Unit |
| SemanticSearch_ReciprocalRankFusion_EmptyInputs_ReturnsEmpty | Unit |
| SemanticSearch_KeywordSearch_FindsMatchingDoc | Integration |
| SemanticSearch_KeywordSearch_NoMatch_ReturnsEmpty | Integration |
| SemanticSearch_KeywordSearch_EmptyQuery_ReturnsEmpty | Integration |
| SemanticSearch_EnrichResult_ReturnsDocDetails | Integration |
| SemanticSearch_HybridSearch_FallsBackToKeyword_WhenNoEmbeddings | Integration |
| SemanticSearch_Search_KeywordMode_ReturnsResults | Integration |
| SemanticSearch_Search_KeywordMode_NoResults_ReturnsEmpty | Integration |
| SemanticSearch_Search_KeywordMode_EmptyQuery_ReturnsEmpty | Integration |
| SemanticSearch_EnrichResult_MissingDoc_ReturnsEmptyFields | Integration |
| SemanticSearch_KeywordSearch_MultipleMatches_ReturnsAll | Integration |
| SemanticSearch_EnrichResult_WithExtractedText_ReturnsSnippet | Integration |
| SemanticSearch_Search_SemanticMode_FailingEmbedder_ReturnsEmpty | Integration |
| SemanticSearch_HybridSearch_KeywordOnlyWhenSemFails | Integration |
| SemanticSearch_SemanticSearch_WithChunks_ReturnsResults | Integration |
| SemanticSearch_SemanticSearch_NoChunks_ReturnsEmpty | Integration |
| SemanticSearch_HybridSearch_WithChunks_CombinesResults | Integration |
| SemanticSearch_Search_HybridMode_ReturnsResults | Integration |
| SemanticSearch_Search_SemanticMode_WithChunks_ReturnsResults | Integration |

## CsvExtractionTests.fs (15 tests)

| Test | Category |
|------|----------|
| CsvExtraction_ParseCsvLine_Comma_SplitsCorrectly | Unit |
| CsvExtraction_ParseCsvLine_QuotedFieldWithComma | Unit |
| CsvExtraction_ParseCsvLine_EmptyFields | Unit |
| CsvExtraction_ParseCsvLine_Semicolon | Unit |
| CsvExtraction_DetectDelimiter_DetectsCorrectly | Unit |
| CsvExtraction_DetectDelimiter_DefaultsToComma | Unit |
| CsvExtraction_ExtractCsv_ProducesContent | Unit |
| CsvExtraction_ExtractCsv_EmptyString_EmptyPages | Unit |
| CsvExtraction_ExtractCsv_HeaderOnly | Unit |
| CsvExtraction_ParseCsvLine_UnterminatedQuote_HandlesGracefully | Unit |
| CsvExtraction_ParseCsvLine_EmptyFields_ReturnsEmptyStrings | Unit |
| CsvExtraction_DetectDelimiter_TabSeparated_ReturnsTab | Unit |
| CsvExtraction_ParseCsvLine_FieldCount_GreaterThanZero | Property |

## ActivityLogTests.fs (15 tests)

| Test | Category |
|------|----------|
| ActivityLog_LogInfo_And_GetRecent_RoundTrips | Integration |
| ActivityLog_LogWarning_IncludesDetails | Integration |
| ActivityLog_LogError_SetsErrorLevel | Integration |
| ActivityLog_GetRecent_RespectsLimit | Integration |
| ActivityLog_GetRecent_EmptyLog_ReturnsEmpty | Integration |
| ActivityLog_GetForDocument_FiltersCorrectly | Integration |
| ActivityLog_GetByCategory_FiltersCorrectly | Integration |
| ActivityLog_LogError_WithDocumentId_LinksToDocument | Integration |
| ActivityLog_GetRecent_MultipleEntries_RespectsOrder | Integration |
| ActivityLog_LogWarning_SetsWarningLevel | Integration |
| ActivityLog_GetByCategory_NoMatches_ReturnsEmpty | Integration |
| ActivityLog_LogError_StoresErrorLevel | Integration |
| ActivityLog_GetForDocument_ReturnsMatchingEntries | Integration |
| ActivityLog_GetByCategory_ReturnsMatchingCategory | Integration |
| ActivityLog_GetRecent_RespectsLimit_LargerSet | Integration |

## DocumentBrowserTests.fs (7 tests)

| Test | Category |
|------|----------|
| DocumentBrowser_ListCategories_EmptyDb_ReturnsEmpty | Integration |
| DocumentBrowser_ListCategories_MultipleCats_ReturnsAll | Integration |
| DocumentBrowser_ListDocuments_FiltersByCategory | Integration |
| DocumentBrowser_ListDocuments_RespectsLimit | Integration |
| DocumentBrowser_GetDocumentDetail_ExistingDoc_ReturnsSome | Integration |
| DocumentBrowser_GetDocumentDetail_Missing_ReturnsNone | Integration |
| DocumentBrowser_GetDocumentDetail_PipelineStatus_Unextracted | Integration |

## DocumentManagementTests.fs (5 tests)

| Test | Category |
|------|----------|
| DocumentManagement_Reclassify_ChangesCategoryInDb | Integration |
| DocumentManagement_Reclassify_NonexistentDoc_ReturnsError | Integration |
| DocumentManagement_Reextract_ClearsExtractedAt | Integration |
| DocumentManagement_GetProcessingQueue_EmptyDb_AllZeros | Integration |
| DocumentManagement_GetProcessingQueue_MixedDocs_CorrectCounts | Integration |

## ReclassificationTests.fs (9 tests)

| Test | Category |
|------|----------|
| Content_Reclassification_ReportsProvenanceChangesOnly | Integration |
| Reclassification_RejectsConcurrentCategoryAndProvenanceChanges | Integration |
| Reclassification_PreservesSourceIdentityAndDoesNotCreateCategoryDirectory | Integration |
| Reclassification_MissingSourceFailsBeforeMetadataMutation | Integration |
| Reclassification_IsIdempotentAndUpdatesProvenanceTagAndFts | Integration |
| Reclassification_ReplacesGeneratedCategoryTagAndPreservesUserTags | Integration |
| Reclassification_PreservesUserTagMatchingGeneratedCategory | Integration |
| Reclassification_RollsBackCategoryTagAndFtsWhenTriggerWriteFails | Integration |
| Reclassification_PreservesEveryV5CompletionAndOutput | Integration |

## ReclassificationApiTests.fs (4 tests)

| Test | Category |
|------|----------|
| Rest_SingleReturnsExplicitIdentityPreservingOutcome | Integration |
| Rest_SingleReportsIdempotentReclassificationAsUnchanged | Integration |
| Rest_SingleMapsWhitespaceCategoryValidationExplicitly | Integration |
| Rest_BatchReportsTruthfulCountsForMixedOutcomes | Integration |

## ReclassificationCompatibilityTests.fs (8 tests)

| Test | Category |
|------|----------|
| Main single signature remains usable and preserves not-found error | Integration |
| MCP retains new_category alongside detailed identity fields | Integration |
| Removed repair tool returns a JSON-RPC error | Integration |
| Legacy move batch retains action and successful count with explicit failures | Integration |
| MCP reclassification failure sets the tool error flag | Integration |
| Single compatibility response reports classification without claiming a move | Integration |
| Main Classifier tuple signature remains callable | Integration |
| Legacy Classifier wrapper exposes partial failure instead of returning success | Integration |

## SavedPathOwnershipTests.fs (13 cases)

| Test | Category |
|------|----------|
| All insert entry points reject canonical path aliases (6 cases) | Integration |
| Full-row persistence cannot take another document path | Integration |
| Existing duplicates remain intact and reject reclassification and deletion | Integration |
| Production insert guards reject non-file or escaping paths (3 cases) | Integration |
| Independent connections cannot concurrently acquire one canonical path | Integration |
| Unregistered SQLite connections fail rather than bypassing ownership guards | Integration |

## SourceFilesTests.fs (7 tests)

| Test | Category |
|------|----------|
| Staged source claims prevent another writer from replacing bytes | Integration |
| Staged source retries are byte-identical and cannot register another hash | Integration |
| Generated writes cannot overwrite a document or staged source | Integration |
| Owned source deletion and movement both fail without mutation | Integration |
| Missing-source redownload requires the exact exclusive identity | Integration |
| Classifier duplicate intake cannot delete its own archived source | Integration |
| Source writes reject a child symlink escaping the archive | Integration |

## ThreadBodyIngestionTests.fs (3 tests)

| Test | Category |
|------|----------|
| Appending a body preserves the previous document and source | Integration |
| Appending a body never invokes document deletion | Integration |
| Appending an independent source does not repair ambiguous historical ownership | Integration |

## EmailChannelProducerTests.fs (4 tests)

| Test | Category |
|------|----------|
| Paged channel ingestion registers body and attachment with immutable source claims | Integration |
| Re-enumerating an acknowledged message leaves registered sources unchanged | Integration |
| Empty enumeration retains the existing watermark | Integration |
| Overlapping channel consumers emit the correct independently owned rows | Integration |

## EmailChannelBodyTests.fs (5 tests)

| Test | Category |
|------|----------|
| Same-day shared-prefix messages retain processed history and distinct sources | Integration |
| An undated distinct body is archived without changing processed history | Integration |
| Malformed historical dates do not prevent an independent body append | Integration |
| Older distinct messages preserve newer processed history | Integration |
| Equal body content does not discard distinct message provenance | Integration |

## EmailChannelOwnershipTests.fs (3 cases)

| Test | Category |
|------|----------|
| Channel attachment collision preserves owned or unindexed existing bytes (2 cases) | Integration |
| Channel sidecar collision preserves its owner and rolls back the failed email claim | Integration |

## EmailChannelFailureTests.fs (2 tests)

| Test | Category |
|------|----------|
| Watermark persistence failure reports an error without losing registered channel sources | Integration |
| Failed body registration reports failure and retains history and watermark | Integration |

## EmailChannelStagedSourceTests.fs (4 cases)

| Test | Category |
|------|----------|
| A standalone staged claim refuses another writer deletion or movement before INSERT (2 cases) | Integration |
| A competing writer and public reader wait for the complete email commit | Integration |
| A public reader observes no partial email after transactional save failure | Integration |

## EmailChannelAtomicTests.fs (2 tests)

| Test | Category |
|------|----------|
| Late attachment failure preserves processed prefix-sharing history and retries exactly once | Integration |
| Other committed emails remain valid and idempotent while a failed email retries | Integration |

## EmailChannelCallbackTests.fs (2 tests)

| Test | Category |
|------|----------|
| Both provider fetches finish before the message write gate is held | Integration |
| Public consumer callbacks read the complete committed email without gate reentrancy | Integration |

## EmailRateLimitTests.fs (2 tests)

| Test | Category |
|------|----------|
| HTTP429 waits sixty seconds outside the write gate before taking the next message | Integration |
| Cancellation interrupts HTTP429 backoff without fetching the next message | Integration |

## LegacyEmailRetryTests.fs (2 tests)

| Test | Category |
|------|----------|
| Legacy late attachment failure rolls back acknowledgement and processing count before retry | Integration |
| Backfill late attachment failure propagates and retains its page for whole-email retry | Integration |

## SavedPathLegacyMetadataTests.fs (2 tests)

| Test | Category |
|------|----------|
| Schema14 disables retirement guards without consuming legacy metadata or history | Integration |
| Legacy retirement metadata does not block a nondestructive document update | Integration |

## EmailProviderSuccessTests.fs (10 cases)

| Test | Category |
|------|----------|
| Non-empty legacy SDK lists preserve complete messages without following continuation (2 cases) | Integration |
| Non-empty SDK pages preserve metadata full IDs and terminal continuation (2 cases) | Integration |
| Full SDK body reads preserve plain and HTML contracts instead of previews (4 cases) | Integration |
| Successful SDK binary attachments map exactly and ingest with the full body (2 cases) | Integration |

## EmailProviderOptionalContractTests.fs (8 cases)

| Test | Category |
|------|----------|
| Valid sparse SDK metadata preserves missing values and documented body fallback (2 cases) | Integration |
| Outlook optional sender shapes preserve available identity without invented fields (4 cases) | Integration |
| Missing Graph collections and body responses are faults rather than empty success | Integration |
| Gmail skips inline content at the threshold but downloads larger inline and normal files | Integration |

## EmailProviderCommitContractTests.fs (4 cases)

| Test | Category |
|------|----------|
| Post-commit notification failure preserves the complete email and remains explicit (2 cases) | Integration |
| Initial watermark read failure prevents SDK work and preserves stored state (2 cases) | Integration |

## EmailProviderBoundaryTests.fs (12 cases)

| Test | Category |
|------|----------|
| Production interpreter transport failures cannot become successful empty results (2 cases) | Integration |
| Production attachment download failure faults instead of returning no attachments (2 cases) | Integration |
| Successful empty provider pages remain successful (2 cases) | Integration |
| Successful empty bodies and attachment collections remain distinguishable from failure (2 cases) | Integration |
| Missing full-message responses are faults rather than fabricated messages (2 cases) | Integration |
| Interpreter attachment failure leaves the email unacknowledged and watermark unchanged (2 cases) | Integration |

## EmailProviderPaginationTests.fs (5 cases)

| Test | Category |
|------|----------|
| Graph page wire format omits absent continuation and serializes present continuation | Unit |
| Production later-page failure drains queued work retains watermark and retries idempotently (2 cases) | Integration |
| Successful empty intermediate pages retain their continuation (2 cases) | Integration |

## EmailProviderRateLimitTests.fs (4 cases)

| Test | Category |
|------|----------|
| Production transport HTTP429 preserves sixty-second cancellable backoff outside the database gate (4 cases) | Integration |

## ThreadsTests.fs (7 tests)

| Test | Category |
|------|----------|
| Threads_ListThreads_EmptyDb_ReturnsEmpty | Integration |
| Threads_ListThreads_GroupsByThreadId | Integration |
| Threads_ListThreads_RespectsLimit | Integration |
| Threads_ListThreadsByAccount_FiltersCorrectly | Integration |
| Threads_GetThreadDetail_ExistingThread_ReturnsSome | Integration |
| Threads_GetThreadDetail_Missing_ReturnsNone | Integration |
| Threads_ListThreads_IncludesParticipants | Integration |

## ExcelExtractionTests.fs (3 tests)

| Test | Category |
|------|----------|
| ExcelExtraction_ExtractExcel_SimpleSheet_ProducesContent | Unit |
| ExcelExtraction_ExtractExcel_MultipleSheets_ProducesMultiplePages | Unit |
| ExcelExtraction_ExtractExcel_EmptySheet_HandlesGracefully | Unit |

## WordExtractionTests.fs (12 tests)

| Test | Category |
|------|----------|
| WordExtraction_ExtractWord_SimpleParagraphs_ProducesContent | Unit |
| WordExtraction_ExtractWord_WithHeading_ProducesHeadingBlock | Unit |
| WordExtraction_ExtractWord_EmptyDocument_HandlesGracefully | Unit |
| WordExtraction_ExtractWord_InvalidBytes_ReturnsEmptyPages | Unit |
| WordExtraction_ExtractWord_MultipleParagraphs_AllExtracted | Unit |
| WordExtraction_ExtractWord_WithMultipleHeadings_ProducesBlocks | Unit |
| WordExtraction_ExtractWord_WithTable_ProducesTable | Unit |
| Extraction_ExtractFromBytes_Docx_ReturnsOk | Unit |
| Extraction_ExtractFromBytes_Excel_WithInvalidBytes_ReturnsError | Unit |
| WordExtraction_ExtractWord_EmptyTable_ProducesEmptyParagraph | Unit |
| WordExtraction_ExtractWord_ValidContent_HighConfidence | Unit |
| WordExtraction_ExtractWord_EmptyContent_LowConfidence | Unit |

## OspreyParityTests.fs (10 tests)

| Test | Category |
|------|----------|
| OspreyParity_O1_MicrosoftPayslip_ExtractsRequiredFields | Integration |
| OspreyParity_O2_QldEducationPayslip_ExtractsRequiredFields | Integration |
| OspreyParity_O3_WestpacCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O4_CbaCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O5_RentalStatement_ExtractsRequiredFields | Integration |
| OspreyParity_O6_FidelityCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O7_AmazonCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O8_UtilityInvoice_ExtractsRequiredFields | Integration |
| OspreyParity_O9_CreditCardCsv_ExtractsRequiredFields | ManualTest |
| OspreyParity_O10_InsuranceRenewal_ExtractsRequiredFields | ManualTest |

## DocumentTests.fs (13 tests)

| Test | Category |
|------|----------|
| Document_decode_string_returns_Some_when_present | Unit |
| Document_decode_returns_None_for_missing_key | Unit |
| Document_decode_returns_None_for_DBNull | Unit |
| Document_decode_returns_None_for_null | Unit |
| Document_decode_int64_from_int64 | Unit |
| Document_encode_adds_key | Unit |
| Document_encode_overwrites_key | Unit |
| Document_hasKey_true_for_present_value | Unit |
| Document_hasKey_false_for_DBNull | Unit |
| Document_hasKey_false_for_missing | Unit |
| Document_id_returns_id_field | Unit |
| Document_stage_returns_default_when_missing | Unit |
| Document_fromRow_is_identity | Unit |

## WorkflowTests.fs (4 tests)

| Test | Category |
|------|----------|
| Workflow_runStage_processes_document_and_forwards | Unit |
| Workflow_runStage_passthrough_when_already_done | Unit |
| Workflow_runStage_passthrough_when_missing_required_keys | Unit |
| Workflow_runStage_marks_failed_on_exception | Unit |

## PromptLoaderTests.fs (26 tests)

| Test | Category |
|------|----------|
| PromptLoader_Parse_ValidContent_ReturnsBothSections | Unit |
| PromptLoader_Parse_MissingSystemDelimiter_ReturnsError | Unit |
| PromptLoader_Parse_MissingUserDelimiter_ReturnsError | Unit |
| PromptLoader_Parse_EmptySystemSection_ReturnsError | Unit |
| PromptLoader_Parse_EmptyUserSection_ReturnsError | Unit |
| PromptLoader_Render_SubstitutesTemplateMarkers | Unit |
| PromptLoader_Render_TruncatesLongText | Unit |
| PromptLoader_LoadFromFile_FileExists_ParsesContent | Unit |
| PromptLoader_LoadFromFile_FileMissing_ReturnsError | Unit |
| PromptLoader_LoadWithFallback_ConfigDirExists_UsesConfigDir | Unit |
| PromptLoader_LoadWithFallback_OnlyAssemblyDir_UsesFallback | Unit |
| PromptLoader_LoadWithFallback_NeitherExists_ReturnsError | Unit |
| ComprehensionSchema_NormaliseCategory_KnownType_MapsCorrectly | Unit |
| ComprehensionSchema_NormaliseCategory_Alias_MapsCorrectly | Unit |
| ComprehensionSchema_NormaliseCategory_Unknown_ReturnsUnclassified | Unit |
| ComprehensionSchema_NormaliseCategory_CaseInsensitive | Unit |
| ComprehensionSchema_NormaliseResponse_ValidJson_ReturnsNormalised | Unit |
| ComprehensionSchema_NormaliseResponse_WithCodeFences_StripsThem | Unit |
| ComprehensionSchema_NormaliseResponse_InvalidJson_ReturnsError | Unit |
| ComprehensionSchema_NormaliseResponse_ConfidenceOutOfRange_Clamped | Unit |

## SenderClassificationTests.fs (19 tests)

| Test | Category |
|------|----------|
| SenderClassification_extractDomain_ValidEmail_ReturnsDomain | Unit |
| SenderClassification_extractDomain_NoAtSign_ReturnsInputLowered | Unit |
| SenderClassification_extractDomain_EmptyString_ReturnsEmpty | Unit |
| SenderClassification_extractDomain_AngleBracketFormat_ExtractsDomain | Unit |
| SenderClassification_classify_KnownBankDomain_ReturnsBank | Unit |
| SenderClassification_classify_KnownPropertyManager_ReturnsPropertyManager | Unit |
| SenderClassification_classify_KnownGovernment_ReturnsGovernment | Unit |
| SenderClassification_classify_KnownUtility_ReturnsUtility | Unit |
| SenderClassification_classify_KnownInsurance_ReturnsInsurance | Unit |
| SenderClassification_classify_KnownEmployer_ReturnsEmployer | Unit |
| SenderClassification_classify_UnknownDomain_FallsBackToDisplayName | Unit |
| SenderClassification_classify_UnknownBoth_ReturnsUnknown | Unit |
| SenderClassification_classify_EmptyInput_ReturnsUnknown | Unit |
| SenderClassification_classify_WhitespaceOnly_ReturnsUnknown | Unit |
| SenderClassification_classify_CaseInsensitiveDomain_Matches | Unit |
| SenderClassification_classify_SubdomainMatch_Matches | Unit |
| SenderClassification_formatHint_Bank_FormatsCorrectly | Unit |
| SenderClassification_formatHint_Unknown_ReturnsEmpty | Unit |
| SenderClassification_formatHint_Utility_IncludesTypeAndLabel | Unit |

## DeepExtractionTests.fs (27 tests)

| Test | Category |
|------|----------|
| DeepExtraction_promptFileForType_Payslip_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_PayrollStatementAlias_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_AgentStatement_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_RentalStatementAlias_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_BankStatement_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_CreditCardAlias_ReturnsSome | Unit |
| DeepExtraction_promptFileForType_Unknown_ReturnsNone | Unit |
| DeepExtraction_computeHash_SameInput_SameOutput | Unit |
| DeepExtraction_computeHash_DifferentInput_DifferentOutput | Unit |
| DeepExtraction_computeHash_Returns16Chars | Unit |
| DeepExtraction_mergeIntoComprehension_AddsDeepExtraction | Unit |
| DeepExtraction_mergeIntoComprehension_InvalidJson_ReturnsError | Unit |
| DeepExtraction_hasValidDeepExtraction_MatchingHash_ReturnsTrue | Unit |
| DeepExtraction_hasValidDeepExtraction_DifferentHash_ReturnsFalse | Unit |
| DeepExtraction_hasValidDeepExtraction_NoDeepExtraction_ReturnsFalse | Unit |
| DeepExtraction_getDocumentType_Present_ReturnsSome | Unit |
| DeepExtraction_getDocumentType_Missing_ReturnsNone | Unit |
| DeepExtraction_getDocumentType_InvalidJson_ReturnsNone | Unit |
| DeepExtraction_extract_ValidPayslip_ReturnsOkWithMetadata | Unit |
| DeepExtraction_extract_UnsupportedType_ReturnsError | Unit |
| DeepExtraction_extract_MissingFromRegistry_ReturnsError | Unit |
| DeepExtraction_extract_ChatFailure_ReturnsError | Unit |
| DeepExtraction_extract_CodeFencedJson_StripsAndSucceeds | Unit |
| McpTools_deepExtract_ValidDocument_ReturnsMergedResult | Integration |
| McpTools_deepExtract_MissingDocument_ReturnsError | Integration |
| McpTools_deepExtract_NoComprehension_ReturnsError | Integration |
| McpTools_deepExtract_MissingDocumentId_ReturnsError | Integration |

## ContactExtractionTests.fs (24 tests)

| Test | Category |
|------|----------|
| ContactExtraction_normaliseName_PtyLtd_Stripped | Unit |
| ContactExtraction_normaliseName_PtyDotLtdDot_Stripped | Unit |
| ContactExtraction_normaliseName_Inc_Stripped | Unit |
| ContactExtraction_normaliseName_Limited_Stripped | Unit |
| ContactExtraction_normaliseName_Quotes_Stripped | Unit |
| ContactExtraction_normaliseName_PlainName_Lowered | Unit |
| ContactExtraction_normaliseName_WhitespacePreservedInMiddle | Unit |
| ContactExtraction_computeContactId_SameInput_SameOutput | Unit |
| ContactExtraction_computeContactId_DifferentAbn_DifferentId | Unit |
| ContactExtraction_computeContactId_WithAbn_IncludesAbn | Unit |
| ContactExtraction_computeContactId_Returns16Chars | Unit |
| ContactExtraction_contactTypeFromSender_Bank_Supplier | Unit |
| ContactExtraction_contactTypeFromSender_Employer_Employer | Unit |
| ContactExtraction_contactTypeFromSender_Government_Government | Unit |
| ContactExtraction_contactTypeFromSender_Unknown_Unknown | Unit |
| ContactExtraction_harvestFromComprehension_TopLevelSenderName_ExtractsContact | Unit |
| ContactExtraction_harvestFromComprehension_NestedFields_ExtractsContact | Unit |
| ContactExtraction_harvestFromComprehension_NoName_ReturnsNone | Unit |
| ContactExtraction_harvestFromComprehension_InvalidJson_ReturnsNone | Unit |
| ContactExtraction_harvestFromComprehension_AngleBracketSender_ExtractsEmail | Unit |
| ContactExtraction_harvestFromComprehension_AbnFromTopLevel_Extracted | Unit |
| ContactExtraction_harvestAndLink_InsertsContactAndLinks | Integration |
| ContactExtraction_harvestAndLink_DuplicateContact_UpdatesLastSeen | Integration |
| ContactExtraction_harvestAndLink_NoName_NoContact | Integration |

## McpContactTests.fs (8 tests)

| Test | Category |
|------|----------|
| McpServer_ContactsBackfill_CreatesContacts | Integration |
| McpServer_ContactsList_ReturnsContacts | Integration |
| McpServer_ContactsList_FilterByQuery | Integration |
| McpServer_ContactsList_FilterByContactType | Integration |
| McpServer_ContactDetail_ReturnsWithDocuments | Integration |
| McpServer_ContactDetail_NotFound_ReturnsError | Integration |
| McpServer_ContactSetTaxRelevant_Updates | Integration |
| McpServer_ContactSetTaxRelevant_NotFound_ReturnsError | Integration |

## PptxExtractionTests.fs (10 tests)

| Test | Category |
|------|----------|
| PptxExtraction_ExtractPptx_InvalidBytes_ReturnsEmptyWithZeroConfidence | Unit |
| PptxExtraction_ExtractPptx_EmptyBytes_ReturnsEmptyWithZeroConfidence | Unit |
| Extraction_IsPptx_ReturnsTrue_ForPptxExtension | Unit |
| Extraction_IsPptx_ReturnsFalse_ForOtherExtensions | Unit |
| PptxExtraction_ExtractPptx_SimpleSlide_ExtractsText | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_CorrectPageCount | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_ExtractsSlideText | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_ExtractsTable | Integration |
| PptxExtraction_ExtractPptx_MultiSlide_ExtractsSpeakerNotes | Integration |
| PptxExtraction_ExtractPptx_SimpleSlide_HighConfidence | Integration |

## OutlookProviderTests.fs (12 tests)

| Test | Category |
|------|----------|
| Classifier_ParseSidecar_ProviderIdField_ParsesProviderId | Unit |
| Classifier_ParseSidecar_LegacyGmailId_FallsBackToProviderId | Unit |
| Classifier_ParseSidecar_BothProviderIdAndGmailId_ProviderIdWins | Unit |
| Config_ParseYaml_OutlookAccount_ParsesClientAndTenant | Unit |
| Config_ParseYaml_OutlookAccount_DefaultTenantId | Unit |
| Config_ParseYaml_OutlookAccount_DefaultRedirectPort | Unit |
| Extraction_IsPptx_DetectsCorrectly | Unit |

## ComprehensionRacTests.fs (12 tests)

| Test | Category |
|------|----------|
| Stages_ExtractSenderDomain_ValidEmail_ReturnsDomain | Unit |
| Stages_ExtractSenderDomain_NoAt_ReturnsNone | Unit |
| Stages_ExtractSenderDomain_AngleBrackets_StripsThem | Unit |
| Stages_CompactSchemaHint_ValidJson_ReturnsTypeAndFieldNames | Unit |
| Stages_CompactSchemaHint_NoFields_ReturnsTypeOnly | Unit |
| Stages_CompactSchemaHint_InvalidJson_ReturnsNone | Unit |
| Stages_CompactSchemaHint_EmptyFields_ReturnsEmptyArray | Unit |
| Stages_CompactSchemaHint_CapsAt300Chars | Unit |

## ArchiveWriterTests.fs (32 cases)

| Test | Category |
|------|----------|
| ArchiveWriter_Slugify_NormalText_ReturnsSlug | Unit |
| ArchiveWriter_Slugify_EmptyOrWhitespace_ReturnsUntitled | Unit |
| ArchiveWriter_Slugify_LongText_TruncatesAt60Chars | Unit |
| ArchiveWriter_ExtractSenderDomain_ValidEmail_ReturnsDomain | Unit |
| ArchiveWriter_ExtractSenderDomain_NoEmail_ReturnsUnknown | Unit |
| ArchiveWriter_ThreadFolderPath_BuildsCorrectPath | Unit |
| ArchiveWriter_ThreadFolderPath_DifferentThreadIds_DifferentPaths | Unit |
| ArchiveWriter_LocalFolderPath_BuildsCorrectPath | Unit |
| ArchiveWriter_MessageFileName_IncludesDateAndSlug | Unit |
| ArchiveWriter_AttachmentFileName_PreservesExtension | Unit |
| ArchiveWriter_AttachmentFileName_HandlesDotInName | Unit |
| ArchiveWriter_AttachmentFileName_DifferentHashes_DifferentNames | Unit |
| ArchiveWriter_WriteMessage_CreatesFile | Unit |
| ArchiveWriter_WriteAttachment_CreatesFile | Unit |
| ArchiveWriter_WriteExtraction_CreatesSidecarFile | Unit |
| ArchiveWriter_WriteComprehension_CreatesThreadJson | Unit |
| ArchiveWriter_WriteSidecar_CreatesHermesJson | Unit |
| ArchiveWriter_ReadExtraction_ReturnsNone_WhenNotExists | Unit |
| ArchiveWriter_ReadComprehension_ReturnsNone_WhenNotExists | Unit |
| ArchiveWriter_ReadExtraction_ReturnsContent_WhenExists | Unit |
| Message filenames distinguish full raw provider identities (3 cases) | Unit |
| Undated source identity is stable across retry dates | Unit |
