namespace Hermes.Core

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Microsoft.Data.Sqlite

/// Explicit selected-document rebuilds. Never scans, rewrites sources, or calls providers.
[<RequireQualifiedAccess>]
module FileContentReindex =

    let [<Literal>] MaxDocuments = 100

    type Report =
        { Indexed: int64 list
          MetadataOnly: int64 list }

    type Inspection =
        { Indexable: int64 list
          MetadataOnly: int64 list }

    type private Intent = InspectOnly | RebuildIndex

    type private Outcome =
        { DocumentId: int64
          HasContent: bool }

    let private publish
        (snapshot: FileContentReindexSource.Snapshot) content (scope: Algebra.TransactionScope) =
        task {
            do! FileContentReindexSource.validateIn scope snapshot
            return!
                content
                |> Option.map (FileContentIndex.replace scope.execNonQuery snapshot.Identity.DocumentId)
                |> Option.defaultWith (fun () -> FileContentIndex.clear scope.execNonQuery snapshot.Identity.DocumentId)
        }

    let private previewPublication db generation (snapshot: FileContentReindexSource.Snapshot) =
        task {
            do! FileContentReindexSource.requireIdle db snapshot.Identity.DocumentId
            let! latest = FileContentReindexSource.load db snapshot.Identity.DocumentId
            let! current = Generation.isCurrent db generation
            return
                if current && FileContentReindexSource.sameSource snapshot latest
                   && snapshot.ExtractionCurrent = latest.ExtractionCurrent then
                    Generation.Published ()
                else Generation.Superseded
        }

    let private publishFor intent db generation snapshot content =
        match intent with
        | InspectOnly -> previewPublication db generation snapshot
        | RebuildIndex -> Generation.publish db generation (publish snapshot content)

    let private rebuildFenced intent db fs archiveDirectory (expected: FileContentReindexSource.Snapshot) =
        task {
            let documentId = expected.Identity.DocumentId
            do! FileContentReindexSource.requireIdle db documentId
            let! generation = Generation.current db documentId
            let! snapshot = FileContentReindexSource.load db documentId
            if not (FileContentReindexSource.sameSource expected snapshot) then
                invalidOp "Source metadata changed before reindex; retry the selected document"
            let! content = FileContentReindexSource.readContent db fs archiveDirectory snapshot
            match! publishFor intent db generation snapshot content with
            | Generation.Superseded -> return Error "Reindex was superseded by reflow"
            | Generation.Published () ->
                return Ok { DocumentId = documentId; HasContent = Option.isSome content }
        }

    let private rebuildOne intent db fs archiveDirectory documentId =
        task {
            let! snapshot = FileContentReindexSource.load db documentId
            let folder =
                PublicationFence.ArtifactFolder.tryFromMetadata
                    archiveDirectory snapshot.Identity.SavedPath snapshot.FolderPath
                |> Option.defaultWith (fun () -> invalidOp "Archive folder not found")
            return!
                Generation.fencedArtifact documentId folder
                    (fun () -> rebuildFenced intent db fs archiveDirectory snapshot)
        }

    let private logFailure (logger: Algebra.Logger) documentId message =
        let contextual = $"File-content reindex failed for document {documentId}: {message}"
        logger.error contextual
        contextual

    let private attempt intent db fs logger archiveDirectory documentId =
        task {
            try
                let! result = rebuildOne intent db fs archiveDirectory documentId
                return result |> Result.mapError (logFailure logger documentId)
            with
            | :? IOException as error -> return Error(logFailure logger documentId error.Message)
            | :? UnauthorizedAccessException as error -> return Error(logFailure logger documentId error.Message)
            | :? DecoderFallbackException as error -> return Error(logFailure logger documentId error.Message)
            | :? InvalidOperationException as error -> return Error(logFailure logger documentId error.Message)
            | :? SqliteException as error -> return Error(logFailure logger documentId error.Message)
            | :? ArgumentException as error -> return Error(logFailure logger documentId error.Message)
            | :? NotSupportedException as error -> return Error(logFailure logger documentId error.Message)
        }

    let private includeOutcome report outcome =
        if outcome.HasContent then
            { report with Indexed = report.Indexed @ [ outcome.DocumentId ] }
        else
            { report with MetadataOnly = report.MetadataOnly @ [ outcome.DocumentId ] }

    let private step intent db fs logger archiveDirectory state documentId =
        state
        |> Result.map (fun report ->
            task {
                let! result = attempt intent db fs logger archiveDirectory documentId
                return result |> Result.map (includeOutcome report)
            })
        |> Result.defaultWith (fun error -> Task.FromResult(Error error))

    let private run intent db fs logger archiveDirectory (documentIds: int64 list) =
        if List.isEmpty documentIds || documentIds.Length > MaxDocuments then
            Task.FromResult(Error $"Select between 1 and {MaxDocuments} document IDs")
        elif documentIds |> List.exists (fun value -> value <= 0L) then
            Task.FromResult(Error "Document IDs must be positive")
        else
            documentIds
            |> List.distinct
            |> List.sort
            |> Prelude.foldTask (step intent db fs logger archiveDirectory)
                (Ok { Indexed = []; MetadataOnly = [] })

    /// Fail-fast, per-document atomic publication; earlier successes remain committed.
    let rebuild db fs logger archiveDirectory documentIds =
        run RebuildIndex db fs logger archiveDirectory documentIds

    /// Reads the same selected, validated files without transactions or index writes.
    let inspect db fs logger archiveDirectory documentIds =
        task {
            let! result = run InspectOnly db fs logger archiveDirectory documentIds
            return result |> Result.map (fun report ->
                { Indexable = report.Indexed; MetadataOnly = report.MetadataOnly })
        }
