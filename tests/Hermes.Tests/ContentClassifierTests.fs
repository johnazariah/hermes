module Hermes.Tests.ContentClassifierTests

open Xunit
open Hermes.Core

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
