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

    /// Build a Gmail API service for the given account label.
    let private createService (configDir: string) (label: string) : Task<GmailService> =
        task {
            let credPath = Path.Combine(configDir, "gmail_credentials.json")
            let tokenDir = Path.Combine(configDir, "tokens")

            use stream = new FileStream(credPath, FileMode.Open, FileAccess.Read)
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
    let create (configDir: string) (label: string) (logger: Algebra.Logger) : Task<Algebra.EmailProvider> =
        task {
            let! service = createService configDir label

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
                            let! messages =
                                response.Messages
                                |> Seq.map (fun stub ->
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
                                    })
                                |> Task.WhenAll

                            return messages |> Array.toList
                    with ex ->
                        logger.error $"Gmail list failed for {label}: {ex.Message}"
                        return []
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
                            let! attachments =
                                msg.Payload.Parts
                                |> Seq.filter (fun p ->
                                    not (String.IsNullOrEmpty(p.Filename)) &&
                                    p.Body <> null &&
                                    not (String.IsNullOrEmpty(p.Body.AttachmentId)))
                                |> Seq.map (fun part ->
                                    task {
                                        let attReq = service.Users.Messages.Attachments.Get("me", messageId, part.Body.AttachmentId)
                                        let! attBody = attReq.ExecuteAsync()
                                        let bytes = decodeBase64Url attBody.Data

                                        return
                                            ({ FileName = part.Filename
                                               MimeType = if part.MimeType <> null then part.MimeType else "application/octet-stream"
                                               SizeBytes = int64 bytes.Length
                                               Content = bytes } : Domain.EmailAttachment)
                                    })
                                |> Task.WhenAll

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

            let provider : Algebra.EmailProvider =
                { listNewMessages = listMessages
                  getAttachments = getAtts
                  getMessageBody = getBody }
            return provider
        }
