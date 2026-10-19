module Hermes.Tests.EmailChannelFailureContractTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

let private failedIds = [ "failed-one"; "failed-two" ]

let private feed: Algebra.EmailProvider =
    let incoming = failedIds |> List.map (fun id -> message id $"thread-{id}" None)
    provider (pages incoming) incoming Map.empty

let private failingFeed (attempts: ConcurrentQueue<string>) : Algebra.EmailProvider =
    { feed with
        getFullMessage = fun id ->
            attempts.Enqueue(id)
            Task.FromException<Domain.EmailMessage>(
                InvalidOperationException($"fetch failed: {id}")) }

let private cancellingFeed (cancellation: CancellationTokenSource) : Algebra.EmailProvider =
    { feed with
        getFullMessage = fun _ ->
            cancellation.Cancel()
            Task.FromCanceled<Domain.EmailMessage>(cancellation.Token) }

let private queuedIds () : ChannelReader<string> =
    let input = Channel.CreateUnbounded<string>()
    failedIds |> List.iter (fun id -> Assert.True(input.Writer.TryWrite(id)))
    input.Writer.Complete()
    input.Reader

let private rejectNotification (_id: int64) (_path: string) : Task<unit> =
    Task.FromException<unit>(InvalidOperationException("Failed email unexpectedly notified intake"))

let private consume (fixture: Fixture) (source: Algebra.EmailProvider)
    (input: ChannelReader<string>) (processed: int ref) : Task<int * int> =
    EmailSync.processMessageConsumer fixture.Files.Fs fixture.Database fixture.Logger
        fixture.Clock source fixture.Config fixture.Account input rejectNotification
        processed 1 CancellationToken.None

let private assertFailures (fixture: Fixture) (errors: string list) : unit =
    let expected =
        failedIds
        |> List.map (fun id -> $"[{fixture.Account}] Email {id} was not processed: fetch failed: {id}")
    Assert.Equal<string list>(expected, errors)
    Assert.Equal<string list>(expected, fixture.Logs |> Seq.toList)

let private consumerErrors (error: exn) : string list =
    match error with
    | EmailSync.ChannelConsumerFailed errors -> errors
    | other -> failwith $"Expected ChannelConsumerFailed, got {other}"

let private assertNoStorage (fixture: Fixture) : Task<unit> =
    task {
        let! count = documentCount fixture
        Assert.Equal(0L, count)
        Assert.Empty(fixture.Files.Files)
        for id in failedIds do do! assertAcknowledged fixture id 0L
    }

let private assertCancelled (fixture: Fixture) (initial: DateTimeOffset)
    (output: Channel<Document.T>) (summary: EmailSync.SyncResult) : Task<unit> =
    task {
        Assert.Equal(0, summary.MessagesProcessed)
        Assert.Equal(0, summary.AttachmentsDownloaded)
        Assert.Equal(0, summary.DuplicatesSkipped)
        Assert.Contains<string>("Channel sync cancelled before completion", summary.Errors)
        Assert.Equal(0, output.Reader.Count)
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some initial, watermark)
        do! assertNoStorage fixture
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Public consumer drains failed messages before reporting every failure`` () =
    withFixture (fun fixture ->
        task {
            let attempts = ConcurrentQueue<string>()
            let input = queuedIds ()
            let processed = ref 0
            let operation = consume fixture (failingFeed attempts) input processed
            let! error =
                Assert.ThrowsAsync<EmailSync.ChannelConsumerFailed>(
                    Func<Task>(fun () -> operation :> Task))
            assertFailures fixture (consumerErrors error)
            Assert.Equal<string list>(failedIds, attempts |> Seq.toList)
            Assert.Equal(0, processed.Value)
            Assert.True(input.Completion.IsCompletedSuccessfully)
            do! assertNoStorage fixture
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Failed message fetches remain failures rather than duplicates or acknowledged work`` () =
    withFixture (fun fixture ->
        task {
            let attempts = ConcurrentQueue<string>()
            let initial = fixture.Clock.utcNow().AddDays(-1.0)
            do! seedWatermark fixture initial
            let! result = run fixture (failingFeed attempts) 1
            assertStorageFailure result
            assertFailures fixture result.Summary.Errors
            Assert.Equal<string list>(failedIds, attempts |> Seq.toList)
            let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
            Assert.Equal(Some initial, watermark)
            do! assertNoStorage fixture
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Empty first enumeration does not create a watermark`` () =
    withFixture (fun fixture ->
        task {
            let! result = run fixture TestHelpers.emptyProvider 1
            assertSuccess 0 0 result
            Assert.Equal(0, result.Summary.DuplicatesSkipped)
            let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
            Assert.True(watermark.IsNone)
            do! assertNoStorage fixture
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Cancellation during fetch leaves pending messages retryable without advancing state`` () =
    withFixture (fun fixture ->
        task {
            use cancellation = new CancellationTokenSource()
            let output = Channel.CreateUnbounded<Document.T>()
            let initial = fixture.Clock.utcNow().AddDays(-1.0)
            do! seedWatermark fixture initial
            let! summary =
                EmailSync.syncAccountWithChannel fixture.Files.Fs fixture.Database fixture.Logger
                    fixture.Clock (cancellingFeed cancellation) fixture.Config fixture.Account
                    output.Writer 1 cancellation.Token
            Assert.True(cancellation.IsCancellationRequested)
            do! assertCancelled fixture initial output summary
        })
