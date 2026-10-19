namespace Hermes.Service

#nowarn "3261"

open System
open System.Threading
open System.Threading.Tasks
open Hermes.Core

/// Startup and shutdown lifetime for the service host.
[<RequireQualifiedAccess>]
module Lifetime =

    /// Exit code used when background work outlived its shutdown budget and the
    /// database connections were deliberately not disposed.
    [<Literal>]
    let AbandonedExitCode = 75

    /// Whether it is safe to release the database connections.
    type Disposition =
        /// Every owned background task has settled. Disposal is safe.
        | Quiesced
        /// Owned background work is still running. The connections must be left
        /// open: disposing them would tear the gate and connection out from
        /// underneath a live worker.
        | Abandoned of stillRunning: int

    /// Schema initialisation must succeed before any background work or API
    /// traffic starts. A broken schema is not something the service can run on,
    /// so the actual error ends startup instead of being discarded.
    let requireSchema (result: Result<unit, string>) : unit =
        match result with
        | Ok () -> ()
        | Error error ->
            failwith
                $"Hermes cannot start: schema initialisation failed: {error}"

    let private isCancellation (error: exn) =
        match error with
        | :? OperationCanceledException -> true
        | :? AggregateException as aggregate ->
            aggregate.InnerExceptions.Count > 0
            && aggregate.InnerExceptions
               |> Seq.forall (fun inner ->
                   inner :? OperationCanceledException)
        | _ -> false

    /// Classifies a settled background task. Cancellation during shutdown is
    /// expected and is never reported as a failure; anything else that stops is
    /// surfaced the same way regardless of which task it was.
    let describeOutcome
        (logger: Algebra.Logger)
        (name: string)
        (cancellationRequested: bool)
        (finished: Task)
        : unit =
        if finished.IsFaulted then
            let fault: exn =
                match finished.Exception with
                | null -> InvalidOperationException "unknown fault"
                | aggregate -> aggregate
            if cancellationRequested && isCancellation fault then
                logger.info $"Background task '{name}' stopped"
            else
                logger.error
                    $"Background task '{name}' faulted and stopped: {fault}"
        elif finished.IsCanceled || cancellationRequested then
            logger.info $"Background task '{name}' stopped"
        else
            logger.warn $"Background task '{name}' exited before shutdown"

    /// Background work is fire-and-forget for the request path, but its terminal
    /// state must still be observed AND its handle retained, so shutdown can
    /// prove it has stopped before the database is released. The returned task
    /// completes only after the work has settled, and never faults: the outcome
    /// is reported, not rethrown.
    let observeBackground
        (logger: Algebra.Logger)
        (name: string)
        (token: CancellationToken)
        (work: Task)
        : Task =
        work.ContinueWith(
            Action<Task>(fun finished ->
                describeOutcome
                    logger name token.IsCancellationRequested finished),
            TaskContinuationOptions.ExecuteSynchronously)

    /// Waits for cancelled background work to settle and reports whether the
    /// database may be released.
    ///
    /// `Quiesced` is returned only when every owned task is genuinely completed,
    /// so a caller can never be told disposal is safe while a worker is still
    /// using the connection. If the budget expires the connections are left to
    /// the process teardown instead, which is strictly safer than disposing the
    /// gate and connection beneath a live worker.
    ///
    /// Owned waits are cancellation-aware, so the budget is a backstop against
    /// a wedged external call rather than the expected path. This runs on the
    /// console host's main thread, which has no SynchronizationContext, so the
    /// wait cannot deadlock; and it is the last thing the process does.
    let shutdown
        (logger: Algebra.Logger)
        (budget: TimeSpan)
        (tasks: Task list)
        : Disposition =
        match tasks with
        | [] -> Quiesced
        | pending ->
            try
                Task.WhenAll(pending).Wait(budget) |> ignore
            with error ->
                // Observation tasks do not fault, but never let an unexpected
                // one masquerade as a completed shutdown.
                logger.error
                    $"Background task shutdown reported a fault: {error.Message}"

            match pending |> List.filter (fun task -> not task.IsCompleted) with
            | [] ->
                logger.info "Background tasks stopped; releasing database"
                Quiesced
            | stillRunning ->
                logger.error
                    $"{stillRunning.Length} background task(s) still running after {budget.TotalSeconds:F0}s; leaving database connections open rather than disposing beneath active workers"
                Abandoned stillRunning.Length

