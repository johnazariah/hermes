# Wave 2: Wire Structured Extraction Pipeline

> Status: **Complete**  
> Design doc: [17-pdf-to-markdown.md](../design/17-pdf-to-markdown.md)  
> Master plan: `.github/prompts/development-plan-apr3.prompt.md` (Wave 2 section)

## Goal

Connect the built-but-idle extractors (PdfStructure, Excel, Word, CSV) to the actual processing pipeline. Add `extracted_markdown` column. Wire MCP content tool. Re-extraction CLI command.

## Tasks

| # | Task | Status |
|---|------|--------|
| E1 | Add `extracted_markdown` column (schema v4) | ✅ Done |
| E2 | Wire Extraction.fs → format dispatch → extractors for new documents | ✅ Done |
| E3 | Wire `hermes_get_document_content(format="markdown")` to read column | ✅ Done |
| E4 | `hermes reextract-all` CLI command | ✅ Done |
| E5 | Document detail pane renders extracted_markdown | ✅ Done |

## Log

(newest on top)
