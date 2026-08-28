module Hermes.Tests.OutlookProviderTests

#nowarn "3261"

open System
open System.Text
open Xunit
open Hermes.Core
open Microsoft.Graph.Models

// ─── Sidecar backwards compatibility ─────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_ProviderIdField_ParsesProviderId`` () =
    let json = """{
  "source_type": "email_attachment",
  "account": "work",
  "provider_id": "abc",
  "original_name": "doc.pdf",
  "sha256": "aaa"
}"""
    match Classifier.parseSidecar json with
    | Ok meta -> Assert.Equal("abc", meta.ProviderId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_LegacyGmailId_FallsBackToProviderId`` () =
    let json = """{
  "source_type": "email_attachment",
  "account": "personal",
  "gmail_id": "xyz",
  "original_name": "invoice.pdf",
  "sha256": "bbb"
}"""
    match Classifier.parseSidecar json with
    | Ok meta -> Assert.Equal("xyz", meta.ProviderId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_BothProviderIdAndGmailId_ProviderIdWins`` () =
    let json = """{
  "source_type": "email_attachment",
  "account": "dual",
  "provider_id": "new-id",
  "gmail_id": "old-id",
  "original_name": "report.pdf",
  "sha256": "ccc"
}"""
    match Classifier.parseSidecar json with
    | Ok meta -> Assert.Equal("new-id", meta.ProviderId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

// ─── Config parsing: Outlook account fields ──────────────────────────

let private testEnv =
    TestHelpers.fakeEnvironment "/home/test" "/home/test/.config/hermes" "/home/test/Documents"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_OutlookAccount_ParsesClientAndTenant`` () =
    let yaml = """
accounts:
  - label: work-outlook
    provider: outlook
    client_id: my-client-id
    tenant_id: my-tenant
    redirect_port: 8080
"""
    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(1, config.Accounts.Length)
        let acct = config.Accounts.[0]
        Assert.Equal("outlook", acct.Provider)
        Assert.Equal("my-client-id", acct.ClientId)
        Assert.Equal("my-tenant", acct.TenantId)
        Assert.Equal(8080, acct.RedirectPort)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_OutlookAccount_DefaultTenantId`` () =
    let yaml = """
accounts:
  - label: work-outlook
    provider: outlook
    client_id: some-client
"""
    match Config.parseYaml testEnv yaml with
    | Ok config ->
        let acct = config.Accounts.[0]
        Assert.Equal("common", acct.TenantId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_OutlookAccount_DefaultRedirectPort`` () =
    let yaml = """
accounts:
  - label: work-outlook
    provider: outlook
    client_id: some-client
"""
    match Config.parseYaml testEnv yaml with
    | Ok config ->
        let acct = config.Accounts.[0]
        Assert.Equal(53682, acct.RedirectPort)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

// ─── Extraction dispatch: PPTX file detection ───────────────────────

[<Theory>]
[<InlineData("report.pptx", true)>]
[<InlineData("report.PPTX", true)>]
[<InlineData("SLIDES.Pptx", true)>]
[<InlineData("report.pdf", false)>]
[<InlineData("report.docx", false)>]
[<InlineData("report.xlsx", false)>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsPptx_DetectsCorrectly`` (path: string, expected: bool) =
    Assert.Equal(expected, Extraction.isPptx path)

// ─── Gmail provider pure helpers ──────────────────────────────────────

module GmailProviderCoverage =

    let private encodeBase64Url (text: string) =
        text
        |> Encoding.UTF8.GetBytes
        |> Convert.ToBase64String
        |> fun encoded ->
            encoded.TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private gmailPart mimeType text =
        let body = Google.Apis.Gmail.v1.Data.MessagePartBody()
        body.Data <- encodeBase64Url text
        let part = Google.Apis.Gmail.v1.Data.MessagePart()
        part.MimeType <- mimeType
        part.Body <- body
        part

    let private gmailContainer
        (parts: Google.Apis.Gmail.v1.Data.MessagePart list)
        =
        let container = Google.Apis.Gmail.v1.Data.MessagePart()
        container.Parts <- ResizeArray parts
        container

    let private gmailAttachment filename attachmentId size disposition =
        let body = Google.Apis.Gmail.v1.Data.MessagePartBody()
        body.AttachmentId <- attachmentId
        size |> Option.iter (fun value -> body.Size <- Nullable value)

        let part = Google.Apis.Gmail.v1.Data.MessagePart()
        part.Filename <- filename
        part.Body <- body

        disposition
        |> Option.iter (fun value ->
            let header = Google.Apis.Gmail.v1.Data.MessagePartHeader()
            header.Name <- "Content-Disposition"
            header.Value <- value
            part.Headers <- ResizeArray [ header ])

        part

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_DecodeBase64Url_UnpaddedUrlCharacters_ReturnsBytes`` () =
        let decoded = GmailProvider.decodeBase64Url "-_8"

        Assert.Equal<byte array>([| 251uy; 255uy |], decoded)
        Assert.Equal("f", GmailProvider.decodeBase64Url "Zg" |> Encoding.UTF8.GetString)
        Assert.Equal("fo", GmailProvider.decodeBase64Url "Zm8" |> Encoding.UTF8.GetString)

    [<Theory>]
    [<InlineData("text/plain", "plain body")>]
    [<InlineData("text/html", "<p>HTML body</p>")>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_ExtractBodyText_TextPart_ReturnsDecodedContent``
        (mimeType: string, expected: string)
        =
        let actual =
            gmailPart mimeType expected
            |> GmailProvider.extractBodyText

        Assert.Equal(expected, actual)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_ExtractBodyText_NestedParts_ReturnsFirstNonEmptyBody`` () =
        let nested =
            gmailPart "text/html" "<p>nested</p>"
            |> List.singleton
            |> gmailContainer

        let root =
            [ Google.Apis.Gmail.v1.Data.MessagePart(); nested ]
            |> gmailContainer

        Assert.Equal("<p>nested</p>", GmailProvider.extractBodyText root)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_ExtractBodyText_EmptyOrNullPayload_ReturnsEmpty`` () =
        let empty = Google.Apis.Gmail.v1.Data.MessagePart()

        Assert.Equal("", GmailProvider.extractBodyText empty)
        Assert.Equal("", GmailProvider.extractBodyText null)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_IsRealAttachment_MissingRequiredFields_ReturnsFalse`` () =
        let missingFilename = gmailAttachment null "attachment-id" None None
        let missingId = gmailAttachment "report.pdf" null None None
        let missingBody = Google.Apis.Gmail.v1.Data.MessagePart()
        missingBody.Filename <- "report.pdf"

        Assert.False(GmailProvider.isRealAttachment missingFilename)
        Assert.False(GmailProvider.isRealAttachment missingId)
        Assert.False(GmailProvider.isRealAttachment missingBody)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_IsRealAttachment_NormalAttachment_ReturnsTrue`` () =
        let attachment = gmailAttachment "report.pdf" "attachment-id" None None

        Assert.True(GmailProvider.isRealAttachment attachment)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_IsRealAttachment_CaseInsensitiveSmallInline_ReturnsFalse`` () =
        let attachment =
            gmailAttachment
                "logo.png"
                "attachment-id"
                (Some 50000)
                (Some "InLiNe; filename=logo.png")

        Assert.False(GmailProvider.isRealAttachment attachment)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``GmailProvider_IsRealAttachment_LargeInline_ReturnsTrue`` () =
        let attachment =
            gmailAttachment
                "diagram.png"
                "attachment-id"
                (Some 50001)
                (Some "inline; filename=diagram.png")

        Assert.True(GmailProvider.isRealAttachment attachment)

// ─── Outlook provider pure mapping helpers ───────────────────────────

module OutlookProviderCoverage =

    let private messageWithSender name address =
        let emailAddress = EmailAddress()
        emailAddress.Name <- name
        emailAddress.Address <- address
        let recipient = Recipient()
        recipient.EmailAddress <- emailAddress
        let message = Message()
        message.From <- recipient
        message

    let private messageWithBody content contentType =
        let body = ItemBody()
        body.Content <- content
        body.ContentType <- Nullable contentType
        let message = Message()
        message.Body <- body
        message

    let private completePreviewMessage receivedAt =
        let message = messageWithSender "Ada" "ada@example.com"
        message.Id <- "message-id"
        message.ConversationId <- "thread-id"
        message.Subject <- "Subject"
        message.BodyPreview <- "Preview"
        message.ReceivedDateTime <- Nullable receivedAt
        message.HasAttachments <- Nullable true
        message.Categories <- ResizeArray [ "work"; "important" ]
        message

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_FormatSender_AllCombinations_FormatsAvailableValues`` () =
        let missingEmailAddress = Message()
        missingEmailAddress.From <- Recipient()

        let actual =
            [ OutlookProvider.formatSender (Message())
              OutlookProvider.formatSender missingEmailAddress
              OutlookProvider.formatSender (messageWithSender "" "")
              OutlookProvider.formatSender (messageWithSender "Ada" "")
              OutlookProvider.formatSender (messageWithSender "" "ada@example.com")
              OutlookProvider.formatSender (messageWithSender "Ada" "ada@example.com") ]

        let expected =
            [ None
              None
              None
              Some "Ada"
              Some "ada@example.com"
              Some "Ada <ada@example.com>" ]

        Assert.Equal<string option list>(expected, actual)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_ToLabels_CategoriesAndFlagged_AppendsFlaggedLabel`` () =
        let message = Message()
        message.Categories <- ResizeArray [ "work"; "blue" ]
        let flag = FollowupFlag()
        flag.FlagStatus <- Nullable FollowupFlagStatus.Flagged
        message.Flag <- flag

        Assert.Equal<string list>(
            [ "work"; "blue"; "flagged" ],
            OutlookProvider.toLabels message)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_ToLabels_NullOrUnflagged_ReturnsNoFlaggedLabel`` () =
        let unflagged = Message()
        let flag = FollowupFlag()
        flag.FlagStatus <- Nullable FollowupFlagStatus.NotFlagged
        unflagged.Flag <- flag

        Assert.Empty(OutlookProvider.toLabels (Message()))
        Assert.Empty(OutlookProvider.toLabels unflagged)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_ParseDate_NullableValues_ReturnsCorrespondingOption`` () =
        let receivedAt =
            DateTimeOffset(2026, 8, 28, 3, 4, 5, TimeSpan.Zero)

        Assert.Equal(None, OutlookProvider.parseDate (Nullable<DateTimeOffset>()))
        Assert.Equal(Some receivedAt, OutlookProvider.parseDate (Nullable receivedAt))

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_MapToPreview_PopulatedMessage_MapsMetadataAndPreview`` () =
        let receivedAt =
            DateTimeOffset(2026, 8, 28, 3, 4, 5, TimeSpan.Zero)

        let mapped = completePreviewMessage receivedAt |> OutlookProvider.mapToPreview

        Assert.Equal("message-id", mapped.ProviderId)
        Assert.Equal("thread-id", mapped.ThreadId)
        Assert.Equal(Some "Ada <ada@example.com>", mapped.Sender)
        Assert.Equal(Some "Subject", mapped.Subject)
        Assert.Equal(Some receivedAt, mapped.Date)
        Assert.Equal<string list>([ "work"; "important" ], mapped.Labels)
        Assert.True(mapped.HasAttachments)
        Assert.Equal(Some "Preview", mapped.BodyText)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_MapToPreview_EmptyOrNullPreview_ReturnsNoBody`` () =
        let whitespace = Message()
        whitespace.BodyPreview <- "   "

        Assert.Equal(None, (OutlookProvider.mapToPreview (Message())).BodyText)
        Assert.Equal(None, (OutlookProvider.mapToPreview whitespace).BodyText)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_MapToFull_HtmlOrTextBody_ReturnsReadableContent`` () =
        let html = messageWithBody "<p>Hello <b>world</b></p>" BodyType.Html
        let text = messageWithBody "Plain body" BodyType.Text

        Assert.Equal(Some "Hello world", (OutlookProvider.mapToFull html).BodyText)
        Assert.Equal(Some "Plain body", (OutlookProvider.mapToFull text).BodyText)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_MapToFull_EmptyNullOrFallbackBody_ReturnsExpectedOption`` () =
        let empty = messageWithBody "   " BodyType.Text
        let nullContent = messageWithBody null BodyType.Text
        let fallback = Message()
        fallback.BodyPreview <- "Fallback preview"

        Assert.Equal(None, (OutlookProvider.mapToFull empty).BodyText)
        Assert.Equal(None, (OutlookProvider.mapToFull nullContent).BodyText)
        Assert.Equal(None, (OutlookProvider.mapToFull (Message())).BodyText)
        Assert.Equal(Some "Fallback preview", (OutlookProvider.mapToFull fallback).BodyText)

    [<Fact>]
    [<Trait("Category", "Unit")>]
    let ``OutlookProvider_MapToPreview_MissingConversationOrIds_UsesFallbacks`` () =
        let withId = Message()
        withId.Id <- "message-id"
        let withoutIds = Message()

        let mappedWithId = OutlookProvider.mapToPreview withId
        let mappedWithoutIds = OutlookProvider.mapToPreview withoutIds

        Assert.Equal("message-id", mappedWithId.ProviderId)
        Assert.Equal("message-id", mappedWithId.ThreadId)
        Assert.Equal("", mappedWithoutIds.ProviderId)
        Assert.Equal("", mappedWithoutIds.ThreadId)
