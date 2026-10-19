module Hermes.Tests.EmailProviderPaginationTests

open System
open System.Net.Http
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailProviderTransportFixtures

type private Gates =
    { FirstCommitted: TaskCompletionSource<unit>
      PageFailed: TaskCompletionSource<unit> }

let private signal () : TaskCompletionSource<unit> =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

let private ids: string list = [ "transport-first"; "transport-second" ]

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Graph page wire format omits absent continuation and serializes present continuation`` () =
    let terminal = pageJson false [] None
    Assert.DoesNotContain("\"@odata.nextLink\"", terminal)
    use continuing =
        System.Text.Json.JsonDocument.Parse(
            pageJson false [] (Some (nextPage false)))
    let continuation =
        continuing.RootElement.GetProperty("@odata.nextLink").GetString()
        |> Option.ofObj
        |> Option.defaultWith (fun () -> failwith "Graph continuation must be a JSON string")
    Assert.Equal<string>(nextPage false, continuation)

let private laterFailure (isGmail: bool) (gates: Gates) : Task<HttpResponseMessage> =
    task {
        do! gates.FirstCommitted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
        gates.PageFailed.TrySetResult(()) |> ignore
        return! failure isGmail
    }

let private messageResponse (isGmail: bool) (gates: Gates)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    task {
        if messageId request = ids[1] then
            do! gates.PageFailed.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
        return! successMessage isGmail request
    }

let private failingPages (isGmail: bool) (gates: Gates)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    if isLaterPage request then laterFailure isGmail gates
    elif isPage request then ok (pageJson isGmail ids (Some (nextPage isGmail)))
    else messageResponse isGmail gates request

let private retryPages (isGmail: bool) (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    if isPage request then ok (pageJson isGmail ids None)
    else successMessage isGmail request

let rec private drain (reader: ChannelReader<Document.T>)
    (collected: Document.T list) : Task<Document.T list> =
    task {
        let! available = reader.WaitToReadAsync().AsTask()
        if available then
            let! document = reader.ReadAsync().AsTask()
            return! drain reader (document :: collected)
        else
            return List.rev collected
    }

let private start (fixture: Fixture) (provider: Algebra.EmailProvider)
    (output: Channel<Document.T>) (ct: CancellationToken) : Task<EmailSync.SyncResult> =
    EmailSync.syncAccountWithChannel
        fixture.Files.Fs fixture.Database fixture.Logger fixture.Clock provider
        fixture.Config fixture.Account output.Writer 1 ct

let private collectFailure (fixture: Fixture) (provider: Algebra.EmailProvider)
    (gates: Gates) : Task<RunResult> =
    task {
        use cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20.0))
        let output = Channel.CreateUnbounded<Document.T>()
        let completion = start fixture provider output cancellation.Token
        let! first = output.Reader.ReadAsync(cancellation.Token).AsTask()
        do! assertRegistered fixture first
        gates.FirstCommitted.TrySetResult(()) |> ignore
        let! summary = completion.WaitAsync(cancellation.Token)
        Assert.True(output.Writer.TryComplete())
        let! documents = drain output.Reader [ first ]
        return { Summary = summary; Documents = documents }
    }

let private assertFailure (fixture: Fixture) (initial: DateTimeOffset)
    (result: RunResult) : Task<unit> =
    task {
        Assert.Equal(2, result.Summary.MessagesProcessed)
        Assert.Equal(2, result.Summary.AttachmentsDownloaded)
        Assert.Equal(2, result.Documents.Length)
        Assert.Equal(0, result.Summary.DuplicatesSkipped)
        let error = Assert.Single<string>(result.Summary.Errors)
        Assert.Contains("enumeration failed", error)
        Assert.Contains("synthetic transport failure", error)
        for document in result.Documents do do! assertRegistered fixture document
        for id in ids do do! assertAcknowledged fixture id 1L
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some initial, watermark)
    }

let private failedAttempt (fixture: Fixture) (isGmail: bool)
    (initial: DateTimeOffset) : Task<RunResult> =
    task {
        let gates = { FirstCommitted = signal (); PageFailed = signal () }
        let! transport = create isGmail fixture.Logger (failingPages isGmail gates)
        try
            let! result = collectFailure fixture transport.Provider gates
            do! assertFailure fixture initial result
            return result
        finally
            transport.Dispose()
    }

let private retry (fixture: Fixture) (isGmail: bool) (previous: RunResult) : Task<unit> =
    task {
        let before = snapshot fixture
        let! transport = create isGmail fixture.Logger (retryPages isGmail)
        try
            let! result = run fixture transport.Provider 1
            assertSuccess 0 0 result
            Assert.Equal(2, result.Summary.DuplicatesSkipped)
            Assert.Equal<Map<string, string>>(before, snapshot fixture)
            for document in previous.Documents do do! assertRegistered fixture document
        finally
            transport.Dispose()
    }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Production later-page failure drains queued work retains watermark and retries idempotently``
    (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let initial = fixture.Clock.utcNow().AddDays(-30.0)
            do! seedWatermark fixture initial
            let! previous = failedAttempt fixture isGmail initial
            do! retry fixture isGmail previous
        })

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Successful empty intermediate pages retain their continuation`` (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let json = pageJson isGmail [] (Some (nextPage isGmail))
            let! transport = create isGmail fixture.Logger (fun _ -> ok json)
            try
                let! page = transport.Provider.listStubPage None None 500
                Assert.Empty(page.Ids)
                Assert.Equal(Some (nextPage isGmail), page.NextPageToken)
            finally
                transport.Dispose()
        })
