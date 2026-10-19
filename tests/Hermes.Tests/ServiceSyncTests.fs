module Hermes.Tests.ServiceSyncTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Service
open Hermes.Tests.ServiceSyncTestSupport

[<Fact>]
let ``Manual wake bypasses the initial delay and in-flight requests coalesce`` () =
    withOwner distant distant Gated (fun harness ->
        task {
            Assert.Equal(Ok ManualSync.Queued, harness.Owner.Manual.Request ())
            let! first = next harness.Recording.Started.Reader
            Assert.Equal(1, first)
            Assert.Equal(Ok ManualSync.Queued, harness.Owner.Manual.Request ())
            [ 1 .. 20 ] |> List.iter (fun _ ->
                Assert.Equal(Ok ManualSync.Coalesced, harness.Owner.Manual.Request ()))
            release harness
            let! second = next harness.Recording.Started.Reader
            Assert.Equal(2, second)
            Assert.Equal<string list>(
                [ "start:1"; "end:1"; "start:2" ],
                harness.Recording.History |> Seq.toList)
        })

[<Fact>]
let ``Automatic cycles continue without manual requests`` () =
    withOwner TimeSpan.Zero (TimeSpan.FromMilliseconds 25.0) Immediate (fun harness ->
        task {
            let! first = next harness.Recording.Started.Reader
            let! second = next harness.Recording.Started.Reader
            Assert.Equal(1, first)
            Assert.Equal(2, second)
        })

[<Fact>]
let ``Cancellation interrupts the initial wait without starting a cycle`` () =
    withOwner distant distant Immediate (fun harness ->
        task {
            harness.Cancellation.Cancel()
            do! harness.Work.WaitAsync(timeout)
            Assert.Empty(harness.Recording.Runs)
            Assert.Equal(Error ManualSync.Stopping, harness.Owner.Manual.Request ())
        })

[<Fact>]
let ``Shutdown discards pending wakeups and joins the cancelled current cycle`` () =
    withOwner distant distant Gated (fun harness ->
        task {
            harness.Owner.Manual.Request () |> ignore
            let! _ = next harness.Recording.Started.Reader
            Assert.Equal(Ok ManualSync.Queued, harness.Owner.Manual.Request ())
            harness.Owner.Stop ()
            harness.Cancellation.Cancel()
            do! harness.Work.WaitAsync(timeout)
            Assert.Equal<int list>([ 1 ], harness.Recording.Runs |> Seq.toList)
            Assert.Equal(Error ManualSync.Stopping, harness.Owner.Manual.Request ())
        })

[<Fact>]
let ``A cycle exception is logged and the same owner handles the follow-up`` () =
    withOwner distant distant FailFirst (fun harness ->
        task {
            harness.Owner.Manual.Request () |> ignore
            let! _ = next harness.Recording.Started.Reader
            harness.Owner.Manual.Request () |> ignore
            let! second = next harness.Recording.Started.Reader
            let! completed = next harness.Recording.Finished.Reader
            Assert.Equal(2, second)
            Assert.Equal(2, completed)
            Assert.Contains(harness.Log.Messages, fun message ->
                message.Contains("synthetic cycle failure"))
        })

[<Fact>]
let ``Readiness and stopped state reject requests explicitly`` () =
    use cancellation = new CancellationTokenSource()
    let owner =
        ProducerLoop.create
            { InitialDelay = distant; Interval = distant
              Logger = (capture ()).Logger
              Cycle = fun _ -> Task.FromResult(()) }
            cancellation.Token
    Assert.Equal(Error ManualSync.Starting, owner.Manual.Request ())
    owner.Stop ()
    Assert.Equal(Error ManualSync.Stopping, owner.Manual.Request ())

[<Theory>]
[<InlineData(0.0)>]
[<InlineData(-1.0)>]
let ``Invalid periodic intervals fail before an owner can be advertised`` (minutes: float) =
    let options : ProducerLoop.Options =
        { InitialDelay = TimeSpan.Zero
          Interval = TimeSpan.FromMinutes minutes
          Logger = (capture ()).Logger
          Cycle = fun _ -> Task.FromResult(()) }
    Assert.Throws<ArgumentException>(fun () ->
        ProducerLoop.create options CancellationToken.None |> ignore)
    |> ignore

[<Fact>]
let ``Fatal owner failure is observed and closes manual admission`` () =
    task {
        let captured = capture ()
        let failingLogger = { captured.Logger with info = fun _ -> invalidOp "logger failed" }
        let owner =
            ProducerLoop.create
                { InitialDelay = TimeSpan.Zero; Interval = distant
                  Logger = failingLogger; Cycle = fun _ -> Task.FromResult(()) }
                CancellationToken.None
        let observed =
            owner.Run ()
            |> Lifetime.observeBackground captured.Logger "synthetic-owner" CancellationToken.None
        do! observed.WaitAsync(timeout)
        Assert.Equal(Error ManualSync.Stopping, owner.Manual.Request ())
        Assert.Contains(captured.Messages, fun message -> message.Contains("synthetic-owner"))
    }
