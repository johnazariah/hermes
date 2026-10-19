namespace Hermes.Core

open System
open System.IO
open System.Text
open System.Threading.Tasks

/// Bounded, authority-checked archive reads shared by extraction and reindexing.
[<RequireQualifiedAccess>]
module internal ArchiveContent =

    let [<Literal>] MaxSourceBytes = 67108864L
    let [<Literal>] MaxTextBytes = 4194304L

    type Identity =
        { DocumentId: int64
          SavedPath: string
          Sha256: string }

    type Source =
        { Path: string
          Bytes: byte array }

    let resolvePath archiveDirectory savedPath =
        Database.canonicalArchivePath archiveDirectory savedPath
        |> Result.bind (fun canonical ->
            SavedPathSchema.validateFile archiveDirectory canonical.FullPath
            |> Result.map (fun () -> canonical.FullPath))

    let decode (bytes: byte array) =
        let text = UTF8Encoding(false, true).GetString(bytes).TrimStart([| '\uFEFF' |])
        if text.Contains('\u0000') then
            invalidOp "Archive text contains a NUL character"
        text

    let private requireSize maximum path length =
        if length < 0L || length > maximum then
            invalidOp $"Archive file exceeds the {maximum}-byte read bound: {path}"

    let private readBounded (fs: Algebra.FileSystem) maximum path : Task<byte array> =
        task {
            if not (fs.fileExists path) then
                raise (FileNotFoundException("Archive content file not found", path))
            requireSize maximum path (fs.getFileSize path)
            let! bytes = fs.readAllBytes path
            requireSize maximum path bytes.LongLength
            return bytes
        }

    /// Ownership queries use the database's authority, not just the supplied root.
    let readSource
        (db: Algebra.Database) fs archiveDirectory (identity: Identity) =
        task {
            let path =
                resolvePath archiveDirectory identity.SavedPath
                |> Result.defaultWith invalidOp
            do! SourceFileAccess.requireExclusive db path (Some identity.DocumentId)
            let! bytes = readBounded fs MaxSourceBytes path
            if not (String.Equals(SourceFiles.hash bytes, identity.Sha256, StringComparison.OrdinalIgnoreCase)) then
                invalidOp $"Source hash mismatch for document {identity.DocumentId}: {identity.SavedPath}"
            return { Path = path; Bytes = bytes }
        }

    /// A sidecar claimed as a source is not an extraction artifact.
    let readExtraction (db: Algebra.Database) fs archiveDirectory sourcePath =
        task {
            let path =
                resolvePath archiveDirectory (sourcePath + ".extracted.md")
                |> Result.defaultWith invalidOp
            do! SourceFileAccess.requireUnclaimed db path
            let! bytes = readBounded fs MaxTextBytes path
            return decode bytes
        }
