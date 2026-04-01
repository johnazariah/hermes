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

// ─── detectBodyFontSize tests ────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectBodyFontSize_ReturnsMostCommonSize`` () =
    let words12 = List.replicate 10 (mkWord "body" 10.0 700.0 30.0 12.0)
    let words18 = List.replicate 2 (mkWord "heading" 10.0 750.0 60.0 18.0)
    let lines : PdfStructure.Line list =
        [ { Words = words12; Y = 700.0; Text = "body text" }
          { Words = words18; Y = 750.0; Text = "heading" } ]
    let bodySize = PdfStructure.detectBodyFontSize lines
    Assert.Equal(12.0, bodySize)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectBodyFontSize_EmptyLines_ReturnsDefault`` () =
    let bodySize = PdfStructure.detectBodyFontSize []
    Assert.Equal(12.0, bodySize)

// ─── detectHeadings tests ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectHeadings_LargeFont_ReturnsH1`` () =
    let line : PdfStructure.Line =
        { Words = [ mkWord "Title" 10.0 800.0 50.0 20.0 ]; Y = 800.0; Text = "Title" }
    let result = PdfStructure.detectHeadings [ line ] 12.0
    match result with
    | [ (_, Some 1) ] -> ()
    | _ -> failwith $"Expected H1, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectHeadings_BoldFont_ReturnsH2`` () =
    let line : PdfStructure.Line =
        { Words = [ mkWord "Section" 10.0 750.0 50.0 12.0 |> fun w -> { w with FontName = "Helvetica-Bold" } ]
          Y = 750.0; Text = "Section" }
    let result = PdfStructure.detectHeadings [ line ] 12.0
    match result with
    | [ (_, Some 2) ] -> ()
    | _ -> failwith $"Expected H2, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectHeadings_AllCaps_ReturnsH3`` () =
    let line : PdfStructure.Line =
        { Words = [ mkWord "EARNINGS AND ALLOWANCES" 10.0 700.0 150.0 12.0 ]
          Y = 700.0; Text = "EARNINGS AND ALLOWANCES" }
    let result = PdfStructure.detectHeadings [ line ] 12.0
    match result with
    | [ (_, Some 3) ] -> ()
    | _ -> failwith $"Expected H3, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectHeadings_BodyText_ReturnsNone`` () =
    let line : PdfStructure.Line =
        { Words = [ mkWord "Normal paragraph text here." 10.0 700.0 150.0 12.0 ]
          Y = 700.0; Text = "Normal paragraph text here." }
    let result = PdfStructure.detectHeadings [ line ] 12.0
    match result with
    | [ (_, None) ] -> ()
    | _ -> failwith $"Expected None (body text), got {result}"

// ─── Table detection tests ───────────────────────────────────────────