/// Admission acknowledges pending work, never successful synchronization.
[<RequireQualifiedAccess>]
module ManualSync =

    type Acknowledgement = Queued | Coalesced
    type Rejection = Starting | Stopping | UnsupportedHost

    type Capability =
        { Request: unit -> Result<Acknowledgement, Rejection> }

    let unavailable : Capability =
        { Request = fun () -> Error UnsupportedHost }

/// One producer owner, with at most one pending manual wake-up.
[<RequireQualifiedAccess>]
module ProducerLoop =

    open System.Threading.Channels

    type Options =
        { InitialDelay: TimeSpan
          Interval: TimeSpan
          Logger: Algebra.Logger
          Cycle: CancellationToken -> Task<unit> }

    type Handle =
        { Manual: ManualSync.Capability
          Run: unit -> Task
          Stop: unit -> unit }

    type private Phase = Starting | Ready | Stopped

    // Mutation is confined to this synchronized capability interpreter.
    type private Inbox =
        { Gate: obj
          Queue: Channel<unit>
          Token: CancellationToken
          mutable Phase: Phase }

    let private close inbox =
        lock inbox.Gate (fun () ->
            inbox.Phase <- Stopped
            inbox.Queue.Writer.TryComplete() |> ignore)

    let private request inbox () =
        lock inbox.Gate (fun () ->
            if inbox.Token.IsCancellationRequested || inbox.Phase = Stopped then
                Error ManualSync.Stopping
            elif inbox.Phase = Starting then
                Error ManualSync.Starting
            elif inbox.Queue.Writer.TryWrite(()) then
                Ok ManualSync.Queued
            else
                Ok ManualSync.Coalesced)

    let private beginRun inbox =
        lock inbox.Gate (fun () ->
            if inbox.Phase <> Starting then
                invalidOp "A producer owner can be run only once"
            inbox.Phase <- Ready)

    let private ready inbox =
        lock inbox.Gate (fun () ->
            inbox.Phase = Ready && not inbox.Token.IsCancellationRequested)

    // The read is cancelled and awaited on timeout: no losing read task survives.
    let private wait inbox delay =
        task {
            use timeout = CancellationTokenSource.CreateLinkedTokenSource(inbox.Token)
            timeout.CancelAfter(delay: TimeSpan)
            try
                let! () = inbox.Queue.Reader.ReadAsync(timeout.Token)
                return true
            with
            | :? OperationCanceledException ->
                return not inbox.Token.IsCancellationRequested
            | :? ChannelClosedException -> return false
        }

    let private cycle (options: Options) (token: CancellationToken) =
        task {
            token.ThrowIfCancellationRequested()
            options.Logger.info "Producer cycle started"
            try
                do! options.Cycle token
                token.ThrowIfCancellationRequested()
                options.Logger.info "Producer cycle completed"
            with
            | :? OperationCanceledException when token.IsCancellationRequested -> ()
            | error ->
                options.Logger.error $"Producer cycle failed; owner remains available: {error}"
        }

    let private run options inbox () : Task =
        task {
            beginRun inbox
            try
                let mutable delay = options.InitialDelay
                let mutable continuing = true
                while continuing && ready inbox do
                    let! due = wait inbox delay
                    continuing <- due && ready inbox
                    if continuing then do! cycle options inbox.Token
                    delay <- options.Interval
            finally
                close inbox
        } :> Task

    let private validate options =
        let maximum = TimeSpan.FromMilliseconds(4294967294.0)
        if options.InitialDelay < TimeSpan.Zero || options.InitialDelay > maximum then
            invalidArg (nameof options) "Initial producer delay is outside the supported range"
        if options.Interval <= TimeSpan.Zero || options.Interval > maximum then
            invalidArg (nameof options) "Producer interval must be positive and bounded"

    let create options token =
        validate options
        let inbox =
            { Gate = obj()
              Queue = Channel.CreateBounded<unit>(1)
              Token = token
              Phase = Starting }
        { Manual = { Request = request inbox }
          Run = run options inbox
          Stop = fun () -> close inbox }

