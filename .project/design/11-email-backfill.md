# Hermes — Email Backfill: Historical Inbox Ingestion

> Design doc for trickle-ingesting historical email into the Hermes archive.  
> Created: 2026-03-31

---

## 1. Problem

Hermes currently syncs **forward only** — `EmailSync.syncAccount` uses `provider.listNewMessages(lastSync)` which passes `after:{epoch}` to Gmail's `messages.list`. On first run it pulls everything with attachments (no `since` filter), but once a `sync_state.last_sync_at` is recorded, subsequent syncs only see new mail.

This means:

- Years of historical email with valuable attachments (invoices, statements, contracts) are never indexed
- The "find me everything about X" promise only covers mail received after Hermes was installed
- The MCP server and chat can't answer questions about historical documents
- Users who set up Hermes today get an empty archive until new mail trickles in

---

## 2. Design Goals

| Goal | Description |
|------|-------------|
| **Complete archive** | All email attachments (and optionally bodies) from configured accounts are eventually indexed |
| **Non-disruptive** | Backfill runs at low priority, doesn't starve real-time sync or overload the machine |
| **Resumable** | Survives restarts — picks up exactly where it left off |
| **Rate-limited** | Respects Gmail API quotas (250 quota units/second, ~50 messages.get/second) |
| **Opt-in with smart defaults** | Enabled by default for new accounts, configurable depth and rate |
| **Observable** | Progress visible in the shell UI — total scanned, total remaining, estimated completion |

---

## 3. How Gmail Pagination Works

The Gmail API `messages.list` returns pages of message stubs (id + threadId only):

```
GET /gmail/v1/users/me/messages?q=has:attachment&maxResults=100
→ { messages: [{id, threadId}, ...], nextPageToken: "abc123", resultSizeEstimate: 8500 }

GET /gmail/v1/users/me/messages?q=has:attachment&maxResults=100&pageToken=abc123
→ { messages: [...], nextPageToken: "def456", resultSizeEstimate: 8500 }
```

Messages are returned **newest first**. To backfill, we page through the entire result set, skipping messages already in our `messages` table. The `resultSizeEstimate` gives a rough total for progress display.

**Key property**: once we've paged to the end (no `nextPageToken`), we've seen every matching message. We record a completion marker so we never re-scan.

---

## 4. Configuration

### config.yaml

```yaml
accounts:
  - label: john-personal
    provider: gmail
    backfill:
      enabled: true               # default: true
      since: 2020-01-01           # default: null (= all history)
      batch_size: 50              # messages per sync cycle, default: 50
      attachments_only: true      # default: true — only fetch messages with attachments
      include_bodies: false       # default: false — also index email body text
```

### Domain types

```fsharp
type BackfillConfig =
    { Enabled: bool
      Since: DateTimeOffset option     // None = all history
      BatchSize: int                    // messages per sync cycle
      AttachmentsOnly: bool             // filter query to has:attachment
      IncludeBodies: bool }             // also store/index email body text
```

### Defaults

| Setting | Default | Rationale |
|---------|---------|-----------|
| `enabled` | `true` | The whole point of Hermes is a complete archive |
| `since` | `null` (all) | Don't leave gaps; user can restrict if inbox is huge |
| `batch_size` | `50` | Conservative; keeps each cycle under 5 seconds of API calls |
| `attachments_only` | `true` | Vast majority of value is in PDF/doc attachments |
| `include_bodies` | `false` | Body indexing is Phase 10 — works independently once messages are in DB |

---

## 5. Sync State Extension

The existing `sync_state` table tracks forward sync. Backfill needs additional columns:

```sql
ALTER TABLE sync_state ADD COLUMN backfill_page_token TEXT;
ALTER TABLE sync_state ADD COLUMN backfill_total_estimate INTEGER;
ALTER TABLE sync_state ADD COLUMN backfill_scanned INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sync_state ADD COLUMN backfill_completed INTEGER NOT NULL DEFAULT 0;
ALTER TABLE sync_state ADD COLUMN backfill_started_at TEXT;
```

| Column | Purpose |
|--------|---------|
| `backfill_page_token` | Gmail `nextPageToken` — resume point. `NULL` = not started, empty string = fully complete |
| `backfill_total_estimate` | `resultSizeEstimate` from first page — rough total for progress bar |
| `backfill_scanned` | Number of message stubs examined (including skipped) |
| `backfill_completed` | `1` when backfill has paged to the end — never re-scan |
| `backfill_started_at` | When backfill began — useful for logging and UI |

### Schema migration

This is schema version 3. The migration runs in `Database.initSchema` after the version check:

