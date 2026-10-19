module Hermes.Tests.SourceIdentitySchemaTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.FileContentStartupFixtures

let private tables =
    [ "documents"; "stage_completions"; "document_content_fts"; "document_generations"
      "archive_source_claims"; "corrections"; "tags"; "stage_publications" ]

let private historyTables =
    [ "archive_source_claims"; "corrections"; "tags"; "stage_publications"
      "reflow_operations"; "reflow_operation_stages" ]

let private seed db =
    execute db
        """INSERT INTO documents(id,source_type,saved_path,category,sha256,stage,extracted_amount)
           VALUES (1,'manual_drop','identity/a.png','tax','synthetic-a','embedded',42.5);
           INSERT INTO document_generations(document_id,generation) VALUES (1,3);
           INSERT INTO stage_completions(document_id,stage_name)
           VALUES (1,'extract'),(1,'triage'),(1,'deep-comprehend'),(1,'embed');
           INSERT INTO document_content_fts(rowid,text,kind) VALUES (1,'oldidentitymarker','extraction');
           INSERT INTO corrections(document_id,field,corrected_value,note)
           VALUES (1,'extracted_amount','42.5','retained synthetic correction');
           INSERT INTO tags(document_id,tag,source) VALUES (1,'owner-tag','user');
           INSERT INTO stage_publications(document_id,stage_name,generation,response_json)
           VALUES (1,'deep-comprehend',3,'{"history":"retained"}');
           INSERT INTO archive_source_claims(ownership_key,sha256)
           VALUES (hermes_saved_path_key('history/unrelated.md'),'retained-claim');""" []

let private withDatabase verify =
    task {
        let db = TestHelpers.createDb ()
        try
            do! seed db
            do! verify db
        finally db.dispose ()
    }

let private assertInvalidated db =
    task {
        let! generation = Generation.current db 1L
        Assert.Equal(4L, generation.Value)
        let! completions = scalar db "SELECT COUNT(*) FROM stage_completions" []
        let! indexed = scalar db "SELECT COUNT(*) FROM document_content_fts" []
        Assert.Equal(0L, completions)
        Assert.Equal(0L, indexed)
        let! rows = db.execReader "SELECT stage,extracted_amount FROM documents WHERE id=1" []
        let reader = Prelude.RowReader(List.exactlyOne rows)
        Assert.Equal("received", reader.String "stage" "")
        Assert.Equal(Some 42.5, reader.OptFloat "extracted_amount")
    }

let private checkChange assignment db =
    task {
        do! assertPrerequisites db
        let! before = snapshot db historyTables
        do! execute db $"UPDATE documents SET {assignment} WHERE id=1" []
        do! assertInvalidated db
        let! after = snapshot db historyTables
        Assert.Equal<Snapshot>(before, after)
    }

[<Theory>]
[<InlineData("saved_path='identity/b.png'")>]
[<InlineData("sha256='synthetic-b'")>]
[<InlineData("source_type='email_body'")>]
[<Trait("Category", "Integration")>]
let ``Database-only source changes supersede generations and current completions without output tables`` assignment =
    withDatabase (checkChange assignment)

let private checkMetadata db =
    task {
        let! before = snapshot db tables
        do! execute db
                """UPDATE documents SET category='invoices',subject='changed metadata',
                   saved_path=saved_path,sha256=sha256,source_type=source_type WHERE id=1""" []
        let! generation = Generation.current db 1L
        Assert.Equal(3L, generation.Value)
        let! after = snapshot db [ "stage_completions"; "document_content_fts"; "document_generations" ]
        Assert.Equal<Map<string, obj> list>(before.Rows.["stage_completions"], after.Rows.["stage_completions"])
        Assert.Equal<Map<string, obj> list>(before.Rows.["document_content_fts"], after.Rows.["document_content_fts"])
        Assert.Equal<Map<string, obj> list>(before.Rows.["document_generations"], after.Rows.["document_generations"])
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Reclassification metadata and no-op source assignments do not supersede extraction`` () =
    withDatabase checkMetadata

let private rolledBackChange (scope: Algebra.TransactionScope) =
    task {
        let! _ = scope.execNonQuery "UPDATE documents SET saved_path='identity/b.png' WHERE id=1" []
        return Error "synthetic rollback"
    }

let private checkRollback db =
    task {
        let! before = snapshot db tables
        let! result = db.inTransaction rolledBackChange
        Assert.True(Result.isError result)
        let! after = snapshot db tables
        Assert.Equal<Snapshot>(before, after)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Source mutation generation index and completion invalidation roll back together`` () =
    withDatabase checkRollback

let private installOldTrigger db version =
    execute db
        """DROP TRIGGER content_fts_source_change;
           CREATE TRIGGER content_fts_source_change
           AFTER UPDATE OF saved_path,sha256,source_type ON documents
           BEGIN DELETE FROM document_content_fts WHERE rowid=old.id; END;
           DELETE FROM schema_version;
           INSERT INTO schema_version(version) VALUES (@version);"""
        [ "@version", Database.boxVal version ]

let private checkUpgrade version db =
    task {
        do! installOldTrigger db version
        let! before = snapshot db tables
        do! initializeTwice db
        let! after = snapshot db tables
        Assert.Equal<Snapshot>(before, after)
        do! checkChange "saved_path='identity/b.png'" db
    }

[<Theory>]
[<InlineData(8)>]
[<InlineData(12)>]
[<InlineData(14)>]
[<InlineData(15)>]
[<InlineData(16)>]
[<Trait("Category", "Integration")>]
let ``Repeated initialization upgrades the old source trigger without disturbing the current ledger`` version =
    withDatabase (checkUpgrade version)
