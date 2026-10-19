module Hermes.Tests.ServiceSyncTestSupport

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Hermes.Core
open Hermes.Service

let timeout = TimeSpan.FromSeconds 5.0
let distant = TimeSpan.FromHours 1.0

type CapturedLog =
    { Messages: ConcurrentQueue<string>
      Logger: Algebra.Logger }

let capture () =
    let messages = ConcurrentQueue<string>()
    { Messages = messages
      Logger =
        { info = messages.Enqueue; warn = messages.Enqueue
          error = messages.Enqueue; debug = messages.Enqueue } }

type Mode = Gated | Immediate | FailFirst

type Recording =
    { Runs: ConcurrentQueue<int>
      History: ConcurrentQueue<string>
      Started: Channel<int>
      Finished: Channel<int>
      Release: Channel<unit> }

type Harness =
    { Owner: ProducerLoop.Handle
      Cancellation: CancellationTokenSource
      Work: Task
      Recording: Recording
      Log: CapturedLog }

let private recordCycle mode recording (token: CancellationToken) =
    task {
        let number = recording.Runs.Count + 1
        recording.Runs.Enqueue(number)
        recording.History.Enqueue($"start:{number}")
        recording.Started.Writer.TryWrite(number) |> ignore
        if mode = Gated then
            let! () = recording.Release.Reader.ReadAsync(token)
            ()
        if mode = FailFirst && number = 1 then invalidOp "synthetic cycle failure"
        recording.History.Enqueue($"end:{number}")
        recording.Finished.Writer.TryWrite(number) |> ignore
    }

let private recording () =
    { Runs = ConcurrentQueue<int>(); History = ConcurrentQueue<string>()
      Started = Channel.CreateUnbounded<int>()
      Finished = Channel.CreateUnbounded<int>()
      Release = Channel.CreateUnbounded<unit>() }

let create initial interval mode =
    let log = capture ()
    let events = recording ()
    let cancellation = new CancellationTokenSource()
    let owner =
        ProducerLoop.create
            { InitialDelay = initial; Interval = interval
              Logger = log.Logger; Cycle = recordCycle mode events }
            cancellation.Token
    { Owner = owner; Cancellation = cancellation; Work = owner.Run ()
      Recording = events; Log = log }

let stop harness =
    harness.Owner.Stop ()
    harness.Cancellation.Cancel()
    harness.Work.WaitAsync(timeout).GetAwaiter().GetResult()
    harness.Cancellation.Dispose()

let withOwner initial interval mode verify =
    task {
        let harness = create initial interval mode
        try do! verify harness
        finally stop harness
    }

let next (reader: ChannelReader<'value>) =
    reader.ReadAsync().AsTask().WaitAsync(timeout)

let release harness =
    harness.Recording.Release.Writer.TryWrite(()) |> ignore
