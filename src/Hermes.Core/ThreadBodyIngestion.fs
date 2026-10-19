namespace Hermes.Core

open System
open System.IO
open System.Threading.Tasks

/// Append immutable message-body sources without replacing thread history.
[<RequireQualifiedAccess>]
module ThreadBodyIngestion =

    type Source = ThreadBodySource.Source

    type Saved =
        { DocumentId: int64
          SavedPath: string }

    let internal prepare account message now (clean: string option) : Source option =
        clean
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.map (ThreadBodySource.create account message now)

    let private writeMetadata db fs archiveDirectory account message now source =
        ArchiveWriter.writeSidecar
            (SourceFiles.metadataFileSystem db fs)
            (ThreadBodySource.folder archiveDirectory source)
            (ThreadBodySource.sidecar account message now source)

    let private alreadyRegistered
        (db: Algebra.Database) (account: string) (messageId: string) : Task<bool> =
        task {
            let! count =
                db.execScalar
                    """SELECT COUNT(*) FROM documents
                       WHERE source_type = 'email_body'
                         AND account = @account AND gmail_id = @id"""
                    [ "@account", Database.boxVal account
                      "@id", Database.boxVal messageId ]
            return unbox<int64> count > 0L
        }

    let private append
        (db: Algebra.Database) fs archiveDirectory account (message: Domain.EmailMessage) now
        (source: Source) (record: Algebra.Database -> Source -> Task<int64>)
        : Task<Saved option> =
        task {
            let! duplicate = alreadyRegistered db account message.ProviderId
            if duplicate then return None
            else
                let path = Path.Combine(archiveDirectory, source.SavedPath)
                do! SourceFiles.writeNew db fs path source.Bytes
                let! id = record db source
                do! FileContentIndex.replace db.execNonQuery id
                        { Kind = FileContentIndex.EmailBody
                          Text = ArchiveContent.decode source.Bytes }
                do! writeMetadata db fs archiveDirectory account message now source
                return Some { DocumentId = id; SavedPath = source.SavedPath }
        }

    /// Registration joins the caller's transaction and never removes old bytes.
    let internal register db fs archiveDirectory account message now
        (source: Source option) (record: Algebra.Database -> Source -> Task<int64>) =
        source
        |> Option.map (fun value ->
            append db fs archiveDirectory account message now value record)
        |> Option.defaultWith (fun () -> Task.FromResult None)

    /// Standalone convenience API; transactional callers use register.
    let save
        (db: Algebra.Database) (fs: Algebra.FileSystem) archiveDirectory account
        (message: Domain.EmailMessage) (now: DateTimeOffset) (clean: string option)
        (record: Algebra.Database -> Source -> Task<int64>) : Task<Saved option> =
        let source = prepare account message now clean
        db.transaction.Run(fun transaction ->
            register transaction fs archiveDirectory account message now source record)
