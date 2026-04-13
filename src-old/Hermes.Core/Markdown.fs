namespace Hermes.Core

open System
open System.IO
open System.Text
open System.Text.RegularExpressions
open System.Threading.Tasks

/// Converts documents to structured Markdown with YAML frontmatter.
/// Parameterised over FileSystem and Logger algebras.
[<RequireQualifiedAccess>]
module Markdown =

    // ─── Types ───────────────────────────────────────────────────────

    /// Metadata for YAML frontmatter.
    type Frontmatter =
        { Source: string
          Account: string option
          Sender: string option
          Subject: string option
          Date: string option
          Category: string
          OriginalName: string
          Vendor: string option
          Amount: string option
          Abn: string option
          ExtractionMethod: string }

    /// Result of converting a document to Markdown.
    type ConversionResult =
        { Markdown: string
          Frontmatter: Frontmatter
          PlainText: string }

    // ─── Frontmatter rendering ───────────────────────────────────────

    let private yamlField (key: string) (value: string option) : string option =
        value |> Option.map (fun v -> $"{key}: {v}")

    let renderFrontmatter (fm: Frontmatter) : string =
        let lines =
            [ Some $"source: {fm.Source}"
              fm.Account |> Option.map (sprintf "account: %s")
              fm.Sender |> Option.map (sprintf "sender: %s")
              fm.Subject |> Option.map (sprintf "subject: %s")
              fm.Date |> Option.map (sprintf "date: %s")
              Some $"category: {fm.Category}"
              Some $"original_name: {fm.OriginalName}"
              fm.Vendor |> Option.map (sprintf "vendor: %s")
              fm.Amount |> Option.map (sprintf "amount: %s")
              fm.Abn |> Option.map (sprintf "abn: %s")
              Some $"extraction_method: {fm.ExtractionMethod}" ]
            |> List.choose id
        "---\n" + (lines |> String.concat "\n") + "\n---"

    // ─── Field extraction (regex heuristics) ─────────────────────────

    let private datePatterns =
        [| @"\b(\d{1,2}[/\-]\d{1,2}[/\-]\d{4})\b"
           @"\b(\d{4}[/\-]\d{1,2}[/\-]\d{1,2})\b"
           @"\b(\d{1,2}\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{4})\b"
           @"\b((?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2},?\s+\d{4})\b" |]

    let private amountPattern = Regex(@"\$\s?([\d,]+\.\d{2})", RegexOptions.Compiled)
    let private abnPattern = Regex(@"ABN[:\s]*([\d\s]{11,14})", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)
    let private acnPattern = Regex(@"ACN[:\s]*([\d\s]{9,12})", RegexOptions.Compiled ||| RegexOptions.IgnoreCase)

    let extractDate (text: string) : string option =
        datePatterns
        |> Array.tryPick (fun pat ->
            let m = Regex.Match(text, pat, RegexOptions.IgnoreCase)
            if m.Success then Some m.Groups.[1].Value else None)

    let extractAmount (text: string) : string option =
        let matches = amountPattern.Matches(text)
        if matches.Count = 0 then None
        else
            matches
            |> Seq.cast<Match>
            |> Seq.map (fun m -> m.Groups.[1].Value.Replace(",", ""))
            |> Seq.choose (fun s -> match Double.TryParse(s) with true, v -> Some v | _ -> None)
            |> Seq.sortDescending
            |> Seq.tryHead
            |> Option.map (sprintf "%.2f")

    let extractVendor (text: string) : string option =
        let lines = text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
        lines
        |> Array.tryHead
        |> Option.map (fun s -> s.Trim())
        |> Option.filter (fun s -> s.Length > 2 && s.Length < 100)

    let extractAbn (text: string) : string option =
        let m = abnPattern.Match(text)
        if m.Success then Some (m.Groups.[1].Value.Trim())
        else
            let a = acnPattern.Match(text)
            if a.Success then Some (a.Groups.[1].Value.Trim()) else None

    // ─── Plain text PDF extraction (no PdfPig dep for now) ───────────

    /// Extract text from PDF bytes. Returns the text or an error.
    /// Currently a placeholder — real impl would use PdfPig.
    let extractPdfText (_bytes: byte array) : Result<string, string> =
        Error "PdfPig not yet integrated — use Ollama vision for OCR"

    // ─── CSV → Markdown ──────────────────────────────────────────────

    let private parseCsvLine (line: string) : string list =
        let mutable inQuote = false
        let mutable field = StringBuilder()
        let fields = ResizeArray<string>()
        for c in line do
            match c with
            | '"' -> inQuote <- not inQuote
            | ',' when not inQuote ->
                fields.Add(field.ToString())
                field <- StringBuilder()
            | _ -> field.Append(c) |> ignore
        fields.Add(field.ToString())
        fields |> Seq.toList

    let csvToMarkdown (content: string) : string =
        let lines =
            content.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.truncate 1001  // limit + header
        match lines with
        | [||] -> "_Empty CSV file._"
        | _ ->
            let header = parseCsvLine lines.[0]
            let separator = header |> List.map (fun _ -> "---") |> String.concat " | "
            let headerRow = header |> String.concat " | "
            let dataRows =
                lines.[1..]
                |> Array.truncate 1000
                |> Array.map (fun line -> parseCsvLine line |> String.concat " | ")
            let table =
                [| yield $"| {headerRow} |"
                   yield $"| {separator} |"
                   for row in dataRows do
                       yield $"| {row} |" |]
            let warning =
                if lines.Length > 1001 then "\n\n_Truncated to 1000 rows._" else ""
            (table |> String.concat "\n") + warning

    // ─── Text → Markdown (wrap plain text with structure) ────────────

    let textToMarkdown (text: string) : string =
        if String.IsNullOrWhiteSpace(text) then "_No text content._"
        else
            // Preserve paragraph structure, trim excessive whitespace
            let paragraphs =
                text.Split([| "\n\n"; "\r\n\r\n" |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map (fun p -> p.Trim())
                |> Array.filter (fun p -> p.Length > 0)
            paragraphs |> String.concat "\n\n"

    // ─── Heading-aware chunker for embeddings ────────────────────────

    /// Split markdown by ## headings; fall back to character-based chunking.
    let chunkByHeadings (markdown: string) (maxChunkSize: int) : string list =
        let headingPattern = Regex(@"^##\s+", RegexOptions.Multiline)
        let sections = headingPattern.Split(markdown) |> Array.toList
        let toStrings (chunks: Embeddings.TextChunk list) =
            chunks |> List.map (fun c -> c.Text)
        match sections with
        | [] | [ _ ] when markdown.Length <= maxChunkSize -> [ markdown ]
        | [] | [ _ ] ->
            Embeddings.chunkText maxChunkSize 100 markdown |> toStrings
        | _ ->
            sections
            |> List.collect (fun section ->
                let trimmed = section.Trim()
                if trimmed.Length = 0 then []
                elif trimmed.Length <= maxChunkSize then [ trimmed ]
                else Embeddings.chunkText maxChunkSize 100 trimmed |> toStrings)

    // ─── Write sidecar .md file ──────────────────────────────────────

    /// Build a ConversionResult from raw text and metadata.
    let buildConversion
        (text: string)
        (category: string)
        (originalName: string)
        (sourceType: string)
        (account: string option)
        (sender: string option)
        (subject: string option)
        (emailDate: string option)
        (method: string)
        : ConversionResult =
        let body = textToMarkdown text
        let fm : Frontmatter =
            { Source = sourceType
              Account = account
              Sender = sender
              Subject = subject
              Date = emailDate |> Option.orElse (extractDate text)
              Category = category
              OriginalName = originalName
              Vendor = extractVendor text
              Amount = extractAmount text
              Abn = extractAbn text
              ExtractionMethod = method }
        { Markdown = renderFrontmatter fm + "\n\n" + body
          Frontmatter = fm
          PlainText = text }

    /// Write the .md sidecar file alongside the original document.
    let writeSidecar
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (savedPath: string)
        (conversion: ConversionResult)
        : Task<Result<string, string>> =
        task {
            try
                let fullPath = Path.Combine(archiveDir, savedPath + ".md")
                let dir = Path.GetDirectoryName(fullPath)
                match dir with
                | null -> ()
                | d when String.IsNullOrEmpty(d) -> ()
                | d -> fs.createDirectory d
                do! fs.writeAllText fullPath conversion.Markdown
                return Ok fullPath
            with ex ->
                return Error $"Failed to write sidecar: {ex.Message}"
        }

    /// Process a document: build markdown and write sidecar.
    let processDocument
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (archiveDir: string)
        (docId: int64)
        : Task<Result<unit, string>> =
        task {
            let! rows =
                db.execReader
                    "SELECT saved_path, category, original_name, source_type, account, sender, subject, email_date, extracted_text, extraction_method FROM documents WHERE id = @id"
                    [ ("@id", Database.boxVal docId) ]
            match rows with
            | [] -> return Error $"Document {docId} not found"
            | row :: _ ->
                let get (key: string) : string option =
                    row
                    |> Map.tryFind key
                    |> Option.bind (fun (v: obj) ->
                        match box v with
                        | :? DBNull -> None
                        | x when System.Object.ReferenceEquals(x, null) -> None
                        | x -> Some (string x))
                let text = get "extracted_text" |> Option.defaultValue ""
                if String.IsNullOrWhiteSpace(text) then
                    return Error $"Document {docId} has no extracted text"
                else
                    let savedPath = get "saved_path" |> Option.defaultValue ""
                    let conversion =
                        buildConversion
                            text
                            (get "category" |> Option.defaultValue "unsorted")
                            (get "original_name" |> Option.defaultValue "unknown")
                            (get "source_type" |> Option.defaultValue "manual_drop")
                            (get "account")
                            (get "sender")
                            (get "subject")
                            (get "email_date")
                            (get "extraction_method" |> Option.defaultValue "unknown")
                    let! writeResult = writeSidecar fs archiveDir savedPath conversion
                    match writeResult with
                    | Error e -> return Error e
                    | Ok path ->
                        logger.info $"Wrote sidecar: {path}"
                        return Ok ()
        }
