# 27 — Critical Review: Knowledge Plunder Wave

> Self-review of the work adopting patterns from matvelloso/knowledge
> Date: May 2026

## What was built

Three commits adding six features:

1. **Microsoft Outlook email provider** — `OutlookProvider.fs` (~380 lines), Graph API with immutable IDs
2. **PPTX extraction** — `PptxExtraction.fs` (~120 lines), Open XML SDK
3. **Sidecar generalisation** — `GmailId` → `ProviderId` with backwards compat
4. **Retrieval-augmented comprehension** — schema hints from past docs (field names only, no values)
5. **Natural language preferences** — config field injected into comprehension prompt
6. **Learned patterns + suggestion approval** — DB tables, upsert from comprehension, approve/reject functions

Test count: 826 passing (was 798). Build: 0 warnings, 0 errors.

## What's good

- **Outlook Graph integration is well-structured**: immutable IDs via `DelegatingHandler` prevent the common bug where message IDs change when mail is moved. `WithUrl` for `@odata.nextLink` is the correct Graph pagination pattern. Token refresh is handled automatically by MSAL + persistent cache.

- **RAC is cautious about value leakage**: `compactSchemaHint` strips actual field values and passes only `document_type` + `field_names`. This avoids the contamination risk where stale amounts/dates from past documents get copied into new extractions.

- **Best-effort side effects are correct**: learned pattern and suggestion writes are wrapped in try/with and never block the pipeline. This is the right pattern for auxiliary functionality that shouldn't break the core flow.

- **Provider factory dispatch is clean**: `Pipeline.Deps.CreateEmailProvider` dispatches on `AccountConfig.Provider`, and the account config carries Outlook-specific fields (client_id, tenant_id, redirect_port) with sensible defaults.

## Blocking issues to fix

### 1. Outlook saves body previews, not full email bodies

`OutlookProvider.getFullMessage` selects `metadataSelect` fields which maps `BodyPreview` into `BodyText`. Gmail's equivalent returns the full message body. This means Outlook email documents will have truncated content, degrading search and comprehension quality.

**Fix**: Make `getFullMessage` fetch the `body` property and normalise it, or have the sync consumer call `getMessageBody` when full text is needed.

**Status**: Not yet fixed.

### 2. Suggestions/approval is not a silver thread

`approveSuggestion` and `rejectSuggestion` exist in Core but have no API endpoints, no UI, and no way for a user to trigger them. By the project's own "definition of done", this is backend-only state with no visible outcome.

**Fix**: Either add `GET/POST /api/suggestions` endpoints + UI panel, or explicitly de-scope as "schema groundwork only."

**Status**: Tracked as pending todo (`api-endpoints`, `ui-settings`).

### 3. PPTX extraction has no positive test coverage

The three content tests (single slide text, tables, speaker notes) were removed because programmatic PPTX construction didn't round-trip correctly through Open XML SDK. Only error-handling and extension-detection tests remain. A regression in the actual extractor will leave the build green.

**Fix**: Add committed fixture PPTX files (real samples, not programmatically generated) and test against them.

**Status**: Not yet fixed.

## Non-blocking issues

### 4. RAC queries will full-scan as data grows

`LIKE '%@domain%'` cannot use the sender index due to the leading wildcard. The vendor fallback query uses `extracted_vendor = @vendor` but there's no index on that column.

**Fix**: Add a normalised `sender_domain` column with index, and an index on `extracted_vendor`.

### 5. `compactSchemaHint` can emit truncated invalid JSON

Hard-cutting at 300 chars can produce malformed JSON fragments. The LLM can probably handle this, but it's noisy.

**Fix**: Serialize a bounded structure properly, or use a deterministic non-JSON format like `document_type=invoice; fields=[amount,date,vendor]`.

### 6. `learned_patterns` is write-only

The table is populated but never read. It's also not wired into the comprehension pipeline or the rules engine. Worse, `approveSuggestion` writes canonical `category` into the `document_type` column, mixing two semantics.

**Fix**: Either wire into retrieval/classification or defer the table. If kept, split `document_type` and `category` into separate columns.

### 7. Suggestion approval leaves document state inconsistent

`approveSuggestion` updates `category` but doesn't set `classification_tier = 'manual'` or update confidence. The document can still appear as low-confidence in the UI.

**Fix**: Update tier/confidence/audit fields on approval, in a transaction.

### 8. Outlook auth is over-permissioned

Scopes include `Mail.ReadWrite` but the provider is read-only. Token cache is hardcoded to `"hermes-outlook"` regardless of which account/tenant is being used.

**Fix**: Reduce to `Mail.Read` + `User.Read`, scope cache per account label.

### 9. PptxExtraction uses `InnerText` instead of paragraph walking

`shape.InnerText` flattens all text content without preserving paragraph boundaries, line breaks, or structured formatting. Tables use Run-level text extraction which can lose cell boundaries.

**Fix**: Walk `TextBody → Paragraph → Run/Break/Field` explicitly and preserve block structure.

### 10. Stages.fs is getting too large

It now owns prompt assembly, RAC retrieval, learned-pattern persistence, suggestion creation, and suggestion approval (~420 lines of comprehension-related code). The missing tests are a symptom of coupling.

**Fix**: Extract into separate modules: `ComprehensionRac.fs`, `PatternLearning.fs`, `SuggestionReview.fs`.

## Verdict

**Good directional progress, but the wave is not complete.** The architecture is sound, the patterns are well-adapted from Knowledge, and the safety rails (value-free schema hints, best-effort side effects) are correct. But three blocking issues remain:

1. Outlook full-body content
2. PPTX positive test coverage  
3. Suggestion silver thread (API + UI)

These should be addressed before this wave is called done. The non-blocking issues are technical debt to track, not blockers.

## Next steps

| Priority | Item | Effort |
|----------|------|--------|
| 🔴 | Fix Outlook `getFullMessage` to return full body | Small |
| 🔴 | Add fixture-based PPTX tests | Small |
| 🔴 | API endpoints for preferences/patterns/suggestions | Medium |
| 🔴 | Settings UI for preferences + suggestion review | Medium |
| 🟡 | Wire learned_patterns into RAC retrieval | Small |
| 🟡 | Add sender_domain column + index | Small |
| 🟡 | Extract Stages.fs into focused modules | Medium |
| 🟢 | Reduce Outlook scopes | Small |
| 🟢 | Fix compactSchemaHint truncation | Small |
