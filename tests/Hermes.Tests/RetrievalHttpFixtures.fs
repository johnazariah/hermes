module Hermes.Tests.RetrievalHttpFixtures

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Data.Sqlite
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture

let encoder: Algebra.EmbeddingClient =
    { dimensions = 2; isAvailable = fun () -> Task.FromResult true
      embed = fun _ -> Task.FromResult(Ok [| 1.0f; 0.0f |]) }

let configured client verify =
    withConfiguredServer client McpAccess.readOnly [] None verify

let ids (results: JsonElement) =
    results.EnumerateArray() |> Seq.map (fun hit -> hit.GetProperty("id").GetInt64())
    |> Seq.sort |> Seq.toList

let rawCall server name arguments =
    let envelope =
        $"""{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"{name}","arguments":{arguments}}}}}"""
    post server "/mcp" (box (json envelope))

let response (server: Server) (path: string) (expectedStatus: int) =
    task {
        use! result = server.Client.GetAsync(path)
        Assert.Equal(expectedStatus, int result.StatusCode)
        let! text = result.Content.ReadAsStringAsync()
        return json text
    }

let executeSql (server: Server) sql parameters =
    task {
        let! _ = server.Db.execNonQuery sql parameters
        return ()
    }

let indexed server documentId vector =
    task {
        do! Embeddings.storeChunk server.Db TestHelpers.defaultClock documentId 0
                "quartz current vector evidence" (Some vector)
        do!
            executeSql server
                """INSERT INTO embedding(document_id,chunk_count) VALUES (@id,1);
                   INSERT OR IGNORE INTO stage_completions(document_id,stage_name)
                   VALUES (@id,'embed');"""
                [ "@id", Database.boxVal documentId ]
    }

let seedSearch server =
    task {
        let! first = seedSource server "/archive/search/first.png" "first immutable source"
        let! second = seedSource server "/archive/search/second.png" "second immutable source"
        let! third = seedSource server "/archive/search/third.png" "third immutable source"
        do! [ first; second; third ] |> Prelude.foldTask (fun () id ->
            FileKeywordFixtures.completeSyntheticExtraction server id) ()
        do! executeSql server "UPDATE documents SET subject='quartz' WHERE id IN (@a,@b)"
                [ "@a", Database.boxVal first; "@b", Database.boxVal third ]
        do! indexed server first [| 1.0f; 0.0f |]
        do! indexed server second [| 0.0f; 1.0f |]
        return [ first; second; third ]
    }

let canonical =
    """{"document_type":"payslip","fields":{"gross_pay":1234.56,"withheld_tax":234.56,"currency":"SYN","earnings":[{"kind":"ordinary","amount":1234.56}]},"unknown_future_field":{"preserved":true}}"""

let persistCanonical server documentId generation text =
    executeSql server
        """INSERT INTO stage_publications(document_id,stage_name,generation,response_json)
           VALUES (@id,'deep-comprehend',@generation,@json)"""
        [ "@id", Database.boxVal documentId; "@generation", Database.boxVal generation
          "@json", Database.boxVal text ]

let seedComprehension server path text =
    task {
        let! id = seedSource server path ("immutable synthetic source " + path)
        do! seedCompletions server id
        do!
            executeSql server
                """INSERT INTO extraction(document_id,method) VALUES (@id,'synthetic');
                   INSERT INTO comprehension(document_id,document_type,category,confidence)
                   VALUES (@id,'payslip','payslips',0.99);"""
                [ "@id", Database.boxVal id ]
        do! persistCanonical server id 0L text
        return id
    }

let private tableHash (server: Server) hashes (name: string) =
    task {
        let escaped = name.Replace("\"", "\"\"")
        let! rows = server.Db.execReader $"SELECT * FROM \"{escaped}\"" []
        let digest =
            rows |> List.map JsonSerializer.Serialize |> List.sort |> String.concat "\n"
            |> Encoding.UTF8.GetBytes |> SourceFiles.hash
        return (name, digest) :: hashes
    }

let databaseHash (server: Server) =
    task {
        let! tables =
            server.Db.execReader
                "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name" []
        let names = tables |> List.map (fun row -> (Prelude.RowReader row).String "name" "")
        let! hashes = names |> Prelude.foldTask (tableHash server) []
        return hashes |> List.sort |> JsonSerializer.Serialize |> Encoding.UTF8.GetBytes |> SourceFiles.hash
    }

let fileDatabase path () =
    let connection = new SqliteConnection($"Data Source={path};Pooling=False")
    connection.Open()
    let db = Database.fromConnectionForArchive "/archive" connection
    db.initSchema().GetAwaiter().GetResult() |> Result.defaultWith failwith
    db

let physicalHashes path =
    [ path; path + "-wal" ] |> List.filter File.Exists
    |> Prelude.foldTask (fun hashes file ->
        task {
            let! bytes = File.ReadAllBytesAsync(file)
            return hashes |> Map.add file (SourceFiles.hash bytes)
        }) Map.empty
