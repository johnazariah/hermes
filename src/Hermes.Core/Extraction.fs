namespace Hermes.Core

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open UglyToad.PdfPig

/// Text extraction: PDF text via PdfPig, regex field parsing, Ollama vision fallback.
/// Pure functions where possible; DB interaction isolated to processDocument/extractBatch.
[<RequireQualifiedAccess>]
module Extraction =

    // ─── Scanned detection ───────────────────────────────────────────

    let [<Literal>] ScannedCharThreshold = 50

    let isLikelyScanned (text: string) =
        text.Trim().Length < ScannedCharThreshold

    // ─── PDF text extraction ─────────────────────────────────────────

    let extractPdfText (pdfBytes: byte[]) : Result<string, string> =
        try
            use doc = PdfDocument.Open(pdfBytes)
            doc.GetPages()
            |> Seq.map (fun page -> page.Text)
            |> Seq.choose (fun t -> if String.IsNullOrEmpty(t) then None else Some t)
            |> String.concat "\n"
            |> Ok
        with ex ->
            Error $"PDF extraction failed: {ex.Message}"

    // ─── File type detection ─────────────────────────────────────────

    let isPdf (path: string) =
        path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)

    let isImage (path: string) =
        [| ".png"; ".jpg"; ".jpeg"; ".tiff"; ".tif"; ".bmp"; ".gif"; ".webp" |]
        |> Array.exists (fun ext -> path.EndsWith(ext, StringComparison.OrdinalIgnoreCase))

    // ─── Regex field parsers (pure) ──────────────────────────────────

    let private datePatterns =
        [| @"\b(\d{1,2})[/\-](\d{1,2})[/\-](\d{4})\b"
           @"\b(\d{4})-(\d{2})-(\d{2})\b"
           @"\b(\d{1,2})\s+(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+(\d{4})\b" |]

    let tryExtractDate (text: string) : string option =
        if String.IsNullOrEmpty(text) then None
        else
            datePatterns
            |> Array.tryPick (fun pat ->
                let m = Regex.Match(text, pat, RegexOptions.IgnoreCase)
                if m.Success then Some m.Value else None)

    let private amountPattern =
        @"(?:\$|AUD\s*)\s*(\d{1,3}(?:,\d{3})*(?:\.\d{2})?)"

    let tryExtractAmount (text: string) : decimal option =
        if String.IsNullOrEmpty(text) then None
        else
            let m = Regex.Match(text, amountPattern, RegexOptions.IgnoreCase)
            if m.Success then
                m.Groups.[1].Value.Replace(",", "")
                |> Decimal.TryParse
                |> function true, d -> Some d | _ -> None
            else None

    let private abnPattern = @"\bABN[:\s]*(\d{2}\s?\d{3}\s?\d{3}\s?\d{3})\b"
    let private acnPattern = @"\bACN[:\s]*(\d{3}\s?\d{3}\s?\d{3})\b"

    let tryExtractAbn (text: string) : string option =
        if String.IsNullOrEmpty(text) then None
        else
            let m = Regex.Match(text, abnPattern, RegexOptions.IgnoreCase)
            if m.Success then Some (m.Groups.[1].Value.Replace(" ", "").Replace("-", ""))
            else
                let a = Regex.Match(text, acnPattern, RegexOptions.IgnoreCase)
                if a.Success then Some (a.Groups.[1].Value.Replace(" ", "").Replace("-", ""))
                else None

    let private vendorPatterns =
        [| @"(?:From|Billed?\s+(?:by|from)|Company|Vendor|Supplier|Issued\s+by)[:\s]+(.+)"
           @"(?:^|\n)\s*([A-Z][A-Za-z0-9\s&',.\-]{2,40}(?:Pty\.?\s*Ltd\.?|Ltd\.?|Inc\.?|LLC|P/L))" |]

    let tryExtractVendor (text: string) : string option =
        if String.IsNullOrEmpty(text) then None
        else
            vendorPatterns
            |> Array.tryPick (fun pat ->
                let m = Regex.Match(text, pat, RegexOptions.IgnoreCase ||| RegexOptions.Multiline)
                if m.Success && m.Groups.Count > 1 then
                    let v = m.Groups.[1].Value.Trim()
                    if String.IsNullOrWhiteSpace(v) then None else Some v
                else None)

    // ─── Extraction result ───────────────────────────────────────────

    type ExtractionResult =
        { Text: string
          Date: string option
          Amount: decimal option
          Vendor: string option
          Abn: string option
          Method: string
          OcrConfidence: float option }

    let analyseText (text: string) (method: string) (confidence: float option) : ExtractionResult =
        { Text = text; Date = tryExtractDate text; Amount = tryExtractAmount text
          Vendor = tryExtractVendor text; Abn = tryExtractAbn text
          Method = method; OcrConfidence = confidence }

    // ─── Extract from bytes (dispatches by file type) ────────────────

    let extractFromBytes (extractor: Algebra.TextExtractor) (path: string) (bytes: byte array) =
        task {
            if isPdf path then
                let! result = extractor.extractPdf bytes
                return result |> Result.map (fun text ->
                    let method = if isLikelyScanned text then "ollama_vision" else "pdfpig"
                    let conf = if isLikelyScanned text then Some 0.7 else None
                    analyseText text method conf)
            elif isImage path then
                let! result = extractor.extractImage bytes
                return result |> Result.map (fun text -> analyseText text "ollama_vision" (Some 0.8))
            else
                return Error $"Unsupported file type: {path}"
        }

    // ─── DB update ───────────────────────────────────────────────────

    let private optVal (v: 'a option) =
        v |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value)

    let private updateRow (db: Algebra.Database) (clock: Algebra.Clock) (docId: int64) (r: ExtractionResult) =
        task {
            let! _ =
                db.execNonQuery
                    """UPDATE documents
                       SET extracted_text = @text, extracted_date = @date,
                           extracted_amount = @amount, extracted_vendor = @vendor,
                           extracted_abn = @abn, extraction_method = @method,
                           ocr_confidence = @confidence, extracted_at = @now
                       WHERE id = @id"""
                    [ ("@text", Database.boxVal r.Text)
                      ("@date", optVal r.Date)
                      ("@amount", r.Amount |> Option.map (fun d -> Database.boxVal (float d)) |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@vendor", optVal r.Vendor)
                      ("@abn", optVal r.Abn)
                      ("@method", Database.boxVal r.Method)
                      ("@confidence", r.OcrConfidence |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@now", Database.boxVal (clock.utcNow().ToString("o")))
                      ("@id", Database.boxVal docId) ]
            ()
        }

    // ─── Process single document ─────────────────────────────────────

    let processDocument
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (extractor: Algebra.TextExtractor)
        (archiveDir: string) (docId: int64) (savedPath: string) (_force: bool)
        : Task<Result<ExtractionResult, string>> =
        let fullPath =
            if IO.Path.IsPathRooted(savedPath) then savedPath
            else IO.Path.Combine(archiveDir, savedPath)
        task {
            if not (fs.fileExists fullPath) then
                return Error $"File not found: {savedPath}"
            else
            try
                let! bytes = fs.readAllBytes fullPath
                let! result = extractFromBytes extractor savedPath bytes
                match result with
                | Error e ->
                    logger.warn $"Extraction failed for doc {docId}: {e}"
                    return Error e
                | Ok extraction ->
                    do! updateRow db clock docId extraction
                    logger.debug $"Extracted doc {docId} ({extraction.Method})"
                    return Ok extraction
            with ex ->
                logger.error $"Extraction error for doc {docId}: {ex.Message}"
                return Error ex.Message
        }

    // ─── Batch extraction ────────────────────────────────────────────

    let getDocumentsForExtraction (db: Algebra.Database) (category: string option) (force: bool) (limit: int) =
        task {
            let where = if force then "1=1" else "extracted_at IS NULL"
            let catClause, catParams =
                match category with
                | Some cat -> " AND category = @cat", [ ("@cat", Database.boxVal cat) ]
                | None -> "", []
            let sql = $"SELECT id, saved_path FROM documents WHERE {where}{catClause} ORDER BY id LIMIT @lim"
            let! rows = db.execReader sql (("@lim", Database.boxVal (int64 limit)) :: catParams)
            return
                rows |> List.choose (fun row ->
                    let r = Prelude.RowReader(row)
                    match r.OptInt64 "id", r.OptString "saved_path" with
                    | Some id, Some path -> Some (id, path)
                    | _ -> None)
        }

    type BatchResult = { Succeeded: int; Failed: int }

    let private processOne fs db logger clock extractor archiveDir force (acc: BatchResult) (docId, path) =
        task {
            let! result = processDocument fs db logger clock extractor archiveDir docId path force
            return match result with
                   | Ok _ -> { acc with Succeeded = acc.Succeeded + 1 }
                   | Error _ -> { acc with Failed = acc.Failed + 1 }
        }

    let extractBatch
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (extractor: Algebra.TextExtractor)
        (archiveDir: string) (category: string option) (force: bool) (limit: int)
        : Task<int * int> =
        task {
            let! docs = getDocumentsForExtraction db category force limit
            logger.info $"Found {docs.Length} document(s) for extraction"
            let! acc =
                Prelude.foldTask
                    (processOne fs db logger clock extractor archiveDir force)
                    { Succeeded = 0; Failed = 0 }
                    docs
            logger.info $"Extraction complete: {acc.Succeeded} succeeded, {acc.Failed} failed"
            return (acc.Succeeded, acc.Failed)
        }
