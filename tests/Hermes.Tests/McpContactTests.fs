module Hermes.Tests.McpContactTests

#nowarn "3261"

open System.Text.Json
open System.Text.Json.Nodes
open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── Test data ───────────────────────────────────────────────────────

let private acmeComprehension =
    """{"document_type":"payslip","sender_name":"Acme Corp","fields":{"employer":"Acme Corp","abn":"12345678901"}}"""

let private globexComprehension =
    """{"document_type":"invoice","sender_name":"Globex Pty Ltd","fields":{"abn":"98765432100"}}"""

let private noNameComprehension =
    """{"document_type":"receipt"}"""

// ─── DB seeding ──────────────────────────────────────────────────────

let private documentType (comprehension: string) =
    use json = JsonDocument.Parse(comprehension)
    json.RootElement.GetProperty("document_type").GetString()

let private markComprehensionCurrent (db: Algebra.Database) (sha: string) (category: string) (comp: string) =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO comprehension (document_id, document_type, category, confidence)
                   SELECT id, @type, @category, 1.0 FROM documents WHERE sha256 = @sha"""
                [ ("@type", Database.boxVal (documentType comp))
                  ("@category", Database.boxVal category)
                  ("@sha", Database.boxVal sha) ]
        let! _ =
            db.execNonQuery
                """INSERT INTO stage_completions (document_id, stage_name)
                   SELECT id, 'deep-comprehend' FROM documents WHERE sha256 = @sha"""
                [ ("@sha", Database.boxVal sha) ]
        return ()
    }

let private insertDoc (db: Algebra.Database) (m: TestHelpers.MemFs) (name: string) (category: string) (sender: string) (comp: string) =
    task {
        let sha = System.Guid.NewGuid().ToString("N")
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                    (original_name, saved_path, source_type, category, sha256, classification_tier, sender)
                   VALUES (@name, @path, 'email_attachment', @cat, @sha, 'llm', @sender)"""
                [ ("@name", Database.boxVal name)
                  ("@path", Database.boxVal $"{category}/{name}")
                  ("@cat", Database.boxVal category)
                  ("@sha", Database.boxVal sha)
                  ("@sender", Database.boxVal sender) ]
        if not (System.String.IsNullOrWhiteSpace(comp)) then
            m.Put $"/archive/{category}/thread.comprehension.json" comp
            do! markComprehensionCurrent db sha category comp
        return ()
    }

let private seedDocs (db: Algebra.Database) (m: TestHelpers.MemFs) =
    task {
        do! TestHelpers.initV5 db
        do! insertDoc db m "payslip-jan.pdf" "payslips" "noreply@acme.com" acmeComprehension
        do! insertDoc db m "invoice-q1.pdf" "invoices" "billing@globex.com" globexComprehension
        do! insertDoc db m "receipt-misc.pdf" "receipts" "unknown@example.com" noNameComprehension
    }

