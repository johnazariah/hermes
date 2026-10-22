namespace Hermes.Tests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailHistoryFixtures

module AsyncEmailFixture =
    type Input =
        { History: History
          Incoming: Domain.EmailMessage
          Sibling: Domain.EmailMessage
          File: Domain.EmailAttachment
          Initial: DateTimeOffset }

    type Baseline =
        { Documents: int64
          Files: Map<string, string>
          Watermark: DateTimeOffset }

    let providerError = "synthetic fault after pending provider capability"
    let storageError = "synthetic fault after pending attachment INSERT"
    let ownerText = "independent canonical path owner"

    let arrange (fixture: Fixture) : Task<Input> =
        task {
            let pair = prefixPair ()
            let! history = seedProcessedBody fixture pair.Previous
            let initial = DateTimeOffset(2025, 2, 28, 12, 0, 0, TimeSpan.Zero)
            do! seedWatermark fixture initial
            return
                { History = history
                  Incoming = { pair.Replacement with BodyText = Some "<p>replacement &amp; body</p>" }
                  Sibling = message "async-sibling" "separate-thread" (Some "sibling body")
                  File = attachment "async-source.pdf" "asynchronous attachment bytes"
                  Initial = initial }
        }

    let feed (input: Input) =
        let messages = [ input.Incoming; input.Sibling ]
        provider (pages messages) messages
            (Map.ofList [ input.Incoming.ProviderId, [ input.File ] ])

    let baseline
        (fixture: Fixture) (documents: int64) (timestamp: DateTimeOffset) : Baseline =
        { Documents = documents; Files = snapshot fixture; Watermark = timestamp }

    let rejectAttachment (fixture: Fixture) : Task<unit> =
        task {
            let! _ =
                fixture.Database.execNonQuery
                    $"""CREATE TRIGGER reject_async_attachment BEFORE INSERT ON documents
                        WHEN new.original_name = 'async-source.pdf'
                        BEGIN SELECT RAISE(ABORT, '{storageError}'); END""" []
            return ()
        }

    let allowAttachment (fixture: Fixture) : Task<unit> =
        task {
            let! _ = fixture.Database.execNonQuery "DROP TRIGGER reject_async_attachment" []
            return ()
        }

