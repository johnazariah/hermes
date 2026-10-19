namespace Hermes.Core

open System.IO
open System.Text
open System.Text.Json

[<RequireQualifiedAccess>]
module internal ComprehensionSource =

    type Current =
        { Snapshot: ComprehensionContract.Snapshot
          Token: Generation.Token
          Folder: PublicationFence.ArtifactFolder
          Identity: ArchiveContent.Identity }

    let private sql =
        $"""SELECT d.*,
                  COALESCE(g.generation, 0) AS current_generation,
                  p.generation AS publication_generation,
                  p.response_json AS publication_json,
                  CASE WHEN {ComprehensionProvenance.currentOutputs}
                  THEN 1 ELSE 0 END AS outputs_current
           FROM documents d
           LEFT JOIN document_generations g ON g.document_id = d.id
           {ComprehensionProvenance.joins "COALESCE(g.generation, 0)"}
           WHERE d.id = @id"""

    let private parseJson text =
        try
            use document = JsonDocument.Parse(text: string)
            if document.RootElement.ValueKind <> JsonValueKind.Object then
                Error(ComprehensionContract.NotAvailable "invalid_canonical_publication")
            else Ok(document.RootElement.Clone())
        with :? JsonException ->
            Error(ComprehensionContract.NotAvailable "invalid_canonical_publication")

    let private identity (reader: Prelude.RowReader) : ComprehensionContract.SourceIdentity =
        { DocumentId = reader.Int64 "id" 0L; SavedPath = reader.String "saved_path" ""
          Sha256 = reader.String "sha256" ""; SourceType = reader.String "source_type" ""
          SourcePath = reader.OptString "source_path"; Account = reader.OptString "account"
          ProviderId = reader.OptString "gmail_id"; ThreadId = reader.OptString "thread_id" }

    let private snapshot (reader: Prelude.RowReader) json : ComprehensionContract.Snapshot =
        { Source = identity reader
          Generation = reader.Int64 "current_generation" 0L
          PublicationGeneration = reader.Int64 "publication_generation" 0L
          PublicationSha256 =
            reader.String "publication_json" "" |> Encoding.UTF8.GetBytes |> SourceFiles.hash
          Canonical = json
          Effective =
            { Category = reader.String "category" ""
              Date = reader.OptString "extracted_date"
              Amount = reader.OptFloat "extracted_amount"
              Vendor = reader.OptString "extracted_vendor" } }

    let private artifactFolder archiveDir (reader: Prelude.RowReader) =
        PublicationFence.ArtifactFolder.tryFromMetadata
            archiveDir (reader.String "saved_path" "") (reader.OptString "folder_path")
        |> Option.map Ok
        |> Option.defaultValue (Error "invalid_artifact_folder")
        |> Result.bind (fun folder ->
            PublicationFence.ArtifactFolder.resolve archiveDir folder
            |> Result.bind (fun path ->
                ArchiveContent.resolvePath archiveDir (Path.Combine(path, "thread.comprehension.json")))
            |> Result.map (fun _ -> folder))
        |> Result.mapError ComprehensionContract.NotAvailable

    let private fromRow archiveDir row =
        let reader = Prelude.RowReader row
        if reader.Int64 "outputs_current" 0L <> 1L then
            Error(ComprehensionContract.NotAvailable "comprehension_outputs_not_current")
        elif reader.OptString "publication_json" |> Option.isNone then
            Error(ComprehensionContract.NotAvailable "current_canonical_publication_missing")
        else
            parseJson (reader.String "publication_json" "")
            |> Result.bind (fun json ->
                artifactFolder archiveDir reader |> Result.map (fun folder ->
                    let value = snapshot reader json
                    { Snapshot = value; Folder = folder
                      Token = { DocumentId = value.Source.DocumentId; Value = value.Generation }
                      Identity =
                        { DocumentId = value.Source.DocumentId; SavedPath = value.Source.SavedPath
                          Sha256 = value.Source.Sha256 } }))

    let load (db: Algebra.Database) archiveDir documentId =
        task {
            let! extraction = db.tableExists "extraction"
            let! comprehension = db.tableExists "comprehension"
            if not (extraction && comprehension) then
                return Error(ComprehensionContract.NotAvailable "comprehension_outputs_not_available")
            else
                let! rows = db.execReader sql [ "@id", Database.boxVal documentId ]
                return rows |> List.tryHead |> Option.map (fromRow archiveDir)
                       |> Option.defaultValue (Error ComprehensionContract.Missing)
        }

    let same first second =
        first.Identity = second.Identity
        && first.Token = second.Token
        && first.Snapshot.Source = second.Snapshot.Source
        && first.Snapshot.PublicationGeneration = second.Snapshot.PublicationGeneration
        && first.Snapshot.PublicationSha256 = second.Snapshot.PublicationSha256
        && first.Snapshot.Effective = second.Snapshot.Effective
        && PublicationFence.ArtifactFolder.identities first.Folder =
           PublicationFence.ArtifactFolder.identities second.Folder
