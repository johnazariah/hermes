namespace Hermes.Tests

open System
open System.Collections.Concurrent
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Hermes.Core

module McpAsyncConformanceFixture =
    type Fixture = { Db: Algebra.Database; Files: TestHelpers.MemFs }
    type Snapshot =
        { Sql: Map<string, string list>
          Text: Map<string, string>
          Bytes: Map<string, string>
          Directories: Map<string, bool> }

    let archiveRoot = IO.Path.GetFullPath("/archive")
    let source = IO.Path.Combine(archiveRoot, "async", "source.txt")
    let sidecar =
        let directory =
            IO.Path.GetDirectoryName(source)
            |> Option.ofObj
            |> Option.defaultWith (fun () -> invalidOp "Synthetic source directory is missing")
        IO.Path.Combine(directory, "thread.comprehension.json")
    let sourceText = "Opaque synthetic asynchronous MCP record."
    let extractedText = "Opaque synthetic extraction input."
    let sha = sourceText |> Encoding.UTF8.GetBytes |> SourceFiles.hash
    let oldProof =
        """{"document_type":"payslip","summary":"opaque record","deep_extraction":{"metadata":{"source_hash":"older-input"},"fields":{"marker":"old"}}}"""
    let parameters = [ "@id", Database.boxVal 1L ]
    let historyTables =
        [ "corrections"; "stage_publications"; "archive_source_claims"
          "pipeline_stage_attempts"; "comprehension_provenance" ]

    let execute (db: Algebra.Database) (sql: string) (values: (string * obj) list) =
        task {
            let! _ = db.execNonQuery sql values
            return ()
        }

    let private seedSql =
        """INSERT INTO documents
             (id,source_type,saved_path,folder_path,category,sha256,original_name,
              extracted_at,embedded_at,ingested_at,stage)
           VALUES (1,'manual_drop','async/source.txt','async','records',@sha,'source.txt',
                   'extract-time','embed-time','2025-03-15T10:30:00Z','embedded');
           INSERT INTO extraction(document_id,method) VALUES (1,'synthetic');
           INSERT INTO triage(document_id,document_type,category,confidence) VALUES (1,'payslip','records',1);
           INSERT INTO comprehension(document_id,document_type,category,confidence) VALUES (1,'payslip','records',1);
           INSERT INTO embedding(document_id,chunk_count) VALUES (1,0);
           INSERT INTO stage_completions(document_id,stage_name)
             VALUES (1,'extract'),(1,'triage'),(1,'deep-comprehend'),(1,'embed');
           INSERT INTO stage_publications(document_id,stage_name,generation,response_json)
             VALUES (1,'deep-comprehend',0,@proof);
           INSERT INTO tags(document_id,tag,source) VALUES (1,'keep-user','user');"""

    let private arrange (fixture: Fixture) =
        task {
            do! TestHelpers.initV5 fixture.Db
            do! SourceFiles.writeNew fixture.Db fixture.Files.Fs source (Encoding.UTF8.GetBytes(sourceText))
            do! execute fixture.Db seedSql [ "@sha", Database.boxVal sha; "@proof", Database.boxVal oldProof ]
            let! first = DocumentManagement.correctField fixture.Db 1L "extracted_vendor" "earlier synthetic" None
            Assert.Equal<Result<unit, string>>(Ok (), first)
            let! latest = DocumentManagement.correctField fixture.Db 1L "extracted_vendor" "verified synthetic" None
            Assert.Equal<Result<unit, string>>(Ok (), latest)
            fixture.Files.Put (source + ".extracted.md") extractedText
            fixture.Files.Put sidecar oldProof
        }

    let withFixture (verify: Fixture -> Task<unit>) =
        task {
            let db = TestHelpers.createDb ()
            let fixture: Fixture = { Db = db; Files = TestHelpers.memFs () }
            try
                do! arrange fixture
                do! verify fixture
            finally
                db.dispose ()
        }

    // Decorate only the scenario database; withFixture retains root disposal.
    // Files and their controlled gates remain the original fixture instances.
    let withYieldEffects (yieldEffects: bool) (verify: Fixture -> Task<unit>) =
        let run (fixture: Fixture) =
            let decorated: Fixture =
                { fixture with
                    Db = V5SchedulerConformanceEffects.database yieldEffects fixture.Db }
            verify decorated
        withFixture run

    let entries (values: ConcurrentDictionary<string, 'T>) : Map<string, 'T> =
        values |> Seq.map (fun entry -> entry.Key, entry.Value) |> Map.ofSeq

    let private table (db: Algebra.Database) collected (row: Map<string, obj>) =
        task {
            let name = (Prelude.RowReader row).String "name" ""
            let escaped = name.Replace("\"", "\"\"")
            let! rows = db.execReader $"SELECT * FROM \"{escaped}\"" []
            let values = rows |> List.map (fun value -> JsonSerializer.Serialize(value)) |> List.sort
            return Map.add name values collected
        }

    let snapshot (db: Algebra.Database) (files: TestHelpers.MemFs) =
        task {
            let! tables = db.execReader "SELECT name FROM sqlite_schema WHERE type='table' ORDER BY name" []
            let! sql = tables |> Prelude.foldTask (table db) Map.empty
            return
                { Sql = sql; Text = entries files.Files
                  Bytes = entries files.Bytes |> Map.map (fun _ bytes -> Convert.ToHexString(bytes))
                  Directories = entries files.Dirs }
        }

    let sameFiles (before: Snapshot) (after: Snapshot) =
        Assert.Equal<Map<string, string>>(before.Text, after.Text)
        Assert.Equal<Map<string, string>>(before.Bytes, after.Bytes)
        Assert.Equal<Map<string, bool>>(before.Directories, after.Directories)

    let sameRows (before: Snapshot) (after: Snapshot) (tables: string list) =
        tables |> List.iter (fun name -> Assert.Equal<string list>(before.Sql[name], after.Sql[name]))

    let json (value: string) =
        use document = JsonDocument.Parse(value)
        document.RootElement.Clone()

    let text (name: string) (value: JsonElement) =
        value.GetProperty(name).GetString()
        |> Option.ofObj |> Option.defaultWith (fun () -> failwith $"Missing string: {name}")

    let keys (expected: string list) (value: JsonElement) =
        let actual = value.EnumerateObject() |> Seq.map (fun property -> property.Name) |> Seq.sort |> Seq.toList
        Assert.Equal<string list>(List.sort expected, actual)

    let equalJson (expected: string) (actual: JsonElement) =
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual.GetRawText())),
                    $"Expected {expected}; actual {actual.GetRawText()}")

    let payload (failed: bool) (reply: string) =
        let outer = json reply
        keys [ "jsonrpc"; "id"; "result" ] outer
        Assert.Equal("2.0", text "jsonrpc" outer)
        Assert.Equal(1, outer.GetProperty("id").GetInt32())
        let result = outer.GetProperty("result")
        keys (if failed then [ "content"; "isError" ] else [ "content" ]) result
        if failed then Assert.True(result.GetProperty("isError").GetBoolean())
        let content = result.GetProperty("content")
        Assert.Equal(1, content.GetArrayLength())
        keys [ "type"; "text" ] content[0]
        Assert.Equal("text", text "type" content[0])
        json (text "text" content[0])

    let call (capabilities: McpServer.Capabilities) (db: Algebra.Database)
             (fs: Algebra.FileSystem) (deps: McpTools.DeepExtractionDeps option)
             (tool: string) (arguments: string) =
        let request =
            $"""{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"{tool}","arguments":{arguments}}}}}"""
        McpServer.processMessageWithCapabilities capabilities db fs TestHelpers.silentLogger
            TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) archiveRoot deps request
