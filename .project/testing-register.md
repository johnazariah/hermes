# Hermes — Testing Register

> **Rule**: Update this file whenever tests are added, removed, or modified.

## Import format error points to the actual key header

Run `39519627685` at `7786a0f` reports 2216 with line number 3 for every
failing scratch import. Source comparison of the intended table/key header
does not establish a justified format change.

The reviewed test-only continuation captures the actual written third line
as exact hex and its canonical schema table name, after reading error fields
1/4. File access is read-only and bounded to 4096 total header bytes, stopping
at the third LF without reading rows. Unknown names, failed reads and optional
output failures remain bounded unknowns; original COM failures are preserved.

Seventeen byte-exact portable controls verify terminators, composite keys,
raw bytes, bounds, actual-file readback and no row reads. Parent passes the
warning-free root/cross-build, 2,182 Core, 796 Setup and all static controls.
No format, production, assertion or 57-case selection change is made.

## Native Import reports format error 2216; capture only its line number

Run `39517899783` at `b86d1ee` makes the associated xUnit channel visible:
all twelve failing fixture imports report exactly native MSI error 2216.
Microsoft defines this as an import-file format error with the failing line
number in record field 4; it does not prove duplicate keys or a file-open
failure.

The source-approved one-file continuation reads integer field 4 only after
confirming 2216, from the same record before release. Only decimal ID/line or
fixed unknowns are emitted; fields 2/3 and physical line contents are not read.
Original Import failures and all 57 cases remain unchanged.

Parent passes warning-free root/Windows cross-builds, 2,182 Core and static
controls. One actual Windows line number remains necessary before choosing
the fixture correction.

## Native import identifier uses xUnit-associated failure output

Run `39516442512` at `3648771` repeats the same common Import failure but
contains no scalar or unknown diagnostic marker in the job log. Capture
visibility through Console.Error is therefore unproved.

The reviewed one-file continuation injects per-test `ITestOutputHelper` and
passes only its bounded string writer into all three fixture-creation paths.
The callback is assigned before construction-time Import. Native field
capture, release, fixed unknowns, original exception and all 57 cases remain
unchanged.

Parent passes warning-free root and Windows cross-builds, all 2,182 Core
cases, metadata-only native discovery and static controls. Independent review
approves source wiring only. The next Windows failure output must supply the
numeric identifier before a fixture correction is selected.

## Native scratch import needs its numeric MSI error identifier

Run `39514317262` at `7309a39` executes 57 native cases: the prior 45 pass and
all 12 new Signature cases fail during common scratch-database Import, before
reader/validator assertions. The Automation error `Import,Folder,File` does
not identify a justified formatting or schema correction.

The reviewed one-file test-only seam now reads the same installer's
LastErrorRecord field 1 immediately after that failure and prints only its
decimal identifier or a fixed unknown marker. Record cleanup or optional
output failure cannot replace the original COM exception. Production
admission, test assertions, selection and all 57 cases remain unchanged.

Parent passes warning-free root build and 2,182 Core cases; the applier passes
779 portable Setup, Windows cross-build and all static/payload 22/
materialisation 7/actionlint checks. Independent review approves only this
capture seam. Actual Windows must supply the identifier before a fixture fix.

## Exact empty Signature metadata is admitted; temporary probe is removed

Final independent review approves the seven-file correction. Optional absence
and observed-empty `Signature` with the exact pinned-WiX6 nine-column schema
are supported. `FileName` is nonlocalized `s255` / 3583; localized, nullable,
wrong-width and other schema changes fail. Every Signature row remains
rejected, including one matching the known registry-search identifier.

Parent passes the warning-free root build, 2,182 Core and 779 unfiltered
portable Setup cases. The applier passes 318 Servicing, 48 Control and Windows
cross-build; all static/payload 22/materialisation 7/actionlint controls pass.
The 47 added portable cases cover real reader/validator/catalog and retention
paths. Twelve native scratch-database cases are wired to the existing complete
Windows project, without filters, skips or OS-return branches; not run locally.

The rejected localization assumption was corrected by a fresh author against
primary v6 definitions. The temporary four-file probe and pre-install stop are
fully removed; normal lifecycle/gates/retention are restored. Actual evidence
remains in session artefacts. Fresh Windows full media/install acceptance is
still required.

## The same validated candidate has an empty Signature table

The single-fetch run `39509813811` at `ad46db9` returns matching immutable
source/package bindings, successful read-only query and fetch, no native
failure, and `isEmpty=true`. No row contents, owner fixture or installation
are involved.

Together with the earlier `unmatchedNames=["Signature"]`, this proves that
the closed inventory rejects a genuine present-but-empty Signature table.
A fresh C# author owns the smallest exact-schema/zero-row correction;
arbitrary file-search signatures are not authorised. The temporary probe is
now being removed before normal lifecycle acceptance resumes.

## One native fetch will establish Signature emptiness

The prior receipt/source-bound inventory query succeeds with 24 tables and
only `Signature` unmatched. It proves a sufficient closed-inventory rejection,
not whether that table contains file-search recipes.

The reviewed three-file continuation changes only the observation to one
read-only `Signature` query and exactly one COM fetch. Clean null means empty;
a record means nonempty and is released without reading content. Query,
fetch or close failures retain the real error and leave the bit unknown.
Source/package bindings, held leases and the terminal pre-fixture stop remain.

Independent review executes the empty/nonempty and failure/privacy controls
with no blockers. Parent passes the warning-free root build, 2,182 Core cases
and complete static controls. No C# allowance is added before the actual bit.
The temporary probe still must be removed after that result.

## One source-bound compiled table predicate is observed read-only

The temporary four-file probe is independently reviewed with no blocking
finding. It binds the published 0.1.0 candidate to its native-validation
receipt and pins the exact 30-key C# source union at `5b06889`, then opens the
native database read-only for one `_Tables.Name` query.

Only source/package binding, native query result, observed count, ordinally
sorted unmatched names and the selected count/membership predicate are
retained. The workflow deliberately stops before owner-fixture creation or
installation and always uploads the bounded JSON; it cannot pass lifecycle
acceptance or distribution.

The reviewer independently verifies source blobs/union, query/access mode,
handle leases, error privacy, stop/upload wiring, actionlint and synthetic
controls. Parent passes the warning-free root build, 2,182 Core cases and all
static controls. A passing predicate eliminates only this selected check,
not upstream duplicate-row validation or complete media admission. Actual
Windows observation remains pending; this temporary stop must be removed.

## Native publication is unit-linked; candidate now reports MediaInvalid

Run `39502695162` at `5b06889` passes all 45 native Windows cases and both
complete MSI validations. For each version, the actual bounded publication
record reports one link for both validated intermediate and published package;
receipt identity/hash checks succeed.

Install advances from the previous non-unit-link refusal to `MediaInvalid`,
still with 1603, Refused, ready=false and null MSI result. Recover remains
NoRecoveryPending with no cleanup failures. No successful installation is
claimed.

A fresh C# specialist owns the now-reachable compiled-candidate media contract
trace. The proved materialisation correction and existing admission predicate
remain closed; media/authoring checks will not be weakened merely to accept
the first native package.

## WiX output materialisation preserves unit-link candidate media

The C# caller trace identifies the first guarded input as the candidate source
MSI, before pinning/hash/database reads. Independent review and execution
confirm the actual pinned WiX6 copy target defaults to hard links. Its
synthetic default aliases the intermediate/output; rewriting the intermediate
can change a previously published baseline.

The approved five-file publication correction disables hard/symbolic copies,
checks effective overrides, isolates fresh output/intermediate directories
and verifies published identity/hash against the native ICE receipt.
Seven real-copy/materialisation controls pass, including unit-link distinct
outputs, unchanged hashes, baseline/upgrade independence and adverse aliases,
receipts and occupied outputs. The reviewer independently executes them and
the pinned SDK Copy task; parent also passes all static controls and the
warning-free root build plus 2,182 Core cases.

The admission predicate and all C# code remain unchanged. The historical
Windows link count was not directly observed and is not asserted to be two.
Fresh exact-head Windows installation remains required.

## Native source-point record identifies the non-unit-link invariant

The single probe `39499387931` at `19f91ad` passes all **45/45 native Windows
cases** with zero skips and both complete MSI validations. Install emits
`stage=1 cause=12 fault=0 errorKind=0 error=0`: the existing
`WindowsPhysicalPaths.Image` link-count-not-one rejection.

This proves the invariant, not the filename, input role or whether the count
is zero versus greater than one. Recover remains NoRecoveryPending with clean
cleanup. No behavioural relaxation or installed success is authorised.

