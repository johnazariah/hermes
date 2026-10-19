namespace Hermes.Core

open System
open System.Threading.Tasks

/// Ownership checks and staged-source claims; only used inside write transactions.
[<RequireQualifiedAccess>]
module SourceFileAccess =

    type Owner =
        { DocumentId: int64
          Sha256: string }

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
                invalidOp $"Saved path ownership conflict for {path}; owners: {actual}"
        }

    let requireUnclaimed (db: Algebra.Database) (path: string) =
        task {
            do! requireExclusive db path None
            let! value =
                db.execScalar
                    """SELECT COUNT(*) FROM archive_source_claims
                       WHERE ownership_key = hermes_saved_path_key(@path)"""
                    [ "@path", Database.boxVal path ]
            if unbox<int64> value <> 0L then
                invalidOp $"Saved path ownership conflict with a staged source: {path}"
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
