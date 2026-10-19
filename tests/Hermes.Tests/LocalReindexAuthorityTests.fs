module Hermes.Tests.LocalReindexAuthorityTests

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.LocalReindexFixtures

type private Guard =
    { Context: ReindexOperator.Context
      Opened: ConcurrentQueue<string>
      SourceReads: ConcurrentQueue<string> }

let private guardedContext (selected: Fixture) (decoy: Fixture) =
    let original = context selected
    let opened = ConcurrentQueue<string>()
    let reads = ConcurrentQueue<string>()
    let environment = TestHelpers.fakeEnvironment selected.Root selected.Root decoy.Root
    let guarded: Guard =
        { Opened = opened; SourceReads = reads
          Context =
            { original with
                Environment = environment
                Files =
                    { original.Files with
                        readAllBytes = fun path ->
                            reads.Enqueue path
                            original.Files.readAllBytes path }
                OpenArchive = fun mode archive ->
                    opened.Enqueue archive
                    original.OpenArchive mode archive } }
    guarded

let private archiveHashes (fixture: Fixture) =
    Directory.GetFiles(fixture.Archive, "*", SearchOption.AllDirectories)
    |> Array.map (fun path -> path, File.ReadAllBytes(path) |> SourceFiles.hash)
    |> Map.ofArray

let private snapshots fixtures =
    fixtures
    |> List.map (fun fixture -> fixture.Archive, archiveHashes fixture)
    |> Map.ofList

let private assertRejected documentId (result: HandlerResult) =
    Assert.Equal(1, result.ExitCode)
    Assert.Equal("not_attempted", (statuses result.Json).[documentId])
    Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("archiveDirectory").ValueKind)
    Assert.Equal(JsonValueKind.Null, result.Json.GetProperty("schemaVersion").ValueKind)
    let outcome = result.Json.GetProperty("outcomes").[0]
    Assert.False(String.IsNullOrWhiteSpace(outcome.GetProperty("error").GetString()))
    Assert.False(String.IsNullOrWhiteSpace(result.Json.GetProperty("error").GetString()))

let private invoke selected (guard: Guard) path apply explicitConfig =
    let configFlags = if explicitConfig then [ "--config"; path ] else []
    let modeFlags = if apply then [ "--apply" ] else []
    let args =
        [ "reindex-files"; "--document-id"; string selected.Ids.Body ]
        @ configFlags @ modeFlags
        |> List.toArray
    runHandlerWith guard.Context args |> assertRejected selected.Ids.Body

let private exercise (yaml: string) (selected: Fixture) (decoy: Fixture) =
    task {
        let path = Path.Combine(selected.Root, "config.yaml")
        let text = yaml.Replace("$ARCHIVE", JsonSerializer.Serialize selected.Archive, StringComparison.Ordinal)
        do! File.WriteAllTextAsync(path, text)
        let guard = guardedContext selected decoy
        Assert.Equal(decoy.Archive, Config.defaultArchiveDir guard.Context.Environment)
        let before = snapshots [ selected; decoy ]
        [ false; true ]
        |> List.iter (fun apply ->
            [ false; true ] |> List.iter (invoke selected guard path apply))
        Assert.Empty(guard.Opened)
        Assert.Empty(guard.SourceReads)
        Assert.Equal<Map<string, Map<string, string>>>(before, snapshots [ selected; decoy ])
    }

let private withDecoy yaml selected =
    withDefaultFixture (exercise yaml selected)

[<Theory>]
[<InlineData("")>]
[<InlineData("   \n")>]
[<InlineData("null\n")>]
[<InlineData("~\n")>]
[<InlineData("{}\n")>]
[<InlineData("sync_interval_minutes: 7\n")>]
[<InlineData("archive_dr: $ARCHIVE\n")>]
[<InlineData("archive_dir: null\n")>]
[<InlineData("archive_dir: ''\n")>]
[<InlineData("archive_dir: '   '\n")>]
[<Trait("Category", "Integration")>]
let ``Incomplete operator config never opens either real synthetic archive in either mode`` (yaml: string) =
    withFixture (withDecoy yaml)

[<Theory>]
[<InlineData("")>]
[<InlineData("null\n")>]
[<InlineData("{}\n")>]
[<InlineData("sync_interval_minutes: 7\n")>]
[<InlineData("archive_dr: ignored\n")>]
[<Trait("Category", "Unit")>]
let ``Ordinary service parsing retains default archive behavior`` (yaml: string) =
    let environment = TestHelpers.fakeEnvironment "/synthetic-home" "/synthetic-config" "/synthetic-documents"
    let parsed = Config.parseYaml environment yaml |> Result.defaultWith failwith
    Assert.Equal(Config.defaultArchiveDir environment, parsed.ArchiveDir)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Explicit operator authority uses the typed config mapping and synthetic home expansion`` () =
    let home = "/synthetic-home"
    let environment = TestHelpers.fakeEnvironment home "/synthetic-config" "/synthetic-documents"
    let parsed =
        Config.parseOperatorYaml environment "archive_dir: ~/selected\nsync_interval_minutes: 9\n"
        |> Result.defaultWith failwith
    Assert.Equal(Path.Combine(home, "selected"), parsed.ArchiveDir)
    Assert.Equal(9, parsed.SyncIntervalMinutes)
