namespace Hermes.Service

open Microsoft.AspNetCore.Http
open Hermes.Core

[<RequireQualifiedAccess>]
module internal PersistedApi =

    let get db fs archiveDir documentId =
        task {
            let! result = PersistedComprehension.read db fs archiveDir documentId
            return
                result |> Result.map (ComprehensionContract.successNode >> Results.Json)
                |> Result.defaultWith (fun failure ->
                    Results.Json(ComprehensionContract.failureNode failure,
                        statusCode = ComprehensionContract.httpStatus failure))
        }
