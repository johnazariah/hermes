module Hermes.Tests.FolderWatcherScanContractTests

open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core

let private archiveDir = "/archive"

let private withFixture
    (verify: TestHelpers.MemFs -> Algebra.Database -> Task<unit>) : Task<unit> =
    task {
        let memory = TestHelpers.memFs ()
        let database = TestHelpers.createDb ()
        try
            do! verify memory database
        finally
            database.dispose ()
    }

let private folder (path: string) (pattern: string) : Domain.WatchFolderConfig =
    { Path = path; Patterns = [ pattern ] }

let private scanFolder (fs: Algebra.FileSystem) (db: Algebra.Database)
    (logger: Algebra.Logger) (watchFolder: Domain.WatchFolderConfig) =
    FolderWatcher.scanFolder fs db logger TestHelpers.defaultClock archiveDir watchFolder

type private ScanScenario =
    { Config: Domain.HermesConfig
      CopiedPaths: string list
      SkippedPaths: string list }

let private scanScenario (memory: TestHelpers.MemFs) : ScanScenario =
    let folders =
        [ folder "/watch/inbox" "*.pdf"
          folder "/watch/reports" "*.txt" ]
    let copied = [ "/watch/inbox/invoice.pdf"; "/watch/reports/summary.txt" ]
    let skipped = [ "/watch/inbox/notes.txt"; "/watch/reports/image.png" ]
    folders |> List.iter (fun watchFolder -> memory.Fs.createDirectory watchFolder.Path)
    copied @ skipped |> List.iter (fun path -> memory.Put path path)
    { Config = { TestHelpers.testConfig archiveDir with WatchFolders = folders }
      CopiedPaths = copied
      SkippedPaths = skipped }

let private assertCopied (memory: TestHelpers.MemFs) (source: string)
    (result: FolderWatcher.IntakeResult) : unit =
    match result with
    | FolderWatcher.Copied savedPath ->
        Assert.NotEqual<string>(memory.Norm source, memory.Norm savedPath)
        Assert.True(memory.Fs.fileExists savedPath)
        Assert.Equal<string option>(Some source, memory.Get savedPath)
        Assert.Equal<string option>(Some source, memory.Get source)
    | other -> failwith $"Expected Copied, got {other}"

[<Fact>]
[<Trait("Category", "Integration")>]
let ``FolderWatcher_IsDuplicate_NullScalar_ReturnsFalse`` () =
    withFixture (fun _ database ->
        task {
            let nullScalar =
                { database with
                    execScalar = fun _ _ -> Task.FromResult<obj | null>(null) }
            let! duplicate = FolderWatcher.isDuplicate nullScalar "abc123"
            Assert.False(duplicate)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``FolderWatcher_ProcessFile_ContentReadFails_ReturnsFailedAndLogsError`` () =
    withFixture (fun memory database ->
        task {
            let source = "/watch/broken.pdf"
            let error = "content read failed"
            memory.Put source "existing content"
            let errors = ConcurrentQueue<string>()
            let logger = { TestHelpers.silentLogger with error = errors.Enqueue }
            let fs =
                { memory.Fs with
                    readAllBytes = fun _ -> Task.FromException<byte array>(IOException(error)) }
            let! result =
                FolderWatcher.processFile fs database logger TestHelpers.defaultClock
                    archiveDir (folder "/watch" "*.pdf") source
            Assert.Equal<FolderWatcher.IntakeResult>(FolderWatcher.Failed error, result)
            Assert.Equal<string>($"Failed to process broken.pdf: {error}", Assert.Single<string>(errors))
            Assert.Empty(memory.Bytes)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``FolderWatcher_ScanFolder_PathMissing_ReturnsEmptyAndWarns`` () =
    withFixture (fun memory database ->
        task {
            let missing = "/watch/missing"
            let warnings = ConcurrentQueue<string>()
            let logger = { TestHelpers.silentLogger with warn = warnings.Enqueue }
            let fs =
                { memory.Fs with
                    getFiles = fun _ _ -> failwith "Missing folders must not be enumerated" }
            let! results = scanFolder fs database logger (folder missing "*")
            Assert.Empty(results)
            Assert.Equal<string>(
                $"Watch folder does not exist: {missing}", Assert.Single<string>(warnings))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``FolderWatcher_ScanAll_ConfiguredFolders_AggregatesMatchingAndMismatchingResults`` () =
    withFixture (fun memory database ->
        task {
            let scenario = scanScenario memory
            let! results =
                FolderWatcher.scanAll memory.Fs database TestHelpers.silentLogger
                    TestHelpers.defaultClock scenario.Config
            let byPath = results |> Map.ofList
            let expectedCount = scenario.CopiedPaths.Length + scenario.SkippedPaths.Length
            Assert.Equal(expectedCount, results.Length)
            Assert.Equal(expectedCount, byPath.Count)
            scenario.CopiedPaths
            |> List.iter (fun path -> assertCopied memory path byPath[path])
            scenario.SkippedPaths
            |> List.iter (fun path ->
                Assert.Equal<FolderWatcher.IntakeResult>(
                    FolderWatcher.Skipped "pattern mismatch", byPath[path]))
        })
