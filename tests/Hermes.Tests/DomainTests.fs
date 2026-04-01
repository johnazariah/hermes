module Hermes.Tests.DomainTests

open Xunit
open Hermes.Core

// ─── SourceType round-trip ───────────────────────────────────────────

[<Theory>]
[<InlineData("email_attachment")>]
[<InlineData("watched_folder")>]
[<InlineData("manual_drop")>]
[<Trait("Category", "Unit")>]
let ``Domain_SourceType_RoundTrip`` (s: string) =
    match Domain.SourceType.fromString s with
    | Ok st -> Assert.Equal(s, Domain.SourceType.toString st)
    | Error e -> failwith e

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Domain_SourceType_UnknownString_ReturnsError`` () =
    match Domain.SourceType.fromString "garbage" with
    | Error _ -> ()
    | Ok _ -> failwith "Expected Error"

// ─── ReminderStatus round-trip ───────────────────────────────────────

[<Theory>]
[<InlineData("active")>]
[<InlineData("snoozed")>]
[<InlineData("completed")>]
[<InlineData("dismissed")>]
[<Trait("Category", "Unit")>]
let ``Domain_ReminderStatus_RoundTrip`` (s: string) =
    let status = Domain.ReminderStatus.fromString s
    Assert.Equal(s, Domain.ReminderStatus.toString status)

// ─── ChatProviderKind round-trip ─────────────────────────────────────

[<Theory>]
[<InlineData("ollama")>]
[<InlineData("azure-openai")>]
[<Trait("Category", "Unit")>]
let ``Domain_ChatProviderKind_RoundTrip`` (s: string) =
    match Domain.ChatProviderKind.fromString s with
    | Ok kind -> Assert.Equal(s, Domain.ChatProviderKind.toString kind)
    | Error e -> failwith e

[<Theory>]
[<InlineData("azure_openai")>]
[<InlineData("azureopenai")>]
[<Trait("Category", "Unit")>]
let ``Domain_ChatProviderKind_AlternateFormats_ParseCorrectly`` (s: string) =
    match Domain.ChatProviderKind.fromString s with
    | Ok Domain.ChatProviderKind.AzureOpenAI -> ()
    | _ -> failwith "Expected AzureOpenAI"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Domain_ChatProviderKind_UnknownString_ReturnsError`` () =
    match Domain.ChatProviderKind.fromString "gpt-local" with
    | Error _ -> ()
    | Ok _ -> failwith "Expected Error"