```fsharp
let private migrateV2toV3 (db: Database) =
    task {
        let! _ = db.execNonQuery "ALTER TABLE sync_state ADD COLUMN backfill_page_token TEXT" []
        let! _ = db.execNonQuery "ALTER TABLE sync_state ADD COLUMN backfill_total_estimate INTEGER" []
        let! _ = db.execNonQuery "ALTER TABLE sync_state ADD COLUMN backfill_scanned INTEGER NOT NULL DEFAULT 0" []
        let! _ = db.execNonQuery "ALTER TABLE sync_state ADD COLUMN backfill_completed INTEGER NOT NULL DEFAULT 0" []
        let! _ = db.execNonQuery "ALTER TABLE sync_state ADD COLUMN backfill_started_at TEXT" []
        let! _ = db.execNonQuery "INSERT OR REPLACE INTO schema_version (version) VALUES (3)" []
        return ()
    }
```

---

## 6. Algebra Extension

The `EmailProvider` algebra needs a new function for paginated listing:

```fsharp
type EmailProvider =
    { listNewMessages: DateTimeOffset option -> Task<EmailMessage list>     // existing
      getAttachments: string -> Task<EmailAttachment list>                   // existing
      getMessageBody: string -> Task<string option>                          // existing
      listMessagePage: string option -> string option -> int                 // new
          -> Task<MessagePage> }

type MessagePage =
    { Messages: EmailMessage list
      NextPageToken: string option
      ResultSizeEstimate: int64 }
```

`listMessagePage` takes:
1. `pageToken: string option` — `None` for first page, `Some token` to continue
2. `query: string option` — Gmail search query (e.g. `"has:attachment after:1577836800"`)
3. `maxResults: int` — page size (max 500, we use 100)

Returns a `MessagePage` with the batch of messages, the next page token (None if last page), and the total estimate.

### GmailProvider implementation

```fsharp
let listPage (pageToken: string option) (query: string option) (maxResults: int) : Task<MessagePage> =
    task {
        let req = service.Users.Messages.List("me")
        req.MaxResults <- int64 maxResults |> Nullable

        match query with
        | Some q -> req.Q <- q
        | None -> ()

        match pageToken with
        | Some pt -> req.PageToken <- pt
        | None -> ()

        let! response = req.ExecuteAsync()

        if response.Messages = null || response.Messages.Count = 0 then
            return { Messages = []; NextPageToken = None; ResultSizeEstimate = 0L }
        else
            let! messages =
                response.Messages
                |> Seq.map (fun stub -> fetchFullMessage stub.Id)
                |> Task.WhenAll

            return
                { Messages = messages |> Array.toList
                  NextPageToken = response.NextPageToken |> Option.ofObj
                  ResultSizeEstimate = response.ResultSizeEstimate |> Option.ofNullable |> Option.defaultValue 0L }
    }
```

---

## 7. Backfill Algorithm

The backfill runs as a second pass **after** the normal forward sync in each sync cycle:

```
runSyncCycle:
  1. Forward sync (existing — new messages via lastSync)
  2. Watch folder scan (existing)
  3. Classify unclassified (existing)
  4. ── BACKFILL PASS ──  (new)
  5. Extract backlog (existing)
  6. Embed backlog (existing)
```

### Backfill pass pseudocode

```
for each account where backfill.enabled and not backfill_completed:
    
    load backfill state from sync_state
    
    build query:
        q = "has:attachment" if attachments_only else ""
        if since: q += " after:{unix_timestamp}"
    
    page_token = backfill_page_token (None if first run)
    remaining_budget = batch_size
    
    while remaining_budget > 0 and page_token is not "":
        fetch page(page_token, query, min(100, remaining_budget))
        
        if first page and total_estimate > 0:
            save backfill_total_estimate
        
        for each message in page:
            backfill_scanned += 1
            
            if message already in DB:
                skip (already processed by forward sync or previous backfill)
                continue
            
            record message to DB
            
            if has attachments:
                download, dedup, save to unclassified/
                (same logic as syncAccount)
            
            if include_bodies and body not stored:
                fetch and store body text
            
            remaining_budget -= 1
        
        page_token = next_page_token
        save backfill_page_token, backfill_scanned to sync_state
    
    if page_token is None:
        mark backfill_completed = 1
        log "Backfill complete for {account}"
```

### Key behaviours

1. **Budget-limited**: Each sync cycle processes at most `batch_size` *new* messages (not counting skips). This caps the API calls and I/O per cycle.

2. **Skip-efficient**: `messageExists` is a cheap primary key lookup. If forward sync already processed a message, backfill skips it instantly. In practice, the first few pages (newest messages) will be mostly skips; older pages are all new.

