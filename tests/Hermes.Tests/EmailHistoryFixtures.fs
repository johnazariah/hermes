module Hermes.Tests.EmailHistoryFixtures

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

type History =
    { Document: Document.T
      Rows: Map<string, Map<string, obj> list>
      Files: Map<string, string> }

let prefixPair () : BodyPair =
    { Previous = message "abcdef1111111111" "immutable-thread" (Some "original body")
      Replacement = message "abcdef2222222222" "immutable-thread" (Some "replacement body") }

let private dependencies (fixture: Fixture) : Stages.Deps =
    { Fs = fixture.Files.Fs
      Db = fixture.Database
      Logger = fixture.Logger
      Clock = fixture.Clock
      Extractor = Interpreters.nullTextExtractor
      Embedder = None
      ChatProvider = None
      TriageProvider = None
      ContentRules = []
      ComprehensionPrompt = None
      TriagePrompt = None
      Preferences = ""
      ArchiveDir = fixture.Config.ArchiveDir }

let private seedStatements: string list =
    [ """UPDATE documents SET stage = 'embedded', starred = 1,
           extracted_at = 'immutable-extract', embedded_at = 'immutable-embed',
           chunk_count = 2 WHERE id = @id"""
      """INSERT INTO extraction (document_id, extracted_vendor, method)
         VALUES (@id, 'history-vendor', 'history-method')"""
      """INSERT INTO triage (document_id, document_type, category, confidence)
         VALUES (@id, 'letter', 'history-category', 0.9)"""
      """INSERT INTO comprehension (document_id, document_type, category, confidence)
         VALUES (@id, 'letter', 'history-category', 0.8)"""
      """INSERT INTO embedding (document_id, chunk_count) VALUES (@id, 2)"""
      """INSERT INTO tags (document_id, tag, source, created_by)
         VALUES (@id, 'keep-user-tag', 'user', 'history-user')"""
      """INSERT INTO reminders (document_id, category, notes)
         VALUES (@id, 'history-category', 'immutable user note')"""
      """INSERT INTO stage_completions (document_id, stage_name)
         VALUES (@id, 'extract'), (@id, 'triage'),
                (@id, 'deep-comprehend'), (@id, 'embed')""" ]

let private readRows (fixture: Fixture) (id: int64)
    : Task<Map<string, Map<string, obj> list>> =
    let queries =
        [ "documents", "SELECT * FROM documents WHERE id = @id"
          "extraction", "SELECT * FROM extraction WHERE document_id = @id"
          "triage", "SELECT * FROM triage WHERE document_id = @id"
          "comprehension", "SELECT * FROM comprehension WHERE document_id = @id"
          "embedding", "SELECT * FROM embedding WHERE document_id = @id"
          "tags", "SELECT * FROM tags WHERE document_id = @id ORDER BY id"
          "reminders", "SELECT * FROM reminders WHERE document_id = @id ORDER BY id"
          "stage_completions",
              "SELECT * FROM stage_completions WHERE document_id = @id ORDER BY stage_name" ]
    Prelude.foldTask
        (fun rows (name, sql) ->
            task {
                let! values = fixture.Database.execReader sql [ "@id", Database.boxVal id ]
                return rows |> Map.add name values
            })
        Map.empty queries

let private historicalFiles (fixture: Fixture) (document: Document.T) : Map<string, string> =
    let path = fullPath fixture (required "saved_path" document)
    [ path; path + ".extracted.md"; path + ".comprehension.json" ]
    |> List.map (fun name ->
        name, fixture.Files.Get name |> Option.defaultWith (fun () -> failwith name))
    |> Map.ofList

let seedProcessedBody (fixture: Fixture) (incoming: Domain.EmailMessage) : Task<History> =
    task {
        let! document = seedBody fixture incoming
        do! PipelineV5.initSchema fixture.Database (StagesV5.standardStages (dependencies fixture))
        let id = Document.id document
        for sql in seedStatements do
            let! _ = fixture.Database.execNonQuery sql [ "@id", Database.boxVal id ]
            ()
        let path = fullPath fixture (required "saved_path" document)
        fixture.Files.Put (path + ".extracted.md") "immutable extracted text"
        fixture.Files.Put (path + ".comprehension.json") """{"immutable":"comprehension"}"""
        let! rows = readRows fixture id
        let saved = rows["documents"] |> List.exactlyOne
        return { Document = saved; Rows = rows; Files = historicalFiles fixture saved }
    }

let assertPreserved (fixture: Fixture) (history: History) : Task<unit> =
    task {
        do! assertRegistered fixture history.Document
        let! rows = readRows fixture (Document.id history.Document)
        Assert.Equal<Map<string, Map<string, obj> list>>(history.Rows, rows)
        Assert.Equal<Map<string, string>>(history.Files, historicalFiles fixture history.Document)
        let! violations = fixture.Database.execReader "PRAGMA foreign_key_check" []
        Assert.Empty(violations)
    }
