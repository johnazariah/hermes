module Hermes.Tests.SourceIdentityRaceTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures
open Hermes.Tests.SourceIdentityRaceFixtures

let private assertNoCurrentOutput (server: Server) documentId =
    task {
        let! value =
            server.Db.execScalar
                """SELECT
                     (SELECT COUNT(*) FROM stage_completions WHERE document_id=@id) +
                     (SELECT COUNT(*) FROM document_content_fts WHERE rowid=@id) +
                     (SELECT COUNT(*) FROM extraction WHERE document_id=@id) +
                     (SELECT COUNT(*) FROM pipeline_stage_attempts WHERE document_id=@id)"""
                [ "@id", Database.boxVal documentId ]
        Assert.Equal(0L, Assert.IsType<int64>(value))
        do! assertAbsent server staleMarker
    }

let private assertPreserved server documentId beforeHistory beforeHashes =
    task {
        let! afterHistory = history server
        let! afterHashes = sourceHashes server
        Assert.Equal<Map<string, Map<string, obj> list>>(beforeHistory, afterHistory)
        Assert.Equal<Map<string, string>>(beforeHashes, afterHashes)
        let! row = readDocument server documentId
        let reader = Prelude.RowReader row
        Assert.Equal(replacementPath, reader.String "saved_path" "")
        Assert.Equal(beforeHashes.[replacementPath], reader.String "sha256" "")
        Assert.Equal(Some 42.5, reader.OptFloat "extracted_amount")
        Assert.Equal("manual", reader.String "classification_tier" "")
        Assert.Equal("tax", reader.String "category" "")
    }

let private interrupt server documentId (pause: Pause) (pending: Task<int>) =
    task {
        try
            do! pause.Entered.Task.WaitAsync(timeout)
            do! reassign server documentId
            do! assertAbsent server staleMarker
        finally
            pause.Resume.TrySetResult(()) |> ignore
        let! processed = pending.WaitAsync(timeout)
        Assert.Equal(0, processed)
    }

let private replayReplacement server documentId beforeHistory beforeHashes =
    task {
        do! replay server replacementMarker
        do! assertViews server replacementMarker documentId replacementPath "document"
        do! assertAbsent server staleMarker
        do! assertPreserved server documentId beforeHistory beforeHashes
    }

let private scenario point server =
    task {
        let! documentId = prepare server
        let! beforeHistory = history server
        let! beforeHashes = sourceHashes server
        let! generation = Generation.current server.Db documentId
        let pause = createPause ()
        let pending = start server point pause
        do! interrupt server documentId pause pending
        let! latest = Generation.current server.Db documentId
        Assert.Equal(generation.Value + 1L, latest.Value)
        do! assertNoCurrentOutput server documentId
        do! assertPreserved server documentId beforeHistory beforeHashes
        do! replayReplacement server documentId beforeHistory beforeHashes
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``V5 rejects source A when authority-valid B is assigned while A provider is paused`` () =
    withServer None (scenario Provider)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``V5 rejects A stage output after A artifact and index publication but before output publication`` () =
    withServer None (scenario Index)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``V5 rejects final A completion when B is assigned after successful A stage output`` () =
    withServer None (scenario Output)
