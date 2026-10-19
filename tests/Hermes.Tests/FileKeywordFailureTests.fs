module Hermes.Tests.FileKeywordFailureTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures

let private sidecarPath = sourcePath + ".extracted.md"

let private mutateFiles (server: Server) fault =
    task {
        match fault with
        | "missing-source" -> server.Files.Fs.deleteFile sourcePath
        | "missing-sidecar" -> server.Files.Fs.deleteFile sidecarPath
        | "changed-source" ->
            do! server.Files.Fs.writeAllBytes sourcePath (Encoding.UTF8.GetBytes "different source bytes")
        | "malformed-sidecar" -> do! server.Files.Fs.writeAllBytes sidecarPath [| 255uy |]
        | "binary-sidecar" -> do! server.Files.Fs.writeAllBytes sidecarPath [| 65uy; 0uy; 66uy |]
        | _ -> ()
    }

let private faultFileSystem (server: Server) (fault: string) =
    let fs = server.Files.Fs
    let target = if fault.EndsWith("source", StringComparison.Ordinal) then sourcePath else sidecarPath
    let isTarget path = server.Files.Norm path = server.Files.Norm target
    { fs with
        readAllBytes = fun path ->
            if isTarget path && fault.StartsWith("unreadable", StringComparison.Ordinal) then
                Task.FromException<byte array>(UnauthorizedAccessException("synthetic denied read"))
            else fs.readAllBytes path
        getFileSize = fun path ->
            if isTarget path && fault.StartsWith("oversized", StringComparison.Ordinal) then
                ArchiveContent.MaxSourceBytes + ArchiveContent.MaxTextBytes + 1L
            else fs.getFileSize path }

[<Theory>]
[<InlineData("missing-source")>]
[<InlineData("missing-sidecar")>]
[<InlineData("changed-source")>]
[<InlineData("malformed-sidecar")>]
[<InlineData("binary-sidecar")>]
[<InlineData("unreadable-source")>]
[<InlineData("unreadable-sidecar")>]
[<InlineData("oversized-source")>]
[<InlineData("oversized-sidecar")>]
[<Trait("Category", "Integration")>]
let ``Real file failures are reported and never become empty successful rebuilds`` fault =
    withServer None (fun server ->
        task {
            let! id = seedSource server sourcePath sourceText
            do! completeSyntheticExtraction server id
            server.Files.Put sidecarPath "failuretourmalinemarker"
            do! mutateFiles server fault
            let logs = ConcurrentQueue<string>()
            let logger = { TestHelpers.silentLogger with error = logs.Enqueue }
            let! result =
                FileContentReindex.rebuild server.Db (faultFileSystem server fault) logger "/archive" [ id ]
            Assert.True(Result.isError result)
            Assert.NotEmpty(logs)
            Assert.Contains($"document {id}", logs |> Seq.exactlyOne)
            do! assertAbsent server "failuretourmalinemarker"
        })

[<Theory>]
[<InlineData("/outside/source.png")>]
[<InlineData("/archive-neighbour/source.png")>]
[<InlineData("../outside/source.png")>]
[<Trait("Category", "Integration")>]
let ``Legacy cross-root metadata stays searchable but its files are never read`` path =
    withServer None (fun server ->
        task {
            let! _ = server.Db.execNonQuery "DROP TRIGGER doc_saved_path_insert" []
            let! value =
                server.Db.execScalar
                    """INSERT INTO documents(source_type,saved_path,category,sha256,subject)
                       VALUES ('manual_drop',@path,'invoices','synthetic','externalmetadatamarker')
                       RETURNING id"""
                    [ "@path", Database.boxVal path ]
            let id = Assert.IsType<int64>(value)
            do! completeSyntheticExtraction server id
            let reads = ConcurrentQueue<string>()
            let fs = { server.Files.Fs with readAllBytes = fun name -> reads.Enqueue name; failwith "External read" }
            let! result = FileContentReindex.rebuild server.Db fs TestHelpers.silentLogger "/archive" [ id ]
            Assert.True(Result.isError result)
            Assert.Empty(reads)
            do! assertViews server "externalmetadatamarker" id path "document"
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``A supplied root cannot override the database archive authority`` () =
    withServer None (fun server ->
        task {
            let! body = ingestBody server "wrongrootmalachitemarker"
            let reads = ConcurrentQueue<string>()
            let fs = { server.Files.Fs with readAllBytes = fun path -> reads.Enqueue path; failwith "Wrong-root read" }
            let! result =
                FileContentReindex.rebuild server.Db fs TestHelpers.silentLogger "/another-archive" [ Document.id body ]
            Assert.True(Result.isError result)
            Assert.Empty(reads)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``A source-claimed extraction path is rejected before its content is read`` () =
    withServer None (fun server ->
        task {
            let! id = seedSource server sourcePath sourceText
            do! completeSyntheticExtraction server id
            do! SourceFiles.writeNew server.Db server.Files.Fs sidecarPath
                    (Encoding.UTF8.GetBytes "claimedsidecarazuritemarker")
            let! result = rebuild server [ id ]
            Assert.True(Result.isError result)
            do! assertAbsent server "claimedsidecarazuritemarker"
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``An index write failure rolls back email acknowledgement and document registration`` () =
    withServer None (fun server ->
        task {
            let! _ = server.Db.execNonQuery "DROP TABLE document_content_fts" []
            let fixture: EmailChannelFixtures.Fixture =
                { Database = server.Db; Files = server.Files; Logs = ConcurrentQueue<string>()
                  Logger = TestHelpers.silentLogger; Clock = TestHelpers.defaultClock
                  Config = TestHelpers.testConfig "/archive"; Account = "keyword-account" }
            let incoming = EmailChannelFixtures.message "failed-keyword-message" "failed-thread" (Some "failure marker")
            let! result = EmailChannelFixtures.run fixture (EmailChannelFixtures.single incoming []) 1
            Assert.NotEmpty(result.Summary.Errors)
            Assert.Empty(result.Documents)
            let! messages = server.Db.execScalar "SELECT COUNT(*) FROM messages" []
            let! documents = server.Db.execScalar "SELECT COUNT(*) FROM documents" []
            Assert.Equal(0L, Assert.IsType<int64>(messages))
            Assert.Equal(0L, Assert.IsType<int64>(documents))
        })

[<Theory>]
[<InlineData("")>]
[<InlineData("plain text")>]
[<InlineData("\uFEFFplain text")>]
[<Trait("Category", "Unit")>]
let ``Strict archive decoding accepts empty plain and BOM-prefixed UTF8`` (text: string) =
    Assert.Equal(text.TrimStart([| '\uFEFF' |]), ArchiveContent.decode (Encoding.UTF8.GetBytes text))

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Strict archive decoding rejects malformed UTF8 and NUL`` () =
    Assert.Throws<DecoderFallbackException>(fun () -> ArchiveContent.decode [| 255uy |] |> ignore) |> ignore
    Assert.Throws<InvalidOperationException>(fun () -> ArchiveContent.decode [| 0uy |] |> ignore) |> ignore
