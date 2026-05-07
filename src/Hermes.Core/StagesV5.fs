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

    // ── Stage processors ─────────────────────────────────────────────
    // These are stubs — the actual implementations will call into the
    // existing Extraction, Stages, Embeddings modules but write to
    // per-stage tables instead of the monolithic documents table.

    /// Extract: read file, produce text. Writes to extraction table.
    let extract (deps: Stages.Deps) (db: Algebra.Database) (logger: Algebra.Logger) (docId: int64) : Task<PipelineV5.StageOutcome> =
        task {
            // Read full document row for v4 compatibility
            let! rows =
                db.execReader
                    "SELECT * FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal docId) ]
            match rows with
            | [] -> return PipelineV5.Failed "Document not found"
            | row :: _ ->
                let doc = Document.fromRow row

                try
                    let! enriched = Stages.extract deps doc
                    let getText key = enriched |> Document.decode<string> key |> Option.defaultValue ""
                    let getFloat key = enriched |> Document.decode<float> key

                    // Write to extraction table only
                    let! _ =
                        db.execNonQuery
                            """INSERT OR REPLACE INTO extraction
                               (document_id, extracted_date, extracted_amount,
                                extracted_vendor, extracted_abn, method, confidence, extracted_at)
                               VALUES (@id, @date, @amt, @vendor, @abn, @method, @conf, datetime('now'))"""
                            [ ("@id", Database.boxVal docId)
                              ("@date", Database.boxVal (getText "extracted_date"))
                              ("@amt", Database.boxVal (getFloat "extracted_amount" |> Option.map box |> Option.defaultValue (box DBNull.Value)))
                              ("@vendor", Database.boxVal (getText "extracted_vendor"))
                              ("@abn", Database.boxVal (getText "extracted_abn"))
                              ("@method", Database.boxVal (getText "extraction_method"))
                              ("@conf", Database.boxVal (getFloat "ocr_confidence" |> Option.map box |> Option.defaultValue (box DBNull.Value))) ]

                    // Update legacy documents table for API/UI compatibility
                    let! _ =
                        db.execNonQuery
                            """UPDATE documents SET
                               extracted_date = @date, extracted_amount = @amt,
                               extracted_vendor = @vendor,
                               extracted_abn = @abn, extraction_method = @method,
                               ocr_confidence = @conf, extracted_at = datetime('now'),
                               stage = 'extracted'
                               WHERE id = @id"""
                            [ ("@id", Database.boxVal docId)
                              ("@date", Database.boxVal (getText "extracted_date"))
                              ("@amt", Database.boxVal (getFloat "extracted_amount" |> Option.map box |> Option.defaultValue (box DBNull.Value)))
                              ("@vendor", Database.boxVal (getText "extracted_vendor"))
                              ("@abn", Database.boxVal (getText "extracted_abn"))
                              ("@method", Database.boxVal (getText "extraction_method"))
                              ("@conf", Database.boxVal (getFloat "ocr_confidence" |> Option.map box |> Option.defaultValue (box DBNull.Value))) ]

                    return PipelineV5.Completed
                with ex ->
                    return PipelineV5.Failed ex.Message
        }

    /// Triage: classify document type with small model. Writes to triage table.
    let triage (deps: Stages.Deps) (db: Algebra.Database) (logger: Algebra.Logger) (docId: int64) : Task<PipelineV5.StageOutcome> =
        task {
            // Read metadata; document content remains in the archive.
            let! rows =
                db.execReader
                    """SELECT d.sender, d.subject, d.category, d.saved_path,
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
                    |> Map.add "extracted_vendor" (box (r.String "extracted_vendor" ""))
                    |> Map.add "extracted_amount" (r.OptFloat "extracted_amount" |> Option.map box |> Option.defaultValue (box ""))

                // Run existing triage function
                try
                    let! enriched = Stages.triage deps doc
                    let getText key = enriched |> Document.decode<string> key |> Option.defaultValue ""
                    let getFloat key = enriched |> Document.decode<float> key

                    let category = getText "category"
                    let comprehension = getText "comprehension"
                    let confidence =
                        getFloat "classification_confidence"
                        |> Option.defaultValue 0.0

                    let tier =
                        getText "classification_tier"
                        |> fun value ->
                            if String.IsNullOrWhiteSpace value then "triage"
                            else value

                    let resultStage =
                        getText "stage"
                        |> fun value ->
                            if String.IsNullOrWhiteSpace value then "understood"
                            else value

                    // Parse the triage JSON response
                    let docType =
                        try
                            let parsed = System.Text.Json.JsonDocument.Parse(comprehension)
                            parsed.RootElement.GetProperty("document_type").GetString() |> Option.ofObj |> Option.defaultValue "other"
                        with _ -> "other"

                    let! _ =
                        db.execNonQuery
                            """INSERT OR REPLACE INTO triage
                               (document_id, document_type, category, confidence, triaged_at)
                               VALUES (@id, @type, @cat, @conf, datetime('now'))"""
                            [ ("@id", Database.boxVal docId)
                              ("@type", Database.boxVal docType)
                              ("@cat", Database.boxVal category)
                              ("@conf", Database.boxVal confidence) ]

                    // Update legacy documents table for API compatibility
                    let! _ =
                        db.execNonQuery
                            """UPDATE documents SET category = @cat,
                               classification_tier = @tier,
                               classification_confidence = @conf,
                               stage = @stage
                               WHERE id = @id"""
                            [ ("@id", Database.boxVal docId)
                              ("@cat", Database.boxVal category)
                              ("@tier", Database.boxVal tier)
                              ("@conf", Database.boxVal confidence)
                              ("@stage", Database.boxVal resultStage) ]

                    return PipelineV5.Completed
                with ex ->
                    return PipelineV5.Failed ex.Message
        }

    /// Deep comprehend: full extraction with large model. Writes to comprehension table.
    let deepComprehend (deps: Stages.Deps) (db: Algebra.Database) (logger: Algebra.Logger) (docId: int64) : Task<PipelineV5.StageOutcome> =
        task {
            // Read from extraction + triage
            let! rows =
                db.execReader
                    """SELECT d.sender, d.subject, d.saved_path,
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
                let triageDocumentType = r.String "document_type" "other"
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
                    |> Map.add "category" (box (r.String "category" ""))
                    |> Map.add "extracted_vendor" (box (r.String "extracted_vendor" ""))
                    |> Map.add "extracted_amount" (r.OptFloat "extracted_amount" |> Option.map box |> Option.defaultValue (box ""))
                    |> Map.add "classification_confidence" (box triageConfidence)
                    |> Map.add "classification_tier" (box triageTier)
                    |> Map.add "stage" (box "triaged")

                try
                    let! enriched = Stages.deepComprehend deps doc
                    let getText key = enriched |> Document.decode<string> key |> Option.defaultValue ""
                    let getFloat key = enriched |> Document.decode<float> key

                    let comprehension = getText "comprehension"
                    let category = getText "category"
                    let confidence =
                        getFloat "classification_confidence"
                        |> Option.defaultValue triageConfidence
                    let tier =
                        getText "classification_tier"
                        |> fun value ->
                            if String.IsNullOrWhiteSpace value then triageTier
                            else value
                    let resultStage =
                        getText "stage"
                        |> fun value ->
                            if String.IsNullOrWhiteSpace value then "understood"
                            else value

                    // Prefer the deep result's document type; retain triage on fallback.
                    let docType =
                        try
                            let parsed = System.Text.Json.JsonDocument.Parse(comprehension)
                            parsed.RootElement.GetProperty("document_type").GetString()
                            |> Option.ofObj
                            |> Option.defaultValue triageDocumentType
                        with _ ->
                            triageDocumentType

                    let! _ =
                        db.execNonQuery
                            """INSERT OR REPLACE INTO comprehension
                               (document_id, document_type, category, confidence, comprehended_at)
                               VALUES (@id, @type, @cat, @conf, datetime('now'))"""
                            [ ("@id", Database.boxVal docId)
                              ("@type", Database.boxVal docType)
                              ("@cat", Database.boxVal category)
                              ("@conf", Database.boxVal confidence) ]

                    // Update legacy table
                    let! _ =
                        db.execNonQuery
                            """UPDATE documents SET category = @cat,
                               classification_tier = @tier,
                               classification_confidence = @conf,
                               stage = @stage
                               WHERE id = @id"""
                            [ ("@id", Database.boxVal docId)
                              ("@cat", Database.boxVal category)
                              ("@tier", Database.boxVal tier)
                              ("@conf", Database.boxVal confidence)
                              ("@stage", Database.boxVal resultStage) ]

                    return PipelineV5.Completed
                with ex ->
                    return PipelineV5.Failed ex.Message
        }

    /// Embed: generate vector embeddings. Writes to embedding table.
    let embed (deps: Stages.Deps) (db: Algebra.Database) (logger: Algebra.Logger) (docId: int64) : Task<PipelineV5.StageOutcome> =
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
                    let! enriched = Stages.embed deps doc
                    let chunkCount = enriched |> Document.decode<int64> "chunk_count" |> Option.defaultValue 0L

                    let! _ =
                        db.execNonQuery
                            """INSERT OR REPLACE INTO embedding
                               (document_id, chunk_count, embedded_at)
                               VALUES (@id, @chunks, datetime('now'))"""
                            [ ("@id", Database.boxVal docId)
                              ("@chunks", Database.boxVal chunkCount) ]

                    // Update legacy table
                    let! _ =
                        db.execNonQuery
                            "UPDATE documents SET embedded_at = datetime('now'), chunk_count = @chunks, stage = 'embedded' WHERE id = @id"
                            [ ("@id", Database.boxVal docId); ("@chunks", Database.boxVal chunkCount) ]

                    return PipelineV5.Completed
                with ex ->
                    return PipelineV5.Failed ex.Message
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
            Process = extract deps
            Gate = None
            GpuModel = None
            Mode = PipelineV5.Channel
            Concurrency = 8 }

          { Name = "triage"
            DependsOn = ["extract"]
            OutputTable = "triage"
            Schema = triageSchema
            Process = triage deps
            Gate = None
            GpuModel = triageModel
            Mode = PipelineV5.Channel
            Concurrency = 1 }

          { Name = "deep-comprehend"
            DependsOn = ["extract"; "triage"]
            OutputTable = "comprehension"
            Schema = comprehensionSchema
            Process = deepComprehend deps
            Gate = Some isFinancial
            GpuModel = instructModel
            Mode = PipelineV5.Batch (TimeSpan.FromMinutes 1.0)
            Concurrency = 1 }

          { Name = "embed"
            DependsOn = ["extract"]
            OutputTable = "embedding"
            Schema = embeddingSchema
            Process = embed deps
            Gate = None
            GpuModel = embedModel
            Mode = PipelineV5.Channel
            Concurrency = 1 } ]
