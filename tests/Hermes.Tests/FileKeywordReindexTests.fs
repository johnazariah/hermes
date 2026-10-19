module Hermes.Tests.FileKeywordReindexTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures

let private clearIndex (server: Server) =
    task {
        let! _ = server.Db.execNonQuery "DELETE FROM document_content_fts" []
        return ()
    }

let private assertRebuild
    (expectedIndexed: int64 list) (expectedMetadata: int64 list)
    (result: Result<FileContentReindex.Report, string>) =
    let report = result |> Result.defaultWith failwith
    Assert.Equal<int64 list>(expectedIndexed, report.Indexed)
    Assert.Equal<int64 list>(expectedMetadata, report.MetadataOnly)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Selected-file rebuilding is deterministic and preserves every source and sidecar hash`` () =
    withServer None (fun server ->
        task {
            let! attachmentId = seedExtracted server "rebuildgarnetmarker"
            let! body = ingestBody server "rebuildberylmarker"
            let bodyId = Document.id body
            let bodyPath = EmailChannelTestSupport.required "saved_path" body
            let! metadata = metadataSnapshot server
            let! hashes = fileHashes server
            do! clearIndex server
            do! assertAbsent server "rebuildgarnetmarker"
            let! first = rebuild server [ bodyId; attachmentId ]
            assertRebuild [ attachmentId; bodyId ] [] first
            let! second = rebuild server [ attachmentId; bodyId; attachmentId ]
            assertRebuild [ attachmentId; bodyId ] [] second
            do! assertViews server "rebuildgarnetmarker" attachmentId sourcePath "document"
            do! assertViews server "rebuildberylmarker" bodyId bodyPath "email"
            let! afterMetadata = metadataSnapshot server
            let! afterHashes = fileHashes server
            Assert.Equal<Map<string, Map<string, obj> list>>(metadata, afterMetadata)
            Assert.Equal<Map<string, string>>(hashes, afterHashes)
        })

let private assertUncompletedSidecarNotRead (server: Server) documentId =
    task {
        let fs =
            { server.Files.Fs with
                readAllBytes = fun _ -> failwith "An uncompleted extraction must not be read" }
        let! result =
            FileContentReindex.rebuild server.Db fs TestHelpers.silentLogger "/archive" [ documentId ]
        assertRebuild [] [ documentId ] result
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP reextract removes obsolete content before replay without deleting immutable body history`` () =
    withServer None (fun server ->
        task {
            let! id = seedExtracted server "obsoletefluoritemarker"
            let! body = ingestBody server "historicalhematitemarker"
            let bodyId = Document.id body
            let bodyPath = EmailChannelTestSupport.required "saved_path" body
            let! hashes = fileHashes server
            do! applyReflow server id "reextract"
            do! assertAbsent server "obsoletefluoritemarker"
            do! assertViews server "historicalhematitemarker" bodyId bodyPath "email"
            do! assertUncompletedSidecarNotRead server id
            let! invalidatedHashes = fileHashes server
            Assert.Equal<Map<string, string>>(hashes, invalidatedHashes)
            do! replay server "replacementrutilemarker"
            do! assertAbsent server "obsoletefluoritemarker"
            do! assertViews server "replacementrutilemarker" id sourcePath "document"
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``An uncompleted generated sidecar is neither read nor exposed`` () =
    withServer None (fun server ->
        task {
            let! id = seedSource server sourcePath sourceText
            server.Files.Put (sourcePath + ".extracted.md") "stalespinelmarker"
            do! assertUncompletedSidecarNotRead server id
            do! assertAbsent server "stalespinelmarker"
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Extraction output alone is insufficient without its completion`` () =
    withServer None (fun server ->
        task {
            let! id = seedSource server sourcePath sourceText
            let! outcome = StagesV5.extract (dependencies server "uncompletedtopazmarker") server.Db TestHelpers.silentLogger id
            Assert.Equal<PipelineV5.StageOutcome>(PipelineV5.Completed, outcome)
            do! assertAbsent server "uncompletedtopazmarker"
            do! assertUncompletedSidecarNotRead server id
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Deleting an extraction completion removes its generated index row`` () =
    withServer None (fun server ->
        task {
            let! id = seedExtracted server "deletedledgerjaspermarker"
            let! _ =
                server.Db.execNonQuery
                    "DELETE FROM stage_completions WHERE document_id=@id AND stage_name='extract'"
                    [ "@id", Database.boxVal id ]
            let! count =
                server.Db.execScalar "SELECT COUNT(*) FROM document_content_fts WHERE rowid=@id"
                    [ "@id", Database.boxVal id ]
            Assert.Equal(0L, Assert.IsType<int64>(count))
            do! assertAbsent server "deletedledgerjaspermarker"
        })

[<Theory>]
[<InlineData(0)>]
[<InlineData(101)>]
[<Trait("Category", "Integration")>]
let ``Rebuild rejects empty or oversized selections before reading`` count =
    withServer None (fun server ->
        task {
            let fs = { server.Files.Fs with readAllBytes = fun _ -> failwith "Unexpected file read" }
            let! result =
                FileContentReindex.rebuild server.Db fs TestHelpers.silentLogger "/archive"
                    (List.init count (fun index -> int64 (index + 1)))
            Assert.True(Result.isError result)
        })

[<Theory>]
[<InlineData(0L)>]
[<InlineData(-1L)>]
[<InlineData(999L)>]
[<Trait("Category", "Integration")>]
let ``Invalid or missing selected identities produce errors`` documentId =
    withServer None (fun server ->
        task {
            let! result = rebuild server [ documentId ]
            Assert.True(Result.isError result)
        })
