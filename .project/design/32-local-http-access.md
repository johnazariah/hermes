# Local HTTP Access

## Purpose and trust boundary

Hermes permits useful local archive reads without a login, but configuration
reads, mutations and interactive model calls require explicit owner-granted
authority. Host and Origin validation establish reachability, not identity.
The MCP boundary remains independent.

The authority chain is:

```text
OS-owner-validated local pipe
  -> approval of one immutable browser pairing
    -> verifier-bound exchange
      -> short-lived, scoped HTTP bearer held only in browser memory
```

Trusted components are the selected local OS account, the installed Hermes
runtime and its canonical React bundle. Foreign web pages, unauthenticated
HTTP callers, other OS accounts and stale/replaced service instances are not
trusted. Arbitrary hostile software already running as the owner is outside
this boundary. Anonymous archive access is deliberately not a confidentiality
boundary against other local HTTP callers.

There is no owner credential file, reusable environment/stdin secret, HTTP
owner endpoint, cookie session or authentication-disable fallback.

## Identities and origins

Three values must remain distinct:

| Value | Definition |
|-------|------------|
| API audience `A` | Exactly `http://127.0.0.1:<selected-port>` |
| Browser origin `U` | The single actual Origin recorded when creating a pairing |
| Service identity | `{instanceId,processId,processStartTimeUtc,origin:A,configKey}` |

`instanceId` is fresh per process. PID and UTC process-start time come from
the actual process. Public HTTP identity becomes authoritative only when
matched to the owner-validated pipe; Tray additionally compares its actual
owned process handle and restart generation.

Pairing admits the selected port's numeric, localhost and IPv6 loopback
service origins, plus explicitly configured `ExtraOrigins`. Do not inherit
other default service ports merely because the outer boundary admits them.
Reject missing, null, duplicate and unlisted pairing Origins. Admission
creates a pending request, not authority.

Pairings and sessions retain immutable `browserOrigin:U` and `apiOrigin:A`.
Exchange requires the recorded `U`. Session requests permit absent Origin
or exactly `U`, after the existing Host/Origin boundary checks. Owner
confirmation displays both values. An explicitly trusted preview proxy may
carry relative `/api/` requests; its transport origin is not the API audience.

## Owner-control transport

### Naming and principal validation

Derive the target from configuration-directory and port arguments without
opening configuration files:

1. Require an absolute directory and apply `Path.GetFullPath`.
2. Remove trailing separators without changing a filesystem root.
3. Uppercase invariantly on Windows; preserve case on Unix.
4. Hash UTF-8 `hermes-owner-control-v1\n<directory>\n<decimal-port>\n`
   with SHA-256. `configKey` is the full lowercase hexadecimal result.
5. Use pipe name `hmop1-` plus its first 32 hexadecimal characters.

This is lexical identity, not symlink resolution. Aliases or differing
temporary-directory namespaces may fail closed; do not scan other pipes
or archives to guess the intended instance.

Both .NET pipe ends use `PipeOptions.Asynchronous | CurrentUserOnly`,
byte mode, non-inheritable handles and cancellation-aware operations. The
client's server name is `"."`, with anonymous impersonation level. The
server has one persistent instance and serializes accepted exchanges.

.NET 9 validates peer effective UID on Unix. Windows constructs the server
security descriptor from the current identity's Owner SID; the client
compares that SID. This supports the declared non-elevated per-user Windows
and foreground macOS contexts. Do not assume elevated and unelevated
contexts interoperate.

The Unix runtime normally unlinks the socket pathname before binding.
Retain that standard stale-path behaviour; do not add Unix
`FirstPipeInstance`, manual deletion or custom permission repair. Do not
claim universal namespace-collision refusal. Owner validation prevents
foreign authority; namespace denial of service remains possible. Windows
retains the runtime's first-instance behaviour for a one-instance pipe.
An exclusively bound numeric HTTP listener prevents two cooperating
Hermes services from owning the same port.

The API proves an OS principal, not the executable identity or a portable
kernel-attested peer PID. A JSON PID is an assertion from an owner-validated
peer, checked for instance consistency rather than promoted into a stronger
OS guarantee.

### Framing and resource bounds

Each connection carries one request and one response: four-byte unsigned
big-endian length followed by exactly that many UTF-8 JSON bytes.

- Length is 1-8,192 bytes, checked before allocation; JSON depth is at most 8.
- Reject BOM, invalid UTF-8, comments, trailing JSON, duplicate/unknown
  properties and unknown operations. Field names are case-sensitive.
