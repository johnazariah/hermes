module Hermes.Tests.PdfStructureTests

open Xunit
open Hermes.Core

// ─── Helper: create a Word record ────────────────────────────────────

let private mkWord text x y width fontSize : PdfStructure.Word =
    { Text = text; X = x; Y = y; Width = width
      FontSize = fontSize; FontName = "TestFont" }

// ─── wordsToLines tests ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_WordsToLines_GroupsByYProximity`` () =
    let words =
        [ mkWord "Hello" 10.0 700.0 40.0 12.0
          mkWord "World" 60.0 700.0 40.0 12.0
          mkWord "Below" 10.0 680.0 40.0 12.0 ]
    let lines = PdfStructure.wordsToLines words
    Assert.Equal(2, lines.Length)
    Assert.Equal("Hello World", lines.[0].Text)
    Assert.Equal("Below", lines.[1].Text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_WordsToLines_SingleWord_ReturnsSingleLine`` () =
    let words = [ mkWord "Solo" 10.0 700.0 30.0 12.0 ]
    let lines = PdfStructure.wordsToLines words
    Assert.Equal(1, lines.Length)
    Assert.Equal("Solo", lines.[0].Text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_WordsToLines_SameLineDifferentX_SortsByXAscending`` () =
    let words =
        [ mkWord "C" 200.0 700.0 10.0 12.0
          mkWord "A" 10.0 700.0 10.0 12.0
          mkWord "B" 100.0 700.0 10.0 12.0 ]
    let lines = PdfStructure.wordsToLines words
    Assert.Equal(1, lines.Length)
    Assert.Equal("A B C", lines.[0].Text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_WordsToLines_ThreeDistinctLines_TopToBottom`` () =
    let words =
        [ mkWord "Top" 10.0 800.0 30.0 12.0
          mkWord "Mid" 10.0 750.0 30.0 12.0
          mkWord "Bot" 10.0 700.0 30.0 12.0 ]
    let lines = PdfStructure.wordsToLines words
    Assert.Equal(3, lines.Length)
    Assert.Equal("Top", lines.[0].Text)
    Assert.Equal("Mid", lines.[1].Text)
    Assert.Equal("Bot", lines.[2].Text)

// ─── linesToText tests ───────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_LinesToText_PreservesReadingOrder`` () =
    let lines : PdfStructure.Line list =
        [ { Words = []; Y = 100.0; Text = "First line (top)" }
          { Words = []; Y = 50.0; Text = "Second line (bottom)" } ]
    let text = PdfStructure.linesToText lines
    Assert.Equal("First line (top)\nSecond line (bottom)", text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_LinesToText_EmptyLines_ReturnsEmpty`` () =
    let text = PdfStructure.linesToText []
    Assert.Equal("", text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_LinesToText_SingleLine_NoTrailingNewline`` () =
    let lines : PdfStructure.Line list =
        [ { Words = []; Y = 700.0; Text = "Only line" } ]
    let text = PdfStructure.linesToText lines
    Assert.Equal("Only line", text)

// ─── extractLetters edge cases ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_ExtractLetters_InvalidBytes_ReturnsEmptyList`` () =
    let result = PdfStructure.extractLetters [| 0uy; 1uy; 2uy |]
    Assert.Empty(result)

// ─── Integration: generated PDF via PdfPig builder ───────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PdfStructure_ExtractLines_GeneratedPdf_ExtractsText`` () =
    use builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder()
    let font =
        builder.AddStandard14Font(
            UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica)
    let page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4)
    page.AddText("Hello World", 12.0, UglyToad.PdfPig.Core.PdfPoint(72.0, 720.0), font)
    |> ignore
    let pdfBytes = builder.Build()
    let result = PdfStructure.extractLines pdfBytes
    Assert.NotEmpty(result)
    let (pageNum, lines) = result.[0]
    Assert.Equal(1, pageNum)
    Assert.True(lines.Length >= 1)
    let fullText = PdfStructure.linesToText lines
    Assert.Contains("Hello", fullText)
