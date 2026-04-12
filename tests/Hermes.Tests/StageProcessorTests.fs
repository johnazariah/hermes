/// Tests for StageProcessors: stage queue interpreters, processBatch, and runLoop.
module Hermes.Tests.StageProcessorTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Helpers ─────────────────────────────────────────────────────────

let private createDbWithStages () =
    let db = TestHelpers.createDb ()
    // Stage tables are created by initSchema (schema V6)
    db

// ─── ExtractQueue: SQLite interpreter ────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ExtractQueue_Enqueue_InsertsRow`` () =
    task {
        let db = createDbWithStages ()
        try
            // Insert a document first (FK reference)
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.extractQueue db
            do! q.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = 1L; FilePath = "test.pdf"; Attempts = 0 }
            let! count = q.count ()
            Assert.Equal(1L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ExtractQueue_Dequeue_ReturnsItems`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.extractQueue db
            do! q.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = 1L; FilePath = "path/to/file.pdf"; Attempts = 0 }
            let! items = q.dequeue 10
            Assert.Equal(1, items.Length)
            Assert.Equal(1L, items.[0].DocId)
            Assert.Equal("path/to/file.pdf", items.[0].FilePath)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ExtractQueue_Complete_RemovesItem`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.extractQueue db
            do! q.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = 1L; FilePath = "test.pdf"; Attempts = 0 }
            let! items = q.dequeue 10
            do! q.complete items.[0]
            let! count = q.count ()
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ExtractQueue_Fail_IncrementsAttempts`` () =
    task {
        let db = createDbWithStages ()
        let logger = TestHelpers.silentLogger
        let clock = TestHelpers.defaultClock
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.extractQueue db
            do! q.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = 1L; FilePath = "test.pdf"; Attempts = 0 }
            let! items = q.dequeue 10
            let! deadLettered = q.fail items.[0] logger clock "test error"
            Assert.False(deadLettered) // First attempt, not dead-lettered yet
            let! items2 = q.dequeue 10
            Assert.Equal(1, items2.[0].Attempts)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ExtractQueue_Fail_DeadLettersAfterMaxAttempts`` () =
    task {
        let db = createDbWithStages ()
        let logger = TestHelpers.silentLogger
        let clock = TestHelpers.defaultClock
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.extractQueue db
            do! q.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = 1L; FilePath = "test.pdf"; Attempts = 0 }
            // Fail 3 times (max attempts = 3)
            let! items1 = q.dequeue 10
            let! _ = q.fail items1.[0] logger clock "error 1"
            let! items2 = q.dequeue 10
            let! _ = q.fail items2.[0] logger clock "error 2"
            let! items3 = q.dequeue 10
            let! deadLettered = q.fail items3.[0] logger clock "error 3"
            Assert.True(deadLettered)
            let! count = q.count ()
            Assert.Equal(0L, count) // Removed from queue
            // Verify it's in dead_letters
            let! dlCount = db.execScalar "SELECT COUNT(*) FROM dead_letters WHERE doc_id = 1" []
            Assert.Equal(1L, dlCount :?> int64)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ExtractQueue_Dequeue_RespectsLimit`` () =
    task {
        let db = createDbWithStages ()
        try
            for i in 1..5 do
                let! _ = db.execNonQuery $"INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test{i}.pdf', 'unclassified', 'sha{i}')" []
                ()
            let q = StageProcessors.extractQueue db
            for i in 1..5 do
                do! q.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = int64 i; FilePath = $"test{i}.pdf"; Attempts = 0 }
            let! items = q.dequeue 3
            Assert.Equal(3, items.Length)
        finally db.dispose ()
    }

// ─── DocQueue (classify/embed): SQLite interpreter ───────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ClassifyQueue_Enqueue_InsertsRow`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.classifyQueue db
            do! q.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }
            let! count = q.count ()
            Assert.Equal(1L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ClassifyQueue_Dequeue_ReturnsItems`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.classifyQueue db
            do! q.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }
            let! items = q.dequeue 10
            Assert.Equal(1, items.Length)
            Assert.Equal(1L, items.[0].DocId)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ClassifyQueue_Complete_RemovesItem`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.classifyQueue db
            do! q.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }
            let! items = q.dequeue 10
            do! q.complete items.[0]
            let! count = q.count ()
            Assert.Equal(0L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmbedQueue_Enqueue_InsertsRow`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.embedQueue db
            do! q.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }
            let! count = q.count ()
            Assert.Equal(1L, count)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmbedQueue_Dequeue_ReturnsItems`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let q = StageProcessors.embedQueue db
            do! q.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }
            let! items = q.dequeue 10
            Assert.Equal(1, items.Length)
            Assert.Equal(1L, items.[0].DocId)
        finally db.dispose ()
    }

