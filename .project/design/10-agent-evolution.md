# Hermes — Agent Evolution: From Index to Personal Agent

> Design doc for evolving Hermes from passive document intelligence into an active personal agent.  
> Created: 2026-03-31

---

## 1. Where We Are

Hermes today is a **passive index** — it ingests, classifies, extracts, embeds, and exposes documents through MCP and a local chat interface. The data flow is one-directional:

```
Sources → Intake → Pipeline → Index → Query (MCP / Chat)
```

The MCP server (doc 05) is read-only by design. The chat UI answers questions about what's already indexed. Nothing in the system takes initiative or performs actions.

---

## 2. Where We're Going

Hermes becomes an **active personal agent** that observes, decides, and acts — while remaining local-first, MCP-first, and privacy-preserving.

```
Sources → Intake → Pipeline → Index ─┬─→ Query (MCP / Chat)
                                      │
                                      ├─→ Triggers (observe + decide)
                                      │     ↓
                                      └─→ Skills  (act)
```

### Guiding principle: MCP-first, not MCP-only

- **Primary integration surface**: MCP tools — both read and write. Any external agent (Claude, Copilot, custom) can orchestrate Hermes via MCP.
- **Internal autonomy**: A lightweight trigger→action pipeline handles time-sensitive things (alerts, due dates, recurring patterns) without waiting for a human to ask.
- **The door stays open**: The internal system uses the same skill interfaces that MCP exposes, so upgrading from MCP-orchestrated to internally-autonomous is a capability dial, not an architecture change.

---

## 3. Three New Concepts

### 3.1 — Triggers (observe + decide)

A trigger is a condition→action rule that fires when a document enters or updates in the index. This extends the existing classification rule engine (`Rules.fs`) with downstream effects beyond folder placement.

```fsharp
type TriggerCondition =
    | CategoryIs of string
    | VendorContains of string
    | AmountAbove of decimal
    | DueDateWithin of TimeSpan
    | SenderDomain of string
    | TextMatches of string  // FTS5 query
    | And of TriggerCondition list
    | Or of TriggerCondition list

type TriggerAction =
    | RaiseAlert of severity: AlertSeverity * message: string
    | InvokeSkill of skillName: string * parameters: Map<string, string>
    | TagDocument of tag: string
    | NotifyUser of message: string

type Trigger = {
    Name: string
    Enabled: bool
    Conditions: TriggerCondition
    Actions: TriggerAction list
}
```

**Examples:**
| Trigger | Condition | Action |
|---------|-----------|--------|
| Bill due soon | `CategoryIs "invoices" AND DueDateWithin 7d` | `RaiseAlert (Warning, "Invoice from {vendor} due {date}")` |
| Large payment | `AmountAbove 5000m` | `NotifyUser "Document with amount > $5,000 received"` |
| Tax document arrived | `CategoryIs "tax" AND SenderDomain "ato.gov.au"` | `TagDocument "tax-2025"` |
| Auto-update GL | `CategoryIs "invoices" AND VendorContains "..."` | `InvokeSkill ("pelican-gl", ...)` |

**Configuration:** YAML in `config.yaml` under a `triggers:` section, same pattern as classification rules.

**Evaluation point:** After the extract stage completes (document has category, vendor, amount, dates). The pipeline becomes:

```
Classify → Extract → **Evaluate Triggers** → Embed
```

### 3.2 — Skills (act)

A skill is a pluggable action capability. Each skill is a Tagless-Final record of functions, consistent with the existing provider pattern (`EmailProvider<'F>`, etc.).

```fsharp
/// A skill that Hermes can invoke to perform an action.
type Skill<'F> = {
    /// Unique name (used in trigger actions and MCP tool names).
    Name: string
    /// Human-readable description.
    Description: string
    /// Execute the skill with parameters, returning a result summary.
    Execute: Map<string, string> -> 'F<Result<string, string>>
    /// Schema for parameters (for MCP tool generation and validation).
    ParameterSchema: SkillParameter list
}

type SkillParameter = {
    Name: string
    Description: string
    Required: bool
    Type: ParameterType
}
```

**Key design properties:**
- Skills are **registered at the composition root**, same as providers.
- Each skill auto-generates an MCP tool (`hermes_skill_{name}`), so external agents can invoke skills too.
- Skills can be invoked by triggers (internal) or by MCP (external) — same code path.
- Fakes for testing — standard Tagless-Final benefit.

**Planned skills (priority order):**

| Skill | Description | Phase |
|-------|-------------|-------|
| `alert` | Surface a notification in the UI tray + shell window | Near-term |
| `send-email` | Draft and send an email via Gmail API | Near-term |
| `pelican-gl` | Post a journal entry to Pelican general ledger | Medium-term |
| `calendar` | Create/check calendar events (Google Calendar API) | Medium-term |
| `draft-reply` | Generate an email reply draft using Ollama + context | Future |
| `appointment` | Book/reschedule appointments via calendar + email | Future |

### 3.3 — Agent Loop (future, not MCP-first phase)

The long-term vision is a lightweight internal planning loop:

```
User: "Pay the Allianz invoice"
  → Search: find Allianz invoice documents
  → Extract: amount = $1,234.00, BSB = ..., account = ...
  → Plan: [1] confirm details with user, [2] invoke pelican-gl skill
  → Confirm: "I found an Allianz invoice for $1,234 due 15 April. Update Pelican GL?"
  → Execute: InvokeSkill("pelican-gl", { amount = "1234"; vendor = "Allianz"; ... })
```

