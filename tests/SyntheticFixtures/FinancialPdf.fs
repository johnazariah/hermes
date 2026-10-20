/// Generated, fictional financial shapes. No external forms or financial records.
module Hermes.Tests.SyntheticFixtures.FinancialPdf

open UglyToad.PdfPig.Content
open UglyToad.PdfPig.Core
open UglyToad.PdfPig.Fonts.Standard14Fonts
open UglyToad.PdfPig.Writer

type private PdfLine =
    { Y: float
      Cells: string list }

let private line y cells = { Y = y; Cells = cells }

let private table top rows =
    rows |> List.mapi (fun index cells -> line (top - float index * 20.0) cells)

let private build (columns: float list) (lines: PdfLine list) =
    use builder = new PdfDocumentBuilder()
    let font = builder.AddStandard14Font(Standard14Font.Courier)
    let page = builder.AddPage(PageSize.A4)
    lines
    |> List.iter (fun row ->
        row.Cells
        |> List.iteri (fun column text ->
            page.AddText(text, 10.0, PdfPoint(columns.[column], row.Y), font)
            |> ignore))
    builder.Build()

let payslip () =
    let details =
        [ line 780.0 [ "SYNTHETIC" ]
          line 760.0 [ "Payslip" ]
          line 740.0 [ "Employee: SYNTHETIC-Alex-7000001" ]
          line 720.0 [ "PayDate: 15/07/2025" ]
          line 700.0 [ "Payroll" ]
          line 680.0 [ "EarningCode: 101" ]
          line 660.0 [ "Summary" ] ]
    let summary =
        table 640.0
            [ [ "Item"; "Current"; "YTD" ]
              [ "Salary"; "$5000.00"; "$20000.00" ]
              [ "Gross"; "$5000.00"; "$20000.00" ]
              [ "PAYG"; "$1250.00"; "$5000.00" ]
              [ "Net"; "$3750.00"; "$15000.00" ]
              [ "Super"; "$600.00"; "$2400.00" ] ]
    build [ 48.0; 260.0; 430.0 ] (details @ summary)

let educationPayslip () =
    let details =
        [ line 780.0 [ "SYNTHETIC" ]
          line 760.0 [ "EducationPayslip" ]
          line 740.0 [ "Employee: SYNTHETIC-Blair-7000002" ]
          line 720.0 [ "PayDate: 29/07/2025" ]
          line 700.0 [ "Earnings" ] ]
    let earnings =
        table 680.0
            [ [ "Code"; "Description"; "Amount"; "YTD" ]
              [ "101"; "Salary"; "$3000.00"; "$9000.00" ]
              [ "205"; "Allowance"; "$200.00"; "$600.00" ]
              [ "TOTAL"; "GROSS"; "$3200.00"; "$9600.00" ]
              [ "PAYG"; "Tax"; "$700.00"; "$2100.00" ]
              [ "NET"; "Pay"; "$2500.00"; "$7500.00" ]
              [ "SUPER"; "SGC"; "$384.00"; "$1152.00" ] ]
    build [ 48.0; 130.0; 330.0; 450.0 ] (details @ earnings)

let rentalStatement () =
    let details =
        [ line 780.0 [ "SYNTHETIC" ]
          line 760.0 [ "RentalStatement" ]
          line 740.0 [ "Statement: RENT-SYN-001" ]
          line 720.0 [ "Property: 10-Synthetic-Lane" ]
          line 700.0 [ "Transactions" ] ]
    let transactions =
        table 680.0
            [ [ "Date"; "Description"; "Amount" ]
              [ "01/08/2025"; "Rent"; "$650.00" ]
              [ "15/08/2025"; "Rent"; "$650.00" ]
              [ "31/08/2025"; "Fee"; "-$50.00" ]
              [ "31/08/2025"; "Net"; "$1250.00" ] ]
    build [ 48.0; 260.0; 430.0 ] (details @ transactions)

let utilityInvoice () =
    [ line 780.0 [ "SYNTHETIC" ]
      line 760.0 [ "UtilityInvoice" ]
      line 740.0 [ "Invoice: UTIL-SYN-001" ]
      line 720.0 [ "Billing" ]
      line 700.0 [ "Issued: 01/08/25" ]
      line 680.0 [ "Due: 15/08/25" ]
      line 660.0 [ "Charges" ]
      line 640.0 [ "Total: $132.40" ]
      line 620.0 [ "Usage: 400kWh" ] ]
    |> build [ 48.0 ]

let insuranceRenewal () =
    let details =
        [ line 780.0 [ "SYNTHETIC" ]
          line 760.0 [ "InsuranceRenewal" ]
          line 740.0 [ "Policy: INS-SYN-001" ]
          line 720.0 [ "Insured: SYNTHETIC-Casey-Example" ]
          line 700.0 [ "Cover" ]
          line 680.0 [ "Start: 01/10/2025" ]
          line 660.0 [ "End: 30/09/2026" ]
          line 640.0 [ "Premium" ] ]
    let charges =
        table 620.0
            [ [ "Component"; "Amount"; "Currency" ]
              [ "Premium"; "$900.00"; "AUD" ]
              [ "GST"; "$90.00"; "AUD" ]
              [ "Total"; "$990.00"; "AUD" ] ]
    build [ 48.0; 260.0; 430.0 ] (details @ charges)
