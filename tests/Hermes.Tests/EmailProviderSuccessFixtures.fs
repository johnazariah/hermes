module Hermes.Tests.EmailProviderSuccessFixtures

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Xunit
open Hermes.Tests.EmailProviderTransportFixtures

type Listing =
    | SingleMessage
    | TwoPages

let ids: string list =
    [ "sdk-success-shared-prefix-0123456789abcdef-first"
      "sdk-success-shared-prefix-0123456789abcdef-second"
      "sdk-success-shared-prefix-0123456789abcdef-third" ]

let sender = "Ada Example <ada@example.test>"
let sentAt = DateTimeOffset(2025, 3, 1, 10, 20, 30, TimeSpan.FromHours(2.0))
let subject (id: string) : string = $"Synthetic evidence {id}"
let threadId (id: string) : string = $"synthetic-thread-{id}"
let preview = "Preview only; the complete evidence is in the body."
let fileName = "evidence.bin"
let mimeType = "application/octet-stream"
let binaryContent: byte array = [| 251uy; 255uy; 254uy; 0uy; 128uy; 13uy; 10uy |]

let plainBody =
    "Complete synthetic body: "
    + String.replicate 16 "Budget evidence, not a preview. "
    + "END-OF-BODY"

let htmlBody = $"<html><body><p>{plainBody}</p><p>Approved &amp; filed.</p></body></html>"
let htmlText = plainBody + " Approved & filed."

let private json (properties: (string * (obj | null)) list) : string =
    JsonSerializer.Serialize(dict properties)

let private base64Url (bytes: byte array) : string =
    Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private textPart (mime: string) (text: string) : obj | null =
    box {| mimeType = mime; body = {| data = Encoding.UTF8.GetBytes(text) |> base64Url |} |}

let private gmailMessage (htmlFirst: bool) (id: string) : string =
    let plain = textPart "text/plain" plainBody
    let html = textPart "text/html" htmlBody
    let alternatives = if htmlFirst then [| html; plain |] else [| plain; html |]
    let parts =
        [| box {| mimeType = "image/png"; body = {| data = "AA==" |} |}
           box {| mimeType = "multipart/alternative"; parts = alternatives |}
           box {| filename = fileName; mimeType = mimeType
                  body = {| attachmentId = "binary-attachment"; size = binaryContent.Length |} |} |]
    let headers =
        [| {| name = "From"; value = sender |}
           {| name = "Subject"; value = subject id |}
           {| name = "Date"; value = "Sat, 01 Mar 2025 10:20:30 +0200" |} |]
    JsonSerializer.Serialize(
        {| id = id; threadId = threadId id; labelIds = [| "INBOX"; "Contract" |]
           snippet = preview
           payload = {| mimeType = "multipart/mixed"; headers = headers; parts = parts |} |})

let private graphProperties (id: string) : (string * (obj | null)) list =
    [ "id", box id
      "conversationId", box (threadId id)
      "subject", box (subject id)
      "from", box {| emailAddress = {| name = "Ada Example"; address = "ada@example.test" |} |}
      "receivedDateTime", box "2025-03-01T10:20:30+02:00"
      "hasAttachments", box true
      "bodyPreview", box preview
      "categories", box [| "Contract" |]
      "flag", box {| flagStatus = "flagged" |} ]

let private graphMessage (htmlFirst: bool) (id: string) : string =
    let body =
        {| contentType = if htmlFirst then "html" else "text"
           content = if htmlFirst then htmlBody else plainBody |}
    ("body", box body) :: graphProperties id |> json

let private attachmentJson (isGmail: bool) : string =
    if isGmail then
        // Both URL-safe substitutions, with the two trailing padding characters omitted.
        JsonSerializer.Serialize({| data = "-__-AIANCg"; size = binaryContent.Length |})
    else
        let file =
            dict
                [ "@odata.type", box "#microsoft.graph.fileAttachment"
                  "id", box "binary-attachment"; "name", box fileName
                  "contentType", box mimeType; "size", box binaryContent.Length
                  "contentBytes", box (Convert.ToBase64String(binaryContent)) ]
        let item =
            dict
                [ "@odata.type", box "#microsoft.graph.itemAttachment"
                  "id", box "embedded-message"; "name", box "Forwarded message" ]
        json [ "value", box [ file; item ] ]

