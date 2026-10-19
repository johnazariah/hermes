module Hermes.Tests.EmailRateLimitTests

open System
open System.Collections.Concurrent
open System.Net
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

type private Work =
    { LimitedId: string
      NextId: string
      Provider: Algebra.EmailProvider
      Input: ChannelReader<string>
      Calls: ConcurrentQueue<string> }

type private Pause =
    { Started: Task<unit>
      Release: unit -> unit
      Delay: EmailRateLimit.Delay }

let private limitedProvider (source: Algebra.EmailProvider)
    (calls: ConcurrentQueue<string>) (limitedId: string) : Algebra.EmailProvider =
    { source with
        getFullMessage = fun id ->
            calls.Enqueue(id)
            if id = limitedId then
                let error =
                    Google.GoogleApiException(
                        "synthetic", "synthetic HTTP 429",
                        HttpStatusCode = HttpStatusCode.TooManyRequests)
                Task.FromException<Domain.EmailMessage>(error)
            else
                source.getFullMessage id }

let private queued (ids: string list) : ChannelReader<string> =
    let input = Channel.CreateUnbounded<string>()
    ids |> List.iter (fun id -> Assert.True(input.Writer.TryWrite(id)))
    input.Writer.Complete()
    input.Reader

let private work () : Work =
    let first = message "rate-first" "rate-thread-first" None
    let next = message "rate-next" "rate-thread-next" None
    let messages = [ first; next ]
    let calls = ConcurrentQueue<string>()
    let source = provider (pages messages) messages Map.empty
    { LimitedId = first.ProviderId
      NextId = next.ProviderId
      Provider = limitedProvider source calls first.ProviderId
      Input = queued [ first.ProviderId; next.ProviderId ]
      Calls = calls }

let private probeGate (fixture: Fixture) : Task<unit> =
    fixture.Database.transaction.Run(fun transaction ->
        task {
            let! count = transaction.execScalar "SELECT COUNT(*) FROM messages" []
            Assert.Equal(0L, Assert.IsType<int64>(count))
        })

let private awaitBackoff (fixture: Fixture)
    (started: TaskCompletionSource<unit>) (resume: TaskCompletionSource<unit>)
    (duration: TimeSpan) (ct: CancellationToken) : Task<unit> =
    task {
        Assert.Equal(TimeSpan.FromSeconds(60.0), duration)
        do! (probeGate fixture).WaitAsync(TimeSpan.FromSeconds(10.0))
        started.TrySetResult(()) |> ignore
        do! resume.Task.WaitAsync(ct)
    }

let private pause (fixture: Fixture) : Pause =
    let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    let resume = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
    { Started = started.Task
      Release = fun () -> resume.TrySetResult(()) |> ignore
      Delay = awaitBackoff fixture started resume }

let private runControlled (fixture: Fixture) (attempt: Work) (backoff: Pause)
    (ct: CancellationToken) : Task<EmailChannelConsumer.Summary> =
    let context =
        EmailSync.channelContext fixture.Files.Fs fixture.Database fixture.Logger
            fixture.Clock fixture.Config fixture.Account
    let notify (_id: int64) (_path: string) : Task<unit> = Task.FromResult()
    EmailChannelConsumer.runWithDelay backoff.Delay context attempt.Provider attempt.Input notify (ref 0) ct

let private assertRateFailure (summary: EmailChannelConsumer.Summary) : unit =
    Assert.Equal(1, summary.Failed)
    Assert.Equal(0, summary.Duplicates)
    Assert.Equal(0, summary.Downloaded)
    let error = Assert.Single<string>(summary.Errors)
    Assert.Contains("synthetic HTTP 429", error)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``HTTP429 waits sixty seconds outside the write gate before taking the next message`` () =
    withFixture (fun fixture ->
        task {
            let attempt = work ()
            let backoff = pause fixture
            let running = runControlled fixture attempt backoff CancellationToken.None
            do! backoff.Started.WaitAsync(TimeSpan.FromSeconds(10.0))
            let beforeRelease = attempt.Calls |> Seq.toList
            backoff.Release ()
            let! result = running.WaitAsync(TimeSpan.FromSeconds(10.0))
            Assert.Equal<string list>([ attempt.LimitedId ], beforeRelease)
            assertRateFailure result
            Assert.Equal(1, result.Processed)
            do! assertAcknowledged fixture attempt.LimitedId 0L
            do! assertAcknowledged fixture attempt.NextId 1L
        })

let private assertCancelled (fixture: Fixture) (attempt: Work)
    (beforeCancellation: string list) (result: EmailChannelConsumer.Summary) : Task<unit> =
    task {
        assertRateFailure result
        Assert.Equal(0, result.Processed)
        Assert.Equal<string list>([ attempt.LimitedId ], beforeCancellation)
        Assert.Equal<string list>(beforeCancellation, attempt.Calls |> Seq.toList)
        do! assertAcknowledged fixture attempt.LimitedId 0L
        do! assertAcknowledged fixture attempt.NextId 0L
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Cancellation interrupts HTTP429 backoff without fetching the next message`` () =
    withFixture (fun fixture ->
        task {
            use cancellation = new CancellationTokenSource()
            let attempt = work ()
            let backoff = pause fixture
            let running = runControlled fixture attempt backoff cancellation.Token
            do! backoff.Started.WaitAsync(TimeSpan.FromSeconds(10.0))
            let beforeCancellation = attempt.Calls |> Seq.toList
            cancellation.Cancel()
            let! result = running.WaitAsync(TimeSpan.FromSeconds(10.0))
            backoff.Release ()
            do! assertCancelled fixture attempt beforeCancellation result
        })
