module Hermes.Tests.ExcelExtractionTests

#nowarn "3261"

open System.IO
open Xunit
open Hermes.Core
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

/// Create a minimal .xlsx in memory with the given rows.
let private makeExcel (sheets: (string * string list list) list) : byte[] =
    use ms = new MemoryStream()
    use doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook)
    let wbPart = doc.AddWorkbookPart()
    wbPart.Workbook <- new Workbook()
    let sheets' = wbPart.Workbook.AppendChild(new Sheets())
    let mutable sheetId = 1u
    for (name, rows) in sheets do
        let wsPart = wbPart.AddNewPart<WorksheetPart>()
        let sheetData = new SheetData()
        for rowData in rows do
            let row = new Row()
            for cellVal in rowData do
                let cell = new Cell(DataType = EnumValue(CellValues.String), CellValue = new CellValue(cellVal))
                row.Append(cell :> OpenXmlElement) |> ignore
            sheetData.Append(row :> OpenXmlElement) |> ignore
        wsPart.Worksheet <- new Worksheet(sheetData :> OpenXmlElement)
        let sheet = new Sheet(Id = StringValue(wbPart.GetIdOfPart(wsPart)), SheetId = UInt32Value(sheetId), Name = StringValue(name))
        sheets'.Append(sheet :> OpenXmlElement) |> ignore
        sheetId <- sheetId + 1u
    doc.Save()
    doc.Dispose()
    ms.ToArray()

// ─── extractExcel ────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ExcelExtraction_ExtractExcel_SimpleSheet_ProducesContent`` () =
    let bytes = makeExcel [ ("Sheet1", [ ["Name"; "Amount"]; ["Alice"; "100"]; ["Bob"; "200"] ]) ]
    let result = ExcelExtraction.extractExcel bytes
    Assert.True(result.Pages.Length > 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ExcelExtraction_ExtractExcel_MultipleSheets_ProducesMultiplePages`` () =
    let bytes = makeExcel [
        ("Sales", [ ["Product"; "Qty"]; ["Widget"; "5"] ])
        ("Expenses", [ ["Item"; "Cost"]; ["Rent"; "1000"] ])
    ]
    let result = ExcelExtraction.extractExcel bytes
    Assert.True(result.Pages.Length >= 2)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ExcelExtraction_ExtractExcel_EmptySheet_HandlesGracefully`` () =
    let bytes = makeExcel [ ("Empty", []) ]
    let result = ExcelExtraction.extractExcel bytes
    Assert.NotNull(result)
