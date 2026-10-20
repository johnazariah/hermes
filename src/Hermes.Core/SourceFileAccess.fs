namespace Hermes.Core

open System
open System.Threading.Tasks

/// Ownership checks and staged-source claims; only used inside write transactions.
[<RequireQualifiedAccess>]
module SourceFileAccess =

    type Owner =
        { DocumentId: int64
          Sha256: string }

    type internal UnclaimedConflict =
        | DocumentOwners of path: string * documentIds: int64 list
        | StagedSource of path: string

    let private documentConflictMessage (path: string) (documentIds: int64 list) =
        $"Saved path ownership conflict for {path}; owners: {documentIds}"

    let internal describeUnclaimedConflict = function
        | DocumentOwners (path, documentIds) ->
            documentConflictMessage path documentIds
        | StagedSource path ->
            $"Saved path ownership conflict with a staged source: {path}"

    let private ownerFromRow row : Owner =
        let value = Prelude.RowReader(row)
        { DocumentId = value.Int64 "id" 0L
          Sha256 = value.String "sha256" "" }

    let owners (db: Algebra.Database) (path: string) : Task<Owner list> =
        task {
            let! rows =
                db.execReader
                    """SELECT id, sha256 FROM documents
                       WHERE hermes_saved_path_key(saved_path) =
                             hermes_saved_path_key(@path)
                       ORDER BY id"""
                    [ "@path", Database.boxVal path ]
            return rows |> List.map ownerFromRow
        }

    let requireExclusive db (path: string) (expectedDocument: int64 option) =
        task {
            let! current = owners db path
            let actual = current |> List.map (fun owner -> owner.DocumentId)
            let expected = expectedDocument |> Option.toList
            if actual <> expected then
                invalidOp (documentConflictMessage path actual)
        }

    let internal checkUnclaimed (db: Algebra.Database) (path: string)
        : Task<Result<unit, UnclaimedConflict>> =
        task {
            let! current = owners db path
            let documentIds = current |> List.map (fun owner -> owner.DocumentId)
            if not (List.isEmpty documentIds) then
                return Error (DocumentOwners (path, documentIds))
            else
                let! value =
                    db.execScalar
                        """SELECT COUNT(*) FROM archive_source_claims
                           WHERE ownership_key = hermes_saved_path_key(@path)"""
                        [ "@path", Database.boxVal path ]
                return
                    if unbox<int64> value = 0L then Ok ()
                    else Error (StagedSource path)
        }

    let requireUnclaimed (db: Algebra.Database) (path: string) =
        task {
            let! result = checkUnclaimed db path
            return result |> Result.defaultWith (describeUnclaimedConflict >> invalidOp)
        }

    let claim (db: Algebra.Database) (path: string) (sha256: string) =
        task {
            let! affected =
                db.execNonQuery
                    """INSERT INTO archive_source_claims (ownership_key, sha256)
                       VALUES (hermes_saved_path_key(@path), @sha)
                       ON CONFLICT(ownership_key) DO UPDATE SET sha256 = excluded.sha256
                       WHERE archive_source_claims.sha256 = excluded.sha256"""
                    [ "@path", Database.boxVal path
                      "@sha", Database.boxVal sha256 ]
            if affected <> 1 then
                invalidOp $"Saved path ownership conflict with different source bytes: {path}"
        }

    let release (db: Algebra.Database) (path: string) =
        task {
            let! _ =
                db.execNonQuery
                    """DELETE FROM archive_source_claims
                       WHERE ownership_key = hermes_saved_path_key(@path)"""
                    [ "@path", Database.boxVal path ]
            return ()
        }
