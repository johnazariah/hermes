module Hermes.Tests.FileContentStartupFixtures

open System.Threading.Tasks
open Xunit
open Hermes.Core

type Snapshot =
    { Rows: Map<string, Map<string, obj> list>
      MetadataFts: Map<string, obj> list }

let execute (db: Algebra.Database) sql parameters =
    task {
        let! _ = db.execNonQuery sql parameters
        return ()
    }

let initialize (db: Algebra.Database) =
    task {
        let! result = db.initSchema ()
        Assert.True(Result.isOk result, $"{result}")
    }

let initializeTwice db =
    [ 1; 2 ]
    |> Prelude.foldTask (fun () _ -> initialize db) ()

let scalar (db: Algebra.Database) sql parameters =
    task {
        let! value = db.execScalar sql parameters
        return Assert.IsType<int64>(value)
    }

let private rows (db: Algebra.Database) tables =
    tables
    |> Prelude.foldTask
        (fun collected table ->
            task {
                let! values = db.execReader $"SELECT * FROM {table} ORDER BY rowid" []
                return collected |> Map.add table values
            })
        Map.empty

let snapshot (db: Algebra.Database) tables =
    task {
        let! metadata = rows db tables
        let! fts =
            db.execReader
                """SELECT rowid FROM documents_fts
                   WHERE documents_fts MATCH 'vendor OR metadatamigrationmarker'
                   ORDER BY rowid""" []
        return { Rows = metadata; MetadataFts = fts }
    }

let preservedTables =
    [ "documents"; "archive_source_claims"; "corrections"; "tags"; "dead_letters"
      "document_generations"; "reflow_operations"; "reflow_operation_stages" ]

let seedMetadata db =
    execute db
        """INSERT INTO archive_source_claims(ownership_key,sha256)
           VALUES (hermes_saved_path_key('history/source.md'),'synthetic-source-hash');
           INSERT INTO documents
             (id,source_type,saved_path,category,sha256,subject,
              extracted_amount,classification_tier,starred)
           VALUES
             (1,'manual_drop','history/source.md','tax','synthetic-source-hash',
              'metadatamigrationmarker',42.5,'manual',1);
           INSERT INTO corrections(document_id,field,corrected_value,note)
           VALUES (1,'extracted_amount','42.5','preserved synthetic correction');
           INSERT INTO tags(document_id,tag,source) VALUES (1,'owner-tag','user');
           INSERT INTO dead_letters(doc_id,stage,error,failed_at)
           VALUES (1,'extract','preserved synthetic failure','2000-01-01');
           INSERT INTO document_generations(document_id,generation) VALUES (1,3);
           INSERT INTO reflow_operations
             (id,document_id,operation_kind,requested_mode,lifecycle,dag_signature)
           VALUES (1,1,'reembed','apply','completed','synthetic-dag');
           INSERT INTO reflow_operation_stages(operation_id,stage_name,outcome)
           VALUES (1,'embed','reran');""" []

/// Models the old Database-only startup boundary, before PipelineV5.initSchema.
/// It deliberately has a persisted version and metadata but no completion ledger.
let removePipelinePrerequisite db version =
    execute db
        """DROP TRIGGER IF EXISTS content_fts_extract_completion_delete;
           DROP TRIGGER IF EXISTS content_fts_document_delete;
           DROP TRIGGER IF EXISTS content_fts_source_change;
           DROP TABLE document_content_fts;
           DROP TABLE stage_completions;
           DELETE FROM schema_version;
           INSERT INTO schema_version(version,applied_at) VALUES (@version,'2000-01-01');"""
        [ "@version", Database.boxVal version ]

let assertPrerequisites (db: Algebra.Database) =
    task {
        let! count =
            scalar db
                """SELECT COUNT(*) FROM sqlite_master WHERE name IN
                   ('stage_completions','idx_stage_completions_stage',
                    'document_content_fts','content_fts_extract_completion_delete')""" []
        Assert.Equal(4L, count)
        let! extraction = db.tableExists "extraction"
        Assert.False(extraction, "This regression must not depend on V5 output initialization")
    }

let seedTriggerRows db =
    execute db
        """INSERT INTO documents(id,source_type,saved_path,category,sha256)
           VALUES (1,'email_body','history/body.md','invoices','body-sha'),
                  (2,'manual_drop','history/receipt.png','invoices','receipt-sha');
           INSERT INTO stage_completions(document_id,stage_name,completed_at)
           VALUES (1,'extract','2000-01-01'),(2,'extract','2000-01-02');
           INSERT INTO document_content_fts(rowid,text,kind)
           VALUES (1,'immutablebodymarker','source'),(2,'generatedreceiptmarker','extraction');""" []

let assertHistory (db: Algebra.Database) previousVersion =
    task {
        let! count = scalar db "SELECT COUNT(*) FROM schema_version" []
        Assert.Equal(2L, count)
        let! retained =
            scalar db
                """SELECT COUNT(*) FROM schema_version
                   WHERE version=@old AND applied_at='2000-01-01'"""
                [ "@old", Database.boxVal previousVersion ]
        Assert.Equal(1L, retained)
        let! current = db.schemaVersion ()
        Assert.Equal(Database.CurrentSchemaVersion, current)
    }
