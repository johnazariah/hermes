module Hermes.Tests.LegacyPipelineRegressionTests

open System
open System.IO
open System.Text
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.CoreRegressionSupport

let private localScan = "regression local scan reached"

let private prepareHydration (fixture: Fixture) (initialStage: string) : Task<unit> =
    task {
        do! seed fixture 1L initialStage
        fixture.Memory.Put (sourcePath 1L + ".extracted.md") "Previously extracted invoice text"
        let! _ =
            fixture.Db.execNonQuery
                "UPDATE documents SET extracted_at = @at WHERE id = 1"
                [ "@at", Database.boxVal stamp ]
        return ()
    }

[<Theory>]
[<InlineData("received", "triage")>]
[<InlineData("extracted", "triage")>]
[<InlineData("triaged", "understand")>]
[<InlineData("understood", "embed")>]
[<Trait("Category", "Integration")>]
let ``Legacy restart routes persisted work to the correct next failing capability`` initialStage nextStage =
    task {
        use fixture = fixture ()
        do! prepareHydration fixture initialStage
        let observation = probe $"Stage '{nextStage}' failed doc 1:"
        let configuration =
            { config () with Pipeline = { (config ()).Pipeline with ExtractConcurrency = 0; LlmConcurrency = 0 } }
        do! runLegacy fixture fixture.Memory.Fs observation configuration
        let! failed = row fixture.Db 1L
        Assert.Equal("failed", Document.stage failed)
        Assert.Equal<string option>(Some stamp, Document.decode<string> "extracted_at" failed)
        Assert.True(saw observation $"Stage '{nextStage}' failed doc 1:")
        Assert.False(saw observation "Stage 'extract' processed")
        Assert.False(saw observation "loop error:")
        assertOriginal fixture failed
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy ingest rereads authoritative fields and does not enqueue a missing row`` () =
    task {
        use fixture = fixture ()
        do! seed fixture 1L "received"
        let observation = probe "not found after INSERT"
        let channels = Pipeline.createChannels ()
        let stale : Document.T =
            Map.ofList [ "id", 1L :> obj; "category", "stale-producer-value" :> obj ]
        let absent : Document.T = Map.ofList [ "id", 99L :> obj ]
        let! ingested = Pipeline.ingest fixture.Db observation.Logger channels.Extract.Writer stale
        let! missing = Pipeline.ingest fixture.Db observation.Logger channels.Extract.Writer absent
        let! authoritative = channels.Extract.Reader.ReadAsync().AsTask().WaitAsync(timeout)
        Assert.Equal(1L, ingested)
        Assert.Equal(99L, missing)
        assertOriginal fixture authoritative
        Assert.Equal(0, channels.Extract.Reader.Count)
        Assert.Equal(0, channels.Triage.Reader.Count + channels.Understand.Reader.Count + channels.Embed.Reader.Count)
        Assert.True(saw observation "Ingest: doc 99 not found after INSERT")
    }

type private IntakeScenario =
    { Sources: string list
      RetainedMetadata: string list }

let private prepareIntake (fixture: Fixture) : IntakeScenario =
    let directories =
        [ "unclassified"; "local"; Path.Combine("local", "thread")
          Path.Combine("local", "thread", "deeper") ]
    directories |> List.iter (fun path -> fixture.Memory.Fs.createDirectory(Path.Combine(archiveDir, path)))
    let sources =
        [ Path.Combine(archiveDir, "unclassified", "direct.bin")
          Path.Combine(archiveDir, "local", "thread", "nested.bin") ]
    sources |> List.iter (fun path -> fixture.Memory.Put path $"source:{Path.GetFileName path}")
    let suffixes = [ ".meta.json"; ".HERMES.JSON"; ".EXTRACTED.MD"; ".COMPREHENSION.JSON" ]
    let sidecars = sources |> List.collect (fun path -> suffixes |> List.map (fun suffix -> path + suffix))
    let grandchild = Path.Combine(archiveDir, "local", "thread", "deeper", "ignored.bin")
    grandchild :: sidecars |> List.iter (fun path -> fixture.Memory.Put path "metadata sentinel")
    { Sources = sources
      RetainedMetadata = grandchild :: (sidecars |> List.filter (fun path -> not (path.EndsWith(".meta.json")))) }

let private assertIntakeRow (fixture: Fixture) (document: Document.T) =
    let path = Document.decode<string> "source_path" document |> Option.defaultWith (fun () -> failwith "Missing source_path")
    let content = fixture.Memory.Get path |> Option.defaultWith (fun () -> failwith $"Source removed: {path}")
    let expectedHash = content |> Encoding.UTF8.GetBytes |> SourceFiles.hash
    let relative = Path.GetRelativePath(archiveDir, path) |> fixture.Memory.Norm
    let actual = Document.decode<string> "saved_path" document |> Option.map fixture.Memory.Norm
    Assert.Equal<string option>(Some relative, actual)
    Assert.Equal<string option>(Some expectedHash, Document.decode<string> "sha256" document)
    Assert.Equal<string option>(Some "watched_folder", Document.decode<string> "source_type" document)
    Assert.Equal<string option>(Some stamp, Document.decode<string> "ingested_at" document)
    Assert.Equal<int64 option>(Some (int64 (Encoding.UTF8.GetByteCount content)), Document.decode<int64> "size_bytes" document)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy intake scans direct and immediate child sources but leaves derived and deeper files untouched`` () =
    task {
        use fixture = fixture ()
        let scenario = prepareIntake fixture
        let observation = probe localScan
        do! runLegacy fixture (observeLocalScan fixture observation) observation (config ())
        let! ingested = documents fixture
        Assert.Equal(2, ingested.Length)
        ingested |> List.iter (assertIntakeRow fixture)
        let names = ingested |> List.choose (Document.decode<string> "original_name") |> Set.ofList
        Assert.Equal<Set<string>>(set [ "direct.bin"; "nested.bin" ], names)
        scenario.Sources |> List.iter (fun path -> Assert.True(fixture.Memory.Fs.fileExists path))
        scenario.Sources |> List.iter (fun path -> Assert.False(fixture.Memory.Fs.fileExists(path + ".meta.json")))
        scenario.RetainedMetadata
        |> List.iter (fun path -> Assert.Equal<string option>(Some "metadata sentinel", fixture.Memory.Get path))
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Legacy duplicate cleanup removes only unowned copies and never the archived owner`` scanOwnedPath =
    task {
        use fixture = fixture ()
        do! seed fixture 1L "embedded"
        let incoming =
            if scanOwnedPath then sourcePath 1L
            else Path.Combine(archiveDir, "local", "duplicate.txt")
        fixture.Memory.Put incoming (payload 1L)
        fixture.Memory.Put (incoming + ".meta.json") "duplicate metadata"
        let observation = probe localScan
        do! runLegacy fixture (observeLocalScan fixture observation) observation (config ())
        let! owner = row fixture.Db 1L
        let! total = count fixture.Db "documents"
        Assert.Equal(1L, total)
        Assert.Equal("embedded", Document.stage owner)
        assertOriginal fixture owner
        Assert.Equal(scanOwnedPath, fixture.Memory.Fs.fileExists incoming)
        Assert.Equal(scanOwnedPath, fixture.Memory.Fs.fileExists(incoming + ".meta.json"))
    }

let private failingEnumeration (fixture: Fixture) (observation: Probe) (broken: string) : Algebra.FileSystem =
    let scanned = observeLocalScan fixture observation
    { scanned with
        getDirectories = fun path ->
            if fixture.Memory.Norm path = fixture.Memory.Norm (Path.Combine(archiveDir, "unclassified")) then
                observation.Logger.debug "regression child enumeration failed"
                raise (IOException "child enumeration unavailable")
            scanned.getDirectories path
        readAllBytes = fun path ->
            if fixture.Memory.Norm path = fixture.Memory.Norm broken then
                Task.FromException<byte array>(IOException "source read unavailable")
            else scanned.readAllBytes path }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy intake tolerates child enumeration and individual read failures without losing direct siblings`` () =
    task {
        use fixture = fixture ()
        let directory = Path.Combine(archiveDir, "unclassified")
        let broken = Path.Combine(directory, "broken.bin")
        let good = Path.Combine(directory, "good.bin")
        fixture.Memory.Fs.createDirectory directory
        fixture.Memory.Put broken "unreadable sentinel"
        fixture.Memory.Put good "good source"
        let observation = probe localScan
        do! runLegacy fixture (failingEnumeration fixture observation broken) observation (config ())
        let! ingested = documents fixture
        Assert.Equal<string option>(Some "good.bin", Document.decode<string> "original_name" (Assert.Single<Document.T> ingested))
        Assert.True(saw observation "regression child enumeration failed")
        Assert.True(saw observation "Folder intake failed for broken.bin: source read unavailable")
        Assert.Equal<string option>(Some "unreadable sentinel", fixture.Memory.Get broken)
        Assert.Equal<string option>(Some "good source", fixture.Memory.Get good)
    }
