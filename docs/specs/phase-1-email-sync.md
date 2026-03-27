# Phase 1: Email Sync → `unclassified/`

**Status**: Not Started  
**Depends On**: Phase 0 (Project Skeleton)  
**Deliverable**: `hermes sync` downloads new attachments to `unclassified/`. `hermes accounts` shows sync status.

---

## Objective

Connect to Gmail accounts via OAuth2, incrementally list messages, download attachments, and drop them into the `unclassified/` folder with provenance metadata recorded in the database.

---

## Tasks

### 1.1 — Gmail OAuth2 Authentication
- [ ] `hermes auth <label>` opens the system browser for Google OAuth2 consent
- [ ] OAuth2 desktop-app flow using `Google.Apis.Auth` NuGet
- [ ] Scope: `gmail.readonly` only — Hermes never modifies email
- [ ] Shared `credentials.json` (OAuth client ID), per-account token files in `{config_dir}/tokens/gmail_{label}.json`
- [ ] Token refresh on expiry (automatic via Google auth library)
- [ ] `hermes accounts` lists configured accounts with: label, email address, token status (valid/expired/missing), last sync time, message count

### 1.2 — IEmailProvider Abstraction
- [ ] Define `IEmailProvider` interface in F#:
  ```fsharp
  type EmailMessage = {
      ProviderId: string       // e.g. Gmail message ID
      ThreadId: string option
      Sender: string
      Subject: string
      Date: DateTimeOffset
      Labels: string list
      HasAttachments: bool
  }

  type EmailAttachment = {
      FileName: string
      MimeType: string
      SizeBytes: int64
      Content: Stream
  }

  type IEmailProvider =
      abstract ListNewMessages: since:DateTimeOffset option -> AsyncSeq<EmailMessage>
      abstract GetAttachments: messageId:string -> Async<EmailAttachment list>
  ```
- [ ] Gmail implementation of `IEmailProvider` using `Google.Apis.Gmail.v1`
- [ ] Abstract enough that IMAP/Graph can implement the same interface later

### 1.3 — Incremental Message Listing
- [ ] Use Gmail History API for efficient incremental sync (track `historyId` in `sync_state`)
- [ ] Fallback to date-based listing if no history ID exists (first sync)
- [ ] Filter: only messages with attachments (Gmail `has:attachment` query for first pass)
- [ ] Store `last_history_id`, `last_sync_at`, `message_count` in `sync_state` table per account
- [ ] `--since DATE` flag overrides the start point
- [ ] `--full` flag re-scans all messages (but skips already-downloaded files by SHA256)

### 1.4 — Attachment Download
- [ ] Download attachments from each message
- [ ] Skip inline images below `min_attachment_size` (default 20KB) — logos, signatures, tracking pixels
- [ ] Default mime types: `application/pdf`, `image/png`, `image/jpeg`, `image/tiff`
- [ ] Configurable `--all-types` to include `.docx`, `.xlsx`, `.csv`, `.zip`
- [ ] Compute SHA256 of downloaded content
- [ ] Standardised filename: `{date:yyyy-MM-dd}_{sender_short}_{original_name}.{ext}`
  - `sender_short` = sender domain or first 20 chars of display name, sanitised for filesystem
- [ ] Write to `{archive_dir}/unclassified/{filename}`
- [ ] Handle filename collisions (append `_1`, `_2`, etc.)

### 1.5 — Provenance Recording
- [ ] Insert row into `messages` table: `gmail_id`, `account`, `sender`, `subject`, `date`, `thread_id`, `label_ids`, `has_attachments`
- [ ] Write a `.meta.json` sidecar file alongside each downloaded attachment in `unclassified/`:
  ```json
  {
    "source_type": "email_attachment",
    "account": "john-personal",
    "gmail_id": "18e4f2a3b5c6d7e8",
    "sender": "bob@plumbing.com.au",
    "subject": "Invoice for March work",
    "date": "2025-03-15T10:30:00+11:00",
    "original_name": "Invoice-2025-001.pdf",
    "sha256": "abc123..."
  }
  ```
- [ ] The classifier (Phase 2) reads this sidecar to populate the `documents` table with provenance

### 1.6 — Rate Limiting & Resilience
- [ ] Gmail API: sleep 1 second every 45 API calls
- [ ] Exponential backoff on HTTP 429 (rate limit exceeded)
- [ ] Retry transient errors (5xx, network timeouts) up to 3 times
- [ ] Log warnings for skipped messages (errors, unsupported attachment types)

### 1.7 — Interrupt Safety
- [ ] Process messages one at a time; update `sync_state` after each message is fully processed
- [ ] If interrupted (Ctrl-C, service stop), the next sync resumes from the last completed message
- [ ] Incomplete downloads (no corresponding `.meta.json`) are cleaned up on next sync start

### 1.8 — Dry Run
- [ ] `hermes sync --dry-run` lists messages and attachments that would be downloaded, without downloading
- [ ] Output: table with date, sender, subject, attachment count, total size

---

## NuGet Packages

| Package | Purpose |
|---------|---------|
| `Google.Apis.Gmail.v1` | Gmail API client |
| `Google.Apis.Auth` | OAuth2 authentication |

---

## Acceptance Criteria

- [ ] `hermes auth john-personal` opens browser, completes OAuth, stores token
- [ ] `hermes accounts` shows account with valid token and email address
- [ ] `hermes sync` downloads attachments from Gmail to `unclassified/`
- [ ] Each attachment has a `.meta.json` sidecar with full provenance
- [ ] Re-running `hermes sync` only downloads new messages (incremental)
- [ ] `hermes sync --full` re-scans all messages but skips existing files (SHA256 match)
- [ ] `hermes sync --dry-run` shows what would be downloaded without downloading
- [ ] Inline images < 20KB are skipped
- [ ] Ctrl-C during sync → next sync resumes correctly
- [ ] Rate limiting prevents Gmail API quota errors
