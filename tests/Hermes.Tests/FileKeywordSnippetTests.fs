module Hermes.Tests.FileKeywordSnippetTests

open System
open System.IO
open System.Text
open System.Text.Json
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures

type private Signature =
    { Id: int64
      Score: float
      Snippet: string }

type private Corpus =
    { First: int64
      Second: int64 }

let private shared = "snippetskewquartzmarker"
let private evidence = "sourceevidencea"

let private signature (hit: Search.SearchResult) : Signature =
    { Id = hit.DocumentId; Score = hit.RelevanceScore
      Snippet = hit.Snippet |> Option.defaultValue "" }

let private responseSignatures (response: JsonElement) : Signature list =
    response.EnumerateArray()
    |> Seq.map (fun hit ->
        { Id = hit.GetProperty("id").GetInt64()
          Score = hit.GetProperty("score").GetDouble()
          Snippet = textProperty "snippet" hit })
    |> Seq.toList

let private insertBody (server: Server) (path: string) bytes subject =
    task {
        let name =
            Path.GetFileName(path)
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> invalidArg (nameof path) "Source path must have a file name")
        let! value =
            server.Db.execScalar
                """INSERT INTO documents(source_type,saved_path,category,sha256,original_name,subject)
                   VALUES ('email_body',@path,'tax',@sha,@name,@subject) RETURNING id"""
                [ "@path", Database.boxVal path; "@sha", Database.boxVal (SourceFiles.hash bytes)
                  "@name", Database.boxVal name
                  "@subject", Database.boxVal subject ]
        return Assert.IsType<int64>(value)
    }

let private seedBody (server: Server) name subject (text: string) =
    task {
        let path = $"/archive/skew/{name}.md"
        let bytes = Encoding.UTF8.GetBytes text
        do! SourceFiles.writeNew server.Db server.Files.Fs path bytes
        return! insertBody server path bytes subject
    }

let private seed server =
    task {
        let padding = List.replicate 1000 "filler" |> String.concat " "
        let subject = List.replicate 32 shared |> String.concat " "
        let! first = seedBody server "a" subject $"{evidence} {shared} {padding}"
        let! second = seedBody server "b" $"{shared} {padding}" $"{shared} brief"
        let! result = rebuild server [ first; second ]
        let report = result |> Result.defaultWith failwith
        Assert.Equal<int64 list>([ first; second ], report.Indexed)
        Assert.Empty(report.MetadataOnly)
        let corpus: Corpus = { First = first; Second = second }
        return corpus
    }

let private proveSkew (ids: Corpus) (metadata: Signature list) (content: Signature list) =
    Assert.Equal<int64 list>([ ids.First; ids.Second ], metadata |> List.map (fun hit -> hit.Id))
    Assert.Equal<int64 list>([ ids.Second; ids.First ], content |> List.map (fun hit -> hit.Id))
    let aMetadata = metadata.Head
    let bMetadata = metadata.[1]
    let bContent = content.Head
    let aContent = content.[1]
    Assert.True(aMetadata.Score < bMetadata.Score)
    Assert.True(bContent.Score < aContent.Score)
    Assert.True(aMetadata.Score < bContent.Score)
    Assert.Contains(evidence, aContent.Snippet)
    Assert.DoesNotContain(evidence, aMetadata.Snippet)
    Assert.NotEqual<string>(aMetadata.Snippet, aContent.Snippet)
    [ { aMetadata with Snippet = aContent.Snippet }
      { bContent with Score = min bContent.Score bMetadata.Score } ]

let private expectations (server: Server) corpus =
    task {
        let sql, parameters = Search.buildQuery { Search.defaultFilter shared with Limit = 200 }
        let! metadata = server.Db.execReader sql parameters
        let! content =
            server.Db.execReader
                """SELECT rowid AS id, bm25(document_content_fts) AS rank,
                          snippet(document_content_fts, 0, '', '', '...', 32) AS snippet
                   FROM document_content_fts WHERE document_content_fts MATCH @query
                   ORDER BY rank, id"""
                [ "@query", Database.boxVal (Search.sanitiseQuery shared) ]
        let read = List.map (Search.mapRow >> signature)
        return proveSkew corpus (read metadata) (read content)
    }

let private assertCore (server: Server) expected count =
    task {
        let filter = { Search.defaultFilter shared with Limit = count }
        let wanted = expected |> List.truncate count
        let! documents = Search.execute server.Db filter
        let! unified = Search.executeUnified server.Db filter
        let! bodies = Search.executeEmailSearch server.Db filter
        let! page = Search.executePage server.Db filter 0
        Assert.Equal<Signature list>(wanted, documents |> List.map signature)
        Assert.Equal<Signature list>(wanted, unified |> List.map signature)
        Assert.Equal<Signature list>(wanted, bodies |> List.map signature)
        Assert.Equal<Signature list>(wanted, page |> Result.defaultWith failwith |> List.map signature)
    }

let private assertHttp server expected count =
    task {
        let wanted = expected |> List.truncate count
        let! documents = get server $"/api/documents?q={shared}&offset=0&limit={count}"
        let! search = get server $"/api/search?q={shared}&limit={count}"
        let! rpc = mcpCall server "hermes_search" (box {| query = shared; limit = count |})
        let results = (payload rpc).GetProperty("results")
        Assert.Equal<Signature list>(wanted, responseSignatures documents)
        Assert.Equal<Signature list>(wanted, responseSignatures search)
        Assert.Equal<Signature list>(wanted, responseSignatures results)
    }

let private assertLimit server expected () count =
    task {
        do! assertCore server expected count
        do! assertHttp server expected count
    }

let private snapshot (server: Server) =
    task {
        let! metadata = metadataSnapshot server
        let! content =
            server.Db.execReader
                "SELECT rowid, text, kind FROM document_content_fts ORDER BY rowid" []
        return metadata |> Map.add "document_content_fts" content
    }

let private assertReadOnly server expected =
    task {
        let! before = snapshot server
        let! hashes = fileHashes server
        do! [ 1; 2; 20; 200; Int32.MaxValue ] |> Prelude.foldTask (assertLimit server expected) ()
        let! offset = get server $"/api/documents?q={shared}&offset=1&limit=1"
        Assert.Equal<Signature list>(List.tail expected, responseSignatures offset)
        let! after = snapshot server
        let! afterHashes = fileHashes server
        Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
        Assert.Equal<Map<string, string>>(hashes, afterHashes)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Rank-skewed source evidence is independent of page size across Core HTTP and MCP`` () =
    withServer None (fun server ->
        task {
            let! corpus = seed server
            let! expected = expectations server corpus
            do! assertReadOnly server expected
        })
