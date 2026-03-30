namespace Hermes.Core

open System
open System.Text.RegularExpressions
open System.Threading.Tasks
open UglyToad.PdfPig

/// Text extraction pipeline: PDF text via PdfPig, regex heuristics for
/// structured fields (date, amount, vendor, ABN), Ollama vision fallback
/// for scanned documents. Parameterised over algebras for testability.
[<RequireQualifiedAccess>]
module Extraction =

    // ─── Scanned PDF detection ───────────────────────────────────────

    /// Threshold below which a PDF page is considered scanned (image-only).
    let [<Literal>] ScannedCharThreshold = 50

    /// Returns true if the extracted text has fewer than the threshold characters.
    let isLikelyScanned (text: string) =
        text.Trim().Length < ScannedCharThreshold

    // ─── Native PDF extraction via PdfPig ────────────────────────────

    /// Extract text from a PDF byte array using PdfPig.
    let extractPdfText (pdfBytes: byte[]) : Result<string, string> =
        try
            use doc = PdfDocument.Open(pdfBytes)
            let text =
                doc.GetPages()
                |> Seq.map (fun page -> page.Text)
                |> Seq.choose (fun t -> if String.IsNullOrEmpty(t) then None else Some t)
                |> String.concat "\n"
            Ok text
        with ex ->
            Error (sprintf "PDF extraction failed: %s" ex.Message)

    // ─── Regex heuristics for field extraction ───────────────────────

    /// Date patterns commonly found in Australian documents.
    let private datePatterns =
        [| // DD/MM/YYYY or DD-MM-YYYY
           @"\b(\d{1,2})[/\-](\d{1,2})[/\-](\d{4})\b"
           // YYYY-MM-DD (ISO)
           @"\b(\d{4})-(\d{2})-(\d{2})\b"
           // DD Mon YYYY or DD Month YYYY
           @"\b(\d{1,2})\s+(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+(\d{4})\b" |]

    /// Try to extract the first date from text.
    let tryExtractDate (text: string) : string option =
        if String.IsNullOrEmpty(text) then
            None
        else
            datePatterns
            |> Array.tryPick (fun pattern ->
                let m = Regex.Match(text, pattern, RegexOptions.IgnoreCase)
                if m.Success then Some m.Value else None)

    /// Amount patterns: $X,XXX.XX or AUD X,XXX.XX or numeric with dollar sign.
    let private amountPattern =
        @"(?:\$|AUD\s*)\s*(\d{1,3}(?:,\d{3})*(?:\.\d{2})?)"

    /// Try to extract the first monetary amount from text.
    let tryExtractAmount (text: string) : decimal option =
        if String.IsNullOrEmpty(text) then
            None
        else
            let m = Regex.Match(text, amountPattern, RegexOptions.IgnoreCase)
            if m.Success then
                let raw = m.Groups.[1].Value.Replace(",", "")
                match Decimal.TryParse(raw) with
                | true, d -> Some d
                | _ -> None
            else
                None

    /// ABN pattern: 11-digit number with optional spaces/hyphens.
    let private abnPattern =
        @"\bABN[:\s]*(\d{2}\s?\d{3}\s?\d{3}\s?\d{3})\b"

    /// ACN pattern: 9-digit number.
    let private acnPattern =
        @"\bACN[:\s]*(\d{3}\s?\d{3}\s?\d{3})\b"

    /// Try to extract ABN or ACN from text.
    let tryExtractAbn (text: string) : string option =
        if String.IsNullOrEmpty(text) then
            None
        else
            let abnMatch = Regex.Match(text, abnPattern, RegexOptions.IgnoreCase)
            if abnMatch.Success then
                Some (abnMatch.Groups.[1].Value.Replace(" ", "").Replace("-", ""))
            else
                let acnMatch = Regex.Match(text, acnPattern, RegexOptions.IgnoreCase)
                if acnMatch.Success then
                    Some (acnMatch.Groups.[1].Value.Replace(" ", "").Replace("-", ""))
                else
                    None

    /// Common vendor label patterns in invoices / receipts.
    let private vendorPatterns =
        [| @"(?:From|Billed?\s+(?:by|from)|Company|Vendor|Supplier|Issued\s+by)[:\s]+(.+)"
           @"(?:^|\n)\s*([A-Z][A-Za-z0-9\s&',.\-]{2,40}(?:Pty\.?\s*Ltd\.?|Ltd\.?|Inc\.?|LLC|P/L))" |]

    /// Try to extract vendor/company name from text.
    let tryExtractVendor (text: string) : string option =
        if String.IsNullOrEmpty(text) then
            None
        else
            vendorPatterns
            |> Array.tryPick (fun pattern ->
                let m = Regex.Match(text, pattern, RegexOptions.IgnoreCase ||| RegexOptions.Multiline)
                if m.Success && m.Groups.Count > 1 then
                    let value = m.Groups.[1].Value.Trim()
                    if String.IsNullOrWhiteSpace(value) then None
                    else Some value
                else
                    None)

    // ─── Extraction result ───────────────────────────────────────────

    /// Fields extracted from document text.
    type ExtractionResult =
        { Text: string
          Date: string option
          Amount: decimal option
          Vendor: string option
          Abn: string option
          Method: string
          OcrConfidence: float option }

    /// Run regex heuristics on extracted text.
    let analyseText (text: string) (method: string) (confidence: float option) : ExtractionResult =
        { Text = text
          Date = tryExtractDate text
          Amount = tryExtractAmount text
          Vendor = tryExtractVendor text
          Abn = tryExtractAbn text
          Method = method
          OcrConfidence = confidence }

    // ─── PdfPig-based TextExtractor interpreter ──────────────────────

    /// Build a TextExtractor algebra using PdfPig for PDFs and
    /// Ollama vision for images.
    let createTextExtractor (ollama: Algebra.OllamaClient) (visionModel: string) : Algebra.TextExtractor =
        { extractPdf =
            fun pdfBytes ->
                task {
                    match extractPdfText pdfBytes with
                    | Ok text when isLikelyScanned text ->
                        // Fallback to Ollama vision for scanned PDFs
                        let! available = ollama.isAvailable ()
                        if available then
                            let prompt = "Extract all text from this document image. Return only the text content."
                            let! ocrResult = ollama.generate visionModel prompt (Some pdfBytes)
                            return ocrResult
                        else
                            return Ok text
                    | Ok text -> return Ok text
                    | Error e -> return Error e
                }
          extractImage =
            fun imageBytes ->
                task {
                    let! available = ollama.isAvailable ()
                    if available then
                        let prompt = "Extract all text from this image. Return only the text content."
                        let! result = ollama.generate visionModel prompt (Some imageBytes)
                        return result
                    else
                        return Error "Ollama is not available for image OCR"
                } }

    // ─── Document processing ─────────────────────────────────────────

    /// Determine if a file is a PDF based on extension.
    let isPdf (filePath: string) =
        filePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)

    /// Determine if a file is an image based on extension.
    let isImage (filePath: string) =
        let exts = [| ".png"; ".jpg"; ".jpeg"; ".tiff"; ".tif"; ".bmp"; ".gif"; ".webp" |]
        exts |> Array.exists (fun ext -> filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase))

    /// Extract text from file bytes based on file type.
    let private extractFromBytes
        (extractor: Algebra.TextExtractor)
        (savedPath: string)
        (bytes: byte array)
        : Task<Result<ExtractionResult, string>> =
        task {
            if isPdf savedPath then
                let! result = extractor.extractPdf bytes
                match result with
                | Ok text ->
                    let method = if isLikelyScanned text then "ollama_vision" else "pdfpig"
                    let confidence = if isLikelyScanned text then Some 0.7 else None
                    return Ok (analyseText text method confidence)
                | Error e -> return Error e
            elif isImage savedPath then
                let! result = extractor.extractImage bytes
                match result with
                | Ok text -> return Ok (analyseText text "ollama_vision" (Some 0.8))
                | Error e -> return Error e
            else
                return Error $"Unsupported file type: {savedPath}"
        }

    /// Update the documents row with extraction results.
    let private updateDocumentRow
        (db: Algebra.Database)
        (clock: Algebra.Clock)
        (docId: int64)
        (result: ExtractionResult)
        : Task<unit> =
        task {
            let now = clock.utcNow().ToString("o")
            let! _ =
                db.execNonQuery
                    """UPDATE documents
                       SET extracted_text = @text, extracted_date = @date,
                           extracted_amount = @amount, extracted_vendor = @vendor,
                           extracted_abn = @abn, extraction_method = @method,
                           ocr_confidence = @confidence, extracted_at = @now
                       WHERE id = @id"""
                    [ ("@text", Database.boxVal result.Text)
                      ("@date", result.Date |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@amount", result.Amount |> Option.map (fun d -> Database.boxVal (float d)) |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@vendor", result.Vendor |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@abn", result.Abn |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@method", Database.boxVal result.Method)
                      ("@confidence", result.OcrConfidence |> Option.map Database.boxVal |> Option.defaultValue (Database.boxVal DBNull.Value))
                      ("@now", Database.boxVal now)
                      ("@id", Database.boxVal docId) ]
            ()
        }

    /// Process a single document: extract text, parse fields, update DB row.
    let processDocument
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (extractor: Algebra.TextExtractor)
        (archiveDir: string)
        (docId: int64)
        (savedPath: string)
        (_force: bool)
        =
        task {
            let fullPath =
                if System.IO.Path.IsPathRooted(savedPath) then savedPath
                else System.IO.Path.Combine(archiveDir, savedPath)

            if not (fs.fileExists fullPath) then
                logger.warn $"File not found, skipping extraction: {savedPath}"
                return Error $"File not found: {savedPath}"
            else

            try
                let! bytes = fs.readAllBytes fullPath
                let! extractionResult = extractFromBytes extractor savedPath bytes

                match extractionResult with
                | Error e ->
                    logger.warn $"Extraction failed for doc {docId}: {e}"
                    return Error e
                | Ok result ->
                    do! updateDocumentRow db clock docId result

                    let dateStr = result.Date |> Option.defaultValue "none"
                    let amountStr = result.Amount |> Option.map string |> Option.defaultValue "none"
                    logger.info $"Extracted doc {docId} ({result.Method}): date={dateStr}, amount={amountStr}"
                    return Ok result
            with ex ->
                let msg = $"Extraction error for doc {docId}: {ex.Message}"
                logger.error msg
                return Error msg
        }

    // ─── Batch extraction ────────────────────────────────────────────

    /// Query documents needing extraction, optionally filtered by category.
    let getDocumentsForExtraction
        (db: Algebra.Database)
        (category: string option)
        (force: bool)
        (limit: int)
        =
        task {
            let baseSql =
                if force then
                    "SELECT id FROM documents WHERE 1=1"
                else
                    "SELECT id FROM documents WHERE extracted_at IS NULL"

            let catFilter, catParams =
                match category with
                | Some cat ->
                    " AND category = @cat", [ ("@cat", Database.boxVal cat) ]
                | None -> "", []

            let results = ResizeArray<int64 * string>()

            for i in 0 .. limit - 1 do
                let offsetSql = sprintf "%s%s ORDER BY id LIMIT 1 OFFSET %d" baseSql catFilter i
                let! row = db.execScalar offsetSql catParams

                match row with
                | null -> ()
                | v ->
                    let docId = v :?> int64
                    let! pathResult =
                        db.execScalar
                            "SELECT saved_path FROM documents WHERE id = @id"
                            [ ("@id", Database.boxVal docId) ]
                    match pathResult with
                    | null -> ()
                    | p ->
                        match p with
                        | :? string as s -> results.Add(docId, s)
                        | _ -> ()

            return results |> Seq.toList
        }

    /// Run extraction on a batch of documents.
    let extractBatch
        (fs: Algebra.FileSystem)
        (db: Algebra.Database)
        (logger: Algebra.Logger)
        (clock: Algebra.Clock)
        (extractor: Algebra.TextExtractor)
        (archiveDir: string)
        (category: string option)
        (force: bool)
        (limit: int)
        =
        task {
            let! docs = getDocumentsForExtraction db category force limit
            logger.info $"Found {docs.Length} document(s) for extraction"

            let mutable successCount = 0
            let mutable errorCount = 0

            for (docId, savedPath) in docs do
                let! result = processDocument fs db logger clock extractor archiveDir docId savedPath force

                match result with
                | Ok _ -> successCount <- successCount + 1
                | Error _ -> errorCount <- errorCount + 1

            logger.info $"Extraction complete: {successCount} succeeded, {errorCount} failed"
            return (successCount, errorCount)
        }
