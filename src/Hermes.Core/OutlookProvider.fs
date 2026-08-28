namespace Hermes.Core

open System
open System.Net.Http
open System.Threading.Tasks
open Azure.Identity
open Microsoft.Graph
open Microsoft.Graph.Models

/// Concrete Microsoft Graph implementation of the EmailProvider algebra.
[<RequireQualifiedAccess>]
module OutlookProvider =

    // ─── Immutable-ID middleware ─────────────────────────────────────

    /// DelegatingHandler that adds the Prefer header for immutable IDs
    /// on every outgoing request. Without this, Graph message IDs change
    /// when messages move between folders.
    type private ImmutableIdHandler() =
        inherit DelegatingHandler()

        override this.SendAsync(request, ct) =
            request.Headers.TryAddWithoutValidation(
                "Prefer", """IdType="ImmutableId" """) |> ignore
            base.SendAsync(request, ct)

    // ─── Scopes ──────────────────────────────────────────────────────

    let private scopes =
        [| "Mail.Read"; "User.Read" |]

    // ─── Mapping helpers ─────────────────────────────────────────────

    let internal formatSender (msg: Message) =
        match msg.From with
        | null -> None
        | from ->
            match from.EmailAddress with
            | null -> None
            | ea ->
                let name = ea.Name |> Option.ofObj |> Option.defaultValue ""
                let addr = ea.Address |> Option.ofObj |> Option.defaultValue ""
                match name, addr with
                | "", "" -> None
                | "", a -> Some a
                | n, "" -> Some n
                | n, a -> Some $"{n} <{a}>"

    let internal toLabels (msg: Message) =
        let categories =
            match msg.Categories with
            | null -> []
            | cats -> cats |> Seq.toList

        let flagLabel =
            match msg.Flag with
            | null -> []
            | flag ->
                if flag.FlagStatus.HasValue && flag.FlagStatus.Value = FollowupFlagStatus.Flagged then
                    [ "flagged" ]
                else []

        categories @ flagLabel

    let internal parseDate (dto: Nullable<DateTimeOffset>) =
        if dto.HasValue then Some dto.Value else None

    let private mapToEmailMessage (bodyText: string option) (msg: Message) : Domain.EmailMessage =
        { ProviderId = msg.Id |> Option.ofObj |> Option.defaultValue ""
          ThreadId =
            msg.ConversationId
            |> Option.ofObj
            |> Option.defaultValue (msg.Id |> Option.ofObj |> Option.defaultValue "")
          Sender = formatSender msg
          Subject = msg.Subject |> Option.ofObj
          Date = msg.ReceivedDateTime |> parseDate
          Labels = toLabels msg
          HasAttachments =
            msg.HasAttachments
            |> Option.ofNullable
            |> Option.defaultValue false
          BodyText = bodyText }

    let internal mapToPreview (msg: Message) : Domain.EmailMessage =
        msg.BodyPreview
        |> Option.ofObj
        |> Option.bind (fun s -> if String.IsNullOrWhiteSpace(s) then None else Some s)
        |> fun body -> mapToEmailMessage body msg

    let private extractFullBody (msg: Message) : string option =
        match msg.Body with
        | null -> msg.BodyPreview |> Option.ofObj
        | body ->
            body.Content
            |> Option.ofObj
            |> Option.map (fun c ->
                if body.ContentType.HasValue && body.ContentType.Value = BodyType.Html then
                    EmailSync.stripHtml c
                else c)
            |> Option.bind (fun s -> if String.IsNullOrWhiteSpace(s) then None else Some s)

    let internal mapToFull (msg: Message) : Domain.EmailMessage =
        extractFullBody msg |> fun body -> mapToEmailMessage body msg

    // ─── Select fields ──────────────────────────────────────────────

    let private metadataSelect =
        [| "id"; "conversationId"; "subject"; "bodyPreview"
           "from"; "receivedDateTime"; "hasAttachments"; "categories"; "flag" |]

    let private fullMessageSelect =
        [| "id"; "conversationId"; "subject"; "bodyPreview"; "body"
           "from"; "receivedDateTime"; "hasAttachments"; "categories"; "flag" |]

    let private idOnlySelect = [| "id" |]

    // ─── Graph client factory ────────────────────────────────────────

    let private createClient
        (clientId: string)
        (tenantId: string)
        (redirectPort: int)
        : GraphServiceClient =

        let opts = InteractiveBrowserCredentialOptions()
        opts.TenantId <- (if String.IsNullOrWhiteSpace(tenantId) then "common" else tenantId)
        opts.ClientId <- clientId
        opts.RedirectUri <- Uri $"http://localhost:{redirectPort}/oauth/callback"
        opts.TokenCachePersistenceOptions <- TokenCachePersistenceOptions(Name = "hermes-outlook")

        let credential = InteractiveBrowserCredential(opts)

        let handler = new ImmutableIdHandler(InnerHandler = new HttpClientHandler())
        let httpClient = new HttpClient(handler)
        new GraphServiceClient(httpClient, credential, scopes)

    // ─── Provider factory ────────────────────────────────────────────

    /// Create an EmailProvider algebra backed by the Microsoft Graph API.
    let create
        (clientId: string)
        (tenantId: string)
        (redirectPort: int)
        (_tokenDir: string)
        (label: string)
        (logger: Algebra.Logger)
        : Task<Algebra.EmailProvider> =
        task {
            let client = createClient clientId tenantId redirectPort

            // ── listNewMessages ──────────────────────────────────────

            let listMessages (sinceOpt: DateTimeOffset option) : Task<Domain.EmailMessage list> =
                task {
                    try
                        let filterClause =
                            sinceOpt
                            |> Option.map (fun since ->
                                let isoDate = since.ToUniversalTime().ToString("o")
                                $"receivedDateTime ge {isoDate}")

                        let! response =
                            client.Me.Messages.GetAsync(fun cfg ->
                                cfg.QueryParameters.Select <- metadataSelect
                                cfg.QueryParameters.Orderby <- [| "receivedDateTime desc" |]
                                cfg.QueryParameters.Top <- Nullable 100

                                filterClause
                                |> Option.iter (fun f -> cfg.QueryParameters.Filter <- f))

                        match response with
                        | null -> return []
                        | r ->
                            return
                                r.Value
                                |> Option.ofObj
                                |> Option.map (Seq.map mapToPreview >> Seq.toList)
                                |> Option.defaultValue []
                    with ex ->
                        logger.error $"Outlook listMessages failed for {label}: {ex.Message}"
                        return []
                }

            // ── getAttachments ───────────────────────────────────────

            let getAtts (messageId: string) : Task<Domain.EmailAttachment list> =
                task {
                    try
                        let! response =
                            client.Me.Messages.[messageId].Attachments.GetAsync()

                        match response with
                        | null -> return []
                        | r ->
                            return
                                r.Value
                                |> Option.ofObj
                                |> Option.defaultValue (System.Collections.Generic.List<_>())
                                |> Seq.choose (fun att ->
                                    match att with
                                    | :? FileAttachment as fa ->
                                        Some
                                            ({ FileName =
                                                fa.Name
                                                |> Option.ofObj
                                                |> Option.defaultValue "attachment"
                                               MimeType =
                                                fa.ContentType
                                                |> Option.ofObj
                                                |> Option.defaultValue "application/octet-stream"
                                               SizeBytes =
                                                fa.Size
                                                |> Option.ofNullable
                                                |> Option.map int64
                                                |> Option.defaultValue 0L
                                               Content =
                                                fa.ContentBytes
                                                |> Option.ofObj
                                                |> Option.defaultValue Array.empty }
                                              : Domain.EmailAttachment)
                                    | other ->
                                        let typeName =
                                            other.OdataType
                                            |> Option.ofObj
                                            |> Option.defaultValue "unknown"
                                        logger.debug $"Outlook skipping attachment type {typeName} on {messageId}"
                                        None)
                                |> Seq.toList
                    with ex ->
                        logger.error $"Outlook attachments failed for {messageId}: {ex.Message}"
                        return []
                }

            // ── getMessageBody ───────────────────────────────────────

            let getBody (messageId: string) : Task<string option> =
                task {
                    try
                        let! msg =
                            client.Me.Messages.[messageId].GetAsync(fun cfg ->
                                cfg.QueryParameters.Select <- [| "body" |])

                        match msg with
                        | null -> return None
                        | m ->
                            match m.Body with
                            | null -> return None
                            | body ->
                                let content = body.Content |> Option.ofObj
                                return
                                    content
                                    |> Option.map (fun c ->
                                        if body.ContentType.HasValue && body.ContentType.Value = BodyType.Html then
                                            EmailSync.stripHtml c
                                        else c)
                                    |> Option.bind (fun s ->
                                        if String.IsNullOrWhiteSpace(s) then None else Some s)
                    with ex ->
                        logger.error $"Outlook getBody failed for {messageId}: {ex.Message}"
                        return None
                }

            // ── getFullMessage ────────────────────────────────────────

            let getFullMessage (messageId: string) : Task<Domain.EmailMessage> =
                task {
                    try
                        let! msg =
                            client.Me.Messages.[messageId].GetAsync(fun cfg ->
                                cfg.QueryParameters.Select <- fullMessageSelect)

                        return
                            match msg with
                            | null -> mapToFull (new Message()) : Domain.EmailMessage
                            | m -> mapToFull m : Domain.EmailMessage
                    with ex ->
                        logger.error $"Outlook getFullMessage failed for {messageId}: {ex.Message}"

                        return
                            { ProviderId = messageId
                              ThreadId = messageId
                              Sender = None
                              Subject = None
                              Date = None
                              Labels = []
                              HasAttachments = false
                              BodyText = None }
                }

            // ── listStubPage ─────────────────────────────────────────

            let listStubs
                (pageToken: string option)
                (query: string option)
                (maxResults: int)
                : Task<Algebra.StubPage> =
                task {
                    try
                        // For nextLink pagination, use a raw HTTP request
                        let! response =
                            match pageToken with
                            | Some nextLink ->
                                client.Me.Messages.WithUrl(nextLink).GetAsync(fun cfg ->
                                    cfg.QueryParameters.Select <- idOnlySelect)
                            | None ->
                                let searchTerm = query |> Option.map (fun q -> sprintf "\"%s\"" q)
                                client.Me.Messages.GetAsync(fun cfg ->
                                    cfg.QueryParameters.Select <- idOnlySelect
                                    cfg.QueryParameters.Orderby <- [| "receivedDateTime desc" |]
                                    cfg.QueryParameters.Top <- Nullable maxResults

                                    searchTerm
                                    |> Option.iter (fun s -> cfg.QueryParameters.Search <- s))

                        match response with
                        | null ->
                            return ({ Ids = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.StubPage)
                        | r ->
                            let ids =
                                r.Value
                                |> Option.ofObj
                                |> Option.map (Seq.choose (fun m -> m.Id |> Option.ofObj) >> Seq.toList)
                                |> Option.defaultValue []

                            let nextToken = r.OdataNextLink |> Option.ofObj

                            return
                                ({ Ids = ids
                                   NextPageToken = nextToken
                                   ResultSizeEstimate = ids |> List.length |> int64 } : Algebra.StubPage)
                    with ex ->
                        logger.error $"Outlook listStubs failed for {label}: {ex.Message}"
                        return ({ Ids = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.StubPage)
                }

            // ── listMessagePage ──────────────────────────────────────

            let listPage
                (pageToken: string option)
                (query: string option)
                (maxResults: int)
                : Task<Algebra.MessagePage> =
                task {
                    try
                        let! response =
                            match pageToken with
                            | Some nextLink ->
                                client.Me.Messages.WithUrl(nextLink).GetAsync(fun cfg ->
                                    cfg.QueryParameters.Select <- metadataSelect)
                            | None ->
                                let searchTerm = query |> Option.map (fun q -> sprintf "\"%s\"" q)
                                client.Me.Messages.GetAsync(fun cfg ->
                                    cfg.QueryParameters.Select <- metadataSelect
                                    cfg.QueryParameters.Orderby <- [| "receivedDateTime desc" |]
                                    cfg.QueryParameters.Top <- Nullable maxResults

                                    searchTerm
                                    |> Option.iter (fun s -> cfg.QueryParameters.Search <- s))

                        match response with
                        | null ->
                            return ({ Messages = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.MessagePage)
                        | r ->
                            let messages =
                                r.Value
                                |> Option.ofObj
                                |> Option.map (Seq.map mapToPreview >> Seq.toList)
                                |> Option.defaultValue []

                            let nextToken = r.OdataNextLink |> Option.ofObj

                            return
                                ({ Messages = messages
                                   NextPageToken = nextToken
                                   ResultSizeEstimate = messages |> List.length |> int64 } : Algebra.MessagePage)
                    with ex ->
                        logger.error $"Outlook listPage failed for {label}: {ex.Message}"
                        return ({ Messages = []; NextPageToken = None; ResultSizeEstimate = 0L } : Algebra.MessagePage)
                }

            // ── Wire up the algebra record ───────────────────────────

            let provider : Algebra.EmailProvider =
                { listNewMessages = listMessages
                  getAttachments = getAtts
                  getMessageBody = getBody
                  getFullMessage = getFullMessage
                  listStubPage = listStubs
                  listMessagePage = listPage }

            return provider
        }
