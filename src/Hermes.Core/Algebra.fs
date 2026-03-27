namespace Hermes.Core

open System
open System.IO
open System.Threading.Tasks

/// Tagless-final algebras as records of functions.
/// Each record defines a capability. Production code provides real interpreters;
/// tests swap in pure / in-memory ones. Business logic is parameterised over
/// these records, never touching concrete I/O directly.
[<RequireQualifiedAccess>]
module Algebra =

    // ─── Clock ───────────────────────────────────────────────────────

    type Clock =
        { utcNow: unit -> DateTimeOffset }

    // ─── File system ─────────────────────────────────────────────────

    type FileSystem =
        { readAllText: string -> Task<string>
          writeAllText: string -> string -> Task<unit>
          writeAllBytes: string -> byte array -> Task<unit>
          readAllBytes: string -> Task<byte array>
          fileExists: string -> bool
          directoryExists: string -> bool
          createDirectory: string -> unit
          deleteFile: string -> unit
          moveFile: string -> string -> unit
          getFiles: string -> string -> string array
          getFileSize: string -> int64 }

    // ─── Logging ─────────────────────────────────────────────────────

    type Logger =
        { info: string -> unit
          warn: string -> unit
          error: string -> unit
          debug: string -> unit }

    // ─── Database ────────────────────────────────────────────────────

    type Database =
        { execNonQuery: string -> (string * obj) list -> Task<int>
          execScalar: string -> (string * obj) list -> Task<obj | null>
          initSchema: unit -> Task<Result<unit, string>>
          tableExists: string -> Task<bool>
          schemaVersion: unit -> Task<int>
          dispose: unit -> unit }

    // ─── Email provider ──────────────────────────────────────────────

    type EmailProvider =
        { listNewMessages: DateTimeOffset option -> Task<Domain.EmailMessage list>
          getAttachments: string -> Task<Domain.EmailAttachment list> }

    // ─── Rules engine ────────────────────────────────────────────────

    type RulesEngine =
        { classify: Domain.SidecarMetadata option -> string -> Domain.ClassificationResult
          reload: unit -> Task<Result<unit, string>> }

    // ─── File watcher ────────────────────────────────────────────────

    type FileWatcher =
        { start: string -> (string -> unit) -> IDisposable }


/// Production interpreters for the algebras.
[<RequireQualifiedAccess>]
module Interpreters =

    let systemClock: Algebra.Clock =
        { utcNow = fun () -> DateTimeOffset.UtcNow }

    let realFileSystem: Algebra.FileSystem =
        { readAllText = File.ReadAllTextAsync
          writeAllText =
            fun path content ->
                task {
                    do! File.WriteAllTextAsync(path, content)
                }
          writeAllBytes =
            fun path bytes ->
                task {
                    do! File.WriteAllBytesAsync(path, bytes)
                }
          readAllBytes = File.ReadAllBytesAsync
          fileExists = File.Exists
          directoryExists = Directory.Exists
          createDirectory = fun path -> Directory.CreateDirectory(path) |> ignore
          deleteFile = File.Delete
          moveFile = fun src dst -> File.Move(src, dst)
          getFiles = fun dir pattern -> Directory.GetFiles(dir, pattern)
          getFileSize = fun path -> FileInfo(path).Length }

    let fileWatcher: Algebra.FileWatcher =
        { start =
            fun dir callback ->
                let watcher = new FileSystemWatcher(dir)
                watcher.EnableRaisingEvents <- true
                watcher.IncludeSubdirectories <- false

                let handler =
                    FileSystemEventHandler(fun _ e -> callback e.FullPath)

                watcher.Created.AddHandler(handler)
                watcher.Renamed.AddHandler(RenamedEventHandler(fun _ e -> callback e.FullPath))
                watcher :> IDisposable }
