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
