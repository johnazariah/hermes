module Hermes.Tests.FileKeywordSchemaTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.FileKeywordFixtures

let private execute (server: Server) sql =
    task {
        let! _ = server.Db.execNonQuery sql []
        return ()
    }

let private annotations (server: Server) documentId =
    task {
        let! _ =
            server.Db.execNonQuery
                """INSERT INTO tags(document_id,tag,source) VALUES (@id,'owner-tag','user');
                   INSERT INTO corrections(document_id,field,corrected_value,note)
                   VALUES (@id,'extracted_amount','42.5','synthetic owner correction');
                   INSERT INTO dead_letters(doc_id,stage,error,failed_at)
                   VALUES (@id,'triage','synthetic preserved error','2025-03-01');"""
                [ "@id", Database.boxVal documentId ]
        return ()
    }

let private upgradeTwice (server: Server) =
    [ 1; 2 ]
    |> Prelude.foldTask
        (fun () _ ->
            task {
                let! result = server.Db.initSchema ()
                Assert.True(Result.isOk result, $"{result}")
            })
        ()

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Schema16 is additive idempotent and preserves schema15 sources metadata workflow and FTS`` () =
    withServer None (fun server ->
        task {
            let! id = seedExtracted server "migrationcontentmarker"
            do! annotations server id
            do! applyReflow server id "reembed"
            let! before = metadataSnapshot server
            let! hashes = fileHashes server
            do! execute server
                    """DROP TABLE document_content_fts;
                       DELETE FROM schema_version WHERE version > 15;
                       INSERT OR IGNORE INTO schema_version(version) VALUES (15);"""
            do! upgradeTwice server
            let! version = server.Db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
            let! after = metadataSnapshot server
            let! afterHashes = fileHashes server
            Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
            Assert.Equal<Map<string, string>>(hashes, afterHashes)
            do! assertViews server "invoices" id sourcePath "document"
            let! rebuilt = rebuild server [ id ]
            Assert.True(Result.isOk rebuilt, $"{rebuilt}")
            do! assertViews server "migrationcontentmarker" id sourcePath "document"
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Deleting a document removes only its content index projection`` () =
    withServer None (fun server ->
        task {
            let! body = ingestBody server "deletedbodyperidotmarker"
            let id = Document.id body
            let! hashes = fileHashes server
            let! _ =
                server.Db.execNonQuery "DELETE FROM documents WHERE id=@id"
                    [ "@id", Database.boxVal id ]
            let! count =
                server.Db.execScalar "SELECT COUNT(*) FROM document_content_fts WHERE rowid=@id"
                    [ "@id", Database.boxVal id ]
            Assert.Equal(0L, Assert.IsType<int64>(count))
            let! after = fileHashes server
            Assert.Equal<Map<string, string>>(hashes, after)
            do! assertAbsent server "deletedbodyperidotmarker"
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Changing source identity invalidates the old file index`` () =
    withServer None (fun server ->
        task {
            let! id = seedExtracted server "oldidentityobsidianmarker"
            let! _ =
                server.Db.execNonQuery
                    """UPDATE documents SET saved_path='/archive/keyword/replacement.png',
                           sha256='replacement-identity' WHERE id=@id"""
                    [ "@id", Database.boxVal id ]
            do! assertAbsent server "oldidentityobsidianmarker"
        })