let private seedStaleManuallyClassifiedDoc (db: Algebra.Database) (m: TestHelpers.MemFs) =
    task {
        do! TestHelpers.initV5 db
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                  (original_name, saved_path, source_type, category, sha256, classification_tier, sender)
                 VALUES ('stale.pdf', 'payslips/stale.pdf', 'manual_drop', 'payslips',
                         'sha-stale-manual', 'manual', 'noreply@acme.com')"""
                []
        m.Put "/archive/payslips/thread.comprehension.json" acmeComprehension
    }

let private onceAsync (work: unit -> Task<unit>) =
    let started =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)
    fun () ->
        if started.TrySetResult(()) then work ()
        else Task.FromResult(())

// ─── JSON-RPC helper ─────────────────────────────────────────────────

let private callTool (db: Algebra.Database) (m: TestHelpers.MemFs) toolName argsJson =
    task {
        let json =
            $"""{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"{toolName}","arguments":{argsJson}}}}}"""
        return! McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock (TestHelpers.standardV5Dag ()) "/archive" None json
    }

let private parseResult (response: string) : JsonElement =
    let doc = JsonDocument.Parse(response)
    let content = doc.RootElement.GetProperty("result").GetProperty("content")
    let text = content.[0].GetProperty("text").GetString()
    JsonDocument.Parse(text).RootElement

// ─── Tests ───────────────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactsBackfill_CreatesContacts`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! seedDocs db m
            let! response = callTool db m "hermes_contacts_backfill" "{}"
            let result = parseResult response
            Assert.Equal("backfill_complete", result.GetProperty("status").GetString())
            Assert.True(result.GetProperty("processed").GetInt32() >= 2)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactsBackfill_ManualClassificationWithStaleSidecar_RemainsUnlinked`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! seedStaleManuallyClassifiedDoc db m
            let! response = callTool db m "hermes_contacts_backfill" "{}"
            let result = parseResult response
            let! links = db.execReader "SELECT document_id FROM document_contacts" []
            Assert.Equal(0, result.GetProperty("processed").GetInt32())
            Assert.Empty(links)
            Assert.Equal(Some acmeComprehension, m.Get "/archive/payslips/thread.comprehension.json")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactsList_ReturnsContacts`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! seedDocs db m
            let! _ = callTool db m "hermes_contacts_backfill" "{}"
            let! response = callTool db m "hermes_contacts" "{}"
            let result = parseResult response
            Assert.True(result.GetProperty("contacts").GetArrayLength() >= 2)
            Assert.True(result.GetProperty("count").GetInt32() >= 2)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactsList_FilterByQuery`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! seedDocs db m
            let! _ = callTool db m "hermes_contacts_backfill" "{}"
            let! response = callTool db m "hermes_contacts" """{"query":"Acme"}"""
            let result = parseResult response
            let contacts = result.GetProperty("contacts")
            Assert.Equal(1, contacts.GetArrayLength())
            Assert.Contains("Acme", contacts.[0].GetProperty("name").GetString())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactsList_FilterByContactType`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! seedDocs db m
            let! _ = callTool db m "hermes_contacts_backfill" "{}"
            let! response = callTool db m "hermes_contacts" """{"contact_type":"employer"}"""
            let result = parseResult response
            let contacts = result.GetProperty("contacts")
            Assert.True(contacts.GetArrayLength() >= 1)
            for i in 0 .. contacts.GetArrayLength() - 1 do
                Assert.Equal("employer", contacts.[i].GetProperty("contact_type").GetString())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactDetail_ReturnsWithDocuments`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! seedDocs db m
            let! _ = callTool db m "hermes_contacts_backfill" "{}"
            let! listResp = callTool db m "hermes_contacts" """{"query":"Acme"}"""
            let contactId = (parseResult listResp).GetProperty("contacts").[0].GetProperty("id").GetString()

            let! response = callTool db m "hermes_contact_detail" $"""{{"contact_id":"{contactId}"}}"""
            let detail = parseResult response
            Assert.Equal(contactId, detail.GetProperty("id").GetString())
            Assert.Contains("Acme", detail.GetProperty("name").GetString())
            Assert.True(detail.GetProperty("documents").GetArrayLength() >= 1)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactDetail_NotFound_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! response = callTool db m "hermes_contact_detail" """{"contact_id":"nonexistent"}"""
            let result = parseResult response
            Assert.True(result.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactSetTaxRelevant_Updates`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            do! seedDocs db m
            let! _ = callTool db m "hermes_contacts_backfill" "{}"
            let! listResp = callTool db m "hermes_contacts" """{"query":"Globex"}"""
            let contactId = (parseResult listResp).GetProperty("contacts").[0].GetProperty("id").GetString()

            let! response = callTool db m "hermes_contact_set_tax_relevant" $"""{{"contact_id":"{contactId}","tax_relevant":"true"}}"""
            let result = parseResult response
            Assert.Equal("updated", result.GetProperty("status").GetString())

            let! filtered = callTool db m "hermes_contacts" """{"tax_relevant":"true"}"""
            let ids =
                let c = (parseResult filtered).GetProperty("contacts")
                [ for i in 0 .. c.GetArrayLength() - 1 -> c.[i].GetProperty("id").GetString() ]
            Assert.Contains(contactId, ids)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactSetTaxRelevant_NotFound_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        try
            let! response = callTool db m "hermes_contact_set_tax_relevant" """{"contact_id":"nonexistent","tax_relevant":"true"}"""
            let result = parseResult response
            Assert.True(result.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpTools_ContactsBackfill_InvalidationDuringRead_DoesNotPublishLink`` () =
    task {
        let db = TestHelpers.createDb ()
        let mem = TestHelpers.memFs ()
        try
            do! TestHelpers.initV5 db
            do!
                insertDoc
                    db mem "race.pdf" "payslips"
                    "noreply@acme.com" acmeComprehension
            let! value =
                db.execScalar
                    """SELECT id FROM documents
                       WHERE original_name = 'race.pdf'"""
                    []
            let documentId = value :?> int64
            let invalidate =
                onceAsync (fun () ->
                    task {
                        let! result =
                            Reflow.request
                                db TestHelpers.silentLogger
                                (TestHelpers.standardV5Dag ())
                                documentId
                                Reflow.Recomprehend
                                Reflow.Apply
                        match result with
                        | Error error -> failwith error
                        | Ok _ -> ()
                    })
            let racingFs =
                { mem.Fs with
                    readAllText =
                        fun path ->
                            task {
                                let! content =
                                    mem.Fs.readAllText path
                                if
                                    path.EndsWith(
                                        "thread.comprehension.json",
                                        StringComparison.Ordinal)
                                then
                                    do! invalidate ()
                                return content
                            } }
            let! result =
                McpTools.contactsBackfill
                    db racingFs "/archive"
                    TestHelpers.silentLogger
                    (JsonObject() :> JsonNode)
            let! links =
                db.execScalar
                    """SELECT count(*) FROM document_contacts
                       WHERE document_id = @doc"""
                    [ ("@doc", Database.boxVal documentId) ]
            Assert.Equal(0, result["processed"].GetValue<int>())
            Assert.Equal(1, result["superseded"].GetValue<int>())
            Assert.Equal(0L, links :?> int64)
        finally
            db.dispose ()
    }
