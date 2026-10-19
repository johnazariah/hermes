namespace Hermes.Core

open System.Threading.Tasks

/// File selection and currentness checks shared by preview and publication.
[<RequireQualifiedAccess>]
module internal FileContentReindexSource =

    type Snapshot =
        { Identity: ArchiveContent.Identity
          SourceType: string
          FolderPath: string option
          ExtractionCurrent: bool
          ExtractionTableExists: bool }

    let private extractionCurrent extractionExists =
        if extractionExists then
            """EXISTS(SELECT 1 FROM extraction e WHERE e.document_id = d.id)
               AND EXISTS(SELECT 1 FROM stage_completions s
                          WHERE s.document_id = d.id AND s.stage_name = 'extract')"""
        else "0"

    let private fromRow extractionExists row =
        let reader = Prelude.RowReader(row)
        { Identity =
            { DocumentId = reader.Int64 "id" 0L
              SavedPath = reader.String "saved_path" ""
              Sha256 = reader.String "sha256" "" }
          SourceType = reader.String "source_type" ""
          FolderPath = reader.OptString "folder_path"
          ExtractionCurrent = reader.Int64 "extraction_current" 0L = 1L
          ExtractionTableExists = extractionExists }

    let load (db: Algebra.Database) documentId =
        task {
            let! extractionExists = db.tableExists "extraction"
            let current = extractionCurrent extractionExists
            let! rows =
                db.execReader
                    $"SELECT d.*, ({current}) AS extraction_current FROM documents d WHERE d.id = @id"
                    [ "@id", Database.boxVal documentId ]
            return rows |> List.tryHead |> Option.map (fromRow extractionExists)
                   |> Option.defaultWith (fun () -> invalidOp $"Document not found: {documentId}")
        }

    let sameSource first second =
        first.Identity = second.Identity
        && first.SourceType = second.SourceType
        && first.FolderPath = second.FolderPath

    /// Never recover or steal a persisted attempt lease as part of indexing.
    let requireIdle (db: Algebra.Database) documentId =
        task {
            let! rows =
                db.execReader
                    "SELECT stage_name FROM pipeline_stage_attempts WHERE document_id = @id"
                    [ "@id", Database.boxVal documentId ]
            if not (List.isEmpty rows) then
                invalidOp $"Document {documentId} has an active pipeline attempt; stop processing and retry"
        }

    let readContent db fs archiveDirectory (snapshot: Snapshot)
        : Task<FileContentIndex.Content option> =
        task {
            if snapshot.SourceType <> "email_body" && not snapshot.ExtractionCurrent then
                return None
            else
                let! source = ArchiveContent.readSource db fs archiveDirectory snapshot.Identity
                if snapshot.SourceType = "email_body" then
                    let content: FileContentIndex.Content =
                        { Kind = FileContentIndex.EmailBody; Text = ArchiveContent.decode source.Bytes }
                    return Some content
                else
                    let! text = ArchiveContent.readExtraction db fs archiveDirectory source.Path
                    let content: FileContentIndex.Content =
                        { Kind = FileContentIndex.Extraction; Text = text }
                    return Some content
        }

    let private parameters snapshot =
        [ "@id", Database.boxVal snapshot.Identity.DocumentId
          "@path", Database.boxVal snapshot.Identity.SavedPath
          "@sha", Database.boxVal snapshot.Identity.Sha256
          "@type", Database.boxVal snapshot.SourceType
          "@folder",
              (snapshot.FolderPath |> Option.map Database.boxVal
               |> Option.defaultValue (Database.boxVal System.DBNull.Value))
          "@ready", Database.boxVal (if snapshot.ExtractionCurrent then 1 else 0) ]

    /// Only scoped SQL runs here; file reads have already finished.
    let validateIn (scope: Algebra.TransactionScope) snapshot =
        task {
            let current = extractionCurrent snapshot.ExtractionTableExists
            let! rows =
                scope.execReader
                    $"""SELECT d.id FROM documents d
                        WHERE d.id = @id AND d.saved_path = @path AND d.sha256 = @sha
                          AND d.source_type = @type AND d.folder_path IS @folder
                          AND ({current}) = @ready
                          AND NOT EXISTS(SELECT 1 FROM pipeline_stage_attempts a
                                         WHERE a.document_id = d.id)"""
                    (parameters snapshot)
            if List.isEmpty rows then
                invalidOp "Source, extraction currentness, or pipeline attempt changed during reindex"
        }
