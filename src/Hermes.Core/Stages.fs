namespace Hermes.Core

#nowarn "3261"

open System
open System.Threading
open System.Threading.Tasks
open System.Text.Json

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
          TriageProvider: Algebra.ChatProvider option
          ContentRules: Domain.ContentRule list
          ComprehensionPrompt: PromptLoader.ParsedPrompt option
          TriagePrompt: PromptLoader.ParsedPrompt option
          Preferences: string
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

                // Write extraction to file alongside source (file-first archive)
                let markdownContent = extraction.Markdown |> Option.defaultValue extraction.Text
                try do! ArchiveWriter.writeExtraction deps.Fs fullPath markdownContent
                with ex -> deps.Logger.debug $"Extract file write failed for doc {docId}: {ex.Message}"

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

    /// Read thread context: all message .md files and extraction .md files from the thread folder.
    /// Returns a combined context string for thread-level comprehension.
    let private readThreadContext (fs: Algebra.FileSystem) (archiveDir: string) (savedPath: string) : Task<string> =
        task {
            let fullPath =
                if IO.Path.IsPathRooted(savedPath) then savedPath
                else IO.Path.Combine(archiveDir, savedPath)
            let folderPath = IO.Path.GetDirectoryName(fullPath) |> Option.ofObj |> Option.defaultValue ""
            if String.IsNullOrWhiteSpace(folderPath) || not (fs.directoryExists folderPath) then
                return ""
            else
                try
                    let! messages = ArchiveWriter.readThreadMessages fs folderPath
                    let threadText =
                        if messages.IsEmpty then ""
                        else
                            let combined = messages |> String.concat "\n\n---\n\n"
                            $"\nThread messages ({messages.Length}):\n{combined}\n"
                    return threadText
                with _ -> return ""
        }

    // ─── Retrieval-augmented comprehension ───────────────────────

    /// Extract domain from an email sender like "Name <user@example.com>".
    /// Delegates to ArchiveWriter's implementation for consistency with folder paths.
    let internal extractSenderDomain (sender: string) : string option =
        match ArchiveWriter.extractSenderDomain sender with
        | "unknown" -> None
        | domain -> Some domain

    /// Extract a compact schema hint (document_type + field_names only, no values).
    let internal compactSchemaHint (comprehensionJson: string) : string option =
        try
            use jdoc = JsonDocument.Parse(comprehensionJson)
            let root = jdoc.RootElement
            let docType =
                match root.TryGetProperty("document_type") with
                | true, el -> el.GetString()
                | _ -> "unknown"
            let fieldNames =
                match root.TryGetProperty("fields") with
                | true, el when el.ValueKind = JsonValueKind.Object ->
                    el.EnumerateObject()
                    |> Seq.map (fun p -> $"\"{p.Name}\"")
                    |> String.concat ","
                | _ -> ""
            let hint = $"""{{"document_type":"{docType}","field_names":[{fieldNames}]}}"""
            if hint.Length > 300 then Some hint.[..299] else Some hint
        with _ -> None

    /// Find 1–2 past high-confidence comprehension schema hints for similar documents.
    /// Match by sender domain (primary) or extracted vendor (fallback).
    let private findExamples (db: Algebra.Database) (doc: Document.T) : Task<string list> =
        let docId = Document.id doc

        let extractHints (rows: Map<string, obj> list) =
            rows
            |> List.choose (fun r ->
                r
                |> Map.tryFind "comprehension"
                |> Option.map string
                |> Option.bind compactSchemaHint)

        let querySender domain =
            db.execReader
                """SELECT comprehension FROM documents
                   WHERE comprehension IS NOT NULL
                   AND id <> @docId
                   AND sender LIKE @pattern
                   AND classification_confidence >= 0.7
                   ORDER BY extracted_at DESC
                   LIMIT 2"""
                [ ("@pattern", Database.boxVal $"%%@{domain}%%")
                  ("@docId",   Database.boxVal docId) ]

        let queryVendor vendor =
            db.execReader
                """SELECT comprehension FROM documents
                   WHERE comprehension IS NOT NULL
                   AND id <> @docId
                   AND extracted_vendor = @vendor
                   AND classification_confidence >= 0.7
                   ORDER BY extracted_at DESC
                   LIMIT 2"""
                [ ("@vendor", Database.boxVal vendor)
                  ("@docId",  Database.boxVal docId) ]

        task {
            let senderDomain =
                doc
                |> Document.decode<string> "sender"
                |> Option.bind extractSenderDomain

            let! domainHints =
                match senderDomain with
                | Some domain -> task { let! rows = querySender domain in return extractHints rows }
                | None -> Task.FromResult([])

            if domainHints.Length > 0 then
                return domainHints
            else
                let vendor =
                    doc
                    |> Document.decode<string> "extracted_vendor"
                    |> Option.defaultValue ""

                if String.IsNullOrWhiteSpace(vendor) then
                    return []
                else
                    let! rows = queryVendor vendor
                    return extractHints rows
        }

    let private addPreferences (preferences: string) (context: string) =
        if String.IsNullOrWhiteSpace preferences then
            context
        else
            $"\nUser preferences:\n{preferences}\n{context}"

    let private appendSchemaHints (context: string) (examples: string list) =
        match examples with
        | [] -> context
        | hints ->
            hints
            |> List.mapi (fun index hint ->
                $"Schema hint from similar document #{index + 1}: {hint}")
            |> String.concat "\n"
            |> fun text -> $"{context}\n{text}\n"

    let private augmentComprehensionContext
        (db: Algebra.Database)
        (preferences: string)
        (doc: Document.T)
        : Task<string> =
        task {
            let context =
                doc
                |> buildContext
                |> addPreferences preferences

            let! examples = findExamples db doc
            return appendSchemaHints context examples
        }

    /// Record a sender→document_type pattern for RAC knowledge accumulation.
    let private upsertLearnedPattern
        (db: Algebra.Database)
        (senderDomain: string)
        (documentType: string)
        (confidence: float)
        : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO learned_patterns (sender_domain, document_type, count, avg_confidence, last_seen)
                       VALUES (@domain, @type, 1, @conf, datetime('now'))
                       ON CONFLICT(sender_domain, document_type) DO UPDATE SET
                           count = count + 1,
                           avg_confidence = (avg_confidence * count + @conf) / (count + 1),
                           last_seen = datetime('now')"""
                    [ ("@domain", Database.boxVal senderDomain)
                      ("@type", Database.boxVal documentType)
                      ("@conf", Database.boxVal confidence) ]

            return ()
        }

    /// Create a review suggestion when comprehension confidence is below threshold.
    let private createSuggestion
        (db: Algebra.Database)
        (docId: int64)
        (proposedCategory: string)
        (currentCategory: string option)
        (confidence: float)
        : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO suggestions
                       (document_id, proposed_category, current_category, confidence)
                       VALUES (@docId, @proposed, @current, @conf)"""
                    [ ("@docId", Database.boxVal docId)
                      ("@proposed", Database.boxVal proposedCategory)
                      ("@current",
                       currentCategory
                       |> Option.defaultValue ""
                       |> Database.boxVal)
                      ("@conf", Database.boxVal confidence) ]

            return ()
        }

    let private learnFromResult
        (deps: Deps)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        task {
            try
                match
                    doc
                    |> Document.decode<string> "sender"
                    |> Option.bind extractSenderDomain
                with
                | Some domain ->
                    do!
                        upsertLearnedPattern
                            deps.Db
                            domain
                            parsed.DocumentType
                            parsed.Confidence
                | None -> ()
            with ex ->
                deps.Logger.debug
                    $"Learned pattern upsert failed for doc {Document.id doc}: {ex.Message}"
        }

    let private suggestReview
        (deps: Deps)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        task {
            if parsed.Confidence < 0.7 then
                try
                    let currentCategory =
                        doc |> Document.decode<string> "category"

                    do!
                        createSuggestion
                            deps.Db
                            (Document.id doc)
                            parsed.CanonicalCategory
                            currentCategory
                            parsed.Confidence
                with ex ->
                    deps.Logger.debug
                        $"Suggestion creation failed for doc {Document.id doc}: {ex.Message}"
        }

    let private recordReviewSignals deps doc parsed : Task<unit> =
        task {
            do! learnFromResult deps doc parsed
            do! suggestReview deps doc parsed
        }

    let private archiveFolderPath (deps: Deps) (doc: Document.T) =
        doc
        |> Document.decode<string> "saved_path"
        |> Option.filter (fun path -> not (String.IsNullOrWhiteSpace path))
        |> Option.map (fun path ->
            if IO.Path.IsPathRooted path then path
            else IO.Path.Combine(deps.ArchiveDir, path))
        |> Option.bind (fun path ->
            IO.Path.GetDirectoryName(path)
            |> Option.ofObj
            |> Option.filter (fun folder -> not (String.IsNullOrWhiteSpace folder)))

    let private writeComprehensionArtifact
        (deps: Deps)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        task {
            try
                match archiveFolderPath deps doc with
                | Some folder ->
                    do! ArchiveWriter.writeComprehension deps.Fs folder parsed.RawJson
                | None -> ()
            with ex ->
                deps.Logger.debug
                    $"Comprehension file write failed for doc {Document.id doc}: {ex.Message}"
        }

    let private insertComprehensionTag
        (db: Algebra.Database)
        (docId: int64)
        (confidence: float)
        (tag: string)
        : Task<unit> =
        task {
            let! _ =
                db.execNonQuery
                    """INSERT OR IGNORE INTO tags
                       (document_id, tag, source, confidence)
                       VALUES (@docId, @tag, 'comprehension', @confidence)"""
                    [ ("@docId", Database.boxVal docId)
                      ("@tag", Database.boxVal tag)
                      ("@confidence", Database.boxVal confidence) ]

            return ()
        }

    let private writeComprehensionTags
        (deps: Deps)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        task {
            try
                do!
                    parsed.Tags
                    |> Prelude.foldTask
                        (fun () tag ->
                            insertComprehensionTag
                                deps.Db
                                (Document.id doc)
                                parsed.Confidence
                                tag)
                        ()
            with ex ->
                deps.Logger.debug
                    $"Tag write failed for doc {Document.id doc}: {ex.Message}"
        }

    let private recordFinalComprehension deps doc parsed : Task<unit> =
        task {
            do! recordReviewSignals deps doc parsed
            do! writeComprehensionArtifact deps doc parsed
            do! writeComprehensionTags deps doc parsed
        }

    let private confidenceTier prefix confidence =
        if confidence >= 0.7 then prefix
        else $"{prefix}_review"

    let private withComprehension stage tier
        (parsed: ComprehensionSchema.NormalisedResponse)
        (doc: Document.T) =
        doc
        |> Document.encode "category" (box parsed.CanonicalCategory)
        |> Document.encode "classification_tier" (box tier)
        |> Document.encode "classification_confidence" (box parsed.Confidence)
        |> Document.encode "comprehension" (box parsed.RawJson)
        |> Document.encode "comprehension_schema" (box "v2")
        |> Document.encode "stage" (box stage)

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

    /// Categories that warrant detailed extraction with the full model.
    let private financialCategories =
        set [ "receipts"; "payslips"; "invoices"; "bank-statements"; "tax"
              "utilities"; "insurance"; "superannuation"; "medical"
              "property"; "rates-and-tax"; "donations"; "dividends"
              "espp"; "stock-vests"; "legal"; "finance-alerts" ]

    /// Fast triage prompt — classify only, no detailed extraction.
    let private triageSystemPrompt =
        "You are a precise document classifier for an Australian household. You classify documents based on their content, sender, and subject line. Respond with ONLY a JSON object — no explanation, no markdown fencing."

    let private triageUserPrompt (text: string) (context: string) : string =
        let truncated =
            if text.Length <= 2000 then text
            else text.Substring(0, 2000) + "\n[... truncated]"
        $"""Classify this document into exactly one type. Use the sender, subject, and content as signals.

IMPORTANT classification rules:
- "Order Received", "Order Confirmation", "Your Receipt", "Payment Receipt", "Invoice" → expense-receipt
- Emails from payment processors (PayPal, Stripe, Square) about payments → expense-receipt
- Emails from restaurants, food delivery, retailers about orders → expense-receipt
- GitHub/CI/CD notifications, automated alerts, status updates → notification
- Marketing emails, newsletters, promotions, deals → notification
- Personal or business correspondence → letter
- Bank/credit card transaction listings → bank-statement
- Salary/wage payment summaries → payslip
- Rental property management statements → agent-statement
- Bills for water, electricity, gas, internet, phone → utility-bill
- Insurance documents → insurance-policy
- Super fund statements → superannuation
- Medical bills, Medicare, health fund → medical
- Tax documents, ATO notices → tax-return
- Stock/RSU vesting confirmations → stock-vest
- Dividend notices → dividend-statement
- Contracts, legal docs → legal

Respond with ONLY this JSON (no other text):
{{"document_type": "<type>", "confidence": <0.0-1.0>, "summary": "<one specific sentence>"}}

Valid types: payslip, agent-statement, bank-statement, mortgage-statement, depreciation-schedule, donation-receipt, insurance-policy, utility-bill, council-rates, land-tax, tax-return, payg-instalment, stock-vest, espp-statement, dividend-statement, expense-receipt, medical, legal, vehicle, superannuation, letter, notification, report, other
{context}
Document text:
{truncated}"""

    /// Understand a document using two-phase approach:
    /// Phase 1 (triage): fast classification with small model → sets stage to "understood" or "triaged"
    /// Phase 2 (deepComprehend): full extraction with large model → only for "triaged" (financial) docs
    let triage (deps: Deps) (doc: Document.T) : Task<Document.T> =
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
                deps.Logger.debug $"Triage skip doc {docId}: empty extracted text"
                return passThrough ()
            else

            // Fast path: content rules (no LLM needed)
            match ContentClassifier.classify text [] None deps.ContentRules with
            | Some (category, confidence) ->
                let canonical = ComprehensionSchema.normaliseCategory category
                deps.Logger.info $"Understood doc {docId} as '{canonical}' via content rules (conf={confidence:F2})"
                return understood canonical "content" confidence

            | None ->
                let triageChat = deps.TriageProvider |> Option.orElse deps.ChatProvider
                match triageChat with
                | None ->
                    deps.Logger.debug $"Triage skip doc {docId}: no chat provider"
                    return passThrough ()

                | Some chat ->
                    let context =
                        doc
                        |> buildContext
                        |> addPreferences deps.Preferences

                    let triageSys, triageUser =
                        match deps.TriagePrompt with
                        | Some prompt ->
                            prompt.System, PromptLoader.renderTriage prompt text context
                        | None ->
                            triageSystemPrompt, triageUserPrompt text context

                    let! triageResult = chat.complete triageSys triageUser

                    match triageResult with
                    | Error e ->
                        deps.Logger.warn $"Triage failed for doc {docId}: {e}"
                        return passThrough ()

                    | Ok triageResponse ->
                        match ComprehensionSchema.normaliseResponse triageResponse with
                        | Error parseErr ->
                            let preview = triageResponse.[..min 200 (triageResponse.Length - 1)]
                            deps.Logger.warn $"Triage parse doc {docId}: {parseErr}: {preview}"
                            return passThrough ()

                        | Ok triaged ->
                            let canonical = triaged.CanonicalCategory
                            let sender = doc |> Document.decode<string> "sender"
                            do! ContactExtraction.harvestAndLink deps.Db deps.Logger docId triaged.RawJson sender

                            if financialCategories.Contains canonical then
                                // Financial doc → mark for deep comprehension
                                let tier = if triaged.Confidence >= 0.7 then "triage" else "triage_review"
                                deps.Logger.info $"Triaged doc {docId} as '{canonical}' ({tier}, conf={triaged.Confidence:F2}) → queued for deep comprehension: {triaged.Summary}"
                                return
                                    doc
                                    |> Document.encode "category" (box canonical)
                                    |> Document.encode "classification_tier" (box tier)
                                    |> Document.encode "classification_confidence" (box triaged.Confidence)
                                    |> Document.encode "comprehension" (box triaged.RawJson)
                                    |> Document.encode "comprehension_schema" (box "v2")
                                    |> Document.encode "stage" (box "triaged")
                            else
                                // Non-financial triage is the final comprehension decision.
                                do! recordFinalComprehension deps doc triaged

                                let tier =
                                    if triaged.Confidence >= 0.7 then
                                        "triage"
                                    else
                                        "triage_review"

                                deps.Logger.info
                                    $"Triaged doc {docId} as '{canonical}' ({tier}, conf={triaged.Confidence:F2}): {triaged.Summary}"

                                return
                                    doc
                                    |> Document.encode "category" (box canonical)
                                    |> Document.encode "classification_tier" (box tier)
                                    |> Document.encode "classification_confidence" (box triaged.Confidence)
                                    |> Document.encode "comprehension" (box triaged.RawJson)
                                    |> Document.encode "comprehension_schema" (box "v2")
                                    |> Document.encode "stage" (box "understood")
        }

    let private buildDeepContext (deps: Deps) (doc: Document.T) : Task<string> =
        task {
            let! documentContext =
                augmentComprehensionContext deps.Db deps.Preferences doc

            let savedPath =
                doc
                |> Document.decode<string> "saved_path"
                |> Option.defaultValue ""

            let! threadContext =
                readThreadContext deps.Fs deps.ArchiveDir savedPath

            return
                [ documentContext; threadContext ]
                |> List.filter (fun value -> not (String.IsNullOrWhiteSpace value))
                |> String.concat "\n"
        }

    let private comprehensionPrompts deps text context =
        match deps.ComprehensionPrompt with
        | Some prompt ->
            prompt.System, PromptLoader.render prompt text context
        | None ->
            fallbackSystemPrompt, fallbackUserPrompt text context

    let private completeFromTriage deps doc : Task<Document.T> =
        task {
            match doc |> Document.decode<string> "comprehension" with
            | Some json ->
                match ComprehensionSchema.normaliseResponse json with
                | Ok parsed -> do! recordFinalComprehension deps doc parsed
                | Error _ -> ()
            | None -> ()

            return doc |> Document.encode "stage" (box "understood")
        }

    let private applyDeepResult
        (deps: Deps)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<Document.T> =
        task {
            let docId = Document.id doc
            let sender = doc |> Document.decode<string> "sender"
            let tier = confidenceTier "comprehension" parsed.Confidence

            do! ContactExtraction.harvestAndLink deps.Db deps.Logger docId parsed.RawJson sender
            do! recordFinalComprehension deps doc parsed

            deps.Logger.info
                $"Understood doc {docId} as '{parsed.CanonicalCategory}' ({parsed.DocumentType}, {tier}, conf={parsed.Confidence:F2}): {parsed.Summary}"

            return doc |> withComprehension "understood" tier parsed
        }

    let private handleDeepResponse deps doc response : Task<Document.T> =
        match ComprehensionSchema.normaliseResponse response with
        | Ok parsed ->
            applyDeepResult deps doc parsed
        | Error parseError ->
            deps.Logger.warn
                $"Deep comprehension parse doc {Document.id doc}: {parseError}, keeping triage"

            completeFromTriage deps doc

    /// Phase 2: Deep comprehension for financially relevant documents.
    let deepComprehend (deps: Deps) (doc: Document.T) : Task<Document.T> =
        task {
            let docId = Document.id doc
            let text =
                doc
                |> Document.decode<string> "extracted_text"
                |> Option.defaultValue ""

            match deps.ChatProvider with
            | None ->
                deps.Logger.debug $"DeepComprehend skip doc {docId}: no chat provider"
                return! completeFromTriage deps doc
            | Some chat ->
                let! context = buildDeepContext deps doc
                let systemPrompt, userPrompt =
                    comprehensionPrompts deps text context

                let! result = chat.complete systemPrompt userPrompt

                match result with
                | Ok response ->
                    return! handleDeepResponse deps doc response
                | Error error ->
                    deps.Logger.warn
                        $"Deep comprehension failed for doc {docId}: {error}, keeping triage result"

                    return! completeFromTriage deps doc
        }

    // ─── Suggestion approval ─────────────────────────────────────

    /// Approve a suggestion: update the document's category and record a learned pattern.
    let approveSuggestion (db: Algebra.Database) (suggestionId: int64) : Task<Result<unit, string>> =
        task {
            try
                let! rows =
                    db.execReader
                        "SELECT document_id, proposed_category, confidence FROM suggestions WHERE id = @id AND status = 'pending'"
                        [("@id", Database.boxVal suggestionId)]
                match rows with
                | [] -> return Error "Suggestion not found or already resolved"
                | row :: _ ->
                    let docId = row.["document_id"] :?> int64
                    let category = row.["proposed_category"] |> string
                    let confidence = row.["confidence"] :?> float

                    let! _ =
                        db.execNonQuery
                            "UPDATE documents SET category = @cat WHERE id = @id"
                            [("@cat", Database.boxVal category); ("@id", Database.boxVal docId)]

                    let! docRows =
                        db.execReader
                            "SELECT sender FROM documents WHERE id = @id"
                            [("@id", Database.boxVal docId)]
                    match docRows with
                    | docRow :: _ ->
                        let sender = docRow |> Map.tryFind "sender" |> Option.map string |> Option.defaultValue ""
                        match extractSenderDomain sender with
                        | Some domain -> do! upsertLearnedPattern db domain category confidence
                        | None -> ()
                    | _ -> ()

                    let! _ =
                        db.execNonQuery
                            "UPDATE suggestions SET status = 'approved', resolved_at = datetime('now') WHERE id = @id"
                            [("@id", Database.boxVal suggestionId)]
                    return Ok ()
            with ex ->
                return Error $"Approval failed: {ex.Message}"
        }

    /// Reject a suggestion.
    let rejectSuggestion (db: Algebra.Database) (suggestionId: int64) : Task<Result<unit, string>> =
        task {
            try
                let! affected =
                    db.execNonQuery
                        "UPDATE suggestions SET status = 'rejected', resolved_at = datetime('now') WHERE id = @id AND status = 'pending'"
                        [("@id", Database.boxVal suggestionId)]
                if affected > 0 then return Ok ()
                else return Error "Suggestion not found or already resolved"
            with ex ->
                return Error $"Rejection failed: {ex.Message}"
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

    /// Build the four standard pipeline stage definitions.
    /// resourceLock: shared GPU mutex (Some for Ollama, None for Azure/no contention)
    /// maxHoldTime: burst duration before yielding the lock
    let standardStages (deps: Deps) (resourceLock: SemaphoreSlim option) (maxHoldTime: TimeSpan) : Workflow.StageDefinition list =
        [ { Name = "extract"
            OutputKey = "extracted_at"
            RequiredKeys = [ "saved_path" ]
            Process = extract deps
            ResourceLock = None          // CPU-only, no GPU contention
            MaxHoldTime = TimeSpan.Zero }

          { Name = "triage"
            OutputKey = "comprehension_schema"
            RequiredKeys = [ "extracted_text" ]
            Process = triage deps
            ResourceLock = resourceLock   // uses GPU (small model)
            MaxHoldTime = maxHoldTime }

          { Name = "understand"
            OutputKey = "comprehension_schema"
            RequiredKeys = [ "extracted_text" ]
            Process = deepComprehend deps
            ResourceLock = resourceLock   // uses GPU (large model)
            MaxHoldTime = maxHoldTime }

          { Name = "embed"
            OutputKey = "embedded_at"
            RequiredKeys = [ "extracted_text" ]
            Process = embed deps
            ResourceLock = resourceLock   // shares GPU with understand
            MaxHoldTime = maxHoldTime } ]
