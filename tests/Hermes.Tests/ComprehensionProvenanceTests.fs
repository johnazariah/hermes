module Hermes.Tests.ComprehensionProvenanceTests

open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures
open Hermes.Tests.ReembedReadFixtures

let private unavailable server documentId reason =
    task {
        let! before = checkpoint server
        let! http = response server $"/api/documents/{documentId}/comprehension" 409
        let! mcp = mcpCall server "hermes_get_comprehension" (box {| document_id = documentId |})
        Assert.Equal(reason, text "error" http)
        Assert.Equal(reason, text "error" (payload mcp))
        Assert.True(mcp.GetProperty("result").GetProperty("isError").GetBoolean())
        Assert.False(http.TryGetProperty("comprehension") |> fst)
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(before, after)
    }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Missing or corrupt origin cannot fall back to valid older canonical JSON`` missing =
    withServer None (fun server ->
        task {
            let publication = if missing then canonical else "{broken"
            let! expected = seedAt server 7L "invalid-origin" publication
            if missing then
                do! executeSql server
                        "DELETE FROM stage_publications WHERE document_id=@id AND generation=7"
                        [ "@id", Database.boxVal expected.DocumentId ]
            do! applyReflow server expected.DocumentId "reembed"
            let reason =
                if missing then "current_canonical_publication_missing"
                else "invalid_canonical_publication"
            do! unavailable server expected.DocumentId reason
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``An unaudited historical generation gap is not guessed or propagated by reembed`` () =
    withServer None (fun server ->
        task {
            let! expected = seedAt server 7L "gap" canonical
            do! executeSql server
                    "UPDATE document_generations SET generation=8 WHERE document_id=@id"
                    [ "@id", Database.boxVal expected.DocumentId ]
            do! unavailable server expected.DocumentId "current_canonical_publication_missing"
            do! applyReflow server expected.DocumentId "reembed"
            do! unavailable server expected.DocumentId "current_canonical_publication_missing"
            let! rows = server.Db.execReader "SELECT * FROM comprehension_provenance" []
            Assert.Empty(rows)
        })

[<Theory>]
[<InlineData("DELETE FROM extraction WHERE document_id=@id")>]
[<InlineData("DELETE FROM comprehension WHERE document_id=@id")>]
[<InlineData("DELETE FROM stage_completions WHERE document_id=@id AND stage_name='extract'")>]
[<InlineData("DELETE FROM stage_completions WHERE document_id=@id AND stage_name='deep-comprehend'")>]
[<Trait("Category", "Integration")>]
let ``Carry requires every current extraction and comprehension output and completion`` sql =
    withServer None (fun server ->
        task {
            let! expected = seedAt server 7L "incomplete" canonical
            do! executeSql server sql [ "@id", Database.boxVal expected.DocumentId ]
            do! applyReflow server expected.DocumentId "reembed"
            do! unavailable server expected.DocumentId "comprehension_outputs_not_current"
            let! rows = server.Db.execReader "SELECT * FROM comprehension_provenance" []
            Assert.Empty(rows)
        })

[<Theory>]
[<InlineData("reextract")>]
[<InlineData("recomprehend")>]
[<Trait("Category", "Integration")>]
let ``An invalidating reflow cannot reuse a previous carried publication`` operation =
    withServer None (fun server ->
        task {
            let! expected = seedAt server 7L "invalidated" canonical
            do! applyReflow server expected.DocumentId "reembed"
            do! verifyBoth server { expected with Generation = 8L }
            do! applyReflow server expected.DocumentId operation
            do! unavailable server expected.DocumentId "comprehension_outputs_not_current"
        })

let private publishReplacement server documentId publication =
    task {
        do! executeSql server
                """INSERT INTO comprehension(document_id,document_type,category,confidence)
                   VALUES (@id,'payslip','payslips',0.99);
                   INSERT INTO stage_completions(document_id,stage_name)
                   VALUES (@id,'triage'),(@id,'deep-comprehend');"""
                [ "@id", Database.boxVal documentId ]
        do! persistCanonical server documentId 9L publication
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``A newly completed comprehension has its own origin rather than the previous carry`` () =
    withServer None (fun server ->
        task {
            let! expected = seedAt server 7L "replacement" canonical
            do! applyReflow server expected.DocumentId "reembed"
            do! applyReflow server expected.DocumentId "recomprehend"
            let replacement = canonical.Replace("1234.56", "5678.90")
            do! publishReplacement server expected.DocumentId replacement
            do! verifyBoth server
                    { expected with Generation = 9L; PublicationGeneration = 9L; Canonical = replacement }
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Carried attribution does not bypass source byte hash validation`` () =
    withServer None (fun server ->
        task {
            let! expected = seedAt server 7L "changed-bytes" canonical
            do! applyReflow server expected.DocumentId "reembed"
            server.Files.Put expected.SavedPath "externally changed synthetic source"
            server.Files.Bytes.TryRemove(server.Files.Norm expected.SavedPath) |> ignore
            do! unavailable server expected.DocumentId "source_or_publication_unavailable"
        })
