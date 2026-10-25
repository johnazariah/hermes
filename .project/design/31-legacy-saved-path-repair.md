# 31 - Safe legacy saved-path recovery

## Purpose

Repair a legacy document locator without changing its recorded source identity,
moving files, guessing among candidates or invalidating retained work. Ordinary
metadata-only reclassification remains independent of this operator.

## Consistency limit

An archive-wide scan of an ordinarily writable filesystem cannot prove unique
hash membership at the instant SQLite commits a repaired pointer.

For example, after the scanner visits `a/` and finds its only matching file in
`z/`, another process can add matching bytes to `a/` before commit. A complete
cursor, final candidate rehash, second enumeration, directory timestamp or
asynchronous watcher notification does not close that interval.

SQLite locks and Hermes publication fences govern cooperating database
writers. They do not prevent an unrelated filesystem writer creating another
candidate. An immutable snapshot proves facts about that snapshot, not about a
different live namespace subsequently used by archive readers.

The strict recovery contract therefore requires a trusted authority that keeps
the relevant source namespace immutable throughout enumeration, verification
and database commit. The portable filesystem capability does not establish
that authority. Without it, apply returns `SafetyPrerequisiteUnavailable`
without scanning, migrating or changing the archive.

This refusal does not constitute a completed repair capability.

## Identity evidence

Recorded SHA-256 establishes expected content identity under the existing
collision-resistance assumption. It does not establish current location or
unique archive membership.

Canonical ownership and `archive_source_claims` establish conservative
database ownership of paths. They do not inventory unregistered files.
Sidecars remain mutable, copyable descriptions; provider IDs and filename
hashes are useful ingestion identities, not authenticated location authority.
None of these alone substitutes for a complete protected view.

Owner selection could authorise an explicit matching-file assignment under a
different contract. It must not be reported as proof of archive-wide uniqueness.

## Required invariants

| Invariant | Requirement |
|-----------|-------------|
| Authority | Explicit archive/database authority, validated against their actual identities |
| Completeness | Full bounded enumeration of one protected namespace, never a cursor prefix |
| Uniqueness | Exactly one admissible directory entry with the recorded full digest |
| Ownership | No duplicate old/target owner or appropriated staged claim |
| Source identity | Recorded digest, source type and ingestion identity remain unchanged |
| Retained work | Source dispatch, artifact access, folder context and publication attribution remain equivalent |
| Atomicity | Path metadata and successful audit commit together, or neither commits |
| Files | No source, artifact or sidecar mutation capability |
| Outcomes | Observation, refusal, rollback, committed success and unknown outcome remain distinct |
| Compatibility | Ordinary source reassignment still advances generation and invalidates current work |

## Protected namespace

A reviewed platform authority must prevent additions, removals, renames,
replacement, new links and byte changes until the transaction settles. It must
cover pre-existing handles, aliases, ancestor changes, mounts and root identity.
A timer-based lease that can expire between validation and commit is insufficient.

The enumerated universe includes hidden, generated, unregistered and temporary
regular files. Distinct hard links and directory entries count separately.
Unsupported links, unreadable entries, ambiguous canonical aliases or exhausted
bounds prevent a complete decision.

SQLite remains writable. Because its database currently lives in the archive,
the authority must define a precise, reviewed exception for database-owned
metadata/journal paths. Such paths cannot have document owners or source claims,
and the exception cannot permit arbitrary writes to surrounding source entries.
Making an entire source-and-database volume read-only is not an enabled solution
to this requirement.

No platform snapshot, ACL change, elevated permission or maintenance environment
is implied by the operator interface.

### Platform mechanisms that do not establish this authority

Directory handles and a second enumeration do not by themselves exclude new
entries or writes through existing file handles. A read-only mounted view also
does not protect a separately writable alias of the underlying source volume.
Any proposed mount-based authority must account for those aliases and retain
the same protected namespace through the SQLite commit.

Linux inode immutability is not an accepted replacement for that proof.
`FS_IMMUTABLE_FL` restricts content and namespace changes, but the `chattr(1)`
documentation explicitly warns that immutable and append-only flags do not
affect writes through already-existing file descriptors. Recursively setting
`+i` therefore does not satisfy the pre-existing-handle requirement. Filesystem
and kernel-specific behaviour would need direct qualification before reliance
on a stronger guarantee.

A concrete maintenance authority must establish exclusive source ownership,
exclude pre-existing writers and writable aliases, preserve ancestor/root
identity, and provide the narrowly scoped writable SQLite exception. Merely
stopping Hermes or changing ordinary file permissions is insufficient.

## Local operator contract

The proposed command is `repair-saved-paths`, routed before normal Service
initialisation. It starts no server, provider, producer, watcher or pipeline.

Dry-run is the default. An explicit operator configuration must identify an
existing archive/database; normal configuration defaults are not authority.
Select 1-100 positive, distinct document IDs. Reject unknown/repeated flags,
ranges, implicit whole-archive operation and caller-supplied candidate paths or
scan cursors.

