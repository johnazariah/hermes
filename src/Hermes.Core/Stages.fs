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

    // ─── Classify stage ──────────────────────────────────────────

    /// Classify a document using content rules and LLM fallback.
    /// Category is metadata only — files stay where they are.
    let classify (deps: Deps) (doc: Document.T) : Task<Document.T> =
        let docId = Document.id doc
        let text = doc |> Document.decode<string> "extracted_text" |> Option.defaultValue ""

        let classified category tier confidence =
            doc
            |> Document.encode "category" (box category)
            |> Document.encode "classification_tier" (box tier)
            |> Document.encode "classification_confidence" (box confidence)
            |> Document.encode "stage" (box "classified")

        let passThrough () =
            doc |> Document.encode "stage" (box "classified")

        task {
            if String.IsNullOrWhiteSpace(text) then
                deps.Logger.debug $"Classify skip doc {docId}: empty extracted text"
                return passThrough ()
            else

            // Fast path: content rules (no LLM)
            match ContentClassifier.classify text [] None deps.ContentRules with
            | Some (category, confidence) ->
                deps.Logger.info $"Classified doc {docId} as '{category}' via content rules (conf={confidence:F2})"
                return classified category "content" confidence

            | None ->
                // Slow path: LLM fallback
                match deps.ChatProvider with
                | None ->
                    deps.Logger.debug $"Classify skip doc {docId}: no chat provider"
                    return passThrough ()

                | Some chat ->
                    let! catRows = deps.Db.execReader "SELECT DISTINCT category FROM documents WHERE category NOT IN ('unsorted','unclassified') LIMIT 50" []
                    let existingCats = catRows |> List.choose (fun r -> Prelude.RowReader(r).OptString "category")
                    let seedCats = [ "invoices"; "bank-statements"; "receipts"; "tax"; "payslips"; "insurance"; "real-estate"; "travel"; "medical"; "utilities"; "legal"; "donations"; "contracts"; "correspondence" ]
                    let allCats = (existingCats @ seedCats) |> List.distinct
                    let prompt = ContentClassifier.buildClassificationPrompt text allCats

                    let! llmResult = chat.complete "You are a document classifier." prompt
                    match llmResult with
                    | Error e ->
                        deps.Logger.warn $"LLM classification failed for doc {docId}: {e}"
                        return passThrough ()

                    | Ok response ->
                        match ContentClassifier.parseClassificationResponse response with
                        | Some (category, confidence, reasoning) when confidence >= 0.4 ->
                            let tier = if confidence >= 0.7 then "llm" else "llm_review"
                            deps.Logger.info $"Classified doc {docId} as '{category}' ({tier}, conf={confidence:F2}): {reasoning}"
                            return classified category tier confidence

                        | Some (_, confidence, _) ->
                            deps.Logger.info $"Classify doc {docId}: LLM confidence too low ({confidence:F2})"
                            return passThrough ()

                        | None ->
                            let preview = response.[..min 200 (response.Length - 1)]
                            deps.Logger.warn $"Classify doc {docId}: could not parse LLM response: {preview}"
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

          { Name = "classify"
            OutputKey = "classification_tier"
            RequiredKeys = [ "extracted_text" ]
            Process = classify deps
            ResourceLock = resourceLock   // shares GPU with embed
            MaxHoldTime = maxHoldTime }

          { Name = "embed"
            OutputKey = "embedded_at"
            RequiredKeys = [ "extracted_text" ]
            Process = embed deps
            ResourceLock = resourceLock   // shares GPU with classify
            MaxHoldTime = maxHoldTime } ]
