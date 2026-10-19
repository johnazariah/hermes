namespace Hermes.Core

open System.Threading
open System.Threading.Channels
open System.Threading.Tasks

/// Page failures fault the producer, but close input normally so queued work drains.
[<RequireQualifiedAccess>]
module internal EmailChannelEnumeration =

    let private enqueue (output: ChannelWriter<string>) (ct: CancellationToken)
        (total: int) (id: string) : Task<int> =
        task {
            do! output.WriteAsync(id, ct).AsTask()
            return total + 1
        }

    let rec private pages (provider: Algebra.EmailProvider) (logger: Algebra.Logger)
        account query output (enumerated: int ref) (ct: CancellationToken) token total =
        task {
            ct.ThrowIfCancellationRequested()
            let! page = provider.listStubPage token (Some query) 500
            let! count = Prelude.foldTask (enqueue output ct) total page.Ids
            enumerated.Value <- count
            logger.debug $"[{account}] Enumerated {count} message IDs so far"
            return!
                page.NextPageToken
                |> Option.map (fun next ->
                    pages provider logger account query output enumerated ct (Some next) count)
                |> Option.defaultWith (fun () -> Task.FromResult count)
        }

    let private report (delay: EmailRateLimit.Delay) provider (logger: Algebra.Logger)
        account query output enumerated ct : Task<int> =
        task {
            try
                return! pages provider logger account query output enumerated ct None 0
            with error ->
                logger.warn $"[{account}] Enumeration failed: {error.Message}"
                do! EmailRateLimit.waitIfNeeded delay ct error
                return raise error
        }

    let runWithDelay delay provider logger account query (output: ChannelWriter<string>)
        enumerated ct : Task<int> =
        task {
            try
                return! report delay provider logger account query output enumerated ct
            finally
                output.TryComplete() |> ignore
        }