/// Existing producer operations, serialized by ProducerLoop.
[<RequireQualifiedAccess>]
module ProducerCycle =

    open System.IO
    open System.Threading.Channels

    type Context =
        { Fs: Algebra.FileSystem
          Db: Algebra.Database
          Logger: Algebra.Logger
          Clock: Algebra.Clock
          Config: Domain.HermesConfig
          CreateEmailProvider: Domain.AccountConfig -> Task<Algebra.EmailProvider> }

    type private Candidate =
        { Path: string
          Name: string
          Hash: string }

    let private protect context label (token: CancellationToken) action =
        task {
            token.ThrowIfCancellationRequested()
            try
                do! action ()
            with
            | :? OperationCanceledException when token.IsCancellationRequested ->
                return raise (OperationCanceledException(token))
            | error -> context.Logger.warn $"{label}: {error.Message}"
        }

    let private parameters context candidate =
        [ "@name", Database.boxVal candidate.Name
          "@path", Database.boxVal (Path.GetRelativePath(context.Config.ArchiveDir, candidate.Path))
          "@size", Database.boxVal (context.Fs.getFileSize candidate.Path)
          "@sha", Database.boxVal candidate.Hash
          "@src", Database.boxVal candidate.Path
          "@now", Database.boxVal (context.Clock.utcNow().ToString("o")) ]

    let private insert context candidate =
        task {
            let! _ =
                context.Db.execNonQuery
                    """INSERT INTO documents
                       (source_type, original_name, saved_path, category, size_bytes,
                        sha256, source_path, ingested_at, stage)
                       VALUES ('watched_folder', @name, @path, 'unclassified',
                               @size, @sha, @src, @now, 'received')"""
                    (parameters context candidate)
            context.Logger.info $"Folder watcher: ingested '{candidate.Name}'"
        }

    let private removeDuplicate context candidate =
        task {
            context.Logger.info
                $"Duplicate detected (sha256={candidate.Hash}), removing: {candidate.Name}"
            do! SourceFiles.delete context.Db context.Fs candidate.Path
        }

    let private intake context path =
        task {
            let! hash = FolderWatcher.computeSha256 context.Fs path
            let candidate =
                { Path = path
                  Name = Path.GetFileName(path) |> Option.ofObj |> Option.defaultValue ""
                  Hash = hash }
            let! count =
                context.Db.execScalar
                    "SELECT COUNT(*) FROM documents WHERE sha256 = @sha"
                    [ "@sha", Database.boxVal hash ]
            let duplicate = match count with :? int64 as value -> value > 0L | _ -> false
            if duplicate then do! removeDuplicate context candidate
            else do! insert context candidate
            do! SourceFiles.delete context.Db context.Fs (path + ".meta.json")
        }

    let private register context token (path: string) =
        protect context $"Folder intake failed for {Path.GetFileName(path)}"
            token (fun () -> intake context path)

    let private scan context token () =
        task {
            let! results =
                FolderWatcher.scanAll
                    context.Fs context.Db context.Logger context.Clock context.Config
            context.Logger.info $"Folder scan examined {results.Length} file(s)"
            for _, result in results do
                match result with
                | FolderWatcher.Copied path -> do! register context token path
                | _ -> ()
        }

    let private unclassified context (token: CancellationToken) =
        task {
            let directory = Path.Combine(context.Config.ArchiveDir, "unclassified")
            if context.Fs.directoryExists directory then
                let files =
                    context.Fs.getFiles directory "*"
                    |> Array.filter (fun path ->
                        not (path.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)))
                for path in files do
                    do! register context token path
        }

    let private reportSync context (result: EmailSync.SyncResult) =
        context.Logger.info
            $"Email sync [{result.Account}]: {result.MessagesProcessed} message(s), {result.AttachmentsDownloaded} attachment(s), {result.DuplicatesSkipped} duplicate(s), {result.Errors.Length} error(s)"
        result.Errors
        |> List.iter (fun error ->
            context.Logger.error $"Email sync [{result.Account}]: {error}")

    let private sync context (account: Domain.AccountConfig) (token: CancellationToken) () =
        task {
            let! provider = context.CreateEmailProvider account
            token.ThrowIfCancellationRequested()
            let writer = Channel.CreateUnbounded<Document.T>().Writer
            let! result =
                EmailSync.syncAccountWithChannel
                    context.Fs context.Db context.Logger context.Clock provider
                    context.Config account.Label writer
                    (max 1 context.Config.Pipeline.EmailConcurrency) token
            token.ThrowIfCancellationRequested()
            reportSync context result
        }

    let run context (token: CancellationToken) =
        task {
            do! protect context "Folder scan error" token (scan context token)
            token.ThrowIfCancellationRequested()
            do! unclassified context token
            for account in context.Config.Accounts do
                do! protect context $"Email sync failed for {account.Label}"
                        token (sync context account token)
        }

