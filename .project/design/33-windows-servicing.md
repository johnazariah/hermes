# 33 - Windows Servicing and Runtime Admission

## Purpose and scope

Windows servicing uses a distributed, SDK-free coordinator and standard Windows
Installer ownership, installation and rollback. The coordinator controls runtime
admission; it does not copy application payloads or implement a second rollback
engine. Raw MSI servicing is unsupported and must fail truthfully before
application mutation.

The product is the canonical Service user process and Windows Tray, with React
served by the Service. This contract does not promote MAUI or macOS installers.
Configuration, archives and unknown application files remain user-owned.

The package retains UpgradeCode
`7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A`.

## Fixed installation identity

The only supported installer application root, called **AppRoot** below, is:

```text
Windows LocalApplicationData known folder for the invoking user
  \Hermes\App
```

`%LOCALAPPDATA%\Hermes\App` is shorthand for that OS-resolved identity, not
authority supplied by an environment variable, configuration file, registry
claim or command-line argument. There is no custom relocation feature.

The root is logically reserved before enrollment and remains reserved through
partial installation or uninstall. Absence of registration or an admission file
does not turn a binary inside that root into a source/development executable.

Setup and both MSI guards must reject a different root, including an
`INSTALLFOLDER` override, before mutation. Registration that actually identifies
an incompatible root must cause refusal, not adoption or relocation.
Component boundaries and aliases matter: textual prefix comparison is not a
sufficient identity check. Ambiguous identity or redirection must fail closed.

This policy changes neither the configuration root nor the configured archive
root. It grants no machine-wide, account or OS-privilege authority.

### Source and development execution

Source/development execution outside the reserved installation remains a
distinct, positively classified case. Classification uses the executing entry
and the actual loaded Hermes assembly paths, with component- and alias-aware
identity checks.

Only a positive determination that those paths are outside AppRoot permits the
outside-installation route. A missing gate alone is not that determination.
Mixed inside/outside images, aliases whose relationship cannot be established,
and other ambiguity refuse startup. No argument or environment switch grants
an outside-installation classification.

## Construction and authority

| Unit | Contract |
|------|----------|
| Shared servicing library | `src/Hermes.Servicing/Hermes.Servicing.csproj`, portable `net9.0`; records, startup capability and platform-annotated Windows interpreter |
| Distributed coordinator | `installer/windows/Hermes.Setup/Hermes.Setup.csproj`, `net9.0-windows`, `win-x64`, self-contained, non-single-file executable |
| Native MSI guard | `installer/windows/Hermes.Installer.Guard/Hermes.Installer.Guard.csproj`, `net9.0-windows`, `win-x64`, NativeAOT shared library, self-contained |

The native binary is `Hermes.Installer.Guard.dll`, embedded in the MSI Binary
table. It exposes exactly `HermesPreflight` and `HermesTransactionFence`, each
using the native MSI DLL-action signature `UINT __stdcall Export(MSIHANDLE)`.
The guard requires no separately installed .NET runtime or SDK.

There is no DTF/net48 host, deferred managed host, second supervisor or
long-lived custom-action state owning admission. The diagnostic harness and its
ready/continue hold are evidence machinery, not servicing locks.

The coordinator runs from distributed media outside AppRoot. Before invoking
MSI it retains a complete package source and matching inventory in the per-user
servicing cache. Windows Installer's cached `LocalPackage` is not assumed to
retain usable installation media.

### Persistent control location

The invoking user's Windows known-folder identity determines the servicing
location:

```text
%LOCALAPPDATA%\Hermes\Servicing\
  7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A\
    admission.bin
    operation.json
    packages\<package-sha256>\...
```

This directory is outside MSI removal. Interrupted uninstall must not erase its
own startup block.

The protocol coordinates cooperating processes under the same OS principal. It
is not protection against hostile same-user code and introduces no root
secret, HMAC, special ACL or machine policy.

## Shared-runtime and exclusive-servicing admission

`admission.bin` is a stable file object. Normal servicing and uninstall must
neither replace nor delete it.

Runtime opens it for read with read-sharing only. The coordinator opens it for
read/write with no sharing. Handles are non-inheritable. Windows file-sharing
enforcement supplies exclusion across that user's sessions; a process snapshot
or named mutex is not the ownership proof.

The file has a small versioned header with durable **Ready** or **Closed**
state. Operation details are stored separately. Missing, malformed or
unsupported state denies installed-runtime admission.

After acquiring exclusive admission, the coordinator completes read-only
ownership and precondition checks. It writes Closed and flushes it to disk,
then persists and flushes the operation record and retained-media references.
Only after successful durable closure may it invoke MSI. Ready may be
published only for a demonstrably safe, completed and verified outcome.

A crash during record writing leaves Closed or invalid state, not permission
to launch. Coordinator death releases the live exclusive handle but does not
remove durable closure.

### Process lifetime and loaded generation

