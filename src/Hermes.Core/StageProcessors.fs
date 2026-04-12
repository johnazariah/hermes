namespace Hermes.Core

open System
open System.Threading
open System.Threading.Tasks

/// Stage queue processors with typed interpreters per stage.
/// Extract stage uses ExtractItem (has file path).
/// Classify and embed stages use DocItem (doc ID only).
[<RequireQualifiedAccess>]
module StageProcessors =

    let private boxVal = Database.boxVal
    let private maxAttempts = 3

    // ─── Extract queue (has file_path) ────────────────────────────

    /// SQLite-backed extract queue. Items have DocId + FilePath.
    let extractQueue (db: Algebra.Database) : Algebra.StageQueue<Algebra.ExtractItem> =
        { dequeue = fun batchSize ->
            task {
                let! rows =
                    db.execReader
                        "SELECT id, doc_id, file_path, attempts FROM stage_extract ORDER BY created_at LIMIT @lim"
                        [ ("@lim", boxVal (int64 batchSize)) ]
                return
                    rows |> List.choose (fun row ->
                        let r = Prelude.RowReader(row)
                        match r.OptInt64 "id", r.OptInt64 "doc_id" with
                        | Some qid, Some did ->
                            Some
                                { Algebra.ExtractItem.QueueId = qid
                                  Algebra.ExtractItem.DocId = did
                                  Algebra.ExtractItem.FilePath = r.String "file_path" ""
                                  Algebra.ExtractItem.Attempts = r.Int64 "attempts" 0L |> int }
                        | _ -> None)
            }

          complete = fun item ->
            task {
                let! _ = db.execNonQuery "DELETE FROM stage_extract WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                ()
            }

          fail = fun item (logger: Algebra.Logger) (clock: Algebra.Clock) error ->
            task {
                if item.Attempts + 1 >= maxAttempts then
                    let! nameObj = db.execScalar "SELECT COALESCE(original_name, saved_path) FROM documents WHERE id = @id" [ ("@id", boxVal item.DocId) ]
                    let name = match nameObj with :? string as s -> s | _ -> "unknown"
                    let! _ =
                        db.execNonQuery
                            """INSERT INTO dead_letters (doc_id, stage, error, failed_at, original_name)
                               VALUES (@doc, 'extract', @err, @now, @name)"""
                            [ ("@doc", boxVal item.DocId); ("@err", boxVal error)
                              ("@now", boxVal (clock.utcNow().ToString("o")))
                              ("@name", boxVal name) ]
                    let! _ = db.execNonQuery "DELETE FROM stage_extract WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                    logger.warn $"Dead-lettered doc {item.DocId} from extract: {error}"
                    return true
                else
                    let! _ = db.execNonQuery "UPDATE stage_extract SET attempts = attempts + 1 WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                    return false
            }

          enqueue = fun item ->
            task {
                let! _ =
                    db.execNonQuery
                        "INSERT OR IGNORE INTO stage_extract (doc_id, file_path) VALUES (@doc, @path)"
                        [ ("@doc", boxVal item.DocId); ("@path", boxVal item.FilePath) ]
                ()
            }

          count = fun () ->
            task {
                let! result = db.execScalar "SELECT COUNT(*) FROM stage_extract" []
                return match result with :? int64 as i -> i | _ -> 0L
            }
        }

    // ─── Classify queue (doc_id only) ─────────────────────────────

    /// SQLite-backed classify queue. Items have DocId only.
    let classifyQueue (db: Algebra.Database) : Algebra.StageQueue<Algebra.DocItem> =
        { dequeue = fun batchSize ->
            task {
                let! rows =
                    db.execReader
                        "SELECT id, doc_id, attempts FROM stage_classify ORDER BY created_at LIMIT @lim"
                        [ ("@lim", boxVal (int64 batchSize)) ]
                return
                    rows |> List.choose (fun row ->
                        let r = Prelude.RowReader(row)
                        match r.OptInt64 "id", r.OptInt64 "doc_id" with
                        | Some qid, Some did ->
                            Some
                                { Algebra.DocItem.QueueId = qid
                                  Algebra.DocItem.DocId = did
                                  Algebra.DocItem.Attempts = r.Int64 "attempts" 0L |> int }
                        | _ -> None)
            }

          complete = fun item ->
            task {
                let! _ = db.execNonQuery "DELETE FROM stage_classify WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                ()
            }

          fail = fun item (logger: Algebra.Logger) (clock: Algebra.Clock) error ->
            task {
                if item.Attempts + 1 >= maxAttempts then
                    let! nameObj = db.execScalar "SELECT COALESCE(original_name, saved_path) FROM documents WHERE id = @id" [ ("@id", boxVal item.DocId) ]
                    let name = match nameObj with :? string as s -> s | _ -> "unknown"
                    let! _ =
                        db.execNonQuery
                            """INSERT INTO dead_letters (doc_id, stage, error, failed_at, original_name)
                               VALUES (@doc, 'classify', @err, @now, @name)"""
                            [ ("@doc", boxVal item.DocId); ("@err", boxVal error)
                              ("@now", boxVal (clock.utcNow().ToString("o")))
                              ("@name", boxVal name) ]
                    let! _ = db.execNonQuery "DELETE FROM stage_classify WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                    logger.warn $"Dead-lettered doc {item.DocId} from classify: {error}"
                    return true
                else
                    let! _ = db.execNonQuery "UPDATE stage_classify SET attempts = attempts + 1 WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                    return false
            }

          enqueue = fun item ->
            task {
                let! _ =
                    db.execNonQuery
                        "INSERT OR IGNORE INTO stage_classify (doc_id) VALUES (@doc)"
                        [ ("@doc", boxVal item.DocId) ]
                ()
            }

          count = fun () ->
            task {
                let! result = db.execScalar "SELECT COUNT(*) FROM stage_classify" []
                return match result with :? int64 as i -> i | _ -> 0L
            }
        }

    // ─── Embed queue (doc_id only) ────────────────────────────────

    /// SQLite-backed embed queue. Items have DocId only.
    let embedQueue (db: Algebra.Database) : Algebra.StageQueue<Algebra.DocItem> =
        { dequeue = fun batchSize ->
            task {
                let! rows =
                    db.execReader
                        "SELECT id, doc_id, attempts FROM stage_embed ORDER BY created_at LIMIT @lim"
                        [ ("@lim", boxVal (int64 batchSize)) ]
                return
                    rows |> List.choose (fun row ->
                        let r = Prelude.RowReader(row)
                        match r.OptInt64 "id", r.OptInt64 "doc_id" with
                        | Some qid, Some did ->
                            Some
                                { Algebra.DocItem.QueueId = qid
                                  Algebra.DocItem.DocId = did
                                  Algebra.DocItem.Attempts = r.Int64 "attempts" 0L |> int }
                        | _ -> None)
            }

          complete = fun item ->
            task {
                let! _ = db.execNonQuery "DELETE FROM stage_embed WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                ()
            }

          fail = fun item (logger: Algebra.Logger) (clock: Algebra.Clock) error ->
            task {
                if item.Attempts + 1 >= maxAttempts then
                    let! nameObj = db.execScalar "SELECT COALESCE(original_name, saved_path) FROM documents WHERE id = @id" [ ("@id", boxVal item.DocId) ]
                    let name = match nameObj with :? string as s -> s | _ -> "unknown"
                    let! _ =
                        db.execNonQuery
                            """INSERT INTO dead_letters (doc_id, stage, error, failed_at, original_name)
                               VALUES (@doc, 'embed', @err, @now, @name)"""
                            [ ("@doc", boxVal item.DocId); ("@err", boxVal error)
                              ("@now", boxVal (clock.utcNow().ToString("o")))
                              ("@name", boxVal name) ]
                    let! _ = db.execNonQuery "DELETE FROM stage_embed WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                    logger.warn $"Dead-lettered doc {item.DocId} from embed: {error}"
                    return true
                else
                    let! _ = db.execNonQuery "UPDATE stage_embed SET attempts = attempts + 1 WHERE id = @id" [ ("@id", boxVal item.QueueId) ]
                    return false
            }

          enqueue = fun item ->
            task {
                let! _ =
                    db.execNonQuery
                        "INSERT OR IGNORE INTO stage_embed (doc_id) VALUES (@doc)"
                        [ ("@doc", boxVal item.DocId) ]
                ()
            }

          count = fun () ->
            task {
                let! result = db.execScalar "SELECT COUNT(*) FROM stage_embed" []
                return match result with :? int64 as i -> i | _ -> 0L
            }
        }

    // ─── Batch processors ─────────────────────────────────────────

    /// Process extract batch: dequeue ExtractItems, process, forward DocIds to classify.
    /// processFn returns Ok docId on success (for forwarding) or Error message.
    let processExtractBatch
        (input: Algebra.StageQueue<Algebra.ExtractItem>)
        (output: Algebra.StageQueue<Algebra.DocItem>)
        (processFn: Algebra.ExtractItem -> Task<Result<int64, string>>)
        (logger: Algebra.Logger) (clock: Algebra.Clock)
        (batchSize: int)
        : Task<int> =
        task {
            let! items = input.dequeue batchSize
            let mutable processed = 0

            for item in items do
                let! result = processFn item
                match result with
                | Ok docId ->
                    do! output.enqueue { Algebra.DocItem.QueueId = 0L; DocId = docId; Attempts = 0 }
                    do! input.complete item
                    processed <- processed + 1
                | Error err ->
                    let! _ = input.fail item logger clock err
                    ()

            return processed
        }

    /// Process doc batch (classify or embed): dequeue DocItems, process, optionally forward.
    let processDocBatch
        (input: Algebra.StageQueue<Algebra.DocItem>)
        (output: Algebra.StageQueue<Algebra.DocItem> option)
        (processFn: Algebra.DocItem -> Task<Result<unit, string>>)
        (logger: Algebra.Logger) (clock: Algebra.Clock)
        (batchSize: int)
        : Task<int> =
        task {
            let! items = input.dequeue batchSize
            let mutable processed = 0

            for item in items do
                let! result = processFn item
                match result with
                | Ok () ->
                    match output with
                    | Some next -> do! next.enqueue { Algebra.DocItem.QueueId = 0L; DocId = item.DocId; Attempts = 0 }
                    | None -> ()
                    do! input.complete item
                    processed <- processed + 1
                | Error err ->
                    let! _ = input.fail item logger clock err
                    ()

            return processed
        }

    // ─── Processor loops ──────────────────────────────────────────

    /// Run extract processor in a loop.
    let runExtractLoop
        (name: string) (logger: Algebra.Logger) (clock: Algebra.Clock)
        (input: Algebra.StageQueue<Algebra.ExtractItem>)
        (output: Algebra.StageQueue<Algebra.DocItem>)
        (processFn: Algebra.ExtractItem -> Task<Result<int64, string>>)
        (batchSize: int) (pollInterval: TimeSpan)
        (ct: CancellationToken)
        : Task<unit> =
        task {
            logger.info $"Stage processor '{name}' started"
            try
                while not ct.IsCancellationRequested do
                    try
                        let! count = processExtractBatch input output processFn logger clock batchSize
                        if count = 0 then
                            try do! Task.Delay(pollInterval, ct)
                            with :? OperationCanceledException -> ()
                    with ex ->
                        logger.warn $"Stage '{name}' error: {ex.Message}"
                        try do! Task.Delay(pollInterval, ct)
                        with :? OperationCanceledException -> ()
            with :? OperationCanceledException -> ()
            logger.info $"Stage processor '{name}' stopped"
        }

    /// Run doc processor (classify/embed) in a loop.
    let runDocLoop
        (name: string) (logger: Algebra.Logger) (clock: Algebra.Clock)
        (input: Algebra.StageQueue<Algebra.DocItem>)
        (output: Algebra.StageQueue<Algebra.DocItem> option)
        (processFn: Algebra.DocItem -> Task<Result<unit, string>>)
        (batchSize: int) (pollInterval: TimeSpan)
        (ct: CancellationToken)
        : Task<unit> =
        task {
            logger.info $"Stage processor '{name}' started"
            try
                while not ct.IsCancellationRequested do
                    try
                        let! count = processDocBatch input output processFn logger clock batchSize
                        if count = 0 then
                            try do! Task.Delay(pollInterval, ct)
                            with :? OperationCanceledException -> ()
                    with ex ->
                        logger.warn $"Stage '{name}' error: {ex.Message}"
                        try do! Task.Delay(pollInterval, ct)
                        with :? OperationCanceledException -> ()
            with :? OperationCanceledException -> ()
            logger.info $"Stage processor '{name}' stopped"
        }

    // ─── Dashboard ────────────────────────────────────────────────

    /// Log pipeline dashboard from queue table counts.
    let dashboardLoop
        (db: Algebra.Database) (logger: Algebra.Logger)
        (extractQ: Algebra.StageQueue<Algebra.ExtractItem>)
        (classifyQ: Algebra.StageQueue<Algebra.DocItem>)
        (embedQ: Algebra.StageQueue<Algebra.DocItem>)
        (interval: TimeSpan) (ct: CancellationToken)
        : Task<unit> =
        task {
            try
                while not ct.IsCancellationRequested do
                    try do! Task.Delay(interval, ct) with :? OperationCanceledException -> ()
                    if ct.IsCancellationRequested then () else

                    let! extractPending = extractQ.count ()
                    let! classifyPending = classifyQ.count ()
                    let! embedPending = embedQ.count ()
                    let! docCount = db.execScalar "SELECT COUNT(*) FROM documents" []
                    let dc = match docCount with :? int64 as i -> i | _ -> 0L

                    logger.info $"⚡ reading:{extractPending} → filing:{classifyPending} → memorising:{embedPending} | {dc} received"
            with :? OperationCanceledException -> ()
        }

    // ─── Convenience: enqueue helpers for producers ───────────────

    /// Enqueue a document into the extract stage (used by email sync and folder watcher).
    let enqueueExtract (q: Algebra.StageQueue<Algebra.ExtractItem>) (docId: int64) (filePath: string) =
        q.enqueue { Algebra.ExtractItem.QueueId = 0L; DocId = docId; FilePath = filePath; Attempts = 0 }