module AsyncEmailAssertions =
    open AsyncEmailFixture

    let count (db: Algebra.Database) sql parameters : Task<int64> =
        task {
            let! value = db.execScalar sql parameters
            return Assert.IsType<int64>(value)
        }

    let state
        (fixture: Fixture) (input: Input)
        (db: Algebra.Database) (expectedDocuments: int64) : Task<unit> =
        task {
            let scoped = { fixture with Database = db }
            do! assertPreserved scoped input.History
            let! actual = documentCount scoped
            Assert.Equal(expectedDocuments, actual)
        }

    let watermark (fixture: Fixture) (expected: DateTimeOffset) : Task<unit> =
        task {
            let! actual = EmailSync.loadSyncState fixture.Database fixture.Account
            Assert.Equal(Some expected, actual)
        }

    let before
        (fixture: Fixture) (input: Input) (baseline: Baseline)
        (reader: ChannelReader<Document.T>) (db: Algebra.Database) : Task<unit> =
        task {
            do! state fixture input db baseline.Documents
            Assert.Equal<Map<string, string>>(baseline.Files, snapshot fixture)
            Assert.Equal(0, reader.Count)
            do! watermark { fixture with Database = db } baseline.Watermark
        }

    let between
        (fixture: Fixture) (input: Input) (documents: int64) (outputs: int)
        (reader: ChannelReader<Document.T>) (db: Algebra.Database) : Task<unit> =
        task {
            do! state fixture input db documents
            Assert.Equal(outputs, reader.Count)
            do! watermark { fixture with Database = db } input.Initial
        }

    let body (fixture: Fixture) (incoming: Domain.EmailMessage) clean =
        task {
            let text = $"# Synthetic channel document\n\n**From:** synthetic@example.test  \n**Date:** 2025-03-01\n\n---\n\n{clean}"
            let expected = ThreadBodySource.create fixture.Account incoming (fixture.Clock.utcNow ()) clean
            let! rows = fixture.Database.execReader
                            "SELECT * FROM documents WHERE gmail_id = @id AND source_type = 'email_body'"
                            [ "@id", Database.boxVal incoming.ProviderId ]
            let document = Assert.Single<Map<string, obj>>(rows)
            Assert.Equal(expected.SavedPath, required "saved_path" document)
            Assert.Equal(incoming.ThreadId, required "thread_id" document)
            Assert.Equal(fixture.Account, required "account" document)
            do! assertRegistered fixture document
            let! bytes = fixture.Files.Fs.readAllBytes (fullPath fixture expected.SavedPath)
            Assert.True(Encoding.UTF8.GetBytes(text) = bytes)
            let! index = fixture.Database.execReader "SELECT text, kind FROM document_content_fts WHERE rowid = @id"
                            [ "@id", Database.boxVal (Document.id document) ]
            Assert.Equal<Map<string, obj>>(Map.ofList [ "text", text :> obj; "kind", "source" :> obj ],
                                          Assert.Single<Map<string, obj>>(index))
        }

    let pendingStore
        (fixture: Fixture) (input: Input) (siblingAlready: bool)
        (reader: ChannelReader<Document.T>) (db: Algebra.Database) : Task<unit> =
        task {
            let scoped = { fixture with Database = db }
            do! between fixture input (if siblingAlready then 3L else 2L) 0 reader db
            do! assertAcknowledged scoped input.Incoming.ProviderId 1L
            do! assertAcknowledged scoped input.Sibling.ProviderId (if siblingAlready then 1L else 0L)
            do! body scoped input.Incoming "replacement & body"
            let path = attachmentPath fixture input.Incoming input.File
            let! bytes = fixture.Files.Fs.readAllBytes path
            Assert.True(input.File.Content = bytes)
            do! assertClaim scoped path (SourceFiles.hash input.File.Content)
        }

    let totals (fixture: Fixture) documents sources =
        task {
            let! actual = documentCount fixture
            let! claims = count fixture.Database "SELECT COUNT(*) FROM archive_source_claims" []
            let! indexed = count fixture.Database "SELECT COUNT(*) FROM document_content_fts" []
            let! ledger = count fixture.Database "SELECT COUNT(*) FROM stage_completions" []
            let! changed = count fixture.Database "SELECT COUNT(*) FROM document_generations WHERE generation <> 0" []
            let! collisions = fixture.Database.execReader
                                "SELECT hermes_saved_path_key(saved_path) FROM documents GROUP BY hermes_saved_path_key(saved_path) HAVING COUNT(*) > 1" []
            Assert.Equal(documents, actual)
            Assert.Equal(documents, claims)
            Assert.Equal(sources, indexed)
            Assert.Equal(4L, ledger)
            Assert.Equal(0L, changed)
            Assert.Empty(collisions)
        }

    let artifact (fixture: Fixture) (input: Input) =
        task {
            let! rows = fixture.Database.execReader
                            "SELECT * FROM documents WHERE gmail_id = @id AND source_type = 'email_attachment'"
                            [ "@id", Database.boxVal input.Incoming.ProviderId ]
            let document = Assert.Single<Map<string, obj>>(rows)
            let path = attachmentPath fixture input.Incoming input.File
            Assert.Equal(Path.GetRelativePath(fixture.Config.ArchiveDir, path), required "saved_path" document)
            Assert.Equal(input.File.FileName, required "original_name" document)
            Assert.Equal(input.Incoming.ThreadId, required "thread_id" document)
            Assert.Equal(fixture.Account, required "account" document)
            Assert.Equal(Some input.File.SizeBytes, Document.decode<int64> "size_bytes" document)
            Assert.Equal(SourceFiles.hash input.File.Content, required "sha256" document)
            do! assertRegistered fixture document
            let! bytes = fixture.Files.Fs.readAllBytes path
            Assert.True(input.File.Content = bytes)
        }

    let private requireText (description: string) (value: string | null) : string =
        match value with
        | null -> failwith $"Missing {description}"
        | text -> text

    let private sidecarName (entry: JsonElement) : string =
        entry.GetProperty("name").GetString()
        |> requireText "sidecar name"

    let private fileName (path: string) : string =
        Path.GetFileName(path)
        |> requireText $"file name for {path}"

    let sidecar (fixture: Fixture) (input: Input) =
        let path = Path.Combine(folder fixture input.Incoming, ".hermes.json")
        let text = fixture.Files.Get path |> Option.defaultWith (fun () -> failwith path)
        use parsed = JsonDocument.Parse(text)
        let names =
            parsed.RootElement.GetProperty("files").EnumerateArray()
            |> Seq.map sidecarName
            |> Seq.toList
        let current = ThreadBodySource.create fixture.Account input.Incoming (fixture.Clock.utcNow ()) "replacement & body"
        let expected =
            [ fileName (required "saved_path" input.History.Document)
              fileName current.SavedPath
              fileName (attachmentPath fixture input.Incoming input.File) ]
        Assert.Equal<string list>(List.sort expected, List.sort names)

    let finished fixture input (result: RunResult) =
        task {
            do! assertPreserved fixture input.History
            for document in result.Documents do do! assertRegistered fixture document
            Assert.Equal(result.Documents.Length, result.Documents |> List.map Document.id |> Set.ofList |> Set.count)
            do! body fixture input.Incoming "replacement & body"
            do! body fixture input.Sibling "sibling body"
            do! artifact fixture input
            sidecar fixture input
            do! totals fixture 4L 3L
            do! assertAcknowledged fixture input.Incoming.ProviderId 1L
            do! assertAcknowledged fixture input.Sibling.ProviderId 1L
            do! watermark fixture (fixture.Clock.utcNow ())
        }

    let rejected fixture input error (result: RunResult) =
        task {
            Assert.Equal(1, result.Summary.MessagesProcessed)
            Assert.Equal(1, result.Summary.AttachmentsDownloaded)
            Assert.Equal(0, result.Summary.DuplicatesSkipped)
            Assert.Contains(error, Assert.Single<string>(result.Summary.Errors))
            let document = Assert.Single<Document.T>(result.Documents)
            Assert.Equal(input.Sibling.ProviderId, required "gmail_id" document)
            do! assertRegistered fixture document
            do! assertPreserved fixture input.History
            do! assertAcknowledged fixture input.Incoming.ProviderId 0L
            do! assertAcknowledged fixture input.Sibling.ProviderId 1L
            let! incoming = count fixture.Database "SELECT COUNT(*) FROM documents WHERE gmail_id = @id"
                                [ "@id", Database.boxVal input.Incoming.ProviderId ]
            Assert.Equal(0L, incoming)
            do! body fixture input.Sibling "sibling body"
            do! watermark fixture input.Initial
        }
