namespace Hermes.Core

open System
open System.IO

/// Excel extraction: ClosedXML workbook sheets → markdown tables.
[<RequireQualifiedAccess>]
module ExcelExtraction =

    /// Extract all sheets from Excel bytes into DocumentContent.
    let extractExcel (bytes: byte[]) : PdfStructure.DocumentContent =
        try
            use stream = new MemoryStream(bytes)
            use workbook = new ClosedXML.Excel.XLWorkbook(stream)
            let pages =
                workbook.Worksheets
                |> Seq.toList
                |> List.mapi (fun idx ws ->
                    let rowCount = min 10000 (ws.LastRowUsed() |> Option.ofObj |> Option.map (fun r -> r.RowNumber()) |> Option.defaultValue 0)
                    let colCount = ws.LastColumnUsed() |> Option.ofObj |> Option.map (fun c -> c.ColumnNumber()) |> Option.defaultValue 0
                    if rowCount = 0 || colCount = 0 then None
                    else
                        let readRow (rowNum: int) =
                            [ for col in 1..colCount ->
                                let cell = ws.Cell(rowNum, col)
                                if cell.HasFormula then cell.CachedValue |> string
                                elif cell.DataType = ClosedXML.Excel.XLDataType.DateTime then
                                    try cell.GetDateTime().ToString("yyyy-MM-dd") with _ -> cell.GetString()
                                else cell.GetString() ]
                        let headers = readRow 1
                        let rows = [ for r in 2..rowCount -> readRow r ]
                        let blocks : PdfStructure.Block list =
                            [ PdfStructure.Block.Heading (2, ws.Name)
                              PdfStructure.Block.TableBlock { Headers = headers; Rows = rows } ]
                        let page : PdfStructure.PageContent = { PageNumber = idx + 1; Blocks = blocks }
                        Some page)
                |> List.choose id
            { Pages = pages; Confidence = if pages.IsEmpty then 0.3 else 0.9 }
        with ex ->
            { Pages = []; Confidence = 0.0 }