- No compression, pipelining, message-mode dependency or `WaitForPipeDrain`.
  Further frames never dispatch another operation.
- Client connection budget is two seconds; an accepted exchange has five
  seconds total, with one second for its header and at most two for payload.
  Response writing uses the remaining budget.
- Observe shutdown and request cancellation. Dispose an interrupted
  connection when necessary, await its work and leave no losing timeout tasks.
- Never hold a connection while waiting for human confirmation.

Recoverable rejected peers do not kill acceptance. Unsupported ownership
validation or a terminal listener failure closes admission and shuts down
the service. Do not switch to an unauthenticated transport.

### Exact pipe wire

```text
Request:
{version,requestId,expectedInstanceId,operation,payload}

Response:
{version,requestId,instance,ok,result,error}
```

`version` is integer `1`; `requestId` is a fresh canonical 128-bit base64url
identifier. `instance` is the complete service identity above. Success has
non-null `result` and null `error`; failure has null `result` and fixed
`error:{code,message}`. No reply contains a verifier, bearer or configuration.

| Operation | Payload | Result |
|-----------|---------|--------|
| `status` | `{}` | `{phase,capabilities}` |
| `resolve` | `{userCode}` | `{pairingId,userCode,challenge,browserOrigin,apiOrigin,requestedCapabilities,createdAt,expiresAt,state}` |
| `approve` | `{pairingId,userCode,challenge,capabilities}` | `{pairingId,state:"approved",capabilities,expiresAt}` |

Only `status` permits null `expectedInstanceId`; all other operations require
the pinned instance. Status phase is `starting`, `ready` or `stopping`.
Resolve returns only live, unconsumed pairings. Approve requires exact
ID/code/challenge/origin binding and a grant contained in the immutable
request and server policy. Identical reapproval is idempotent without
extending expiry; conflicting approval fails.

Pipe application codes are `invalid_request`, `unsupported_version`,
`wrong_instance`, `instance_not_ready`, `pairing_not_found`,
`pairing_expired`, `pairing_consumed`, `pairing_conflict`,
`insufficient_capability`, `access_capacity_reached` and `internal_error`.
Connection rejection, corruption and timeout remain transport failures.

Every client validates wire version, correlation, full config key, selected
origin, instance and process identity. Replacement instances require fresh
discovery and confirmation; clients never silently follow them.

## HTTP session protocol

### Common representation

Use strict UTF-8 JSON with exact camel-case fields, integer version `1`,
depth at most 8 and duplicate/unknown-property rejection. Identifiers and
proofs use canonical unpadded base64url.

| Value | Representation |
|-------|----------------|
| Instance and pairing IDs | 128 random bits, 22 characters |
| Browser verifier | 256 random bits, 43 characters |
| S256 challenge | Base64url SHA-256 of the verifier's UTF-8 text |
| Session credential | `h1.<instanceId>.<256-bit-random-secret>` |
| Display code | 12 random Crockford-base32 characters, `XXXX-XXXX-XXXX` |

Display codes and pairing IDs do not approve or redeem grants. The server
retains credential digests, not reusable bearer strings, and uses fixed-time
comparisons for fixed-size cryptographic proofs.

`archive.read` is implicit. Requested and granted authority arrays contain
distinct recognised values in ordinal order: `config.read`, `model.invoke`,
`mutate`. Effective status additionally includes `archive.read`.

### Endpoints and exact fields

```text
GET /api/access -> 200
{version,instanceId,processId,processStartTimeUtc,origin,
 state,capabilities,expiresAt,pairingMethod:"ownerApproval"}

POST /api/access/pairings
request: {version,instanceId,challenge,requestedCapabilities}
201:
{version,instanceId,pairingId,userCode,browserOrigin,apiOrigin,
 requestedCapabilities,expiresAt,status:"pending"}

POST /api/access/pairings/{pairingId}/exchange
request: {version,instanceId,verifier}
202:
{version,instanceId,status:"pending",expiresAt}
201:
{version,instanceId,tokenType:"Bearer",accessToken,capabilities,expiresAt}

DELETE /api/access/session -> 204
```

Anonymous status has `state:"anonymous"` and null `expiresAt`; authenticated
status has `state:"authenticated"`. Present but invalid Authorization returns
an error, not anonymous success. No endpoint redirects.

Creating a pairing freezes its challenge, requested capabilities, origins and
instance binding. Approval grants no session to CLI or Tray. Exchange proves
the browser verifier, consumes approval atomically and issues exactly one
credential. A lost successful reply requires a new pairing; never resurrect
the consumed grant. Pending replies include `Retry-After: 2`.

