module Hermes.Tests.QueryEncodingRaceHttpTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures

type private Scenario =
    { Target: int64
      InitialGeneration: int64
      Expected: int64 list
      Before: Checkpoint }

let private delayedEmbedding gate query =
    task {
        do! hold gate
        return! encoder.embed query
    }

let private delayedEncoder gate : Algebra.EmbeddingClient =
    { encoder with embed = delayedEmbedding gate }

let private expected mode documents =
    match mode with
    | SearchContract.Semantic -> [ List.head documents ]
    | SearchContract.Hybrid -> [ List.head documents; List.last documents ]
    | SearchContract.Keyword -> invalidArg (nameof mode) "This fixture tests vector modes"

let private prepare selected mode server =
    task {
        let! documents = seedSearch server
        Assert.Equal(3, documents.Length)
        let target = documents |> List.item 1
        let! before = checkpoint server
        let! baseline = search selected mode server
        assertReply selected 200 false baseline
        Assert.Contains<int64>(target, searchResults selected baseline |> ids)
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(before, after)
        let! generation = Generation.current server.Db target
        let value: Scenario =
            { Target = target; InitialGeneration = generation.Value
              Expected = expected mode documents; Before = before }
        return value
    }

let private assertVectorInvalidation (server: Server) scenario =
    task {
        let! generation = Generation.current server.Db scenario.Target
        Assert.Equal(scenario.InitialGeneration + 1L, generation.Value)
        let! rows = server.Db.execReader
                        """SELECT
                             (SELECT COUNT(*) FROM document_chunks WHERE document_id=@id) +
                             (SELECT COUNT(*) FROM embedding WHERE document_id=@id) +
                             (SELECT COUNT(*) FROM stage_completions
                              WHERE document_id=@id AND stage_name='embed') AS remaining"""
                        [ "@id", Database.boxVal scenario.Target ]
        let reader = rows |> List.exactlyOne |> Prelude.RowReader
        Assert.Equal(0L, reader.Int64 "remaining" -1L)
    }

let private invalidate server scenario () =
    task {
        do! applyReflow server scenario.Target "reembed"
        do! assertVectorInvalidation server scenario
        return! checkpoint server
    }

let private assertRemaining selected mode scenario reply =
    assertReply selected 200 false reply
    let actual = searchResults selected reply |> ids
    Assert.Equal<int64 list>(scenario.Expected |> List.sort, actual)
    Assert.DoesNotContain<int64>(scenario.Target, actual)
    if selected = Mcp then
        Assert.Equal(SearchContract.modeName mode, text "mode" reply.Body)
        let coverage = reply.Body.GetProperty("coverage")
        Assert.Equal(1, coverage.GetProperty("indexedDocuments").GetInt32())
        Assert.Equal(3, coverage.GetProperty("eligibleDocuments").GetInt32())

let private scenario gate selected mode server =
    task {
        let! prepared = prepare selected mode server
        arm gate
        let pending = search selected mode server
        let! completed = interleave gate pending (invalidate server prepared)
        assertRemaining selected mode prepared completed.Reply
        let! repeated = search selected mode server
        assertRemaining selected mode prepared repeated
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(completed.State, after)
        Assert.Equal<Map<string, string>>(prepared.Before.FileHashes, after.FileHashes)
    }

[<Theory>]
[<InlineData(false, "semantic")>]
[<InlineData(true, "semantic")>]
[<InlineData(false, "hybrid")>]
[<InlineData(true, "hybrid")>]
[<Trait("Category", "Integration")>]
let ``Paused query encoding cannot retain vector evidence invalidated by actual HTTP reflow`` useMcp mode =
    let selected = SearchContract.parseMode (Some mode) |> Result.defaultWith failwith
    let gate = createGate ()
    withConfiguredServer (Some(delayedEncoder gate)) McpAccess.readOnly [] None
        (scenario gate (transport useMcp) selected)
