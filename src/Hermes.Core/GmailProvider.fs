namespace Hermes.Core

open System
open System.IO
open System.Threading.Tasks
open Google.Apis.Auth.OAuth2
open Google.Apis.Gmail.v1
open Google.Apis.Gmail.v1.Data
open Google.Apis.Services
open Google.Apis.Util.Store

/// Concrete Gmail API implementation of the EmailProvider algebra.
[<RequireQualifiedAccess>]
module GmailProvider =

    /// Build a Gmail API service from pre-loaded credential bytes.
    let private createService (credentialBytes: byte array) (tokenDir: string) (label: string) : Task<GmailService> =
        task {
            use stream = new MemoryStream(credentialBytes)
            let! credential =
                GoogleWebAuthorizationBroker.AuthorizeAsync(
                    GoogleClientSecrets.FromStream(stream).Secrets,
                    [| GmailService.Scope.GmailReadonly |],
                    label,
                    Threading.CancellationToken.None,
                    FileDataStore(tokenDir, true))

            return new GmailService(
                BaseClientService.Initializer(
                    HttpClientInitializer = credential,
                    ApplicationName = "Hermes"))
        }

    let private decodeBase64Url (s: string) : byte array =
        let padded = s.Replace('-', '+').Replace('_', '/')
        let padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=')
        Convert.FromBase64String(padded)

    let private extractBodyText (payload: MessagePart) : string =
        let rec walk (part: MessagePart) =
            if System.Object.ReferenceEquals(part, null) then ""
            elif part.Body <> null && not (String.IsNullOrEmpty(part.Body.Data)) &&
                 (part.MimeType = "text/plain" || part.MimeType = "text/html") then
                let bytes = decodeBase64Url part.Body.Data
                Text.Encoding.UTF8.GetString(bytes)
            elif part.Parts <> null then
                part.Parts
                |> Seq.map walk
                |> Seq.tryFind (fun s -> s.Length > 0)
                |> Option.defaultValue ""
            else ""
        walk payload

    /// Create an EmailProvider algebra backed by the Gmail API.
    let create (credentialBytes: byte array) (tokenDir: string) (label: string) (logger: Algebra.Logger) : Task<Algebra.EmailProvider> =
        task {
            let! service = createService credentialBytes tokenDir label

            let fetchMessageFull (stub: Message) : Task<Domain.EmailMessage> =
                task {
                    let getReq = service.Users.Messages.Get("me", stub.Id)
                    getReq.Format <- UsersResource.MessagesResource.GetRequest.FormatEnum.Full |> Nullable
                    let! msg = getReq.ExecuteAsync()
                    let headers = msg.Payload.Headers |> Seq.map (fun h -> h.Name, h.Value) |> dict
                    let tryHeader key = match headers.TryGetValue(key) with true, v -> Some v | _ -> None

                    return
                        ({ ProviderId = msg.Id
                           ThreadId = if msg.ThreadId <> null then msg.ThreadId else ""
                           Sender = tryHeader "From"
                           Subject = tryHeader "Subject"
                           Date =
                             tryHeader "Date"
                             |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
                           Labels = (if msg.LabelIds <> null then msg.LabelIds |> Seq.toList else [])
                           HasAttachments = true
                           BodyText = Some (extractBodyText msg.Payload) } : Domain.EmailMessage)
                }

            let getMessageById (messageId: string) : Task<Domain.EmailMessage> =
                task {
                    let getReq = service.Users.Messages.Get("me", messageId)
                    getReq.Format <- UsersResource.MessagesResource.GetRequest.FormatEnum.Full |> Nullable
                    let! msg = getReq.ExecuteAsync()
                    let headers = msg.Payload.Headers |> Seq.map (fun h -> h.Name, h.Value) |> dict
                    let tryHeader key = match headers.TryGetValue(key) with true, v -> Some v | _ -> None

                    return
                        ({ ProviderId = msg.Id
                           ThreadId = if msg.ThreadId <> null then msg.ThreadId else ""
                           Sender = tryHeader "From"
                           Subject = tryHeader "Subject"
                           Date =
                             tryHeader "Date"
                             |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
                           Labels = (if msg.LabelIds <> null then msg.LabelIds |> Seq.toList else [])
                           HasAttachments = true
                           BodyText = Some (extractBodyText msg.Payload) } : Domain.EmailMessage)
                }

            let listMessages (sinceOpt: DateTimeOffset option) : Task<Domain.EmailMessage list> =
                task {
                    try
                        let req = service.Users.Messages.List("me")
                        req.MaxResults <- 100L |> Nullable
                        req.Q <- "has:attachment"

                        match sinceOpt with
                        | Some since ->
                            let epoch = since.ToUnixTimeSeconds()
                            req.Q <- $"has:attachment after:{epoch}"
                        | None -> ()

                        let! response = req.ExecuteAsync()

                        if response.Messages = null || response.Messages.Count = 0 then
                            return []
                        else
                            let! messages = response.Messages |> Seq.map fetchMessageFull |> Task.WhenAll
                            return messages |> Array.toList
                    with ex ->
                        logger.error $"Gmail list failed for {label}: {ex.Message}"
                        return []
                }

            let fetchAttachment (messageId: string) (part: MessagePart) : Task<Domain.EmailAttachment> =
                task {
                    let attReq = service.Users.Messages.Attachments.Get("me", messageId, part.Body.AttachmentId)
                    let! attBody = attReq.ExecuteAsync()
                    let bytes = decodeBase64Url attBody.Data

                    return
                        ({ FileName = part.Filename
                           MimeType = if part.MimeType <> null then part.MimeType else "application/octet-stream"
                           SizeBytes = int64 bytes.Length
                           Content = bytes } : Domain.EmailAttachment)
                }

            let getAtts (messageId: string) : Task<Domain.EmailAttachment list> =
                task {
                    try
                        let getReq = service.Users.Messages.Get("me", messageId)
                        getReq.Format <- UsersResource.MessagesResource.GetRequest.FormatEnum.Full |> Nullable
                        let! msg = getReq.ExecuteAsync()

                        if msg.Payload = null || msg.Payload.Parts = null then
                            return []
                        else
                            let isRealAttachment (p: MessagePart) =
                                not (String.IsNullOrEmpty(p.Filename)) &&
                                p.Body <> null &&
                                not (String.IsNullOrEmpty(p.Body.AttachmentId)) &&
                                // Skip inline images (signatures, tracking pixels, logos)
                                // unless they're large enough to be real content (>50KB)
                                (p.Headers = null ||
                                 not (p.Headers |> Seq.exists (fun h ->
                                    h.Name = "Content-Disposition" &&
                                    h.Value <> null &&
                                    h.Value.StartsWith("inline", StringComparison.OrdinalIgnoreCase))) ||
                                 (p.Body.Size.HasValue && p.Body.Size.Value > 50000))
                            let attParts = msg.Payload.Parts |> Seq.filter isRealAttachment
                            let! attachments = attParts |> Seq.map (fetchAttachment messageId) |> Task.WhenAll
                            return attachments |> Array.toList
                    with ex ->
                        logger.error $"Gmail attachments failed for {messageId}: {ex.Message}"
                        return []
                }

            let getBody (messageId: string) : Task<string option> =
                task {
                    try
                        let getReq = service.Users.Messages.Get("me", messageId)
                        getReq.Format <- UsersResource.MessagesResource.GetRequest.FormatEnum.Full |> Nullable
                        let! msg = getReq.ExecuteAsync()
                        let body = extractBodyText msg.Payload
                        return if String.IsNullOrWhiteSpace(body) then None else Some body
                    with _ ->
                        return None
                }

            let fetchMessageMetadata (stub: Message) : Task<Domain.EmailMessage> =
                task {
                    let getReq = service.Users.Messages.Get("me", stub.Id)
                    getReq.Format <- UsersResource.MessagesResource.GetRequest.FormatEnum.Metadata |> Nullable
                    let! msg = getReq.ExecuteAsync()
                    let headers = msg.Payload.Headers |> Seq.map (fun h -> h.Name, h.Value) |> dict
                    let tryHeader key = match headers.TryGetValue(key) with true, v -> Some v | _ -> None

                    return
                        ({ ProviderId = msg.Id
                           ThreadId = if msg.ThreadId <> null then msg.ThreadId else ""
                           Sender = tryHeader "From"
                           Subject = tryHeader "Subject"
                           Date =
                             tryHeader "Date"
                             |> Option.bind (fun s -> match DateTimeOffset.TryParse(s) with true, d -> Some d | _ -> None)
                           Labels = (if msg.LabelIds <> null then msg.LabelIds |> Seq.toList else [])
                           HasAttachments = true
                           BodyText = None } : Domain.EmailMessage)
                }

            let listStubs (pageToken: string option) (query: string option) (maxResults: int) : Task<Algebra.StubPage> =
                task {
                    try
                        let req = service.Users.Messages.List("me")
                        req.MaxResults <- int64 maxResults |> Nullable

                        match query with
                        | Some q -> req.Q <- q
                        | None -> req.Q <- "has:attachment"

                        match pageToken with
                        | Some t -> req.PageToken <- t
                        | None -> ()

                        let! response = req.ExecuteAsync()

                        if response.Messages = null || response.Messages.Count = 0 then
                            return ({ Ids = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.StubPage)
                        else
                            let ids = response.Messages |> Seq.map (fun m -> m.Id) |> Seq.toList

                            let nextToken =
                                match response.NextPageToken with
                                | null -> None
                                | t -> Some t

                            return
                                { Ids = ids
                                  NextPageToken = nextToken
                                  ResultSizeEstimate = response.ResultSizeEstimate |> Option.ofNullable |> Option.map int64 |> Option.defaultValue 0L }
                    with ex ->
                        logger.error $"Gmail listStubs failed for {label}: {ex.Message}"
                        return ({ Ids = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.StubPage)
                }

            let listPage (pageToken: string option) (query: string option) (maxResults: int) : Task<Algebra.MessagePage> =
                task {
                    try
                        let req = service.Users.Messages.List("me")
                        req.MaxResults <- int64 maxResults |> Nullable

                        match query with
                        | Some q -> req.Q <- q
                        | None -> req.Q <- "has:attachment"

                        match pageToken with
                        | Some t -> req.PageToken <- t
                        | None -> ()

                        let! response = req.ExecuteAsync()

                        if response.Messages = null || response.Messages.Count = 0 then
                            return ({ Messages = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.MessagePage)
                        else
                            let! messages = response.Messages |> Seq.map fetchMessageMetadata |> Task.WhenAll

                            let nextToken =
                                match response.NextPageToken with
                                | null -> None
                                | t -> Some t

                            let result : Algebra.MessagePage =
                                { Messages = messages |> Array.toList
                                  NextPageToken = nextToken
                                  ResultSizeEstimate = response.ResultSizeEstimate |> Option.ofNullable |> Option.map int64 |> Option.defaultValue 0L }
                            return result
                    with ex ->
                        logger.error $"Gmail listPage failed for {label}: {ex.Message}"
                        return ({ Messages = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.MessagePage)
                }

            let provider : Algebra.EmailProvider =
                { listNewMessages = listMessages
                  getAttachments = getAtts
                  getMessageBody = getBody
                  getFullMessage = getMessageById
                  listStubPage = listStubs
                  listMessagePage = listPage }
            return provider
        }
