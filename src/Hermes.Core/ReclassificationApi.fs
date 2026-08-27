namespace Hermes.Core

open System.Threading.Tasks

[<RequireQualifiedAccess>]
module ReclassificationApi =

    type ItemResponse =
        { documentId: int64
          status: string
          previousCategory: string option
          category: string option
          changed: bool
          savedPath: string option
          sha256: string option
          error: string option }

    type BatchResponse =
        { action: string
          succeeded: int
          failed: int
          outcomes: ItemResponse list }

    type private ItemStatus =
        | Reclassified
        | Unchanged
        | Failed

    let private statusText = function
        | Reclassified -> "reclassified"
        | Unchanged -> "unchanged"
        | Failed -> "failed"

    let private success (outcome: Reclassification.Outcome) =
        let status =
            if outcome.Changed then Reclassified else Unchanged
        { documentId = outcome.DocumentId
          status = statusText status
          previousCategory = Some outcome.PreviousCategory
          category = Some outcome.Category
          changed = outcome.Changed
          savedPath = Some outcome.SavedPath
          sha256 = Some outcome.Sha256
          error = None }

    let private failure documentId error =
        { documentId = documentId
          status = statusText Failed
          previousCategory = None
          category = None
          changed = false
          savedPath = None
          sha256 = None
          error = Some (Reclassification.describeError error) }

    let single db fs archiveDir documentId category : Task<ItemResponse> =
        task {
            let! result =
                DocumentManagement.reclassify
                    db fs archiveDir documentId category
            return
                result
                |> Result.map success
                |> Result.defaultWith (failure documentId)
        }

    let private executeAll db fs archiveDir category documentIds =
        let rec loop outcomes = function
            | [] -> Task.FromResult(List.rev outcomes)
            | documentId :: tail ->
                task {
                    let! outcome =
                        single
                            db fs archiveDir documentId category
                    return! loop (outcome :: outcomes) tail
                }

        loop [] documentIds

    let batch db fs archiveDir documentIds category : Task<BatchResponse> =
        task {
            let! outcomes =
                executeAll db fs archiveDir category documentIds
            let failed =
                outcomes
                |> List.filter (fun outcome -> outcome.error.IsSome)
                |> List.length
            return
                { action = "reclassify"
                  succeeded = outcomes.Length - failed
                  failed = failed
                  outcomes = outcomes }
        }
