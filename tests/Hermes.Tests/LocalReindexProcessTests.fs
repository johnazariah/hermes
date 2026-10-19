module Hermes.Tests.LocalReindexProcessTests

open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.LocalReindexFixtures

let private assertNoImplicitSelection fixture =
    withDatabase fixture (fun db ->
        task {
            let! hits = Search.executeUnified db (Search.defaultFilter "cliunselecteddiamondmarker")
            Assert.Empty(hits)
            let! stale = Search.executeUnified db (Search.defaultFilter "clistaleonyxmarker")
            Assert.Empty(stale)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual service executable defaults to read-only dry run and exits before host startup`` () =
    withFixture (fun fixture ->
        task {
            let! before = metadata fixture
            let hashes = sourceHashes fixture
            let! child =
                LocalReindexProcess.run fixture
                    (arguments fixture [ fixture.Ids.Body; fixture.Ids.Extracted ] [])
            Assert.Equal(0, child.ExitCode)
            let json = HttpRegressionFixture.json child.StandardOutput
            Assert.Equal("dry_run", json.GetProperty("mode").GetString())
            Assert.Equal("would_index", (statuses json).[fixture.Ids.Body])
            Assert.Equal("would_index", (statuses json).[fixture.Ids.Extracted])
            let! after = metadata fixture
            Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
            Assert.Equal<Map<string, string>>(hashes, sourceHashes fixture)
            Assert.False(Directory.Exists(Path.Combine(fixture.Archive, "unclassified")))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual service executable applies only explicit IDs and preserves identities and files`` () =
    withFixture (fun fixture ->
        task {
            let! before = metadata fixture
            let hashes = sourceHashes fixture
            let! child =
                LocalReindexProcess.run fixture
                    (arguments fixture [ fixture.Ids.Body; fixture.Ids.Extracted; fixture.Ids.Stale ] [ "--apply" ])
            Assert.Equal(0, child.ExitCode)
            let json = HttpRegressionFixture.json child.StandardOutput
            Assert.Equal("apply", json.GetProperty("mode").GetString())
            Assert.Equal("indexed", (statuses json).[fixture.Ids.Body])
            Assert.Equal("metadata_only", (statuses json).[fixture.Ids.Stale])
            do! assertIndexed fixture "clibodyemeraldmarker" fixture.Ids.Body
            do! assertIndexed fixture "cliextractedsapphiremarker" fixture.Ids.Extracted
            do! assertNoImplicitSelection fixture
            let! after = metadata fixture
            Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
            Assert.Equal<Map<string, string>>(hashes, sourceHashes fixture)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual service executable returns nonzero and per-document errors for missing IDs`` () =
    withFixture (fun fixture ->
        task {
            let! child =
                LocalReindexProcess.run fixture
                    (arguments fixture [ 999L; fixture.Ids.Body ] [ "--apply" ])
            Assert.Equal(1, child.ExitCode)
            let json = HttpRegressionFixture.json child.StandardOutput
            Assert.Equal("failed", (statuses json).[999L])
            Assert.Equal("indexed", (statuses json).[fixture.Ids.Body])
            Assert.Equal(1, json.GetProperty("exitCode").GetInt32())
            let failed = json.GetProperty("outcomes").[0]
            Assert.Equal(JsonValueKind.String, failed.GetProperty("error").ValueKind)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual service executable rejects unbounded invocation without creating service directories`` () =
    withFixture (fun fixture ->
        task {
            let! child =
                LocalReindexProcess.run fixture
                    [| "reindex-files"; "--config"; fixture.ConfigPath; "--all"; "--apply" |]
            Assert.Equal(2, child.ExitCode)
            let json = HttpRegressionFixture.json child.StandardOutput
            Assert.Equal(0, json.GetProperty("outcomes").GetArrayLength())
            let! count = contentCount fixture
            Assert.Equal(0L, count)
            Assert.False(Directory.Exists(Path.Combine(fixture.Archive, "unclassified")))
        })
