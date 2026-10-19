namespace Hermes.Core

open System
open System.IO
open System.Security.Cryptography
open System.Text

/// Targeted ingestion mutations, never archive scanning or saved-path repair.
[<RequireQualifiedAccess>]
module SourceFiles =

    let hash (bytes: byte array) =
        bytes |> SHA256.HashData |> Convert.ToHexString |> fun value -> value.ToLowerInvariant()

    let private atomicWrite (fs: Algebra.FileSystem) path bytes =
        task {
            let temporary = path + "." + Guid.NewGuid().ToString("N") + ".hermes_ingesting"
            try
                do! fs.writeAllBytes temporary bytes
                if fs.fileExists path then
                    invalidOp $"Source destination already exists: {path}"
                fs.moveFile temporary path
            finally
                if fs.fileExists temporary then fs.deleteFile temporary
        }

    let private ensureContent (fs: Algebra.FileSystem) path bytes =
        task {
            if fs.fileExists path then
                let! current = fs.readAllBytes path
                if hash current <> hash bytes then
                    invalidOp $"Saved path ownership conflict with existing source bytes: {path}"
            else
                Path.GetDirectoryName(path)
                |> Option.ofObj
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.iter fs.createDirectory
                do! atomicWrite fs path bytes
        }

    let writeNew (db: Algebra.Database) fs path bytes =
        db.transaction.Run(fun transaction ->
            task {
                do! SourceFileAccess.requireExclusive transaction path None
                do! SourceFileAccess.claim transaction path (hash bytes)
                do! ensureContent fs path bytes
            })

    let writeMetadata (db: Algebra.Database) (fs: Algebra.FileSystem) path bytes =
        db.transaction.Run(fun transaction ->
            task {
                do! SourceFileAccess.requireUnclaimed transaction path
                do! fs.writeAllBytes path bytes
            })

    let newFileSystem db (fs: Algebra.FileSystem) =
        { fs with
            writeAllBytes = writeNew db fs
            writeAllText = fun path text ->
                writeNew db fs path (Encoding.UTF8.GetBytes(text: string)) }

    let metadataFileSystem db (fs: Algebra.FileSystem) =
        { fs with
            writeAllBytes = writeMetadata db fs
            writeAllText = fun path text ->
                writeMetadata db fs path (Encoding.UTF8.GetBytes(text: string)) }

    let restore (db: Algebra.Database) fs
        (documentId: int64) (path: string) (expectedSha: string) (bytes: byte array) =
        db.transaction.Run(fun transaction ->
            task {
                do! SourceFileAccess.requireExclusive transaction path (Some documentId)
                let! owners = SourceFileAccess.owners transaction path
                if not (String.Equals(owners.Head.Sha256, expectedSha, StringComparison.OrdinalIgnoreCase))
                   || not (String.Equals(hash bytes, expectedSha, StringComparison.OrdinalIgnoreCase)) then
                    invalidOp $"Source identity changed before re-download: {documentId}"
                do! SourceFileAccess.claim transaction path (hash bytes)
                do! ensureContent fs path bytes
            })

    let delete (db: Algebra.Database) (fs: Algebra.FileSystem) path =
        db.transaction.Run(fun transaction ->
            task {
                do! SourceFileAccess.requireUnclaimed transaction path
                if fs.fileExists path then fs.deleteFile path
                do! SourceFileAccess.release transaction path
            })

    let move (db: Algebra.Database) (fs: Algebra.FileSystem) source destination =
        db.transaction.Run(fun transaction ->
            task {
                do! SourceFileAccess.requireUnclaimed transaction source
                do! SourceFileAccess.requireUnclaimed transaction destination
                if fs.fileExists destination then
                    invalidOp $"Source destination already exists: {destination}"
                let! bytes = fs.readAllBytes source
                do! SourceFileAccess.claim transaction destination (hash bytes)
                fs.moveFile source destination
                do! SourceFileAccess.release transaction source
            })
