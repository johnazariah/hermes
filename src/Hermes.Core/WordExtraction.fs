namespace Hermes.Core

#nowarn "3261"

open System
open System.IO
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing

/// Word document extraction: Open XML SDK paragraphs/tables/headings → markdown.
[<RequireQualifiedAccess>]
module WordExtraction =

    let private headingLevel (style: string) =
        match style with
        | "Heading1" | "heading 1" -> Some 1
        | "Heading2" | "heading 2" -> Some 2
        | "Heading3" | "heading 3" -> Some 3
        | s when not (String.IsNullOrEmpty(s)) && s.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) -> Some 4
        | _ -> None

    let private extractTable (table: Table) : PdfStructure.Block =
        let rows =
            table.Elements<TableRow>()
            |> Seq.map (fun row ->
                row.Elements<TableCell>()
                |> Seq.map (fun cell -> cell.InnerText.Trim())
                |> Seq.toList)
            |> Seq.toList
        match rows with
        | headers :: dataRows ->
            PdfStructure.Block.TableBlock { Headers = headers; Rows = dataRows }
        | [] ->
            PdfStructure.Block.Paragraph ""

    let private extractParagraph (para: Paragraph) : PdfStructure.Block option =
        let text = para.InnerText.Trim()
        if String.IsNullOrEmpty(text) then None
        else
            let styleId =
                para.ParagraphProperties
                |> Option.ofObj
                |> Option.bind (fun pp -> pp.ParagraphStyleId |> Option.ofObj)
                |> Option.map (fun s -> s.Val.Value)
                |> Option.defaultValue ""
            match headingLevel styleId with
            | Some level -> Some (PdfStructure.Block.Heading (level, text))
            | None -> Some (PdfStructure.Block.Paragraph text)

    /// Extract content from Word document bytes into DocumentContent.
    let extractWord (bytes: byte[]) : PdfStructure.DocumentContent =
        try
            use stream = new MemoryStream(bytes)
            use doc = WordprocessingDocument.Open(stream, false)
            let body = doc.MainDocumentPart.Document.Body
            let blocks =
                body.ChildElements
                |> Seq.choose (fun elem ->
                    match elem with
                    | :? Paragraph as p -> extractParagraph p
                    | :? Table as t -> Some (extractTable t)
                    | _ -> None)
                |> Seq.toList
            { Pages = [ { PageNumber = 1; Blocks = blocks } ]
              Confidence = if blocks.IsEmpty then 0.3 else 0.9 }
        with _ ->
            { Pages = []; Confidence = 0.0 }
