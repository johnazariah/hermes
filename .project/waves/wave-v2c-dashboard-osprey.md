# Wave v2c: Dashboard Polish & Osprey Integration

## Summary

The Hermes dashboard needs refinement for dad-friendliness — clear, self-explanatory progress indicators with plain-English tooltips and live document names — while the MCP server (Osprey) must be validated and extended to work correctly with the new channel-driven pipeline. The goal: dad can watch the pipeline work and understand what's happening; AI agents can query documents while ingestion is in progress. Observable and queryable at all times.

## Goals

- **Dashboard clarity**: Every progress bar and stat card is self-explanatory with tooltips
- **Single source of truth**: Sidebar pipeline section replaced with compact mirror of main dashboard
- **Live processing visibility**: "Currently processing" indicators at each active stage
- **Meaningful activity feed**: Activity panel populated with real pipeline events
- **Per-account email breakdown**: Each Gmail account's download progress shown separately
- **MCP concurrent access**: All 13 MCP tools work while pipeline is actively ingesting
- **Partial-pipeline queryability**: Search returns results from extracted docs even if not yet filed/memorised
- **Improved search grouping**: MCP `search` uses `thread_id` and `source_type` for result grouping
- **SSE/streaming verification**: Pipeline state streaming verified or cleanly removed

## Non-Goals

- New MCP tools (validate existing 13 only)
- Dashboard layout redesign (polish existing components)
- Mobile responsiveness
- New pipeline stages
- MCP authentication/authorization
- Email provider expansion beyond Gmail

## Target Domains

| Domain | Status | Relationship | Role |
|--------|--------|-------------|------|
| pipeline | existing | **consume** | Read pipeline state for dashboard display |
| mcp-server | existing | **modify** | Validate tools, extend search grouping, verify concurrent safety |
| web-dashboard | existing | **modify** | Sidebar refactor, tooltips, activity feed, per-account breakdown |
| activity-log | existing | **modify** | Populate with pipeline events |
| database | existing | **consume** | WAL-mode concurrent reads |
| email-sync | existing | **consume** | Per-account sync state for progress breakdown |

## Complexity

- **Score**: CS-3 (medium)
- **Total P**: 7
- **Confidence**: 0.80
- **Dependencies**: Wave v2b (pipeline fixes) must be complete
- **Phases**:
  1. Dashboard polish (sidebar, tooltips, processing indicators, download completion)
  2. Activity log population (emit pipeline events to activity_log table)
  3. Activity panel wiring (display activity events)
  4. Per-account email breakdown (API + UI)
  5. MCP tool validation (all 13 tools against channel pipeline)
  6. MCP search enhancement (`thread_id`/`source_type` grouping, partial results)
  7. SSE/streaming verification
  8. Integration testing (concurrent MCP + pipeline)

## Acceptance Criteria

### Dashboard Polish

1. **Sidebar compactness**: Sidebar pipeline section displays same data as main dashboard in compact form. One source of truth.

2. **Tooltip coverage**: Every progress bar and stat card has a tooltip explaining in plain English what the stage does (e.g., "Downloading: Hermes is fetching new emails and attachments from your Gmail accounts"). No jargon.

3. **Currently processing indicator**: Each active pipeline stage shows the name of the document being processed. Idle stages show "Idle". Updates within 2 seconds.

4. **Activity panel population**: Shows document downloaded, read, and filed events in reverse-chronological order with timestamps and document names. Updates within 5 seconds.

5. **Per-account email breakdown**: Each Gmail account shows its own download progress bar with account name. Single-account setups show one labelled bar.

6. **Download completion state**: When sync completes, progress bar shows "Complete ✓" or transitions cleanly. Does not remain at 100% indefinitely.

### Osprey (MCP) Integration

7. **All 13 tools functional**: Each MCP tool returns valid results against channel-pipeline-populated DB. No unhandled exceptions.

8. **Concurrent access**: MCP tool invocations return correct results during active ingestion. No SQLite locking errors. Search < 500ms during ingestion.

9. **Partial-pipeline queryability**: Documents with extracted text but not yet filed/memorised are returned by `search` and `get_document`. Documents still downloading are NOT returned.

10. **Search grouping with thread_id**: `search` groups results by `thread_id` when available. Email body and attachments appear together. `source_type` included in response metadata.

11. **SSE streaming resolution**: Pipeline state available via SSE or polling. Dead endpoints removed. If SSE retained, events emit within 2 seconds.

## Open Questions (RESOLVED)

1. **Activity log retention**: Unbounded for now. Having someone mail us their log should be enough for debugging. Cap in future wave if needed.
2. **Tooltip content review**: Iterate during implementation — don't block on copywriting.
3. **Currently processing granularity**: Just document name.
4. **Per-account progress source**: Needs modification — current sync loop shares one counter across accounts. Add per-account counters.
5. **MCP partial results boundary**: Queryable after extraction (post-read). Classification is optional enrichment, not a gate for searchability.

## Risks & Assumptions

| Risk | Impact | Mitigation |
|------|--------|------------|
| WAL contention under load | MCP queries slow during ingestion | Test with 500+ docs ingesting + concurrent MCP queries |
| "Currently processing" state exposure | Mutable state in F# pipeline | Use mutable PipelineStatus record (already exists) |
| Tooltip quality | Bad tooltips worse than none | Write as if explaining to non-technical parent |
| Activity log volume | Rapid growth during large ingestion | Consider retention policy in future wave |

## Workshop Opportunities

| Topic | Type | Key Questions |
|-------|------|---------------|
| Activity Log Event Model | Data Model | What event types? What metadata? Where emit? Retention? |
| MCP Concurrent Access Patterns | Integration | Connection pooling? Read-only connections? SQLITE_BUSY handling? |
| Dashboard Tooltip Copy | UX | Dad-friendly language for each stage? Consistent voice? |
