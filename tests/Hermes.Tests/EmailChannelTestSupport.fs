module Hermes.Tests.EmailChannelTestSupport

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures

type BodyPair =
    { Previous: Domain.EmailMessage
      Replacement: Domain.EmailMessage }

let required (name: string) (document: Document.T) : string =
    document
    |> Document.decode<string> name
    |> Option.defaultWith (fun () -> failwith $"Missing document field: {name}")

let source (sourceType: string) (result: RunResult) : Document.T =
    result.Documents
    |> List.filter (fun document -> required "source_type" document = sourceType)
    |> List.exactlyOne

let fullPath (fixture: Fixture) (savedPath: string) : string =
    Path.Combine(fixture.Config.ArchiveDir, savedPath)

let folder (fixture: Fixture) (incoming: Domain.EmailMessage) : string =
    let relative =
        ArchiveWriter.threadFolderPath fixture.Account
            (ArchiveWriter.extractSenderDomain (incoming.Sender |> Option.defaultValue ""))
            (incoming.Subject |> Option.defaultValue "(no subject)") incoming.ThreadId
    fullPath fixture relative

let attachmentPath
    (fixture: Fixture) (incoming: Domain.EmailMessage) (file: Domain.EmailAttachment) : string =
    let name =
        ArchiveWriter.attachmentFileName
            (incoming.Date |> Option.defaultValue (fixture.Clock.utcNow ()))
            file.FileName (SourceFiles.hash file.Content)
    Path.Combine(folder fixture incoming, name)

let assertSuccess (processed: int) (downloaded: int) (result: RunResult) : unit =
    Assert.Equal(processed, result.Summary.MessagesProcessed)
    Assert.Equal(downloaded, result.Summary.AttachmentsDownloaded)
    Assert.Equal(downloaded, result.Documents.Length)
    Assert.Empty(result.Summary.Errors)

let assertClaim (fixture: Fixture) (path: string) (sha256: string) : Task<unit> =
    task {
        let! actual =
            fixture.Database.execScalar
                "SELECT sha256 FROM archive_source_claims WHERE ownership_key = hermes_saved_path_key(@path)"
                [ "@path", Database.boxVal path ]
        Assert.Equal(sha256, Assert.IsType<string>(actual))
    }

let assertRegistered (fixture: Fixture) (document: Document.T) : Task<unit> =
    task {
        let path = fullPath fixture (required "saved_path" document)
        let! bytes = fixture.Files.Fs.readAllBytes path
        Assert.True(Document.id document > 0L)
        Assert.Equal(required "sha256" document, SourceFiles.hash bytes)
        let! rows =
            fixture.Database.execReader "SELECT * FROM documents WHERE id = @id"
                [ "@id", Database.boxVal (Document.id document) ]
        Assert.Equal(1, rows.Length)
        Assert.Equal<Map<string, obj>>(document, rows.Head)
        do! assertClaim fixture path (required "sha256" document)
    }

let assertAttachment
    (fixture: Fixture) (expected: Domain.EmailAttachment) (result: RunResult) : Task<unit> =
    task {
        let document = source "email_attachment" result
        let! bytes = fixture.Files.Fs.readAllBytes (fullPath fixture (required "saved_path" document))
        Assert.True(expected.Content = bytes)
        Assert.Equal(expected.FileName, required "original_name" document)
    }

let assertBody (fixture: Fixture) (expected: string) (result: RunResult) : unit =
    let document = source "email_body" result
    let text =
        fixture.Files.Get(fullPath fixture (required "saved_path" document))
        |> Option.defaultWith (fun () -> failwith "Registered body source is missing")
    Assert.Contains(expected, text)
    Assert.DoesNotContain("<p>", text)

let documentCount (fixture: Fixture) : Task<int64> =
    task {
        let! count = fixture.Database.execScalar "SELECT COUNT(*) FROM documents" []
        return Assert.IsType<int64>(count)
    }

let seedWatermark (fixture: Fixture) (timestamp: DateTimeOffset) : Task<unit> =
    task {
        let! _ =
            fixture.Database.execNonQuery
                """INSERT INTO sync_state (account, last_sync_at, message_count)
                   VALUES (@account, @timestamp, 0)
                   ON CONFLICT(account) DO UPDATE SET last_sync_at = excluded.last_sync_at"""
                [ "@account", Database.boxVal fixture.Account
                  "@timestamp", Database.boxVal (timestamp.ToString("o")) ]
        return ()
    }

let seedOwner (fixture: Fixture) (path: string) (text: string) : Task<int64> =
    task {
        let relative = Path.GetRelativePath(fixture.Config.ArchiveDir, path)
        let sha256 = Encoding.UTF8.GetBytes(text) |> SourceFiles.hash
        let! id =
            fixture.Database.execScalar
                """INSERT INTO documents (source_type, saved_path, category, sha256)
                   VALUES ('manual_drop', @path, 'prior-category', @sha) RETURNING id"""
                [ "@path", Database.boxVal relative; "@sha", Database.boxVal sha256 ]
        return Assert.IsType<int64>(id)
    }

let snapshot (fixture: Fixture) : Map<string, string> =
    fixture.Files.Files
    |> Seq.map (fun pair -> pair.Key, pair.Value)
    |> Map.ofSeq

let bodyPair () : BodyPair =
    let threadId = "shared-body-thread"
    let previous =
        { message "body-old" threadId (Some "original body") with
            Date = Some (DateTimeOffset(2025, 2, 28, 0, 0, 0, TimeSpan.Zero)) }
    { Previous = previous
      Replacement = message "body-new" threadId (Some "replacement body") }

let seedBody (fixture: Fixture) (incoming: Domain.EmailMessage) : Task<Document.T> =
    task {
        let! result = run fixture (single incoming []) 1
        assertSuccess 1 1 result
        let document = source "email_body" result
        do! assertRegistered fixture document
        return document
    }

let assertAcknowledged
    (fixture: Fixture) (id: string) (expected: int64) : Task<unit> =
    task {
        let! count =
            fixture.Database.execScalar
                "SELECT COUNT(*) FROM messages WHERE account = @account AND gmail_id = @id"
                [ "@account", Database.boxVal fixture.Account
                  "@id", Database.boxVal id ]
        Assert.Equal(expected, Assert.IsType<int64>(count))
    }

let assertStorageFailure (result: RunResult) : unit =
    Assert.Equal(0, result.Summary.MessagesProcessed)
    Assert.Equal(0, result.Summary.AttachmentsDownloaded)
    Assert.Equal(0, result.Summary.DuplicatesSkipped)
    Assert.Empty(result.Documents)
    Assert.NotEmpty(result.Summary.Errors)