3. **Resume via page token**: Gmail page tokens are opaque but stable. If Hermes restarts, it resumes from the saved `backfill_page_token`. Worst case it re-skips a few messages on the last page.

4. **Completion is permanent**: Once `backfill_completed = 1`, we never re-scan. New mail is handled by forward sync. If the user changes `since` to an earlier date, they must reset backfill (see section 10).

5. **Interleaved with forward sync**: Forward sync runs first (quick, handles new mail), then backfill processes its batch. The full pipeline (classify, extract, embed) runs once after both passes, processing whatever landed in `unclassified/`.

---

## 8. Gmail API Quota Budget

Gmail API quota is **250 units/second** (default). Key costs:

| API call | Units | Per-message |
|----------|-------|-------------|
| `messages.list` | 5 | Once per page (100 messages) |
| `messages.get` (full) | 5 | Once per message |
| `messages.attachments.get` | 5 | Once per attachment |

For a batch of 50 messages with 1.5 attachments average:

```
List pages:   1 × 5  =    5 units
Get messages: 50 × 5  =  250 units
Get attachments: 75 × 5 = 375 units
Total:                    630 units (~3 seconds of quota)
```

With a 15-minute sync interval, that's 630 units per 15 minutes = **0.7 units/second** — well under the 250/second limit. Even with `batch_size: 200`, we'd use ~2.8 units/second.

For large accounts (100K+ messages), full backfill at `batch_size: 50` every 15 minutes:
- 50 messages per cycle × 4 cycles/hour × 24 hours = **4,800 messages/day**
- 100K messages ÷ 4,800/day = **~21 days** to complete

With `batch_size: 200` and `sync_interval_minutes: 5`:
- 200 × 12 cycles/hour × 24 = **57,600 messages/day**
- 100K ÷ 57,600 = **~1.7 days**

---

## 9. UI Integration — Full Specification

Every UI element described here must be **built and wired** as part of the feature. XAML-only is not done. See copilot-instructions.md "UI integration: definition of done".

### 9.1 Settings Dialog — Expanded

The current Settings dialog has 3 fields. It becomes a tabbed/sectioned dialog:

```
┌─────────────────────────────────────────────────┐
│  Hermes Settings                           [×]  │
├─────────────────────────────────────────────────┤
│                                                  │
│  ── General ──────────────────────────────────── │
│  Sync interval (minutes):  [15     ▼]           │
│  Min attachment size (KB): [20     ▼]           │
│                                                  │
│  ── AI / Chat ────────────────────────────────── │
│  Chat provider:  (●) Ollama  ( ) Azure OpenAI   │
│                                                  │
│  Ollama URL:     [http://localhost:11434      ]  │
│  Ollama model:   [llama3.2                   ]  │
│                                                  │
│  Azure endpoint: [                           ]  │
│  Azure API key:  [••••••••••                 ]  │
│  Deployment:     [gpt-4o-mini                ]  │
│                                                  │
│  ── Accounts ─────────────────────────────────── │
│  ┌──────────────────────────────────────────┐   │
│  │ 📧 john-personal          [Re-auth] [×] │   │
│  │    Backfill: ●On  since 2020-01-01       │   │
│  │    Batch size: [50]                       │   │
│  ├──────────────────────────────────────────┤   │
│  │ 📧 john-work              [Re-auth] [×] │   │
│  │    Backfill: ○Off                        │   │
│  └──────────────────────────────────────────┘   │
│  [+ Add Gmail Account]                          │
│                                                  │
│  ── Watch Folders ────────────────────────────── │
│  ┌──────────────────────────────────────────┐   │
│  │ 📁 ~/Downloads    *.pdf         [×]      │   │
│  │ 📁 ~/Desktop      *.pdf,*.png   [×]      │   │
│  └──────────────────────────────────────────┘   │
│  [+ Add Folder]                                  │
│                                                  │
│              [Save]                              │
└─────────────────────────────────────────────────┘
```

**Wiring requirements:**
- Chat provider radio buttons toggle visibility of Ollama vs Azure OpenAI fields
- Azure API key field uses `PasswordChar="●"` — never displayed in plain text
- Save writes all settings to `config.yaml` via `HermesServiceBridge.UpdateFullConfigAsync` (new method — replaces the current 3-field `UpdateConfigAsync`)
- Per-account backfill toggle writes to the account's `backfill.enabled` in config
- Re-auth button opens the same OAuth flow as "Add Account" but for an existing label
- Remove account (×) confirms then removes from config + optionally deletes token

**Bridge method specification:**