Fresh, separate scopes now trace the C# input role and SDK/publication
materialisation. A correction must preserve the unit-link admission rule,
hashes, package identities, full ICE and baseline/upgrade independence; it
cannot merely accept aliased inputs.

## Finite admission source-point evidence is source-approved

The new internal discriminator tags only nine existing bare managed rejection
points. Independent review approves the bounded seam: exception type, reason,
HResult, check order, catch boundaries and gate/publication guards remain.
The existing v1 channel maps causes 10–18 explicitly, with Entry stage,
fault=None and native kind/code zero. Genuine native evidence takes precedence.

Legacy parser cases 1–9 remain unchanged. Nine new legal records and 367
adverse combinations verify the finite vocabulary and cross-field rules;
existing 2,599 numerical and 57 adverse controls still pass.

Parent passes the warning-free root build, 2,182 Core, 732 Setup and 318
Servicing cases plus all static controls. The applier also passes 48 Control
and Windows cross-build. No Windows execution or behavioural root fix is
claimed; one exact-head target record is required to identify the invariant.

## Unavailable reaches a bare managed admission rejection

Run `39494190534` at `bb9c02f` passes both supported OS jobs, Tray, all
41 native Windows cases and both complete MSI validations. Install retains
`Unavailable` with v1 `stage=1 cause=2 fault=0 errorKind=2 error=-2146233088`;
Recover still reports NoRecoveryPending and cleanup remains clean.

For the inspected production route, `0x80131500` selects the outer
`AdmissionException` fallback without native evidence, not an ordinary OS I/O
failure. It does not distinguish the existing bare InvalidState constructors:
canonical-root, image kind/link count, ancestor limit, directory kind,
final-path prefix and publication-state checks.

The prior author stops at this observability boundary without a behavioural
patch. A fresh narrowly scoped task will add only a finite typed source-point
cause at those existing rejections and matching v1 parser/tests. This small
vocabulary extension is necessary because existing causes 1–9 cannot describe
the invariant truthfully; source IDs must not masquerade as OS errors.
Exception type/reason, catch boundaries and all checks remain unchanged.

## Unavailable uses the existing v1 refusal evidence channel

The narrow seven-file continuation is independently source-approved. It emits
retained `Unavailable` metadata, annotates existing admission/outer-I/O return
sites and forwards retained native error kind/code at the existing outer
boundary. Sharing violations 32/33 remain Busy. Catch boundaries, report,
exit/readiness policy, optional-writer handling and v1 parser are unchanged.

No v2 schema, native-field suite, friend-assembly expansion or new tracing
framework is applied. Parent passes the warning-free solution build,
2,182 Core and 712 Setup cases plus all static controls. The applier also
passes 48 Control, 297 Servicing and the Windows cross-build; zero skips.

The next hosted record may locate a concrete handling boundary without
uniquely identifying the originating operation. It remains diagnostic
evidence only, not a behavioural fix or installed acceptance.

## Native shortcut controls pass; install advances to Unavailable

Run `39489798798` at `aa4f3df` passes the complete **41/41 Windows project**
with zero failures/skips, including real COM field reads, managed missing-path
mapping, missing ancestors, redirected/dangling paths, collisions and held or
malformed files. Both MSI versions again pass complete native ICE and actual
mutation controls.

The first SDK-free Install now returns `Unavailable` rather than the previous
shortcut `InvalidEvidence` record. Failure-Recover reports `NoRecoveryPending`;
the bounded report contains no cleanup failures. Both still return refusal,
`ready=false` and null MSI result, so no installation or absent-MSI-call claim
is made.

A fresh C# specialist owns the now-distinct `Unavailable` trace. The proved
shortcut behaviour and prior trust/ICE/admission contracts are not reopened.

## Shortcut absence uses physical proof before COM loading

Independent review approves the narrow five-file correction. The real
shortcut adapter now requires physical identity, missing-entry metadata,
another physical proof and another missing-entry observation before returning
absence. Unknown/redirected identity, reparse metadata, collisions and metadata
errors remain refusals. A present file's COM or managed loading failure stays
`Unreadable` with its original numerical HRESULT.

Parent passes the warning-free solution build, **2,182 Core** and **693 Setup**
cases plus all static controls. The applier also passes **26 focused**,
**48 Control** and **297 Servicing** cases and the Windows-project cross-build,
with zero failures/skips or build warnings/errors.

New native cases use the existing isolated sandbox and complete Windows
project step. They have not executed locally; in particular the exact managed
missing-path exception assertion must not be weakened after a native failure.
Actual first-install/recovery success remains unproved.

## Actual refusal evidence identifies initial shortcut observation

Run `39486421130` at `82cbb13` again validates both real MSI versions and passes
native Windows cases/mutation controls. The published numeric pipeline now
preserves the same structured record for Install and failure-Recover:
`stage=8`, `cause=4`, `fault=5`, `errorKind=2`, `error=2147942403`.

The source maps this to initial inventory / shortcut observation with HRESULT
`0x80070003` (`ERROR_PATH_NOT_FOUND`). Original exit 1603, `Refused`,
`ready=false`, null MSI result and separate cleanup refusal remain visible;
there is no report or diagnostic protocol error.

A fresh C# specialist owns the exact observer/ancestor trace and safe
classification correction. Genuine absence must be proved without treating
inaccessible, redirected, reparse or dangling paths as absent. The evidence
does not yet identify which path/API produced the error or establish a
successful installation.

## Bounded first-install refusal evidence is source-approved

The actual first-install `InvalidEvidence` report does not identify a unique
origin. Diagnostic-only C# changes retain fixed stage/cause/observation-fault
and numerical native-error evidence through entry, inspection and reporting.
At most one bounded numerical stderr record reaches the wrapper and its
corresponding acceptance stage; no raw messages, paths, identities or tokens
are retained. Install, failure-Recover and cleanup outcomes stay separate.

Independent review found that an optional evidence writer failure could mask
the original refusal. A fresh C# author closes that path: only the additional
write handles I/O/access failures, preserving the existing report, marker and
1603 exit. Mandatory writes, cancellation and programming-fault propagation
remain unchanged. Final review approves the integrated diagnostic-only source.

Parent directly passes the warning-free solution build, **2,182 Core** and
**667 Setup** cases plus the complete static gate. The applier also passes
**53 focused**, **48 Control** and **297 Servicing** cases. The real parser
controls exercise 2,599 numerical combinations/boundaries, 57 adverse inputs,
redirected synthetic child stderr and separate coordinator-stage persistence.
All reported suites have zero failures/skips.

Fresh hosted evidence must still identify the actual refusal origin. Neither
this source approval nor a null MSI result proves that no MSI call occurred,
establishes a behavioural fix, or demonstrates successful installation.

## Both native MSI builds validate; first coordinator install refuses evidence

Run `39479960150` at `4532cd7` passes all 31 native Windows cases and real
copied-MSI mutation refusals. Both baseline 0.1.0 and upgrade 0.1.1 build
successfully with complete authenticated native ICE: each produces a verified
receipt for 699 precisely matched, prerequisite-checked warnings. Neither ICE
selection nor suppression is used.

The synthetic owner fixture is then created successfully. The first SDK-free
Setup Install returns 1603 with `Refused`, `ready=false`, `msiExitCode=null`
and `InvalidEvidence`; no protocol error is reported. Failure recovery returns
the same refusal and cannot establish a completed safe state. No successful
installation or installed-native proof is claimed.

The bounded 697-byte acceptance artefact records the original install failure
and cleanup refusal. A fresh C# specialist owns the exact-origin trace or
bounded diagnostic correction. The preflight contract is not relaxed.

## Native ICE output reaches a blank-line adapter correction

Run `39477575313` at `8c17a15` passes both supported OS jobs, Tray and all
31 native Windows cases. Actual isolated MSI filename/reparenting mutations
are rejected by the real read-only gate. The authenticated validator executes
full ICE and emits exactly one ICE61, one ICE63 and 697 ICE91 warnings; the
original ICE64/72 errors are absent. Classification then fails at PowerShell
binding because `Lines` contains empty string elements.

The reviewed adapter admits those empty elements while retaining mandatory
diagnostic completeness. Shared capture reads stdout/stderr independently,
preserving unterminated stream boundaries. Six valid layouts and ten rejected
outputs/exits run through real synthetic child processes; unrelated binding
errors cannot satisfy the negative controls. Empty output, missing/duplicate/
foreign diagnostics, truncation and nonzero exits remain failures.

