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

    // ─── Table detection ─────────────────────────────────────────────

    /// Find column boundaries by clustering word X-positions across lines.
    let findColumnBoundaries (lines: Line list) (gapThreshold: float) : float list =
        lines
        |> List.collect (fun l -> l.Words |> List.map (fun w -> w.X))
        |> List.sort
        |> List.fold (fun (groups: float list list) x ->
            match groups with
            | (last :: _ as grp) :: rest when x - last < gapThreshold ->
                (x :: grp) :: rest
            | _ -> [ x ] :: groups) []
        |> List.map (List.averageBy id)
        |> List.sort

    /// Check if a set of lines form a table: 3+ columns aligned across 3+ rows.
    let isTableRegion (lines: Line list) : bool =
        if lines.Length < 3 then false
        else
            let boundaries = findColumnBoundaries lines 15.0
            let colCount = boundaries.Length
            colCount >= 3
            && lines |> List.forall (fun l -> l.Words.Length >= 2)

    /// Assign a word to its nearest column boundary.
    let private wordToColumn (boundaries: float list) (word: Word) : int =
        boundaries
        |> List.mapi (fun i bx -> (i, abs (word.X - bx)))
        |> List.minBy snd
        |> fst

    /// Extract cell values from a table region by assigning words to columns.
    let extractTableCells (lines: Line list) (colBoundaries: float list) : string list list =
        let colCount = colBoundaries.Length
        lines
        |> List.map (fun line ->
            let cells = Array.create colCount ""
            line.Words
            |> List.iter (fun w ->
                let col = wordToColumn colBoundaries w
                cells.[col] <-
                    if cells.[col] = "" then w.Text
                    else cells.[col] + " " + w.Text)
            cells |> Array.toList)

    /// Detect table regions in a list of lines.
    /// Returns: list of (remaining non-table lines, extracted Table).
    type TableDetection = { NonTableLines: Line list; Tables: Table list }

    let private tryExtractTable (candidate: Line list) : Table option =
        let boundaries = findColumnBoundaries candidate 15.0
        if boundaries.Length < 3 || candidate.Length < 3 then None
        else
            let rows = extractTableCells candidate boundaries
            match rows with
            | headers :: dataRows when dataRows.Length >= 1 ->
                Some { Headers = headers; Rows = dataRows }
            | _ -> None

    let detectTables (lines: Line list) : TableDetection =
        let rec scan (remaining: Line list) (nonTable: Line list) (tables: Table list) =
            match remaining with
            | [] -> { NonTableLines = List.rev nonTable; Tables = List.rev tables }
            | _ ->
                let candidate =
                    remaining
                    |> List.takeWhile (fun l -> l.Words.Length >= 2)
                if candidate.Length >= 3 then
                    match tryExtractTable candidate with
                    | Some tbl ->
                        let rest = remaining |> List.skip candidate.Length
                        scan rest nonTable (tbl :: tables)
                    | None ->
                        scan (List.tail remaining) (List.head remaining :: nonTable) tables
                else
                    scan (List.tail remaining) (List.head remaining :: nonTable) tables
        scan lines [] []

    // ─── Multi-page table continuation ───────────────────────────────

    /// Check if lines could be a continuation of a previous table.
    let isContinuation (prevTable: Table) (currentLines: Line list) (colBoundaries: float list) : bool =
        colBoundaries.Length = prevTable.Headers.Length
        && currentLines.Length >= 2
        && currentLines |> List.forall (fun l -> l.Words.Length >= 2)

    /// Merge tables across pages when column boundaries match.
    let mergeMultiPageTables (pageTables: (int * Table list) list) : Table list =
        let merge (merged: Table list) (tables: Table list) =
            tables
            |> List.fold (fun acc tbl ->
                match acc with
                | prev :: rest when prev.Headers.Length = tbl.Headers.Length ->
                    { prev with Rows = prev.Rows @ tbl.Rows } :: rest
                | _ -> tbl :: acc) merged
        pageTables
        |> List.fold (fun acc (_, tables) -> merge acc tables) []
        |> List.rev

    // ─── Key-Value detection ─────────────────────────────────────────

    let private tryParseColonKV (text: string) : KeyValue option =
        let idx = text.IndexOf(':')
        if idx > 0 && idx < text.Length - 1 then
            let key = text.Substring(0, idx).Trim()
            let value = text.Substring(idx + 1).Trim()
            if key.Length >= 2 && key.Length <= 40 && value.Length >= 1 then
                Some { Key = key; Value = value }
            else None
        else None

    let private tryParseGapKV (line: Line) : KeyValue option =
        match line.Words with
        | [] | [_] -> None
        | words ->
            let sorted = words |> List.sortBy (fun w -> w.X)
            let gaps =
                sorted
                |> List.pairwise
                |> List.map (fun (a, b) -> b.X - (a.X + a.Width))
            let maxGap = gaps |> List.max
            let avgWidth = sorted |> List.averageBy (fun w -> w.Width)
            if maxGap > avgWidth * 2.0 then
                let splitIdx =
                    gaps
                    |> List.indexed
                    |> List.maxBy snd
                    |> fst
                let keyWords = sorted |> List.take (splitIdx + 1)
                let valWords = sorted |> List.skip (splitIdx + 1)
                let key = keyWords |> List.map (fun w -> w.Text) |> String.concat " "
                let value = valWords |> List.map (fun w -> w.Text) |> String.concat " "
                if key.Length >= 2 && value.Length >= 1 then
                    Some { Key = key; Value = value }
                else None
            else None

    /// Detect key-value pairs from lines (colon-separated or gap-separated).
    let detectKeyValues (lines: Line list) : (Line * KeyValue option) list =
        lines
        |> List.map (fun line ->
            let kv =
                tryParseColonKV line.Text
                |> Option.orElseWith (fun () -> tryParseGapKV line)
            (line, kv))

    /// Group consecutive KV pairs into Block list.
    let groupKeyValues (kvPairs: (Line * KeyValue option) list) : Block list =
        let flush (acc: KeyValue list) (blocks: Block list) =
            match acc with
            | [] -> blocks
            | kvs -> KeyValueBlock (List.rev kvs) :: blocks
        let folder (acc, blocks) (line, kv) =
            match kv with
            | Some k -> (k :: acc, blocks)
            | None -> ([], Paragraph line.Text :: flush acc blocks)
        let finalAcc, finalBlocks = kvPairs |> List.fold folder ([], [])
        flush finalAcc finalBlocks |> List.rev

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

    // ─── CID detection + Confidence scoring ──────────────────────────

    /// Structured content for a single page.
    type PageContent = { PageNumber: int; Blocks: Block list }

    /// Full document extraction result with confidence score.
    type DocumentContent = { Pages: PageContent list; Confidence: float }

    /// Check if text contains CID-encoded sequences (> 30% "(cid:" patterns).
    let isCidEncoded (text: string) : bool =
        if String.IsNullOrEmpty(text) then false
        else
            let cidCount = System.Text.RegularExpressions.Regex.Matches(text, @"\(cid:").Count
            let wordCount = max 1 (text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length)
            float cidCount / float wordCount > 0.3

    /// Calculate extraction confidence (0.0–1.0).
    let calculateConfidence (pages: PageContent list) (rawText: string) : float =
        let textScore =
            if String.IsNullOrWhiteSpace(rawText) then 0.0
            elif isCidEncoded rawText then 0.2
            else 1.0
        let structureScore =
            let blockCount =
                pages |> List.sumBy (fun p -> p.Blocks.Length)
            let hasTable = pages |> List.exists (fun p -> p.Blocks |> List.exists (function TableBlock _ -> true | _ -> false))
            let hasKV = pages |> List.exists (fun p -> p.Blocks |> List.exists (function KeyValueBlock _ -> true | _ -> false))
            let s = if blockCount > 0 then 0.3 else 0.0
            let s = s + (if hasTable then 0.2 else 0.0)
            s + (if hasKV then 0.1 else 0.0)
        min 1.0 (textScore * 0.6 + structureScore + 0.1)

    /// Classify lines into blocks (headings, paragraphs, tables, KV pairs).
    let private classifyBlocks (lines: Line list) : Block list =
        let bodySize = detectBodyFontSize lines
        let tableResult = detectTables lines
        let tableBlocks = tableResult.Tables |> List.map TableBlock
        let headingsAndBody =
            detectHeadings tableResult.NonTableLines bodySize
            |> List.map (fun (line, level) ->
                match level with
                | Some lvl -> Heading (lvl, line.Text)
                | None -> Paragraph line.Text)
        let kvResults = detectKeyValues tableResult.NonTableLines
        let kvBlocks = groupKeyValues kvResults
        if kvBlocks |> List.exists (function KeyValueBlock _ -> true | _ -> false) then
            tableBlocks @ kvBlocks
        else
            tableBlocks @ headingsAndBody

    /// Main entry: extract structured content from PDF bytes.
    let extractStructured (pdfBytes: byte[]) : DocumentContent =
        let pageLines = extractLines pdfBytes
        let rawText =
            pageLines |> List.collect snd |> linesToText
        let pages =
            pageLines
            |> List.map (fun (pageNum, lines) ->
                { PageNumber = pageNum; Blocks = classifyBlocks lines })
        let confidence = calculateConfidence pages rawText
        { Pages = pages; Confidence = confidence }
