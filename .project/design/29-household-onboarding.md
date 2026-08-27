# 29 — Household Onboarding & Profile

> Origin: Discussion 2026-05-09, onboarding UX review

## Problem

The current onboarding is a freeform text area ("tell Hermes about yourself"). This is:
- **Unsatisfying** — users don't know what to type
- **Single-user** — no concept of a household with multiple people
- **Unstructured** — natural language preferences are hard to query programmatically
- **Missing context** — no employment, banking, property, or investment details that help comprehension

## Solution

A **step-by-step wizard** that collects structured household information. Data saved as `household.yaml` — human-readable, editable, portable. Every step shows a clear privacy banner.

## Privacy First

Every step of the wizard displays:

> 🔒 **This information stays on your device.** It configures Hermes locally to help classify your documents. Nothing is shared with any external service.

## Wizard Steps

### Pre-start: Expectation setting

Before the wizard begins:

```
┌─────────────────────────────────────────────┐
│ 👋 Welcome to Hermes                        │
│                                             │
│ Let's set up your household profile so      │
│ Hermes can understand your documents.       │
│                                             │
│ This takes about 5 minutes. You can skip    │
│ any step and come back to it later.         │
│                                             │
│ 🔒 Everything stays on your device.         │
│                                             │
│ ━━━━━━━━━━━━━━━━ ○○○○○○○○                  │
│ 8 steps                                     │
│                                             │
│              [Get Started →]                │
└─────────────────────────────────────────────┘
```

### Step 1: Household members

Who lives in this household?

```
Members:
┌─────────────────────────────────────────────┐
│  Name: [Alex Morgan           ]             │
│  Role: [Primary ▼] (Primary/Spouse/         │
│         Dependent/Other)                    │
│                                    [+ Add]  │
│                                             │
│  Name: [Jordan Morgan         ]             │
│  Role: [Spouse ▼]                           │
│                                    [+ Add]  │
└─────────────────────────────────────────────┘
```

### Step 2: Email accounts

Link email addresses to household members.

```
┌─────────────────────────────────────────────┐
│ Alex Morgan                                 │
│  📧 [alex@example.com          ] [Gmail ▼]  │
│  📧 [alex.work@example.com     ] [Gmail ▼]  │
│                                    [+ Add]  │
│                                             │
│ Jordan Morgan                               │
│  📧 [jordan@example.com        ] [Gmail ▼]  │
│                                    [+ Add]  │
└─────────────────────────────────────────────┘
```

Provider dropdown: Gmail, Outlook

### Step 3: Employment

Where does each person work?

```
┌─────────────────────────────────────────────┐
│ Alex Morgan                                 │
│  Employer: [Contoso                  ]      │
│  Role:     [Engineer                 ]      │
│                                             │
│ Jordan Morgan                               │
│  Employer: [Fabrikam                 ]      │
│  Role:     [Teacher                  ]      │
│                                             │
│ (helps recognise payslips)                  │
└─────────────────────────────────────────────┘
```

### Step 4: Banking

Bank accounts — can be shared (joint) or individual.

```
┌─────────────────────────────────────────────┐
│ Bank: [Commonwealth Bank       ▼]           │
│ Type: [Everyday ▼] (Everyday/Savings/       │
│        Mortgage/Credit Card/Loan)           │
│ Owners: [☑ John] [☑ Smitha]               │
│                                    [+ Add]  │
│                                             │
│ Bank: [Westpac                 ▼]           │
│ Type: [Mortgage ▼]                          │
│ Owners: [☑ John] [☑ Smitha]               │
│                                    [+ Add]  │
└─────────────────────────────────────────────┘
```

### Step 5: Properties

Investment/rental properties.

```
┌─────────────────────────────────────────────┐
│ Address: [10 Sample St, Exampleton   ]      │
│ Manager: [Sample Property Management ]      │
│ Owners:  [☑ Alex] [☑ Jordan]               │
│                                    [+ Add]  │
│                                             │
│ Address: [20 Demo Rd, Testville       ]      │
│ Manager: [Demo Realty                 ]      │
│ Owners:  [☑ Alex] [□ Jordan]                │
│                                    [+ Add]  │
└─────────────────────────────────────────────┘
```

### Step 6: Investments & Super

Per person.

```
┌─────────────────────────────────────────────┐
│ Alex Morgan                                 │
│  Super fund: [Example Super           ]     │
│  Broker:     [Example Broker          ]     │
│  Holdings:   [ABC, XYZ                 ]     │
│  Share plan: [☑ Yes]                       │
│                                             │
│ Jordan Morgan                               │
│  Super fund: [Sample Super            ]     │
│  Broker:     [                        ]     │
│  Holdings:   [                        ]     │
└─────────────────────────────────────────────┘
```