Narrow independent review approves the four-file correction. Parent passes
the warning-free solution build, all 2,182 Core cases and complete static
22-payload/28-adverse/provenance/path/policy/actionlint controls. C# and all
previous trust/hash/ICE requirements are unchanged. Fresh Windows classifier
and installed-lifecycle success remain outstanding.

## Integrated MSI authoring and validation source is approved

Final independent review closes all three source findings: pinned-validator
provenance, actual MSI file/path binding, and consistent target-name handling.
One shared C# reader now interprets bare or valid `short|long` names across
media inspection, authoring validation and catalog/resource resolution.
Exact long-target identity and all surrounding ownership/root checks remain.

Alias integration tests exercise candidate, retention, registered-cache and
retained-source discovery, including resolved resources and adverse names.
The ordinary compression fixture uses `auxiliary.dat`; explicit reserved-AUX
cases still fail even when the manifest agrees. No production rule is relaxed.

Parent directly verifies the warning-free full solution build, **2,182 Core**
and **614 Setup** passes, zero failures/skips, plus all static/payload 22,
adverse 28, validator provenance/path/policy controls and workflow lint.
The applier additionally verifies **114 focused** and **48 Control** passes,
warning-free relevant builds and exact patch application.

This is integrated source/managed proof only. Actual hosted renamed/reparented
MSI controls, complete native ICE, SQL-null/table readback, advertisement
refusal and the full installed lifecycle remain required on the exact
published candidate before merge or distribution.

## Revised validator trust and MSI path checks close their source findings

Independent cycle-2 review closes both Stage1 findings: the source-fixed
archive digest authenticates the complete SDK validator tree, with read
leases retained through execution, and actual MSI file/component/directory
facts are checked against the manifest. Synthetic controls reject substituted
tools/dependencies/archive content and altered filenames, bindings and paths.
The hosted-only script genuinely mutates isolated MSI copies; those Windows
controls have not executed yet.

Integrated review identifies one remaining consumer disagreement: Stage1 and
the media reader recognise valid `short|long` MSI target names, while C#
authoring/resource code still compares or propagates raw fields. A fresh C#
specialist owns that target-name/resource-resolution correction; the previous
author is locked out of this rejected scope. Unchanged Type19, SQL-null and
ancestor-ownership contracts remain source-approved.

Current local checks pass static/payload 22/adverse 28/policy/provenance/path
controls, the warning-free full solution build, all 2,182 Core cases,
537 Setup cases and 48 Control cases. No integrated-source approval or
native install success is claimed until the alias contract is resolved.

## Independent authoring contract covers advertisement and all ancestors

The C# specialist's nine-file patch is applied mechanically and independently
source-approved. It admits exactly the two unchanged native guards plus the
unconditional built-in Type19 advertisement refusal at 1399. A separate
read-only `CustomAction.Source IS NULL` query must observe only that refusal;
normalised empty text is not sufficient authority.

Removal validation now covers every manifest-derived ancestor with its exact
canonical identity and first descendant owner in preserved manifest order.
Empty-directory-only, uninstall-only mode 2 and all special owners remain
strict. Adverse tests cover missing/extra/duplicate rows, altered owners,
wildcards, modes, sequences, SQL-null observations and refusal semantics.

Both Setup Release builds pass without warnings/errors; **537 Setup tests**
and **48 Control tests** pass with zero failures/skips. The two new C# test
files receive the required BOM without changing their source payload.
These are managed/source results, not native SQL-null, advertisement or
uninstall proof.

The separate Stage1 validation policy is **rejected**: validator execution
lacks independent pinned-SDK provenance, and actual MSI facts omit filename
and File/Component/Directory path binding. A fresh author owns that revision;
the rejected author cannot revise or advise it. No unapproved policy is
published and complete Windows acceptance remains outstanding.

## Committed identity passes; full native ICE finds authoring gaps

Exact-head run `39467659071` at `cb020df` passes both supported jobs, all
61 browser journeys per OS, Tray and all 31 native Windows servicing cases.
Windows coverage is 14,726/17,126 lines (85.9862%) and 15,193/24,668 branches
(61.5899%); macOS is 14,679/17,126 (85.7118%) and 15,109/24,668 (61.2494%).

The first MSI now passes setter-retention and fresh read-only persisted
PackageCode checks. Complete normal ICE executes and identifies real authoring
errors: ICE64 for two user-profile ancestor directories absent from RemoveFile,
and ICE72 for a DLL custom action in the advertised sequence. Elevated ICE61,
ICE63 and ICE91 warnings also describe the all-version removal range,
transaction-fence placement and fixed per-user payload respectively.

Fresh non-C# and C# specialists own the authoring/independent-validation
corrections. No ICE is suppressed or restricted, no unknown warning is accepted
and no actual installation has succeeded. Full installed/native recovery
acceptance remains required before merge or distribution.

## Self-contained Windows acceptance runs independently

Run `39464582678` passes Windows but never executes a macOS supported step:
GitHub cancels allocation with “The job was not acquired by Runner of type
hosted even after multiple attempts”. The installer is consequently skipped;
this is an infrastructure failure, not an MSI or macOS test result. One
unchanged failed-job retry was requested.

The installer job builds every input in its own disposable Windows workspace
and downloads no other job's artefacts. Its scheduling-only prerequisites are
removed so unrelated runner allocation cannot prevent native diagnostics.
Publication still requires `build-and-test`, `build-tray` and `installer`.
Static controls enforce both independent construction and the complete
publication dependency set. All 22 payload and 28 adverse controls and
workflow lint pass; the solution builds warning-free and all 2,182 F# cases pass.

## PackageCode verification reads the committed MSI

Run `39460550649` identifies the null-GUID failure precisely: phase
`summary-package-readback`, line 80, where the original writable summary
object returns null after persist/commit. Product/scope, all 697 file/component
checks and summary platform/WordCount have already passed. This is not a
component-GUID failure.

The finaliser now requires the setter to retain the selected non-null GUID
before persistence, closes the writable summary/database after commit, then
opens fresh read-only handles to verify the committed PackageCode. Missing,
empty, malformed or different identities still fail. Complete ICE still runs
after finalisation, and no later mutation is introduced.

Controls execute both exact GUID guards with matching and four adverse values,
require persist/commit/release/read-only ordering and retain original failure
context. All 22 payload and 28 adverse report controls pass. The solution builds
warning-free and all 2,182 F# cases pass. Windows must establish whether the
setter retains the value and whether the committed file contains it; no native
success is claimed from these controls.

## Finaliser context is initialised in script scope

Review of the diagnostic-only delta finds its initial phase assignment in
the row helper's local scope. It now precedes the outer MSI try block so an
early database/property failure cannot replace its original exception with
an uninitialised-variable error. A package-layout control enforces that scope
and ordering. The exact-catch exception-identity control still passes; the
solution builds warning-free and all 2,182 F# cases pass. The superseded
diagnostic run is cancelled; no native result is claimed for this correction.

## Explicit finalisation phase and stack evidence

Run `39456791213` passes both complete supported platform jobs, including all
61 browser journeys, and all 31 Windows servicing cases. The finaliser still
reports the null-GUID conversion; normal error view does not expose its source
line on the actual Windows automation path.

An explicit failure-context catch now records the fixed finalisation phase,
invocation line and at most four stack frames of 512 characters each, then
rethrows. The exact catch body is exercised by the package-layout controls:
the original exception object must survive and its synthetic phase must be
reported. No identity comparison, mutation order or acceptance rule changes.
All 22 payload and 28 adverse report controls pass; the solution builds
warning-free and all 2,182 F# cases pass. The native failing phase remains
unknown until Windows executes this candidate.

## Native-message ordering and real sync-response capture

Run `39452815857` first fails a Windows native-navigation unit assertion:
two event-loop turns did not finish asynchronous Web Crypto before checking
the replacement navigation. The fixture now awaits actual native-message
notifications for pairing, authentication and replacement pairing, with a
five-second test deadline. The early-ready/stale-exchange case uses the same
causal completion signal. Production access and wire assertions are unchanged.

Both attempts of that run also fail macOS's palette sync case when Chromium
cannot return its response body through `Network.getResponseBody`. The test
now buffers the real Service response with the existing route-fetch pattern
before delivering it unchanged to the browser. Redirect following and retries
are disabled. Status/body, one UI POST, exactly one completed producer cycle,
stored document/content and visible feedback assertions all remain.

Local validation passes 102 JavaScript units, lint, build, discovery of all
61 browser cases, both focused real-Service sync cases and the complete
61-case browser suite. The canonical bundle hash remains `1ac59912876db25479f308c61d76c64f40f9af766bbea20808a99b0b4ca97c34`.
The solution builds warning-free and all 2,182 F# cases pass. The diagnostic
run never reaches MSI finalisation; its null-GUID location remains unknown.

