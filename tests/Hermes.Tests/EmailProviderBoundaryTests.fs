module Hermes.Tests.EmailProviderBoundaryTests

open System
open System.Net
open System.Net.Http
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailProviderTransportFixtures

let private mustFail (work: unit -> Task<'value>) : Task<unit> =
    task {
        let! error =
            Assert.ThrowsAnyAsync<Exception>(Func<Task>(fun () -> work () :> Task))
        Assert.Contains("synthetic transport failure", error.Message)
    }

let private assertFaults (provider: Algebra.EmailProvider) : Task<unit> =
    task {
        do! mustFail (fun () -> provider.listNewMessages None)
        do! mustFail (fun () -> provider.getFullMessage "transport-first")
        do! mustFail (fun () -> provider.getMessageBody "transport-first")
        do! mustFail (fun () -> provider.listStubPage None None 500)
        do! mustFail (fun () -> provider.listMessagePage None None 500)
    }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Production interpreter transport failures cannot become successful empty results`` (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let! transport = create isGmail fixture.Logger (fun _ -> failure isGmail)
            try
                do! assertFaults transport.Provider
                Assert.True(fixture.Logs.Count >= 5)
            finally
                transport.Dispose()
        })

let private attachmentFailure (isGmail: bool)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    if isAttachment request then failure isGmail
    else ok (messageJson isGmail "transport-first" true)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Production attachment download failure faults instead of returning no attachments`` (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let! transport = create isGmail fixture.Logger (attachmentFailure isGmail)
            try
                do! mustFail (fun () -> transport.Provider.getAttachments "transport-first")
                Assert.NotEmpty(fixture.Logs)
            finally
                transport.Dispose()
        })

let private assertEmpty (provider: Algebra.EmailProvider) : Task<unit> =
    task {
        let! messages = provider.listNewMessages None
        let! stubs = provider.listStubPage None None 500
        let! page = provider.listMessagePage None None 500
        Assert.Empty(messages)
        Assert.Empty(stubs.Ids)
        Assert.True(stubs.NextPageToken.IsNone)
        Assert.Empty(page.Messages)
        Assert.True(page.NextPageToken.IsNone)
    }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Successful empty provider pages remain successful`` (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let! transport = create isGmail fixture.Logger (fun _ -> ok (emptyCollection isGmail))
            try
                do! assertEmpty transport.Provider
                Assert.Empty(fixture.Logs)
            finally
                transport.Dispose()
        })

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Successful empty bodies and attachment collections remain distinguishable from failure``
    (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let send (request: HttpRequestMessage) : Task<HttpResponseMessage> =
                if isAttachment request then ok (emptyCollection isGmail)
                else ok (emptyBodyJson isGmail)
            let! transport = create isGmail fixture.Logger send
            try
                let! body = transport.Provider.getMessageBody "empty-body"
                let! attachments = transport.Provider.getAttachments "empty-body"
                Assert.True(body.IsNone)
                Assert.Empty(attachments)
            finally
                transport.Dispose()
        })

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Missing full-message responses are faults rather than fabricated messages`` (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let send (_request: HttpRequestMessage) : Task<HttpResponseMessage> =
                response HttpStatusCode.NoContent "" |> Task.FromResult
            let! transport = create isGmail fixture.Logger send
            try
                let! _ =
                    Assert.ThrowsAnyAsync<Exception>(
                        Func<Task>(fun () -> transport.Provider.getFullMessage "transport-first" :> Task))
                Assert.NotEmpty(fixture.Logs)
            finally
                transport.Dispose()
        })

let private singleAttachmentFailure (isGmail: bool)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    if isPage request then ok (pageJson isGmail [ "transport-first" ] None)
    else attachmentFailure isGmail request

let private assertRetryable (fixture: Fixture) (initial: DateTimeOffset)
    (result: RunResult) : Task<unit> =
    task {
        assertStorageFailure result
        do! assertAcknowledged fixture "transport-first" 0L
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some initial, watermark)
    }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Interpreter attachment failure leaves the email unacknowledged and watermark unchanged``
    (isGmail: bool) =
    withFixture (fun fixture ->
        task {
            let initial = fixture.Clock.utcNow().AddDays(-30.0)
            do! seedWatermark fixture initial
            let! transport = create isGmail fixture.Logger (singleAttachmentFailure isGmail)
            try
                let! result = run fixture transport.Provider 1
                do! assertRetryable fixture initial result
            finally
                transport.Dispose()
        })
