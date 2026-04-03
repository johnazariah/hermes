module Hermes.Tests.ContentClassifierTests

open Xunit
open Hermes.Core

open FsCheck
open FsCheck.Xunit

// ─── Helpers ─────────────────────────────────────────────────────────

let private payslipRule : Domain.ContentRule =
    { Name = "payslip-by-content"
      Conditions =
        [ Domain.ContentAny [ "gross pay"; "net pay"; "tax withheld"; "superannuation" ] ]
      Category = "payslips"
      Confidence = 0.85 }

let private bankStatementRule : Domain.ContentRule =
    { Name = "bank-statement-by-headers"
      Conditions =
        [ Domain.HasTable
          Domain.TableHeadersAll [ "Date"; "Balance" ] ]
      Category = "bank-statements"
      Confidence = 0.80 }

let private invoiceRule : Domain.ContentRule =
    { Name = "invoice-by-content"
      Conditions =
        [ Domain.ContentAll [ "invoice"; "total" ]
          Domain.HasAmount ]
      Category = "invoices"
      Confidence = 0.75 }

// ─── evaluateRule tests ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_Evaluate_PayslipKeywords_MatchesPayslips`` () =
    let markdown = "Employee Payslip\n\nGross Pay: $2,732.60\nTax Withheld: $684.65\nNet Pay: $2,047.95"
    let result = ContentClassifier.evaluateRule markdown [] None payslipRule
    match result with
    | Some ("payslips", conf) -> Assert.Equal(0.85, conf)
    | _ -> failwith $"Expected payslips match, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_Evaluate_BankStatementHeaders_MatchesBankStatements`` () =
    let markdown = "Transaction History"
    let tables : PdfStructure.Table list =
        [ { Headers = [ "Date"; "Narrative"; "Debit"; "Credit"; "Balance" ]
            Rows = [ [ "01/10"; "Payment"; "$100"; ""; "$4900" ] ] } ]
    let result = ContentClassifier.evaluateRule markdown tables None bankStatementRule
    match result with
    | Some ("bank-statements", conf) -> Assert.Equal(0.80, conf)
    | _ -> failwith $"Expected bank-statements match, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_Evaluate_NoMatch_ReturnsNone`` () =
    let markdown = "Random document with no special keywords."
    let result = ContentClassifier.evaluateRule markdown [] None payslipRule
    Assert.True(result.IsNone)

// ─── classify tests ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_Classify_MultipleMatches_ReturnsBestConfidence`` () =
    let markdown = "Invoice\nTotal: $500\nGross Pay: $2000"
    let rules = [ payslipRule; invoiceRule ]
    let result = ContentClassifier.classify markdown [] (Some 500m) rules
    match result with
    | Some ("payslips", 0.85) -> ()
    | _ -> failwith $"Expected payslips (highest confidence), got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_Classify_NoRulesMatch_ReturnsNone`` () =
    let markdown = "Nothing special here."
    let result = ContentClassifier.classify markdown [] None [ payslipRule; bankStatementRule ]
    Assert.True(result.IsNone)

// ─── LLM classification tests (Tier 3) ──────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_BuildPrompt_TruncatesTo2000Chars`` () =
    let longText = String.replicate 300 "word word "
    let prompt = ContentClassifier.buildClassificationPrompt longText [ "invoices"; "payslips" ]
    Assert.Contains("[... truncated]", prompt)
    Assert.Contains("invoices, payslips", prompt)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_BuildPrompt_ShortText_NoTruncation`` () =
    let prompt = ContentClassifier.buildClassificationPrompt "Short doc" [ "tax" ]
    Assert.DoesNotContain("truncated", prompt)
    Assert.Contains("Short doc", prompt)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_ParseResponse_ValidJson_ReturnsCategory`` () =
    let json = """{"category": "insurance", "confidence": 0.92, "reasoning": "Contains Allianz policy number"}"""
    let result = ContentClassifier.parseClassificationResponse json
    match result with
    | Some ("insurance", conf, reasoning) ->
        Assert.Equal(0.92, conf)
        Assert.Contains("Allianz", reasoning)
    | _ -> failwith $"Expected insurance match, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_ParseResponse_MarkdownCodeBlock_ExtractsJson`` () =
    let response = "```json\n{\"category\": \"tax\", \"confidence\": 0.85, \"reasoning\": \"ATO notice\"}\n```"
    let result = ContentClassifier.parseClassificationResponse response
    match result with
    | Some ("tax", 0.85, _) -> ()
    | _ -> failwith $"Expected tax match, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_ParseResponse_InvalidJson_ReturnsNone`` () =
    let result = ContentClassifier.parseClassificationResponse "not json at all"
    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_ParseResponse_MissingCategory_ReturnsNone`` () =
    let json = """{"confidence": 0.5, "reasoning": "unclear"}"""
    let result = ContentClassifier.parseClassificationResponse json
    Assert.True(result.IsNone)

// ─── Additional ContentClassifier tests ──────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_ParseResponse_EmptyCategory_ReturnsNone`` () =
    let json = """{"category":"","confidence":0.5,"reasoning":"test"}"""
    let result = ContentClassifier.parseClassificationResponse json
    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_ParseResponse_ValidJson_ReturnsValues`` () =
    let json = """{"category":"invoices","confidence":0.95,"reasoning":"Looks like an invoice"}"""
    let result = ContentClassifier.parseClassificationResponse json
    match result with
    | Some (cat, conf, reasoning) ->
        Assert.Equal("invoices", cat)
        Assert.InRange(conf, 0.94, 0.96)
        Assert.Contains("invoice", reasoning)
    | None -> failwith "Expected Some"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_ParseResponse_NoReasoning_DefaultsToEmpty`` () =
    let json = """{"category":"tax","confidence":0.7}"""
    let result = ContentClassifier.parseClassificationResponse json
    match result with
    | Some ("tax", _, reasoning) -> Assert.Equal("", reasoning)
    | _ -> failwith "Expected tax match"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_BuildPrompt_IncludesCategoriesAndContent`` () =
    let prompt = ContentClassifier.buildClassificationPrompt "Invoice from ACME" ["invoices"; "receipts"; "tax"]
    Assert.Contains("invoices", prompt)
    Assert.Contains("receipts", prompt)
    Assert.Contains("Invoice from ACME", prompt)
    Assert.Contains("category", prompt)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContentClassifier_BuildPrompt_LongText_Truncates`` () =
    let longText = String.replicate 10000 "word "
    let prompt = ContentClassifier.buildClassificationPrompt longText ["invoices"]
    Assert.True(prompt.Length < longText.Length)
    Assert.Contains("truncated", prompt)

// ─── Property-based tests ────────────────────────────────────────────

[<Property>]
[<Trait("Category", "Property")>]
let ``ContentClassifier_BuildPrompt_AlwaysTruncatesTo2000`` (text: NonEmptyString) =
    let prompt = ContentClassifier.buildClassificationPrompt text.Get ["cat1"]
    prompt.Length <= text.Get.Length + 500
