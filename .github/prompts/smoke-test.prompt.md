---
description: "Privacy-safe manual smoke checklist for the canonical Hermes Service, React UI, and Windows Tray."
---

# Smoke Test Checklist

Use this checklist after a wave changes a supported or preview runtime surface.
Record evidence in the active wave and testing register; do not turn this prompt
into a separate status manifest.

## Safety Preconditions

- [ ] Use an isolated `HERMES_CONFIG_DIR`, archive, database, and development port.
- [ ] Configure no real email accounts or watched folders; provider sync is disabled.
- [ ] Use only synthetic fixtures. Do not open or copy the live archive.
- [ ] Disable cloud providers and external network access unless the test has
      explicit consent and sanitized input.
- [ ] Record counts, synthetic IDs, and pass/fail results only; never record body
      text, credentials, personal paths, or personal filenames.

## Launch

- [ ] `dotnet run --project src/Hermes.Service` starts against the isolated state.
- [ ] The health endpoint responds on the configured development port.
- [ ] `/` and registered deep links `/documents`, `/search`, `/settings`, and
      `/onboarding` render the same React application rather than Blazor.
- [ ] On Windows, `dotnet run --project src/Hermes.Tray` opens that same Service
      UI and exits cleanly.
- [ ] No Gmail, Outlook, watcher, or Ollama activity occurs unless explicitly
      enabled for a synthetic test.

## Canonical React UI

- [ ] Home, Documents, Search, Settings, and onboarding routes render without
      console errors.
- [ ] Pipeline and Chat routes remain BLOCKED until issue #9 wires them.
- [ ] Navigation and browser refresh preserve the selected deep route.
- [ ] Empty-state panels are truthful when the isolated archive contains no data.
- [ ] With synthetic data, document lists and detail views show persisted values,
      and file actions target only the disposable archive.
- [ ] Settings read and write only the isolated configuration and visibly report
      errors rather than silently succeeding.

## Pipeline and Archive

- [ ] A synthetic fixture enters the V5 DAG and appears in Pipeline activity.
- [ ] Extract, triage, deep-comprehend, and embed state matches the declared DAG
      and `stage_completions`.
- [ ] Processing preserves the synthetic source bytes and path.
- [ ] Retry/reload does not duplicate stage output or move files.
- [ ] Reflow and reclassification remain blocked unless the active issue supplies
      the bounded synthetic procedure and expected rollback evidence.

## Search and MCP

- [ ] Metadata keyword search returns the expected synthetic document.
- [ ] File-backed content search is not marked passing until issue #11 is closed.
- [ ] Semantic/hybrid HTTP and MCP reachability is not marked passing until issue
      #6 is closed.
- [ ] MCP tool enumeration, calls, and returned IDs use only synthetic data.
- [ ] Read-only calls do not write files, mutate database state, or invoke an LLM.

## Chat SSE (component-level until routed)

- [ ] Record browser-level Chat as BLOCKED until issue #9 exposes a canonical
      route.
- [ ] In component tests, or after route wiring, `ChatPane` and `ChatPage` both
      submit a synthetic query.
- [ ] Each consumer handles `results`, optional `answer`, and terminal `done`
      events without duplicate rendering.
- [ ] Search-only chat works with model providers disabled.
- [ ] The UI makes no request to the nonexistent `/api/pipeline/state` endpoint.

## Trust Boundary

- [ ] Service remains bound to loopback.
- [ ] Canonical UI origins work on the configured development port.
- [ ] Foreign-origin requests are rejected once the trust-boundary work lands.
- [ ] Mutating MCP tools are unavailable by default and reject missing or invalid
      credentials once authenticated mutation lands.
- [ ] Responses and logs disclose no credentials or personal data.

## Evidence

- [ ] Record OS, commit SHA, isolated port, synthetic fixture identifier, and
      PASS/FAIL/BLOCKED for each applicable section.
- [ ] Link known failures to active issues (#6, #9, #11, #17, or #18).
- [ ] Confirm the source archive and owner checkout are unchanged.

> **Result:** ______ passing / ______ applicable
>
> **Commit:** ________________  **Date:** ________________
