module Hermes.Tests.EmailProviderSuccessTests

open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailProviderTransportFixtures
open Hermes.Tests.EmailProviderSuccessFixtures

let private withTransport (isGmail: bool) (htmlFirst: bool) (listing: Listing)
    (verify: Fixture -> Algebra.EmailProvider -> Task<unit>) : Task<unit> =
    withFixture (fun fixture ->
        task {
            let! transport = create isGmail fixture.Logger (send isGmail htmlFirst listing)
            try
                do! verify fixture transport.Provider
            finally
                transport.Dispose()
        })

let private expectedMessage (isGmail: bool) (body: string option)
    (id: string) : Domain.EmailMessage =
    { ProviderId = id
      ThreadId = threadId id
      Sender = Some sender
      Subject = Some (subject id)
      Date = Some sentAt
      Labels = if isGmail then [ "INBOX"; "Contract" ] else [ "Contract"; "flagged" ]
      HasAttachments = true
      BodyText = body }

let private assertMessages (isGmail: bool) (body: string option)
    (expectedIds: string list) (actual: Domain.EmailMessage list) : unit =
    let expected = expectedIds |> List.map (expectedMessage isGmail body)
    Assert.Equal<Domain.EmailMessage list>(expected, actual)

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Non-empty legacy SDK lists preserve complete messages without following continuation``
    (isGmail: bool) =
    withTransport isGmail false TwoPages (fun fixture provider ->
        task {
            let! messages = provider.listNewMessages (Some (sentAt.AddDays(-1.0)))
            let body = Some (if isGmail then plainBody else preview)
            assertMessages isGmail body (List.take 2 ids) messages
            Assert.Empty(fixture.Logs)
        })

let private assertPage (isGmail: bool) (expectedIds: string list)
    (next: string option) (page: Algebra.MessagePage) : unit =
    assertMessages isGmail (if isGmail then None else Some preview) expectedIds page.Messages
    Assert.Equal(next, page.NextPageToken)
    Assert.Equal((if isGmail then 3L else int64 expectedIds.Length), page.ResultSizeEstimate)

let private verifyPages (isGmail: bool) (provider: Algebra.EmailProvider) : Task<unit> =
    task {
        let! first = provider.listMessagePage None (Some "Contract") 2
        let! last = provider.listMessagePage first.NextPageToken (Some "Contract") 2
        assertPage isGmail (List.take 2 ids) (Some (nextPage isGmail)) first
        assertPage isGmail (List.skip 2 ids) None last
        let! stubs = provider.listStubPage None (Some "Contract") 2
        let! finalStubs = provider.listStubPage stubs.NextPageToken (Some "Contract") 2
        Assert.Equal<string list>(List.take 2 ids, stubs.Ids)
        Assert.Equal(Some (nextPage isGmail), stubs.NextPageToken)
        Assert.Equal<string list>(List.skip 2 ids, finalStubs.Ids)
        Assert.True(finalStubs.NextPageToken.IsNone)
        Assert.Equal((if isGmail then 3L else 2L), stubs.ResultSizeEstimate)
        Assert.Equal((if isGmail then 3L else 1L), finalStubs.ResultSizeEstimate)
    }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Non-empty SDK pages preserve metadata full IDs and terminal continuation`` (isGmail: bool) =
    withTransport isGmail false TwoPages (fun fixture provider ->
        task {
            do! verifyPages isGmail provider
            Assert.Empty(fixture.Logs)
        })

[<Theory>]
[<InlineData(true, false)>]
[<InlineData(true, true)>]
[<InlineData(false, false)>]
[<InlineData(false, true)>]
[<Trait("Category", "Integration")>]
let ``Full SDK body reads preserve plain and HTML contracts instead of previews``
    (isGmail: bool, htmlFirst: bool) =
    withTransport isGmail htmlFirst SingleMessage (fun fixture provider ->
        task {
            let expected = Some (if not htmlFirst then plainBody elif isGmail then htmlBody else htmlText)
            let! full = provider.getFullMessage ids.Head
            assertMessages isGmail expected [ ids.Head ] [ full ]
            let! body = provider.getMessageBody ids.Head
            Assert.Equal(expected, body)
            Assert.Empty(fixture.Logs)
        })

let private assertDownloaded (attachment: Domain.EmailAttachment) : unit =
    Assert.Equal<string>(fileName, attachment.FileName)
    Assert.Equal<string>(mimeType, attachment.MimeType)
    Assert.Equal(int64 binaryContent.Length, attachment.SizeBytes)
    Assert.Equal<byte>(binaryContent, attachment.Content)

let private bodyBytes () : byte array =
    let markdown =
        $"# {subject ids.Head}\n\n**From:** {sender}  \n**Date:** 2025-03-01\n\n---\n\n{htmlText}"
    Encoding.UTF8.GetBytes(markdown)

let private assertProvenance (fixture: Fixture) (document: Document.T) : unit =
    Assert.Equal<string>(fixture.Account, required "account" document)
    Assert.Equal<string>(ids.Head, required "gmail_id" document)
    Assert.Equal<string>(threadId ids.Head, required "thread_id" document)
    Assert.Equal<string>(sender, required "sender" document)
    Assert.Equal<string>(subject ids.Head, required "subject" document)
    Assert.Equal<string>(sentAt.ToString("o"), required "email_date" document)

let private assertStored (fixture: Fixture) (expected: byte array)
    (document: Document.T) : Task<unit> =
    task {
        do! assertRegistered fixture document
        let! actual =
            fixture.Files.Fs.readAllBytes (fullPath fixture (required "saved_path" document))
        Assert.Equal<byte>(expected, actual)
        Assert.Equal<string>(SourceFiles.hash expected, required "sha256" document)
        Assert.Equal(Some (int64 expected.Length), Document.decode<int64> "size_bytes" document)
    }

let private assertIngested (fixture: Fixture) (result: RunResult) : Task<unit> =
    task {
        assertSuccess 1 2 result
        Assert.Equal(0, result.Summary.DuplicatesSkipped)
        let body = source "email_body" result
        let file = source "email_attachment" result
        Assert.NotEqual(Document.id body, Document.id file)
        do! assertStored fixture (bodyBytes ()) body
        do! assertStored fixture binaryContent file
        Assert.Equal<string>(fileName, required "original_name" file)
        Assert.Equal<string>(mimeType, required "mime_type" file)
        Assert.Equal<string>("text/markdown", required "mime_type" body)
        for document in result.Documents do assertProvenance fixture document
        do! assertAcknowledged fixture ids.Head 1L
    }

let private assertRetry (fixture: Fixture) (provider: Algebra.EmailProvider) : Task<unit> =
    task {
        let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
        Assert.Equal(Some (fixture.Clock.utcNow()), watermark)
        let before = snapshot fixture
        let! repeated = run fixture provider 1
        assertSuccess 0 0 repeated
        Assert.Equal(1, repeated.Summary.DuplicatesSkipped)
        Assert.Equal<Map<string, string>>(before, snapshot fixture)
        let! count = documentCount fixture
        Assert.Equal(2L, count)
        do! assertAcknowledged fixture ids.Head 1L
    }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Successful SDK binary attachments map exactly and ingest with the full body`` (isGmail: bool) =
    withTransport isGmail true SingleMessage (fun fixture provider ->
        task {
            let! attachments = provider.getAttachments ids.Head
            assertDownloaded (Assert.Single<Domain.EmailAttachment>(attachments))
            let! result = run fixture provider 1
            do! assertIngested fixture result
            do! assertRetry fixture provider
            Assert.Empty(fixture.Logs)
        })
