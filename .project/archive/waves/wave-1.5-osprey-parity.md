# Wave 1.5: Osprey Parity Validation

> Status: ✅ **Done**  
> Agent prompt: `.github/prompts/wave1.5-osprey-parity.prompt.md`  
> Design context: [17-pdf-to-markdown.md](../design/17-pdf-to-markdown.md)  
> Branch: `feat/osprey-parity` (worktree: `../hermes-parity`)

## Goal

Validate that F# extractors (PdfStructure, CsvExtraction) produce output containing the fields Osprey's proven Python parsers need. 10 real document types tested. Fix any extraction gaps before wiring extractors into the pipeline (Wave 2).

## Tasks

| # | Document type | Extractor | Status |
|---|--------------|-----------|--------|
| O1 | Microsoft payslip (PDF) | PdfStructure | ✅ Pass — Salary, Tax, Net Pay, YTD, SGC extracted |
| O2 | QLD Education payslip (PDF) | PdfStructure | ✅ Pass — Employee ID, earning codes, CID-garbled text parseable |
| O3 | Westpac bank statement (CSV) | CsvExtraction | ⏭ Skip — no CSVs in archive |
| O4 | CBA bank statement (CSV) | CsvExtraction | ⏭ Skip — no CSVs in archive |
| O5 | Ray White rental statement (PDF) | PdfStructure | ✅ Pass — Rent amounts, tables detected |
| O6 | Fidelity dividend (CSV) | CsvExtraction | ⏭ Skip — not in archive |
| O7 | Amazon order history (CSV) | CsvExtraction | ⏭ Skip — not in archive |
| O8 | Utility invoice — AGL/Telstra (PDF) | PdfStructure | ✅ Pass — Dollar amounts, dates extracted |
| O9 | Credit card statement (CSV) | CsvExtraction | ⏭ Skip — not in archive |
| O10 | Insurance renewal (PDF) | PdfStructure | ⏭ Skip — not in archive |

## Result

**4/4 tested PDF types passed with NO extractor fixes needed.** PdfStructure handles Microsoft payslips, QLD Education payslips, rental statements, and utility invoices correctly. 6 CSV types skipped (not available in email archive — they come from manual downloads).

716 tests (710 passed, 6 skipped).

## Log

### April 3, 2026 — Complete
- All 4 available PDF document types validated
- No extractor fixes required — PdfStructure works correctly
- CID-garbled text in QLD Education payslips still parseable
- CSV tests skipped — documents not in email archive (bank/dividend/Amazon CSVs are manual downloads, not email attachments)
- Wave ready for review → unblocks Wave 2
