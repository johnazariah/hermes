/// Integration tests for Pipeline.fs — verifies documents flow through all stages.
/// Uses in-memory SQLite + fake algebras. No external services.
module Hermes.Tests.PipelineTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

let private makeTestDeps
    (extractor: Algebra.TextExtractor)
    (chat: Algebra.ChatProvider option)
    (embedder: Algebra.EmbeddingClient option)
    : Pipeline.Deps =
    { Extractor = extractor
      Embedder = embedder
      ChatProvider = chat
      ContentRules = []
      CreateEmailProvider = fun _ _ -> task { return TestHelpers.emptyProvider } }

let private successExtractor : Algebra.TextExtractor =
    { extractPdf = fun bytes ->
        task { return Ok (System.Text.Encoding.UTF8.GetString(bytes)) }
      extractImage = fun _ ->
        task { return Error "not supported" } }

let private minimalRules : Algebra.RulesEngine =
    { classify = fun _ _ ->
        { Domain.ClassificationResult.Category = "unclassified"
          MatchedRule = Domain.ClassificationRule.DefaultRule }
      reload = fun () -> task { return Ok () } }

/// Insert a document row + file on disk + enqueue to stage_extract.
/// Returns the doc ID.
let private seedDocument (db: Algebra.Database) (m: TestHelpers.MemFs) (archiveDir: string) (fileName: string) (content: string) =
    task {
        let filePath = $"{archiveDir}/unclassified/{fileName}"
        m.Fs.createDirectory $"{archiveDir}/unclassified"
        m.Put filePath content

        let! _ =
            db.execNonQuery
                """INSERT INTO documents (source_type, saved_path, category, sha256, original_name)
                   VALUES ('test', @path, 'unclassified', @sha, @name)"""
                [ ("@path", Database.boxVal $"unclassified/{fileName}")
                  ("@sha", Database.boxVal (Guid.NewGuid().ToString("N")))
                  ("@name", Database.boxVal fileName) ]
        let! idObj = db.execScalar "SELECT last_insert_rowid()" []
        let docId = match idObj with :? int64 as i -> i | _ -> 0L

        let eq = StageProcessors.extractQueue db
        do! eq.enqueue
                { Algebra.ExtractItem.QueueId = 0L
                  Algebra.ExtractItem.DocId = docId
                  Algebra.ExtractItem.FilePath = $"unclassified/{fileName}"
                  Algebra.ExtractItem.Attempts = 0 }
        return docId
    }

