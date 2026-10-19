module Hermes.Tests.EmailProviderCommitContractTests

open System
open System.Collections.Concurrent
open System.Net.Http
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailProviderTransportFixtures
open Hermes.Tests.EmailProviderSuccessFixtures

type private Attempt =
    { Input: ChannelReader<string>
      Processed: int ref
      Seen: ConcurrentQueue<Document.T> }

let private notificationFailure = "synthetic post-commit notification failure"
let private watermarkFailure = "synthetic initial watermark read failure"

let private withGmail (handler: HttpRequestMessage -> Task<HttpResponseMessage>)
    (verify: Fixture -> Algebra.EmailProvider -> Task<unit>) : Task<unit> =
    withFixture (fun fixture ->
        task {
            let! transport = create true fixture.Logger handler
            try
                do! verify fixture transport.Provider
            finally
                transport.Dispose()
        })

let private prepare (fixture: Fixture) (provider: Algebra.EmailProvider) : Task<Attempt> =
    task {
        let input = Channel.CreateUnbounded<string>()
        let enumerated = ref 0
        let! count =
            EmailSync.enumerateIds provider fixture.Logger fixture.Account "after:0"
                input.Writer enumerated CancellationToken.None
        Assert.Equal(1, count)
        Assert.Equal(1, enumerated.Value)
        return { Input = input.Reader; Processed = ref 0; Seen = ConcurrentQueue<Document.T>() }
    }

let private notify (fixture: Fixture) (seen: ConcurrentQueue<Document.T>)
    (id: int64) (path: string) : Task<unit> =
    task {
        let! rows =
            fixture.Database.execReader "SELECT * FROM documents WHERE id = @id"
                [ "@id", Database.boxVal id ]
        let document = Assert.Single<Document.T>(rows)
        Assert.Equal<string>(path, required "saved_path" document)
        let! count = documentCount fixture
        Assert.Equal(2L, count)
        do! assertAcknowledged fixture ids.Head 1L
        do! assertRegistered fixture document
        seen.Enqueue(document)
        if required "source_type" document = "email_body" then
            invalidOp notificationFailure
    }

let private assertNotificationError (errors: string list) : unit =
    let error = Assert.Single<string>(errors)
    Assert.StartsWith("Post-commit notification for document ", error)
    Assert.EndsWith(notificationFailure, error)

let private verifySummary (fixture: Fixture) (provider: Algebra.EmailProvider)
    (attempt: Attempt) : Task<unit> =
    let context =
        EmailSync.channelContext fixture.Files.Fs fixture.Database fixture.Logger
            fixture.Clock fixture.Config fixture.Account
    let operation =
        EmailChannelConsumer.run context provider attempt.Input
            (notify fixture attempt.Seen) attempt.Processed CancellationToken.None
    task {
        let! summary = operation.WaitAsync(TimeSpan.FromSeconds(20.0))
        Assert.Equal(1, summary.Processed)
        Assert.Equal(2, summary.Downloaded)
        Assert.Equal(0, summary.Duplicates)
        Assert.Equal(0, summary.Failed)
        assertNotificationError summary.Errors
    }

let private verifyAdapter (fixture: Fixture) (provider: Algebra.EmailProvider)
    (attempt: Attempt) : Task<unit> =
    let operation =
        EmailSync.processMessageConsumer fixture.Files.Fs fixture.Database fixture.Logger
            fixture.Clock provider fixture.Config fixture.Account attempt.Input
            (notify fixture attempt.Seen) attempt.Processed 1 CancellationToken.None
    task {
        let! error =
            Assert.ThrowsAsync<EmailSync.ChannelConsumerFailed>(
                Func<Task>(fun () -> operation.WaitAsync(TimeSpan.FromSeconds(20.0)) :> Task))
        match (error :> exn) with
        | EmailSync.ChannelConsumerFailed errors -> assertNotificationError errors
        | _ -> failwith "Expected the channel consumer failure payload"
    }

let private verifyCommitted (fixture: Fixture) (provider: Algebra.EmailProvider)
    (attempt: Attempt) : Task<unit> =
    task {
        Assert.Equal(1, attempt.Processed.Value)
        let documents = attempt.Seen |> Seq.toList
        Assert.Equal<string list>(
            [ "email_body"; "email_attachment" ], List.map (required "source_type") documents)
        for document in documents do do! assertRegistered fixture document
        do! assertAcknowledged fixture ids.Head 1L
        let before = snapshot fixture
        let! retry = run fixture provider 1
        assertSuccess 0 0 retry
        Assert.Equal(1, retry.Summary.DuplicatesSkipped)
        Assert.Equal<Map<string, string>>(before, snapshot fixture)
        let! count = documentCount fixture
        Assert.Equal(2L, count)
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Post-commit notification failure preserves the complete email and remains explicit``
    (publicAdapter: bool) =
    withGmail (send true true SingleMessage) (fun fixture provider ->
        task {
            let! attempt = prepare fixture provider
            if publicAdapter then do! verifyAdapter fixture provider attempt
            else do! verifySummary fixture provider attempt
            do! verifyCommitted fixture provider attempt
        })

let private legacyRun (fixture: Fixture) (provider: Algebra.EmailProvider) : Task<RunResult> =
    task {
        let! summary =
            EmailSync.syncAccount fixture.Files.Fs fixture.Database fixture.Logger fixture.Clock
                provider fixture.Config fixture.Account
        return { Summary = summary; Documents = [] }
    }

let private unreadableWatermark (fixture: Fixture) : Fixture =
    let database =
        { fixture.Database with
            execScalar = fun sql _ ->
                Assert.Contains("sync_state", sql)
                Task.FromException<obj | null>(InvalidOperationException(watermarkFailure)) }
    { fixture with Database = database }

let private assertReadFailureSummary (fixture: Fixture) (result: RunResult) : unit =
    assertStorageFailure result
    Assert.Equal<string>(watermarkFailure, Assert.Single<string>(result.Summary.Errors))
    Assert.Equal<string>(fixture.Account, result.Summary.Account)
    Assert.Contains<string>(
        fixture.Logs,
        Predicate<string>(fun text -> text.Contains(watermarkFailure, StringComparison.Ordinal)))

let private verifyReadFailure (fixture: Fixture) (provider: Algebra.EmailProvider)
    (requests: ConcurrentQueue<HttpMethod>) (legacy: bool) : Task<unit> =
    task {
        let initial = fixture.Clock.utcNow().AddDays(-30.0)
        do! seedWatermark fixture initial
        let broken = unreadableWatermark fixture
        let! result = if legacy then legacyRun broken provider else run broken provider 1
        assertReadFailureSummary fixture result
        Assert.Empty(requests)
        Assert.Empty(fixture.Files.Files)
        let! count = documentCount fixture
        Assert.Equal(0L, count)
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some initial, watermark)
    }

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
[<Trait("Category", "Integration")>]
let ``Initial watermark read failure prevents SDK work and preserves stored state`` (legacy: bool) =
    let requests = ConcurrentQueue<HttpMethod>()
    let handler (request: HttpRequestMessage) : Task<HttpResponseMessage> =
        requests.Enqueue(request.Method)
        failure true
    withGmail handler (fun fixture provider -> verifyReadFailure fixture provider requests legacy)
