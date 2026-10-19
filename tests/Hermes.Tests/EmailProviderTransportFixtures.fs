module Hermes.Tests.EmailProviderTransportFixtures

open System
open System.Net
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Google.Apis.Gmail.v1
open Google.Apis.Http
open Google.Apis.Services
open Microsoft.Graph
open Hermes.Core

type Transport =
    { Provider: Algebra.EmailProvider
      Dispose: unit -> unit }

type private SyntheticHandler(send: HttpRequestMessage -> Task<HttpResponseMessage>) =
    inherit HttpMessageHandler()
    override _.SendAsync(request, _ct) = send request

let response (status: HttpStatusCode) (json: string) : HttpResponseMessage =
    let result = new HttpResponseMessage(status)
    result.Content <- new StringContent(json, Encoding.UTF8, "application/json")
    result

let ok (json: string) : Task<HttpResponseMessage> =
    response HttpStatusCode.OK json |> Task.FromResult

let failure (isGmail: bool) : Task<HttpResponseMessage> =
    let json =
        if isGmail then
            """{"error":{"code":400,"message":"synthetic transport failure"}}"""
        else
            """{"error":{"code":"BadRequest","message":"synthetic transport failure"}}"""
    response HttpStatusCode.BadRequest json |> Task.FromResult

let private gmail (logger: Algebra.Logger)
    (send: HttpRequestMessage -> Task<HttpResponseMessage>) : Task<Transport> =
    task {
        let factory =
            { new IHttpClientFactory with
                member _.CreateHttpClient(_args) =
                    new ConfigurableHttpClient(
                        new ConfigurableMessageHandler(new SyntheticHandler(send))) }
        let settings =
            BaseClientService.Initializer(
                HttpClientFactory = factory, ApplicationName = "Hermes.Tests")
        let service = new GmailService(settings)
        let! provider = GmailProvider.fromService service "synthetic" logger
        return { Provider = provider; Dispose = fun () -> service.Dispose() }
    }

let private outlook (logger: Algebra.Logger)
    (send: HttpRequestMessage -> Task<HttpResponseMessage>) : Task<Transport> =
    task {
        let http = new HttpClient(new SyntheticHandler(send))
        let client = new GraphServiceClient(http)
        let! provider = OutlookProvider.fromClient client "synthetic" logger
        return
            { Provider = provider
              Dispose = fun () -> client.Dispose(); http.Dispose() }
    }

let create (isGmail: bool) (logger: Algebra.Logger)
    (send: HttpRequestMessage -> Task<HttpResponseMessage>) : Task<Transport> =
    if isGmail then gmail logger send else outlook logger send

let emptyCollection (isGmail: bool) : string =
    if isGmail then """{"messages":[]}""" else """{"value":[]}"""

let messageJson (isGmail: bool) (id: string) (withAttachment: bool) : string =
    if isGmail then
        let parts =
            if withAttachment then
                """[{"filename":"source.pdf","mimeType":"application/pdf","body":{"attachmentId":"synthetic-attachment","size":32}}]"""
            else "[]"
        sprintf
            """{"id":"%s","threadId":"transport-thread","labelIds":[],"payload":{"mimeType":"text/plain","headers":[{"name":"From","value":"synthetic@example.test"},{"name":"Subject","value":"Synthetic transport"},{"name":"Date","value":"Sat, 1 Mar 2025 00:00:00 +0000"}],"body":{"data":"c3ludGhldGljIGJvZHk"},"parts":%s}}"""
            id parts
    else
        sprintf
            """{"id":"%s","conversationId":"transport-thread","subject":"Synthetic transport","from":{"emailAddress":{"address":"synthetic@example.test"}},"receivedDateTime":"2025-03-01T00:00:00Z","hasAttachments":true,"body":{"contentType":"text","content":"synthetic body"}}"""
            id

let emptyBodyJson (isGmail: bool) : string =
    if isGmail then
        """{"id":"empty-body","payload":{"mimeType":"text/plain","headers":[],"body":{"data":""},"parts":[]}}"""
    else
        """{"id":"empty-body","body":{"contentType":"text","content":""}}"""

let private graphContinuation (next: string option) : (string * obj) list =
    next
    |> Option.map (fun value -> "@odata.nextLink", Database.boxVal value)
    |> Option.toList

let pageJson (isGmail: bool) (ids: string list) (next: string option) : string =
    let entries = ids |> List.map (fun id -> {| id = id |})
    if isGmail then
        JsonSerializer.Serialize(
            {| messages = entries
               nextPageToken = next |> Option.toObj
               resultSizeEstimate = int64 ids.Length |})
    else
        let properties =
            ("value", Database.boxVal entries) :: graphContinuation next
            |> dict
        JsonSerializer.Serialize(properties)

let private requestUri (request: HttpRequestMessage) : Uri =
    request.RequestUri
    |> Option.ofObj
    |> Option.defaultWith (fun () ->
        invalidOp "Synthetic transport received a request without a URI")

let isPage (request: HttpRequestMessage) : bool =
    (requestUri request).AbsolutePath.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)

let isAttachment (request: HttpRequestMessage) : bool =
    (requestUri request).AbsolutePath.Contains("/attachments", StringComparison.OrdinalIgnoreCase)

let isLaterPage (request: HttpRequestMessage) : bool =
    let query = (requestUri request).Query
    query.Contains("pageToken", StringComparison.OrdinalIgnoreCase)
    || query.Contains("page=2", StringComparison.Ordinal)

let nextPage (isGmail: bool) : string =
    if isGmail then "synthetic-next"
    else "https://graph.microsoft.com/v1.0/me/messages?page=2"

let messageId (request: HttpRequestMessage) : string =
    (requestUri request).AbsolutePath.Split('/')
    |> Array.last
    |> Uri.UnescapeDataString

let successMessage (isGmail: bool) (request: HttpRequestMessage) : Task<HttpResponseMessage> =
    if isAttachment request then ok (emptyCollection isGmail)
    else ok (messageJson isGmail (messageId request) false)
