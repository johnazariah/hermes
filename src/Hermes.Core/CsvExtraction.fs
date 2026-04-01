namespace Hermes.Core

open System

/// CSV extraction: dialect detection, markdown table, raw content preservation.
[<RequireQualifiedAccess>]
module CsvExtraction =

    /// Parse a CSV line respecting quoted fields.
    let parseCsvLine (delimiter: char) (line: string) : string list =
        let mutable inQuotes = false
        let mutable field = System.Text.StringBuilder()
        let fields = ResizeArray<string>()
        for ch in line do
            if ch = '"' then
                inQuotes <- not inQuotes
            elif ch = delimiter && not inQuotes then
                fields.Add(field.ToString().Trim())
                field <- System.Text.StringBuilder()
            else
                field.Append(ch) |> ignore
        fields.Add(field.ToString().Trim())
        fields |> Seq.toList

    /// Detect CSV dialect by counting candidate delimiters in first line.
    let detectDelimiter (text: string) : char =
        let firstLine =
            text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.tryHead
            |> Option.defaultValue ""
        let candidates = [| ','; ';'; '\t'; '|' |]
        candidates
        |> Array.maxBy (fun d ->
            firstLine |> Seq.filter ((=) d) |> Seq.length)

    /// Extract CSV text into DocumentContent (markdown table).
    let extractCsv (text: string) : PdfStructure.DocumentContent =
        if String.IsNullOrWhiteSpace(text) then
            { Pages = []; Confidence = 0.3 }
        else
            let delimiter = detectDelimiter text
            let lines =
                text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (parseCsvLine delimiter)
                |> Array.toList
            match lines with
            | [] ->
                { Pages = []; Confidence = 0.3 }
            | headers :: rows ->
                let table : PdfStructure.Table = { Headers = headers; Rows = rows }
                let blocks = [ PdfStructure.Block.TableBlock table ]
                { Pages = [ { PageNumber = 1; Blocks = blocks } ]
                  Confidence = 0.85 }