Delete requires the supplied valid session and revokes it. A repeated request
with that revoked credential receives the normal credential error.

### Lifetimes and capacity

Use monotonic elapsed time for validity and UTC only for presented timestamps.
Clock rollback, polling and reapproval cannot extend a grant.

| Resource | Bound |
|----------|-------|
| Pending pairing | 180 seconds |
| Approved exchange | Earlier of original expiry or 60 seconds after approval |
| Session | 30 minutes absolute; no refresh or sliding extension |
| Live pairings / sessions | 32 / 128 |
| Protocol body / Authorization value | 4 KiB / 1 KiB |
| Pair creation | 30 per minute per instance |
| Valid pending polling | Once per two seconds per pairing |
| Invalid exchange attempts | 120 per minute per instance |
| Recent consumed/expired tombstones | 256 entries, at most 180 seconds |

Prune expired state. Reject capacity exhaustion without evicting valid
sessions. Bearers remain replayable during their lifetime; this is not
per-request proof-of-possession authentication.

### Authorization and errors

Require one Authorization value, a case-insensitive `Bearer` scheme, exactly
one ASCII separator space and a case-sensitive canonical token. Reject
duplicates, comma combinations, contaminated whitespace and malformed
lengths/encoding. Do not trim credentials into validity. Cookies, URLs and
request bodies never supply session authority.

Authentication errors use
`{error,message,instanceId,requiredCapabilities}` with fixed safe text:

| Status | Codes |
|--------|-------|
| 401 | `authentication_required`, `invalid_authorization`, `invalid_credential`, `credential_expired`, `wrong_instance`, `invalid_pairing_proof` |
| 403 | `insufficient_capability`, `session_origin_mismatch` |
| 404 | `pairing_not_found` |
| 409 | `pairing_consumed` |
| 410 | `pairing_expired` |
| 400 / 413 / 415 | `invalid_request` / `request_too_large` / `unsupported_media_type` |
| 429 | `access_rate_limited`, `access_capacity_reached` |
| 503 | `access_unavailable` |

Session 401 replies include `WWW-Authenticate: Bearer realm="Hermes"`; invalid
tokens may add `error="invalid_token"`. All access-protocol, protected and
authentication-error replies, including authenticated SSE, use
`Cache-Control: no-store`. Preserve search CORS behaviour and expose
`Retry-After` and `WWW-Authenticate` where required. Never log credentials,
verifiers, challenges or request bodies; foreground display of the nonsecret
pairing code and permission summary is intentional.

## Route policy and lifecycle

Each application endpoint has exactly one explicit classification, validated
at startup: anonymous read, required capabilities, parsed chat/search,
access-protocol operation or independent MCP. Include health, DAG and MCP
registrations outside `ApiServer`; do not authorise by path-prefix matching.

| Surface | Authority |
|---------|-----------|
| Canonical pages/assets and health | Anonymous |
| Archive documents/content/files/comprehension, categories, stats, pipeline/DAG, reminders, activity, dead letters, corrections, tags, learned patterns, suggestions and reflow status | Implicit `archive.read` |
| Settings, preferences and sync-account GETs | `config.read` |
| All archive/configuration/queue mutations, including reflow dry-run | `mutate` |
| Keyword search and explicit `aiEnabled:false` chat | Implicit `archive.read` |
| Semantic/hybrid HTTP encoder calls and explicit `aiEnabled:true` chat | `model.invoke` |
| MCP | Its existing independent policy; never interpret HTTP session credentials as MCP authority |

Parse chat/search once into bounded typed requests shared by classification
and execution. Chat explicitly supplies Boolean `aiEnabled`. Anonymous
search-chat makes zero provider calls. Missing AI capability fails before
SSE/provider work; an unavailable enabled provider returns a truthful
pre-stream error rather than silently becoming search-only.

Order is LocalBoundary, security headers, routing, HTTP authority, canonical
static dispatch, endpoint execution. Framework 404/405 endpoints dispatch
no application handler and remain truthful terminal errors, never SPA HTML.
Do not invent anonymous OAuth callback exceptions for nonexistent routes.

Startup parses owner commands first, validates routes/policy/bundle and
initialises resources without starting producers. It binds the exclusive
HTTP listener with admission closed, creates and supervises owner control,
then atomically enters ready before health succeeds or workers start.
Before ready, ordinary API work returns `503 access_unavailable` without
provider, archive, database-query or queue work. Startup failure unwinds
resources; terminal control failure closes admission, revokes sessions and
pairings, and requests shutdown. Shutdown cancels and awaits all control work.

