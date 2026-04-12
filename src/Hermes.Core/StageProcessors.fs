namespace Hermes.Core

open System
open System.Threading
open System.Threading.Tasks

/// Stage queue processors: generic pipeline stage execution over the StageQueue algebra.
/// Each stage is a function (docId → Task<Result<unit, string>>) wired between an input and output queue.
[<RequireQualifiedAccess>]
module StageProcessors =

    // ─── SQLite interpreter for StageQueue ────────────────────────

    /// Build a StageQueue backed by a SQLite table.
    /// tableName: the stage_* table name.
    /// maxAttempts: how many retries before dead-lettering.
    let fromSqlite
        (db: Algebra.Database) (logger: Algebra.Logger) (clock: Algebra.Clock)
        (tableName: string) (maxAttempts: int)
        : Algebra.StageQueue =

        let boxVal = Database.boxVal

        { dequeue = fun batchSize ->
            task {
                let! rows =
                    db.execReader
                        $"SELECT id, doc_id, file_path, attempts FROM {tableName} ORDER BY created_at LIMIT @lim"
                        [ ("@lim", boxVal (int64 batchSize)) ]
                return
                    rows |> List.choose (fun row ->
                        let r = Prelude.RowReader(row)
                        match r.OptInt64 "id", r.OptInt64 "doc_id" with
                        | Some qid, Some did ->
                            Some { Algebra.StageItem.QueueId = qid
                                   DocId = did
                                   Payload = r.String "file_path" ""
                                   Attempts = r.Int64 "attempts" 0L |> int }
                        | _ -> None)
            }

          complete = fun queueId ->
            task {
                let! _ = db.execNonQuery $"DELETE FROM {tableName} WHERE id = @id" [ ("@id", boxVal queueId) ]
                ()
            }

          fail = fun queueId error ->
            task {
                let! attObj = db.execScalar $"SELECT attempts FROM {tableName} WHERE id = @id" [ ("@id", boxVal queueId) ]
                let attempts = match attObj with :? int64 as i -> int i | _ -> 0

                if attempts + 1 >= maxAttempts then
                    // Dead-letter: get doc info, insert into dead_letters, remove from queue
                    let! docObj = db.execScalar $"SELECT doc_id FROM {tableName} WHERE id = @id" [ ("@id", boxVal queueId) ]
                    let docId = match docObj with :? int64 as i -> i | _ -> 0L
                    let! nameObj = db.execScalar "SELECT COALESCE(original_name, saved_path) FROM documents WHERE id = @id" [ ("@id", boxVal docId) ]
                    let name = match nameObj with :? string as s -> s | _ -> "unknown"

                    let! _ =
                        db.execNonQuery
                            """INSERT INTO dead_letters (doc_id, stage, error, failed_at, original_name)
                               VALUES (@doc, @stage, @err, @now, @name)"""
                            [ ("@doc", boxVal docId); ("@stage", boxVal tableName)
                              ("@err", boxVal error)
                              ("@now", boxVal (clock.utcNow().ToString("o")))
                              ("@name", boxVal name) ]
                    let! _ = db.execNonQuery $"DELETE FROM {tableName} WHERE id = @id" [ ("@id", boxVal queueId) ]
                    logger.warn $"Dead-lettered doc {docId} from {tableName}: {error}"
                    return true
                else
                    let! _ = db.execNonQuery $"UPDATE {tableName} SET attempts = attempts + 1 WHERE id = @id" [ ("@id", boxVal queueId) ]
                    return false
            }

          enqueue = fun docId payload ->
            task {
                let! _ =
                    db.execNonQuery
                        $"INSERT OR IGNORE INTO {tableName} (doc_id, file_path) VALUES (@doc, @path)"
                        [ ("@doc", boxVal docId); ("@path", boxVal payload) ]
                ()
            }

          count = fun () ->
            task {
                let! result = db.execScalar $"SELECT COUNT(*) FROM {tableName}" []
                return match result with :? int64 as i -> i | _ -> 0L
            }
        }

    // ─── Generic stage runner ─────────────────────────────────────

    /// Process one batch: dequeue from input, run the process function,
    /// advance to output queue (if provided), complete or fail.
    let processBatch
        (input: Algebra.StageQueue)
        (output: Algebra.StageQueue option)
        (processFn: Algebra.StageItem -> Task<Result<unit, string>>)
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
                    | Some next -> do! next.enqueue item.DocId item.Payload
                    | None -> ()
                    do! input.complete item.QueueId
                    processed <- processed + 1
                | Error err ->
                    let! _ = input.fail item.QueueId err
                    ()

            return processed
        }

    /// Run a stage processor in a loop: poll, process, sleep when idle.
    let runLoop
        (name: string) (logger: Algebra.Logger)
        (input: Algebra.StageQueue)
        (output: Algebra.StageQueue option)
        (processFn: Algebra.StageItem -> Task<Result<unit, string>>)
        (batchSize: int)
        (pollInterval: TimeSpan)
        (ct: CancellationToken)
        : Task<unit> =
        task {
            logger.info $"Stage processor '{name}' started"
            try
                while not ct.IsCancellationRequested do
                    try
                        let! count = processBatch input output processFn batchSize
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

    // ─── Dashboard log (reads queue counts) ───────────────────────

    /// Log pipeline dashboard from queue table counts.
    let dashboardLoop
        (db: Algebra.Database) (logger: Algebra.Logger)
        (extractQueue: Algebra.StageQueue) (classifyQueue: Algebra.StageQueue) (embedQueue: Algebra.StageQueue)
        (interval: TimeSpan) (ct: CancellationToken)
        : Task<unit> =
        task {
            try
                while not ct.IsCancellationRequested do
                    try do! Task.Delay(interval, ct) with :? OperationCanceledException -> ()
                    if ct.IsCancellationRequested then () else

                    let! extractPending = extractQueue.count ()
                    let! classifyPending = classifyQueue.count ()
                    let! embedPending = embedQueue.count ()
                    let! docCount = db.execScalar "SELECT COUNT(*) FROM documents" []
                    let dc = match docCount with :? int64 as i -> i | _ -> 0L

                    logger.info $"⚡ reading:{extractPending} → filing:{classifyPending} → memorising:{embedPending} | {dc} received"
            with :? OperationCanceledException -> ()
        }