## Finaliser conversion failures retain their source location

Hosted run `39448288911` passes both supported platform jobs, Tray and all
31 native Windows servicing cases, then fails during MSI finalisation with
`Cannot convert null to type "System.Guid"`. The default concise error view
does not identify the failing cast. Its location is not yet established.

The finaliser now selects PowerShell's normal error view; all identity,
inventory, summary, ICE and lifecycle requirements are unchanged. A controlled
null-GUID child script proves the failing source line is retained and the
process still fails. The package-layout control requires this diagnostic
setting and passes. The solution builds without warnings/errors and all
2,182 F# cases pass. Actual installed acceptance remains outstanding.

## MSI query helpers emit only their records

Hosted run `39443249566` narrows the finalisation failure to
`ProductCode: rows=3`, before value comparison. The row helper has two
unsuppressed bookkeeping calls surrounding its single record output.
`Execute` and `Close` results are now explicitly discarded; the related
identity reader also discards `Execute` output. Query exceptions still
propagate, and uniqueness/value/component/summary checks remain unchanged.

Synthetic pipeline controls reproduce three outputs with noisy bookkeeping,
then prove one unchanged row when those results are discarded. A throwing
query remains a failure. Static controls enforce the same casts in the actual
helper. All 22 payload and 28 adverse controls pass; the normal solution builds
warning-free and passes all 2,182 F# cases. Windows must still confirm the real
COM helper's row cardinality and complete finalisation/ICE/lifecycle sequence.

## Finalisation comparison evidence

Hosted run `39437427176` exposes the previously hidden failure: the built MSI's
product identity check disagrees with the manifest at the three-property
comparison. The original error still does not identify which property, row
count or returned type differs.

The unchanged comparison now reports its selected build-identity property,
expected/actual value, row count and type. Only ProductCode, UpgradeCode and
ProductVersion are queried here; no owner/archive data is involved. A controlled
probe extracted from the exact comparison confirms equality still passes and
mismatch still fails with the additional evidence. Static validation passes
22 payload and 28 adverse controls; the solution builds warning-free and all
2,182 F# cases pass. No identity mismatch is accepted or guessed away; the next
Windows result is needed to establish the correction.

## Complete native suite passes; identity finalisation needs its original error

Hosted run `39431966923` passes the complete **31/31 Windows servicing suite**
with zero skips. Both supported Core/browser/coverage jobs, Tray and payload
construction remain green. The first lifecycle build now reaches
`Finalize-PackageIdentity.ps1` before normal ICE validation, but its child
command exits 1 and the quiet wrapper exposes only MSB3073.

A controlled, non-MSI MSBuild/PowerShell failure reproduces the logging defect:
quiet output omits the original exception, while minimal verbosity with high
child-stream importance preserves it and still returns failure. The package
wrapper now uses that visible setting. Static controls require it and retain
the existing finalise-before-validation ordering, complete ICE requirements and
all identity checks. No finalisation or installation rule is changed.

All 22 payload and 28 adverse report controls pass; the solution builds
warning-free and passes all 2,182 F# cases. The actual finalisation cause is
still unknown until the next Windows build exposes it. No MSI install, native
WebView or interrupted-recovery acceptance is claimed.

## Actual Windows kernel spelling expectations

Hosted run `39427186591` completes the corrected payload-construction step and
executes all **31 Windows servicing cases**: 30 pass and one fixture fails
before its inventory-rejection assertion. The intermediate directory spelling
`service ` returns exact Win32 error 3 rather than opening the canonical image.

The fixture now distinguishes the three positive native aliases from this
native-missing spelling. Every theory row still publishes the same contradictory
inventory and must receive `InvalidState` without changing bytes. Native aliases
still require successful open/content proof; the missing case requires an
invalid handle and exact `ERROR_PATH_NOT_FOUND`, not an arbitrary I/O failure.
Production path validation is unchanged, and the project retains 31 cases.

The project cross-builds warning-free; all 297 portable Servicing/model cases
pass. The solution builds cleanly. One local full run encountered an unrelated
post-shutdown port-bind failure; the six-case Host selection and the complete
2,182-case run then pass on unchanged code. No retry, budget or assertion change
is added to CI. Native kernel execution and the installed lifecycle remain
required on Windows.

## Both platform gates pass; SDK endpoint inventory corrected

Hosted run `39423147741` proves the flush repair. Windows runs **2,178/2,178 F#**
cases (the four governed Unix-only cases are excluded), Servicing/model
297/297 and all 61 actual browser journeys. Its exact coverage is
**14,726/17,126 lines (85.9862%)** and
**15,210/24,668 branches (61.6588%)**. macOS runs 2,182/2,182 F# cases,
Servicing/model 297/297 and 61 browser journeys at
**14,679/17,126 lines (85.7118%)** and
**15,110/24,668 branches (61.2534%)**. Both supported jobs pass.

The first actual installer job publishes the NativeAOT guard/Setup and acceptance
helpers, then correctly refuses a payload/inventory count mismatch. A local
non-executing Windows cross-publish reproduces it: the Service contains 419
files but its SDK-resolved inventory contains 418. The extra file is the Web
SDK's separately copied endpoint manifest.

Inventory capture now uses the SDK's declared
`StaticWebAssetEndpointsPublishManifestPath` source and destination identity,
hashing the intermediate at publish time rather than trusting candidate bytes.
Already-resolved entries are not duplicated. Three new controls verify source
provenance/output tampering, duplicate prevention and refusal of an unproven
candidate manifest. All **22 payload controls** pass, as do 28 adverse report
controls. The complete genuine Service/Tray cross-publish now passes strict
validation for **697 runtime files**, including all 419 Service files.

The normal solution builds warning-free and passes all 2,182 F# cases.
Cross-publishing does not execute a Windows binary or establish installed
acceptance; the next actual Windows construction/lifecycle run remains required.

## VSTest in-process flush preserves native coverage collection

Hosted run `39418524069` passes Windows Core execution and all 61 actual browser
journeys, alongside Setup 456, Control 48, Servicing/model 297 and Tray 319.
The remaining failure is its coverage gate: the native report retains the full
17,126-line denominator but only 3,592 covered lines, with broad in-process API,
pipeline and access modules reporting zero hits. The macOS graph remains green.

Coverlet 6.0.2 documents VSTest's early test-host shutdown as a known source of
lost process-exit hits and recommends its in-process collector handshake. CI
now enables only that flush hook, resolving the pinned collector path and
assembly identity from existing package metadata. Native Coverlet remains the
sole instrumenter/reporter and still merges native JSON; no second collector,
source exclusion, denominator change or threshold reduction is introduced.

A controlled local tracker-log experiment confirms that
`CoverletInProcDataCollector` calls module unload before shutdown. The exact
updated workflow then passes **297/297 Servicing/model** and **2,182/2,182 F#**
cases and the unchanged five-assembly gate at
**14,683/17,126 lines (85.7351%)** and
**15,197/24,668 branches (61.6061%)**. The solution also builds warning-free and
passes all 2,182 Debug cases.

`Assert-TestExecution.ps1` validates fresh named TRX counters and prints only
aggregate counts. Eight synthetic controls reject empty, failed, skipped,
incomplete, invalid and malformed runs; the existing seven exact coverage
controls remain. The valid fixture includes VSTest's `name` attribute, and root
validation uses the DOM local name rather than PowerShell's attribute-adapted
`Name` property. Raw TRX/captured output remain private. Actual Windows flush
confirmation and installed acceptance are still required.

## Runtime closed-pipe HRESULT classification

Hosted run `39415995186` confirms the isolated-profile correction: no startup,
canonical preflight, MCP or drain failures remain. The sole Windows failure is
the surplus-write classification **after** the pipe fixture has validated its
correlated first response and closure.

The .NET 9.0.15 `PipeStream.WinIOError` implementation maps Win32 broken-pipe,
no-data and pipe-not-connected results to the same broken-pipe message while
preserving their distinct HRESULTs. The fixture now recognises exactly
`0x8007006D`, `0x800700E8` and `0x800700E9`, only on Windows while uncancelled
and after the existing first-response proof. It does not match message text or
accept generic I/O failures.

The matrix exercises all three write/closure outcomes and rejects invalid
first responses, other-platform results, raw Win32 integers, access-denied,
invalid-parameter and generic I/O errors. Independent source review approves
the narrow change. All **26 focused pipe cases** and **2,182 full F# cases**
pass locally, zero failures/skips, with a warning-free build. Exact native
collection retains five assemblies at **14,683/17,126 lines (85.7351%)** and
**15,134/24,668 branches (61.3507%)**, with all 297 Servicing/model cases passing.
Actual Windows confirmation and installed/native acceptance remain pending.

