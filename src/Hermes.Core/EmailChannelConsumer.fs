namespace Hermes.Core

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Functional consumer accumulation; storage failures are not duplicates.
[<RequireQualifiedAccess>]
module internal EmailChannelConsumer =

    type Summary =
        { Processed: int
          Downloaded: int
          Duplicates: int
          Failed: int
          Errors: string list }

    type private Outcome =
        | AlreadyProcessed
        | Stored of documents: int * errors: string list
        | Rejected of error: string

    type private FollowUp =
        { Description: string
          Execute: unit -> Task<unit> }

    let empty: Summary =
        { Processed = 0; Downloaded = 0; Duplicates = 0; Failed = 0; Errors = [] }

    let combine (left: Summary) (right: Summary) : Summary =
        { Processed = left.Processed + right.Processed
          Downloaded = left.Downloaded + right.Downloaded
          Duplicates = left.Duplicates + right.Duplicates
          Failed = left.Failed + right.Failed
          Errors = left.Errors @ right.Errors }

    let private add (summary: Summary) = function
        | AlreadyProcessed -> { summary with Duplicates = summary.Duplicates + 1 }
        | Rejected error ->
            { summary with Failed = summary.Failed + 1; Errors = error :: summary.Errors }
        | Stored (count, errors) ->
            { summary with
                Processed = summary.Processed + 1
                Downloaded = summary.Downloaded + count
                Errors = List.rev errors @ summary.Errors }

    let private followUp
        (delay: EmailRateLimit.Delay) (ct: CancellationToken)
        (logger: Algebra.Logger) (errors: string list) (work: FollowUp) =
        task {
            try
                do! work.Execute ()
                return errors
            with error ->
                let message = $"Post-commit {work.Description}: {error.Message}"
                logger.warn message
                do! EmailRateLimit.waitIfNeeded delay ct error
                return message :: errors
        }

    let private finish (delay: EmailRateLimit.Delay) (ct: CancellationToken)
        (context: EmailChannelPersistence.Context) (notify: int64 -> string -> Task<unit>)
        (committed: EmailChannelPersistence.Committed) : Task<Outcome> =
        task {
            let notifications =
                committed.Documents |> List.map (fun document ->
                    { Description = $"notification for document {document.DocumentId}"
                      Execute = fun () -> notify document.DocumentId document.SavedPath })
            let! errors =
                Prelude.foldTask (followUp delay ct context.Logger) [] notifications
            return Stored (committed.Documents.Length, List.rev errors)
        }

    let private ingestNew (delay: EmailRateLimit.Delay) (ct: CancellationToken)
        (context: EmailChannelPersistence.Context) (provider: Algebra.EmailProvider)
        (notify: int64 -> string -> Task<unit>) (id: string) : Task<Outcome> =
        task {
            let! fetched = EmailChannelPersistence.fetch provider id
            let! committed = EmailChannelPersistence.persist context fetched
            return!
                committed
                |> Option.map (finish delay ct context notify)
                |> Option.defaultWith (fun () -> Task.FromResult AlreadyProcessed)
        }

    let private reject (delay: EmailRateLimit.Delay) (ct: CancellationToken)
        (context: EmailChannelPersistence.Context) (id: string) (error: exn) : Task<Outcome> =
        task {
            let message = $"[{context.Account}] Email {id} was not processed: {error.Message}"
            context.Logger.warn message
            do! EmailRateLimit.waitIfNeeded delay ct error
            return Rejected message
        }

    let private handleMessage (delay: EmailRateLimit.Delay) (ct: CancellationToken)
        (context: EmailChannelPersistence.Context) (provider: Algebra.EmailProvider)
        (notify: int64 -> string -> Task<unit>) (id: string) : Task<Outcome> =
        task {
            try
                let! exists = context.Store.Exists context.Database context.Account id
                if exists then return AlreadyProcessed
                else return! ingestNew delay ct context provider notify id
            with error ->
                return! reject delay ct context id error
        }

    let private noteProgress (processed: int ref) = function
        | Stored _ -> Interlocked.Increment(processed) |> ignore
        | _ -> ()

    let rec private loop (delay: EmailRateLimit.Delay)
        (context: EmailChannelPersistence.Context) (provider: Algebra.EmailProvider)
        (notify: int64 -> string -> Task<unit>) (processed: int ref)
        (input: ChannelReader<string>) (ct: CancellationToken) (summary: Summary) =
        task {
            if ct.IsCancellationRequested then return summary
            else
                try
                    let! id = input.ReadAsync(ct).AsTask()
                    let! outcome = handleMessage delay ct context provider notify id
                    noteProgress processed outcome
                    return! loop delay context provider notify processed input ct (add summary outcome)
                with
                | :? ChannelClosedException
                | :? OperationCanceledException -> return summary
        }

    let runWithDelay (delay: EmailRateLimit.Delay)
        (context: EmailChannelPersistence.Context) (provider: Algebra.EmailProvider)
        (input: ChannelReader<string>) (notify: int64 -> string -> Task<unit>)
        (processed: int ref) (ct: CancellationToken) : Task<Summary> =
        task {
            let! result = loop delay context provider notify processed input ct empty
            return { result with Errors = List.rev result.Errors }
        }

    let run context provider input notify processed ct : Task<Summary> =
        runWithDelay EmailRateLimit.systemDelay context provider input notify processed ct
