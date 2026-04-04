---
description: "Manual smoke test checklist for Hermes UI. Run through this after every wave."
---

# Smoke Test Checklist

> **Pre-conditions**
> - `dotnet run --project src/Hermes.App` launches successfully
> - At least 1 Gmail account configured (or watch folder active)
> - Archive has documents
> - Ollama running locally (or Azure OpenAI configured)

---

## Launch & Layout

- [ ] Window opens within ~5 seconds
- [ ] Three columns visible: funnel (left), content (centre), chat (right)
- [ ] Funnel column shows all sections: Sources, Intake, Extracting, Classifying, Library, Index, Action Items, Services
- [ ] Status bar at bottom shows document count and state
- [ ] Window title shows "Hermes"

## Sources Section

- [ ] Email accounts listed with message counts
- [ ] Backfill progress bar visible (if backfill active)
- [ ] "Last sync: Nm ago" text present and updates on refresh
- [ ] **[+ Add Account]** button opens OAuth dialog (or shows instructions)
- [ ] **[+ Add Folder]** button opens folder picker
- [ ] Watch folders listed under Sources

## Processing Pipeline — Intake

- [ ] Intake section shows count of files in `unclassified/`
- [ ] Drop a file into `~/Documents/Hermes/unclassified/` → appears in Intake within 15 seconds
- [ ] After sync cycle: file moves from Intake → Extracting → Classifying → Library

## Processing Pipeline — Extracting

- [ ] Count shown matches documents being extracted
- [ ] Section collapses or shows 0 when empty

## Processing Pipeline — Classifying

- [ ] Count shown matches documents being classified
- [ ] Section collapses or shows 0 when empty

## Library

- [ ] Categories listed with correct document counts
- [ ] Click a category → content pane shows document list for that category
- [ ] Click a document → content pane shows metadata + extracted markdown preview
- [ ] **[Open File]** button opens the document in the default app
- [ ] Classification tier and confidence shown (e.g. "content (85%)" or "rule")
- [ ] **[Back]** button returns to category list
- [ ] Breadcrumb trail updates during navigation

## Index

- [ ] Progress bars show searchable/embedded counts
- [ ] Percentages update after a sync cycle

## Action Items — Reminders

- [ ] Overdue reminders displayed with red styling
- [ ] Upcoming reminders displayed with amber/normal styling
- [ ] **[Mark Paid]** → removes from active list
- [ ] **[Snooze 7d]** → disappears from list (reappears after snooze period)
- [ ] **[Dismiss]** → permanently removed from list
- [ ] Document link on reminder → opens file in default app
- [ ] Action item count in funnel updates after actions

## Services

- [ ] Ollama status dot: green if running, red if not
- [ ] Database status dot: green if DB exists

## Chat Pane

- [ ] Type a query → results appear with document cards
- [ ] Document cards show filename, category, date, amount (where available)
- [ ] Click a document card → opens file in default app
- [ ] **AI toggle** → enables LLM summarisation (when Ollama/Azure configured)
- [ ] With AI on: response includes natural language summary + document cards
- [ ] Suggested query chips visible when chat is empty
- [ ] Click a chip → query executes
- [ ] Empty query → no action (button disabled or no-op)
- [ ] Chat history preserved across navigation

## Chat Pane Toggle

- [ ] Click **💬** button → chat pane hides, content pane expands
- [ ] Click **💬** again → chat pane reappears
- [ ] Chat history preserved across toggle

## Settings Dialog

- [ ] **⚙** button opens settings dialog
- [ ] **General tab**: sync interval, min attachment size populated from config
- [ ] **AI/Chat tab**: provider radio (Ollama/Azure OpenAI) reflects current config
- [ ] **Accounts tab**: accounts listed with backfill toggle and batch size
- [ ] **Save** → closes dialog, changes written to config.yaml
- [ ] **Cancel** → closes dialog, no changes saved

## Sync Controls

- [ ] **[Sync Now]** → button shows "Syncing...", completes, counts update
- [ ] **[Pause]** → toggles to "Resume", sync stops
- [ ] **[Resume]** → sync restarts, button reverts to "Pause"

## Error States

- [ ] Ollama not running → service dot red, chat still works (search-only, no AI summary)
- [ ] No Gmail credentials → "Add Account" prompt shown in Sources
- [ ] Empty archive (no documents) → Library shows 0, processing sections empty
- [ ] Chat with empty archive → response says "No database found" or "No results"

## Keyboard & Accessibility

- [ ] Tab key moves focus through controls in logical order
- [ ] Enter key submits chat query when input is focused
- [ ] Escape key closes settings dialog

---

> **Result**: ______ / ______ items passing
>
> **Tested by**: ________________  **Date**: ________________
