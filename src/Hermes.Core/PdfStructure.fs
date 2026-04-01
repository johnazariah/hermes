namespace Hermes.Core

open System
open UglyToad.PdfPig
open UglyToad.PdfPig.Content

/// PDF structure extraction: letters → words → lines with spatial awareness.
/// Preserves reading order (top-to-bottom, left-to-right) for downstream processing.
[<RequireQualifiedAccess>]
module PdfStructure =

    // ─── Domain types ────────────────────────────────────────────────

    /// A word extracted from a PDF with spatial position metadata.
    type Word =
        { Text: string
          X: float
          Y: float
          Width: float
          FontSize: float
          FontName: string }

    /// A line of text: spatially-grouped words with a representative Y position.
    type Line =
        { Words: Word list
          Y: float
          Text: string }

    // ─── Letter extraction ───────────────────────────────────────────

    /// Open a PDF from bytes and extract per-page letter lists.
    let extractLetters (pdfBytes: byte[]) : (int * Letter list) list =
        try
            use doc = PdfDocument.Open(pdfBytes)
            doc.GetPages()
            |> Seq.map (fun page -> (page.Number, page.Letters |> Seq.toList))
            |> Seq.toList
        with _ -> []

    // ─── Letters → Words (private helpers) ───────────────────────────

    type private WordAcc =
        { Current: Letter list
          Completed: Word list }

    let private avgGlyphWidth (letters: Letter list) =
        match letters with
        | [] -> 5.0
        | ls ->
            ls
            |> List.averageBy (fun l -> float l.BoundingBox.Width)
            |> max 1.0

    let private buildWord (letters: Letter list) : Word =
        let sorted = letters |> List.sortBy (fun l -> l.Location.X)
        let first = List.head sorted
        let last = List.last sorted
        { Text = sorted |> List.map (fun l -> l.Value) |> String.concat ""
          X = first.Location.X
          Y = first.Location.Y
          Width = (last.Location.X + float last.BoundingBox.Width) - first.Location.X
          FontSize = sorted |> List.averageBy (fun l -> float l.FontSize)
          FontName = first.FontName |> Option.ofObj |> Option.defaultValue "" }

    let private shouldBreakWord (prev: Letter) (curr: Letter) (avgWidth: float) =
        let yDist = abs (curr.Location.Y - prev.Location.Y)
        let xGap = curr.Location.X - (prev.Location.X + float prev.BoundingBox.Width)
        let fontSize = max (float prev.FontSize) (float curr.FontSize)
        yDist > fontSize * 0.5 || xGap > avgWidth * 0.5

    let private foldLetter (acc: WordAcc) (letter: Letter) : WordAcc =
        match acc.Current with
        | [] ->
            { acc with Current = [ letter ] }
        | prev :: _ ->
            if shouldBreakWord prev letter (avgGlyphWidth acc.Current) then
                { Current = [ letter ]; Completed = buildWord acc.Current :: acc.Completed }
            else
                { acc with Current = letter :: acc.Current }

    let private finalizeWords (acc: WordAcc) : Word list =
        match acc.Current with
        | [] -> acc.Completed |> List.rev
        | letters -> (buildWord letters :: acc.Completed) |> List.rev

    /// Convert raw PdfPig letters into spatially-grouped words.
    let lettersToWords (letters: Letter list) : Word list =
        letters
        |> List.filter (fun l -> not (String.IsNullOrWhiteSpace l.Value))
        |> List.sortBy (fun l -> (-l.Location.Y, l.Location.X))
        |> List.fold foldLetter { Current = []; Completed = [] }
        |> finalizeWords

    // ─── Words → Lines (private helpers) ─────────────────────────────

    type private LineAcc =
        { CurrentWords: Word list
          Completed: Line list }

    let private buildLine (words: Word list) : Line =
        let sorted = words |> List.sortBy (fun w -> w.X)
        let avgY = sorted |> List.averageBy (fun w -> w.Y)
        { Words = sorted; Y = avgY
          Text = sorted |> List.map (fun w -> w.Text) |> String.concat " " }

    let private foldWord (acc: LineAcc) (word: Word) : LineAcc =
        match acc.CurrentWords with
        | [] ->
            { acc with CurrentWords = [ word ] }
        | prev :: _ ->
            let threshold = max prev.FontSize word.FontSize * 0.5
            if abs (word.Y - prev.Y) < threshold then
                { acc with CurrentWords = word :: acc.CurrentWords }
            else
                { CurrentWords = [ word ]
                  Completed = buildLine acc.CurrentWords :: acc.Completed }

    let private finalizeLines (acc: LineAcc) : Line list =
        match acc.CurrentWords with
        | [] -> acc.Completed |> List.rev
        | words -> (buildLine words :: acc.Completed) |> List.rev

    /// Group words into lines by Y-proximity, sorted top-to-bottom.
    let wordsToLines (words: Word list) : Line list =
        words
        |> List.sortByDescending (fun w -> w.Y)
        |> List.fold foldWord { CurrentWords = []; Completed = [] }
        |> finalizeLines

    // ─── High-level pipelines ────────────────────────────────────────

    /// Full pipeline: extract letters, group into words, cluster into lines.
    let extractLines (pdfBytes: byte[]) : (int * Line list) list =
        extractLetters pdfBytes
        |> List.map (fun (pageNum, letters) ->
            (pageNum, letters |> lettersToWords |> wordsToLines))

    /// Join line texts with newlines for plain-text output.
    let linesToText (lines: Line list) : string =
        lines |> List.map (fun l -> l.Text) |> String.concat "\n"