An admitted installed runtime retains its read handle until OS process
termination, not merely until an entry method or acquiring thread returns.
The production interpreter roots that handle independently of caller scope.
Its public admission token has no disposal or release capability.

Tray and Service hold independent handles. Tray death cannot release the
Service's exclusion; lingering Service workers retain it while their process
remains alive. Process termination closes that process's kernel handle.

Apphost and runtime mapping may precede managed entry. A process reaching the
fence must therefore match the Ready installation's executing managed-entry
MVID and compatible already-loaded Hermes assemblies. The actual identities
come from loaded assembly objects, not from replacement DLLs subsequently read
from disk. An old image mapped before upgrade must not resume application work
as though it were the newly installed generation.

Failure before managed entry is possible; it does not admit application or
owner-data activity.

## Startup boundary

Every supported installed entry acquires admission before constructing
application dependencies or touching configuration, archive, logs, database or
provider state.

Tray admission precedes ordinary option parsing/application initialization and
the `--installed-smoke` branch. Its configuration/port instance lease is
separate and subordinate. An installed Tray's explicit Service-path override
must identify an enrolled, compatible Service entrypoint; its own lease is not
permission to start an ungated child.

Service admission belongs at the outermost entry, before `ServiceEntry`
dispatch. It covers foreground Service, owned `--control-stdin` execution,
reindex, the owner CLI when compiled into that executable, apphost invocation
and direct managed-assembly invocation.

The composition root supplies the production startup capability and tests
supply a fake. Both CLI and ordinary-Service dependencies require admission
before construction. Refusal is a bounded startup failure with no owner-data
effects. The outside-installation classification is separate from an admitted
installed runtime; it is not an installed-runtime bypass.

## Standard MSI contract

| Public property | Meaning |
|-----------------|---------|
| `HERMES_SERVICING_RUN` | Coordinator operation GUID; correlation, not a secret |
| `HERMES_SERVICING_ABI` | Admission protocol version |
| `HERMES_ALLOW_ROLLBACK` | Explicit downgrade permission |

Coordinator properties are secure for execute-sequence propagation.
`INSTALLFOLDER` must resolve to the fixed AppRoot. Supplying properties never
grants admission.

Each export independently verifies the live coordinator's Windows pipe-server
process instance, matching user and durable Closed operation, root, ABI,
ProductCode and action. Permission requires the coordinator to retain its
exclusive handle while the matching synchronous MSI call is outstanding.
A stale journal, copied public properties, guessed named object or
self-reported PID is insufficient.

No custom action retains ownership between invocations. If the coordinator
dies after answering a guard, durable Closed still protects startup.

For major-upgrade removal, the old cached package discovers the coordinator
through durable operation context. Its authorization is restricted to the
recorded old ProductCode and matching `UPGRADINGPRODUCTCODE`. Arbitrary custom
properties must not be assumed to propagate into `RemoveExistingProducts`.

### Sequence and unsupported operations

Both native actions are immediate, run in the user context and have checked
return values.

| Position | Action |
|----------|--------|
| After costing, before `InstallValidate` | `HermesPreflight` |
| 1500 | `InstallInitialize` |
| 1501 | `HermesTransactionFence` |
| 1502 | `RemoveExistingProducts`, when applicable |
| Standard positions | Component/file/registry/shortcut actions |
| 6600 | `InstallFinalize` |

Equivalent standard Upgrade-table authoring replaces the `MajorUpgrade`
convenience schedule so the transactional fence precedes old-product removal.
It retains all-version related-product removal, separate newer-version
detection and explicit downgrade permission. Windows Installer remains the
payload owner and rollback engine.

Both guards cover install, repair, uninstall and authorized nested removal,
not merely first installation. Disabled rollback is refused before mutation.
Administrative and advertisement execution paths also carry the preflight
refusal; a UI-sequence-only guard is insufficient. Elevated/per-machine,
patch and partial-feature servicing are unsupported.

Raw install, upgrade, repair and uninstall must refuse before application
mutation, whether the application is running or stopped. Silent raw MSI must
report failure rather than successful servicing with disabled admission.
Earlier engine errors retain their actual result. Authorized nested removal
is part of the coordinator's recorded major upgrade, not an exception for
arbitrary raw MSI sessions.

`MSIRESTARTMANAGERCONTROL=Disable` and authored/default plus
coordinator-supplied `REBOOT=ReallySuppress` are separate safeguards. Restart
suppression is not application-shutdown suppression. No application-close,
process-kill, automatic launch, `ForceReboot` or `ScheduleReboot` action is
part of servicing.

## Completion and recovery

The coordinator acquires exclusive admission without waiting for applications
to close. If a runtime holds admission, it refuses without changing admission
state, installed bytes or running processes.

After ownership, registration, fixed root and media validation, it closes
admission durably and calls `MsiInstallProductW` or
`MsiConfigureProductExW` synchronously. Completion includes ordinary Windows
Installer rollback. Verification covers product inventory, installation
binding, the complete owned-payload inventory, shortcut/Run state and relevant
outstanding deferred file operations.

