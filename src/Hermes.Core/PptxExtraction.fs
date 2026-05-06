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
        // Use InnerText which walks the entire element tree to get all text content
        let text = shape.InnerText
        if System.String.IsNullOrWhiteSpace(text) then []
        else [ PdfStructure.Block.Paragraph (text.Trim()) ]

    // ─── Table extraction ────────────────────────────────────────────

    let private cellText (cell: DocumentFormat.OpenXml.Drawing.TableCell) =
        cell.Elements<DocumentFormat.OpenXml.Drawing.TextBody>()
        |> Seq.collect (fun tb ->
            tb.Elements<DocumentFormat.OpenXml.Drawing.Paragraph>()
            |> Seq.map paragraphText)
        |> String.concat " "
        |> fun s -> s.Trim()

    let private extractDrawingTable (tbl: DocumentFormat.OpenXml.Drawing.Table) =
        let rows =
            tbl.Elements<DocumentFormat.OpenXml.Drawing.TableRow>()
            |> Seq.map (fun row ->
                row.Elements<DocumentFormat.OpenXml.Drawing.TableCell>()
                |> Seq.map cellText
                |> Seq.toList)
            |> Seq.toList

        match rows with
        | headers :: dataRows ->
            PdfStructure.Block.TableBlock { Headers = headers; Rows = dataRows }
        | [] ->
            PdfStructure.Block.Paragraph ""

    let private tryExtractTable (frame: GraphicFrame) =
        frame.Graphic
        |> Option.ofObj
        |> Option.bind (fun g -> g.GraphicData |> Option.ofObj)
        |> Option.bind (fun gd ->
            gd.Descendants<DocumentFormat.OpenXml.Drawing.Table>()
            |> Seq.tryHead)
        |> Option.map extractDrawingTable

    // ─── Speaker notes extraction ────────────────────────────────────

    let private extractNotes (slidePart: SlidePart) =
        slidePart.NotesSlidePart
        |> Option.ofObj
        |> Option.bind (fun np ->
            np.NotesSlide.CommonSlideData.ShapeTree
            |> Option.ofObj)
        |> Option.map (fun tree ->
            tree.Elements<Shape>()
            |> Seq.collect extractShapeText
            |> Seq.toList)
        |> Option.defaultValue []
        |> List.map (fun block ->
            match block with
            | PdfStructure.Block.Paragraph t ->
                PdfStructure.Block.Paragraph $"Speaker notes: {t}"
            | other -> other)

    // ─── Slide extraction ────────────────────────────────────────────

    let private extractSlideBlocks (slidePart: SlidePart) =
        match slidePart.Slide.CommonSlideData with
        | null -> []
        | csd ->
            match csd.ShapeTree with
            | null -> []
            | tree ->
                let shapeBlocks =
                    tree.Elements<Shape>()
                    |> Seq.collect extractShapeText
                    |> Seq.toList

                let tableBlocks =
                    tree.Elements<GraphicFrame>()
                    |> Seq.choose tryExtractTable
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