Apply additionally supplies an operation UUID and expected observation digest.
It obtains a fresh protected view and recomputes evidence rather than trusting
a prior scan. The digest detects a changed observation; it is not a capability
or proof of completeness.

Initial resource bounds are 100,000 namespace entries, depth 64, 64 MiB per
file, 4 GiB total hashed bytes, 120 seconds for enumeration and five seconds for
the final transaction. Hash bounded streams. An inaccessible or oversized entry,
cancellation or limit exhaustion means incomplete evidence, never a skipped
candidate followed by success.

The selected batch is atomic. Any ineligible item or failed precondition refuses
the batch. No resumable scan cache or automatic limit escalation is included.

## Eligibility and workflow continuity

An already-valid locator reports no change. A missing old locator or equivalent
path spelling may be eligible only after uniqueness and ownership are proved.
If the old path contains different bytes, report `SourceIdentityMismatch`.

Matching source bytes alone cannot justify preserving completed work. Extraction
reads derive `<saved_path>.extracted.md`, and shared artifacts depend on folder
context. A completed document additionally requires unchanged dispatch,
thread/folder semantics, equivalent publication-fence identities and every
path-derived artifact still available as the same protected object or an
independently verified byte-identical, already-attributed artifact.

Do not rewrite canonical JSON, relabel publications, create reembed provenance,
assign shared enrichment by proximity or restore deleted completion markers.
Unproved continuity returns `ArtifactContinuityUnproven` without changing paths
or clearing existing work. General artifact relocation is outside this contract.

Reject active/stale attempt leases and active reflow affecting the selected
document or shared artifacts. Repair never steals or recovers them. Every
source-dependent publisher must have durable ownership or be excluded by a
proved offline boundary; an empty lease table alone is insufficient.

## Intentional trigger compatibility

The normal source-change trigger advances generation, removes completions and
content-index markers, and returns a reassigned document to `received`.
That behavior remains correct for ordinary path/hash/type changes.

A proved same-identity locator correction needs a narrowly scoped
dedicated-connection, transaction-bound, single-statement authorisation. It
must match operation/document identity, exact old/new paths and keys, unchanged
digest/type/folder fields, generation and precondition fingerprint. It is
default-deny, unavailable through normal database callers, not persisted as a
global enable bit, and revoked on success, exception and cancellation.

Only an exact authorised locator transition may preserve current work.
Canonical ownership and claim triggers still run. Normal reassignment, changed
digest/type, incomplete permits or conflicting ownership retain existing
invalidation/refusal. Trigger evaluation cannot depend on ordering or consuming
a permit during its first predicate evaluation.

The metadata FTS trigger also needs a separately tested restriction to actual
indexed-column changes; otherwise a path-only update rewrites unrelated FTS.
Preservation must occur atomically, not by reconstructing deleted state.

## Audit and failure semantics

The allowed write set is the selected locator fields and repair audit rows.
Source bytes/hashes, user corrections, annotations, workflow ledger, attempts,
outputs, publications, provenance, claims and indexes remain unchanged.

Audit identifies the operation, selected IDs, old/new locators, input digest,
protected-view identity, precondition fingerprint and observed result. It
contains no source text. Success rows share the path-update transaction.
Failure reporting is explicit and does not masquerade as a committed audit.

After interruption, operation lookup distinguishes committed, absent and
unverifiable outcomes. `OutcomeUnknown` is nonzero and requires lookup before
another mutation. A cursor or operation UUID cannot revive expired evidence.

## Synthetic proof and enablement

Before implementation, independent review must approve the safety model,
continuity rules, narrow trigger authorisation, operator contract and one
concrete platform authority.

Tests must cover unique/missing/duplicate-hash candidates, duplicate owners and
claims, out-of-archive/case/Unicode aliases, behind-cursor additions, forged/stale
observations, interruption/restart, exhausted/inaccessible scans, busy publishers,
artifact continuity, wrong permits and injected transaction/uncertain-commit
failures. Assert the exact allowed write set and before/after source, workflow
and user-state hashes. Unsupported protection must refuse before mutation.

Only synthetic fixtures are authorised. Personal or de-identified corpus use
requires new explicit owner approval of bounded IDs and queries.

## Product decision

The recommended strict policy retains refusal until a concrete protected
namespace is proved. Two alternatives require an explicit owner decision:
authorise platform-specific maintenance-authority work, or replace global
uniqueness with a clearly named owner-selected identity-attested assignment.
The latter is a different recovery contract, not fulfilment of strict uniqueness.

## References

- [28 - File-first archive](28-file-first-archive.md)
- [30 - Pipeline V5](30-pipeline-v5-architecture.md)
- Issue #24 and the active stabilization wave
- [Linux inode flag semantics](https://man7.org/linux/man-pages/man2/ioctl_iflags.2.html)
  and [`chattr(1)` limitations](https://man7.org/linux/man-pages/man1/chattr.1.html)
- `SavedPathSchema.fs`, `SourceFileAccess.fs`, `Database.fs`,
  `FileContentSchema.fs`, `ArchiveWriter.fs`, `PublicationFence.fs`,
  `Generation.fs`, `FileContentReindex.fs`, `PersistedComprehension.fs`