## Isolated-profile and full-duplex pipe fixture corrections

Hosted run `39411699226` confirms the startup cause: each failing owned child
reports `Composition / KnownFolder / EmptyKnownFolder`. The fixture now creates
native-path `AppData/Local` and `AppData/Roaming` beneath its owned temporary
home and sets matching child-only environment values. Both launchers reuse
the helper. All **11 profile/child-process cases** pass locally, including the
three new isolation/preservation cases; production admission is unchanged.

The revised pipe fixture starts its first-response read and combined write
before releasing dispatch. Source review approves its correlated-response,
exact-dispatch, closure, narrow Windows-error and task-settlement checks.
The expanded matrix initially hit inference and FS3511 compiler errors.
A fresh specialist replaced the two problematic resumable loops with a
monomorphic runner over deferred checks. Cases remain sequential with the same
exception identities; no warning suppression, budget or assertion change is
used. All **26 pipe cases** now pass locally, including the native integration
case and synthetic adverse/cancellation cases.

The complete candidate builds without warnings/errors and passes **2,182 F#
cases**, zero failures/skips. Exact native collection also passes 297
Servicing/model cases and retains five assemblies at
**14,683/17,126 lines (85.7351%)** and
**15,258/24,668 branches (61.8534%)**. Current-head target-Windows confirmation
and the complete installed lifecycle remain pending.

## Typed startup-refusal evidence integrated and reviewed

The existing current-user API now has an additive, single-attempt evidence
capture. Production still uses OS-known LocalApplicationData, actual loaded
MVIDs, the same lazy physical probes and process-rooted leases. Native failures
capture their error immediately; only fixed numeric stage/operation/failure,
error kind/code and ancestor-depth/image-ordinal fields leave the interpreter.
Success and other refusals do not acquire a new diagnostic or callback.

The F# adapter preserves its existing API, first refusal marker and exit 78.
Only Unavailable can append one typed line, after the marker. New tests cover
current-user composition, unchanged refusal mappings/probe ordering, native-call
failure attribution and no identifier disclosure. Five F# cases verify ordering,
known exceptions, null evidence and unchanged permitted/non-Windows behaviour.
Independent C# and F# source reviews approve their respective changes.

All **297 Servicing/model cases** and **2,170 full F# cases** pass locally, zero
failures/skips. The combined startup/pipe/drain selection passes 33 cases.
Setup remains 456/456 and Control 48/48. Acceptance, ProcessProbe, WindowsTests
and the managed Guard cross-build without warnings/errors; static controls
retain 19 payload passes and 28 adverse report passes.

The exact native recipe retains five assemblies at
**14,683/17,126 lines (85.7351%)** and
**15,225/24,668 branches (61.7196%)**, improving the measured baseline without
exclusions or threshold changes. The complete solution builds cleanly.
Actual Windows refusal evidence and target acceptance remain pending; this
instrumentation does not establish or correct the startup cause.

## Established Windows pipe and drain fixture corrections

Sanitised hosted run `39407281492` confirms that all three drain failures occur
while deleting `db.sqlite`, after the existing ownership/drain checks.
Fixture cleanup now clears only the exact fixture's idle SQLite pool after
those checks and owner cleanup. A nested-fixture regression proves that another
fixture's pool retains its handle; no global pool clear or production database
change is used.

The pipelining failure is the initial two-frame write, not a closure read:
synchronous dispatch can disconnect before that write completes on Windows.
An explicit write-completion latch now controls the synthetic dispatch. The
case still writes both frames together, decodes and compares the first reply,
requires closure and asserts exactly the first request ID was dispatched.
A cancellation regression covers the latch; no initial I/O error is swallowed
and no budget or production pipe behaviour changes.

All **21 focused cases** and **2,165 full Release F# cases** pass locally, zero
failures/skips. Target-Windows confirmation is pending.

The same hosted run confirms the two MCP fixture corrections. Eight actual
Service children still exit 78 with only `HERMES_STARTUP_UNAVAILABLE`, before
canonical preflight. That message cannot distinguish known-folder composition
from native path/query/gate failures. A C# specialist is implementing bounded,
path-free typed refusal evidence; no admission-policy correction is yet
established or claimed.

## Windows Core diagnosis and qualified MCP archive root

Hosted run `39403692526` confirms the corrected Windows Servicing/model suite:
all **251 cases pass**, alongside Setup 456, Control 48 and Tray 319. The next
Core phase exposes 14 F# failures in child startup/canonical preflight, owner
pipe closure, request-drain cleanup and two asynchronous MCP ownership cases.
Windows Core/browser and installed acceptance therefore remain blocked.

The MCP fixture supplied `/archive` to publication while constructing its
expected paths from a fully qualified root. On Windows, the ownership key
agrees but the deliberately preserved error-path spelling differs. One
qualified archive root now feeds both paths. The two exact-assertion scenarios
also run through both immediate and yielding interpreters. Production
publication behaviour and source authority are unchanged.

Early-child test diagnostics now include exit code and bounded, redacted
captures under the existing startup deadline. Two pure regressions cover
authority-shape redaction, both streams and the 4,096-character limit. CI
retains failing test names and at most 32 call frames, adds a redacted message
capped at 4,096 characters, and never uploads raw TRX or captured output.
Synthetic checks verify the actual workflow branch's masking, output limits
and preserved nonzero exit status.

All **21 focused cases** and **2,163 full F# cases** pass locally, zero
failures/skips. The solution builds without warnings/errors. The exact updated
workflow recipe passes 2,163 F# plus 251 Servicing/model cases at
**14,533/17,018 lines (85.3978%)** and
**15,104/24,636 branches (61.3087%)**, retaining the unchanged five-assembly
denominator and 85/60 gates. The remaining Windows lifecycle causes await actual
sanitised failure details; no speculative production correction is claimed.

## Enrolled-service fixture uses native absolute paths

The first two hosted Windows runs expose five fixture failures in
`EnrolledServiceTests`: `Path.Combine` preserves embedded protocol `/`
separators, while production canonicalises the absolute paths to Windows
separators. Exact fixture comparisons then miss entry-image identity and
redirection, misclassifying it as the library image.

A C# specialist corrects only the fixture: absolute paths use native components,
protocol inventory paths retain `/`, and unexpected metadata paths fail
explicitly. Two new cases exercise native and forward-slash apphost inputs; the
replacement-library case retains the loaded-generation refusal before image
metadata reads. Production containment, ordering and MVID policy are unchanged.
All **22 focused cases** and **251 portable Servicing/model cases** pass locally.

The updated exact coverage recipe retains five assemblies at
**14,533/17,018 lines (85.3978%)** and
**15,126/24,636 branches (61.3980%)**; all seven threshold controls pass.
The default solution builds cleanly and passes all 2,159 Debug cases.
Servicing coverage now logs normal diagnostic output. Windows uploads any
available Servicing Cobertura alongside the required combined report, keeping
the three-day retention; a partial report cannot satisfy the combined gate.

Hosted run `39402069802`, before this fixture correction, proves the pairing
checkpoint fix on macOS: all 102 units and 61 actual browser journeys pass,
alongside Setup 456, Control 48 and exact 85/60 coverage. Windows passes Tray
319, Setup 456 and Control 48 but still fails the old enrolled fixture.
Current-head Windows acceptance remains required.

## Hosted pairing-cancellation regression

The first candidate's macOS hosted job executes 101/102 JavaScript units and
fails the late-pairing cancellation case: one `setImmediate` does not guarantee
that asynchronous Web Crypto has completed and the deferred fetch has started.
The test now waits for an explicit fetch-entry signal before cancellation and
then releases the late response. Its polling/expiry and discarded-result
assertions remain unchanged; production code is unchanged.

The focused regression, lint and all 102 units pass locally. Current-head
hosted execution remains the acceptance gate.

## Native writer provenance correction verified locally

The fresh revision replaces classic diagnostic events with a manifest-based ETW
provider imported into Application. Native provider/PID/event-time fields bind
each diagnostic to a freshly observed OS process instance and the expected
custom-action host; cached inventory cannot substitute for that observation.
Portable regressions reject absent or contradictory headers, PID reuse,
claim/creation mismatches, replay and competing invocations.

