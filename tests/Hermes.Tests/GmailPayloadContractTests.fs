module Hermes.Tests.GmailPayloadContractTests

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailProviderTransportFixtures

let private parsePayload (json: string) : obj | null =
    use document = JsonDocument.Parse(json)
    box (document.RootElement.Clone())

let private messageWire (payload: obj | null) : string =
    JsonSerializer.Serialize({| id = "payload-contract"; payload = payload |})

let private withGmail (handler: HttpRequestMessage -> Task<HttpResponseMessage>)
    (verify: Algebra.EmailProvider -> Task<unit>) : Task<unit> =
    task {
        let! transport = create true TestHelpers.silentLogger handler
        try
            do! verify transport.Provider
        finally
            transport.Dispose()
    }

[<Theory>]
[<InlineData("null")>]
[<InlineData("{}")>]
[<Trait("Category", "Integration")>]
let ``Gmail null and empty payloads have no readable body`` (payloadJson: string) =
    let wire = parsePayload payloadJson |> messageWire
    withGmail (fun _ -> ok wire) (fun provider ->
        task {
            let! body = provider.getMessageBody "payload-contract"
            Assert.True(body.IsNone)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Gmail skips an empty sibling before selecting nested decoded body content`` () =
    let expected = "<p>nested</p>"
    let data = Encoding.UTF8.GetBytes(expected) |> Convert.ToBase64String
    let textPart = {| mimeType = "text/html"; body = {| data = data |} |}
    let nested = box {| parts = [| textPart |] |}
    let root = box {| parts = [| parsePayload "{}"; nested |] |}
    withGmail (fun _ -> ok (messageWire root)) (fun provider ->
        task {
            let! body = provider.getMessageBody "payload-contract"
            Assert.Equal(Some expected, body)
        })

let private missingAttachmentFields =
    """{"parts":[
      {"filename":null,"body":{"attachmentId":"missing-filename"}},
      {"filename":"missing-body.pdf"},
      {"filename":"missing-id.pdf","body":{}}
    ]}"""

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Gmail attachment parts missing required fields never trigger a download`` () =
    let wire = parsePayload missingAttachmentFields |> messageWire
    let handler (request: HttpRequestMessage) =
        Assert.False(isAttachment request, "Invalid attachment metadata must not be downloaded")
        ok wire
    withGmail handler (fun provider ->
        task {
            let! attachments = provider.getAttachments "payload-contract"
            Assert.Empty(attachments)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Gmail decodes a short three-character unpadded URL-safe attachment`` () =
    let handler (request: HttpRequestMessage) =
        if isAttachment request then ok """{"data":"-_8"}"""
        else ok (messageJson true "transport-first" true)
    withGmail handler (fun provider ->
        task {
            let! attachments = provider.getAttachments "transport-first"
            let file = Assert.Single<Domain.EmailAttachment>(attachments)
            Assert.Equal<byte>([| 251uy; 255uy |], file.Content)
            Assert.Equal(2L, file.SizeBytes)
        })
