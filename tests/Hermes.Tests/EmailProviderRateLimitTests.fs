module Hermes.Tests.EmailProviderRateLimitTests

open System
open System.Collections.Concurrent
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailProviderTransportFixtures

let private throttled (isGmail: bool) : exn =
    if isGmail then
        Google.GoogleApiException(
            "synthetic", "synthetic transport HTTP429",
            HttpStatusCode = HttpStatusCode.TooManyRequests)
    else
        Microsoft.Kiota.Abstractions.ApiException(
            "synthetic transport HTTP429", ResponseStatusCode = 429)

let private queued () : ChannelReader<string> =
    let input = Channel.CreateUnbounded<string>()
    Assert.True(input.Writer.TryWrite("transport-first"))
    Assert.True(input.Writer.TryWrite("transport-second"))
    input.Writer.Complete()
    input.Reader

let private probeGate (fixture: Fixture) : Task<unit> =
    fixture.Database.transaction.Run(fun transaction ->
        task {
            let! count = transaction.execScalar "SELECT COUNT(*) FROM messages" []
            Assert.Equal(0L, Assert.IsType<int64>(count))
        })

let private delay (fixture: Fixture) (cancel: bool)
    (cancellation: CancellationTokenSource) (calls: ConcurrentQueue<TimeSpan>)
    (duration: TimeSpan) (ct: CancellationToken) : Task<unit> =
    task {
        Assert.Equal(TimeSpan.FromSeconds(60.0), duration)
        Assert.Equal(cancellation.Token, ct)
        calls.Enqueue(duration)
        do! probeGate fixture
        if cancel then
            cancellation.Cancel()
            ct.ThrowIfCancellationRequested()
    }

let private consume (fixture: Fixture) (provider: Algebra.EmailProvider)
    (pause: EmailRateLimit.Delay) (ct: CancellationToken) : Task<EmailChannelConsumer.Summary> =
    let context =
        EmailSync.channelContext fixture.Files.Fs fixture.Database fixture.Logger
            fixture.Clock fixture.Config fixture.Account
    let notify (_id: int64) (_path: string) : Task<unit> = Task.FromResult()
    EmailChannelConsumer.runWithDelay pause context provider (queued ()) notify (ref 0) ct

let private check (fixture: Fixture) (cancel: bool)
    (result: EmailChannelConsumer.Summary) : Task<unit> =
    task {
        Assert.Equal(1, result.Failed)
        Assert.Equal((if cancel then 0 else 1), result.Processed)
        Assert.Equal(0, result.Duplicates)
        Assert.Contains("synthetic transport HTTP429", Assert.Single<string>(result.Errors))
        do! assertAcknowledged fixture "transport-first" 0L
        do! assertAcknowledged fixture "transport-second" (if cancel then 0L else 1L)
    }

let private limitedResponse (isGmail: bool)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    if messageId request = "transport-first" then
        Task.FromException<HttpResponseMessage>(throttled isGmail)
    else
        successMessage isGmail request

let private exercise (fixture: Fixture) (isGmail: bool) (cancel: bool) : Task<unit> =
    task {
        use cancellation = new CancellationTokenSource()
        let calls = ConcurrentQueue<TimeSpan>()
        let! transport = create isGmail fixture.Logger (limitedResponse isGmail)
        try
            let pause = delay fixture cancel cancellation calls
            let! result = consume fixture transport.Provider pause cancellation.Token
            Assert.Single(calls) |> ignore
            do! check fixture cancel result
        finally
            transport.Dispose()
    }

[<Theory>]
[<InlineData(true, false)>]
[<InlineData(false, false)>]
[<InlineData(true, true)>]
[<InlineData(false, true)>]
[<Trait("Category", "Integration")>]
let ``Production transport HTTP429 preserves sixty-second cancellable backoff outside the database gate``
    (isGmail: bool, cancel: bool) =
    withFixture (fun fixture -> exercise fixture isGmail cancel)