let private mkTableLine (cells: (string * float) list) y : PdfStructure.Line =
    let words = cells |> List.map (fun (text, x) -> mkWord text x y 40.0 10.0)
    { Words = words; Y = y; Text = words |> List.map (fun w -> w.Text) |> String.concat " " }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_FindColumnBoundaries_ThreeColumns_ReturnsThreeBoundaries`` () =
    let lines =
        [ mkTableLine [ ("Date", 50.0); ("Amount", 200.0); ("Balance", 400.0) ] 700.0
          mkTableLine [ ("01/10", 50.0); ("$100", 200.0); ("$5000", 400.0) ] 688.0
          mkTableLine [ ("02/10", 50.0); ("$200", 200.0); ("$4800", 400.0) ] 676.0 ]
    let boundaries = PdfStructure.findColumnBoundaries lines 15.0
    Assert.Equal(3, boundaries.Length)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_IsTableRegion_AlignedRows_ReturnsTrue`` () =
    let lines =
        [ mkTableLine [ ("Date", 50.0); ("Amount", 200.0); ("Balance", 400.0) ] 700.0
          mkTableLine [ ("01/10", 50.0); ("$100", 200.0); ("$5000", 400.0) ] 688.0
          mkTableLine [ ("02/10", 50.0); ("$200", 200.0); ("$4800", 400.0) ] 676.0 ]
    Assert.True(PdfStructure.isTableRegion lines)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_IsTableRegion_ParagraphText_ReturnsFalse`` () =
    let lines : PdfStructure.Line list =
        [ { Words = [ mkWord "This is a paragraph." 50.0 700.0 200.0 12.0 ]; Y = 700.0; Text = "This is a paragraph." }
          { Words = [ mkWord "More text here." 50.0 688.0 150.0 12.0 ]; Y = 688.0; Text = "More text here." } ]
    Assert.False(PdfStructure.isTableRegion lines)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_ExtractTableCells_AssignsWordsToCorrectColumns`` () =
    let lines =
        [ mkTableLine [ ("Date", 50.0); ("Amount", 200.0); ("Balance", 400.0) ] 700.0
          mkTableLine [ ("01/10", 50.0); ("$100", 200.0); ("$5000", 400.0) ] 688.0 ]
    let boundaries = PdfStructure.findColumnBoundaries lines 15.0
    let cells = PdfStructure.extractTableCells lines boundaries
    Assert.Equal(2, cells.Length)
    Assert.Equal("Date", cells.[0].[0])
    Assert.Equal("Amount", cells.[0].[1])
    Assert.Equal("Balance", cells.[0].[2])
    Assert.Equal("01/10", cells.[1].[0])

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectTables_BankStatement_ExtractsTransactionTable`` () =
    let lines =
        [ mkTableLine [ ("Date", 50.0); ("Narrative", 150.0); ("Debit", 300.0); ("Credit", 400.0); ("Balance", 500.0) ] 700.0
          mkTableLine [ ("01/10", 50.0); ("Opening", 150.0); ("", 300.0); ("", 400.0); ("$5000", 500.0) ] 688.0
          mkTableLine [ ("02/10", 50.0); ("Payment", 150.0); ("$100", 300.0); ("", 400.0); ("$4900", 500.0) ] 676.0
          mkTableLine [ ("03/10", 50.0); ("Deposit", 150.0); ("", 300.0); ("$200", 400.0); ("$5100", 500.0) ] 664.0 ]
    let result = PdfStructure.detectTables lines
    Assert.Equal(1, result.Tables.Length)
    Assert.Empty(result.NonTableLines)
    let tbl = result.Tables.[0]
    Assert.Equal(5, tbl.Headers.Length)
    Assert.Equal(3, tbl.Rows.Length)

// ─── Multi-page table continuation tests ─────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_IsContinuation_SameColumns_ReturnsTrue`` () =
    let prevTable : PdfStructure.Table =
        { Headers = [ "Date"; "Amount"; "Balance" ]; Rows = [ [ "01/10"; "$100"; "$5000" ] ] }
    let lines =
        [ mkTableLine [ ("02/10", 50.0); ("$200", 200.0); ("$4800", 400.0) ] 700.0
          mkTableLine [ ("03/10", 50.0); ("$300", 200.0); ("$4500", 400.0) ] 688.0 ]
    let boundaries = [ 50.0; 200.0; 400.0 ]
    Assert.True(PdfStructure.isContinuation prevTable lines boundaries)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_IsContinuation_DifferentColumns_ReturnsFalse`` () =
    let prevTable : PdfStructure.Table =
        { Headers = [ "Date"; "Amount"; "Balance" ]; Rows = [] }
    let lines =
        [ mkTableLine [ ("Name", 50.0); ("Value", 300.0) ] 700.0
          mkTableLine [ ("foo", 50.0); ("bar", 300.0) ] 688.0 ]
    let boundaries = [ 50.0; 300.0 ]
    Assert.False(PdfStructure.isContinuation prevTable lines boundaries)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_MergeMultiPageTables_CombinesRows_KeepsSingleHeader`` () =
    let t1 : PdfStructure.Table =
        { Headers = [ "Date"; "Amount" ]; Rows = [ [ "01/10"; "$100" ] ] }
    let t2 : PdfStructure.Table =
        { Headers = [ "Date"; "Amount" ]; Rows = [ [ "02/10"; "$200" ]; [ "03/10"; "$300" ] ] }
    let result = PdfStructure.mergeMultiPageTables [ (1, [ t1 ]); (2, [ t2 ]) ]
    Assert.Equal(1, result.Length)
    let merged = result.[0]
    Assert.Equal<string list>([ "Date"; "Amount" ], merged.Headers)
    Assert.Equal(3, merged.Rows.Length)

