namespace Hermes.Core

#nowarn "3261"

open System
open System.Threading.Tasks

/// Pipeline v5 stage definitions.
/// Each stage declares its dependencies, output table schema, gate, and processor.
[<RequireQualifiedAccess>]
module StagesV5 =

    // ── Output table schemas ─────────────────────────────────────────

    let private extractionSchema = """
        CREATE TABLE IF NOT EXISTS extraction (
            document_id       INTEGER PRIMARY KEY REFERENCES documents(id),
            extracted_date    TEXT,
            extracted_amount  REAL,
            extracted_vendor  TEXT,
            extracted_abn     TEXT,
            method            TEXT,
            confidence        REAL,
            extracted_at      TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """

    let private triageSchema = """
        CREATE TABLE IF NOT EXISTS triage (
            document_id       INTEGER PRIMARY KEY REFERENCES documents(id),
            document_type     TEXT NOT NULL,
            category          TEXT NOT NULL,
            confidence        REAL NOT NULL,
            triaged_at        TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """

    let private comprehensionSchema = """
        CREATE TABLE IF NOT EXISTS comprehension (
            document_id       INTEGER PRIMARY KEY REFERENCES documents(id),
            document_type     TEXT,
            category          TEXT,
            confidence        REAL,
            schema_version    TEXT DEFAULT 'v2',
            comprehended_at   TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """

    let private embeddingSchema = """
        CREATE TABLE IF NOT EXISTS embedding (
            document_id       INTEGER PRIMARY KEY REFERENCES documents(id),
            chunk_count       INTEGER NOT NULL DEFAULT 0,
            embedded_at       TEXT NOT NULL DEFAULT (datetime('now'))
        );
        """

    // ── Shared helpers ───────────────────────────────────────────────

    /// Categories that warrant deep comprehension with the large model.
    let financialCategories =
        set [ "receipts"; "payslips"; "invoices"; "bank-statements"; "tax"
              "utilities"; "insurance"; "superannuation"; "medical"
              "property"; "rates-and-tax"; "donations"
              "dividends"; "espp"; "stock-vests"; "legal"
              "finance-alerts" ]

    /// Check if a triaged document is financial (gate for deep-comprehend).
    let private isFinancial (db: Algebra.Database) (docId: int64) : Task<bool> =
        task {
            let! rows =
                db.execReader
                    "SELECT category FROM triage WHERE document_id = @id"
                    [ ("@id", Database.boxVal docId) ]
            match rows with
            | row :: _ ->
                let r = Prelude.RowReader(row)
                let cat = r.String "category" ""
                return financialCategories.Contains cat
            | [] -> return false
        }

    let private publicationOutcome
        stageName
        documentId
        (publication: Generation.Publication<unit>) =
        match publication with
        | Generation.Published () -> PipelineV5.Completed
        | Generation.Superseded ->
            PipelineV5.Failed
                $"{stageName} output for doc {documentId} was superseded by reflow"

    let private extractionParameters
        docId
        (enriched: Document.T) =
        let getText key =
            enriched
            |> Document.decode<string> key
            |> Option.defaultValue ""
        let getFloat key =
            enriched |> Document.decode<float> key
        [ ("@id", Database.boxVal docId)
          ("@date", Database.boxVal (getText "extracted_date"))
          ("@amt", Database.boxVal (getFloat "extracted_amount" |> Option.map box |> Option.defaultValue (box DBNull.Value)))
          ("@vendor", Database.boxVal (getText "extracted_vendor"))
          ("@abn", Database.boxVal (getText "extracted_abn"))
          ("@method", Database.boxVal (getText "extraction_method"))
          ("@conf", Database.boxVal (getFloat "ocr_confidence" |> Option.map box |> Option.defaultValue (box DBNull.Value))) ]

    let private writeExtractionStageOutput
        docId
        enriched
        (scope: Algebra.TransactionScope)
        : Task<unit> =
        task {
            let parameters = extractionParameters docId enriched
            let! _ =
                scope.execNonQuery
                    """INSERT OR REPLACE INTO extraction
                       (document_id, extracted_date, extracted_amount,
                        extracted_vendor, extracted_abn, method, confidence, extracted_at)
                       VALUES (@id, @date, @amt, @vendor, @abn, @method, @conf, datetime('now'))"""
                    parameters
            let! _ =
                scope.execNonQuery
                    """UPDATE documents SET
                       extracted_date = @date, extracted_amount = @amt,
                       extracted_vendor = @vendor,
                       extracted_abn = @abn, extraction_method = @method,
                       ocr_confidence = @conf, extracted_at = datetime('now')
                       WHERE id = @id"""
                    parameters
            return ()
        }

    // ── Stage processors ─────────────────────────────────────────────
    // These are stubs — the actual implementations will call into the
    // existing Extraction, Stages, Embeddings modules but write to
    // per-stage tables instead of the monolithic documents table.

    /// Extract: read file, produce text. Writes to extraction table.
    let internal extractAt
        (deps: Stages.Deps)
        (generation: Generation.Token)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            let! rows =
                db.execReader
                    "SELECT * FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal docId) ]
            match rows with
            | [] -> return PipelineV5.Failed "Document not found"
            | row :: _ ->
                let doc = Document.fromRow row
                try
                    let stageDeps =
                        { deps with Db = db; Logger = logger }
                    let! enriched =
                        Stages.extractAt generation stageDeps doc
                    let! publication =
                        Generation.publish
                            db generation
                            (writeExtractionStageOutput
                                docId enriched)
                    return
                        publicationOutcome
                            "Extract" docId publication
                with ex ->
                    return PipelineV5.Failed ex.Message
        }

    let extract
        (deps: Stages.Deps)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            let! generation = Generation.current db docId
            return!
                extractAt deps generation db logger docId
        }

    type private TriageStageOutput =
        { DocumentType: string
          Category: string
          Confidence: float
          Tier: string }

    let private triageDocumentType (comprehension: string) =
        try
            use parsed =
                System.Text.Json.JsonDocument.Parse(comprehension)
            parsed.RootElement.GetProperty("document_type").GetString()
            |> Option.ofObj
            |> Option.defaultValue "other"
        with _ ->
            "other"

    let private triageStageOutput (enriched: Document.T) =
        let getText key =
            enriched
            |> Document.decode<string> key
            |> Option.defaultValue ""
        let tier = getText "classification_tier"
        { DocumentType =
            getText "comprehension"
            |> triageDocumentType
          Category = getText "category"
          Confidence =
            enriched
            |> Document.decode<float> "classification_confidence"
            |> Option.defaultValue 0.0
          Tier =
            if String.IsNullOrWhiteSpace tier then "triage"
            else tier }

    let private writeTriageStageOutput
        docId
        (output: TriageStageOutput)
        (scope: Algebra.TransactionScope)
        : Task<unit> =
        task {
            let parameters =
                [ ("@id", Database.boxVal docId)
                  ("@type", Database.boxVal output.DocumentType)
                  ("@cat", Database.boxVal output.Category)
                  ("@conf", Database.boxVal output.Confidence)
                  ("@tier", Database.boxVal output.Tier) ]
            let! _ =
                scope.execNonQuery
                    """INSERT OR REPLACE INTO triage
                       (document_id, document_type, category, confidence, triaged_at)
                       VALUES (@id, @type, @cat, @conf, datetime('now'))"""
                    parameters
            let! _ =
                scope.execNonQuery
                    """UPDATE documents SET
                       category = CASE WHEN classification_tier = 'manual' THEN category ELSE @cat END,
                       classification_tier = CASE WHEN classification_tier = 'manual' THEN classification_tier ELSE @tier END,
                       classification_confidence = CASE WHEN classification_tier = 'manual' THEN classification_confidence ELSE @conf END
                       WHERE id = @id"""
                    parameters
            return ()
        }

    /// Triage: classify document type with small model. Writes to triage table.
    let private triageAt
        (deps: Stages.Deps)
        (generation: Generation.Token)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            // Read metadata; document content remains in the archive.
            let! rows =
                db.execReader
                    """SELECT d.sender, d.subject, d.category, d.saved_path,
                              d.folder_path,
                              e.extracted_vendor, e.extracted_amount
                       FROM extraction e
                       JOIN documents d ON d.id = e.document_id
                       WHERE e.document_id = @id"""
                    [ ("@id", Database.boxVal docId) ]
            match rows with
            | [] -> return PipelineV5.Failed "No extraction found"
            | row :: _ ->
                let r = Prelude.RowReader(row)

                // Build a v4 Document.T for compatibility
                let doc =
                    Map.empty
                    |> Map.add "id" (box docId)
                    |> Map.add "sender" (box (r.String "sender" ""))
                    |> Map.add "subject" (box (r.String "subject" ""))
                    |> Map.add "category" (box (r.String "category" ""))
                    |> Map.add "saved_path" (box (r.String "saved_path" ""))
                    |> Map.add "folder_path" (box (r.String "folder_path" ""))
                    |> Map.add "extracted_vendor" (box (r.String "extracted_vendor" ""))
                    |> Map.add "extracted_amount" (r.OptFloat "extracted_amount" |> Option.map box |> Option.defaultValue (box ""))

                // Run existing triage function
                try
                    let stageDeps =
                        { deps with Db = db; Logger = logger }
                    let! enriched =
                        Stages.triageAt generation stageDeps doc
                    let output = triageStageOutput enriched
                    let! publication =
                        Generation.publish
                            db generation
                            (writeTriageStageOutput docId output)
                    return
                        publicationOutcome
                            "Triage" docId publication
                with ex ->
                    return PipelineV5.Failed ex.Message
        }

    let triage
        (deps: Stages.Deps)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            let! generation = Generation.current db docId
            return! triageAt deps generation db logger docId
        }

    let private deepTier (confidence: float) : string =
        if confidence >= 0.7 then "comprehension"
        else "comprehension_review"

    let private writeDeepStageOutput
        (docId: int64)
        (scope: Algebra.TransactionScope)
        (_doc: Document.T)
        (parsed: ComprehensionSchema.NormalisedResponse)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    """INSERT OR REPLACE INTO comprehension
                         (document_id, document_type, category,
                          confidence, comprehended_at)
                       VALUES
                         (@id, @type, @cat, @conf, datetime('now'))"""
                    [ ("@id", Database.boxVal docId)
                      ("@type", Database.boxVal parsed.DocumentType)
                      ("@cat", Database.boxVal parsed.CanonicalCategory)
                      ("@conf", Database.boxVal parsed.Confidence) ]
            let! _ =
                scope.execNonQuery
                    """UPDATE documents SET
                         category =
                           CASE WHEN classification_tier = 'manual'
                                THEN category ELSE @cat END,
                         classification_tier =
                           CASE WHEN classification_tier = 'manual'
                                THEN classification_tier ELSE @tier END,
                         classification_confidence =
                           CASE WHEN classification_tier = 'manual'
                                THEN classification_confidence ELSE @conf END
                       WHERE id = @id"""
                    [ ("@id", Database.boxVal docId)
                      ("@cat", Database.boxVal parsed.CanonicalCategory)
                      ("@tier", Database.boxVal (deepTier parsed.Confidence))
                      ("@conf", Database.boxVal parsed.Confidence) ]
            return ()
        }

    /// Deep comprehend: full extraction with large model. Writes to comprehension table.
    let private deepComprehendAt
        (deps: Stages.Deps)
        (generation: Generation.Token)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            // Read from extraction + triage
            let! rows =
                db.execReader
                    """SELECT d.sender, d.subject, d.saved_path, d.folder_path,
                              d.classification_tier, e.extracted_vendor, e.extracted_amount,
                              t.category, t.document_type, t.confidence
                       FROM extraction e
                       JOIN documents d ON d.id = e.document_id
                       JOIN triage t ON t.document_id = e.document_id
                       WHERE e.document_id = @id"""
                    [ ("@id", Database.boxVal docId) ]
            match rows with
            | [] -> return PipelineV5.Failed "No extraction/triage found"
            | row :: _ ->
                let r = Prelude.RowReader(row)
                let triageConfidence = r.Float "confidence" 0.0
                let defaultTriageTier =
                    if triageConfidence >= 0.7 then "triage"
                    else "triage_review"
                let triageTier =
                    r.String "classification_tier" defaultTriageTier

                // Build v4 Document.T for compatibility
                let doc =
                    Map.empty
                    |> Map.add "id" (box docId)
                    |> Map.add "sender" (box (r.String "sender" ""))
                    |> Map.add "subject" (box (r.String "subject" ""))
                    |> Map.add "saved_path" (box (r.String "saved_path" ""))
                    |> Map.add "folder_path" (box (r.String "folder_path" ""))
                    |> Map.add "category" (box (r.String "category" ""))
                    |> Map.add "extracted_vendor" (box (r.String "extracted_vendor" ""))
                    |> Map.add "extracted_amount" (r.OptFloat "extracted_amount" |> Option.map box |> Option.defaultValue (box ""))
                    |> Map.add "classification_confidence" (box triageConfidence)
                    |> Map.add "classification_tier" (box triageTier)
                    |> Map.add "stage" (box "triaged")

                try
                    let stageDeps =
                        { deps with Db = db; Logger = logger }
                    let! _ =
                        Stages.deepComprehendAt
                            generation
                            (writeDeepStageOutput docId)
                            stageDeps
                            doc
                    return PipelineV5.Completed
                with ex ->
                    return PipelineV5.Failed ex.Message
        }

    let deepComprehend
        (deps: Stages.Deps)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            let! generation = Generation.current db docId
            return!
                deepComprehendAt
                    deps generation db logger docId
        }

    let private writeEmbeddingStageOutput
        docId
        chunkCount
        (scope: Algebra.TransactionScope)
        : Task<unit> =
        task {
            let parameters =
                [ ("@id", Database.boxVal docId)
                  ("@chunks", Database.boxVal chunkCount) ]
            let! _ =
                scope.execNonQuery
                    """INSERT OR REPLACE INTO embedding
                       (document_id, chunk_count, embedded_at)
                       VALUES (@id, @chunks, datetime('now'))"""
                    parameters
            let! _ =
                scope.execNonQuery
                    "UPDATE documents SET embedded_at = datetime('now'), chunk_count = @chunks WHERE id = @id"
                    parameters
            return ()
        }

    /// Embed: generate vector embeddings. Writes to embedding table.
    let internal embedAt
        (deps: Stages.Deps)
        (generation: Generation.Token)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            let! rows =
                db.execReader
                    """SELECT d.saved_path
                       FROM documents d
                       JOIN extraction e ON e.document_id = d.id
                       WHERE d.id = @id"""
                    [ ("@id", Database.boxVal docId) ]
            match rows with
            | [] -> return PipelineV5.Failed "No extraction found"
            | row :: _ ->
                let r = Prelude.RowReader(row)

                // Build v4 doc for compatibility
                let doc =
                    Map.empty
                    |> Map.add "id" (box docId)
                    |> Map.add "saved_path" (box (r.String "saved_path" ""))

                try
                    let stageDeps =
                        { deps with Db = db; Logger = logger }
                    let! enriched =
                        Stages.embedAt generation stageDeps doc
                    let chunkCount = enriched |> Document.decode<int64> "chunk_count" |> Option.defaultValue 0L

                    let! publication =
                        Generation.publish
                            db generation
                            (writeEmbeddingStageOutput
                                docId chunkCount)
                    return
                        publicationOutcome
                            "Embed" docId publication
                with ex ->
                    return PipelineV5.Failed ex.Message
        }

    let embed
        (deps: Stages.Deps)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (docId: int64)
        : Task<PipelineV5.StageOutcome> =
        task {
            let! generation = Generation.current db docId
            return!
                embedAt deps generation db logger docId
        }

    // ── Stage registration ───────────────────────────────────────────

    /// Build the stage definitions for the standard Hermes pipeline.
    let standardStages (deps: Stages.Deps) : PipelineV5.StageDefinition list =
        let triageModel = deps.TriageProvider |> Option.map (fun _ -> "qwen2.5:7b")
        let instructModel = deps.ChatProvider |> Option.map (fun _ -> "qwen2.5:32b")
        let embedModel = deps.Embedder |> Option.map (fun _ -> "nomic-embed-text")

        [ { PipelineV5.StageDefinition.Name = "extract"
            DependsOn = []
            OutputTable = "extraction"
            Schema = extractionSchema
            Process =
                fun db logger execution ->
                    extractAt
                        deps execution.Generation
                        db logger execution.DocumentId
            Gate = None
            GpuModel = None
            Mode = PipelineV5.Channel
            Concurrency = 8 }

          { Name = "triage"
            DependsOn = ["extract"]
            OutputTable = "triage"
            Schema = triageSchema
            Process =
                fun db logger execution ->
                    triageAt
                        deps execution.Generation
                        db logger execution.DocumentId
            Gate = None
            GpuModel = triageModel
            Mode = PipelineV5.Channel
            Concurrency = 1 }

          { Name = "deep-comprehend"
            DependsOn = ["extract"; "triage"]
            OutputTable = "comprehension"
            Schema = comprehensionSchema
            Process =
                fun db logger execution ->
                    deepComprehendAt
                        deps execution.Generation
                        db logger execution.DocumentId
            Gate = Some isFinancial
            GpuModel = instructModel
            Mode = PipelineV5.Batch (TimeSpan.FromMinutes 1.0)
            Concurrency = 1 }

          { Name = "embed"
            DependsOn = ["extract"]
            OutputTable = "embedding"
            Schema = embeddingSchema
            Process =
                fun db logger execution ->
                    embedAt
                        deps execution.Generation
                        db logger execution.DocumentId
            Gate = None
            GpuModel = embedModel
            Mode = PipelineV5.Channel
            Concurrency = 1 } ]
