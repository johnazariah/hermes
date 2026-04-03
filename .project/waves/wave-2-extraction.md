# Wave 2: Wire Structured Extraction Pipeline

> Status: **Not started** (blocked on Wave 1.5)  
> Design doc: [17-pdf-to-markdown.md](../design/17-pdf-to-markdown.md)  
> Master plan: `.github/prompts/development-plan-apr3.prompt.md` (Wave 2 section)

## Goal

Connect the built-but-idle extractors (PdfStructure, Excel, Word, CSV) to the actual processing pipeline. Add `extracted_markdown` column. Wire MCP content tool. Re-extraction CLI command.

## Tasks

| # | Task | Status |
|---|------|--------|
| E1 | Add `extracted_markdown` column (schema v4) | Not started |
| E2 | Wire Extraction.fs → format dispatch → extractors for new documents | Not started |
| E3 | Wire `hermes_get_document_content(format="markdown")` to read column | Not started |
| E4 | `hermes reextract-all` CLI command | Not started |
| E5 | Document detail pane renders extracted_markdown | Not started |

## Log

(newest on top)
