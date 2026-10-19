module Hermes.Tests.OwnedServiceProcessTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.OwnedServiceProcess

type private TableRows =
    { Table: string
      Rows: Map<string, obj> list }

let private withDatabase paths action =
    task {
        let db = Database.fromPath (Path.Combine(paths.Archive, "db.sqlite"))
        try return! action db
        finally db.dispose ()
    }

let private seedCompletions (db: Algebra.Database) =
    task {
        for stage in [ "extract"; "triage"; "deep-comprehend"; "embed" ] do
            let! _ =
                db.execNonQuery
                    """INSERT INTO stage_completions(document_id,stage_name,completed_at)
                       VALUES (9001,@stage,'2020-01-02')"""
                    [ "@stage", Database.boxVal stage ]
            ()
    }

let private seedOutputs (db: Algebra.Database) =
    task {
        let statements =
            [ "INSERT INTO extraction(document_id,method,extracted_at) VALUES (9001,'synthetic','2020-01-02')"
              "INSERT INTO triage(document_id,document_type,category,confidence,triaged_at) VALUES (9001,'other','other',1,'2020-01-02')"
              "INSERT INTO comprehension(document_id,document_type,comprehended_at) VALUES (9001,'other','2020-01-02')"
              "INSERT INTO embedding(document_id,chunk_count,embedded_at) VALUES (9001,0,'2020-01-02')" ]
        for sql in statements do
            let! _ = db.execNonQuery sql []
            ()
    }

let private seed paths =
    withDatabase paths (fun db ->
        task {
            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                       (id,source_type,saved_path,category,sha256,stage,original_name)
                       VALUES (9001,'manual_drop','synthetic.txt','other','synthetic-sha',
                               'embedded','synthetic.txt')"""
                    []
            do! seedCompletions db
            do! seedOutputs db
            let! _ =
                db.execNonQuery
                    "INSERT INTO sync_state(account,last_sync_at,message_count) VALUES ('synthetic','2020-01-02',17)"
                    []
            ()
        })

let private readTable (db: Algebra.Database) name =
    task {
        let! rows = db.execReader $"SELECT * FROM {name} ORDER BY 1,2" []
        return { Table = name; Rows = rows }
    }

let private ledger paths =
    withDatabase paths (fun db ->
        task {
            let! tables =
                [ "documents"; "stage_completions"; "pipeline_stage_attempts"
                  "extraction"; "triage"; "comprehension"; "embedding"; "sync_state" ]
                |> List.map (readTable db)
                |> List.toArray
                |> Task.WhenAll
            return tables |> Array.map (fun table -> table.Table, table.Rows) |> Map.ofArray
        })

[<Theory>]
[<InlineData("shutdown")>]
[<InlineData("eof")>]
[<Trait("Category", "Integration")>]
let ``Actual owned service stops from stdin during initial producer delay`` command =
    withRoot (fun root ->
        task {
            let paths = layout root
            use child = start paths
            do! healthy child
            let! result = stop child command
            Assert.DoesNotContain("still running after", result.Output)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual owned service rejects unknown commands and stops while sync is idle`` () =
    withRoot (fun root ->
        task {
            use child = start (layout root)
            do! healthy child
            do! child.Process.StandardInput.WriteLineAsync("Shutdown")
            do! child.Process.StandardInput.FlushAsync()
            do! Task.Delay(TimeSpan.FromSeconds 6.0)
            do! healthy child
            let! result = stop child "shutdown"
            Assert.Contains("Rejected unknown stdin control command", result.Output)
            Assert.Contains("Producer cycle completed", result.Output)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Actual owned restart has a new PID and preserves completed V5 ledger and watermarks`` () =
    withRoot (fun root ->
        task {
            let paths = layout root
            use first = start paths
            do! healthy first
            let! _ = stop first "shutdown"
            do! seed paths
            let! before = ledger paths
            use second = start paths
            do! healthy second
            Assert.NotEqual(first.Process.Id, second.Process.Id)
            let! _ = stop second "eof"
            let! after = ledger paths
            Assert.Equal<Map<string, Map<string, obj> list>>(before, after)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Independent owned services report their own identities and stopping one leaves the other alive`` () =
    withRoot (fun root ->
        task {
            use first = start (layout (Path.Combine(root, "first")))
            do! healthy first
            use second = start (layout (Path.Combine(root, "second")))
            do! healthy second
            Assert.NotEqual(first.Process.Id, second.Process.Id)
            let! _ = stop first "shutdown"
            do! healthy second
            let! _ = stop second "eof"
            ()
        })
