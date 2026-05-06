module Hermes.Tests.ComprehensionRacTests

#nowarn "3261"

open Xunit
open Hermes.Core

// ─── extractSenderDomain ─────────────────────────────────────────────

[<Theory>]
[<InlineData("noreply@telstra.com", "telstra.com")>]
[<InlineData("John Smith <john@example.org>", "example.org")>]
[<InlineData("support@ato.gov.au", "ato.gov.au")>]
[<Trait("Category", "Unit")>]
let ``Stages_ExtractSenderDomain_ValidEmail_ReturnsDomain`` (sender: string, expected: string) =
    let result = Stages.extractSenderDomain sender
    Assert.Equal(Some expected, result)

[<Theory>]
[<InlineData("no-email-here")>]
[<InlineData("")>]
[<InlineData("just a name")>]
[<Trait("Category", "Unit")>]
let ``Stages_ExtractSenderDomain_NoAt_ReturnsNone`` (sender: string) =
    Assert.Equal(None, Stages.extractSenderDomain sender)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_ExtractSenderDomain_AngleBrackets_StripsThem`` () =
    let result = Stages.extractSenderDomain "HR <payroll@microsoft.com>"
    Assert.Equal(Some "microsoft.com", result)

// ─── compactSchemaHint ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_ValidJson_ReturnsTypeAndFieldNames`` () =
    let json = """{"document_type":"invoice","confidence":0.9,"summary":"test","fields":{"vendor":"Telstra","amount":89.5,"date":"2026-03-15"}}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome, "Expected Some")
    let hint = result.Value
    Assert.Contains("invoice", hint)
    Assert.Contains("vendor", hint)
    Assert.Contains("amount", hint)
    Assert.Contains("date", hint)
    // Must NOT contain actual values
    Assert.DoesNotContain("Telstra", hint)
    Assert.DoesNotContain("89.5", hint)
    Assert.DoesNotContain("2026-03-15", hint)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_NoFields_ReturnsTypeOnly`` () =
    let json = """{"document_type":"letter","confidence":0.8,"summary":"A letter"}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome)
    Assert.Contains("letter", result.Value)
    Assert.Contains("field_names", result.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_InvalidJson_ReturnsNone`` () =
    Assert.Equal(None, Stages.compactSchemaHint "not json at all")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_EmptyFields_ReturnsEmptyArray`` () =
    let json = """{"document_type":"report","fields":{}}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome)
    Assert.Contains("field_names", result.Value)
    Assert.Contains("[]", result.Value)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Stages_CompactSchemaHint_CapsAt300Chars`` () =
    // Create JSON with many fields to exceed 300 chars
    let manyFields =
        [ for i in 1..50 -> $"\"very_long_field_name_{i}\":\"value\"" ]
        |> String.concat ","
    let json = $"""{{"document_type":"huge","fields":{{{manyFields}}}}}"""
    let result = Stages.compactSchemaHint json
    Assert.True(result.IsSome)
    Assert.True(result.Value.Length <= 300, $"Hint too long: {result.Value.Length}")
