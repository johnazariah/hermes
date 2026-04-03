# Wave 6: Pelican Integration

> Status: **Not started** (needs Wave 2 + Wave 3)  
> Master plan: `.github/prompts/development-plan-apr3.prompt.md` (Wave 6 section)  
> Pelican repo: https://github.com/johnazariah/pelican

## Goal

Hermes detects tax-relevant documents → posts events to Pelican's REST API → GL journal entries created. Uses `GlClient` algebra for the REST call.

**Ultimate acceptance test**: Replicate FY2024-25 tax numbers from `c:\work\tax-database\tax_data_fy2025.json`.

## Tasks

| # | Task | Status |
|---|------|--------|
| P1 | Add `Algebra.GlClient` record | Not started |
| P2 | Implement PelicanClient interpreter (HTTP POST to /api/events/*) | Not started |
| P3 | Add `pelican:` config section | Not started |
| P4 | Build TaxEventDetector.fs | Not started |
| P5 | Wire into pipeline after classification | Not started |
| P6 | End-to-end: payslip → Hermes → Pelican → journal | Not started |

## Log

(newest on top)