// ─── Stage queue integration ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Pipeline_ExtractStage_ProcessesDocument`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let archiveDir = "/archive"
        try
            let! docId = seedDocument db m archiveDir "test.txt" "Hello World"
            let eq = StageProcessors.extractQueue db
            let cq = StageProcessors.classifyQueue db

            // Run one extract batch
            let extractFn (item: Algebra.ExtractItem) =
                task {
                    let! result = Extraction.processDocument m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock successExtractor archiveDir item.DocId item.FilePath false
                    return result |> Result.map (fun _ -> item.DocId)
                }
            let! processed = StageProcessors.processExtractBatch eq cq extractFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, processed)

            // Verify: extracted text populated, forwarded to classify
            let! textObj = db.execScalar "SELECT extracted_text FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
            Assert.NotNull(textObj)
            let text = string textObj
            Assert.Contains("Hello World", text)

            let! classifyCount = cq.count ()
            Assert.Equal(1L, classifyCount)

            let! extractCount = eq.count ()
            Assert.Equal(0L, extractCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Pipeline_ClassifyStage_ClassifiesDocument`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let archiveDir = "/archive"
        try
            // Seed a document that's already extracted (in classify queue)
            m.Fs.createDirectory $"{archiveDir}/unclassified"
            m.Put $"{archiveDir}/unclassified/invoice.pdf" "INVOICE Total $500 from ACME Corp"

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text, extracted_at)
                       VALUES ('test', 'unclassified/invoice.pdf', 'unclassified', 'sha-inv', 'invoice.pdf', 'INVOICE Total $500 from ACME Corp', datetime('now'))"""
                    []
            let! idObj = db.execScalar "SELECT last_insert_rowid()" []
            let docId = match idObj with :? int64 as i -> i | _ -> 0L

            let cq = StageProcessors.classifyQueue db
            let eq = StageProcessors.embedQueue db
            do! cq.enqueue { Algebra.DocItem.QueueId = 0L; DocId = docId; Attempts = 0 }

            // Classify with a chat provider that returns "invoices"
            let chat = TestHelpers.fakeChatProvider """{"category":"invoices","confidence":0.92,"reasoning":"Contains invoice markers"}"""

            m.Fs.createDirectory $"{archiveDir}/invoices"
            let classifyFn (item: Algebra.DocItem) =
                task {
                    let! docRows = db.execReader "SELECT category, extracted_text FROM documents WHERE id = @id" [ ("@id", Database.boxVal item.DocId) ]
                    match docRows |> List.tryHead with
                    | Some docRow ->
                        let dr = Prelude.RowReader(docRow)
                        let cat = dr.String "category" ""
                        let text = dr.OptString "extracted_text"
                        if (cat = "unsorted" || cat = "unclassified") && text.IsSome then
                            let! catRows = db.execReader "SELECT DISTINCT category FROM documents WHERE category NOT IN ('unsorted','unclassified') LIMIT 50" []
                            let categories = catRows |> List.choose (fun r2 -> Prelude.RowReader(r2).OptString "category")
                            let seedCats = [ "invoices"; "bank-statements"; "receipts"; "tax" ]
                            let allCats = (categories @ seedCats) |> List.distinct
                            let prompt = ContentClassifier.buildClassificationPrompt text.Value allCats
                            let! llmResult = chat.complete "You are a document classifier." prompt
                            match llmResult with
                            | Ok response ->
                                match ContentClassifier.parseClassificationResponse response with
                                | Some (newCat, conf, _) when conf >= 0.4 ->
                                    let! moveResult = DocumentManagement.reclassify db m.Fs archiveDir item.DocId newCat
                                    match moveResult with
                                    | Ok () ->
                                        let tier = if conf >= 0.7 then "llm" else "llm_review"
                                        let! _ = db.execNonQuery "UPDATE documents SET classification_tier = @tier, classification_confidence = @conf WHERE id = @id" [ ("@tier", Database.boxVal tier); ("@conf", Database.boxVal conf); ("@id", Database.boxVal item.DocId) ]
                                        ()
                                    | Error _ -> ()
                                | _ -> ()
                            | Error _ -> ()
                        return Ok ()
                    | None -> return Ok ()
                }
            let! processed = StageProcessors.processDocBatch cq (Some eq) classifyFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, processed)

            // Verify: category updated, forwarded to embed
            let! catObj = db.execScalar "SELECT category FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
            Assert.Equal("invoices", string catObj)

            let! embedCount = eq.count ()
            Assert.Equal(1L, embedCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Pipeline_EmbedStage_EmbedsDocument`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            // Seed a document that's extracted and classified (in embed queue)
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name, extracted_text, extracted_at)
                       VALUES ('test', 'invoices/invoice.pdf', 'invoices', 'sha-emb', 'invoice.pdf', 'INVOICE Total $500 from ACME Corp', datetime('now'))"""
                    []
            let! idObj = db.execScalar "SELECT last_insert_rowid()" []
            let docId = match idObj with :? int64 as i -> i | _ -> 0L

            let eq = StageProcessors.embedQueue db
            do! eq.enqueue { Algebra.DocItem.QueueId = 0L; DocId = docId; Attempts = 0 }

            // Embed with fake embedder
            let embedder = TestHelpers.fakeEmbedder 384

            let embedFn (item: Algebra.DocItem) =
                task {
                    let! _ = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock embedder false (Some 1) None
                    return Ok ()
                }
            let! processed = StageProcessors.processDocBatch eq None embedFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, processed)

            // Verify: embedded_at set
            let! embObj = db.execScalar "SELECT embedded_at FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
            Assert.NotNull(embObj)
            Assert.IsType<string>(embObj) |> ignore
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Pipeline_FullFlow_ExtractClassifyEmbed`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let archiveDir = "/archive"
        try
            // Seed document
            let! docId = seedDocument db m archiveDir "receipt.txt" "RECEIPT Shop: Woolworths Amount: $42.50"

            let eq = StageProcessors.extractQueue db
            let cq = StageProcessors.classifyQueue db
            let embq = StageProcessors.embedQueue db

            // Stage 1: Extract
            let extractFn (item: Algebra.ExtractItem) =
                task {
                    let! result = Extraction.processDocument m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock successExtractor archiveDir item.DocId item.FilePath false
                    return result |> Result.map (fun _ -> item.DocId)
                }
            let! extracted = StageProcessors.processExtractBatch eq cq extractFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, extracted)

            // Stage 2: Classify (skip LLM — just forward to embed)
            let classifyFn (_item: Algebra.DocItem) = Task.FromResult(Ok ())
            let! classified = StageProcessors.processDocBatch cq (Some embq) classifyFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, classified)

            // Stage 3: Embed
            let embedder = TestHelpers.fakeEmbedder 384
            let embedFn (_item: Algebra.DocItem) =
                task {
                    let! _ = Embeddings.batchEmbed db TestHelpers.silentLogger TestHelpers.defaultClock embedder false (Some 1) None
                    return Ok ()
                }
            let! embedded = StageProcessors.processDocBatch embq None embedFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, embedded)

            // Verify final state
            let! row = db.execReader "SELECT extracted_text, extracted_at, embedded_at FROM documents WHERE id = @id" [ ("@id", Database.boxVal docId) ]
            let r = Prelude.RowReader(row.[0])
            Assert.True(r.OptString "extracted_text" |> Option.isSome, "Should have extracted text")
            Assert.True(r.OptString "extracted_at" |> Option.isSome, "Should have extracted_at timestamp")
            Assert.True(r.OptString "embedded_at" |> Option.isSome, "Should have embedded_at timestamp")

            // All queues empty
            let! eqc = eq.count ()
            let! cqc = cq.count ()
            let! embqc = embq.count ()
            Assert.Equal(0L, eqc)
            Assert.Equal(0L, cqc)
            Assert.Equal(0L, embqc)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Pipeline_ExtractFailure_RetriesAndDeadLetters`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let archiveDir = "/archive"
        try
            // Seed document with NO file on disk — extraction will fail
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents (source_type, saved_path, category, sha256, original_name)
                       VALUES ('test', 'unclassified/missing.pdf', 'unclassified', 'sha-miss', 'missing.pdf')"""
                    []
            let! idObj = db.execScalar "SELECT last_insert_rowid()" []
            let docId = match idObj with :? int64 as i -> i | _ -> 0L

            let eq = StageProcessors.extractQueue db
            let cq = StageProcessors.classifyQueue db
            do! eq.enqueue
                    { Algebra.ExtractItem.QueueId = 0L; DocId = docId
                      FilePath = "unclassified/missing.pdf"; Attempts = 0 }

            let extractFn (item: Algebra.ExtractItem) =
                task {
                    let! result = Extraction.processDocument m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock successExtractor archiveDir item.DocId item.FilePath false
                    return result |> Result.map (fun _ -> item.DocId)
                }

            // Process 3 times — should dead-letter after 3rd
            for _ in 1..3 do
                let! _ = StageProcessors.processExtractBatch eq cq extractFn TestHelpers.silentLogger TestHelpers.defaultClock 10
                ()

            // Verify: removed from extract queue, in dead letters
            let! eqc = eq.count ()
            Assert.Equal(0L, eqc)

            let! dlCount = db.execScalar "SELECT COUNT(*) FROM dead_letters WHERE doc_id = @id" [ ("@id", Database.boxVal docId) ]
            Assert.Equal(1L, dlCount :?> int64)

            // Not forwarded to classify
            let! cqc = cq.count ()
            Assert.Equal(0L, cqc)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Pipeline_QueueCounts_AccurateAtEachStage`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        let archiveDir = "/archive"
        try
            // Seed 3 documents
            for i in 1..3 do
                let! _ = seedDocument db m archiveDir $"doc{i}.txt" $"Content of document {i}"
                ()

            let eq = StageProcessors.extractQueue db
            let cq = StageProcessors.classifyQueue db
            let embq = StageProcessors.embedQueue db

            // All 3 in extract queue
            let! eqc0 = eq.count ()
            Assert.Equal(3L, eqc0)

            // Extract 2 of 3
            let extractFn (item: Algebra.ExtractItem) =
                task {
                    let! result = Extraction.processDocument m.Fs db TestHelpers.silentLogger TestHelpers.defaultClock successExtractor archiveDir item.DocId item.FilePath false
                    return result |> Result.map (fun _ -> item.DocId)
                }
            let! _ = StageProcessors.processExtractBatch eq cq extractFn TestHelpers.silentLogger TestHelpers.defaultClock 2

            // 1 left in extract, 2 in classify
            let! eqc1 = eq.count ()
            let! cqc1 = cq.count ()
            Assert.Equal(1L, eqc1)
            Assert.Equal(2L, cqc1)

            // Classify all
            let classifyFn (_: Algebra.DocItem) = Task.FromResult(Ok ())
            let! _ = StageProcessors.processDocBatch cq (Some embq) classifyFn TestHelpers.silentLogger TestHelpers.defaultClock 10

            // 0 in classify, 2 in embed
            let! cqc2 = cq.count ()
            let! embqc2 = embq.count ()
            Assert.Equal(0L, cqc2)
            Assert.Equal(2L, embqc2)
        finally db.dispose ()
    }
