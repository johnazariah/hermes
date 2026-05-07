namespace Hermes.Core

#nowarn "3261"

open System.IO
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Presentation
open DocumentFormat.OpenXml.Drawing

/// PowerPoint extraction: Open XML SDK slides/shapes/tables/notes → DocumentContent.
[<RequireQualifiedAccess>]
module PptxExtraction =

    // ─── Text extraction helpers ─────────────────────────────────────

    let private paragraphText (p: DocumentFormat.OpenXml.Drawing.Paragraph) =
        p.Elements<DocumentFormat.OpenXml.Drawing.Run>()
        |> Seq.map (fun r -> r.Text.Text)
        |> String.concat ""
        |> fun s -> s.Trim()

    let private textBodyBlocks (textBody: TextBody) =
        textBody.Elements<DocumentFormat.OpenXml.Drawing.Paragraph>()
        |> Seq.map paragraphText
        |> Seq.filter (fun t -> t.Length > 0)
        |> Seq.map PdfStructure.Block.Paragraph
        |> Seq.toList

    let private extractShapeText (shape: Shape) =
        let text = shape.InnerText
        if System.String.IsNullOrWhiteSpace(text) then []
        else [ PdfStructure.Block.Paragraph (text.Trim()) ]

    // ─── Table extraction ────────────────────────────────────────────

    let private cellText (cell: DocumentFormat.OpenXml.OpenXmlElement) =
        cell.Descendants()
        |> Seq.filter (fun e -> e.LocalName = "t")
        |> Seq.map (fun e -> e.InnerText)
        |> String.concat " "
        |> fun s -> s.Trim()

    let private extractDrawingTable (tbl: DocumentFormat.OpenXml.OpenXmlElement) =
        let rows =
            tbl.ChildElements
            |> Seq.filter (fun e -> e.LocalName = "tr")
            |> Seq.map (fun row ->
                row.ChildElements
                |> Seq.filter (fun e -> e.LocalName = "tc")
                |> Seq.map cellText
                |> Seq.toList)
            |> Seq.toList

        match rows with
        | headers :: dataRows ->
            PdfStructure.Block.TableBlock { Headers = headers; Rows = dataRows }
        | [] ->
            PdfStructure.Block.Paragraph ""

    let private tryExtractTable (frame: GraphicFrame) =
        frame.Descendants()
        |> Seq.tryFind (fun e -> e.LocalName = "tbl")
        |> Option.map extractDrawingTable

    // ─── Speaker notes extraction ────────────────────────────────────

    let private extractNotes (slidePart: SlidePart) =
        if isNull slidePart.NotesSlidePart then []
        else
            let np = slidePart.NotesSlidePart
            if isNull (box np.NotesSlide) then [] else
            let csd = np.NotesSlide.CommonSlideData
            if isNull (box csd) || isNull (box csd.ShapeTree) then [] else
            csd.ShapeTree.ChildElements
            |> Seq.filter (fun e -> e.LocalName = "sp")
            |> Seq.map (fun e -> e.InnerText.Trim())
            |> Seq.filter (fun t -> t.Length > 0)
            |> Seq.map (fun t -> PdfStructure.Block.Paragraph $"Speaker notes: {t}")
            |> Seq.toList

    // ─── Slide extraction ────────────────────────────────────────────

    let private extractSlideBlocks (slidePart: SlidePart) =
        let slide = slidePart.Slide
        if isNull (box slide) then [] else
        let csd = slide.CommonSlideData
        if isNull (box csd) then [] else
        let tree = csd.ShapeTree
        if isNull (box tree) then [] else

        let shapeBlocks =
            tree.ChildElements
            |> Seq.filter (fun e -> e.LocalName = "sp")
            |> Seq.collect (fun e ->
                let text = e.InnerText
                if System.String.IsNullOrWhiteSpace(text) then Seq.empty
                else seq { PdfStructure.Block.Paragraph (text.Trim()) })
            |> Seq.toList

        let tableBlocks =
            tree.ChildElements
            |> Seq.filter (fun e -> e.LocalName = "graphicFrame")
            |> Seq.choose (fun frame ->
                frame.Descendants()
                |> Seq.tryFind (fun e -> e.LocalName = "tbl")
                |> Option.map extractDrawingTable)
            |> Seq.toList

        let noteBlocks = extractNotes slidePart

        shapeBlocks @ tableBlocks @ noteBlocks

    let private resolveSlidePartOrdered (presentationPart: PresentationPart) =
        presentationPart.Presentation.SlideIdList.Elements<SlideId>()
        |> Seq.map (fun slideId ->
            slideId.RelationshipId.Value
            |> presentationPart.GetPartById
            :?> SlidePart)
        |> Seq.toList

    let private slideToPage (idx: int) (slidePart: SlidePart) : PdfStructure.PageContent =
        { PageNumber = idx + 1
          Blocks = extractSlideBlocks slidePart }

    // ─── Public API ──────────────────────────────────────────────────

    /// Extract content from PowerPoint bytes into DocumentContent.
    let extractPptx (bytes: byte[]) : PdfStructure.DocumentContent =
        try
            use stream = new MemoryStream(bytes)
            use doc = PresentationDocument.Open(stream, false)

            let pages =
                doc.PresentationPart
                |> resolveSlidePartOrdered
                |> List.mapi slideToPage

            let hasContent =
                pages |> List.exists (fun p -> p.Blocks.IsEmpty |> not)

            { Pages = pages
              Confidence = if hasContent then 0.9 else 0.3 }
        with _ ->
            { Pages = []; Confidence = 0.0 }