### Step 7: Calendars

Access to calendars for due date reminders.

```
┌─────────────────────────────────────────────┐
│ Alex Morgan                                 │
│  📅 Google Calendar  [Connect →]            │
│  📅 Outlook Calendar [Connect →]            │
│                                             │
│ Jordan Morgan                               │
│  📅 Google Calendar  [Connect →]            │
│                                             │
│ (enables due date reminders for bills,      │
│  rates, tax deadlines)                      │
└─────────────────────────────────────────────┘
```

### Step 8: Anything else

Freeform for anything the structured steps didn't cover.

```
┌─────────────────────────────────────────────┐
│ Anything else Hermes should know?           │
│                                             │
│ ┌─────────────────────────────────────────┐ │
│ │ Documents from ATO are always tax.     │ │
│ │ We sold 20 Demo Rd in Dec 2025.        │ │
│ │ Telecom bills are for 10 Sample St.    │ │
│ └─────────────────────────────────────────┘ │
│                                             │
│ This is saved as your preferences and       │
│ guides how Hermes classifies documents.     │
└─────────────────────────────────────────────┘
```

### Completion

```
┌─────────────────────────────────────────────┐
│ 🎉 You're all set!                          │
│                                             │
│ Household: 2 members                        │
│ Email: 3 accounts connected                 │
│ Properties: 2 investment properties         │
│ Banking: 2 accounts                         │
│                                             │
│ Hermes will start syncing your email and    │
│ learning from your documents.               │
│                                             │
│ You can update any of this in Settings.     │
│                                             │
│              [Go to Home →]                 │
└─────────────────────────────────────────────┘
```

## Data Model: household.yaml

```yaml
# Hermes Household Profile
# This file configures Hermes locally. Nothing is shared externally.

members:
  - name: Alex Morgan
    role: primary
    email:
      - address: alex@example.com
        provider: gmail
      - address: alex.work@example.com
        provider: gmail
    employment:
      employer: Microsoft
      role: Principal SWE
    investments:
      super_fund: AustralianSuper
      broker: CommSec
      holdings: [MSFT, VAS, VGS]
      employee_share_plan: true
    calendars:
      - provider: google
        address: alex@example.com

  - name: Jordan Morgan
    role: spouse
    email:
      - address: jordan@example.com
        provider: gmail
    employment:
      employer: QLD Education
      role: Teacher
    investments:
      super_fund: QSuper

banking:
  - bank: Commonwealth Bank
    type: everyday
    owners: [Alex Morgan, Jordan Morgan]
  - bank: Westpac
    type: mortgage
    owners: [Alex Morgan, Jordan Morgan]

properties:
  - address: 10 Sample St, Exampleton
    manager: Sample Property Management
    owners: [Alex Morgan, Jordan Morgan]
  - address: 20 Demo Rd, Testville
    manager: Demo Realty
    owners: [Alex Morgan]

preferences: |
  Documents from ATO are always tax.
  We sold 20 Demo Rd in Dec 2025.
  Telecom bills are for 10 Sample St.
```

## How the profile helps comprehension

The household profile is injected into the comprehension prompt as structured context:

```
Household context:
- Members: Alex Morgan (primary, works at Contoso), Jordan Morgan (spouse, works at Fabrikam)
- Properties: 10 Sample St Exampleton (Sample Property Management), 20 Demo Rd Testville (Demo Realty)
- Banking: Commonwealth Bank (joint everyday), Westpac (joint mortgage)
- Alex's super: Example Super, broker: Example Broker, holdings: ABC/XYZ, employee share plan
- Smitha's super: QSuper
```

This gives the LLM enough context to:
- Match payslips to the right person by employer name
- Associate property documents with the right address
- Recognise bank statements by institution name
- Handle joint accounts appearing in either person's email
- Tag documents to the right household member

## API

- `GET /api/household` — returns the household YAML as text
- `PUT /api/household` — saves the household YAML
- `GET /api/household/members` — returns structured JSON of members (for UI dropdowns)

## Implementation phases

1. **Backend**: Domain types for household, YAML load/save, inject into comprehension context
2. **Frontend**: 8-step wizard with progress indicator, skip buttons, privacy banner
3. **Settings integration**: household editor in Settings page (edit after onboarding)

## Open questions

1. **Calendar OAuth**: Google Calendar API requires separate OAuth scope. Do we extend the existing Gmail OAuth, or create a separate calendar connection?
2. **Bank account matching**: How do we match "Commonwealth Bank" in the profile to bank statements? By sender domain? By content keywords?
3. **Multi-person document ownership**: When a joint bank statement arrives, does it get tagged to both people? How does the UI show this?
4. **Profile migration**: When household.yaml changes (property sold, employer changed), do we re-comprehend affected documents?
