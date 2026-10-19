module Hermes.Tests.SyntheticKeywordPerformanceTests

open System
open System.Diagnostics
open System.Threading.Tasks
open Xunit
open Xunit.Abstractions
open Hermes.Core

let private percentile fraction samples =
    let ordered = samples |> List.sort
    let index = int (Math.Ceiling(float ordered.Length * fraction)) - 1
    ordered |> List.item index

let private claims (db: Algebra.Database) =
    db.execReader "SELECT * FROM archive_source_claims ORDER BY ownership_key" []

let private measureRebuild db files entries =
    task {
        let timer = Stopwatch.StartNew()
        do! SyntheticKeywordCorpus.rebuild db files entries
        timer.Stop()
        return timer.Elapsed.TotalMilliseconds
    }

let private querySample (db: Algebra.Database) (entry: SyntheticKeywordCorpus.Entry) =
    task {
        let timer = Stopwatch.StartNew()
        let! hits = Search.executeUnified db (Search.defaultFilter entry.Marker)
        timer.Stop()
        let hit = hits |> List.exactlyOne
        Assert.Equal(entry.DocumentId, hit.DocumentId)
        Assert.Equal(entry.SavedPath, hit.SavedPath)
        Assert.Contains(entry.Marker, hit.Snippet |> Option.defaultValue "")
        return timer.Elapsed.TotalMilliseconds
    }

let private includeSample db collected entry =
    task {
        let! milliseconds = querySample db entry
        return milliseconds :: collected
    }

let private measureQueries db (entries: SyntheticKeywordCorpus.Entry list) =
    task {
        let! _ = querySample db entries.Head
        let selected = [ 1 .. 40 ] |> List.map (fun index -> entries.[(index * 23) % entries.Length])
        return! selected |> Prelude.foldTask (includeSample db) []
    }

let private assertIndexed (db: Algebra.Database) (entries: SyntheticKeywordCorpus.Entry list) =
    task {
        Assert.Equal(1000, entries |> List.map (fun entry -> entry.DocumentId) |> List.distinct |> List.length)
        let! count = db.execScalar "SELECT COUNT(*) FROM document_content_fts WHERE kind='source'" []
        Assert.Equal(1000L, Assert.IsType<int64>(count))
    }

let private measure (output: ITestOutputHelper) db files entries =
    task {
        let! beforeHashes = SyntheticKeywordCorpus.hashes files entries
        let! beforeClaims = claims db
        let! indexing = measureRebuild db files entries
        do! assertIndexed db entries
        let! samples = measureQueries db entries
        let! afterHashes = SyntheticKeywordCorpus.hashes files entries
        let! afterClaims = claims db
        Assert.Equal<Map<string, string>>(beforeHashes, afterHashes)
        Assert.Equal<Map<string, obj> list>(beforeClaims, afterClaims)
        let summary: string =
            $"SYNTHETIC scope=Core/in-memory-SQLite+MemFs sources=1000 batch_max=100 queries=40 warmup=1 indexing_ms={indexing:F3} query_p50_ms={percentile 0.50 samples:F3} query_p95_ms={percentile 0.95 samples:F3}; no production threshold"
        output.WriteLine summary
    }

let private withCorpus output =
    task {
        let db = TestHelpers.createDb ()
        let files = TestHelpers.memFs ()
        try
            let! entries = SyntheticKeywordCorpus.seed db files 1000 "syntheticperformancemarker"
            do! measure output db files entries
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Reported synthetic percentiles use nearest ranks`` () =
    let samples = [ 1.0 .. 20.0 ] |> List.rev
    Assert.Equal(10.0, percentile 0.50 samples)
    Assert.Equal(19.0, percentile 0.95 samples)

type SyntheticKeywordPerformance(output: ITestOutputHelper) =
    [<Fact>]
    [<Trait("Category", "Integration")>]
    member _.``1000 distinct immutable synthetic sources report bounded rebuild and Core query timings`` () =
        withCorpus output
