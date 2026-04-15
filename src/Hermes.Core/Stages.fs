namespace Hermes.Core

#nowarn "3261"

open System
open System.Threading
open System.Threading.Tasks

/// Pure stage processor functions for the pipeline.
/// Each function: Document.T -> Task<Document.T>
/// No channel logic, no DB writes (that's the workflow monad's job).
[<RequireQualifiedAccess>]
module Stages =

    /// Dependencies injected at the composition root.
    type Deps =
        { Fs: Algebra.FileSystem
          Db: Algebra.Database
          Logger: Algebra.Logger
          Clock: Algebra.Clock
          Extractor: Algebra.TextExtractor
          Embedder: Algebra.EmbeddingClient option
          ChatProvider: Algebra.ChatProvider option
          ContentRules: Domain.ContentRule list
          ComprehensionPrompt: PromptLoader.ParsedPrompt option
          ArchiveDir: string }

    // ─── Extract stage ───────────────────────────────────────────

    /// Extract text from a document file. Returns enriched document with extraction fields.
    let extract (deps: Deps) (doc: Document.T) : Task<Document.T> =
        task {
            let savedPath = doc |> Document.decode<string> "saved_path" |> Option.defaultValue ""
            let docId = Document.id doc
            let fullPath =
                if IO.Path.IsPathRooted(savedPath) then savedPath
                else IO.Path.Combine(deps.ArchiveDir, savedPath)

            if not (deps.Fs.fileExists fullPath) then
                deps.Logger.warn $"Extract: file not found for doc {docId}: {savedPath}"
                return
                    doc
                    |> Document.encode "extraction_method" (box "failed")
                    |> Document.encode "extracted_at" (box (deps.Clock.utcNow().ToString("o")))
                    |> Document.encode "stage" (box "extracted")
            else

            let! bytes = deps.Fs.readAllBytes fullPath
            let! result = Extraction.extractFromBytes deps.Extractor savedPath bytes
            match result with
            | Error e ->
                deps.Logger.warn $"Extract failed for doc {docId}: {e}"
                return
                    doc
                    |> Document.encode "extraction_method" (box "failed")
                    |> Document.encode "extracted_at" (box (deps.Clock.utcNow().ToString("o")))
                    |> Document.encode "stage" (box "extracted")
            | Ok extraction ->
                let now = deps.Clock.utcNow().ToString("o")
                return
                    doc
                    |> Document.encode "extracted_text" (box extraction.Text)
                    |> Document.encode "extracted_markdown" (extraction.Markdown |> Option.map box |> Option.defaultValue (box DBNull.Value))
                    |> Document.encode "extracted_date" (extraction.Date |> Option.map box |> Option.defaultValue (box DBNull.Value))
                    |> Document.encode "extracted_amount" (extraction.Amount |> Option.map (fun d -> box (float d)) |> Option.defaultValue (box DBNull.Value))
                    |> Document.encode "extracted_vendor" (extraction.Vendor |> Option.map box |> Option.defaultValue (box DBNull.Value))
                    |> Document.encode "extracted_abn" (extraction.Abn |> Option.map box |> Option.defaultValue (box DBNull.Value))
                    |> Document.encode "extraction_method" (box extraction.Method)
                    |> Document.encode "ocr_confidence" (extraction.OcrConfidence |> Option.map box |> Option.defaultValue (box DBNull.Value))
                    |> Document.encode "extraction_confidence" (extraction.OcrConfidence |> Option.map box |> Option.defaultValue (box DBNull.Value))
                    |> Document.encode "extracted_at" (box now)
                    |> Document.encode "stage" (box "extracted")
        }

    // ─── Understand stage ──────────────────────────────────────

    /// Maximum characters of document text to send to the LLM.
    let private maxComprehensionChars = 3000

    /// Build context string from extracted metadata.
    let private buildContext (doc: Document.T) : string =
        let vendor = doc |> Document.decode<string> "extracted_vendor" |> Option.defaultValue ""
        let amount = doc |> Document.decode<float> "extracted_amount"
        let sender = doc |> Document.decode<string> "sender" |> Option.defaultValue ""
        let subject = doc |> Document.decode<string> "subject" |> Option.defaultValue ""

        let senderHint =
            if sender <> "" then SenderClassification.classify sender |> SenderClassification.formatHint
            else ""

        let contextParts =
            [ if senderHint <> "" then senderHint
              if vendor <> "" then $"Known vendor: {vendor}"
              if amount.IsSome then $"Detected amount: {amount.Value}"
              if sender <> "" then $"Email sender: {sender}"
              if subject <> "" then $"Email subject: {subject}" ]

        if contextParts.IsEmpty then ""
        else "\nContext from prior extraction:\n" + (contextParts |> String.concat "\n") + "\n"

    /// Fallback prompts when no external prompt file is loaded.
    let private fallbackSystemPrompt =
        "You are a document intelligence system. You read documents and produce structured JSON understanding. Be precise with monetary amounts and dates."

    let private fallbackUserPrompt (text: string) (context: string) : string =
        let truncated =
            if text.Length <= maxComprehensionChars then text
            else text.Substring(0, maxComprehensionChars) + "\n[... truncated]"
        $"""Read the following document text and produce a JSON understanding.
{context}
Include:
- document_type: what kind of document this is
- confidence: 0.0-1.0 how confident you are
- summary: a 1-2 sentence human-readable summary
- fields: an object with the key structured data extracted

Extract all monetary amounts, dates, names, account numbers, and identifiers you can find.
Respond with ONLY a JSON object, no explanation.

Document text:
{truncated}"""

    /// Understand a document: produce structured comprehension via LLM.
    /// Falls back to content rules for fast-path classification when no LLM is available.
    let understand (deps: Deps) (doc: Document.T) : Task<Document.T> =
        let docId = Document.id doc
        let text = doc |> Document.decode<string> "extracted_text" |> Option.defaultValue ""

        let understood category tier confidence =
            doc
            |> Document.encode "category" (box category)
            |> Document.encode "classification_tier" (box tier)
            |> Document.encode "classification_confidence" (box confidence)
            |> Document.encode "stage" (box "understood")

        let passThrough () =
            doc |> Document.encode "stage" (box "understood")

        task {
            if String.IsNullOrWhiteSpace(text) then
                deps.Logger.debug $"Understand skip doc {docId}: empty extracted text"
                return passThrough ()
            else

            // Fast path: content rules (no LLM needed)
            match ContentClassifier.classify text [] None deps.ContentRules with
            | Some (category, confidence) ->
                let canonical = ComprehensionSchema.normaliseCategory category
                deps.Logger.info $"Understood doc {docId} as '{canonical}' via content rules (conf={confidence:F2})"
                return understood canonical "content" confidence

            | None ->
                // LLM comprehension path
                match deps.ChatProvider with
                | None ->
                    deps.Logger.debug $"Understand skip doc {docId}: no chat provider"
                    return passThrough ()

                | Some chat ->
                    let context = buildContext doc

                    let systemPrompt, userPrompt =
                        match deps.ComprehensionPrompt with
                        | Some p -> p.System, PromptLoader.render p text context
                        | None -> fallbackSystemPrompt, fallbackUserPrompt text context

                    let! llmResult = chat.complete systemPrompt userPrompt
                    match llmResult with
                    | Error e ->
                        deps.Logger.warn $"Comprehension failed for doc {docId}: {e}"
                        return passThrough ()

                    | Ok response ->
                        match ComprehensionSchema.normaliseResponse response with
                        | Ok parsed ->
                            let tier = if parsed.Confidence >= 0.7 then "comprehension" else "comprehension_review"
                            deps.Logger.info $"Understood doc {docId} as '{parsed.CanonicalCategory}' ({parsed.DocumentType}, {tier}, conf={parsed.Confidence:F2}): {parsed.Summary}"
                            return
                                doc
                                |> Document.encode "category" (box parsed.CanonicalCategory)
                                |> Document.encode "classification_tier" (box tier)
                                |> Document.encode "classification_confidence" (box parsed.Confidence)
                                |> Document.encode "comprehension" (box parsed.RawJson)
                                |> Document.encode "comprehension_schema" (box "v2")
                                |> Document.encode "stage" (box "understood")

                        | Error parseErr ->
                            let preview = response.[..min 200 (response.Length - 1)]
                            deps.Logger.warn $"Understand doc {docId}: {parseErr}: {preview}"
                            return passThrough ()
        }

    // ─── Embed stage ─────────────────────────────────────────────

    /// Generate embeddings for a document's extracted text.
    let embed (deps: Deps) (doc: Document.T) : Task<Document.T> =
        task {
            let docId = Document.id doc
            let text = doc |> Document.decode<string> "extracted_text" |> Option.defaultValue ""

            match deps.Embedder with
            | None ->
                deps.Logger.debug $"Embed skip doc {docId}: no embedder configured"
                return doc |> Document.encode "stage" (box "embedded")
            | Some embedder ->
                let! available = embedder.isAvailable ()
                if not available then
                    return failwith $"Embedding service unavailable for doc {docId}"
                elif String.IsNullOrWhiteSpace(text) then
                    deps.Logger.debug $"Embed skip doc {docId}: no text to embed"
                    return
                        doc
                        |> Document.encode "embedded_at" (box (deps.Clock.utcNow().ToString("o")))
                        |> Document.encode "chunk_count" (box 0L)
                        |> Document.encode "stage" (box "embedded")
                else
                    let! result = Embeddings.embedDocument deps.Db deps.Logger deps.Clock embedder docId text
                    match result with
                    | Ok chunkCount ->
                        return
                            doc
                            |> Document.encode "embedded_at" (box (deps.Clock.utcNow().ToString("o")))
                            |> Document.encode "chunk_count" (box (int64 chunkCount))
                            |> Document.encode "stage" (box "embedded")
                    | Error e ->
                        return failwith $"Embedding failed for doc {docId}: {e}"
        }

    // ─── Stage definitions ───────────────────────────────────────

    /// Build the three standard pipeline stage definitions.
    /// resourceLock: shared GPU mutex (Some for Ollama, None for Azure/no contention)
    /// maxHoldTime: burst duration before yielding the lock
    let standardStages (deps: Deps) (resourceLock: SemaphoreSlim option) (maxHoldTime: TimeSpan) : Workflow.StageDefinition list =
        [ { Name = "extract"
            OutputKey = "extracted_at"
            RequiredKeys = [ "saved_path" ]
            Process = extract deps
            ResourceLock = None          // CPU-only, no GPU contention
            MaxHoldTime = TimeSpan.Zero }

          { Name = "understand"
            OutputKey = "comprehension_schema"
            RequiredKeys = [ "extracted_text" ]
            Process = understand deps
            ResourceLock = resourceLock   // shares GPU with embed
            MaxHoldTime = maxHoldTime }

          { Name = "embed"
            OutputKey = "embedded_at"
            RequiredKeys = [ "extracted_text" ]
            Process = embed deps
            ResourceLock = resourceLock   // shares GPU with understand
            MaxHoldTime = maxHoldTime } ]
