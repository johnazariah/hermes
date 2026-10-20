/// Representative SYNTHETIC CSV shapes, not copies of provider exports.
module Hermes.Tests.SyntheticFixtures.FinancialCsv

let bankDebitCredit =
    [ "Date,Narrative,Debit Amount,Credit Amount,Balance"
      "01/08/2025,\"SYNTHETIC Rent, Example\",650.00,,4350.00"
      "02/08/2025,SYNTHETIC Salary,,3750.00,8100.00" ]
    |> String.concat "\n"

let bankSignedAmount =
    [ "Date,Amount,Description,Balance"
      "03/08/2025,-42.80,\"SYNTHETIC Grocer, Example\",8057.20"
      "04/08/2025,100.00,SYNTHETIC Refund,8157.20" ]
    |> String.concat "\n"

let dividends =
    [ "Date,Fund,Transaction Type,Units,Unit Price,Amount,Franking Credit"
      "15/09/2025,SYNTHETIC-FUND-A,Dividend,100.0000,1.2500,125.00,53.57"
      "30/09/2025,SYNTHETIC-FUND-B,Dividend,100.0000,0.7500,75.00,0.00" ]
    |> String.concat "\n"

let orders =
    [ "Order ID,Order Date,Product Name,Quantity,Unit Price,Total,Currency"
      "SYN-ORDER-001,2025-10-01,\"SYNTHETIC Notebook, A5\",2,12.50,25.00,AUD"
      "SYN-ORDER-002,2025-10-02,SYNTHETIC Cable,1,20.00,20.00,AUD" ]
    |> String.concat "\n"

let creditCard =
    [ "Transaction Date,Post Date,Description,Debit,Credit,Currency"
      "03/08/2025,04/08/2025,\"SYNTHETIC Grocer, Example\",42.80,,AUD"
      "05/08/2025,06/08/2025,SYNTHETIC Payment,,100.00,AUD" ]
    |> String.concat "\n"