Authority is checked immediately before handler admission. Already expired
or revoked sessions cannot admit work; accepted work may complete later.
Revocation does not pretend to roll back accepted sync/reflow operations.
Restart revokes all authority and unfinished pairings.

## Foreground and browser journeys

`open-ui [--config-dir ABS] [--port N]` and
`pair-ui CODE [--config-dir ABS] [--port N]` branch before YAML, providers,
schema, archive and pipeline startup. They select one target, validate the
owner-pipe identity against public HTTP identity, resolve the displayed code,
show `U`, `A` and permissions, and explicitly confirm before approving the
exact pinned request. No connection stays open during user input. Failure
does not start another service, scan endpoints or change archives.

React owns one in-memory access state scoped to `{U,A,instanceId}`:
checking, anonymous, pairing, authenticated, expired or unavailable.
Reload intentionally locks an ordinary tab. Bearer/verifier values never
enter storage, URLs, query keys or persisted caches.

One shared helper covers every API caller, including SSE/direct fetches.
It admits only relative `/api/` transport through the current trusted UI
origin or the explicitly selected API target. It rejects MCP, provider and
external targets, redirects and caller Authorization overrides, and uses
`credentials:"omit"`. Audience identity never authorises forwarding tokens
to an arbitrary transport.

Protected GETs are disabled while anonymous; denial is not "unconfigured"
and cannot trigger onboarding. Locking clears sensitive queries/editors,
cancels pending protected reads and ignores obsolete-generation results.
401 clears authority; capability 403 preserves it and explains the missing
permission. Rate/network errors are neither logout nor success. Never replay
mutations automatically.

Anonymous browsing and search-only chat remain useful. Protected controls
visibly require unlocking. Settings, onboarding, sync, batch actions,
dead-letter and suggestion controls report actual backend outcomes.
Document tags use the server contract `{tags:[tag]}` and verify persistence.

## Native navigation and pairing

Tray's pipe client uses the same name/wire golden vectors without taking a
dependency on the whole Core assembly. Its owned identity includes start
time from the launched process handle and the supervisor restart generation.

Every top-level navigation start invalidates the previous navigation ID and
cancels pairing work. After successful current canonical navigation, the
host sends a fresh public 128-bit navigation identifier:

```text
{version:1,type:"hermes.access.ready",navigationId,instanceId}
```

The page echoes it in exactly these messages:

```text
{version:1,type:"hermes.access.pairing",navigationId,
 instanceId,pairingId,userCode,challenge}

{version:1,type:"hermes.access.status",navigationId,
 instanceId,state:"authenticated",capabilities,expiresAt}
```

The status follows successful verifier exchange, not navigation or approval.
No verifier or bearer crosses the bridge.

Accept only current top-level source/navigation/instance/owned-generation
messages. Resolve must match the notice ID/code/challenge and canonical UI
origin; approve only the expected UI permission request. Recheck owned
identity before and after asynchronous work. Late messages cannot revive
old authority. Expiry/failure is visibly unpaired with Reconnect; a pairing
failure is not misreported as a WebView failure. Browser fallback opens an
ordinary URL, exports no credential and requires foreground pairing.

## Evidence and implementation boundaries

F# owns the bounded authority domain, strict wire codec, owner pipe
interpreters, HTTP policy, supervised lifecycle and early CLI. JS owns
memory-only access, shared transport, real protected controls and native
messages. C# owns the thin owner client and owned-navigation adapter.
Existing HTTP fixtures gain explicit synthetic session authority separate
from MCP; tests never disable production policy to make mutations pass.

Required evidence includes actual Windows/macOS same-owner connection,
disconnect, clean/crash restart and cancellation; golden codec/name vectors;
expiry/race/capacity and negative route tests; actual browser actions; and
owned Windows WebView authentication plus a real protected request/result.
Record the deployed runtime patch. API documentation/runtime source establish
the standard different-user primitive; absent actual cross-user/elevation
testing is stated plainly, not replaced with a mandatory privileged harness.

Relevant .NET 9 source: [Unix server](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Unix.cs),
[Unix client](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.Unix.cs),
[Windows server](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeServerStream.Windows.cs),
[Windows client](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/NamedPipeClientStream.Windows.cs)
and [Unix pipe paths](https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/System.IO.Pipes/src/System/IO/Pipes/PipeStream.Unix.cs).