// ─── Key-Value detection tests ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectKV_ColonSeparated_ReturnsKV`` () =
    let line : PdfStructure.Line =
        { Words = [ mkWord "Pay Date: 31.07.2024" 50.0 700.0 200.0 10.0 ]
          Y = 700.0; Text = "Pay Date: 31.07.2024" }
    let result = PdfStructure.detectKeyValues [ line ]
    match result with
    | [ (_, Some kv) ] ->
        Assert.Equal("Pay Date", kv.Key)
        Assert.Equal("31.07.2024", kv.Value)
    | _ -> failwith $"Expected KV pair, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectKV_GapSeparated_ReturnsKV`` () =
    let line : PdfStructure.Line =
        { Words = [ mkWord "Employee" 50.0 700.0 60.0 10.0
                    mkWord "#" 115.0 700.0 10.0 10.0
                    mkWord "12345678" 350.0 700.0 60.0 10.0 ]
          Y = 700.0; Text = "Employee # 12345678" }
    let result = PdfStructure.detectKeyValues [ line ]
    match result with
    | [ (_, Some kv) ] ->
        Assert.Equal("Employee #", kv.Key)
        Assert.Equal("12345678", kv.Value)
    | _ -> failwith $"Expected KV pair, got {result}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_DetectKV_ParagraphText_ReturnsNone`` () =
    let line : PdfStructure.Line =
        { Words = [ mkWord "This" 50.0 700.0 30.0 10.0
                    mkWord "is" 85.0 700.0 15.0 10.0
                    mkWord "a" 105.0 700.0 10.0 10.0
                    mkWord "normal" 120.0 700.0 45.0 10.0
                    mkWord "sentence." 170.0 700.0 60.0 10.0 ]
          Y = 700.0; Text = "This is a normal sentence." }
    let result = PdfStructure.detectKeyValues [ line ]
    match result with
    | [ (_, None) ] -> ()
    | _ -> failwith $"Expected None (not a KV pair), got {result}"

// ─── CID detection + Confidence scoring tests ────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_IsCidEncoded_WithCidSequences_ReturnsTrue`` () =
    let text = "(cid:1) (cid:2) (cid:3) (cid:4) hello"
    Assert.True(PdfStructure.isCidEncoded text)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_IsCidEncoded_NormalText_ReturnsFalse`` () =
    Assert.False(PdfStructure.isCidEncoded "This is normal text with no CID sequences at all.")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_CalculateConfidence_FullyDecoded_ReturnsHigh`` () =
    let tbl : PdfStructure.Table = { Headers = ["A";"B"]; Rows = [["1";"2"]] }
    let blocks : PdfStructure.Block list =
        [ PdfStructure.Block.Paragraph "Hello world"
          PdfStructure.Block.TableBlock tbl ]
    let pages : PdfStructure.PageContent list =
        [ { PageNumber = 1; Blocks = blocks } ]
    let conf = PdfStructure.calculateConfidence pages "Hello world A B 1 2"
    Assert.True(conf >= 0.8, $"Expected high confidence, got {conf}")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_CalculateConfidence_MostlyCid_ReturnsLow`` () =
    let pages : PdfStructure.PageContent list =
        [ { PageNumber = 1; Blocks = [] } ]
    let conf = PdfStructure.calculateConfidence pages "(cid:1) (cid:2) (cid:3) (cid:4) x"
    Assert.True(conf < 0.5, $"Expected low confidence, got {conf}")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfStructure_ExtractStructured_GeneratedPdf_ReturnsContent`` () =
    use builder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder()
    let font =
        builder.AddStandard14Font(
            UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica)
    let page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4)
    page.AddText("Invoice Summary", 16.0, UglyToad.PdfPig.Core.PdfPoint(72.0, 750.0), font) |> ignore
    page.AddText("Amount: $500.00", 12.0, UglyToad.PdfPig.Core.PdfPoint(72.0, 720.0), font) |> ignore
    let pdfBytes = builder.Build()
    let result = PdfStructure.extractStructured pdfBytes
    Assert.NotEmpty(result.Pages)
    Assert.True(result.Confidence > 0.0, $"Expected positive confidence, got {result.Confidence}")

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