All **249 Servicing/model cases** pass, zero failures/skips. Two initial test
failures were corrected without changing production behaviour: an
`ImmutableArray` assertion now compares its sequence, and the creation-identity
mutation expects the stronger PID-reuse refusal. Control remains **48/48**.
Acceptance, ProcessProbe, WindowsTests and the managed Guard cross-build with
zero warnings/errors. The strict report suite passes its valid fixture and
**28 adverse controls**; static packaging retains all 19 payload passes.

The exact native coverage recipe executes all 249 model/Servicing and 2,159 F#
cases and retains five assemblies at **14,533/17,018 lines (85.3978%)** and
**15,043/24,636 branches (61.0610%)** in the final rebuilt candidate. Both integer gates and all seven threshold
controls pass. The default solution also builds without warnings/errors and
passes all 2,159 Debug cases.

Independent source re-review approves the corrected provenance, report and
non-authoritative transport/harness, closing the prior finding. Microsoft's
native header and event XML documentation confirm that their process fields
identify the event's generating process. The manifest parses, and its harness
refuses macOS before filesystem/profile effects. Actual Windows resource
compilation, registration, event delivery, MSI schemas, installed lifecycle and
published-artifact acceptance remain pending.

## Five-assembly coverage and independent observer review

The exact CI collection recipe now executes **212 portable Servicing/model
cases** and **2,159 F# cases**, with zero failures/skips. Its native JSON merge
retains Core, Service, UI, Servicing and the new Acceptance.Model assembly at
**14,447/16,932 lines (85.3236%)** and
**15,110/24,582 branches (61.4677%)**. Both integer gates and all seven threshold
regression controls pass. The additional model initially took line coverage
below 85%; 37 new parser/lifecycle/correlation cases cover its real positive and
negative paths rather than removing it from measurement.

The new cases exercise lifecycle 1040/1042, install/removal/configuration outcomes
1033/1034/1035, both ProductCode encodings, and distinct engine/client identities.
The synthetic records validate parser and correlation behaviour; actual Windows
provider schemas still require target-host execution.

Control passes **48/48 portable cases** after separating Windows-only observation
and its failure sink from the portable authority. Acceptance, ProcessProbe,
WindowsTests and the managed Guard cross-build without warnings/errors. The
default solution also builds with zero warnings/errors and passes all 2,159
Debug cases.

Workflow source now restores, builds and runs the portable Setup and Control
projects explicitly on both supported platforms; neither was included in the
default solution or Windows-only test project. The exact new local build/test
commands pass all 456 Setup and 48 Control cases. Target-host CI execution
remains pending. Generated `.artifacts/` output is excluded from Git.

Independent source review approves the kernel/process component and the
non-authoritative Control/Guard callback seam. It rejects the independent
writer-provenance proof: checking the PID/start time claimed in diagnostic JSON
does not establish which process reported the native event. A fresh author owns
the correction; the original author is excluded from that revision. Actual
Windows execution, complete installed lifecycle acceptance and publication
remain pending.

## Corrective repair locally verified and independently approved

The fresh aggregate revision passes **456/456 portable Setup cases**, zero
failures/skips. Pre-servicing registered ownership can admit a definitively
owned missing/damaged payload; complete hash/length/MVID proof remains required
before Ready. New repair, same-package reinstall, upgrade/downgrade and uninstall
cases retain binding, ownership, path, retained-media, fence and pending-work
negatives. Failed restoration of damaged bytes remains Closed. The fixture's
three authored version fields now derive from its manifest, allowing the real
1.2.4 upgrade/downgrade scenarios to reach MSI and verification.

Independent root re-review approves this distinction and carries forward the
Main/compiled-trust/composition/pin-lifetime findings. The whole default solution
also builds without warnings/errors and runs 2,159 Debug cases with zero
failures/skips; the separate Release/native coverage and browser evidence below
remain valid.

The actual Windows diagnostic component and its process probe and Windows-test
projects now cross-build without warnings/errors. The single mechanical
compilation correction wraps an `ImmutableArray`-returning method group in its
typed `SelectMany` lambda. Attempting only the four pure report-policy cases on
macOS fails assembly resolution for the Windows-RID helper; it supplies no
passing test or Windows-runtime evidence.

Its first source delivery explicitly leaves five old-engine-outcome cases
Blocked and returns incomplete exit 3; those results cannot satisfy publication.
A concrete independent observer binding is still being authored and must be
source-reviewed and exercised on the disposable Windows host. Construction-gate
replacement is authorised only for the now-reviewed complete construction;
runtime, MSI, recovery/interleaving and publication gates remain mandatory.

## Exact local coverage gates passed; Windows acceptance still pending

All **2,159 F# cases** and **154 portable Servicing cases** pass, with zero
failures/skips and a warning-free build. The last bounded MCP variation reuses
the independently approved yielding interpreter and retains exact public tool,
transaction, source/history and artifact assertions.

Native Coverlet 6.0.2 collection and native JSON merging preserve Core,
Service, UI and Servicing at **14,127/16,594 lines (85.1332%)** and
**14,835/24,311 branches (61.0218%)**. The exact workflow step was then extracted
and executed locally: its independent run reports the same line counts and
14,775/24,311 branches (60.7750%). Both satisfy the integer gates without
rounding, exclusions or denominator changes. `Assert-Coverage.ps1` now enforces
85% line and 60% branch on both CI platforms, requires all four assemblies and
validates the counters. Seven regression controls prove exact boundaries pass,
rounded near-misses fail, and missing assemblies or invalid denominators fail.
The tool version is pinned in `.config/dotnet-tools.json`.

The latest canonical bundle retains 61 passing actual Chromium journeys and
102 passing units. The complete initial Setup root suite passes 399 portable
cases, but independent review identifies a real corrective-repair precondition:
full payload equality is required before repairing a definitively owned
missing/damaged file. A fresh eligible author has supplied its bounded
ownership/post-verification distinction and regressions; that revision is not
yet accepted. Native MSI construction remains blocked.

Installer payload/schema/coordinator/workflow static validation passes all
19 payload cases and its additional contract checks. Actual Windows units,
native exports/callbacks, interrupted recovery/interleavings and complete
installed SDK-free servicing remain separate mandatory gates. No post-financial
code slice has yet been published.

## Latest conformance and executable-root integration

The complete current F# graph builds without warnings/errors and passes
2,155/2,155 cases, with zero failures/skips. New controlled and yielding
interpreter cases exercise scheduler dependencies, real transaction ownership,
source claims, recovery and MCP reads/mutations. A real staged-source conflict
exposed a faulted MCP request task: typed ownership rejection now remains under
the metadata writer transaction and publication fence, returns a tool error,
and cannot advance the artifact revision. Existing throwing guards and
unrelated programming-fault propagation are preserved. Independent review
approves both that production delta and the test-only interpreter decoration.

Pinned local `coverlet.console` 6.0.2 collects Servicing separately and merges
its native JSON while executing the full F# graph. Both actual test runs pass
(154 and 2,155 cases). The resulting native Cobertura retains all four
assemblies and reports **14,127/16,594 lines** and
**14,550/24,311 branches**: the exact 60% branch gate needs **37 more covered
branches**. A standard ReportGenerator trial changed the source denominator
and is not used for gating; the checked local-tool manifest pins native
Coverlet instead. Further meaningful MCP immediate/yielding variations are
being authored, not counted as executed.

The OS installation-observation and MSI authoring-validator components build
cleanly, pass 328 portable cases and have independent source approval. The
subsequent actual Setup aggregate/factory/entry source has been applied and
builds cleanly, but its expanded suite is **347 passed, 43 failed, zero
skipped, 390 total**. Most failures encounter synthetic UNC/host path
interpretation before their ownership or post-state assertions; fresh-install
and other aggregate outcomes also remain unproved. The eligible author owns a
coherent correction; no tests are skipped or expected safety outcomes relaxed.
Its executable/publication wiring is not installer acceptance, and the
unconditional MSI construction block remains.

The exact latest canonical bundle still has 61 passing real Chromium journeys,
102 passing JavaScript units and clean lint/build. Actual Windows native,
installed-runtime and complete servicing acceptance remain outstanding. No
post-financial slice is yet published.

## Complete local access graph — executed, not yet published

The complete Release graph now builds without warnings/errors and executes
2,133/2,133 F# cases, with zero failures/skips. Independent source re-review
approves the corrected Host/Program request lifetime, shared canonical routing,
early owner CLI/default target selection and real authenticated HTTP fixtures.
The final private-adapter revision is also approved: stale pinned requests are
rejected against the actual composition identity before any phase-dependent
owner call. Its real pipe scenario traverses Starting, Ready and Stopped while
retaining listener health, pending pairing and existing session authority.

