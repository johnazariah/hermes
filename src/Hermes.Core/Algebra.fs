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
          fileExists: string -> bool
          directoryExists: string -> bool
          createDirectory: string -> unit }

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
          fileExists = File.Exists
          directoryExists = Directory.Exists
          createDirectory = fun path -> Directory.CreateDirectory(path) |> ignore }