let private pageWire (isGmail: bool) (pageIds: string list)
    (next: string option) (total: int64) : string =
    let entries =
        pageIds
        |> List.map (fun id ->
            if isGmail then dict [ "id", box id ] else dict (graphProperties id))
    let continuation =
        next
        |> Option.map (fun token ->
            (if isGmail then "nextPageToken" else "@odata.nextLink"), box token)
        |> Option.toList
    let estimate = if isGmail then [ "resultSizeEstimate", box total ] else []
    [ (if isGmail then "messages" else "value"), box entries ]
    @ continuation @ estimate |> json

let private pageResponse (isGmail: bool) (listing: Listing)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    match listing with
    | SingleMessage ->
        Assert.False(isLaterPage request)
        ok (pageWire isGmail [ ids.Head ] None 1L)
    | TwoPages ->
        if isLaterPage request then ok (pageWire isGmail (List.skip 2 ids) None 3L)
        else ok (pageWire isGmail (List.take 2 ids) (Some (nextPage isGmail)) 3L)

let private attachmentResponse (isGmail: bool) (uri: Uri) : Task<HttpResponseMessage> =
    let suffix =
        if isGmail then $"/messages/{ids.Head}/attachments/binary-attachment"
        else $"/messages/{ids.Head}/attachments"
    Assert.EndsWith(suffix, uri.AbsolutePath)
    ok (attachmentJson isGmail)

let private messageResponse (isGmail: bool) (htmlFirst: bool)
    (uri: Uri) (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    let id = messageId request
    Assert.Contains(id, ids)
    Assert.EndsWith($"/messages/{id}", uri.AbsolutePath)
    ok (if isGmail then gmailMessage htmlFirst id else graphMessage htmlFirst id)

let send (isGmail: bool) (htmlFirst: bool) (listing: Listing)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    let uri =
        request.RequestUri
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Synthetic SDK request omitted its URI")
    Assert.Equal<HttpMethod>(HttpMethod.Get, request.Method)
    if isPage request then pageResponse isGmail listing request
    elif isAttachment request then attachmentResponse isGmail uri
    else messageResponse isGmail htmlFirst uri request

let private jsonElement (value: string) : JsonElement =
    use document = JsonDocument.Parse(value)
    document.RootElement.Clone()

let sparseMessage (isGmail: bool) (fromJson: string option) : string =
    if isGmail then
        let headers: {| name: string; value: string |} array = [||]
        JsonSerializer.Serialize(
            {| id = ids.Head
               payload = {| mimeType = "text/plain"; headers = headers |} |})
    else
        let optionalSender =
            fromJson
            |> Option.map (fun value ->
                "from", box (jsonElement value))
            |> Option.toList
        [ "id", box ids.Head
          "bodyPreview", box preview
          "flag", box {| flagStatus = "notFlagged" |} ]
        @ optionalSender |> json

let sparseSend (isGmail: bool) (fromJson: string option)
    (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    Assert.Equal<HttpMethod>(HttpMethod.Get, request.Method)
    let message = sparseMessage isGmail fromJson
    if isPage request then
        let entry =
            if isGmail then box {| id = ids.Head |}
            else box (jsonElement message)
        let estimate = if isGmail then [ "resultSizeEstimate", box 1L ] else []
        ok (json ([ (if isGmail then "messages" else "value"), box [ entry ] ] @ estimate))
    elif isAttachment request then
        Assert.False(isGmail, "Gmail must not download attachments when parts are absent")
        ok (emptyCollection false)
    else
        Assert.Equal<string>(ids.Head, messageId request)
        ok message
