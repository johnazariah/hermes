/// Valid, generated SYNTHETIC PDFs for text-advance and separator regressions.
module Hermes.Tests.SyntheticFixtures.PdfWordBoundaryFixtures

open UglyToad.PdfPig.Content
open UglyToad.PdfPig.Core
open UglyToad.PdfPig.Fonts.Standard14Fonts
open UglyToad.PdfPig.Writer

type private TextRun =
    { Text: string
      Size: float
      X: float
      Y: float }

let private text (value: string) (size: float) (x: float) (y: float) : TextRun =
    { Text = value; Size = size; X = x; Y = y }

let private build (fontName: Standard14Font) (runs: TextRun list) : byte[] =
    use builder = new PdfDocumentBuilder()
    let font = builder.AddStandard14Font(fontName)
    let page = builder.AddPage(PageSize.A4)
    runs
    |> List.iter (fun run ->
        page.AddText(run.Text, run.Size, PdfPoint(run.X, run.Y), font)
        |> ignore)
    builder.Build()

let numericTokens (fontName: Standard14Font) : byte[] =
    [ text "SYNTHETIC" 10.0 48.0 780.0
      text "7000001 101 15/07/2025 $5000.00 -$50.00 1,250.00 0000123 AB101Z 0.00"
          10.0 48.0 720.0 ]
    |> build fontName

// PDF tab stops are represented by text positioning, not a control-code glyph.
// The explicit one-point space advances only 0.6 points in Courier.
let narrowSpaceAndTabStops () : byte[] =
    [ text " SYNTHETIC " 10.0 48.0 780.0
      text "100" 10.0 48.0 720.0
      text " " 1.0 66.0 720.0
      text "200" 10.0 66.6 720.0
      text "7000001" 10.0 160.0 720.0
      text "7000002" 10.0 280.0 720.0
      text "NextLine " 10.0 48.0 700.0 ]
    |> build Standard14Font.Courier

let numericColumns () : byte[] =
    [ text "SYNTHETIC" 10.0 48.0 780.0
      text "Account" 10.0 48.0 720.0
      text "Date" 10.0 108.0 720.0
      text "Amount" 10.0 190.0 720.0
      text "7000001" 10.0 48.0 700.0
      text "15/07/2025" 10.0 108.0 700.0
      text "$5000.00" 10.0 190.0 700.0
      text "7000002" 10.0 48.0 680.0
      text "29/07/2025" 10.0 108.0 680.0
      text "-$50.00" 10.0 190.0 680.0 ]
    |> build Standard14Font.Courier