| Completed outcome | Admission and result |
|-------------------|----------------------|
| Success with verified desired state | Ready for that state |
| Failure with verified restored previous state | Ready for the previous state; still report servicing failure |
| Unknown or incomplete state | Remain Closed |
| 3010 or relevant pending replacement/removal | Remain Closed; report reboot required, never ready |

No automatic application termination or reboot resolves an outcome.

### Interrupted operation

Recovery is another operation of the same coordinator, not a supervisor. It
acquires exclusive admission, retains the interrupted record, assigns a new
run ID and performs real standard MSI recovery/maintenance from retained
media.

Windows Installer execute-sequence serialization supplies the mutation
boundary. An old session already past its transactional fence must finish
mutation or rollback before recovery can pass its own execute sequence. An
old session delayed before its fence encounters the revoked run ID and must
refuse before mutation. The coordinator does not directly acquire a global
MSI mutex; unrelated installers receive ordinary Windows Installer contention.

Reopening after interruption requires a completed recovery operation through
its own transactional fence, full desired-state verification and no relevant
pending mutations. Returning 1618 is not completion. Returning 1605 for an
already-absent product is not a serialization barrier. Neither pre-execution
success, process disappearance, an empty process snapshot, elapsed time nor
byte-only comparison permits unlocking.

For intended absence with an already-absent product, a retained package may
need a normal guarded installation followed by normal guarded uninstall.
Admission remains Closed throughout. Unknown-file collision and ownership
rules still apply; unresolved ownership refuses recovery.

A durable receipt is evidence of its recorded call, not authorization to
unlock after another crash. A surviving coordinator does not require a second
recovery pass after an already verified normal success or rollback.

## Enrollment, ownership and user-data preservation

Ordinary first installation may encounter existing configuration, archives and
unknown AppRoot files. It does not require a clean CI profile or proof of an
entire installation history. It requires no conflicting installation and no
collision with new package-owned paths.

Under exclusive admission, Setup reconciles related-product inventory with
installation registration and uses registered component ownership and
retained package inventories rather than filenames alone. Ambiguous ownership,
root or path redirection refuses. A new payload path occupied by an unowned
file refuses; that file must not be adopted into an uninstall manifest.

Unknown files and directories survive. Uninstall removes only package-owned
resources and empty package directories. Configuration and archive paths are
neither MSI components nor recovery inputs.

An actually detected ungated or incompatible installation refuses in-place
servicing even when stopped. Its incompatibility is reported without
terminating it, adopting it or automatically invoking its unsafe uninstall.
A process snapshot cannot make it race-safe. This is a present-state decision,
not a claim that no legacy binary ever existed; an empty release list is not
historical proof.

## Evidence obligations

Native execution belongs on a dedicated disposable GitHub-hosted Windows
runner. Synthetic diagnostic admission restrictions do not become production
enrollment restrictions.

| Boundary | Required evidence |
|----------|-------------------|
| Ordinary first installation | Existing synthetic owner data and noncolliding unknown files survive; SDK-free installed Tray/Service operate |
| Upgrade and explicit rollback | Runtime payload bytes actually change; product/version and complete inventories agree; owner/unknown hashes remain unchanged |
| Live runtime | Coordinator and raw silent upgrade/repair/uninstall refuse; the same Tray/Service process instances survive; installed bytes remain unchanged |
| Startup/mutation interleavings | Before/after runtime acquisition and both guards, either runtime wins and servicing refuses, or startup refuses |
| Every entry route | Tray normal/smoke, direct Service, managed DLL, owned child, reindex and owner CLI cannot bypass installed admission |
| Raw MSI while stopped | Truthful failure before mutation, including repair, uninstall, administrative and advertisement paths |
| Injected normal MSI failure | Standard rollback restores the previous inventory; reopening follows verification |
| Coordinator or acquiring-thread death | Process-lifetime runtime retention and durable Closed survive as specified; no deadline-based unlocking |
| Delayed old session | Revoked pre-fence sessions refuse; already-admitted mutation serializes before completed recovery |
| Interrupted operation | Install/upgrade/rollback/repair/uninstall reach verified recovery outcome or remain Closed |
| Deferred replacement | No automatic shutdown/reboot; 3010 is never ready; recovery verifies after reboot |
| Ownership and aliases | Fixed-root overrides, collisions and detected incompatible ownership refuse without adoption/deletion; positive outside-root development still works |
| Distribution | Native exports load through MSI; real installed native proof blocks artifact/release delivery; macOS and legacy surfaces are not promoted |

## References

- [Pipeline V5 architecture](30-pipeline-v5-architecture.md)
- [Active stabilization wave](../waves/wave-v5-stabilization.md) — implementation history and validation evidence
- [Testing register](../testing-register.md) — executed results
