module Hermes.Tests.CorrectedExtractionHttpTests

open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture

let private sourcePath = "/archive/tax/source.png"
let private sourceContent = "immutable image fixture"
let private correctionNote = "checked against original tax document"
let private generatedText =
    "Vendor: Generated replay\nDate: 2031-02-03\nTotal: $999.00\nABN: 12 345 678 901"

let private submitCorrections allFields server documentId =
    task {
        let fields =
            [ yield {| field = "extracted_amount"; value = "42.5" |}
              if allFields then
                  yield {| field = "extracted_date"; value = "2025-06-30" |}
                  yield {| field = "extracted_vendor"; value = "User verified vendor" |} ]
        let! first = post server $"/api/documents/{documentId}/correct" (box {| corrections = fields; note = correctionNote |})
        Assert.True(first.GetProperty("corrected").GetBoolean())
        let! latest =
            post server $"/api/documents/{documentId}/correct"
                (box {| corrections = [ {| field = "extracted_amount"; value = "75.25" |} ]; note = correctionNote |})
        Assert.True(latest.GetProperty("corrected").GetBoolean())
    }

let private history (server: Server) documentId =
    server.Db.execReader
        "SELECT * FROM corrections WHERE document_id=@id ORDER BY id"
        [ "@id", Database.boxVal documentId ]

let private assertInvalidated allFields server documentId =
    task {
        let! row = readDocument server documentId
        let reader = Prelude.RowReader(row)
        Assert.Equal(Some 75.25, reader.OptFloat "extracted_amount")
        Assert.Equal((if allFields then Some "2025-06-30" else None), reader.OptString "extracted_date")
        Assert.Equal((if allFields then Some "User verified vendor" else None), reader.OptString "extracted_vendor")
        Assert.True((reader.OptString "extracted_at").IsNone)
        Assert.True((reader.OptString "extracted_abn").IsNone)
        let! generated = server.Db.execScalar "SELECT COUNT(*) FROM extraction WHERE document_id=@id" [ "@id", Database.boxVal documentId ]
        Assert.Equal(0L, unbox<int64> generated)
    }

let private dependencies (server: Server) : Stages.Deps =
    let extractor =
        { Interpreters.nullTextExtractor with
            extractImage = fun _ -> Task.FromResult(Ok generatedText) }
    { Fs = server.Files.Fs; Db = server.Db; Logger = TestHelpers.silentLogger
      Clock = TestHelpers.defaultClock; Extractor = extractor; Embedder = None
      ChatProvider = None; TriageProvider = None; ContentRules = []
      ComprehensionPrompt = None; TriagePrompt = None; Preferences = ""; ArchiveDir = "/archive" }

let private replay server =
    task {
        let realExtract =
            StagesV5.standardStages (dependencies server) |> List.find (fun stage -> stage.Name = "extract")
        let stages =
            TestHelpers.standardV5Stages
            |> List.map (fun stage -> if stage.Name = "extract" then { stage with Process = realExtract.Process } else stage)
        let dag = PipelineV5.buildDag stages |> Result.defaultWith failwith
        let! processed =
            PipelineV5.processStageForDag dag dag.Stages.["extract"] server.Db TestHelpers.silentLogger
                (PipelineV5.createGpuScheduler TestHelpers.silentLogger) 1
                (System.TimeSpan.FromMinutes 1.0) CancellationToken.None
        Assert.Equal(1, processed)
    }

// Accept the existing REST option representation, but compare its actual value.
let private optionValue (value: JsonElement) =
    if value.ValueKind = JsonValueKind.Object then value.GetProperty("value")
    else value

let private assertValues allFields (date: JsonElement) (amount: JsonElement) (vendor: JsonElement) =
    Assert.Equal((if allFields then "2025-06-30" else "2031-02-03"), (optionValue date).GetString())
    Assert.Equal(75.25, (optionValue amount).GetDouble())
    Assert.Equal((if allFields then "User verified vendor" else "Generated replay"), (optionValue vendor).GetString())

let private assertViews allFields server documentId =
    task {
        let! document = get server $"/api/documents/{documentId}"
        let summary = document.GetProperty("summary")
        assertValues allFields (summary.GetProperty("extractedDate"))
            (summary.GetProperty("extractedAmount")) (summary.GetProperty("vendor"))
        let! response = mcpCall server "hermes_list_documents" (box {| since_id = 0; limit = 10 |})
        let feed = payload response
        Assert.Equal(1, feed.GetArrayLength())
        Assert.Equal(documentId, feed.[0].GetProperty("id").GetInt64())
        assertValues allFields (feed.[0].GetProperty("extracted_date"))
            (feed.[0].GetProperty("extracted_amount")) (feed.[0].GetProperty("extracted_vendor"))
    }

let private assertGeneratedRow row =
    let reader = Prelude.RowReader(row)
    Assert.Equal("2031-02-03", reader.String "extracted_date" "")
    Assert.Equal(Some 999.0, reader.OptFloat "extracted_amount")
    Assert.Equal("Generated replay", reader.String "extracted_vendor" "")
    Assert.Equal("12345678901", reader.String "live_abn" "")
    Assert.Equal("ollama_vision", reader.String "live_method" "")
    Assert.NotEqual<string>("2000-01-01", reader.String "live_at" "")

let private assertGenerated (server: Server) documentId =
    task {
        let! rows =
            server.Db.execReader
                """SELECT e.*,d.extracted_abn AS live_abn,d.extraction_method AS live_method,
                          d.extracted_at AS live_at
                   FROM extraction e JOIN documents d ON d.id=e.document_id WHERE d.id=@id"""
                [ "@id", Database.boxVal documentId ]
        rows |> List.exactlyOne |> assertGeneratedRow
    }

let private assertIdentity allFields server documentId beforeHistory =
    task {
        let! row = readDocument server documentId
        let reader = Prelude.RowReader(row)
        let! bytes = server.Files.Fs.readAllBytes sourcePath
        Assert.Equal<byte>(Encoding.UTF8.GetBytes(sourceContent), bytes)
        Assert.Equal(sourcePath, reader.String "saved_path" "")
        Assert.Equal("/original/source.png", reader.String "source_path" "")
        Assert.Equal(SourceFiles.hash bytes, reader.String "sha256" "")
        let! afterHistory = history server documentId
        Assert.Equal<Map<string, obj> list>(beforeHistory, afterHistory)
        Assert.Equal((if allFields then 4 else 2), afterHistory.Length)
        afterHistory |> List.iter (fun item -> Assert.Equal(correctionNote, (Prelude.RowReader item).String "note" ""))
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``HTTP corrections remain effective through reextract and real extraction replay`` allFields =
    withServer None (fun server ->
        task {
            let! documentId = seedSource server sourcePath sourceContent
            do! seedCompletions server documentId
            do! submitCorrections allFields server documentId
            let! beforeHistory = history server documentId
            do! applyReflow server documentId "reextract"
            do! assertInvalidated allFields server documentId
            do! replay server
            do! assertViews allFields server documentId
            do! assertGenerated server documentId
            do! assertIdentity allFields server documentId beforeHistory
        })
