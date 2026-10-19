module Hermes.Tests.PersistedReadRaceFixtures

open System
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures

type Payslip =
    { Json: string
      GrossPay: decimal
      WithheldTax: decimal }

type Expected =
    { DocumentId: int64
      Generation: int64
      SavedPath: string
      Sha256: string
      Payslip: Payslip }

type Scenario =
    { Original: Expected
      Replacement: Expected
      Sibling: Expected
      Before: Checkpoint }

type ConfirmationPause =
    { Gate: Gate
      FirstSnapshot: TaskCompletionSource<unit> }

let private folder = "/archive/persisted-read-race"
let private oldPath = folder + "/source-a.png"
let private newPath = folder + "/source-b.png"
let private siblingPath = folder + "/sibling.png"
let private replacementBytes = Encoding.UTF8.GetBytes("immutable synthetic replacement source B")

let private original: Payslip =
    { Json = canonical; GrossPay = 1234.56M; WithheldTax = 234.56M }

let private replacement: Payslip =
    { Json = """{"document_type":"payslip","fields":{"gross_pay":5678.90,"withheld_tax":678.90,"currency":"SYN","earnings":[{"kind":"ordinary","amount":5678.90}]},"unknown_future_field":{"preserved":true},"replacement_source":true}"""
      GrossPay = 5678.90M; WithheldTax = 678.90M }

let private sibling: Payslip =
    { Json = """{"document_type":"payslip","fields":{"gross_pay":8765.43,"withheld_tax":765.43,"currency":"SYN"},"sibling_document":true}"""
      GrossPay = 8765.43M; WithheldTax = 765.43M }

let confirmationPause () : ConfirmationPause =
    { Gate = createGate ()
      FirstSnapshot =
        TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously) }

/// Both snapshots come from real SQLite reads. Pause AFTER the second query has
/// released its database execution gate, but BEFORE its rows reach readCurrent.
/// This specifically exercises the final generation check, not just reloading.
let private confirmationRead pause (db: Algebra.Database) (sql: string) parameters =
    task {
        let! rows = db.execReader sql parameters
        if pause.Gate.Armed.Task.IsCompleted
           && sql.Contains("AS publication_json", StringComparison.Ordinal)
           && not (pause.FirstSnapshot.TrySetResult(())) then
            do! hold pause.Gate
        return rows
    }

let database pause () : Algebra.Database =
    let db = TestHelpers.createDb ()
    { db with execReader = confirmationRead pause db }

let private expected (server: Server) before id generation path payslip : Expected =
    { DocumentId = id; Generation = generation; SavedPath = path
      Sha256 = before.FileHashes.[server.Files.Norm path]; Payslip = payslip }

let private prepared server id siblingId before : Scenario =
    { Original = expected server before id 0L oldPath original
      Replacement = expected server before id 1L newPath replacement
      Sibling = expected server before siblingId 0L siblingPath sibling
      Before = before }

let prepare server =
    task {
        let! id = seedComprehension server oldPath original.Json
        let! siblingId = seedComprehension server siblingPath sibling.Json
        do! SourceFiles.writeNew server.Db server.Files.Fs newPath replacementBytes
        server.Files.Put (folder + "/thread.comprehension.json")
            """{"document_type":"payslip","fields":{"gross_pay":999999.99},"deep_extraction":{"fields":{"shared_unattributed_deep_marker":314159.26}}}"""
        let! before = checkpoint server
        return prepared server id siblingId before
    }

let private assignReplacement server scenario =
    let next = scenario.Replacement
    executeSql server
        """UPDATE documents SET saved_path=@path,folder_path=@folder,sha256=@sha
           WHERE id=@id"""
        [ "@id", Database.boxVal next.DocumentId; "@path", Database.boxVal next.SavedPath
          "@folder", Database.boxVal folder; "@sha", Database.boxVal next.Sha256 ]

let private assertAdvanced (server: Server) scenario =
    task {
        let! current = Generation.current server.Db scenario.Original.DocumentId
        Assert.Equal(scenario.Original.Generation + 1L, current.Value)
        Assert.Equal(scenario.Replacement.Generation, current.Value)
        let! rows = server.Db.execReader
                        "SELECT COUNT(*) AS completions FROM stage_completions WHERE document_id=@id"
                        [ "@id", Database.boxVal scenario.Original.DocumentId ]
        let reader = rows |> List.exactlyOne |> Prelude.RowReader
        Assert.Equal(0L, reader.Int64 "completions" -1L)
    }

/// Explicit fixture writer: complete B while A's response is paused. Merely
/// finding current outputs after resumption must not certify A's generation.
let private publishReplacement server scenario =
    let next = scenario.Replacement
    executeSql server
        """INSERT OR REPLACE INTO extraction(document_id,method) VALUES (@id,'synthetic-current-B');
           INSERT OR REPLACE INTO comprehension(document_id,document_type,category,confidence)
           VALUES (@id,'payslip','payslips',0.99);
           INSERT INTO stage_completions(document_id,stage_name)
           VALUES (@id,'extract'),(@id,'deep-comprehend');
           INSERT INTO stage_publications(document_id,stage_name,generation,response_json)
           VALUES (@id,'deep-comprehend',@generation,@json);"""
        [ "@id", Database.boxVal next.DocumentId; "@generation", Database.boxVal next.Generation
          "@json", Database.boxVal next.Payslip.Json ]

let transition server scenario () =
    task {
        do! assignReplacement server scenario
        do! assertAdvanced server scenario
        do! publishReplacement server scenario
        return! checkpoint server
    }
