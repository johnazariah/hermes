module Hermes.Tests.SourceIdentityRaceFixtures

open System
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures

type PausePoint = Provider | Index | Output

type Pause =
    { Entered: TaskCompletionSource<unit>
      Resume: TaskCompletionSource<unit> }

let staleMarker = "sourcearacesapphiremarker"
let replacementMarker = "sourcebcurrentemeraldmarker"
let replacementPath = "/archive/replacement/b.png"
let replacementText = "separately claimed immutable synthetic source B"

let createPause () : Pause =
    { Entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
      Resume = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously) }

let private hold (pause: Pause) =
    task {
        pause.Entered.TrySetResult(()) |> ignore
        do! pause.Resume.Task.WaitAsync(TimeSpan.FromSeconds 30.0)
    }

let private extract point pause (bytes: byte array) =
    task {
        Assert.Equal(SourceFiles.hash (Encoding.UTF8.GetBytes sourceText), SourceFiles.hash bytes)
        if point = Provider then do! hold pause
        return Ok staleMarker
    }

let private indexed point pause (seen: TaskCompletionSource<unit>) (db: Algebra.Database) callback =
    task {
        let! result = db.inTransaction callback
        if point = Index && seen.TrySetResult(()) then do! hold pause
        return result
    }

let private runExtraction point pause (stage: PipelineV5.StageDefinition) db logger execution =
    task {
        let seen = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let wrapped: Algebra.Database =
            { db with inTransaction = indexed point pause seen db }
        let! outcome = stage.Process wrapped logger execution
        if point = Output then
            Assert.Equal(PipelineV5.Completed, outcome)
            do! hold pause
        return outcome
    }

let private dag server point pause =
    let original = dependencies server staleMarker
    let deps: Stages.Deps =
        { original with Extractor = { original.Extractor with extractImage = extract point pause } }
    let realExtract =
        StagesV5.standardStages deps |> List.find (fun stage -> stage.Name = "extract")
    TestHelpers.standardV5Stages
    |> List.map (fun stage ->
        if stage.Name = "extract" then
            { stage with Process = runExtraction point pause realExtract }
        else stage)
    |> PipelineV5.buildDag
    |> Result.defaultWith failwith

let start server point pause =
    let stages = dag server point pause
    PipelineV5.processStageForDag stages stages.Stages.["extract"] server.Db TestHelpers.silentLogger
        (PipelineV5.createGpuScheduler TestHelpers.silentLogger) 1
        (TimeSpan.FromMinutes 1.0) CancellationToken.None

let prepare (server: Server) =
    task {
        let! documentId = seedSource server sourcePath sourceText
        do! SourceFiles.writeNew server.Db server.Files.Fs replacementPath (Encoding.UTF8.GetBytes replacementText)
        let! _ =
            server.Db.execNonQuery
                """INSERT INTO corrections(document_id,field,corrected_value,note)
                   VALUES (@id,'extracted_amount','42.5','retained synthetic financial correction');
                   INSERT INTO tags(document_id,tag,source) VALUES (@id,'owner-tag','user');
                   INSERT INTO stage_publications(document_id,stage_name,generation,response_json)
                   VALUES (@id,'deep-comprehend',0,'{"history":"retained"}');
                   UPDATE documents SET extracted_amount=42.5,classification_tier='manual',category='tax'
                   WHERE id=@id;"""
                [ "@id", Database.boxVal documentId ]
        return documentId
    }

let reassign (server: Server) documentId =
    task {
        let! _ =
            server.Db.execNonQuery
                """UPDATE documents SET saved_path=@path,folder_path='/archive/replacement',sha256=@sha
                   WHERE id=@id"""
                [ "@id", Database.boxVal documentId; "@path", Database.boxVal replacementPath
                  "@sha", Database.boxVal (SourceFiles.hash (Encoding.UTF8.GetBytes replacementText)) ]
        return ()
    }

let history (server: Server) =
    [ "archive_source_claims"; "corrections"; "tags"; "stage_publications"
      "learned_pattern_evidence"; "reflow_operations"; "reflow_operation_stages" ]
    |> Prelude.foldTask
        (fun collected table ->
            task {
                let! rows = server.Db.execReader $"SELECT * FROM {table} ORDER BY rowid" []
                return collected |> Map.add table rows
            })
        Map.empty

let sourceHashes (server: Server) =
    [ sourcePath; replacementPath ]
    |> Prelude.foldTask
        (fun collected path ->
            task {
                let! bytes = server.Files.Fs.readAllBytes path
                return collected |> Map.add path (SourceFiles.hash bytes)
            })
        Map.empty
