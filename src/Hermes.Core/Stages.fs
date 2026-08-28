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

    type private ExtractionArtifact =
        { FullPath: string
          Markdown: string
          Enriched: Document.T }

    let private archivePath (deps: Deps) (doc: Document.T) =
        let savedPath =
            doc
            |> Document.decode<string> "saved_path"
            |> Option.defaultValue ""
        if IO.Path.IsPathRooted savedPath then savedPath
        else IO.Path.Combine(deps.ArchiveDir, savedPath)

    let private enrichExtraction
        (now: string)
        (extraction: Domain.ExtractionResult)
        (doc: Document.T) =
        doc
        |> Document.encode "extracted_date" (extraction.Date |> Option.map box |> Option.defaultValue (box DBNull.Value))
        |> Document.encode "extracted_amount" (extraction.Amount |> Option.map (fun d -> box (float d)) |> Option.defaultValue (box DBNull.Value))
        |> Document.encode "extracted_vendor" (extraction.Vendor |> Option.map box |> Option.defaultValue (box DBNull.Value))
        |> Document.encode "extracted_abn" (extraction.Abn |> Option.map box |> Option.defaultValue (box DBNull.Value))
        |> Document.encode "extraction_method" (box extraction.Method)
        |> Document.encode "ocr_confidence" (extraction.OcrConfidence |> Option.map box |> Option.defaultValue (box DBNull.Value))
        |> Document.encode "extraction_confidence" (extraction.OcrConfidence |> Option.map box |> Option.defaultValue (box DBNull.Value))
        |> Document.encode "extracted_at" (box now)
        |> Document.encode "stage" (box "extracted")

    let private createExtractionArtifact
        (deps: Deps)
        (doc: Document.T)
        : Task<ExtractionArtifact> =
        task {
            let savedPath = doc |> Document.decode<string> "saved_path" |> Option.defaultValue ""
            let docId = Document.id doc
            let fullPath = archivePath deps doc

            if not (deps.Fs.fileExists fullPath) then
                deps.Logger.warn $"Extract: file not found for doc {docId}: {savedPath}"
                return failwith $"Extract failed for doc {docId}: source file not found: {savedPath}"
            else

            let! bytes = deps.Fs.readAllBytes fullPath
            let! result = Extraction.extractFromBytes deps.Extractor savedPath bytes
            match result with
            | Error e ->
                deps.Logger.warn $"Extract failed for doc {docId}: {e}"
                return failwith $"Extraction failed for doc {docId}: {e}"
            | Ok extraction ->
                let now = deps.Clock.utcNow().ToString("o")
                let markdownContent = extraction.Markdown |> Option.defaultValue extraction.Text
                return
                    { FullPath = fullPath
                      Markdown = markdownContent
                      Enriched = enrichExtraction now extraction doc }
        }

    let private extractionFolder deps doc =
        let savedPath =
            doc
            |> Document.decode<string> "saved_path"
            |> Option.defaultValue ""
        let folderPath = doc |> Document.decode<string> "folder_path"
        PublicationFence.ArtifactFolder.tryFromMetadata
            deps.ArchiveDir savedPath folderPath

    let private publishExtractionArtifact
        (deps: Deps)
        (generation: Generation.Token)
        (doc: Document.T)
        (artifact: ExtractionArtifact)
        : Task<unit> =
        task {
            match extractionFolder deps doc with
            | None ->
                return
                    invalidOp
                        $"Extraction write failed for doc {Document.id doc}: archive folder not found"
            | Some folder ->
                match!
                    Generation.publishEffect
                        deps.Db generation folder (fun () ->
                            ArchiveWriter.writeExtraction
                                deps.Fs artifact.FullPath artifact.Markdown)
                with
                | Generation.Published () -> return ()
                | Generation.Superseded ->
                    return
                        invalidOp
                            $"Extraction for doc {Document.id doc} was superseded by reflow"
        }

    let internal extractAt
        (generation: Generation.Token)
        (deps: Deps)
        (doc: Document.T)
        : Task<Document.T> =
        task {
            let! artifact = createExtractionArtifact deps doc
            do! publishExtractionArtifact deps generation doc artifact
            return artifact.Enriched
        }

    /// Extract text from a document file. Returns enriched document with extraction fields.
    let extract (deps: Deps) (doc: Document.T) : Task<Document.T> =
        task {
            let! generation =
                Generation.current deps.Db (Document.id doc)
            return! extractAt generation deps doc
        }

    // ─── Understand stage ──────────────────────────────────────

    /// Maximum characters of document text to send to the LLM.
    let private maxComprehensionChars = 3000

    let private archiveFilePath (deps: Deps) (doc: Document.T) =
        let savedPath =
            doc
            |> Document.decode<string> "saved_path"
            |> Option.defaultValue ""

        if IO.Path.IsPathRooted savedPath then savedPath
        else IO.Path.Combine(deps.ArchiveDir, savedPath)

    let private readExtractedText deps doc : Task<string> =
        task {
            let! result =
                doc
                |> archiveFilePath deps
                |> ArchiveWriter.readExtraction deps.Fs

            return result |> Option.defaultValue ""
        }

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

    [<Literal>]
    let private triageStageName = "triage"

    [<Literal>]
    let private deepComprehendStageName = "deep-comprehend"

    type private ExampleMatch =
        | SenderDomain of string
        | Vendor of string

    type private ExampleCandidate =
        { DocumentId: int64
          FolderPath: string
          Folder: PublicationFence.ArtifactFolder }

    let private exampleCandidate archiveDir (row: Map<string, obj>) =
        let reader = Prelude.RowReader(row)
        let savedPath = reader.String "saved_path" ""
        let folderPath = reader.OptString "folder_path"
        match
            PublicationFence.ArtifactFolder.tryFromMetadata
                archiveDir savedPath folderPath
        with
        | None -> None
        | Some folder ->
            match PublicationFence.ArtifactFolder.resolve archiveDir folder with
            | Error _ -> None
            | Ok resolved ->
                Some
                    { DocumentId = reader.Int64 "document_id" 0L
                      FolderPath = resolved
                      Folder = folder }

    let private hasCurrentComprehension
        (db: Algebra.Database)
        (documentId: int64)
        : Task<bool> =
        task {
            let! value =
                db.execScalar
                    """SELECT 1
                       FROM stage_completions sc
                       JOIN comprehension c ON c.document_id = sc.document_id
                       WHERE sc.document_id = @doc
                         AND sc.stage_name = @stage
                       LIMIT 1"""
                    [ ("@doc", Database.boxVal documentId)
                      ("@stage", Database.boxVal deepComprehendStageName) ]
            return match value with null -> false | _ -> true
        }

    let private validateCurrentHint
        (db: Algebra.Database)
        (candidate: ExampleCandidate)
        (json: string)
        : Task<string option> =
        task {
            let! current =
                hasCurrentComprehension db candidate.DocumentId
            return if current then compactSchemaHint json else None
        }

    let private readCurrentHint
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (candidate: ExampleCandidate)
        : Task<string option> =
        task {
            let! generation =
                Generation.current db candidate.DocumentId
            let! publication =
                Generation.readArtifactStable
                    db generation candidate.Folder (fun () ->
                        task {
                            let! comprehension =
                                ArchiveWriter.readComprehension
                                    fs candidate.FolderPath
                            return!
                                comprehension
                                |> Option.map
                                    (validateCurrentHint db candidate)
                                |> Option.defaultValue
                                    (Task.FromResult(None))
                        })
            return
                match publication with
                | Generation.Published hint -> hint
                | Generation.Superseded -> None
        }

    let private addCurrentHint
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (hints: string list)
        (candidate: ExampleCandidate)
        : Task<string list> =
        task {
            let! hint = readCurrentHint db fs candidate
            return
                hint
                |> Option.map (fun value -> value :: hints)
                |> Option.defaultValue hints
        }

    let private extractCurrentHints
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (rows: Map<string, obj> list)
        : Task<string list> =
        task {
            let! reversed =
                rows
                |> List.choose (exampleCandidate archiveDir)
                |> Prelude.foldTask (addCurrentHint db fs) []
            return List.rev reversed
        }

    let private queryExampleCandidates
        (db: Algebra.Database)
        (docId: int64)
        (criterion: ExampleMatch)
        : Task<Map<string, obj> list> =
        let predicate, matchValue =
            match criterion with
            | SenderDomain domain -> "d.sender LIKE @match", $"%%@{domain}%%"
            | Vendor vendor -> "d.extracted_vendor = @match", vendor
        db.execReader
            $"""SELECT d.id AS document_id, d.saved_path, d.folder_path
                FROM documents d
                JOIN stage_completions sc
                  ON sc.document_id = d.id AND sc.stage_name = @stage
                JOIN comprehension c ON c.document_id = d.id
                WHERE d.classification_tier IS NOT NULL
                  AND d.id <> @docId
                  AND {predicate}
                  AND d.classification_confidence >= 0.7
                ORDER BY d.extracted_at DESC
                LIMIT 2"""
            [ ("@stage", Database.boxVal deepComprehendStageName)
              ("@docId", Database.boxVal docId)
              ("@match", Database.boxVal matchValue) ]

    let private loadExampleHints
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (docId: int64)
        (criterion: ExampleMatch)
        : Task<string list> =
        task {
            let! rows = queryExampleCandidates db docId criterion
            return! extractCurrentHints db fs archiveDir rows
        }

    /// Find 1–2 current, high-confidence comprehension schema hints.
    let private findExamples (db: Algebra.Database) (fs: Algebra.FileSystem) (archiveDir: string) (doc: Document.T) : Task<string list> =
        task {
            let docId = Document.id doc
            let senderMatch =
                doc
                |> Document.decode<string> "sender"
                |> Option.bind extractSenderDomain
                |> Option.map SenderDomain
            let! senderHints =
                senderMatch
                |> Option.map (loadExampleHints db fs archiveDir docId)
                |> Option.defaultValue (Task.FromResult<string list>([]))
            if not senderHints.IsEmpty then return senderHints
            else
                let vendor =
                    doc
                    |> Document.decode<string> "extracted_vendor"
                    |> Option.defaultValue ""
                if String.IsNullOrWhiteSpace vendor then return []
                else
                    return!
                        loadExampleHints
                            db fs archiveDir docId (Vendor vendor)
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
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (preferences: string)
        (doc: Document.T)
        : Task<string> =
        task {
            let context =
                doc
                |> buildContext
                |> addPreferences preferences

            let! examples = findExamples db fs archiveDir doc
            return appendSchemaHints context examples
        }

    /// Record a sender→document_type pattern for RAC knowledge accumulation.
    let private upsertLearnedPatternWith
        (execNonQuery:
            string -> (string * obj) list -> Task<int>)
        (senderDomain: string)
        (documentType: string)
        (confidence: float)
        : Task<unit> =
        task {
            let! _ =
                execNonQuery
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

    let private upsertLearnedPattern (db: Algebra.Database) =
        upsertLearnedPatternWith db.execNonQuery

    let private upsertLearnedPatternIn
        (scope: Algebra.TransactionScope) =
        upsertLearnedPatternWith scope.execNonQuery

    let private claimLearnedPatternEvidenceIn
        (scope: Algebra.TransactionScope)
        (generation: Generation.Token)
        (stageName: string)
        (senderDomain: string)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<bool> =
        task {
            let! affected =
                scope.execNonQuery
                    """INSERT INTO learned_pattern_evidence
                         (document_id, stage_name, generation, sender_domain,
                          document_type, confidence)
                       VALUES (@doc, @stage, @generation, @domain, @type, @confidence)
                       ON CONFLICT(document_id, stage_name, generation) DO NOTHING"""
                    [ ("@doc", Database.boxVal generation.DocumentId)
                      ("@stage", Database.boxVal stageName)
                      ("@generation", Database.boxVal generation.Value)
                      ("@domain", Database.boxVal senderDomain)
                      ("@type", Database.boxVal parsed.DocumentType)
                      ("@confidence", Database.boxVal parsed.Confidence) ]
            return affected = 1
        }

    /// Create a review suggestion when comprehension confidence is below threshold.
    let private createSuggestionIn
        (scope: Algebra.TransactionScope)
        (docId: int64)
        (proposedCategory: string)
        (currentCategory: string option)
        (confidence: float)
        : Task<unit> =
        task {
            let current = currentCategory |> Option.defaultValue ""
            let! _ =
                scope.execNonQuery
                    """INSERT INTO suggestions
                         (document_id, proposed_category, current_category, confidence)
                       SELECT @docId, @proposed, @current, @conf
                       WHERE NOT EXISTS (
                           SELECT 1 FROM suggestions
                           WHERE document_id = @docId
                             AND proposed_category = @proposed
                             AND COALESCE(current_category, '') = @current
                             AND status = 'pending')"""
                    [ ("@docId", Database.boxVal docId)
                      ("@proposed", Database.boxVal proposedCategory)
                      ("@current", Database.boxVal current)
                      ("@conf", Database.boxVal confidence) ]

            return ()
        }

    let private learnFromResultIn
        (scope: Algebra.TransactionScope)
        (generation: Generation.Token)
        (stageName: string)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        task {
            match
                doc
                |> Document.decode<string> "sender"
                |> Option.bind extractSenderDomain
            with
            | None -> return ()
            | Some domain ->
                let! claimed =
                    claimLearnedPatternEvidenceIn
                        scope generation stageName domain parsed
                if claimed then
                    do!
                        upsertLearnedPatternIn
                            scope domain parsed.DocumentType parsed.Confidence
        }

    let private suggestReviewIn
        (scope: Algebra.TransactionScope)
        (documentId: int64)
        (currentCategory: string option)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        if parsed.Confidence < 0.7 then
            createSuggestionIn
                scope
                documentId
                parsed.CanonicalCategory
                currentCategory
                parsed.Confidence
        else
            Task.FromResult(())

    let private recordReviewSignalsIn
        (scope: Algebra.TransactionScope)
        (generation: Generation.Token)
        (stageName: string)
        (doc: Document.T)
        (currentCategory: string option)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        task {
            do! learnFromResultIn scope generation stageName doc parsed
            do!
                suggestReviewIn
                    scope (Document.id doc) currentCategory parsed
        }

    type private ComprehensionArtifact =
        { Resource: PublicationFence.ArtifactFolder
          FolderPath: string }

    let private comprehensionArtifact (deps: Deps) (doc: Document.T) =
        let documentId = Document.id doc
        let savedPath =
            doc |> Document.decode<string> "saved_path" |> Option.defaultValue ""
        let folderPath = doc |> Document.decode<string> "folder_path"
        match
            PublicationFence.ArtifactFolder.tryFromMetadata
                deps.ArchiveDir savedPath folderPath
        with
        | None ->
            Error $"Comprehension write failed for doc {documentId}: archive folder not found"
        | Some resource ->
            PublicationFence.ArtifactFolder.resolve deps.ArchiveDir resource
            |> Result.map (fun path ->
                { Resource = resource; FolderPath = path })
            |> Result.mapError (fun error ->
                $"Comprehension write failed for doc {documentId}: {error}")

    let private writeComprehensionArtifact
        (deps: Deps)
        (artifact: ComprehensionArtifact)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        ArchiveWriter.writeComprehension
            deps.Fs artifact.FolderPath parsed.RawJson

    /// Captured before slow model work. Siblings share one thread artifact, so
    /// this is what lets a later publisher reject an earlier, slower one.
    let private currentArtifactRevision
        (deps: Deps)
        (doc: Document.T)
        : Task<ArtifactRevision.Token> =
        match comprehensionArtifact deps doc with
        | Error error -> invalidOp error
        | Ok artifact ->
            ArtifactRevision.current deps.Db artifact.Resource

    let private insertComprehensionTagIn
        (scope: Algebra.TransactionScope)
        (docId: int64)
        (confidence: float)
        (tag: string)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    """INSERT OR IGNORE INTO tags
                       (document_id, tag, source, confidence)
                       VALUES (@docId, @tag, 'comprehension', @confidence)"""
                    [ ("@docId", Database.boxVal docId)
                      ("@tag", Database.boxVal tag)
                      ("@confidence", Database.boxVal confidence) ]

            return ()
        }

    let private writeComprehensionTagsIn
        (scope: Algebra.TransactionScope)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        parsed.Tags
        |> Prelude.foldTask
            (fun () tag ->
                insertComprehensionTagIn
                    scope
                    (Document.id doc)
                    parsed.Confidence
                    tag)
            ()

    type internal ComprehensionPublisher =
        Algebra.TransactionScope ->
            Document.T ->
            ComprehensionSchema.NormalisedResponse ->
            Task<unit>

    type private CanonicalComprehension =
        { Response: ComprehensionSchema.NormalisedResponse
          CurrentCategory: string option }

    let private noComprehensionOutput : ComprehensionPublisher =
        fun _ _ _ -> Task.FromResult(())

    let private optionalDbValue = function
        | Some value -> Database.boxVal value
        | None -> Database.boxVal DBNull.Value

    let private canonicalComprehensionFromRow
        stageName
        (row: Map<string, obj>) =
        let reader = Prelude.RowReader(row)
        let json = reader.String "response_json" ""
        match ComprehensionSchema.normaliseResponse json with
        | Ok response ->
            { Response = response
              CurrentCategory = reader.OptString "current_category" }
        | Error error ->
            invalidOp
                $"Stored canonical response for stage '{stageName}' is invalid: {error}"

    let private readCanonicalComprehensionIn
        (scope: Algebra.TransactionScope)
        (generation: Generation.Token)
        (stageName: string)
        : Task<CanonicalComprehension> =
        task {
            let! rows =
                scope.execReader
                    """SELECT response_json, current_category
                       FROM stage_publications
                       WHERE document_id = @doc
                         AND stage_name = @stage
                         AND generation = @generation"""
                    [ ("@doc", Database.boxVal generation.DocumentId)
                      ("@stage", Database.boxVal stageName)
                      ("@generation", Database.boxVal generation.Value) ]
            return
                rows
                |> List.tryHead
                |> Option.map
                    (canonicalComprehensionFromRow stageName)
                |> Option.defaultWith (fun () ->
                    invalidOp
                        $"Canonical response for stage '{stageName}' was not persisted")
        }

    let private claimComprehensionIn
        (generation: Generation.Token)
        (stageName: string)
        (doc: Document.T)
        (response: ComprehensionSchema.NormalisedResponse)
        (scope: Algebra.TransactionScope)
        : Task<CanonicalComprehension> =
        task {
            let currentCategory =
                doc |> Document.decode<string> "category"
            let! _ =
                scope.execNonQuery
                    """INSERT INTO stage_publications
                         (document_id, stage_name, generation,
                          response_json, current_category)
                       VALUES
                         (@doc, @stage, @generation, @response, @current)
                       ON CONFLICT(document_id, stage_name, generation)
                       DO NOTHING"""
                    [ ("@doc", Database.boxVal generation.DocumentId)
                      ("@stage", Database.boxVal stageName)
                      ("@generation", Database.boxVal generation.Value)
                      ("@response", Database.boxVal response.RawJson)
                      ("@current", optionalDbValue currentCategory) ]
            return!
                readCanonicalComprehensionIn
                    scope generation stageName
        }

    let private publishComprehensionDataIn
        (generation: Generation.Token)
        (stageName: string)
        (isFinal:
            ComprehensionSchema.NormalisedResponse -> bool)
        (publishOutput: ComprehensionPublisher)
        (doc: Document.T)
        (canonical: CanonicalComprehension)
        (scope: Algebra.TransactionScope)
        : Task<unit> =
        task {
            let parsed = canonical.Response
            let sender = doc |> Document.decode<string> "sender"
            let! _ =
                ContactExtraction.harvestAndLinkIn
                    scope (Document.id doc) parsed.RawJson sender
            if isFinal parsed then
                do!
                    recordReviewSignalsIn
                        scope generation stageName doc
                        canonical.CurrentCategory parsed
                do! writeComprehensionTagsIn scope doc parsed
            do! publishOutput scope doc parsed
        }

    let private publishComprehensionAt
        (deps: Deps)
        (generation: Generation.Token)
        (artifactRevision: ArtifactRevision.Token)
        (stageName: string)
        (isFinal:
            ComprehensionSchema.NormalisedResponse -> bool)
        (publishOutput: ComprehensionPublisher)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<Generation.Publication<ComprehensionSchema.NormalisedResponse>> =
        task {
            match comprehensionArtifact deps doc with
            | Error error -> return invalidOp error
            | Ok artifact ->
                let! publication =
                    Generation.publishCanonical
                        deps.Db generation artifact.Resource artifactRevision
                        (claimComprehensionIn
                            generation stageName doc parsed)
                        (fun canonical ->
                            writeComprehensionArtifact
                                deps artifact canonical.Response)
                        (publishComprehensionDataIn
                            generation stageName isFinal
                            publishOutput doc)
                return
                    match publication with
                    | Generation.Published canonical ->
                        Generation.Published canonical.Response
                    | Generation.Superseded ->
                        Generation.Superseded
        }

    let private requirePublished
        (documentId: int64)
        (publication:
            Generation.Publication<ComprehensionSchema.NormalisedResponse>)
        : ComprehensionSchema.NormalisedResponse =
        match publication with
        | Generation.Published response -> response
        | Generation.Superseded ->
            invalidOp
                $"Comprehension for doc {documentId} was superseded by reflow"

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
    let internal triageAt
        (generation: Generation.Token)
        (deps: Deps)
        (doc: Document.T)
        : Task<Document.T> =
        let docId = Document.id doc

        let understood category tier confidence =
            doc
            |> Document.encode "category" (box category)
            |> Document.encode "classification_tier" (box tier)
            |> Document.encode "classification_confidence" (box confidence)
            |> Document.encode "stage" (box "understood")

        task {
            let! text = readExtractedText deps doc

            if String.IsNullOrWhiteSpace(text) then
                deps.Logger.warn $"Triage failed doc {docId}: no extracted text"
                return failwith $"Triage failed doc {docId}: no extracted text"
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
                    deps.Logger.warn $"Triage failed doc {docId}: no chat provider configured"
                    return failwith $"Triage failed doc {docId}: no chat provider configured"
                | Some chat ->
                    let! artifactRevision = currentArtifactRevision deps doc
                    let! context =
                        augmentComprehensionContext
                            deps.Db
                            deps.Fs
                            deps.ArchiveDir
                            deps.Preferences
                            doc

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
                        return failwith $"Triage failed for doc {docId}: {e}"

                    | Ok triageResponse ->
                        match ComprehensionSchema.normaliseResponse triageResponse with
                        | Error parseErr ->
                            let preview = triageResponse.[..min 200 (triageResponse.Length - 1)]
                            deps.Logger.warn $"Triage parse doc {docId}: {parseErr}: {preview}"
                            return failwith $"Triage parse failed for doc {docId}: {parseErr}"

                        | Ok triaged ->
                            let isFinal
                                (response: ComprehensionSchema.NormalisedResponse) =
                                not
                                    (financialCategories.Contains
                                        response.CanonicalCategory)
                            let! publication =
                                publishComprehensionAt
                                    deps generation artifactRevision
                                    triageStageName isFinal
                                    noComprehensionOutput doc triaged
                            let triaged =
                                requirePublished docId publication
                            let canonical = triaged.CanonicalCategory

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

    let triage
        (deps: Deps)
        (doc: Document.T)
        : Task<Document.T> =
        task {
            let! generation =
                Generation.current deps.Db (Document.id doc)
            return! triageAt generation deps doc
        }

    let private buildDeepContext (deps: Deps) (doc: Document.T) : Task<string> =
        task {
            let! documentContext =
                augmentComprehensionContext
                    deps.Db
                    deps.Fs
                    deps.ArchiveDir
                    deps.Preferences
                    doc

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

    let private applyDeepResult
        (deps: Deps)
        (generation: Generation.Token)
        (artifactRevision: ArtifactRevision.Token)
        (publishOutput: ComprehensionPublisher)
        (doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<Document.T> =
        task {
            let docId = Document.id doc

            let! publication =
                publishComprehensionAt
                    deps generation artifactRevision deepComprehendStageName
                    (fun _ -> true)
                    publishOutput doc parsed
            let committed =
                requirePublished docId publication
            let tier =
                confidenceTier
                    "comprehension" committed.Confidence

            deps.Logger.info
                $"Understood doc {docId} as '{committed.CanonicalCategory}' ({committed.DocumentType}, {tier}, conf={committed.Confidence:F2}): {committed.Summary}"

            return
                doc
                |> withComprehension "understood" tier committed
                |> Document.encode "deep_comprehended" (box true)
        }

    let private handleDeepResponse
        (deps: Deps)
        (generation: Generation.Token)
        (artifactRevision: ArtifactRevision.Token)
        (publishOutput: ComprehensionPublisher)
        (doc: Document.T)
        (response: string)
        : Task<Document.T> =
        match ComprehensionSchema.normaliseResponse response with
        | Ok parsed ->
            applyDeepResult
                deps generation artifactRevision publishOutput doc parsed
        | Error parseError ->
            deps.Logger.warn
                $"Deep comprehension parse doc {Document.id doc}: {parseError}"

            failwith $"Deep comprehension parse failed for doc {Document.id doc}: {parseError}"

    /// Phase 2: Deep comprehension for financially relevant documents.
    let internal deepComprehendAt
        (generation: Generation.Token)
        (publishOutput: ComprehensionPublisher)
        (deps: Deps)
        (doc: Document.T)
        : Task<Document.T> =
        task {
            let docId = Document.id doc
            let! artifactRevision = currentArtifactRevision deps doc
            let! text = readExtractedText deps doc

            if String.IsNullOrWhiteSpace(text) then
                deps.Logger.warn $"DeepComprehend failed doc {docId}: no extracted text"
                return failwith $"DeepComprehend failed doc {docId}: no extracted text"
            else

            match deps.ChatProvider with
            | None ->
                deps.Logger.warn $"DeepComprehend failed doc {docId}: no chat provider configured"
                return failwith $"DeepComprehend failed doc {docId}: no chat provider configured"
            | Some chat ->
                let! context = buildDeepContext deps doc
                let systemPrompt, userPrompt =
                    comprehensionPrompts deps text context

                let! result = chat.complete systemPrompt userPrompt

                match result with
                | Ok response ->
                    return!
                        handleDeepResponse
                            deps generation artifactRevision
                            publishOutput doc response
                | Error error ->
                    deps.Logger.warn $"Deep comprehension failed for doc {docId}: {error}"
                    return failwith $"Deep comprehension failed for doc {docId}: {error}"
        }

    let deepComprehend
        (deps: Deps)
        (doc: Document.T)
        : Task<Document.T> =
        task {
            let! generation =
                Generation.current deps.Db (Document.id doc)
            return!
                deepComprehendAt
                    generation noComprehensionOutput deps doc
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

    let internal embedAt
        (generation: Generation.Token)
        (deps: Deps)
        (doc: Document.T)
        : Task<Document.T> =
        task {
            let docId = Document.id doc
            let savedPath = doc |> Document.decode<string> "saved_path" |> Option.defaultValue ""
            let fullPath =
                if IO.Path.IsPathRooted(savedPath) then savedPath
                else IO.Path.Combine(deps.ArchiveDir, savedPath)
            let! textOpt = ArchiveWriter.readExtraction deps.Fs fullPath
            let text = textOpt |> Option.defaultValue ""

            match deps.Embedder with
            | None ->
                deps.Logger.warn $"Embed failed doc {docId}: no embedder configured"
                return failwith $"Embed failed doc {docId}: no embedder configured"
            | Some embedder ->
                let! available = embedder.isAvailable ()
                if not available then
                    return failwith $"Embedding service unavailable for doc {docId}"
                elif String.IsNullOrWhiteSpace(text) then
                    deps.Logger.warn $"Embed failed doc {docId}: no extracted text to embed"
                    return failwith $"Embed failed doc {docId}: no extracted text to embed"
                else
                    let! result =
                        Embeddings.embedDocumentAt
                            deps.Db deps.Logger deps.Clock
                            embedder generation text
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

    /// Generate embeddings for a document's extracted text.
    let embed (deps: Deps) (doc: Document.T) : Task<Document.T> =
        task {
            let! generation =
                Generation.current deps.Db (Document.id doc)
            return! embedAt generation deps doc
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
            OutputKey = "classification_tier"
            RequiredKeys = [ "extracted_at" ]
            Process = triage deps
            ResourceLock = resourceLock   // uses GPU (small model)
            MaxHoldTime = maxHoldTime }

          { Name = "understand"
            OutputKey = "deep_comprehended"
            RequiredKeys = [ "extracted_at" ]
            Process = deepComprehend deps
            ResourceLock = resourceLock   // uses GPU (large model)
            MaxHoldTime = maxHoldTime }

          { Name = "embed"
            OutputKey = "embedded_at"
            RequiredKeys = [ "extracted_at" ]
            Process = embed deps
            ResourceLock = resourceLock   // shares GPU with understand
            MaxHoldTime = maxHoldTime } ]
