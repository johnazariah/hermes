namespace Hermes.Core

open System
open System.IO
open System.Threading.Channels

/// Pipeline Stage 1: Classify files in unclassified/ and write doc IDs to the extract channel.
/// Also handles Tier 2 (content rules) and Tier 3 (LLM) reclassification of unsorted docs.
[<RequireQualifiedAccess>]
module ClassifyStage =

    /// Classify all files in unclassified/, writing new doc IDs to the extract channel.
    let classifyNew
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (clock: Algebra.Clock) (rules: Algebra.RulesEngine) (archiveDir: string)
        (extractChannel: ChannelWriter<int64>) =
        task {
            let dir = Path.Combine(archiveDir, "unclassified")
            if fs.directoryExists dir then
                let files =
                    fs.getFiles dir "*"
                    |> Array.filter (fun f -> not (f.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
                for file in files do
                    let! result = Classifier.processFile fs db logger clock rules archiveDir file
                    match result with
                    | Ok (Some docId) -> do! extractChannel.WriteAsync(docId)
                    | _ -> ()
        }

    /// Reclassify unsorted documents using Tier 2 (content rules) and Tier 3 (LLM).
    let reclassifyUnsorted
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (chatProvider: Algebra.ChatProvider option) (contentRules: Domain.ContentRule list)
        (archiveDir: string) =
        task {
            // Tier 2: content rules reclassification
            let! reclassified, remaining =
                Classifier.reclassifyUnsortedBatch db fs contentRules archiveDir 50
            if reclassified > 0 then
                logger.info $"Tier 2 reclassified {reclassified} documents ({remaining} still unsorted)"

            // Tier 3: LLM classification for remaining unsorted
            match chatProvider with
            | Some provider when remaining > 0 ->
                try
                    let! unsortedRows =
                        db.execReader
                            """SELECT id, extracted_text FROM documents
                               WHERE (category = 'unsorted' OR category = 'unclassified')
                                 AND extracted_text IS NOT NULL
                               ORDER BY id ASC LIMIT 50"""
                            []
                    let! catRows =
                        db.execReader "SELECT DISTINCT category FROM documents WHERE category NOT IN ('unsorted','unclassified')" []
                    let dbCategories =
                        catRows |> List.choose (fun r -> Prelude.RowReader(r).OptString "category")
                    // Seed with standard categories so LLM can use them even before any docs exist there
                    let seedCategories =
                        [ "invoices"; "bank-statements"; "receipts"; "tax"; "payslips"
                          "insurance"; "real-estate"; "travel"; "medical"; "utilities"
                          "legal"; "donations"; "contracts"; "correspondence" ]
                    let categories = (dbCategories @ seedCategories) |> List.distinct
                    let mutable llmClassified = 0
                    for row in unsortedRows do
                        let r = Prelude.RowReader(row)
                        match r.OptInt64 "id", r.OptString "extracted_text" with
                        | Some docId, Some text ->
                            let prompt = ContentClassifier.buildClassificationPrompt text categories
                            let! llmResult = provider.complete "You are a document classifier." prompt
                            match llmResult with
                            | Ok response ->
                                match ContentClassifier.parseClassificationResponse response with
                                | Some (cat, conf, reasoning) when conf >= 0.4 ->
                                    let! moveResult = DocumentManagement.reclassify db fs archiveDir docId cat
                                    match moveResult with
                                    | Ok () ->
                                        let tier = if conf >= 0.7 then "llm" else "llm_review"
                                        let! _ =
                                            db.execNonQuery
                                                """UPDATE documents SET classification_tier = @tier,
                                                   classification_confidence = @conf WHERE id = @id"""
                                                [ ("@tier", Database.boxVal tier)
                                                  ("@conf", Database.boxVal conf)
                                                  ("@id", Database.boxVal docId) ]
                                        llmClassified <- llmClassified + 1
                                        if conf < 0.7 then
                                            logger.warn $"LLM classified doc {docId} as {cat} (conf={conf:F2}, needs review): {reasoning}"
                                        else
                                            logger.info $"LLM classified doc {docId} as {cat} (conf={conf:F2}): {reasoning}"
                                    | Error e -> logger.warn $"LLM reclassify move failed for doc {docId}: {e}"
                                | _ -> ()
                            | Error e ->
                                let docIdStr = r.OptInt64 "id" |> Option.map string |> Option.defaultValue "?"
                                logger.warn $"LLM classification failed for doc {docIdStr}: {e}"
                        | _ -> ()
                    if llmClassified > 0 then
                        logger.info $"Tier 3 LLM classified {llmClassified} documents"
                with ex ->
                    logger.debug $"LLM classification skipped: {ex.Message}"
            | _ -> ()
        }