**This is explicitly deferred.** The MCP-first approach means external agents already handle planning and execution. The internal loop only becomes valuable when we want Hermes to act autonomously on triggers without a human in the chat.

---

## 4. MCP Evolution

The MCP server (doc 05) evolves from read-only to read-write:

### New write tools

| Tool | Description | Safety |
|------|-------------|--------|
| `hermes_create_alert` | Create an alert/notification for the user | Low risk |
| `hermes_send_email` | Draft and send email via configured account | **Confirmation required** |
| `hermes_invoke_skill` | Invoke any registered skill by name | Per-skill risk assessment |
| `hermes_manage_trigger` | Create/update/disable triggers | Low risk |
| `hermes_tag_document` | Add/remove tags on a document | Low risk |

### Safety model

Write operations are categorised:

| Level | Policy | Examples |
|-------|--------|----------|
| **Safe** | Execute immediately, log | `create_alert`, `tag_document` |
| **Confirm** | Queue for user approval in shell UI | `send_email`, `pelican-gl` |
| **Deny** | Never auto-execute, MCP-only with explicit human action | `delete_document` (future) |

The confirmation queue appears in the shell window as a notification badge — user reviews and approves/rejects pending actions. This maps to the alert panel in the UI redesign (doc 09).

---

## 5. Architecture Impact

### Pipeline extension

```
Classify → Extract → **Trigger Evaluator** → Embed
                           │
                           ├─→ Alert Channel<T>
                           └─→ Skill Dispatch Channel<T>
```

Two new `Channel<T>` consumers branch off after trigger evaluation:
- **Alert channel**: surfaces notifications in the UI.
- **Skill dispatch channel**: executes skill actions (with confirmation gating for non-safe operations).

### New Core modules

| Module | Purpose |
|--------|---------|
| `Triggers.fs` | Trigger condition DSL, evaluation, YAML config parsing |
| `Skills.fs` | Skill registry, dispatch, parameter validation |
| `Alerts.fs` | Alert model, persistence (SQLite), UI surface |

### DB additions

```sql
-- Alerts raised by triggers or skills
CREATE TABLE IF NOT EXISTS alerts (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at    TEXT    NOT NULL DEFAULT (datetime('now')),
    severity      TEXT    NOT NULL,  -- 'info', 'warning', 'urgent'
    message       TEXT    NOT NULL,
    document_id   INTEGER REFERENCES documents(id),
    trigger_name  TEXT,
    acknowledged  INTEGER NOT NULL DEFAULT 0
);

-- Pending skill actions awaiting confirmation
CREATE TABLE IF NOT EXISTS pending_actions (
    id            INTEGER PRIMARY KEY AUTOINCREMENT,
    created_at    TEXT    NOT NULL DEFAULT (datetime('now')),
    skill_name    TEXT    NOT NULL,
    parameters    TEXT    NOT NULL,  -- JSON
    document_id   INTEGER REFERENCES documents(id),
    trigger_name  TEXT,
    status        TEXT    NOT NULL DEFAULT 'pending',  -- 'pending', 'approved', 'rejected', 'executed', 'failed'
    result        TEXT
);

-- Document tags (many-to-many)
CREATE TABLE IF NOT EXISTS document_tags (
    document_id   INTEGER NOT NULL REFERENCES documents(id),
    tag           TEXT    NOT NULL,
    created_at    TEXT    NOT NULL DEFAULT (datetime('now')),
    PRIMARY KEY (document_id, tag)
);
```

---

## 6. Implementation Order

| Phase | What | Depends on |
|-------|------|------------|
| **E1** | Alert model + `alerts` table + shell UI notification badge | UI redesign (doc 09) |
| **E2** | Trigger DSL + YAML config + evaluator in pipeline | E1 |
| **E3** | Skill interface + registry + `alert` skill (first skill) | E2 |
| **E4** | MCP write tools (`hermes_create_alert`, `hermes_tag_document`) | E3 |
| **E5** | `send-email` skill + confirmation queue + pending actions UI | E4 |
| **E6** | Pelican GL integration skill | E5 |
| **E7** | Calendar skill + appointment flow | E5 |
| **E8** | Internal agent loop (deferred — evaluate if MCP-first is sufficient) | E5+ |

---

## 7. What Doesn't Change

- **Local-first**: skills execute locally. External API calls (Gmail send, Pelican, Calendar) go direct from the user's machine, not through any cloud relay.
- **Tagless-Final**: skills are records of functions parameterised over the effect type — same pattern as everything else in Core.
- **MCP is primary**: external agents remain first-class citizens. The internal trigger system is a convenience layer, not a replacement.
- **SQLite is the store**: alerts, pending actions, and tags join the existing schema.
- **Pipeline architecture**: `Channel<T>` stages, uni-directional flow, each stage independent.

---

## 8. Open Questions

| # | Question | Leaning |
|---|----------|---------|
| 1 | Should triggers be YAML-only or also configurable via UI? | YAML first, UI later (like classification rules) |
| 2 | Should the confirmation queue be in-app only or also surface via OS notifications? | Both — OS notification points to in-app queue |
| 3 | Pelican GL integration: REST API, file export, or direct DB? | REST API if Pelican exposes one; otherwise CSV export |
| 4 | Should skills be loadable as plugins (DLLs) or compiled-in only? | Compiled-in for now; plugin model is over-engineering at this stage |
| 5 | How much context should triggers see? Full extracted text or metadata only? | Metadata first (category, vendor, amount, dates); text matching via FTS5 for advanced triggers |
| 6 | Should the agent loop use Ollama or be rule-based? | Rule-based triggers + Ollama for the chat agent loop (phase E8) |
