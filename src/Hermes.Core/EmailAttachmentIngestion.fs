namespace Hermes.Core

open System
open System.IO
open System.Threading.Tasks

/// Attachment registration inside the enclosing message transaction.
[<RequireQualifiedAccess>]
module internal EmailAttachmentIngestion =

    let sourceType = "email_attachment"

    type Prepared =
        { Attachment: Domain.EmailAttachment
          SavedPath: string
          Sha256: string }

    let prepare (account: string) (message: Domain.EmailMessage) (now: DateTimeOffset)
        (attachment: Domain.EmailAttachment) : Prepared =
        let domain = ArchiveWriter.extractSenderDomain (message.Sender |> Option.defaultValue "")
        let folder =
            ArchiveWriter.threadFolderPath account domain
                (message.Subject |> Option.defaultValue "(no subject)") message.ThreadId
        let sha256 = SourceFiles.hash attachment.Content
        let name =
            ArchiveWriter.attachmentFileName
                (message.Date |> Option.defaultValue now) attachment.FileName sha256
        { Attachment = attachment
          SavedPath = Path.Combine(folder, name)
          Sha256 = sha256 }

    let private duplicate (db: Algebra.Database) (sha256: string) : Task<bool> =
        task {
            let! count =
                db.execScalar "SELECT COUNT(*) FROM documents WHERE sha256 = @sha"
                    [ "@sha", Database.boxVal sha256 ]
            return unbox<int64> count > 0L
        }

    let private fileName (file: Prepared) : string =
        file.SavedPath
        |> Path.GetFileName
        |> Option.ofObj
        |> Option.filter (String.IsNullOrWhiteSpace >> not)
        |> Option.defaultWith (fun () -> invalidOp "Attachment saved path has no file name")

    let private metadata (account: string) (message: Domain.EmailMessage) (now: DateTimeOffset)
        (file: Prepared) : ArchiveWriter.SidecarData =
        { Version = 2
          SourceType = sourceType
          Account = account
          ProviderId = message.ProviderId
          ThreadId = message.ThreadId
          Sender = message.Sender
          Subject = message.Subject
          ReceivedAt = now.ToString("o")
          Files =
              [ { Name = fileName file
                  MimeType = file.Attachment.MimeType
                  SizeBytes = file.Attachment.SizeBytes
                  Sha256 = file.Sha256 } ] }

    let private writeMetadata (db: Algebra.Database) (fs: Algebra.FileSystem)
        (archiveDirectory: string) (account: string) (message: Domain.EmailMessage)
        (now: DateTimeOffset) (file: Prepared) : Task<unit> =
        let relativeFolder =
            file.SavedPath |> Path.GetDirectoryName |> Option.ofObj |> Option.defaultValue ""
        ArchiveWriter.writeSidecar
            (SourceFiles.metadataFileSystem db fs)
            (Path.Combine(archiveDirectory, relativeFolder))
            (metadata account message now file)

    let private registerNew (db: Algebra.Database) (fs: Algebra.FileSystem)
        (archiveDirectory: string) (account: string) (message: Domain.EmailMessage) (now: DateTimeOffset)
        (record: Algebra.Database -> Domain.EmailAttachment -> string -> string -> Task<int64>)
        (file: Prepared) : Task<ThreadBodyIngestion.Saved option> =
        task {
            do! SourceFiles.writeNew db fs
                    (Path.Combine(archiveDirectory, file.SavedPath)) file.Attachment.Content
            do! writeMetadata db fs archiveDirectory account message now file
            let! id = record db file.Attachment file.SavedPath file.Sha256
            let saved: ThreadBodyIngestion.Saved =
                { DocumentId = id; SavedPath = file.SavedPath }
            return Some saved
        }

    let register (db: Algebra.Database) (fs: Algebra.FileSystem)
        (archiveDirectory: string) (account: string) (message: Domain.EmailMessage) (now: DateTimeOffset)
        (record: Algebra.Database -> Domain.EmailAttachment -> string -> string -> Task<int64>)
        (file: Prepared) : Task<ThreadBodyIngestion.Saved option> =
        task {
            let! exists = duplicate db file.Sha256
            if exists then return None
            else
                return! registerNew db fs archiveDirectory account message now record file
        }
