module Hermes.Tests.PptxExtractionTests

#nowarn "3261"

open System.IO
open Xunit
open Hermes.Core

// ─── Fixture helpers ─────────────────────────────────────────────────

let private fixtureDir =
    let testDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)
    Path.Combine(testDir, "..", "..", "..", "..", "test-documents")

let private readFixture (name: string) =
    let path = Path.Combine(fixtureDir, name)
    if File.Exists(path) then File.ReadAllBytes(path)
    else failwith $"Test fixture not found: {path}"

// ─── Error handling ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PptxExtraction_ExtractPptx_InvalidBytes_ReturnsEmptyWithZeroConfidence`` () =
    let result = PptxExtraction.extractPptx [| 0uy; 1uy; 2uy; 3uy |]
    Assert.Equal(0.0, result.Confidence)
    Assert.Empty(result.Pages)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PptxExtraction_ExtractPptx_EmptyBytes_ReturnsEmptyWithZeroConfidence`` () =
    let result = PptxExtraction.extractPptx Array.empty
    Assert.Equal(0.0, result.Confidence)
    Assert.Empty(result.Pages)

// ─── File type detection ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsPptx_ReturnsTrue_ForPptxExtension`` () =
    Assert.True(Extraction.isPptx "report.pptx")
    Assert.True(Extraction.isPptx "SLIDES.PPTX")
    Assert.True(Extraction.isPptx "deck.Pptx")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsPptx_ReturnsFalse_ForOtherExtensions`` () =
    Assert.False(Extraction.isPptx "report.pdf")
    Assert.False(Extraction.isPptx "report.docx")
    Assert.False(Extraction.isPptx "report.ppt")

// ─── Fixture-based content tests ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PptxExtraction_ExtractPptx_SimpleSlide_ExtractsText`` () =
    let bytes = readFixture "sample-simple.pptx"
    let result = PptxExtraction.extractPptx bytes
    Assert.True(result.Confidence > 0.5, $"Expected high confidence, got {result.Confidence}")
    Assert.True(result.Pages.Length >= 1, $"Expected pages, got {result.Pages.Length}")
    let allText =
        result.Pages
        |> List.collect (fun p -> p.Blocks)
        |> List.choose (fun b -> match b with PdfStructure.Block.Paragraph t -> Some t | _ -> None)
        |> String.concat " "
    Assert.Contains("Hello World", allText)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PptxExtraction_ExtractPptx_MultiSlide_CorrectPageCount`` () =
    let bytes = readFixture "sample-multi.pptx"
    let result = PptxExtraction.extractPptx bytes
    Assert.Equal(3, result.Pages.Length)
    let pageNumbers = result.Pages |> List.map (fun p -> p.PageNumber)
    Assert.Equal<int list>([ 1; 2; 3 ], pageNumbers)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PptxExtraction_ExtractPptx_MultiSlide_ExtractsSlideText`` () =
    let bytes = readFixture "sample-multi.pptx"
    let result = PptxExtraction.extractPptx bytes
    let slide1Text =
        result.Pages.[0].Blocks
        |> List.choose (fun b -> match b with PdfStructure.Block.Paragraph t -> Some t | _ -> None)
        |> String.concat " "
    Assert.Contains("Quarterly Report", slide1Text)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PptxExtraction_ExtractPptx_MultiSlide_ExtractsTable`` () =
    let bytes = readFixture "sample-multi.pptx"
    let result = PptxExtraction.extractPptx bytes
    let tableBlocks =
        result.Pages
        |> List.collect (fun p -> p.Blocks)
        |> List.choose (fun b ->
            match b with
            | PdfStructure.Block.TableBlock t -> Some t
            | _ -> None)
    Assert.NotEmpty(tableBlocks)
    let table = tableBlocks.[0]
    Assert.Contains("Name", table.Headers)
    Assert.Contains("Amount", table.Headers)
    Assert.True(table.Rows.Length >= 2, $"Expected at least 2 data rows, got {table.Rows.Length}")

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PptxExtraction_ExtractPptx_MultiSlide_ExtractsSpeakerNotes`` () =
    let bytes = readFixture "sample-multi.pptx"
    let result = PptxExtraction.extractPptx bytes
    let allText =
        result.Pages
        |> List.collect (fun p -> p.Blocks)
        |> List.choose (fun b ->
            match b with
            | PdfStructure.Block.Paragraph t -> Some t
            | _ -> None)
        |> String.concat " "
    Assert.Contains("Speaker notes:", allText)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PptxExtraction_ExtractPptx_SimpleSlide_HighConfidence`` () =
    let bytes = readFixture "sample-simple.pptx"
    let result = PptxExtraction.extractPptx bytes
    Assert.Equal(0.9, result.Confidence)
