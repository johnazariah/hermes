namespace Hermes.Core

open System.Threading.Tasks

/// Document browsing queries for the UI navigator: categories, document lists, detail views.
[<RequireQualifiedAccess>]
module DocumentBrowser =

    // ─── Types ───────────────────────────────────────────────────────

    type DocumentSummary =
        { Id: int64; OriginalName: string; Category: string
          ExtractedDate: string option; ExtractedAmount: float option
          Sender: string option; Vendor: string option
          SourceType: string option; Account: string option; SourcePath: string option
          ClassificationTier: string option
          ClassificationConfidence: float option }

    type PipelineStatus =
        { Understood: bool; Extracted: bool; Embedded: bool }

    type DocumentDetail =
        { Summary: DocumentSummary; ExtractedText: string option
          Comprehension: string option
          FilePath: string; Vendor: string option
          IngestedAt: string; ExtractedAt: string option
          EmbeddedAt: string option; PipelineStatus: PipelineStatus }

    // ─── Queries ─────────────────────────────────────────────────────

    /// List categories with document counts.
    let listCategories (db: Algebra.Database) : Task<(string * int) list> =
        task {
            let! rows =
                db.execReader
                    "SELECT category, COUNT(*) as cnt FROM documents GROUP BY category ORDER BY cnt DESC"
                    []
            return rows |> List.choose (fun row ->
                let r = Prelude.RowReader(row)
                r.OptString "category"
                |> Option.map (fun cat -> (cat, r.Int64 "cnt" 0L |> int)))
        }

    /// List documents in a category with offset-based pagination.
    let listDocuments (db: Algebra.Database) (category: string) (offset: int) (limit: int) : Task<DocumentSummary list> =
        task {
            let sql, parms =
                if System.String.IsNullOrEmpty(category) then
                    """SELECT id, original_name, category, extracted_date, extracted_amount,
                              sender, extracted_vendor, source_type, account, source_path,
                              classification_tier, classification_confidence
                       FROM documents ORDER BY id DESC LIMIT @lim OFFSET @off""",
                    [ ("@lim", Database.boxVal (int64 limit)); ("@off", Database.boxVal (int64 offset)) ]
                else
                    """SELECT id, original_name, category, extracted_date, extracted_amount,
                              sender, extracted_vendor, source_type, account, source_path,
                              classification_tier, classification_confidence
                       FROM documents WHERE category = @cat
                       ORDER BY id DESC LIMIT @lim OFFSET @off""",
                    [ ("@cat", Database.boxVal category)
                      ("@lim", Database.boxVal (int64 limit)); ("@off", Database.boxVal (int64 offset)) ]
            let! rows = db.execReader sql parms
            return rows |> List.choose (fun row ->
                let r = Prelude.RowReader(row)
                r.OptInt64 "id"
                |> Option.map (fun id ->
                    { Id = id
                      OriginalName = r.String "original_name" ""
                      Category = r.String "category" ""
                      ExtractedDate = r.OptString "extracted_date"
                      ExtractedAmount = r.OptFloat "extracted_amount"
                      Sender = r.OptString "sender"
                      Vendor = r.OptString "extracted_vendor"
                      SourceType = r.OptString "source_type"
                      Account = r.OptString "account"
                      SourcePath = r.OptString "source_path"
                      ClassificationTier = r.OptString "classification_tier"
                      ClassificationConfidence = r.OptFloat "classification_confidence" }))
        }

    /// Get full document detail by ID.
    let getDocumentDetail (db: Algebra.Database) (documentId: int64) : Task<DocumentDetail option> =
        task {
            let! rows =
                db.execReader
                    """SELECT id, original_name, category, saved_path, extracted_text,
                              comprehension,
                              extracted_date, extracted_amount, extracted_vendor, sender,
                              source_type, account, source_path,
                              classification_tier, classification_confidence,
                              ingested_at, extracted_at, embedded_at
                       FROM documents WHERE id = @id"""
                    [ ("@id", Database.boxVal documentId) ]
            return rows |> List.tryHead |> Option.bind (fun row ->
                let r = Prelude.RowReader(row)
                r.OptInt64 "id"
                |> Option.map (fun id ->
                    let summary =
                        { Id = id
                          OriginalName = r.String "original_name" ""
                          Category = r.String "category" ""
                          ExtractedDate = r.OptString "extracted_date"
                          ExtractedAmount = r.OptFloat "extracted_amount"
                          Sender = r.OptString "sender"
                          Vendor = r.OptString "extracted_vendor"
                          SourceType = r.OptString "source_type"
                          Account = r.OptString "account"
                          SourcePath = r.OptString "source_path"
                          ClassificationTier = r.OptString "classification_tier"
                          ClassificationConfidence = r.OptFloat "classification_confidence" }
                    let pipeline =
                        { Understood = summary.Category <> "unsorted" && summary.Category <> "unclassified"
                          Extracted = (r.OptString "extracted_at").IsSome
                          Embedded = (r.OptString "embedded_at").IsSome }
                    { Summary = summary
                      ExtractedText = r.OptString "extracted_text"
                      Comprehension = r.OptString "comprehension"
                      FilePath = r.String "saved_path" ""
                      Vendor = r.OptString "extracted_vendor"
                      IngestedAt = r.String "ingested_at" ""
                      ExtractedAt = r.OptString "extracted_at"
                      EmbeddedAt = r.OptString "embedded_at"
                      PipelineStatus = pipeline }))
        }
