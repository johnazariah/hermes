module Hermes.Tests.ReflowMigrationIntegrationTests

open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailHistoryFixtures

let private reflowTables =
    [ "reflow_operation_stages"; "reflow_operations"
      "stage_publications"; "learned_pattern_evidence"
      "document_generations"; "artifact_folder_revisions"
      "pipeline_stage_attempts" ]

let private rows (db: Algebra.Database) tables =
    tables
    |> Prelude.foldTask
        (fun collected table ->
            task {
                let! values = db.execReader $"SELECT * FROM {table} ORDER BY rowid" []
                return collected |> Map.add table values
            })
        Map.empty

type private Preserved =
    { Metadata: Map<string, Map<string, obj> list>
      Fts: Map<string, obj> list }

let private snapshot (db: Algebra.Database) =
    task {
        let! metadata = rows db [ "archive_source_claims"; "dead_letters" ]
        let! fts =
            db.execReader
                "SELECT rowid FROM documents_fts WHERE documents_fts MATCH 'synthetic' ORDER BY rowid" []
        return { Metadata = metadata; Fts = fts }
    }

let private execute (db: Algebra.Database) sql =
    task {
        let! _ = db.execNonQuery sql []
        return ()
    }

let private upgrade (db: Algebra.Database) =
    let initialize () _ =
        task {
            let! result = db.initSchema ()
            Assert.True(Result.isOk result, $"{result}")
        }
    task {
        do! [ 1; 2 ] |> Prelude.foldTask initialize ()
        do! TestHelpers.initV5 db
    }

let private assertSchema (db: Algebra.Database) =
    let check () table =
        task {
            let! exists = db.tableExists table
            Assert.True(exists, table)
        }
    task {
        do! reflowTables |> Prelude.foldTask check ()
        let! version = db.schemaVersion ()
        Assert.Equal(15, version)
    }

let private mainV14Layout (fixture: Fixture) (history: History) =
    let removeReflow =
        reflowTables
        |> List.map (fun table -> $"DROP TABLE {table};")
        |> String.concat "\n"
    task {
        do! execute fixture.Database removeReflow
        do! execute fixture.Database
                """DROP INDEX IF EXISTS idx_dead_letters_active;
                   DELETE FROM schema_version WHERE version > 14;
                   INSERT OR IGNORE INTO schema_version(version) VALUES (8), (13), (14);"""
        let! _ =
            fixture.Database.execNonQuery
                """INSERT INTO dead_letters(doc_id, stage, error, failed_at)
                   VALUES (@id, 'extract', 'preserve historical failure', '2025-03-01')"""
                [ "@id", Database.boxVal (Document.id history.Document) ]
        return ()
    }

let private verifyMainHistory fixture history before =
    task {
        let! after = snapshot fixture.Database
        Assert.Equal<Preserved>(before, after)
        Assert.NotEmpty(after.Fts)
        do! assertPreserved fixture history
        do! assertSchema fixture.Database
        let! versions =
            fixture.Database.execScalar
                "SELECT COUNT(*) FROM schema_version WHERE version IN (14, 15)" []
        Assert.Equal(2L, unbox<int64> versions)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Main v14 gains reflow without losing claims source history annotations ledger or FTS`` () =
    withFixture (fun fixture ->
        task {
            let! history = seedProcessedBody fixture (prefixPair ()).Previous
            do! mainV14Layout fixture history
            do! SourceFiles.writeNew fixture.Database fixture.Files.Fs
                    "/archive/staged/source.pdf" (Encoding.UTF8.GetBytes "staged source")
            let! before = snapshot fixture.Database
            do! upgrade fixture.Database
            do! verifyMainHistory fixture history before
        })

let private deployedV12 =
    """INSERT INTO documents(id, source_type, saved_path, category, sha256)
       VALUES (1, 'manual_drop', 'deployed-v12.pdf', 'unsorted', 'deployed-sha');
       INSERT INTO stage_completions(document_id, stage_name) VALUES (1, 'extract');
       INSERT INTO reflow_operations
         (id, document_id, operation_kind, requested_mode, lifecycle, dag_signature)
       VALUES (1, 1, 'reembed', 'apply', 'completed', 'deployed-v12');
       INSERT INTO reflow_operation_stages(operation_id, stage_name, outcome)
       VALUES (1, 'embed', 'reran');
       INSERT INTO document_generations(document_id, generation) VALUES (1, 3);
       INSERT INTO learned_pattern_evidence
         (document_id, stage_name, generation, sender_domain, document_type, confidence)
       VALUES (1, 'triage', 3, 'example.test', 'letter', 0.9);
       INSERT INTO stage_publications
         (document_id, stage_name, generation, response_json, current_category)
       VALUES (1, 'triage', 3, '{"summary":"preserved"}', 'unsorted');
       INSERT INTO artifact_folder_revisions(folder_identity, revision)
       VALUES ('archive-relative:.', 4);
       INSERT INTO pipeline_stage_attempts(document_id, stage_name, attempt_token)
       VALUES (1, 'embed', '0123456789abcdef0123456789abcdef');
       DROP TRIGGER doc_saved_path_insert;
       DROP TRIGGER doc_saved_path_update;
       DROP TRIGGER doc_saved_path_delete;
       DROP TABLE archive_source_claims;
       DELETE FROM schema_version;
       INSERT INTO schema_version(version) VALUES (8), (12);"""

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Deployed v12 keeps reflow history and gains main source ownership guards`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! TestHelpers.initV5 db
            do! execute db deployedV12
            let! before = rows db (reflowTables @ [ "stage_completions"; "documents" ])
            do! upgrade db
            let! after = rows db (reflowTables @ [ "stage_completions"; "documents" ])
            Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
            let! claims = db.tableExists "archive_source_claims"
            Assert.True(claims)
            do! assertSchema db
        finally
            db.dispose ()
    }
