module Hermes.Tests.SavedPathLegacyMetadataTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailHistoryFixtures

let private legacySchema =
    """CREATE TABLE archive_source_retirements (
           ownership_key TEXT PRIMARY KEY REFERENCES archive_source_claims(ownership_key),
           document_id INTEGER NOT NULL, token TEXT NOT NULL UNIQUE);
       CREATE TRIGGER doc_retiring_path_insert BEFORE INSERT ON documents
       BEGIN SELECT RAISE(ABORT, 'obsolete retirement insert guard'); END;
       CREATE TRIGGER doc_retiring_path_update BEFORE UPDATE ON documents
       BEGIN SELECT RAISE(ABORT, 'obsolete retirement update guard'); END;"""

let private seedLegacyMetadata (fixture: Fixture) (history: History) : Task<unit> =
    task {
        let! _ = fixture.Database.execNonQuery legacySchema []
        let! _ =
            fixture.Database.execNonQuery
                """INSERT INTO archive_source_retirements (ownership_key, document_id, token)
                   VALUES (hermes_saved_path_key(@path), @id, 'legacy-token');
                   DELETE FROM schema_version WHERE version = @current;
                   INSERT OR IGNORE INTO schema_version (version) VALUES (13);"""
                [ "@path", Database.boxVal (required "saved_path" history.Document)
                  "@id", Database.boxVal (Document.id history.Document)
                  "@current", Database.boxVal Database.CurrentSchemaVersion ]
        return ()
    }

let private assertLegacyMetadata (fixture: Fixture) (history: History) : Task<unit> =
    task {
        let! rows = fixture.Database.execReader "SELECT * FROM archive_source_retirements" []
        let value = Prelude.RowReader(rows |> List.exactlyOne)
        Assert.Equal("legacy-token", value.String "token" "")
        Assert.Equal(Document.id history.Document, value.Int64 "document_id" 0L)
        let! version = fixture.Database.schemaVersion ()
        Assert.Equal(Database.CurrentSchemaVersion, version)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Schema14 disables retirement guards without consuming legacy metadata or history`` () =
    withFixture (fun fixture ->
        task {
            let pair = prefixPair ()
            let! history = seedProcessedBody fixture pair.Previous
            do! seedLegacyMetadata fixture history
            let! initialized = fixture.Database.initSchema ()
            Assert.True(Result.isOk initialized)
            do! assertLegacyMetadata fixture history
            do! assertPreserved fixture history
            let! appended = run fixture (single pair.Replacement []) 1
            assertSuccess 1 1 appended
            do! assertPreserved fixture history
            do! assertLegacyMetadata fixture history
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Legacy retirement metadata does not block a nondestructive document update`` () =
    withFixture (fun fixture ->
        task {
            let! history = seedProcessedBody fixture (prefixPair ()).Previous
            do! seedLegacyMetadata fixture history
            let! initialized = fixture.Database.initSchema ()
            Assert.True(Result.isOk initialized)
            let! changed =
                fixture.Database.execNonQuery "UPDATE documents SET starred = starred WHERE id = @id"
                    [ "@id", Database.boxVal (Document.id history.Document) ]
            Assert.Equal(1, changed)
            do! assertPreserved fixture history
            do! assertLegacyMetadata fixture history
        })
