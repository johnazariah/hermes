module Hermes.Tests.ReembedReadHttpTests

open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures
open Hermes.Tests.PersistedReadRaceFixtures
open Hermes.Tests.ReembedReadFixtures

let private assertLink server (expected: ReembedReadFixtures.Expected) =
    task {
        let! rows = server.Db.execReader
                        """SELECT cp.*,ro.operation_kind FROM comprehension_provenance cp
                           JOIN reflow_operations ro ON ro.id=cp.operation_id
                           WHERE cp.document_id=@id AND cp.generation=@generation"""
                        [ "@id", Database.boxVal expected.DocumentId
                          "@generation", Database.boxVal expected.Generation ]
        let reader = rows |> List.exactlyOne |> Prelude.RowReader
        Assert.Equal(expected.PublicationGeneration, reader.Int64 "publication_generation" -1L)
        Assert.Equal("deep-comprehend", reader.String "stage_name" "")
        Assert.Equal("reembed", reader.String "operation_kind" "")
    }

let private assertCompleted server (expected: ReembedReadFixtures.Expected) =
    task {
        let! rows = server.Db.execReader
                        """SELECT lifecycle FROM reflow_operations
                           WHERE document_id=@id AND operation_kind='reembed' ORDER BY id DESC LIMIT 1"""
                        [ "@id", Database.boxVal expected.DocumentId ]
        let reader = rows |> List.exactlyOne |> Prelude.RowReader
        Assert.Equal("completed", reader.String "lifecycle" "")
    }

let private round path server (expected: ReembedReadFixtures.Expected) =
    task {
        do! applyReflow server expected.DocumentId "reembed"
        let current = { expected with Generation = expected.Generation + 1L }
        do! verifyPhysical path server current
        do! applyReflow server expected.DocumentId "reembed"
        do! verifyPhysical path server current
        do! assertLink server current
        do! completeEmbedding server
        do! assertCompleted server current
        do! verifyPhysical path server current
        return current
    }

let private assertUnchangedPublications server (expected: ReembedReadFixtures.Expected) =
    task {
        let! rows = server.Db.execReader
                        """SELECT generation,response_json FROM stage_publications
                           WHERE document_id=@id AND stage_name='deep-comprehend' ORDER BY generation"""
                        [ "@id", Database.boxVal expected.DocumentId ]
        Assert.Equal(2, rows.Length)
        let origin = rows |> List.last |> Prelude.RowReader
        Assert.Equal(expected.PublicationGeneration, origin.Int64 "generation" -1L)
        Assert.Equal(expected.Canonical, origin.String "response_json" "")
    }

let private lifecycle path server =
    task {
        let! original = seedAt server 7L "source" canonical
        let siblingJson = canonical.Replace("1234.56", "999999.99")
        let! sibling = seedAt server 7L "sibling" siblingJson
        server.Files.Put "/archive/carried/thread.comprehension.json"
            """{"fields":{"shared_unattributed_deep_marker":99999999}}"""
        do! verifyPhysical path server original
        let! first = round path server original
        do! verifyBoth server sibling
        let! _ = round path server first
        do! verifyBoth server sibling
        do! assertUnchangedPublications server original
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP and MCP retain generation N canonical payslip before and after actual embed completion`` () =
    withPhysicalServer lifecycle

let private changeSource server (expected: ReembedReadFixtures.Expected) () =
    task {
        do! executeSql server "UPDATE documents SET source_type='email_attachment' WHERE id=@id"
                [ "@id", Database.boxVal expected.DocumentId ]
        return! checkpoint server
    }

let private carriedRace (pause: ConfirmationPause) useMcp server =
    task {
        let! original = seedAt server 7L "paused" canonical
        do! applyReflow server original.DocumentId "reembed"
        let current = { original with Generation = 8L }
        do! verifyBoth server current
        arm pause.Gate
        let selected = transport useMcp
        let pending = comprehension selected server current.DocumentId
        let! completed = interleave pause.Gate pending (changeSource server current)
        assertReply selected 409 true completed.Reply
        Assert.Equal("comprehension_changed_during_read", text "error" completed.Reply.Body)
        Assert.False(completed.Reply.Body.TryGetProperty("comprehension") |> fst)
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(completed.State, after)
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Carried publication cannot escape the final document generation fence during a paused read`` useMcp =
    let pause = confirmationPause ()
    withServerUsing (database pause) None McpAccess.readOnly [] None
        (carriedRace pause useMcp)
