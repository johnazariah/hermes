module Hermes.Tests.ProducerCycleTestSupport

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Service
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

type Scenario =
    { Fixture: Fixture
      Context: ProducerCycle.Context
      Messages: ConcurrentQueue<string> }

let withScenario verify =
    withFixture (fun fixture ->
        let log = ServiceSyncTestSupport.capture ()
        let context: ProducerCycle.Context =
            { Fs = fixture.Files.Fs; Db = fixture.Database
              Logger = log.Logger; Clock = fixture.Clock
              Config = { fixture.Config with Accounts = []; WatchFolders = [] }
              CreateEmailProvider = fun _ -> Task.FromResult TestHelpers.emptyProvider }
        verify { Fixture = fixture; Context = context; Messages = log.Messages })

let run context = ProducerCycle.run context CancellationToken.None

let withAccounts scenario labels create =
    let template = scenario.Fixture.Config.Accounts |> List.head
    { scenario.Context with
        Config =
            { scenario.Context.Config with
                Accounts = labels |> List.map (fun label -> { template with Label = label }) }
        CreateEmailProvider = create }

let assertLog scenario (fragment: string) =
    let messages = String.Join("\n", scenario.Messages)
    Assert.True(
        scenario.Messages |> Seq.exists (fun message -> message.Contains(fragment, StringComparison.Ordinal)),
        $"Missing log fragment '{fragment}': {messages}")

let documents scenario =
    scenario.Context.Db.execReader "SELECT * FROM documents ORDER BY id" []

let assertCount scenario expected =
    task {
        let! rows = documents scenario
        Assert.Equal(expected, rows.Length)
    }

let drop scenario name text =
    let directory = Path.Combine(scenario.Context.Config.ArchiveDir, "unclassified")
    scenario.Fixture.Files.Fs.createDirectory directory
    let path = Path.Combine(directory, name)
    scenario.Fixture.Files.Put path text
    path

let canonicalPath scenario (name: string) (text: string) =
    let now = scenario.Context.Clock.utcNow ()
    let stem = Path.GetFileNameWithoutExtension(name) |> Option.ofObj |> Option.defaultValue "file"
    let sha = Encoding.UTF8.GetBytes(text: string) |> SourceFiles.hash
    Path.Combine(
        scenario.Context.Config.ArchiveDir,
        ArchiveWriter.localFolderPath now stem,
        ArchiveWriter.attachmentFileName now name sha)

let assertIntake scenario path text =
    task {
        let! rows =
            scenario.Context.Db.execReader
                "SELECT * FROM documents WHERE hermes_saved_path_key(saved_path) = hermes_saved_path_key(@path)"
                [ "@path", Database.boxVal path ]
        let row = rows |> List.exactlyOne
        let bytes = Encoding.UTF8.GetBytes(text: string)
        Assert.Equal("watched_folder", required "source_type" row)
        Assert.Equal("received", required "stage" row)
        Assert.Equal(SourceFiles.hash bytes, required "sha256" row)
        Assert.Equal(int64 bytes.Length, Assert.IsType<int64>(row["size_bytes"]))
        Assert.Equal(Path.GetRelativePath(scenario.Context.Config.ArchiveDir, path), required "saved_path" row)
        Assert.Equal(scenario.Fixture.Files.Norm path, scenario.Fixture.Files.Norm (required "source_path" row))
        Assert.Equal(scenario.Context.Clock.utcNow().ToString("o"), required "ingested_at" row)
        return row
    }

let assertWatermark scenario account (timestamp: DateTimeOffset) count =
    task {
        let! rows =
            scenario.Context.Db.execReader
                "SELECT last_sync_at, message_count FROM sync_state WHERE account = @account"
                [ "@account", Database.boxVal account ]
        let row = rows |> List.exactlyOne
        Assert.Equal(timestamp.ToString("o"), required "last_sync_at" row)
        Assert.Equal(count, Assert.IsType<int64>(row["message_count"]))
    }

let assertNoWatermark scenario account =
    task {
        let! state = EmailSync.loadSyncState scenario.Context.Db account
        Assert.True(Option.isNone state)
    }

let assertCancelled (work: Task<unit>) =
    task {
        let! _ =
            Assert.ThrowsAnyAsync<OperationCanceledException>(
                Func<Task>(fun () -> work :> Task))
        return ()
    }
