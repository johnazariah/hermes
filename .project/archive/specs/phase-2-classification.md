# Phase 2: Classification Pipeline

**Status**: Not Started  
**Depends On**: Phase 0 (Project Skeleton)  
**Deliverable**: Files auto-classify within seconds of appearing in `unclassified/`. Users correct by moving files.

---

## Objective

Watch the `unclassified/` folder for new files, deduplicate, classify using a rules cascade, move files to category folders, and record them in the database.

---

## Tasks

### 2.1 — FileSystemWatcher on `unclassified/`
- [x] `System.IO.FileSystemWatcher` monitoring `{archive_dir}/unclassified/` for new files
- [x] Filter on `Created` events, ignore directories
- [x] Debounce: wait 500ms after a file creation event before processing (handles large file writes)
- [x] Verify file is fully written (stable size check) before processing
- [x] Post `FileEvent` to a `Channel<FileEvent>` for the classifier task to consume

### 2.2 — Sidecar Metadata Reading
- [x] When processing a file from `unclassified/`, look for a matching `.meta.json` sidecar
- [x] If present: read provenance (source_type, account, gmail_id, sender, subject, date, sha256)
- [x] If absent: source_type = `manual_drop`, compute SHA256, no email metadata

### 2.3 — SHA256 Deduplication
- [x] Compute SHA256 of the file content (or read from sidecar if available)
- [x] Query `documents` table for existing rows with the same SHA256
- [x] If duplicate found:
  - Insert a new `documents` row pointing to the existing `saved_path`
  - Delete the duplicate file from `unclassified/`
  - Delete the sidecar if present
  - Log: "Deduplicated {filename} → existing {saved_path}"
- [x] If not a duplicate: proceed to classification

### 2.4 — Rules Engine
- [x] Load rules from `{config_dir}/rules.yaml` on startup, reload on file change
- [x] Rules cascade — **first match wins**, evaluated in priority order:

#### Signal 1: Sender Domain Rules
```yaml
domain_rules:
  cba.com.au: bank-statements
  westpac.com.au: bank-statements
  ato.gov.au: tax
  # ... user-defined
```
- Match sender email domain against rules
- Highest confidence — zero ambiguity

#### Signal 2: Filename Pattern Rules
```yaml
filename_rules:
  - pattern: "statement"
    category: bank-statements
  - pattern: "invoice|inv[-_]\\d"
    category: invoices
  - pattern: "receipt"
    category: receipts
  - pattern: "payslip|pay[-_]?stub"
    category: payslips
  # ... user-defined
```
- Case-insensitive regex match on filename

#### Signal 3: Subject Pattern Rules
```yaml
subject_rules:
  - pattern: "body\\s*corp|strata"
    category: property
  - pattern: "subscription|renewal"
    category: subscriptions
  # ... user-defined
```
- Case-insensitive regex match on email subject (from sidecar metadata)
- Only applicable for email-sourced documents

#### Signal 4: Fallback
- If no rule matches → category = `unsorted`

### 2.5 — File Move & DB Insert
- [x] Move file from `unclassified/{filename}` to `{archive_dir}/{category}/{filename}`
- [x] Create category directory if it doesn't exist
- [x] Delete the sidecar `.meta.json` after processing
- [x] Insert `documents` row:
  - `source_type`, `gmail_id`, `account`, `sender`, `subject`, `email_date`
  - `original_name`, `saved_path` (relative to archive root), `category`
  - `mime_type` (from file extension), `size_bytes`, `sha256`
  - `extracted_at = NULL`, `embedded_at = NULL`
- [x] Post `DocumentId` to the extraction channel (Phase 3)

### 2.6 — Default Category Folders
- [x] On `hermes init`, create default category folders:
  ```
  bank-statements/ insurance/ invoices/ legal/ medical/ donations/
  payslips/ property/ rates-and-tax/ receipts/ subscriptions/
  tax/ utilities/ unsorted/
  ```
- [x] Users can create subcategories freely (e.g. `property/manorwoods/`)

### 2.7 — Reconcile Command
- [x] `hermes reconcile` walks the entire archive directory tree
- [x] For each file found, check if it exists in the `documents` table (by `saved_path`)
- [x] **Moved files**: file exists in DB at old path, found at new path → update `saved_path` and `category`
- [x] **Deleted files**: DB row exists, file missing → mark as deleted (add `deleted_at` column or remove row)
- [x] **New files**: file exists on disk, no DB row → treat as manual drop, insert row
- [x] Match moved files by SHA256 hash when the path doesn't match
- [x] `--dry-run` flag: report what would change without changing
- [x] Report: table of moves, deletes, new files

### 2.8 — Suggest Rules
- [x] `hermes suggest-rules` analyses the `documents` table
- [x] Find patterns: files originally classified as `unsorted` that the user moved to a category
- [x] Group by sender domain and target category → propose domain rules
- [x] Group by filename pattern and target category → propose filename rules
- [x] Output: YAML snippet for each proposed rule, user accepts/rejects interactively
- [x] Never auto-applies rules — always requires user confirmation

---

## Rules YAML Format

```yaml
# ~/.config/hermes/rules.yaml

domain_rules:
  # sender_domain: category
  cba.com.au: bank-statements
  commbank.com.au: bank-statements
  ato.gov.au: tax

filename_rules:
  # regex pattern (case-insensitive) → category
  - pattern: "statement"
    category: bank-statements
  - pattern: "invoice|inv[-_]\\d"
    category: invoices
  - pattern: "receipt"
    category: receipts
  - pattern: "payslip|pay[-_]?stub|remittance"
    category: payslips

subject_rules:
  - pattern: "body\\s*corp|strata|owners.*corp"
    category: property
  - pattern: "subscription|renewal"
    category: subscriptions
```

---

## Acceptance Criteria

- [x] Dropping a PDF into `unclassified/` → classified and moved within 2 seconds
- [x] An email attachment with `.meta.json` sidecar is classified using sender domain, filename, or subject rules
- [x] A file dropped without a sidecar is classified by filename pattern or goes to `unsorted/`
- [x] Duplicate files (same SHA256) are deduplicated — one file on disk, multiple DB rows
- [x] `hermes reconcile` detects files moved by the user and updates DB categories
- [x] `hermes reconcile --dry-run` reports changes without applying them
- [x] `hermes suggest-rules` analyses overrides and proposes sensible new rules
- [x] Rules reload when `rules.yaml` is modified on disk
- [x] Category directories are created on demand
