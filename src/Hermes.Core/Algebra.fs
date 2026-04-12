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
          getDirectories: string -> string array
          getFileSize: string -> int64 }

    // ─── Environment ────────────────────────────────────────────────

    type Environment =
        { homeDirectory: unit -> string
          configDirectory: unit -> string
          documentsDirectory: unit -> string }

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
          execReader: string -> (string * obj) list -> Task<Map<string, obj> list>
          initSchema: unit -> Task<Result<unit, string>>
          tableExists: string -> Task<bool>
          schemaVersion: unit -> Task<int>
          dispose: unit -> unit }

    // ─── Stage queue (pipeline work queues) ──────────────────────────

    /// Work item for the extract stage — includes file path.
    type ExtractItem =
        { QueueId: int64
          DocId: int64
          FilePath: string
          Attempts: int }

    /// Work item for classify/embed stages — doc ID only.
    type DocItem =
        { QueueId: int64
          DocId: int64
          Attempts: int }

    /// Tagless-Final stage queue parameterised over item type.
    type StageQueue<'Item> =
        { /// Dequeue up to N items ordered by creation time.
          dequeue: int -> Task<'Item list>
          /// Mark an item as complete (remove from queue).
          complete: 'Item -> Task<unit>
          /// Record a failed attempt. Returns true if max attempts exceeded (dead-lettered).
          fail: 'Item -> Logger -> Clock -> string -> Task<bool>
          /// Add an item to this queue.
          enqueue: 'Item -> Task<unit>
          /// Count of pending items.
          count: unit -> Task<int64> }

    // ─── Email provider ──────────────────────────────────────────────

    /// A page of message ID stubs — no content, just IDs for enumeration.
    type StubPage =
        { Ids: string list
          NextPageToken: string option
          ResultSizeEstimate: int64 }

    type MessagePage =
        { Messages: Domain.EmailMessage list
          NextPageToken: string option
          ResultSizeEstimate: int64 }

    type EmailProvider =
        { listNewMessages: DateTimeOffset option -> Task<Domain.EmailMessage list>
          getAttachments: string -> Task<Domain.EmailAttachment list>
          getMessageBody: string -> Task<string option>
          getFullMessage: string -> Task<Domain.EmailMessage>
          listStubPage: string option -> string option -> int -> Task<StubPage>
          listMessagePage: string option -> string option -> int -> Task<MessagePage> }

    // ─── Rules engine ────────────────────────────────────────────────

    type RulesEngine =
        { classify: Domain.SidecarMetadata option -> string -> Domain.ClassificationResult
          reload: unit -> Task<Result<unit, string>> }

    // ─── Embedding client ─────────────────────────────────────────────

    type EmbeddingClient =
        { embed: string -> Task<Result<float32[], string>>
          dimensions: int
          isAvailable: unit -> Task<bool> }

    // ─── File watcher ────────────────────────────────────────────────

    type FileWatcher =
        { start: string -> (string -> unit) -> IDisposable }

    // ─── Text extraction ─────────────────────────────────────────────

    type TextExtractor =
        { extractPdf: byte array -> Task<Result<string, string>>
          extractImage: byte array -> Task<Result<string, string>> }

    // ─── Plugin registry ─────────────────────────────────────────────

    /// An extractor plugin: given file bytes, produces extraction result.
    /// Tried in descending priority order. First canHandle match wins.
    type ExtractorPlugin =
        { Name: string
          Priority: int
          CanHandle: string -> bool
          Extract: byte array -> Task<Result<Domain.ExtractionResult, string>> }

    /// A post-processor plugin: fires after extraction completes.
    type PostProcessor =
        { Name: string
          Process: Database -> FileSystem -> Logger -> Clock -> int64 -> Task<unit> }

    // ─── Ollama client ───────────────────────────────────────────────

    type OllamaClient =
        { generate: string -> string -> byte array option -> Task<Result<string, string>>
          isAvailable: unit -> Task<bool> }

    // ─── Chat provider ───────────────────────────────────────────────

    /// Tagless-Final chat provider — abstracts over Ollama, Azure OpenAI, or fakes.
    type ChatProvider =
        { /// Send a system prompt + user message, get a response.
          complete: string -> string -> Task<Result<string, string>> }


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
          getDirectories = fun path -> Directory.GetDirectories(path)
          getFileSize = fun path -> FileInfo(path).Length }

    let systemEnvironment : Algebra.Environment =
        { homeDirectory = fun () -> System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile)
          configDirectory = fun () ->
              // HERMES_CONFIG_DIR env var overrides default (for dev vs production separation)
              match System.Environment.GetEnvironmentVariable("HERMES_CONFIG_DIR") with
              | null | "" ->
                  let appData =
                      if System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) then
                          System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData)
                      else
                          Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), ".config")
                  Path.Combine(appData, "hermes")
              | dir -> dir
          documentsDirectory = fun () -> System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments) }

    let nullTextExtractor: Algebra.TextExtractor =
        { extractPdf = fun _ -> Task.FromResult(Error "OCR not available in CLI mode")
          extractImage = fun _ -> Task.FromResult(Error "OCR not available in CLI mode") }

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
