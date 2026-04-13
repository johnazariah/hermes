namespace Hermes.Core

open System.IO
open System.Threading.Channels

/// On startup, repopulate pipeline channels from durable state (filesystem + DB).
/// Ensures no documents are lost after a crash or shutdown.
[<RequireQualifiedAccess>]
module Recovery =

    /// Scan unclassified/ and push files to ingest, query DB for incomplete docs.
    let repopulate
        (fs: Algebra.FileSystem) (db: Algebra.Database) (logger: Algebra.Logger)
        (archiveDir: string) (extractCh: ChannelWriter<int64>) =
        task {
            // 1. Unextracted docs → extract channel
            let! docs = Extraction.getDocumentsForExtraction db None false 10000
            if not docs.IsEmpty then
                logger.info $"Recovery: {docs.Length} unextracted document(s) queued for extraction"
                for (docId, _) in docs do
                    do! extractCh.WriteAsync(docId)

            // 2. Files in unclassified/ are handled by the normal classify stage
            //    (classifyNew scans the directory each cycle)
            let unclassifiedDir = Path.Combine(archiveDir, "unclassified")
            if fs.directoryExists unclassifiedDir then
                let count =
                    fs.getFiles unclassifiedDir "*"
                    |> Array.filter (fun f -> not (f.EndsWith(".meta.json", System.StringComparison.OrdinalIgnoreCase)))
                    |> Array.length
                if count > 0 then
                    logger.info $"Recovery: {count} file(s) in unclassified/ will be processed next cycle"
        }
