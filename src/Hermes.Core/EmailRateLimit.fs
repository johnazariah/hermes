namespace Hermes.Core

open System
open System.Net
open System.Threading
open System.Threading.Tasks

/// Cancellable provider throttling, always outside database transactions.
[<RequireQualifiedAccess>]
module internal EmailRateLimit =

    type Delay = TimeSpan -> CancellationToken -> Task<unit>

    let systemDelay (duration: TimeSpan) (ct: CancellationToken) : Task<unit> =
        task {
            do! Task.Delay(duration, ct)
        }

    let private limited (error: exn) : bool =
        match error with
        | :? Google.GoogleApiException as failure ->
            failure.HttpStatusCode = HttpStatusCode.TooManyRequests
        | :? Microsoft.Kiota.Abstractions.ApiException as failure ->
            failure.ResponseStatusCode = int HttpStatusCode.TooManyRequests
        | :? System.Net.Http.HttpRequestException as failure ->
            failure.StatusCode = Nullable HttpStatusCode.TooManyRequests
        | _ -> false

    let waitIfNeeded (delay: Delay) (ct: CancellationToken) (error: exn) : Task<unit> =
        task {
            if limited error then
                try
                    do! delay (TimeSpan.FromSeconds(60.0)) ct
                with
                | :? OperationCanceledException when ct.IsCancellationRequested -> ()
        }
