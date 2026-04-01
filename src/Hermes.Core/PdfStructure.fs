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

    // ─── Structured block types ──────────────────────────────────────

    /// A table with headers and data rows.
    type Table = { Headers: string list; Rows: string list list }

    /// A key-value pair extracted from the document.
    type KeyValue = { Key: string; Value: string }

    /// A classified block of content from a PDF page.
    type Block =
        | Heading of level: int * text: string
        | Paragraph of text: string
        | TableBlock of Table
        | KeyValueBlock of KeyValue list

    // ─── Heading detection ───────────────────────────────────────────

    /// Find the most common font size across all words (= body text size).
    let detectBodyFontSize (lines: Line list) : float =
        lines
        |> List.collect (fun l -> l.Words)
        |> List.map (fun w -> System.Math.Round(w.FontSize, 1))
        |> List.countBy id
        |> List.sortByDescending snd
        |> List.tryHead
        |> Option.map fst
        |> Option.defaultValue 12.0

    let private isBoldFont (fontName: string) =
        let fn = fontName.ToUpperInvariant()
        fn.Contains("BOLD") || fn.Contains("BLACK") || fn.Contains("HEAVY")

    let private isAllCaps (text: string) =
        let letters = text |> Seq.filter System.Char.IsLetter |> Seq.toArray
        letters.Length >= 3 && letters |> Array.forall System.Char.IsUpper

    let private lineAvgFontSize (line: Line) =
        match line.Words with
        | [] -> 12.0
        | ws -> ws |> List.averageBy (fun w -> w.FontSize)

    let private lineMajorityBold (line: Line) =
        match line.Words with
        | [] -> false
        | ws ->
            let boldCount = ws |> List.filter (fun w -> isBoldFont w.FontName) |> List.length
            boldCount * 2 > ws.Length

    /// Classify each line as a heading level (Some 1/2/3) or body (None).
    let detectHeadings (lines: Line list) (bodySize: float) : (Line * int option) list =
        let classify (line: Line) =
            let avgSize = lineAvgFontSize line
            let sizeRatio = avgSize / bodySize
            let bold = lineMajorityBold line
            let allCaps = isAllCaps line.Text
            let level =
                if sizeRatio >= 1.5 then Some 1
                elif sizeRatio >= 1.2 || bold then Some 2
                elif allCaps then Some 3
                else None
            (line, level)
        lines |> List.map classify

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
