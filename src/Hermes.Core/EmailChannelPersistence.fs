namespace Hermes.Core

open System
open System.Threading.Tasks

/// One new email acknowledgement and all its document registrations.
[<RequireQualifiedAccess>]
module internal EmailChannelPersistence =

    type Store =
        { Exists: Algebra.Database -> string -> string -> Task<bool>
          Acknowledge: Algebra.Database -> string -> Domain.EmailMessage -> DateTimeOffset -> Task<unit>
          Body: string -> Domain.EmailMessage -> DateTimeOffset -> Algebra.Database -> ThreadBodyIngestion.Source -> Task<int64>
          Attachment: string -> Domain.EmailMessage -> DateTimeOffset -> Algebra.Database -> Domain.EmailAttachment -> string -> string -> Task<int64> }

    type Context =
        { Database: Algebra.Database
          Files: Algebra.FileSystem
          Logger: Algebra.Logger
          Clock: Algebra.Clock
          Config: Domain.HermesConfig
          Account: string
          CleanBody: string -> string
          Store: Store }

    type Fetched =
        { Message: Domain.EmailMessage
          Attachments: Domain.EmailAttachment list }

    type private Prepared =
        { Message: Domain.EmailMessage
          ReceivedAt: DateTimeOffset
          Body: ThreadBodyIngestion.Source option
          Attachments: EmailAttachmentIngestion.Prepared list }

    type Committed =
        { Documents: ThreadBodyIngestion.Saved list }

    /// All provider calls finish before persistence acquires a write transaction.
    let fetch (provider: Algebra.EmailProvider) (id: string) : Task<Fetched> =
        task {
            let! message = provider.getFullMessage id
            if message.ProviderId <> id then
                invalidOp "Provider returned a different message identity"
            let! attachments = provider.getAttachments id
            return { Message = message; Attachments = attachments }
        }

    let private prepare (context: Context) (fetched: Fetched) : Prepared =
        let now = context.Clock.utcNow ()
        { Message = fetched.Message
          ReceivedAt = now
          Body =
              fetched.Message.BodyText
              |> Option.map context.CleanBody
              |> ThreadBodyIngestion.prepare context.Account fetched.Message now
          Attachments =
              fetched.Attachments
              |> List.filter (fun file -> file.SizeBytes >= int64 context.Config.MinAttachmentSize)
              |> List.map (EmailAttachmentIngestion.prepare context.Account fetched.Message now) }

    let private registerAttachment (context: Context) (prepared: Prepared)
        (db: Algebra.Database) (receipts: ThreadBodyIngestion.Saved list)
        (file: EmailAttachmentIngestion.Prepared) : Task<ThreadBodyIngestion.Saved list> =
        task {
            let! saved =
                EmailAttachmentIngestion.register db context.Files context.Config.ArchiveDir
                    context.Account prepared.Message prepared.ReceivedAt
                    (context.Store.Attachment context.Account prepared.Message prepared.ReceivedAt) file
            return
                saved
                |> Option.map (fun value -> value :: receipts)
                |> Option.defaultValue receipts
        }

    let private register (context: Context) (prepared: Prepared)
        (db: Algebra.Database) : Task<Committed option> =
        task {
            let! exists = context.Store.Exists db context.Account prepared.Message.ProviderId
            if exists then return None
            else
                do! context.Store.Acknowledge db context.Account prepared.Message prepared.ReceivedAt
                let! body =
                    ThreadBodyIngestion.register db context.Files context.Config.ArchiveDir
                        context.Account prepared.Message prepared.ReceivedAt prepared.Body
                        (context.Store.Body context.Account prepared.Message prepared.ReceivedAt)
                let! files =
                    Prelude.foldTask (registerAttachment context prepared db) [] prepared.Attachments
                let bodyFiles = body |> Option.toList
                return Some { Documents = bodyFiles @ List.rev files }
        }

    let persist (context: Context) (fetched: Fetched) : Task<Committed option> =
        let prepared = prepare context fetched
        context.Database.transaction.Run(register context prepared)
