module Hermes.Tests.FileKeywordFixtures

open System
open System.Collections.Concurrent
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture

let sourcePath = "/archive/keyword/source.png"
let sourceText = "immutable synthetic image fixture"

let ingestBody (server: Server) text =
    task {
        let fixture: EmailChannelFixtures.Fixture =
            { Database = server.Db; Files = server.Files
              Logs = ConcurrentQueue<string>(); Logger = TestHelpers.silentLogger
              Clock = TestHelpers.defaultClock
              Config = { TestHelpers.testConfig "/archive" with MinAttachmentSize = 4 }
              Account = "keyword-account" }
        let incoming = EmailChannelFixtures.message "keyword-message" "keyword-thread" (Some text)
        let! result = EmailChannelFixtures.run fixture (EmailChannelFixtures.single incoming []) 1
        Assert.Empty(result.Summary.Errors)
        Assert.Equal(1, result.Summary.MessagesProcessed)
        return result.Documents |> List.exactlyOne
    }

let dependencies (server: Server) text : Stages.Deps =
    { Fs = server.Files.Fs; Db = server.Db; Logger = TestHelpers.silentLogger
      Clock = TestHelpers.defaultClock
      Extractor =
        { Interpreters.nullTextExtractor with
            extractImage = fun _ -> Task.FromResult(Ok text) }
      Embedder = None; ChatProvider = None; TriageProvider = None
      ContentRules = []; ComprehensionPrompt = None; TriagePrompt = None
      Preferences = ""; ArchiveDir = "/archive" }

let private extractionDag server text =
    let extraction =
        StagesV5.standardStages (dependencies server text)
        |> List.find (fun stage -> stage.Name = "extract")
    TestHelpers.standardV5Stages
    |> List.map (fun stage -> if stage.Name = "extract" then { stage with Process = extraction.Process } else stage)
    |> PipelineV5.buildDag
    |> Result.defaultWith failwith

let replay server text =
    task {
        let dag = extractionDag server text
        let! processed =
            PipelineV5.processStageForDag dag dag.Stages.["extract"] server.Db TestHelpers.silentLogger
                (PipelineV5.createGpuScheduler TestHelpers.silentLogger) 1
                (TimeSpan.FromMinutes 1.0) CancellationToken.None
        Assert.Equal(1, processed)
    }

let seedExtracted server text =
    task {
        let! documentId = seedSource server sourcePath sourceText
        do! replay server text
        return documentId
    }

let httpSearch (server: Server) (query: string) =
    get server ("/api/search?q=" + Uri.EscapeDataString query)

let mcpSearch (server: Server) (query: string) =
    task {
        let! response = mcpCall server "hermes_search" (box {| query = query; limit = 20 |})
        return (payload response).GetProperty("results")
    }

let textProperty (name: string) (value: JsonElement) =
    value.GetProperty(name).GetString() |> Option.ofObj |> Option.defaultValue ""

let assertHit
    (documentId: int64) (path: string) (resultType: string) (marker: string)
    (results: JsonElement) =
    Assert.Equal(1, results.GetArrayLength())
    let hit = results.[0]
    Assert.Equal(documentId, hit.GetProperty("id").GetInt64())
    Assert.Equal(path, textProperty "path" hit)
    Assert.Equal(resultType, textProperty "resultType" hit)
    Assert.Contains(marker, textProperty "snippet" hit)
    Assert.True(hit.GetProperty("score").GetDouble() <= 0.0)

let assertViews server query documentId path resultType =
    task {
        let! http = httpSearch server query
        assertHit documentId path resultType query http
        let! mcp = mcpSearch server query
        assertHit documentId path resultType query mcp
    }

let assertAbsent server query =
    task {
        let! http = httpSearch server query
        let! mcp = mcpSearch server query
        Assert.Equal(0, http.GetArrayLength())
        Assert.Equal(0, mcp.GetArrayLength())
    }

let metadataSnapshot (server: Server) =
    [ "documents"; "messages"; "archive_source_claims"; "stage_completions"
      "corrections"; "tags"; "extraction"; "dead_letters"
      "document_generations"; "reflow_operations"; "reflow_operation_stages" ]
    |> Prelude.foldTask
        (fun collected table ->
            task {
                let! rows = server.Db.execReader $"SELECT * FROM {table} ORDER BY rowid" []
                return collected |> Map.add table rows
            })
        Map.empty

let fileHashes (server: Server) =
    Seq.append server.Files.Files.Keys server.Files.Bytes.Keys
    |> Seq.distinct
    |> Seq.sort
    |> Seq.toList
    |> Prelude.foldTask
        (fun collected path ->
            task {
                let! bytes = server.Files.Fs.readAllBytes path
                return collected |> Map.add path (SourceFiles.hash bytes)
            })
        Map.empty

let completeSyntheticExtraction (server: Server) documentId =
    task {
        let! _ =
            server.Db.execNonQuery
                """INSERT INTO extraction(document_id, method) VALUES (@id, 'synthetic');
                   INSERT INTO stage_completions(document_id, stage_name) VALUES (@id, 'extract');"""
                [ "@id", Database.boxVal documentId ]
        return ()
    }

let rebuild (server: Server) ids =
    FileContentReindex.rebuild server.Db server.Files.Fs TestHelpers.silentLogger "/archive" ids