All 61 actual Chromium cases pass against the exact canonical bundle
`1ac59912876db25479f308c61d76c64f40f9af766bbea20808a99b0b4ca97c34`,
served by the rebuilt isolated Service with real foreground owner-CLI approval.
The source also passes lint, build and 102 JavaScript units. The browser run
includes protected persistence, local locking and real revocation, restart
fencing, two-instance previews, and actual PDF rendering. The transport now
preserves null-body responses and fences generation changes after pending body
cancellation. Native option disabling is checked through its DOM property and
keyboard behaviour; the focus/save case restores its own synthetic preferences.

Portable Servicing executes 154/154 cases. Setup executes 181/181, including
the corrected descendant-absence, native unregistered-component and complete
compressed-media controls plus additive query-snapshot tests. Independent
re-review approves the three I/O-leaf corrections. New Windows observation
readers and an MSI authoring validator are separate uncompiled integration
batches; aggregate verification and executable Setup composition remain missing.

The F# collector alone includes the newly referenced Servicing assembly without
executing its portable suite: 13,185/16,579 lines and 13,338/24,245 branches.
A separate proper Servicing collection reports 913/1,124 lines and 322/453
branches. The resolved source identities are identical, its line coverage is a
superset, and the first report covers zero Servicing branches. Their
denominator-preserving union therefore retains Core, Service, UI and Servicing
at **14,096/16,579 lines (85.0232%)** and **13,660/24,245 branches (56.3415%)**.
No assemblies or source are excluded. Another 887 covered branches are needed
for the exact 60% gate; CI aggregation and target-OS evidence remain outstanding.
The initial combined VSTest attempt without a discoverable collector supplies
test execution only, not coverage evidence.

Actual Windows units, owned authenticated WebView and the complete installed
MSI lifecycle remain unexecuted for this graph. No new code slice has been
published since the financial merge; MSI production remains blocked.

## Unpublished access integration and servicing-policy revision

The Release .NET test graph builds with zero warnings/errors after integrating
the real owner pipe, bounded authority runtime, HTTP protocol/private adapter,
outer Service startup admission and suspended V5 controls. The combined
`LocalAccessRuntime`, `OwnerControlPipe`, `V5AsyncTests`, `LocalAccessHttp`,
`OwnerAccessAdapterTests` and `StartupAdmissionTests` selection passes all
79 cases, with zero failures/skips.

The separate status-only discovery addition also builds cleanly. Its combined
`OwnerStatusDiscovery`, `OwnerControlPipe` and `LocalAccessWire` selection
passes 127 cases, with zero failures/skips. These exercise actual macOS
same-owner pipe exchanges as well as strict codecs; they are not Windows
acceptance. Discovery precedes HTTP identity comparison and cannot resolve or
approve a pairing. The existing completely pinned client remains strict.

Independent source review approves the bounded actor and F# pre-effect
startup hook. It rejects stale-instance outbound encoding that can terminate
the listener, malformed escaped-Unicode handling, and missing no-store headers
on framework protocol errors. Fresh revisions are required before the boundary
can be accepted. Passing component cases do not cover those missing paths.
The real Host, early owner CLI and existing-route/authenticated-fixture batches
are being integrated separately; their complete graph and application journeys
have not yet passed.

The fresh Setup publication-policy revision passes all 72 portable cases,
including 17 new returned-failure, throw-after-visible-Ready, failed-reclosure
and pending-reclosure/disposal controls. Independent source re-review approves
the revision: only a successful confirmed reclosure claims Closed; otherwise
the result is Unknown and preserves its native receipt. Unknown does not prove
runtime denial. Ready publication is reachable only after completed guarded
MSI execution, durable receipt and safe-target verification in the source model.
Production I/O, executable composition and actual Windows acceptance remain
separate gates.

The shared servicing library now passes 154 portable cases, including 20
enrolled-child controls; private MSI control has 46 passing portable cases.
Independent review approves the fresh native revision: actual Tray,
diagnostic and fixture-helper entries acquire pre-effect admission, installed
child selection validates the enrolled physical Service/MVIDs before launch,
and navigation completion is bound to the initiating engine ID. Both native
tests and the BCL-only Smoke executable cross-build without warnings/errors.
The shared `SmokeEntry` is exercised by source-linked tests rather than linking
its executable `Main`. Actual Windows units, owned authenticated WebView and
installed MSI lifecycle have not run for this graph.

Concrete Setup I/O leaves build without warnings/errors and bring the portable
Setup selection to 96 passing cases. Their 24 new cases cover actual owned
temporary-file persistence, retention and inventory functions. Independent
review nevertheless rejects descendant-junction false absence, unregistered
component result handling and acceptance of loose files in supposedly complete
retained MSI media. A fresh author owns that revision; aggregate inspection,
verification and executable composition remain separate. MSI publication
remains blocked.

The full Host/CLI/real-route graph has not compiled: its first attempt reports
40 production diagnostics, mostly CLI qualification/type annotations. Separate
source review also rejects production canonical routing that never reaches its
middleware, disposal beneath still-running admitted HTTP work, and default
environment-port parsing that differs from the running Service. A fresh
shared production/fixture/lifetime revision is being applied. Three further
test-only V5 cases have been added for genuinely pending embedding, failed
replacement/retry and stale generation, including production semantic retrieval;
they are not yet compiled or executed.

The last full measured access-foundations coverage remains the 1,849-case
baseline below. No newer full coverage result, 85%/60% close, authentication
completion or installer promotion is claimed.

## Financial slice integrated; current unfinished foundations

#29 merged as `e7758a3797dc6ca5d52ae1ac573a2a4802f0a27a`.
Exact-head run `39361173950` passes all five real checks: Windows executes
1,714 cases and Mac 1,718, with zero failures/skips. Both execute all 45
browser cases and 21 JavaScript units. Windows also passes all 100 native
cases and real WebView2 154.0.4258.62 proof for owned PID 4312, followed by
graceful unforced exit 0 and successful cleanup.

Main run `39362778578` passes all seven jobs. Each exact published Service
bundle passes all 45 browser cases before upload and records manifest
`b29507df6e860e22caa76b75c940438d42d355210d7d931f0e3ae3ca8d8c1e96`.
Main Windows root is 11,108/13,209 lines and 10,581/20,820 branches across
Core/Service/UI. The 85%/60% close gate remains unmet.

The separate uncommitted foundations graph builds cleanly and runs
1,849/1,849 Mac cases with zero failures/skips. Its root is 11,754/13,901
lines and 11,275/21,350 branches, retaining the same assemblies. The 131
focused passes comprise 49 pure-domain cases, 64 strict wire cases, 12 target
naming cases and six genuinely suspended email-capability cases.

The fresh domain collision revision is independently approved: live and
retained credential digests cannot be reissued; revoked digests join the
existing bounded recent-history store. Seven new cases preserve approval,
prepared rejection state and the invalid-proof budget while proving old-token
denial, noncolliding recovery, retention and capacity. This is not perpetual
entropy history or a completed HTTP authentication feature.

The four new `AsyncCapabilityControl` / `AsyncEmailConformance*` modules
exercise controlled pending provider/SQLite effects, original archive/history,
source claims, retry, duplicate content and ownership races without sleeps.
Strict wire/target and these email cases still require independent review.
Owner-pipe I/O, runtime serialization, HTTP/CLI/native wiring and actual
Windows/Mac journeys remain separate gates.

Native client lifetime revisions cross-build, but a cancelled-successful-
settlement path remains under fresh revision. The browser source revision
is independently approved with 99 passing units and 61 discovered cases;
a standalone Chromium PDF probe is not canonical-app/authentication proof.

The strict servicing-library revision passes 94 portable cases; its actual
Windows kernel tests remain unexecuted. The separate MSI private-control
slice passes 16 fake-effect cases; NativeAOT loading and full Setup/recovery
orchestration are not thereby established. Fixed-root Windows source/dev
classification is being implemented before the F# startup hook can be
enabled safely. MSI construction continues to fail closed.

## Financial preservation slice — exact staged-tree local proof

The financial/Workflow/API slice is validated separately from unfinished
authentication, React and installer changes. Its code tree
`8a188c010f2a214e6a2f357954e75c12224e0345` was exported to an owned validation
directory without private financial fixtures. The only existing binary test
fixtures copied were the two named synthetic sample presentations.

Fresh `npm ci` and the baseline canonical React build pass. The exported
Release test graph builds with zero warnings/errors and executes all
1,718 cases: 1,718 passed, zero failed and zero skipped. Root coverage is
11,108/13,209 lines and 10,698/20,820 branches, retaining Core/Service/UI.
The unchanged 85% line / 60% branch close gate is not met.

