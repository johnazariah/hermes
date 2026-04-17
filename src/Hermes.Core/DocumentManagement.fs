namespace Hermes.Core

#nowarn "3261"

open System
open System.IO
open System.Threading.Tasks

/// Document management operations: reclassify, re-extract, queue inspection.
[<RequireQualifiedAccess>]
module DocumentManagement =

    // ─── Types ───────────────────────────────────────────────────────

    type QueueStage = { Count: int; SampleIds: int64 list }

    type ProcessingQueue =
        { Unclassified: QueueStage
          Unextracted: QueueStage
          Unembedded: QueueStage }

    // ─── Reclassify ──────────────────────────────────────────────────

    /// Move a document to a different category (file move + DB update).
    let reclassify
        (db: Algebra.Database) (fs: Algebra.FileSystem)
        (archiveDir: string) (documentId: int64) (newCategory: string)
        : Task<Result<unit, string>> =
        task {
            let! rows =
                db.execReader
                    "SELECT saved_path, category FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal documentId) ]
            match rows with
            | [] -> return Error $"Document {documentId} not found"
            | row :: _ ->
                let r = Prelude.RowReader(row)
                let savedPath = r.String "saved_path" ""
                let oldCategory = r.String "category" ""
                let fileName = Path.GetFileName(savedPath)
                let newPath = Path.Combine(newCategory, fileName)
                let fullOld = Path.Combine(archiveDir, savedPath)
                let fullNew = Path.Combine(archiveDir, newPath)
                try
                    fs.createDirectory (Path.GetDirectoryName(fullNew))
                    if fs.fileExists fullOld then
                        fs.moveFile fullOld fullNew
                    let! _ =
                        db.execNonQuery
                            """UPDATE documents SET category = @cat, saved_path = @path,
                               classification_tier = 'manual'
                               WHERE id = @id"""
                            [ ("@cat", Database.boxVal newCategory)
                              ("@path", Database.boxVal newPath)
                              ("@id", Database.boxVal documentId) ]
                    return Ok ()
                with ex ->
                    return Error $"Reclassify failed: {ex.Message}"
        }

    // ─── Re-extract ──────────────────────────────────────────────────

    /// Clear extraction fields, marking document for re-extraction on next cycle.
    let reextract (db: Algebra.Database) (documentId: int64) : Task<Result<unit, string>> =
        task {
            let! affected =
                db.execNonQuery
                    """UPDATE documents
                       SET extracted_text = NULL, extracted_date = NULL,
                           extracted_amount = NULL, extracted_vendor = NULL,
                           extracted_abn = NULL, extraction_method = NULL,
                           ocr_confidence = NULL, extraction_confidence = NULL,
                           extracted_at = NULL
                       WHERE id = @id"""
                    [ ("@id", Database.boxVal documentId) ]
            if affected = 0 then return Error $"Document {documentId} not found"
            else return Ok ()
        }

    // ─── Processing queue ────────────────────────────────────────────

    let private getStage (db: Algebra.Database) (where: string) (limit: int) : Task<QueueStage> =
        task {
            let! countObj = db.execScalar $"SELECT COUNT(*) FROM documents WHERE {where}" []
            let count =
                match countObj with
                | :? int64 as i -> int i
                | _ -> 0
            let! rows =
                db.execReader
                    $"SELECT id FROM documents WHERE {where} ORDER BY id ASC LIMIT @lim"
                    [ ("@lim", Database.boxVal (int64 limit)) ]
            let ids =
                rows |> List.choose (fun row ->
                    let r = Prelude.RowReader(row)
                    r.OptInt64 "id")
            return { Count = count; SampleIds = ids }
        }

    /// Get processing queue overview with counts and sample IDs.
    let getProcessingQueue (db: Algebra.Database) (limit: int) : Task<ProcessingQueue> =
        task {
            let! unclassified = getStage db "category = 'unsorted' OR category = 'unclassified'" limit
            let! unextracted = getStage db "extracted_at IS NULL" limit
            let! unembedded = getStage db "extracted_at IS NOT NULL AND embedded_at IS NULL" limit
            return { Unclassified = unclassified; Unextracted = unextracted; Unembedded = unembedded }
        }

    // ─── Corrections (user feedback) ─────────────────────────────────

    /// Allowed document columns that can be corrected by the user.
    let private correctableColumns = set [ "category"; "extracted_amount"; "extracted_vendor"; "extracted_date" ]

    /// Save a correction and update the document field.
    let correctField
        (db: Algebra.Database) (documentId: int64)
        (field: string) (correctedValue: string) (note: string option)
        : Task<Result<unit, string>> =
        task {
            if not (correctableColumns.Contains field) then
                return Error $"Field '{field}' is not correctable"
            else
                // Read current value
                let! rows =
                    db.execReader
                        $"SELECT {field} FROM documents WHERE id = @id"
                        [ ("@id", Database.boxVal documentId) ]
                match rows with
                | [] -> return Error $"Document {documentId} not found"
                | row :: _ ->
                    let r = Prelude.RowReader(row)
                    let originalValue = r.OptString field |> Option.defaultValue ""
                    // Record the correction
                    let! _ =
                        db.execNonQuery
                            """INSERT INTO corrections (document_id, field, original_value, corrected_value, note)
                               VALUES (@doc, @field, @orig, @corr, @note)"""
                            [ ("@doc", Database.boxVal documentId)
                              ("@field", Database.boxVal field)
                              ("@orig", Database.boxVal originalValue)
                              ("@corr", Database.boxVal correctedValue)
                              ("@note", Database.boxVal (note |> Option.defaultValue "")) ]
                    // Update the document
                    let! _ =
                        db.execNonQuery
                            $"UPDATE documents SET {field} = @val, classification_tier = 'manual' WHERE id = @id"
                            [ ("@val", Database.boxVal correctedValue)
                              ("@id", Database.boxVal documentId) ]
                    return Ok ()
        }

    /// Reset comprehension fields so the document is re-queued for understanding.
    let recomprehend (db: Algebra.Database) (documentId: int64) : Task<Result<unit, string>> =
        task {
            let! affected =
                db.execNonQuery
                    """UPDATE documents
                       SET comprehension = NULL, comprehension_schema = NULL,
                           category = 'unclassified', classification_tier = NULL,
                           classification_confidence = NULL, stage = 'extracted'
                       WHERE id = @id"""
                    [ ("@id", Database.boxVal documentId) ]
            if affected = 0 then return Error $"Document {documentId} not found"
            else return Ok ()
        }

    /// List all corrections for prompt tuning export.
    let listCorrections (db: Algebra.Database) (limit: int) : Task<{| DocumentId: int64; Field: string; OriginalValue: string; CorrectedValue: string; Note: string; CreatedAt: string |} list> =
        task {
            let! rows =
                db.execReader
                    """SELECT c.document_id, c.field, c.original_value, c.corrected_value, c.note, c.created_at
                       FROM corrections c ORDER BY c.created_at DESC LIMIT @lim"""
                    [ ("@lim", Database.boxVal (int64 limit)) ]
            return rows |> List.map (fun row ->
                let r = Prelude.RowReader(row)
                {| DocumentId = r.Int64 "document_id" 0L
                   Field = r.String "field" ""
                   OriginalValue = r.String "original_value" ""
                   CorrectedValue = r.String "corrected_value" ""
                   Note = r.String "note" ""
                   CreatedAt = r.String "created_at" "" |})
        }
