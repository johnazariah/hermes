module Hermes.Tests.PdfWordBoundaryTests

open System
open Xunit
open Hermes.Core
open Hermes.Tests.SyntheticFixtures
open UglyToad.PdfPig.Content
open UglyToad.PdfPig.Fonts.Standard14Fonts

let private extractLetters (bytes: byte[]) : Letter list =
    bytes |> PdfStructure.extractLetters |> List.collect snd

let private assertContinuousRun (text: string) (letters: Letter list) : unit =
    let run =
        letters
        |> List.windowed text.Length
        |> List.find (fun values ->
            (values |> List.map (fun letter -> letter.Value) |> String.concat "") = text)
    run
    |> List.pairwise
    |> List.iter (fun (previous, current) ->
        Assert.Equal(previous.EndBaseLine.X, current.StartBaseLine.X, 6))

[<Theory>]
[<InlineData(Standard14Font.Courier)>]
[<InlineData(Standard14Font.Helvetica)>]
[<InlineData(Standard14Font.TimesRoman)>]
[<Trait("Category", "Integration")>]
let ``PdfWords_ValidTextAdvances_PreserveNumericAndOrdinaryTokens`` (font: Standard14Font) =
    let letters = PdfWordBoundaryFixtures.numericTokens font |> extractLetters
    assertContinuousRun "$5000.00" letters
    let words = letters |> PdfStructure.lettersToWords
    Assert.Equal<string list>(
        [ "SYNTHETIC"; "7000001"; "101"; "15/07/2025"; "$5000.00"
          "-$50.00"; "1,250.00"; "0000123"; "AB101Z"; "0.00" ],
        words |> List.map (fun word -> word.Text))
    let lines = words |> PdfStructure.wordsToLines
    Assert.Equal(2, lines.Length)
    Assert.Equal(
        "7000001 101 15/07/2025 $5000.00 -$50.00 1,250.00 0000123 AB101Z 0.00",
        lines.[1].Text)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PdfWords_ExplicitSmallSpaceAndTabStops_DoNotJoinTokens`` () =
    let letters = PdfWordBoundaryFixtures.narrowSpaceAndTabStops () |> extractLetters
    Assert.True(
        letters |> List.exists (fun letter ->
            letter.Value = " "
            && abs (letter.EndBaseLine.X - letter.StartBaseLine.X) < 1.0))
    let words = letters |> PdfStructure.lettersToWords
    Assert.Equal<string list>(
        [ "SYNTHETIC"; "100"; "200"; "7000001"; "7000002"; "NextLine" ],
        words |> List.map (fun word -> word.Text))
    Assert.Equal(18.0, words.[1].Width, 6)
    Assert.Equal(66.6, words.[2].X, 6)
    Assert.Equal(3, words |> PdfStructure.wordsToLines |> List.length)

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PdfWords_WhitespaceOnlyLetters_DoNotCreateWords`` () =
    let spaces =
        PdfWordBoundaryFixtures.narrowSpaceAndTabStops ()
        |> extractLetters
        |> List.filter (fun letter -> String.IsNullOrWhiteSpace letter.Value)
    Assert.NotEmpty(spaces)
    Assert.Empty(PdfStructure.lettersToWords spaces)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``PdfWords_EmptyLetters_ReturnNoWords`` () =
    Assert.Empty(PdfStructure.lettersToWords [])

[<Fact>]
[<Trait("Category", "Integration")>]
let ``PdfWords_PositionedColumns_PreserveExactNumericTableAndMarkdown`` () =
    let result = PdfWordBoundaryFixtures.numericColumns () |> FinancialAssertions.pdf
    FinancialAssertions.table [ "Account"; "Date"; "Amount" ]
        [ [ "7000001"; "15/07/2025"; "$5000.00" ]
          [ "7000002"; "29/07/2025"; "-$50.00" ] ] result
    FinancialAssertions.confidence 0.5 result