```csharp
/// Writes the complete config to config.yaml using YamlDotNet serialisation.
/// Replaces the current UpdateConfigAsync which only handles 3 fields.
public async Task UpdateFullConfigAsync(
    int syncIntervalMinutes,
    int minAttachmentSizeKb,
    string chatProvider,            // "ollama" or "azure-openai"
    string ollamaUrl,
    string ollamaModel,
    string? azureEndpoint,
    string? azureApiKey,
    string? azureDeployment);

/// Removes an account from config.yaml and optionally deletes its OAuth token.
public async Task RemoveAccountFromConfigAsync(string label, bool deleteToken);

/// Re-authenticates an existing account (same OAuth flow, existing label).
public async Task ReAuthAccountAsync(string label);
```

### 9.2 Account Card — Sidebar

Each account in the EMAIL ACCOUNTS expander shows backfill progress:

```
▾ EMAIL ACCOUNTS
  ● john-personal                    ● Synced
    423 emails · 2 min ago
    Backfill ████████░░ 6,340 / ~8,500

  ● john-work                        ● Synced
    187 emails · 14 min ago
    Backfill: complete ✓

  [+ Add Account]
```

**Wiring requirements:**
- `Stats.getBackfillProgress` called on the same refresh timer as other stats
- `ProgressBar` for backfill (same style as extracted/embedded bars)
- Text shows `{scanned:N0} / ~{estimate:N0}` during backfill, `complete ✓` when done
- Hidden entirely when backfill is not enabled for that account

### 9.3 Status Bar

When backfill is actively running in the current sync cycle:
```
●● Ready · 1,234 docs · Backfilling john-personal (74%)...
```

**Wiring:**
- `ShellViewModel.StatusBarText` already drives the status bar
- Extend `RefreshAsync` to include backfill state in the status bar text

### 9.4 First-Run Notification

When a new account is added and backfill is enabled, show an info message in the chat panel:

```
Hermes: I'm scanning your email history for john-personal.
This runs in the background — about 4,800 messages per day.
You'll start seeing historical documents in search results as they're indexed.
```

**Wiring:**
- `ShellViewModel.AddGmailAccountAsync` (or post-add hook) pushes a ChatMessage

---

## 10. Controls and Reset

### Pause/resume backfill

The per-account `backfill.enabled` flag in config controls this. Setting `enabled: false` stops backfill immediately (next cycle skips it). Setting it back to `true` resumes from the saved page token.

### Reset backfill

If a user wants to re-scan (e.g. changed `since` date):

```yaml
# CLI command
hermes backfill reset --account john-personal
```

This clears `backfill_page_token`, `backfill_scanned`, and `backfill_completed` in `sync_state` for that account. Next sync cycle starts backfill from scratch.

Implementation: a simple `Database` function that zeroes the columns.

### First-run behaviour

On first run (no `sync_state` row for an account):
- Forward sync runs with `lastSync = None`, which fetches recent messages (Gmail returns ~100 newest by default)
- Backfill starts from page 1, working through the full history
- The first few pages overlap with what forward sync already grabbed — skips are cheap

This means the user sees recent documents immediately, and historical documents trickle in over the following days/weeks.

---

## 11. Implementation Phases — Silver Thread

Each phase delivers **verifiable end-to-end functionality**. No orphaned backend without a UI surface. No XAML without wiring. Each phase's "proof" column says how you know it works.

### Phase B1: Config + Settings UI for Chat Provider

The expanded Settings dialog, starting with the AI/Chat section.

| Layer | What |
|-------|------|
| **Domain.fs** | `BackfillConfig` type (already designed in section 4) |
| **Config.fs** | `BackfillDto`, `ChatDto` parsing — including per-account `backfill:` section |
| **HermesServiceBridge.cs** | `UpdateFullConfigAsync(...)` — writes entire config.yaml, not just 3 fields. Takes: sync interval, min size, chat provider, ollama settings, azure openai settings. Uses YamlDotNet serialiser. |
| **ShellWindow.axaml.cs** | Settings dialog rebuilt: General section + AI/Chat section with radio toggle (Ollama / Azure OpenAI), conditional field visibility |
| **Tests** | Config round-trip: parse YAML with `chat:` and `backfill:` → `toConfig` → verify all fields |
| **Proof** | Open Settings, switch to Azure OpenAI, fill endpoint + key, Save → reopen Settings → fields persist. Chat uses Azure OpenAI for next query. |

### Phase B2: Account Management UI

Accounts section in Settings dialog — add, re-auth, remove, backfill toggle.

