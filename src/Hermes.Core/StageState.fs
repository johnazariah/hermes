namespace Hermes.Core

open System.Text.RegularExpressions
open System.Threading.Tasks

/// Single source of truth for state owned by each pipeline stage.
[<RequireQualifiedAccess>]
module StageState =

    let private identifier =
        Regex(@"^[A-Za-z0-9_]+$", RegexOptions.Compiled)

    let private manualGuard =
        "COALESCE(classification_tier, '') <> 'manual'"

    let private extractionResetSql =
        """UPDATE documents SET
             extracted_date = NULL,
             extracted_amount = NULL,
             extracted_vendor = NULL,
             extracted_abn = NULL,
             ocr_confidence = NULL,
             extraction_method = NULL,
             extraction_confidence = NULL,
             extracted_at = NULL
           WHERE id = @doc"""

    let private classificationResetSql =
        $"""UPDATE documents SET
              category =
                CASE WHEN {manualGuard} THEN 'unclassified' ELSE category END,
              classification_confidence =
                CASE WHEN {manualGuard} THEN NULL ELSE classification_confidence END,
              classification_tier =
                CASE WHEN {manualGuard} THEN NULL ELSE classification_tier END
            WHERE id = @doc"""

    let private comprehensionOwnedSql =
        [ classificationResetSql
          "DELETE FROM tags WHERE document_id = @doc AND source = 'comprehension'"
          "DELETE FROM document_contacts WHERE document_id = @doc"
          "DELETE FROM suggestions WHERE document_id = @doc AND status = 'pending'" ]

    let private ownedSql (stageName: string) : string list =
        match stageName with
        | "extract" -> [ extractionResetSql ]
        | "triage"
        | "deep-comprehend" -> comprehensionOwnedSql
        | "embed" ->
            [ "DELETE FROM document_chunks WHERE document_id = @doc"
              """UPDATE documents SET
                   embedded_at = NULL,
                   chunk_count = NULL
                 WHERE id = @doc""" ]
        | _ -> []

    let private outputTableName (name: string) : string =
        if identifier.IsMatch(name) then name
        else invalidOp $"Invalid pipeline output table name: '{name}'"

    let private run
        (scope: Algebra.TransactionScope)
        (documentId: int64)
        ()
        (sql: string)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    sql
                    [ ("@doc", Database.boxVal documentId) ]
            return ()
        }

    let deriveDocumentStage (completed: Set<string>) : string =
        if not (completed |> Set.contains "extract") then "received"
        elif not (completed |> Set.contains "triage") then "extracted"
        elif not (completed |> Set.contains "deep-comprehend") then "triaged"
        elif not (completed |> Set.contains "embed") then "understood"
        else "embedded"

    let updateDocumentProjection
        (scope: Algebra.TransactionScope)
        (documentId: int64)
        : Task<unit> =
        task {
            let! rows =
                scope.execReader
                    "SELECT stage_name FROM stage_completions WHERE document_id = @doc"
                    [ ("@doc", Database.boxVal documentId) ]
            let completed =
                rows
                |> List.map (fun row ->
                    (Prelude.RowReader row).String "stage_name" "")
                |> Set.ofList
            let! _ =
                scope.execNonQuery
                    "UPDATE documents SET stage = @stage WHERE id = @doc"
                    [ ("@stage", Database.boxVal (deriveDocumentStage completed))
                      ("@doc", Database.boxVal documentId) ]
            return ()
        }

    /// Removes database state owned by one stage. Shared filesystem sidecars
    /// are deliberately never deleted.
    let invalidate
        (scope: Algebra.TransactionScope)
        (documentId: int64)
        (stageName: string)
        (outputTable: string)
        : Task<unit> =
        task {
            let table = outputTableName outputTable
            let! _ =
                scope.execNonQuery
                    """DELETE FROM stage_completions
                       WHERE document_id = @doc AND stage_name = @stage"""
                    [ ("@doc", Database.boxVal documentId)
                      ("@stage", Database.boxVal stageName) ]
            do! run scope documentId () $"DELETE FROM {table} WHERE document_id = @doc"
            do!
                ownedSql stageName
                |> Prelude.foldTask (run scope documentId) ()
        }
