module Hermes.Tests.LocalReindexHandlerTests

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.LocalReindexFixtures

let private assertIntegrity fixture before hashes =
    task {
        let! after = metadata fixture
        Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
        Assert.Equal<Map<string, string>>(hashes, sourceHashes fixture)
    }

let private assertDefaultStatuses (fixture: Fixture) (json: JsonElement) =
    Assert.Equal("dry_run", json.GetProperty("mode").GetString())
    Assert.Equal(fixture.Archive, json.GetProperty("archiveDirectory").GetString())
    let actual = statuses json
    Assert.Equal("would_index", actual.[fixture.Ids.Body])
    Assert.Equal("would_index", actual.[fixture.Ids.Extracted])
    Assert.Equal("would_clear_content_index", actual.[fixture.Ids.Stale])

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Handler dry run validates files but preserves indexes metadata and all archive content`` () =
    withFixture (fun fixture ->
        task {
            let! before = metadata fixture
            let hashes = sourceHashes fixture
            let result = runHandler fixture [ fixture.Ids.Body; fixture.Ids.Extracted; fixture.Ids.Stale ] []
            Assert.Equal(0, result.ExitCode)
            assertDefaultStatuses fixture result.Json
            let! indexed = contentCount fixture
            Assert.Equal(0L, indexed)
            do! assertIntegrity fixture before hashes
            Assert.False(Directory.Exists(Path.Combine(fixture.Archive, "unclassified")))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Handler apply reports partial failure and continues with subsequent explicit identities`` () =
    withFixture (fun fixture ->
        task {
            let hashes = sourceHashes fixture
            let result =
                runHandler fixture [ fixture.Ids.Body; 999L; fixture.Ids.Extracted; fixture.Ids.Stale ] [ "--apply" ]
            Assert.Equal(1, result.ExitCode)
            let actual = statuses result.Json
            Assert.Equal("indexed", actual.[fixture.Ids.Body])
            Assert.Equal("failed", actual.[999L])
            Assert.Equal("indexed", actual.[fixture.Ids.Extracted])
            Assert.Equal("metadata_only", actual.[fixture.Ids.Stale])
            do! assertIndexed fixture "clibodyemeraldmarker" fixture.Ids.Body
            do! assertIndexed fixture "cliextractedsapphiremarker" fixture.Ids.Extracted
            Assert.Equal<Map<string, string>>(hashes, sourceHashes fixture)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Handler does not read an uncompleted sidecar`` () =
    withFixture (fun fixture ->
        task {
            let original = context fixture
            let files =
                { original.Files with
                    readAllBytes = fun path ->
                        if path.EndsWith("stale.png.extracted.md", StringComparison.Ordinal) then
                            failwith "Uncompleted sidecar was read"
                        else original.Files.readAllBytes path }
            let result =
                runHandlerWith { original with Files = files }
                    (arguments fixture [ fixture.Ids.Stale ] [ "--apply" ])
            Assert.Equal(0, result.ExitCode)
            Assert.Equal("metadata_only", (statuses result.Json).[fixture.Ids.Stale])
        })

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Missing or malformed configuration fails without default archive fallback`` malformed =
    withFixture (fun fixture ->
        task {
            let path = Path.Combine(fixture.Root, "bad-config.yaml")
            if malformed then do! File.WriteAllTextAsync(path, "[invalid YAML")
            let args =
                [| "reindex-files"; "--config"; path; "--document-id"; string fixture.Ids.Body; "--apply" |]
            let result = runHandlerWith (context fixture) args
            Assert.Equal(1, result.ExitCode)
            Assert.Equal("not_attempted", (statuses result.Json).[fixture.Ids.Body])
            Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("archiveDirectory").ValueKind)
            let! indexed = contentCount fixture
            Assert.Equal(0L, indexed)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Missing archive database is not created by apply`` () =
    withFixture (fun fixture ->
        task {
            let empty = Path.Combine(fixture.Root, "empty archive")
            Directory.CreateDirectory(empty) |> ignore
            let config = Path.Combine(fixture.Root, "empty.yaml")
            do! File.WriteAllTextAsync(config, $"archive_dir: {JsonSerializer.Serialize empty}\n")
            let result =
                runHandlerWith (context fixture)
                    [| "reindex-files"; "--config"; config; "--document-id"; "1"; "--apply" |]
            Assert.Equal(1, result.ExitCode)
            Assert.Equal("not_attempted", (statuses result.Json).[1L])
            Assert.False(File.Exists(Path.Combine(empty, "db.sqlite")))
            Assert.Empty(Directory.GetFileSystemEntries(empty))
        })

let private addAttempt fixture =
    withDatabase fixture (fun db ->
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO pipeline_stage_attempts(document_id,stage_name,attempt_token,started_at)
                       VALUES (@id,'extract','0123456789abcdef0123456789abcdef','2000-01-01')"""
                    [ "@id", Database.boxVal fixture.Ids.Body ]
            return ()
        })

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Operator refuses active or stale persisted leases without recovering them`` apply =
    withFixture (fun fixture ->
        task {
            do! addAttempt fixture
            let! before = metadata fixture
            let hashes = sourceHashes fixture
            let result =
                runHandler fixture [ fixture.Ids.Body ] (if apply then [ "--apply" ] else [])
            Assert.Equal(1, result.ExitCode)
            Assert.Equal("failed", (statuses result.Json).[fixture.Ids.Body])
            do! assertIntegrity fixture before hashes
        })