| Layer | What |
|-------|------|
| **HermesServiceBridge.cs** | `RemoveAccountFromConfigAsync(label)` — removes account entry from YAML, optionally deletes token file. `ReAuthAccountAsync(label)` — same OAuth flow as add, for existing label. |
| **ShellWindow.axaml.cs** | Settings dialog Accounts section: list of accounts with [Re-auth] [×] buttons. Per-account backfill toggle (on/off), since date picker, batch size spinner. [+ Add Gmail Account] at bottom (existing behaviour, moved here). |
| **Tests** | Config round-trip with accounts + backfill sections |
| **Proof** | Open Settings → see accounts list → toggle backfill on → set since date → Save → reopen → persisted. Remove account → confirm dialog → gone from list. |

### Phase B3: Schema Migration + Backfill Engine

The backend engine — but immediately surfaced in the account card.

| Layer | What |
|-------|------|
| **Database.fs** | Schema v2→v3 migration: backfill columns on `sync_state` |
| **Algebra.fs** | `listMessagePage` added to `EmailProvider` |
| **GmailProvider.fs** | `listMessagePage` implementation |
| **EmailSync.fs** | `backfillAccount` function — core logic from section 7 |
| **Stats.fs** | `getBackfillProgress` — queries `sync_state` for backfill_scanned, backfill_total_estimate, backfill_completed |
| **ServiceHost.fs** | Backfill pass wired after forward sync (step 4 in runSyncCycle) |
| **ShellViewModel.cs** | `BackfillProgress` per account in `RefreshAsync` |
| **ShellWindow.axaml + .cs** | Account card gains ProgressBar + text for backfill progress |
| **Tests** | `backfillAccount` with fake provider: processes N messages, saves page token, resumes, completes. `getBackfillProgress` returns correct counts. |
| **Proof** | Add a Gmail account with backfill enabled → run app → account card shows "Backfill: scanning..." with progress bar updating on refresh timer. Status bar shows "Backfilling {account} (N%)..." |

### Phase B4: First-Run + CLI Reset

Polish: first-run notification and backfill reset.

| Layer | What |
|-------|------|
| **ShellViewModel.cs** | After AddGmailAccount completes, push a ChatMessage explaining backfill timeline |
| **Program.fs (CLI)** | `hermes backfill reset --account {label}` — clears backfill columns in sync_state |
| **Proof** | Add new account → chat shows "I'm scanning your email history..." message. CLI `hermes backfill reset --account test` → backfill restarts from page 1 next sync cycle |

### Summary: B1→B2→B3→B4, each delivers a working feature

```
B1: Settings dialog expanded → user can configure chat provider end-to-end
B2: Account management in UI → user can add/remove/configure accounts end-to-end
B3: Backfill engine + progress UI → user sees historical emails appearing with live progress
B4: First-run experience + reset → user gets guidance and control
```

### Definition of done (every phase)

Per `.github/copilot-instructions.md`:
1. XAML exists with all controls laid out
2. Code-behind wired — every named control has event handlers
3. Buttons do something — no dead controls
4. Data is live — reads from DB/config, not placeholder text
5. Build clean — 0 errors, 0 warnings
6. Tests pass — new tests for new logic
7. Smoke tested — `dotnet run --project src/Hermes.App`, verify the specific proof for that phase

---

## 12. What This Doesn't Change

- **Forward sync is untouched** — `syncAccount` continues to work exactly as it does today
- **Pipeline is untouched** — backfill drops files into `unclassified/` just like forward sync; the classify → extract → embed pipeline doesn't know or care about the source
- **Dedup is reused** — `hashExists` and `messageExists` prevent duplicate processing
- **OAuth scope stays `gmail.readonly`** — backfill only reads, never modifies mail

---

## 13. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should backfill be enabled by default for new accounts? | Yes — the value of Hermes is proportional to archive completeness |
| 2 | Should there be a global backfill kill switch vs per-account? | Both — global `backfill.enabled` in root config + per-account override |
| 3 | What if Gmail API returns an error mid-backfill? | Save progress, retry next cycle. Log the error. Don't reset the page token |
| 4 | Should we show a first-run "scanning your email history" notification? | Yes — set expectations about timeline and explain what's happening |
| 5 | Should `batch_size` be dynamically adjusted based on available quota? | Not now — fixed size is simpler and predictable |
| 6 | Should backfill process messages without attachments for body-only indexing? | When `attachments_only: false` and `include_bodies: true`, yes — but this is a lot of messages for little value in v1 |
| 7 | Should we provide an estimate of completion time in the UI? | Yes — `(total_estimate - scanned) / batch_size × sync_interval` gives a rough ETA |
