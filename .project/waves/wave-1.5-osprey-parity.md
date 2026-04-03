# Wave 1.5: Osprey Parity Validation

> Status: ⏳ **Active**  
> Agent prompt: `.github/prompts/wave1.5-osprey-parity.prompt.md`  
> Design context: [17-pdf-to-markdown.md](../design/17-pdf-to-markdown.md)

## Goal

Validate that F# extractors (PdfStructure, CsvExtraction) produce output containing the fields Osprey's proven Python parsers need. 10 real document types tested. Fix any extraction gaps before wiring extractors into the pipeline (Wave 2).

## Tasks

| # | Document type | Extractor | Status |
|---|--------------|-----------|--------|
| O1 | Microsoft payslip (PDF) | PdfStructure | Not started |
| O2 | QLD Education payslip (PDF) | PdfStructure | Not started |
| O3 | Westpac bank statement (CSV) | CsvExtraction | Not started |
| O4 | CBA bank statement (CSV) | CsvExtraction | Not started |
| O5 | Ray White rental statement (PDF) | PdfStructure | Not started |
| O6 | Fidelity dividend (CSV) | CsvExtraction | Not started |
| O7 | Amazon order history (CSV) | CsvExtraction | Not started |
| O8 | Utility invoice — AGL/Telstra (PDF) | PdfStructure | Not started |
| O9 | Credit card statement (CSV) | CsvExtraction | Not started |
| O10 | Insurance renewal (PDF) | PdfStructure | Not started |

## Log

(newest on top — agent appends entries here as work progresses)