/// Owned-child control protocol, independently testable without console I/O.
[<RequireQualifiedAccess>]
module OwnedControl =

    let [<Literal>] Flag = "--control-stdin"

    type Invocation =
        { Owned: bool
          Arguments: string array }

    type Signal = Byte of byte | End

    type Input =
        { Read: CancellationToken -> Task<Signal> }

    type private Line = Collecting of string | Discarding

    type private Transition =
        { Line: Line
          Stop: bool }

    let parse (arguments: string array) =
        let flags = arguments |> Array.filter ((=) Flag)
        let malformed =
            arguments
            |> Array.exists (fun argument ->
                argument <> Flag && argument.StartsWith(Flag, StringComparison.Ordinal))
        if malformed || flags.Length > 1 then
            Error "Specify --control-stdin once, as a standalone flag"
        else
            Ok
                { Owned = flags.Length = 1
                  Arguments = arguments |> Array.filter ((<>) Flag) }

    let private finishLine (logger: Algebra.Logger) (text: string) =
        let command =
            if text.EndsWith("\r", StringComparison.Ordinal) then
                text.Substring(0, text.Length - 1)
            else text
        let shutdown = command = "shutdown"
        if not shutdown then logger.warn "Rejected unknown stdin control command"
        { Line = Collecting ""; Stop = shutdown }

    let private consume logger line value =
        match line, value with
        | Collecting text, 10uy -> finishLine logger text
        | Discarding, 10uy -> { Line = Collecting ""; Stop = false }
        | Discarding, _ -> { Line = Discarding; Stop = false }
        | Collecting text, _ when text.Length >= 64 ->
            logger.warn "Rejected overlong stdin control command"
            { Line = Discarding; Stop = false }
        | Collecting text, _ ->
            { Line = Collecting(text + string (char value)); Stop = false }

    let private advance logger stop line signal =
        let transition =
            match signal with
            | Byte value -> consume logger line value
            | End ->
                logger.info "Owned stdin reached EOF; stopping because owner disappeared"
                { Line = line; Stop = true }
        if transition.Stop then stop ()
        transition

    let private drive logger input stop (token: CancellationToken) =
        task {
            let mutable current = { Line = Collecting ""; Stop = false }
            while not current.Stop && not token.IsCancellationRequested do
                let! signal = input.Read token
                current <- advance logger stop current.Line signal
        }

    let run (logger: Algebra.Logger) input stop (token: CancellationToken) : Task =
        task {
            try
                do! drive logger input stop token
            with
            | :? OperationCanceledException when token.IsCancellationRequested -> ()
            | error ->
                logger.error $"Owned stdin control failed; stopping service: {error}"
                stop ()
        } :> Task

