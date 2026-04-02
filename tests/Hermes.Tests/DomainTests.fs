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

// ─── ClassificationRule.describe ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ClassificationRule_Describe_DefaultRule_ContainsDefault`` () =
    let desc = Domain.ClassificationRule.describe Domain.ClassificationRule.DefaultRule
    Assert.Contains("default", desc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ClassificationRule_Describe_FilenameRule_ContainsName`` () =
    let desc = Domain.ClassificationRule.describe (Domain.ClassificationRule.FilenameRule("inv-rule", "(?i)invoice"))
    Assert.Contains("filename", desc)
    Assert.Contains("inv-rule", desc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ClassificationRule_Describe_SubjectRule_ContainsSubject`` () =
    let desc = Domain.ClassificationRule.describe (Domain.ClassificationRule.SubjectRule("sub-rule", "(?i)receipt"))
    Assert.Contains("subject", desc)
    Assert.Contains("sub-rule", desc)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ClassificationRule_Describe_DomainRule_ContainsDomain`` () =
    let desc = Domain.ClassificationRule.describe (Domain.ClassificationRule.DomainRule("dom-rule", "finance.com"))
    Assert.Contains("domain", desc)
    Assert.Contains("finance.com", desc)

// ─── Prelude.TaskResult ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``TaskResult_Map_OkValue_Transforms`` () =
    task {
        let! result = Prelude.TaskResult.map (fun x -> x * 2) (task { return Ok 21 })
        Assert.Equal(Ok 42, result)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``TaskResult_Map_Error_PreservesError`` () =
    task {
        let! result = Prelude.TaskResult.map (fun (x: int) -> x * 2) (task { return Error "fail" })
        Assert.Equal(Error "fail", result)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``TaskResult_Bind_OkToOk_Chains`` () =
    task {
        let! result = Prelude.TaskResult.bind (fun x -> task { return Ok (x * 2) }) (task { return Ok 21 })
        Assert.Equal(Ok 42, result)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``TaskResult_Bind_ErrorShortCircuits`` () =
    task {
        let! result = Prelude.TaskResult.bind (fun (x: int) -> task { return Ok (x * 2) }) (task { return Error "fail" })
        Assert.Equal(Error "fail", result)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``TaskResult_MapError_Error_Transforms`` () =
    task {
        let! result = Prelude.TaskResult.mapError (fun e -> $"wrapped: {e}") (task { return Error "inner" })
        Assert.Equal(Error "wrapped: inner", result)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``TaskResult_MapError_Ok_PreservesOk`` () =
    task {
        let! result = Prelude.TaskResult.mapError (fun (e: string) -> $"wrapped: {e}") (task { return Ok 42 })
        Assert.Equal(Ok 42, result)
    }

// ─── Prelude.foldTask ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Prelude_FoldTask_EmptyList_ReturnsInit`` () =
    task {
        let! result = Prelude.foldTask (fun s _ -> task { return s + 1 }) 0 []
        Assert.Equal(0, result)
    }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Prelude_FoldTask_AccumulatesValues`` () =
    task {
        let! result = Prelude.foldTask (fun s x -> task { return s + x }) 0 [1; 2; 3; 4]
        Assert.Equal(10, result)
    }