All ten financial parity cases now assert generated PDF/CSV source values.
Additional PDF controls establish exact dates, identifiers and amounts across
three fonts, while explicit small spaces, positioned columns and separate
lines remain distinct. Invalid/empty PDF controls reject explicitly.
The advance-based repair changes no fixture font or gap coefficient.

The fresh Workflow revision is independently approved: a recoverable
dual-persistence failure does not strand the following document; the exact
underlying error survives; faulted input terminates once; and both 20K
prebuffered cases drain naturally with caller-owned output completion.

HTTP cases assert whole-request correction validation and transactional
rollback, missing-resource refusal and bounded pagination. Linked reminders
read the current document vendor across two corrections through document
detail, REST and MCP without rewriting reminder rows or earlier history.
The oracle permits only the existing category-provenance trigger's exact
new tag on the first correction; a same-label user tag and every other
retained DB/FS value remain unchanged. The second correction preserves the
entire tag table. Narrow reminder re-review and actual published-head
Windows/macOS proof remain required before integration.

## Unpublished local-access foundations

`LocalAccessModel`, `LocalAccessDomain`, `LocalAccessDomainTests` and
`LocalAccessBoundaryTests` compile with zero warnings/errors; their 42
focused cases pass with no skips. This establishes pure bounded-state,
representation and cryptographic-proof contracts, not HTTP or pipe authority.
The host, early CLI, bounded interpreter and end-to-end integration remain
separate. Mechanical compilation corrections add explicit record/input types,
list assertion types and the nullable challenge-parser input annotation;
no warnings are suppressed.

The native access client/navigation foundations and unit project cross-build
cleanly. The standalone installed-Tray harness is now BCL-only `net9.0` with
a Windows annotation; scoped restore removes the stale Tray/WebView graph.
Its self-contained Windows publish contains no replacement Tray/WebView
assemblies. Neither cross-build nor publish executes Windows.

Independent review approves the revised standard-MSI/coordinator architecture
for implementation only. The shared startup fence, coordinator, native
actions and actual installer acceptance remain unshipped work; packaging
continues to fail closed rather than emit an unguarded MSI.

## Mechanically applied installed-native proposal — not acceptance

The independent C# author `ec93e27c-96ce-4a56-b5ac-8f2459a3c202` supplied the
installed-Tray B revision and its narrow owner-fixture follow-up. All nineteen
C#/project/test patch blocks across fifteen files were mechanically applied and
verified byte-for-byte by reconstructing both authored patches against the
original HEAD. No C# content was generated by the installer integration author.

The external harness no longer references the Tray assembly. Its installed
apphost diagnostic retains the existing supervisor, WebView/React proof and
ownership oracle, adds OS PID/start identity and installed-file hashes, and
accepts the bounded paired ready/continue hold. The original B proposal adds 25
cases and the owner-fixture follow-up adds 17; 142 native cases are expected
only, pending actual discovery. The parent reports a clean cross-build of the
earlier B test project before this follow-up; that is not Windows execution.
The latest fixture changes were not built or executed by the installer
integration author, and no runtime, coverage or installer result is claimed.

PowerShell now uses the actual installed-Tray CLI with a 300-second deadline and
new report/signal paths below the hosted runner's `RUNNER_TEMP`, copying only
bounded synthetic reports to the upload directory. CI moves real WebView proof
to the blocking installed lifecycle gate rather than invoking the removed
Service-only harness contract.

Before recording the preservation baseline, PowerShell creates only the
synthetic archive and calls the native `--create-owner-fixture` helper with a
new receipt under `RUNNER_TEMP`. The helper requires an absent genuine owner
directory and atomically publishes a newly attested fixture without replacement.
Every smoke call reuses `--owner-fixture-receipt`; the final report must verify
owner state, exact fixture hashes and the original fixture ID. PowerShell's
cross-transition checks retain config/settings/marker/receipt hashes. Neither
helper nor script adds automatic owner-profile deletion.

Servicing admission A remains architecturally unresolved across every entrypoint,
including direct Service launches and ungated-binary migration. No custom-action
project/output/exports/sequencing contract is agreed; MSI production remains
fail-closed. The owner-config conflict has a mechanically integrated,
language-authored fixture-provenance resolution, not an admission bypass.
Language review and dedicated Windows execution remain mandatory.

## Synthetic financial and Core candidate execution

The pre-Service-route-delta Release graph compiles with zero warnings/errors.
Its first focused run selects 151 cases: 145 passed, six failed and none
skipped. The selection comprises 102 MCP cases, 12 synthetic parity cases
and 37 new Core regressions. All five generated financial PDFs fail required
value assertions; the five CSV cases and two negative controls pass.
An actual synthetic payslip trace retains correct raw PDF letter values but
introduces spaces inside dates, identifiers and monetary values during
word/line construction. The original financial assertions are retained;
extractor/generator diagnosis does not use any private fixture.

One dual-persistence-failure Workflow case also fails its original-cause
assertion. Independent F# review rejects the completion revision because its
outer catch also permanently stops the consumer after that recoverable
failure. A fresh author owns continuation/cause preservation and a
deterministic following-document regression. Neither the selected passes
nor the clean build establish full-suite discovery, coverage, Windows proof
or a completed parity repair.

## Independent Windows installer revision controls

`node --test installer/windows/payload.test.mjs` passes fourteen synthetic controls:
complete SDK-inventory acceptance, stable relative-path component identity,
unexpected archive/PDF, credential, log and configuration rejection, forged
inventory rejection, missing runtime-essential assembly rejection, unexpected
empty-directory rejection, changed-byte rejection, publish-time hashes surviving
later compiler-output changes, canonical web anti-substitution and re-signed
archive rejection, MSI version
boundaries, and per-component HKCU keypath generation. Fixtures stay in ignored project build output; these
controls never invoke Windows Installer or access a profile/provider.

The revised disposable-Windows lifecycle script requires empty related-product
inventory before synthetic state, changed Service/Tray assembly bytes between
versions, complete installed inventory/shortcut/Run/product registration checks
after each transition, direct-MSI and wrapper running-app refusal, explicit
rollback, retained unknown user files and pre-cleanup failure/native evidence.
PowerShell/XML/workflow static validation passes. Actual ICE-clean MSI builds,
native servicing-lock integration and installed-Tray Windows execution remain
required; source checks do not promote the installer. Packaging deliberately
fails closed with an unconditional project error until independently reviewed
servicing-admission construction replaces the gate; an empty placeholder file
cannot enable an unguarded MSI.
`pwsh -NoProfile -File installer/windows/Test-Static.ps1` also covers script/XML
parsing, both MSI-version validators, the real MSBuild SDK-source metadata/hash
capture task, workflow lint, excluded macOS wrapper syntax and diff whitespace.

## Integrated canonical delivery and next candidate

#27 merged as `db5beec014b0d7b932475c5d954edd54b96bfaf6`; #8/#9 are closed.
Final published-head Windows selects 1,567 .NET cases: 1,560 passed, seven
retained skips and zero failed. Mac executes 1,571: 1,564 passed, seven skips
and zero failed. Both pass all 45 browser cases and 21 JavaScript unit cases.
Actual Windows executes 100 native cases with no skips and passes the real
WebView2 ownership, routing and graceful-shutdown smoke.

Main run `39339871190` passes all seven jobs. Each exact main-published Service
bundle passes all 45 browser cases before upload. Main root coverage is
10,623/13,116 lines and 9,958/20,728 branches, retaining Core/Service/UI.
These results complete the canonical-delivery slice, not #18's 85% line /
60% branch close gate or #28's forthcoming HTTP session authority.

The next unexecuted candidate introduces generated synthetic PDF/CSV financial
fixtures and replaces the ten optional/private-fixture parity definitions.
It also adds explicit cross-platform relative-path MCP controls and the new
`CoreRegressionSupport`, `WorkflowFailureRegressionTests`,
`WorkflowLeaseRegressionTests`, `LegacyPipelineRegressionTests`,
`PipelineV5BatchRegressionTests` and `GenerationValidationRegressionTests`
modules. No private fixture is opened. The combined Release build currently
fails new-test nullability/type diagnostics, so no new executed count,
discovery result, skip removal or coverage gain is claimed.

`installer/windows/Test-Installer.ps1` defines a separate disposable
GitHub-hosted Windows install/launch/upgrade/rollback/uninstall acceptance
journey, including SDK-independent runtime proof and unchanged synthetic
user-data hashes. Script/XML parsing passes; actual WiX validation and MSI
execution remain pending. This is not an installer promotion result.

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
