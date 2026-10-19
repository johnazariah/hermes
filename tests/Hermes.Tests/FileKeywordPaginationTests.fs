module Hermes.Tests.FileKeywordPaginationTests

open System
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures

type private Signature =
    { Id: int64
      Score: float
      Snippet: string }

type private Page =
    { Offset: int
      Count: int }

let private shared = "paginationquartzmarker"

let private signature (hit: Search.SearchResult) : Signature =
    { Id = hit.DocumentId; Score = hit.RelevanceScore
      Snippet = hit.Snippet |> Option.defaultValue "" }

let private responseSignatures (response: JsonElement) =
    response.EnumerateArray()
    |> Seq.map (fun hit ->
        let item: Signature =
            { Id = hit.GetProperty("id").GetInt64()
              Score = hit.GetProperty("score").GetDouble()
              Snippet = textProperty "snippet" hit }
        item)
    |> Seq.toList

let private assertPage server expected (page: Page) =
    task {
        let path = $"/api/documents?q={shared}&category=tax&offset={page.Offset}&limit={page.Count}"
        let wanted = expected |> List.skip (min page.Offset expected.Length) |> List.truncate page.Count
        let! first = get server path
        let! repeated = get server path
        Assert.Equal<Signature list>(wanted, responseSignatures first)
        Assert.Equal<Signature list>(wanted, responseSignatures repeated)
    }

let private assertPages server expected =
    [ { Offset = 0; Count = 50 }; { Offset = 50; Count = 50 }
      { Offset = 100; Count = 50 }; { Offset = 150; Count = 50 }
      { Offset = 75; Count = 50 }; { Offset = 0; Count = 200 }
      { Offset = 101; Count = 79 }; { Offset = 180; Count = 50 }
      { Offset = Int32.MaxValue; Count = 200 }
      { Offset = 0; Count = Int32.MaxValue } ]
    |> Prelude.foldTask (fun () page -> assertPage server expected page) ()

let private assertCorpus (entries: SyntheticKeywordCorpus.Entry list) (expected: Signature list) =
    Assert.Equal(180, expected.Length)
    Assert.Equal(180, expected |> List.map (fun hit -> hit.Id) |> List.distinct |> List.length)
    Assert.Equal<Set<int64>>(
        entries |> List.map (fun entry -> entry.DocumentId) |> Set.ofList,
        expected |> List.map (fun hit -> hit.Id) |> Set.ofList)
    Assert.Equal<Signature list>(expected |> List.sortBy (fun hit -> hit.Score, hit.Id), expected)

let private checkOtherQueries server expected =
    task {
        let! defaultPage = get server $"/api/documents?q={shared}"
        Assert.Equal<Signature list>(expected |> List.truncate 50, responseSignatures defaultPage)
        let! differentCategory = get server $"/api/documents?q={shared}&category=invoices"
        Assert.Equal(0, differentCategory.GetArrayLength())
        let! punctuation = get server "/api/documents?q=%2B%2B"
        Assert.Equal(0, punctuation.GetArrayLength())
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Real HTTP pagination ranks and deduplicates all 180 overlapping matches before slicing`` () =
    withServer None (fun server ->
        task {
            let! entries = SyntheticKeywordCorpus.seed server.Db server.Files 180 shared
            do! SyntheticKeywordCorpus.rebuild server.Db server.Files entries
            let! ranked = Search.execute server.Db { Search.defaultFilter shared with Limit = 200 }
            let expected = ranked |> List.map signature
            assertCorpus entries expected
            do! assertPages server expected
            do! checkOtherQueries server expected
        })

[<Theory>]
[<InlineData("offset=-1")>]
[<InlineData("offset=invalid")>]
[<InlineData("offset=2147483648")>]
[<InlineData("limit=0")>]
[<InlineData("limit=-1")>]
[<InlineData("limit=invalid")>]
[<InlineData("limit=2147483648")>]
[<Trait("Category", "Integration")>]
let ``Invalid keyword pagination is HTTP400 rather than clamped or defaulted`` (parameters: string) =
    withServer None (fun server ->
        task {
            use! response = server.Client.GetAsync($"/api/documents?q={shared}&{parameters}")
            Assert.Equal(400, int response.StatusCode)
            let! text = response.Content.ReadAsStringAsync()
            Assert.False(String.IsNullOrWhiteSpace(textProperty "error" (json text)))
        })

[<Theory>]
[<InlineData(-1, 20)>]
[<InlineData(0, 0)>]
[<InlineData(0, -1)>]
[<Trait("Category", "Unit")>]
let ``Core paging rejects invalid requests without rewriting their values`` offset count =
    task {
        let db = TestHelpers.createDb ()
        try
            let! result = Search.executePage db { Search.defaultFilter shared with Limit = count } offset
            Assert.True(Result.isError result)
        finally db.dispose ()
    }
