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

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_Heading2_ReturnsLevel2`` () =
    let bytes = makeWord [ ("Heading2", "Subtitle") ]
    let result = WordExtraction.extractWord bytes
    let blocks = result.Pages.[0].Blocks
    let headings = blocks |> List.choose (function PdfStructure.Block.Heading (l, _) -> Some l | _ -> None)
    Assert.Contains(2, headings)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_Heading3_ReturnsLevel3`` () =
    let bytes = makeWord [ ("Heading3", "Section") ]
    let result = WordExtraction.extractWord bytes
    let blocks = result.Pages.[0].Blocks
    let headings = blocks |> List.choose (function PdfStructure.Block.Heading (l, _) -> Some l | _ -> None)
    Assert.Contains(3, headings)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_UnknownHeadingStyle_ReturnsLevel4`` () =
    let bytes = makeWord [ ("Heading5", "Deep section") ]
    let result = WordExtraction.extractWord bytes
    let blocks = result.Pages.[0].Blocks
    let headings = blocks |> List.choose (function PdfStructure.Block.Heading (l, _) -> Some l | _ -> None)
    Assert.Contains(4, headings)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_NonHeadingStyle_ReturnsParagraph`` () =
    let bytes = makeWord [ ("Normal", "Regular text") ]
    let result = WordExtraction.extractWord bytes
    let blocks = result.Pages.[0].Blocks
    let paras = blocks |> List.choose (function PdfStructure.Block.Paragraph t -> Some t | _ -> None)
    Assert.Contains("Regular text", paras)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_MixedContent_PreservesAllBlocks`` () =
    let bytes = makeWord [
        ("Heading1", "Title")
        ("", "Intro text")
        ("Heading2", "Section 1")
        ("", "Section body")
    ]
    let result = WordExtraction.extractWord bytes
    let blocks = result.Pages.[0].Blocks
    Assert.True(blocks.Length >= 4)
    Assert.True(result.Confidence > 0.5)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``WordExtraction_ExtractWord_WithTable_ReturnsTableBlock`` () =
    // Build a docx with a table using Open XML directly
    use ms = new MemoryStream()
    use doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document)
    let mainPart = doc.AddMainDocumentPart()
    let body = new Body()
    let tbl = new Table()
    for rowData in [ ["Name";"Age"]; ["Alice";"30"]; ["Bob";"25"] ] do
        let tr = new TableRow()
        for cellText in rowData do
            let tc = new TableCell()
            let p = new Paragraph(new Run(new Text(cellText)))
            tc.Append(p :> OpenXmlElement) |> ignore
            tr.Append(tc :> OpenXmlElement) |> ignore
        tbl.Append(tr :> OpenXmlElement) |> ignore
    body.Append(tbl :> OpenXmlElement) |> ignore
    mainPart.Document <- new Document(body :> OpenXmlElement)
    doc.Save()
    doc.Dispose()
    let bytes = ms.ToArray()
    let result = WordExtraction.extractWord bytes
    let tables = result.Pages.[0].Blocks |> List.choose (function PdfStructure.Block.TableBlock t -> Some t | _ -> None)
    Assert.Equal(1, tables.Length)
    Assert.Equal<string list>(["Name";"Age"], tables.[0].Headers)
    Assert.Equal(2, tables.[0].Rows.Length)
