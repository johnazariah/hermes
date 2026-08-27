namespace Hermes.Core

open System
open System.IO
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Atomic, metadata-only document reclassification.
[<RequireQualifiedAccess>]
module Reclassification =

    type Provenance =
        | Manual
        | Content of confidence: float

    type Error =
        | InvalidCategory
        | DocumentNotFound of documentId: int64
        | SourceMissing of path: string
        | ConcurrentChange of documentId: int64
        | DatabaseFailure of message: string

    type Outcome =
        { DocumentId: int64
          PreviousCategory: string
          Category: string
          Changed: bool
          SavedPath: string
          Sha256: string }

    type private Identity =
        { Category: string
          SavedPath: string
          Sha256: string
          Tier: string option
          Confidence: float option }

    let describeError = function
        | InvalidCategory -> "Category must not be empty"
        | DocumentNotFound id -> $"Document {id} not found"
        | SourceMissing path -> $"Document source is missing: {path}"
        | ConcurrentChange id ->
            $"Document {id} changed while it was being reclassified"
        | DatabaseFailure message -> $"Reclassification failed: {message}"

    let private loadIdentity (db: Algebra.Database) documentId =
        task {
            let! rows =
                db.execReader
                    """SELECT category, saved_path, sha256,
                              classification_tier, classification_confidence
                       FROM documents WHERE id = @id"""
                    [ "@id", Database.boxVal documentId ]

            return
                rows
                |> List.tryHead
                |> Option.map (fun row ->
                    let reader = Prelude.RowReader(row)
                    { Category = reader.String "category" ""
                      SavedPath = reader.String "saved_path" ""
                      Sha256 = reader.String "sha256" ""
                      Tier = reader.OptString "classification_tier"
                      Confidence = reader.OptFloat "classification_confidence" })
        }

    let private provenanceValues = function
        | Manual -> "manual", Database.boxVal DBNull.Value
        | Content confidence -> "content", Database.boxVal confidence

    let private optionValue value =
        value
        |> Option.map Database.boxVal
        |> Option.defaultValue (Database.boxVal DBNull.Value)

    let private provenanceChanged identity = function
        | Manual ->
            identity.Tier <> Some "manual"
            || identity.Confidence.IsSome
        | Content confidence ->
            identity.Tier <> Some "content"
            || identity.Confidence <> Some confidence

    let private sourcePath (archiveDir: string) (savedPath: string) =
        if Path.IsPathRooted(savedPath) then savedPath
        else Path.Combine(archiveDir, savedPath)

    let private parameters documentId category identity provenance =
        let tier, confidence = provenanceValues provenance
        [ "@id", Database.boxVal documentId
          "@category", Database.boxVal category
          "@tier", Database.boxVal tier
          "@confidence", confidence
          "@savedPath", Database.boxVal identity.SavedPath
          "@sha256", Database.boxVal identity.Sha256
          "@oldCategory", Database.boxVal identity.Category
          "@oldTier", optionValue identity.Tier
          "@oldConfidence", optionValue identity.Confidence ]

    let private updateDocument
        (db: Algebra.Database)
        documentId category identity provenance =
        task {
            try
                let! affected =
                    db.execNonQuery
                        """UPDATE documents
                           SET category = @category,
                               classification_tier = @tier,
                               classification_confidence = @confidence
                           WHERE id = @id
                             AND saved_path = @savedPath
                             AND sha256 = @sha256
                             AND category IS @oldCategory
                             AND classification_tier IS @oldTier
                             AND classification_confidence IS @oldConfidence"""
                        (parameters documentId category identity provenance)

                return
                    if affected = 1 then Ok ()
                    else Error (ConcurrentChange documentId)
            with
            | :? SqliteException as ex ->
                return Error (DatabaseFailure ex.Message)
        }

    let private outcome documentId category provenance identity =
        { DocumentId = documentId
          PreviousCategory = identity.Category
          Category = category
          Changed =
            identity.Category <> category
            || provenanceChanged identity provenance
          SavedPath = identity.SavedPath
          Sha256 = identity.Sha256 }

    let reclassifyWith
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (documentId: int64)
        (newCategory: string)
        (provenance: Provenance)
        : Task<Result<Outcome, Error>> =
        task {
            if String.IsNullOrWhiteSpace(newCategory) then
                return Error InvalidCategory
            else
                let category = newCategory.Trim()
                let! identity = loadIdentity db documentId

                match identity with
                | None -> return Error (DocumentNotFound documentId)
                | Some value ->
                    let path = sourcePath archiveDir value.SavedPath
                    if not (fs.fileExists path) then
                        return Error (SourceMissing path)
                    else
                        let! result =
                            updateDocument db documentId category value provenance
                        return result |> Result.map (fun () ->
                            outcome documentId category provenance value)
        }

    let reclassify db fs archiveDir documentId category =
        reclassifyWith db fs archiveDir documentId category Manual

    let reclassifyFromContent db fs archiveDir documentId category confidence =
        reclassifyWith
            db fs archiveDir documentId category (Content confidence)
