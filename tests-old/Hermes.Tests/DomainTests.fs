module Hermes.Tests.DomainTests

#nowarn "3261"
#nowarn "3264"

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

// ─── Prelude.RowReader ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptString_Present_ReturnsSome`` () =
    let row = Map.ofList [("name", box "hello")]
    let reader = Prelude.RowReader(row)
    Assert.Equal(Some "hello", reader.OptString "name")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptString_Missing_ReturnsNone`` () =
    let row = Map.ofList [("other", box "value")]
    let reader = Prelude.RowReader(row)
    Assert.True((reader.OptString "name").IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptString_DBNull_ReturnsNone`` () =
    let row = Map.ofList [("name", box System.DBNull.Value)]
    let reader = Prelude.RowReader(row)
    Assert.True((reader.OptString "name").IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptInt64_Present_ReturnsSome`` () =
    let row = Map.ofList [("id", box 42L)]
    let reader = Prelude.RowReader(row)
    Assert.Equal(Some 42L, reader.OptInt64 "id")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptInt64_Missing_ReturnsNone`` () =
    let row = Map.empty<string, obj>
    let reader = Prelude.RowReader(row)
    Assert.True((reader.OptInt64 "id").IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptInt64_FromInt_Converts`` () =
    let row = Map.ofList [("id", box 42)]
    let reader = Prelude.RowReader(row)
    Assert.Equal(Some 42L, reader.OptInt64 "id")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_String_Present_ReturnsValue`` () =
    let row = Map.ofList [("name", box "hello")]
    let reader = Prelude.RowReader(row)
    Assert.Equal("hello", reader.String "name" "default")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_String_Missing_ReturnsFallback`` () =
    let row = Map.empty<string, obj>
    let reader = Prelude.RowReader(row)
    Assert.Equal("fallback", reader.String "name" "fallback")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_Int64_Present_ReturnsValue`` () =
    let row = Map.ofList [("id", box 99L)]
    let reader = Prelude.RowReader(row)
    Assert.Equal(99L, reader.Int64 "id" 0L)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_Int64_FromInt_Converts`` () =
    let row = Map.ofList [("id", box 42)]
    let reader = Prelude.RowReader(row)
    Assert.Equal(42L, reader.Int64 "id" 0L)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_Float_Present_ReturnsValue`` () =
    let row = Map.ofList [("score", box 3.14)]
    let reader = Prelude.RowReader(row)
    Assert.Equal(3.14, reader.Float "score" 0.0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_Float_Missing_ReturnsFallback`` () =
    let row = Map.empty<string, obj>
    let reader = Prelude.RowReader(row)
    Assert.Equal(1.0, reader.Float "score" 1.0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptFloat_Present_ReturnsSome`` () =
    let row = Map.ofList [("amount", box 3.14)]
    let reader = Prelude.RowReader(row)
    Assert.True((reader.OptFloat "amount").IsSome)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptFloat_FromInt64_Converts`` () =
    let row = Map.ofList [("score", box 5L)]
    let reader = Prelude.RowReader(row)
    Assert.Equal(Some 5.0, reader.OptFloat "score")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptDateTimeOffset_ValidString_ReturnsSome`` () =
    let row = Map.ofList [("ts", box "2025-03-15T10:30:00Z")]
    let reader = Prelude.RowReader(row)
    let result = reader.OptDateTimeOffset "ts"
    Assert.True(result.IsSome)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_OptDateTimeOffset_Invalid_ReturnsNone`` () =
    let row = Map.ofList [("ts", box "not-a-date")]
    let reader = Prelude.RowReader(row)
    Assert.True((reader.OptDateTimeOffset "ts").IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``RowReader_Raw_ReturnsOriginalMap`` () =
    let row = Map.ofList [("a", box "1"); ("b", box "2")]
    let reader = Prelude.RowReader(row)
    Assert.Equal(2, reader.Raw.Count)