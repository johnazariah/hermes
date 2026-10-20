/// Synthetic field-preservation checks through Hermes' real extractors.
/// Historical case names identify requirements, not external parser equivalence.
module Hermes.Tests.OspreyParityTests

open System
open System.Text
open Xunit
open Hermes.Tests.SyntheticFixtures

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O1_MicrosoftPayslip_ExtractsRequiredFields`` () =
    let result = FinancialPdf.payslip () |> FinancialAssertions.pdf
    FinancialAssertions.contains
        [ "- **Employee:** SYNTHETIC-Alex-7000001"
          "- **PayDate:** 15/07/2025"
          "- **EarningCode:** 101" ] result
    FinancialAssertions.table [ "Item"; "Current"; "YTD" ]
        [ [ "Salary"; "$5000.00"; "$20000.00" ]
          [ "Gross"; "$5000.00"; "$20000.00" ]
          [ "PAYG"; "$1250.00"; "$5000.00" ]
          [ "Net"; "$3750.00"; "$15000.00" ]
          [ "Super"; "$600.00"; "$2400.00" ] ] result
    FinancialAssertions.confidence 0.5 result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O2_QldEducationPayslip_ExtractsRequiredFields`` () =
    let result = FinancialPdf.educationPayslip () |> FinancialAssertions.pdf
    FinancialAssertions.contains
        [ "- **Employee:** SYNTHETIC-Blair-7000002"
          "- **PayDate:** 29/07/2025" ] result
    FinancialAssertions.table [ "Code"; "Description"; "Amount"; "YTD" ]
        [ [ "101"; "Salary"; "$3000.00"; "$9000.00" ]
          [ "205"; "Allowance"; "$200.00"; "$600.00" ]
          [ "TOTAL"; "GROSS"; "$3200.00"; "$9600.00" ]
          [ "PAYG"; "Tax"; "$700.00"; "$2100.00" ]
          [ "NET"; "Pay"; "$2500.00"; "$7500.00" ]
          [ "SUPER"; "SGC"; "$384.00"; "$1152.00" ] ] result
    FinancialAssertions.confidence 0.5 result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O3_WestpacCsv_ExtractsRequiredFields`` () =
    let result = FinancialCsv.bankDebitCredit |> FinancialAssertions.csv
    FinancialAssertions.table [ "Date"; "Narrative"; "Debit Amount"; "Credit Amount"; "Balance" ]
        [ [ "01/08/2025"; "SYNTHETIC Rent, Example"; "650.00"; ""; "4350.00" ]
          [ "02/08/2025"; "SYNTHETIC Salary"; ""; "3750.00"; "8100.00" ] ] result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O4_CbaCsv_ExtractsRequiredFields`` () =
    let result = FinancialCsv.bankSignedAmount |> FinancialAssertions.csv
    FinancialAssertions.table [ "Date"; "Amount"; "Description"; "Balance" ]
        [ [ "03/08/2025"; "-42.80"; "SYNTHETIC Grocer, Example"; "8057.20" ]
          [ "04/08/2025"; "100.00"; "SYNTHETIC Refund"; "8157.20" ] ] result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O5_RentalStatement_ExtractsRequiredFields`` () =
    let result = FinancialPdf.rentalStatement () |> FinancialAssertions.pdf
    FinancialAssertions.contains
        [ "- **Statement:** RENT-SYN-001"; "- **Property:** 10-Synthetic-Lane" ] result
    FinancialAssertions.table [ "Date"; "Description"; "Amount" ]
        [ [ "01/08/2025"; "Rent"; "$650.00" ]
          [ "15/08/2025"; "Rent"; "$650.00" ]
          [ "31/08/2025"; "Fee"; "-$50.00" ]
          [ "31/08/2025"; "Net"; "$1250.00" ] ] result
    FinancialAssertions.confidence 0.3 result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O6_FidelityCsv_ExtractsRequiredFields`` () =
    let result = FinancialCsv.dividends |> FinancialAssertions.csv
    FinancialAssertions.table
        [ "Date"; "Fund"; "Transaction Type"; "Units"; "Unit Price"; "Amount"; "Franking Credit" ]
        [ [ "15/09/2025"; "SYNTHETIC-FUND-A"; "Dividend"; "100.0000"; "1.2500"; "125.00"; "53.57" ]
          [ "30/09/2025"; "SYNTHETIC-FUND-B"; "Dividend"; "100.0000"; "0.7500"; "75.00"; "0.00" ] ] result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O7_AmazonCsv_ExtractsRequiredFields`` () =
    let result = FinancialCsv.orders |> FinancialAssertions.csv
    FinancialAssertions.table
        [ "Order ID"; "Order Date"; "Product Name"; "Quantity"; "Unit Price"; "Total"; "Currency" ]
        [ [ "SYN-ORDER-001"; "2025-10-01"; "SYNTHETIC Notebook, A5"; "2"; "12.50"; "25.00"; "AUD" ]
          [ "SYN-ORDER-002"; "2025-10-02"; "SYNTHETIC Cable"; "1"; "20.00"; "20.00"; "AUD" ] ] result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O8_UtilityInvoice_ExtractsRequiredFields`` () =
    let result = FinancialPdf.utilityInvoice () |> FinancialAssertions.pdf
    FinancialAssertions.contains
        [ "- **Invoice:** UTIL-SYN-001"
          "- **Issued:** 01/08/25"
          "- **Due:** 15/08/25"
          "- **Total:** $132.40"
          "- **Usage:** 400kWh" ] result
    FinancialAssertions.confidence 0.3 result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O9_CreditCardCsv_ExtractsRequiredFields`` () =
    let result = FinancialCsv.creditCard |> FinancialAssertions.csv
    FinancialAssertions.table
        [ "Transaction Date"; "Post Date"; "Description"; "Debit"; "Credit"; "Currency" ]
        [ [ "03/08/2025"; "04/08/2025"; "SYNTHETIC Grocer, Example"; "42.80"; ""; "AUD" ]
          [ "05/08/2025"; "06/08/2025"; "SYNTHETIC Payment"; ""; "100.00"; "AUD" ] ] result

[<Fact>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_O10_InsuranceRenewal_ExtractsRequiredFields`` () =
    let result = FinancialPdf.insuranceRenewal () |> FinancialAssertions.pdf
    FinancialAssertions.contains
        [ "- **Policy:** INS-SYN-001"
          "- **Insured:** SYNTHETIC-Casey-Example"
          "- **Start:** 01/10/2025"
          "- **End:** 30/09/2026" ] result
    FinancialAssertions.table [ "Component"; "Amount"; "Currency" ]
        [ [ "Premium"; "$900.00"; "AUD" ]
          [ "GST"; "$90.00"; "AUD" ]
          [ "Total"; "$990.00"; "AUD" ] ] result
    FinancialAssertions.confidence 0.5 result

[<Theory>]
[<InlineData("")>]
[<InlineData("SYNTHETIC, but not a PDF")>]
[<Trait("Category", "Integration")>]
let ``OspreyParity_PdfFixture_MissingOrMalformed_FailsExplicitly`` (source: string) =
    let error =
        Assert.Throws<InvalidOperationException>(fun () ->
            source |> Encoding.ASCII.GetBytes |> FinancialAssertions.pdf |> ignore)
    Assert.Equal("SYNTHETIC PDF fixture has no extractable pages", error.Message)
