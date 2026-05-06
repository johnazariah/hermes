module Hermes.Tests.PptxExtractionTests

#nowarn "3261"

open Xunit
open Hermes.Core

// ─── Tests ───────────────────────────────────────────────────────────

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
    Assert.False(Extraction.isPptx "report.xlsx")
    Assert.False(Extraction.isPptx "report.ppt")