/// Readiness probes avoid an uncancellable blocking console-read task.
/// This interpreter is the sole reader of stdin in owned mode.
module private ControlPipe =

    open System.ComponentModel
    open System.Runtime.InteropServices

    type Sample = Pending | Received of OwnedControl.Signal

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type PollDescriptor =
        { Descriptor: int
          Events: int16
          ReturnedEvents: int16 }

    [<DllImport("libc", SetLastError = true)>]
    extern int poll([<In; Out>] PollDescriptor[] descriptors, unativeint count, int timeout)

    [<DllImport("libc", SetLastError = true)>]
    extern nativeint read(int descriptor, [<Out>] byte[] buffer, unativeint count)

    [<DllImport("libc", SetLastError = true)>]
    extern int dup(int descriptor)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern nativeint GetStdHandle(uint32 kind)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 GetFileType(nativeint handle)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool PeekNamedPipe(
        nativeint handle, nativeint buffer, uint32 size, nativeint bytesRead,
        uint32& available, nativeint bytesLeft)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool ReadFile(
        nativeint handle, [<Out>] byte[] buffer, uint32 size,
        uint32& bytesRead, nativeint overlapped)

    let private failure (code: int) = raise (Win32Exception(code))

    let private windowsFailure () =
        match Marshal.GetLastPInvokeError() with
        | 109 | 232 | 233 -> Received OwnedControl.End
        | code -> failure code

    let private unixByte () =
        let buffer = Array.zeroCreate<byte> 1
        match read(0, buffer, 1un) with
        | 0n -> Received OwnedControl.End
        | 1n -> Received(OwnedControl.Byte buffer.[0])
        | _ ->
            match Marshal.GetLastPInvokeError() with
            | 4 -> Pending
            | code -> failure code

    let unix () =
        let descriptors = [| { Descriptor = 0; Events = 1s; ReturnedEvents = 0s } |]
        match poll(descriptors, 1un, 0) with
        | 0 -> Pending
        | -1 ->
            match Marshal.GetLastPInvokeError() with
            | 4 -> Pending
            | code -> failure code
        | _ when descriptors.[0].ReturnedEvents &&& 32s <> 0s ->
            invalidOp "Owned stdin descriptor is invalid"
        | _ -> unixByte ()

    let private windowsByte handle =
        let buffer = Array.zeroCreate<byte> 1
        let mutable count = 0u
        if ReadFile(handle, buffer, 1u, &count, 0n) then
            if count = 0u then Received OwnedControl.End
            else Received(OwnedControl.Byte buffer.[0])
        else windowsFailure ()

    let private windows handle () =
        let mutable available = 0u
        if PeekNamedPipe(handle, 0n, 0u, 0n, &available, 0n) then
            if available = 0u then Pending else windowsByte handle
        else windowsFailure ()

    let unixReader () =
        let descriptor = dup(0)
        if descriptor < 0 then failure (Marshal.GetLastPInvokeError())
        use handle =
            new Microsoft.Win32.SafeHandles.SafePipeHandle(nativeint descriptor, true)
        try
            use pipe =
                new System.IO.Pipes.AnonymousPipeClientStream(
                    System.IO.Pipes.PipeDirection.In, handle)
            unix
        with
        | :? ArgumentException
        | :? System.IO.IOException ->
            invalidOp "--control-stdin requires a redirected owner pipe"

    let windowsReader () =
        let handle = GetStdHandle(0xfffffff6u)
        if handle = 0n || handle = -1n || GetFileType(handle) <> 3u then
            invalidOp "--control-stdin requires a redirected owner pipe"
        windows handle

/// Cancellable asynchronous waiting without a parked Console.ReadLine worker.
[<RequireQualifiedAccess>]
module StdinControl =

    let private read sample (token: CancellationToken) =
        task {
            let mutable current = ControlPipe.Pending
            while current = ControlPipe.Pending do
                token.ThrowIfCancellationRequested()
                current <- sample ()
                if current = ControlPipe.Pending then do! Task.Delay(25, token)
            return
                match current with
                | ControlPipe.Received signal -> signal
                | ControlPipe.Pending -> invalidOp "No stdin sample was obtained"
        }

    let create () : OwnedControl.Input =
        if not Console.IsInputRedirected then
            invalidOp "--control-stdin requires redirected stdin from an owning process"
        let sample =
            if OperatingSystem.IsWindows() then ControlPipe.windowsReader ()
            else ControlPipe.unixReader ()
        { Read = read sample }
