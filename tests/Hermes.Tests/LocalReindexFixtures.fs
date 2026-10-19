module Hermes.Tests.LocalReindexFixtures

open System
open System.Collections.Concurrent
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core
open Hermes.Service

type Identities =
    { Body: int64
      Extracted: int64
      Stale: int64
      Unselected: int64 }

type Fixture =
    { Root: string
      Archive: string
      ConfigPath: string
      Ids: Identities }

type HandlerResult =
    { ExitCode: int
      Json: JsonElement }

let private openDatabase (archive: string) : Algebra.Database =
    let settings =
        SqliteConnectionStringBuilder(
            DataSource = Path.Combine(archive, "db.sqlite"), Pooling = false)
    let connection = new SqliteConnection(settings.ConnectionString)
    connection.Open()
    Database.fromConnectionForArchive archive connection

let withDatabase (fixture: Fixture) (verify: Algebra.Database -> Task<'T>) : Task<'T> =
    task {
        let db = openDatabase fixture.Archive
        try return! verify db
        finally db.dispose ()
    }

let private seed
    (db: Algebra.Database) (archive: string) (sourceType: string)
    (relative: string) (text: string) =
    task {
        let bytes = Encoding.UTF8.GetBytes(text: string)
        let name =
            Path.GetFileName(relative)
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.defaultWith (fun () -> invalidArg (nameof relative) "Source path must have a file name")
        do! SourceFiles.writeNew db Interpreters.realFileSystem (Path.Combine(archive, relative)) bytes
        let! value =
            db.execScalar
                """INSERT INTO documents(source_type,saved_path,category,sha256,original_name,subject)
                   VALUES (@type,@path,'invoices',@sha,@name,'Synthetic operator metadata')
                   RETURNING id"""
                [ "@type", Database.boxVal sourceType; "@path", Database.boxVal relative
                  "@sha", Database.boxVal (SourceFiles.hash bytes)
                  "@name", Database.boxVal name ]
        return Assert.IsType<int64>(value)
    }

let private seedExtraction (db: Algebra.Database) archive documentId =
    task {
        do! File.WriteAllTextAsync(
                Path.Combine(archive, "source", "image.png.extracted.md"), "cliextractedsapphiremarker")
        let! _ =
            db.execNonQuery
                """INSERT INTO extraction(document_id,method) VALUES (@id,'synthetic');
                   INSERT INTO stage_completions(document_id,stage_name) VALUES (@id,'extract');
                   INSERT INTO corrections(document_id,field,corrected_value,note)
                   VALUES (@id,'extracted_amount','42.5','synthetic retained correction');
                   INSERT INTO tags(document_id,tag,source) VALUES (@id,'owner-tag','user');"""
                [ "@id", Database.boxVal documentId ]
        return ()
    }

let private seedAll (db: Algebra.Database) (archive: string) : Task<Identities> =
    task {
        let! body = seed db archive "email_body" "source/body.md" "clibodyemeraldmarker"
        let! extracted = seed db archive "manual_drop" "source/image.png" "synthetic image bytes"
        let! stale = seed db archive "manual_drop" "source/stale.png" "synthetic stale image"
        let! unselected = seed db archive "email_body" "source/unselected.md" "cliunselecteddiamondmarker"
        do! seedExtraction db archive extracted
        do! File.WriteAllTextAsync(
                Path.Combine(archive, "source", "stale.png.extracted.md"), "clistaleonyxmarker")
        return { Body = body; Extracted = extracted; Stale = stale; Unselected = unselected }
    }

let private initialize archiveName (root: string) : Task<Fixture> =
    task {
        let archive = Path.Combine(root, archiveName)
        Directory.CreateDirectory(archive) |> ignore
        let configPath = Path.Combine(root, "operator config.yaml")
        let yaml = $"archive_dir: {JsonSerializer.Serialize archive}\naccounts: []\nwatch_folders: []\nollama:\n  enabled: false\n"
        do! File.WriteAllTextAsync(configPath, yaml)
        let db = openDatabase archive
        try
            let! schema = db.initSchema ()
            schema |> Result.defaultWith failwith
            do! PipelineV5.initSchema db TestHelpers.standardV5Stages
            let! ids = seedAll db archive
            return { Root = root; Archive = archive; ConfigPath = configPath; Ids = ids }
        finally db.dispose ()
    }

let private withArchive archiveName (verify: Fixture -> Task<unit>) : Task<unit> =
    task {
        let root = Path.Combine(Path.GetTempPath(), "hermes-local-reindex-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(root) |> ignore
        try
            let! fixture = initialize archiveName root
            do! verify fixture
        finally
            Directory.Delete(root, true)
    }

let withFixture verify = withArchive "configured archive" verify

/// A real synthetic archive at Config.defaultArchiveDir's expected leaf name.
let withDefaultFixture verify = withArchive "hermes" verify

let arguments (fixture: Fixture) (ids: int64 list) (extra: string list) =
    [ "reindex-files"; "--config"; fixture.ConfigPath ]
    @ (ids |> List.collect (fun id -> [ "--document-id"; string id ]))
    @ extra
    |> List.toArray

let context (fixture: Fixture) : ReindexOperator.Context =
    let environment = TestHelpers.fakeEnvironment fixture.Root fixture.Root fixture.Root
    { ServiceEntry.localContext Interpreters.realFileSystem environment with
        Logger = TestHelpers.silentLogger }

let runHandlerWith
    (context: ReindexOperator.Context) (arguments: string array) : HandlerResult =
    let output = ConcurrentQueue<string>()
    let code =
        ServiceEntry.runWith context output.Enqueue
            (fun _ -> failwith "Reindex unexpectedly entered service startup") arguments
    { ExitCode = code
      Json = output |> Seq.exactlyOne |> HttpRegressionFixture.json }

let runHandler fixture ids extra =
    runHandlerWith (context fixture) (arguments fixture ids extra)

let statuses (json: JsonElement) =
    json.GetProperty("outcomes").EnumerateArray()
    |> Seq.map (fun item ->
        item.GetProperty("documentId").GetInt64(),
        (item.GetProperty("status").GetString() |> Option.ofObj |> Option.defaultValue ""))
    |> Map.ofSeq

let sourceHashes (fixture: Fixture) =
    Directory.GetFiles(Path.Combine(fixture.Archive, "source"), "*", SearchOption.AllDirectories)
    |> Array.map (fun path -> path, File.ReadAllBytes(path) |> SourceFiles.hash)
    |> Map.ofArray

let metadata fixture =
    withDatabase fixture (fun db ->
        [ "documents"; "archive_source_claims"; "stage_completions"; "corrections"; "tags"
          "document_generations"; "pipeline_stage_attempts"; "schema_version" ]
        |> Prelude.foldTask
            (fun collected table ->
                task {
                    let! rows = db.execReader $"SELECT * FROM {table} ORDER BY rowid" []
                    return collected |> Map.add table rows
                })
            Map.empty)

let contentCount fixture =
    withDatabase fixture (fun db ->
        task {
            let! value = db.execScalar "SELECT COUNT(*) FROM document_content_fts" []
            return Assert.IsType<int64>(value)
        })

let assertIndexed fixture query documentId =
    withDatabase fixture (fun db ->
        task {
            let! hits = Search.executeUnified db (Search.defaultFilter query)
            Assert.Equal(documentId, (hits |> List.exactlyOne).DocumentId)
        })
