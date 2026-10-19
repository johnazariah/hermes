namespace Hermes.Core

open System
open System.IO
open System.Text

/// Pure construction of a thread-body source and its sidecar metadata.
[<RequireQualifiedAccess>]
module ThreadBodySource =

    let sourceType = "email_body"

    type Source =
        { FileName: string
          SavedPath: string
          Sha256: string
          Bytes: byte array }

    let private markdown (message: Domain.EmailMessage) (clean: string) =
        let subject = message.Subject |> Option.defaultValue "(no subject)"
        let sender = message.Sender |> Option.defaultValue "unknown"
        let date =
            message.Date
            |> Option.map (fun value -> value.ToString("yyyy-MM-dd"))
            |> Option.defaultValue "undated"
        $"# {subject}\n\n**From:** {sender}  \n**Date:** {date}\n\n---\n\n{clean}"

    let create
        (account: string) (message: Domain.EmailMessage)
        (_now: DateTimeOffset) (clean: string) : Source =
        let domain = ArchiveWriter.extractSenderDomain (message.Sender |> Option.defaultValue "")
        let folder =
            ArchiveWriter.threadFolderPath account domain
                (message.Subject |> Option.defaultValue "(no subject)") message.ThreadId
        let name =
            ArchiveWriter.messageFileName
                (message.Date |> Option.defaultValue DateTimeOffset.UnixEpoch)
                "message" message.ProviderId
        let bytes = markdown message clean |> Encoding.UTF8.GetBytes
        { FileName = name
          SavedPath = Path.Combine(folder, name)
          Sha256 = SourceFiles.hash bytes
          Bytes = bytes }

    let folder (archiveDirectory: string) (source: Source) : string =
        source.SavedPath
        |> Path.GetDirectoryName
        |> Option.ofObj
        |> Option.defaultValue ""
        |> fun relative -> Path.Combine(archiveDirectory, relative)

    let sidecar
        (account: string) (message: Domain.EmailMessage)
        (now: DateTimeOffset) (source: Source) : ArchiveWriter.SidecarData =
        { Version = 2
          SourceType = sourceType
          Account = account
          ProviderId = message.ProviderId
          ThreadId = message.ThreadId
          Sender = message.Sender
          Subject = message.Subject
          ReceivedAt = now.ToString("o")
          Files =
              [ { Name = source.FileName; MimeType = "text/markdown"
                  SizeBytes = int64 source.Bytes.Length; Sha256 = source.Sha256 } ] }
