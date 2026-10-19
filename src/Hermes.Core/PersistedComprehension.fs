namespace Hermes.Core

open System.Threading.Tasks

/// No chat, extraction, publication, reindex, or write capability is invoked.
/// Shared thread enrichment is deliberately not attributed from proximity.
[<RequireQualifiedAccess>]
module PersistedComprehension =

    let private superseded () =
        ComprehensionContract.NotAvailable "comprehension_changed_during_read"

    let private readCurrent db fs archiveDir (source: ComprehensionSource.Current) () =
        task {
            let! _ = ArchiveContent.readSource db fs archiveDir source.Identity
            let! confirmed = ComprehensionSource.load db archiveDir source.Token.DocumentId
            return confirmed
                   |> Result.bind (fun latest ->
                       if ComprehensionSource.same source latest then Ok latest.Snapshot
                       else Error(superseded ()))
        }

    let private readStable db fs archiveDir (source: ComprehensionSource.Current) =
        task {
            let! publication =
                Generation.readArtifactStable db source.Token source.Folder
                    (readCurrent db fs archiveDir source)
            return
                match publication with
                | Generation.Published result -> result
                | Generation.Superseded -> Error(superseded ())
        }

    let read db fs archiveDir documentId =
        task {
            if documentId <= 0L then
                return Error(ComprehensionContract.InvalidRequest "document_id must be positive")
            else
                try
                    return!
                        ComprehensionSource.load db archiveDir documentId
                        |> Prelude.TaskResult.bind (readStable db fs archiveDir)
                with _ ->
                    return Error(ComprehensionContract.NotAvailable "source_or_publication_unavailable")
        }