// ─── Cross-stage: extract → classify forwarding ──────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ProcessBatch_Extract_ForwardsToClassify`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let eq = StageProcessors.extractQueue db
            let cq = StageProcessors.classifyQueue db
            do! eq.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = 1L; FilePath = "test.pdf"; Attempts = 0 }

            // Process with a no-op function that always succeeds
            let processFn (_item: Algebra.ExtractItem) = Task.FromResult(Ok 1L) // returns docId
            let! processed = StageProcessors.processExtractBatch eq cq processFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, processed)

            // Verify: removed from extract, added to classify
            let! extractCount = eq.count ()
            let! classifyCount = cq.count ()
            Assert.Equal(0L, extractCount)
            Assert.Equal(1L, classifyCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ProcessBatch_Classify_ForwardsToEmbed`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let cq = StageProcessors.classifyQueue db
            let eq = StageProcessors.embedQueue db
            do! cq.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }

            let processFn (_item: Algebra.DocItem) = Task.FromResult(Ok ())
            let! processed = StageProcessors.processDocBatch cq (Some eq) processFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, processed)

            let! classifyCount = cq.count ()
            let! embedCount = eq.count ()
            Assert.Equal(0L, classifyCount)
            Assert.Equal(1L, embedCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ProcessBatch_FailedItem_RemainsInQueue`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let eq = StageProcessors.extractQueue db
            let cq = StageProcessors.classifyQueue db
            do! eq.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = 1L; FilePath = "test.pdf"; Attempts = 0 }

            let processFn (_item: Algebra.ExtractItem) = Task.FromResult(Error "extraction failed")
            let! processed = StageProcessors.processExtractBatch eq cq processFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(0, processed) // Nothing succeeded

            // Item still in extract queue with incremented attempts
            let! extractCount = eq.count ()
            Assert.Equal(1L, extractCount)
            let! items = eq.dequeue 10
            Assert.Equal(1, items.[0].Attempts)

            // Nothing forwarded to classify
            let! classifyCount = cq.count ()
            Assert.Equal(0L, classifyCount)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ProcessBatch_Embed_NoForwarding`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            let eq = StageProcessors.embedQueue db
            do! eq.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }

            let processFn (_item: Algebra.DocItem) = Task.FromResult(Ok ())
            let! processed = StageProcessors.processDocBatch eq None processFn TestHelpers.silentLogger TestHelpers.defaultClock 10
            Assert.Equal(1, processed)

            let! embedCount = eq.count ()
            Assert.Equal(0L, embedCount) // Removed after processing
        finally db.dispose ()
    }

// ─── Schema validation ──────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StageExtract_HasFilePathColumn`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            // This should work — stage_extract has file_path
            let! _ = db.execNonQuery "INSERT INTO stage_extract (doc_id, file_path) VALUES (1, 'test.pdf')" []
            let! count = db.execScalar "SELECT COUNT(*) FROM stage_extract" []
            Assert.Equal(1L, count :?> int64)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StageClassify_DoesNotHaveFilePathColumn`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            // stage_classify should only have doc_id — no file_path
            let! _ = db.execNonQuery "INSERT INTO stage_classify (doc_id) VALUES (1)" []
            let! count = db.execScalar "SELECT COUNT(*) FROM stage_classify" []
            Assert.Equal(1L, count :?> int64)
        finally db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``StageEmbed_DoesNotHaveFilePathColumn`` () =
    task {
        let db = createDbWithStages ()
        try
            let! _ = db.execNonQuery "INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test.pdf', 'unclassified', 'sha1')" []
            // stage_embed should only have doc_id — no file_path
            let! _ = db.execNonQuery "INSERT INTO stage_embed (doc_id) VALUES (1)" []
            let! count = db.execScalar "SELECT COUNT(*) FROM stage_embed" []
            Assert.Equal(1L, count :?> int64)
        finally db.dispose ()
    }

// ─── Count accuracy ──────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``QueueCounts_MatchStatsApi`` () =
    task {
        let db = createDbWithStages ()
        try
            for i in 1..5 do
                let! _ = db.execNonQuery $"INSERT INTO documents (source_type, saved_path, category, sha256) VALUES ('test', 'test{i}.pdf', 'unclassified', 'sha{i}')" []
                ()
            let eq = StageProcessors.extractQueue db
            let cq = StageProcessors.classifyQueue db
            let ebq = StageProcessors.embedQueue db
            // Enqueue different counts
            for i in 1L..3L do do! eq.enqueue { Algebra.ExtractItem.QueueId = 0L; Algebra.ExtractItem.DocId = i; FilePath = $"test{i}.pdf"; Attempts = 0 }
            for i in 1L..2L do do! cq.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = i; Algebra.DocItem.Attempts = 0 }
            do! ebq.enqueue { Algebra.DocItem.QueueId = 0L; Algebra.DocItem.DocId = 1L; Algebra.DocItem.Attempts = 0 }

            let! extractCount = eq.count ()
            let! classifyCount = cq.count ()
            let! embedCount = ebq.count ()
            Assert.Equal(3L, extractCount)
            Assert.Equal(2L, classifyCount)
            Assert.Equal(1L, embedCount)
        finally db.dispose ()
    }
