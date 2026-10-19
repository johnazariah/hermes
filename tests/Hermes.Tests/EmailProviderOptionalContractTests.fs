module Hermes.Tests.EmailProviderOptionalContractTests

open System
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailProviderTransportFixtures
open Hermes.Tests.EmailProviderSuccessFixtures

let private withProvider (isGmail: bool)
    (handler: HttpRequestMessage -> Task<HttpResponseMessage>)
    (verify: Algebra.EmailProvider -> Task<unit>) : Task<unit> =
    task {
        let! transport = create isGmail TestHelpers.silentLogger handler
        try
            do! verify transport.Provider
        finally
            transport.Dispose()
    }

let private expectedSparse (isGmail: bool) (sender: string option)
    (body: string option) : Domain.EmailMessage =
    { ProviderId = ids.Head
      ThreadId = if isGmail then "" else ids.Head
      Sender = sender
      Subject = None
      Date = None
      Labels = []
      HasAttachments = isGmail
      BodyText = body }

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Valid sparse SDK metadata preserves missing values and documented body fallback`` (isGmail: bool) =
    withProvider isGmail (sparseSend isGmail None) (fun provider ->
        task {
            let fullBody = Some (if isGmail then "" else preview)
            let! full = provider.getFullMessage ids.Head
            Assert.Equal<Domain.EmailMessage>(expectedSparse isGmail None fullBody, full)
            let! legacy = provider.listNewMessages None
            Assert.Equal<Domain.EmailMessage list>([ expectedSparse isGmail None fullBody ], legacy)
            let! body = provider.getMessageBody ids.Head
            Assert.True(body.IsNone)
            let! attachments = provider.getAttachments ids.Head
            Assert.Empty(attachments)
            let! page = provider.listMessagePage None None 1
            let pageBody = if isGmail then None else Some preview
            Assert.Equal<Domain.EmailMessage list>([ expectedSparse isGmail None pageBody ], page.Messages)
            Assert.True(page.NextPageToken.IsNone)
            Assert.Equal(1L, page.ResultSizeEstimate)
        })

[<Theory>]
[<InlineData("""{}""", "")>]
[<InlineData("""{"emailAddress":{}}""", "")>]
[<InlineData("""{"emailAddress":{"name":"","address":""}}""", "")>]
[<InlineData("""{"emailAddress":{"address":"address@example.test"}}""", "address@example.test")>]
[<InlineData("""{"emailAddress":{"name":"Name only"}}""", "Name only")>]
[<Trait("Category", "Integration")>]
let ``Outlook optional sender shapes preserve available identity without invented fields``
    (fromJson: string, expected: string) =
    withProvider false (sparseSend false (Some fromJson)) (fun provider ->
        task {
            let sender = if expected = "" then None else Some expected
            let! full = provider.getFullMessage ids.Head
            Assert.Equal<Domain.EmailMessage>(expectedSparse false sender (Some preview), full)
        })

let private assertAbsent (description: string) (work: unit -> Task<'value>) : Task<unit> =
    task {
        let! error =
            Assert.ThrowsAsync<InvalidOperationException>(Func<Task>(fun () -> work () :> Task))
        Assert.Equal<string>($"Outlook returned no {description}", error.Message)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Missing Graph collections and body responses are faults rather than empty success`` () =
    let handler (_request: HttpRequestMessage) : Task<HttpResponseMessage> =
        response HttpStatusCode.NoContent "" |> Task.FromResult
    withProvider false handler (fun provider ->
        task {
            do! assertAbsent "message collection" (fun () -> provider.listNewMessages None)
            do! assertAbsent "attachment collection" (fun () -> provider.getAttachments ids.Head)
            do! assertAbsent "body response" (fun () -> provider.getMessageBody ids.Head)
            do! assertAbsent "stub page" (fun () -> provider.listStubPage None None 1)
            do! assertAbsent "message page" (fun () -> provider.listMessagePage None None 1)
        })

let private binaryFile (name: string) (mime: string) (bytes: byte array) : Domain.EmailAttachment =
    { FileName = name
      MimeType = mime
      SizeBytes = int64 bytes.Length
      Content = bytes }

let private inlineFiles: Domain.EmailAttachment list =
    let imageMime = "image/png"
    [ binaryFile "threshold.png" imageMime (Array.create 50000 240uy)
      binaryFile "large.png" imageMime (Array.create 50001 241uy)
      binaryFile fileName mimeType binaryContent ]

let private inlinePart (file: Domain.EmailAttachment) : obj | null =
    let disposition =
        if file.FileName = fileName then "attachment"
        else "InLiNe; filename=inline.png"
    box
        {| filename = file.FileName
           mimeType = file.MimeType
           headers = [| {| name = "Content-Disposition"; value = disposition |} |]
           body = {| attachmentId = file.FileName; size = file.SizeBytes |} |}

let private inlineMetadata () : string =
    JsonSerializer.Serialize(
        {| id = ids.Head
           payload = {| mimeType = "multipart/mixed"; parts = List.map inlinePart inlineFiles |} |})

let private inlineSend (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    Assert.Equal<HttpMethod>(HttpMethod.Get, request.Method)
    if isAttachment request then
        let id = messageId request
        Assert.NotEqual<string>(inlineFiles.Head.FileName, id)
        let file = inlineFiles |> List.find (fun value -> value.FileName = id)
        let data =
            Convert.ToBase64String(file.Content).TrimEnd('=').Replace('+', '-').Replace('/', '_')
        ok (JsonSerializer.Serialize({| data = data; size = file.SizeBytes |}))
    else
        Assert.Equal<string>(ids.Head, messageId request)
        ok (inlineMetadata ())

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Gmail skips inline content at the threshold but downloads larger inline and normal files`` () =
    withProvider true inlineSend (fun provider ->
        task {
            let! attachments = provider.getAttachments ids.Head
            Assert.Equal<Domain.EmailAttachment list>(List.tail inlineFiles, attachments)
        })
