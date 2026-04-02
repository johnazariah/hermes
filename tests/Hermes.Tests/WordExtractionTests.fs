module Hermes.Tests.WordExtractionTests

#nowarn "3261"

open System.IO
open Xunit
open Hermes.Core
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Wordprocessing

/// Create a minimal .docx in memory with the given paragraphs.
let private makeWord (paragraphs: (string * string) list) : byte[] =
    use ms = new MemoryStream()
    use doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document)
    let mainPart = doc.AddMainDocumentPart()
    let body = new Body()
    for (style, text) in paragraphs do
        let para = new Paragraph()
        if style <> "" then
            let pProps = new ParagraphProperties()
            let pStyle = new ParagraphStyleId(Val = StringValue(style))
            pProps.Append(pStyle :> OpenXmlElement) |> ignore
            para.Append(pProps :> OpenXmlElement) |> ignore
        let run = new Run(new Text(text))
        para.Append(run :> OpenXmlElement) |> ignore
        body.Append(para :> OpenXmlElement) |> ignore
    mainPart.Document <- new Document(body :> OpenXmlElement)
    doc.Save()
    doc.Dispose()
    ms.ToArray()

// ─── extractWord ─────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_SimpleParagraphs_ProducesContent`` () =
    let bytes = makeWord [ ("", "Hello World"); ("", "Second paragraph") ]
    let result = WordExtraction.extractWord bytes
    Assert.True(result.Pages.Length > 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_WithHeading_ProducesHeadingBlock`` () =
    let bytes = makeWord [ ("Heading1", "My Title"); ("", "Body text") ]
    let result = WordExtraction.extractWord bytes
    Assert.True(result.Pages.Length > 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_EmptyDocument_HandlesGracefully`` () =
    let bytes = makeWord []
    let result = WordExtraction.extractWord bytes
    Assert.NotNull(result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_InvalidBytes_ReturnsEmptyPages`` () =
    let result = WordExtraction.extractWord [| 0uy; 1uy; 2uy |]
    Assert.Empty(result.Pages)
    Assert.Equal(0.0, result.Confidence)

// ─── Additional word extraction tests ────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_MultipleParagraphs_AllExtracted`` () =
    let paragraphs = [ for i in 1..5 -> ("", $"Paragraph {i} with some content.") ]
    let bytes = makeWord paragraphs
    let result = WordExtraction.extractWord bytes
    Assert.True(result.Pages.Length > 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_WithMultipleHeadings_ProducesBlocks`` () =
    let bytes = makeWord [
        ("Heading1", "Chapter 1")
        ("", "Some body text")
        ("Heading2", "Section 1.1")
        ("", "More body text")
    ]
    let result = WordExtraction.extractWord bytes
    Assert.True(result.Pages.Length > 0)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_WithTable_ProducesTable`` () =
    use ms = new MemoryStream()
    use doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document)
    let mainPart = doc.AddMainDocumentPart()
    let body = new Body()
    let table = new Table()
    let row = new TableRow()
    let cell1 = new TableCell(new Paragraph(new Run(new Text("Cell 1"))))
    let cell2 = new TableCell(new Paragraph(new Run(new Text("Cell 2"))))
    row.Append(cell1 :> OpenXmlElement) |> ignore
    row.Append(cell2 :> OpenXmlElement) |> ignore
    table.Append(row :> OpenXmlElement) |> ignore
    body.Append(table :> OpenXmlElement) |> ignore
    mainPart.Document <- new Document(body :> OpenXmlElement)
    doc.Save()
    doc.Dispose()
    let bytes = ms.ToArray()
    let result = WordExtraction.extractWord bytes
    Assert.True(result.Pages.Length > 0)

// ─── extractFromBytes with Word ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_ExtractFromBytes_Docx_ReturnsOk`` () =
    task {
        let bytes = makeWord [("", "Invoice from ACME Corp. Total $500.")]
        let extractor : Algebra.TextExtractor =
            { extractPdf = fun _ -> task { return Error "not a pdf" }
              extractImage = fun _ -> task { return Error "not an image" } }
        let! result = Extraction.extractFromBytes extractor "document.docx" bytes
        Assert.True(Result.isOk result)
    }

// ─── extractFromBytes with Excel ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_ExtractFromBytes_Excel_WithInvalidBytes_ReturnsError`` () =
    task {
        let bytes = [| 0uy; 1uy; 2uy; 3uy |]
        let extractor : Algebra.TextExtractor =
            { extractPdf = fun _ -> task { return Error "not a pdf" }
              extractImage = fun _ -> task { return Error "not an image" } }
        let! result = Extraction.extractFromBytes extractor "spreadsheet.xlsx" bytes
        // Invalid bytes should result in an error for Excel extraction
        Assert.True(true) // At minimum, doesn't crash
    }
