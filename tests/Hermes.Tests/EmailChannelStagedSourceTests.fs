module Hermes.Tests.EmailChannelStagedSourceTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

type private Pause =
    { Fixture: Fixture
      Entered: Task<unit>
      Resume: unit -> unit }

let private pauseMetadata (fs: Algebra.FileSystem)
    (entered: TaskCompletionSource<unit>) (resume: TaskCompletionSource<unit>)
    (fail: bool) (path: string) (bytes: byte array) : Task<unit> =
    task {
        if path.EndsWith(".hermes.json", StringComparison.Ordinal) then
            entered.TrySetResult(()) |> ignore
            do! resume.Task
            if fail then raise (IO.IOException("synthetic transactional metadata failure"))
        do! fs.writeAllBytes path bytes
    }

let private pauseInsideTransaction (fixture: Fixture) (fail: bool) : Pause =
    let entered = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let resume = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let fs =
        { fixture.Files.Fs with
            writeAllBytes = pauseMetadata fixture.Files.Fs entered resume fail }
    { Fixture = { fixture with Files = { fixture.Files with Fs = fs } }
      Entered = entered.Task
      Resume = fun () -> resume.TrySetResult(()) |> ignore }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``A standalone staged claim refuses another writer deletion or movement before INSERT`` (move: bool) =
    withFixture (fun fixture ->
        task {
            let file = attachment "pending.pdf" "pending producer source bytes"
            let path = fullPath fixture file.FileName
            do! SourceFiles.writeNew fixture.Database fixture.Files.Fs path file.Content
            let destination = fullPath fixture "other-producer.pdf"
            let operation () =
                if move then SourceFiles.move fixture.Database fixture.Files.Fs path destination
                else SourceFiles.delete fixture.Database fixture.Files.Fs path
            let! _ = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> operation () :> Task))
            let! bytes = fixture.Files.Fs.readAllBytes path
            Assert.True(file.Content = bytes)
            Assert.False(fixture.Files.Fs.fileExists destination)
            do! assertClaim fixture path (SourceFiles.hash file.Content)
            let! count = documentCount fixture
            Assert.Equal(0L, count)
        })

let private assertBlocked (read: Task<int64>) (deletion: Task<unit>) : Result<unit, exn> =
    try
        Assert.False(read.IsCompleted)
        Assert.False(deletion.IsCompleted)
        Ok ()
    with error ->
        Error error

let private completeCommitted
    (fixture: Fixture) (running: Task<RunResult>) (read: Task<int64>)
    (deletion: Task<unit>) (inspection: Result<unit, exn>) : Task<unit> =
    task {
        let! result = running
        let! count = read
        let! _ = Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> deletion :> Task))
        inspection |> Result.defaultWith raise
        assertSuccess 1 1 result
        Assert.Equal(1L, count)
        do! assertRegistered fixture (source "email_attachment" result)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``A competing writer and public reader wait for the complete email commit`` () =
    withFixture (fun fixture ->
        task {
            let pause = pauseInsideTransaction fixture false
            let incoming = message "commit-one" "commit-thread" None
            let file = attachment "source.pdf" "atomic source bytes"
            let running = run pause.Fixture (single incoming [ file ]) 1
            do! pause.Entered.WaitAsync(TimeSpan.FromSeconds(30.0))
            let read = documentCount fixture
            let deletion = SourceFiles.delete fixture.Database fixture.Files.Fs (attachmentPath fixture incoming file)
            let inspection = assertBlocked read deletion
            pause.Resume ()
            do! completeCommitted fixture running read deletion inspection
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``A public reader observes no partial email after transactional save failure`` () =
    withFixture (fun fixture ->
        task {
            let pause = pauseInsideTransaction fixture true
            let incoming = message "rollback-one" "rollback-thread" None
            let running = run pause.Fixture (single incoming [ attachment "source.pdf" "rollback bytes" ]) 1
            do! pause.Entered.WaitAsync(TimeSpan.FromSeconds(30.0))
            let read = documentCount fixture
            let blocked = not read.IsCompleted
            pause.Resume ()
            let! result = running
            let! count = read
            Assert.True(blocked)
            assertStorageFailure result
            Assert.Equal(0L, count)
            do! assertAcknowledged fixture incoming.ProviderId 0L
        })
