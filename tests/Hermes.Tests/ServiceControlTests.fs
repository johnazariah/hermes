module Hermes.Tests.ServiceControlTests

open System
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Service
open Hermes.Tests.ServiceSyncTestSupport

type private Reader =
    { Signals: Channel<OwnedControl.Signal>
      Stopped: TaskCompletionSource<unit>
      Cancellation: CancellationTokenSource
      Work: Task
      Log: CapturedLog }

let private reader () =
    let signals = Channel.CreateUnbounded<OwnedControl.Signal>()
    let stopped = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let cancellation = new CancellationTokenSource()
    let log = capture ()
    let input : OwnedControl.Input =
        { Read = fun token -> signals.Reader.ReadAsync(token).AsTask() }
    let stop () = stopped.TrySetResult() |> ignore
    { Signals = signals; Stopped = stopped; Cancellation = cancellation
      Work = OwnedControl.run log.Logger input stop cancellation.Token; Log = log }

let private withReader verify =
    task {
        let owned = reader ()
        try do! verify owned
        finally
            owned.Cancellation.Cancel()
            owned.Work.WaitAsync(timeout).GetAwaiter().GetResult()
            owned.Cancellation.Dispose()
    }

let private send owned text =
    Encoding.UTF8.GetBytes(text: string)
    |> Array.iter (fun value ->
        owned.Signals.Writer.TryWrite(OwnedControl.Byte value) |> ignore)

[<Fact>]
let ``Only the exact command stops and CRLF framing is supported`` () =
    withReader (fun owned ->
        task {
            send owned "Shutdown\nshutdown \n shutdown\nshutdown\r\n"
            do! owned.Work.WaitAsync(timeout)
            Assert.True(owned.Stopped.Task.IsCompletedSuccessfully)
            Assert.Equal(
                3,
                owned.Log.Messages
                |> Seq.filter (fun message -> message.Contains("Rejected unknown"))
                |> Seq.length)
        })

[<Fact>]
let ``EOF stops even with an unterminated unknown line`` () =
    withReader (fun owned ->
        task {
            send owned "partial"
            owned.Signals.Writer.TryWrite(OwnedControl.End) |> ignore
            do! owned.Work.WaitAsync(timeout)
            Assert.True(owned.Stopped.Task.IsCompletedSuccessfully)
        })

[<Fact>]
let ``Cancellation joins an idle reader without inventing a shutdown command`` () =
    withReader (fun owned ->
        task {
            owned.Cancellation.Cancel()
            do! owned.Work.WaitAsync(timeout)
            Assert.False(owned.Stopped.Task.IsCompleted)
        })

[<Fact>]
let ``Overlong input is rejected without hiding the next valid command`` () =
    withReader (fun owned ->
        task {
            send owned (String.replicate 80 "x" + "\nshutdown\n")
            do! owned.Work.WaitAsync(timeout)
            Assert.True(owned.Stopped.Task.IsCompletedSuccessfully)
            Assert.Contains(owned.Log.Messages, fun message -> message.Contains("overlong"))
        })

[<Fact>]
let ``A failed owner channel is logged and stops the service`` () =
    task {
        let captured = capture ()
        let stopped = TaskCompletionSource<unit>()
        let input : OwnedControl.Input =
            { Read = fun _ -> Task.FromException<OwnedControl.Signal>(IO.IOException("broken input")) }
        let work =
            OwnedControl.run captured.Logger input
                (fun () -> stopped.TrySetResult() |> ignore) CancellationToken.None
        do! work.WaitAsync(timeout)
        Assert.True(stopped.Task.IsCompletedSuccessfully)
        Assert.Contains(captured.Messages, fun message -> message.Contains("broken input"))
    }

[<Fact>]
let ``Owned flag is removed before ordinary argument handling`` () =
    let parsed =
        OwnedControl.parse [| "--initial-sync-days"; "2"; "--control-stdin" |]
        |> Result.defaultWith invalidOp
    Assert.True(parsed.Owned)
    Assert.Equal<string list>([ "--initial-sync-days"; "2" ], Array.toList parsed.Arguments)

[<Theory>]
[<InlineData("--control-stdin=true")>]
[<InlineData("--control-stdin=false")>]
[<InlineData("--control-stdin-extra")>]
let ``Control flag does not accept values or aliases`` argument =
    Assert.True(OwnedControl.parse [| argument |] |> Result.isError)

[<Fact>]
let ``Duplicate control flags are rejected and foreground arguments are unchanged`` () =
    Assert.True(
        OwnedControl.parse [| "--control-stdin"; "--control-stdin" |] |> Result.isError)
    let parsed =
        OwnedControl.parse [| "--initial-sync-days"; "3" |]
        |> Result.defaultWith invalidOp
    Assert.False(parsed.Owned)
    Assert.Equal<string list>([ "--initial-sync-days"; "3" ], Array.toList parsed.Arguments)
