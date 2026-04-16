module Hermes.Tests.McpContactTests

#nowarn "3261"

open System.Text.Json
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

let private insertDoc (db: Algebra.Database) name category sender comp =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                    (original_name, saved_path, source_type, category, sha256, comprehension, sender)
                   VALUES (@name, @path, 'email_attachment', @cat, @sha, @comp, @sender)"""
                [ ("@name", Database.boxVal name)
                  ("@path", Database.boxVal $"{category}/{name}")
                  ("@cat", Database.boxVal category)
                  ("@sha", Database.boxVal (System.Guid.NewGuid().ToString("N")))
                  ("@comp", Database.boxVal comp)
                  ("@sender", Database.boxVal sender) ]
        ()
    }

let private seedDocs (db: Algebra.Database) =
    task {
        do! insertDoc db "payslip-jan.pdf" "payslips" "noreply@acme.com" acmeComprehension
        do! insertDoc db "invoice-q1.pdf" "invoices" "billing@globex.com" globexComprehension
        do! insertDoc db "receipt-misc.pdf" "receipts" "unknown@example.com" noNameComprehension
    }

// ─── JSON-RPC helper ─────────────────────────────────────────────────

let private callTool (db: Algebra.Database) toolName argsJson =
    task {
        let m = TestHelpers.memFs ()
        let json =
            $"""{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"{toolName}","arguments":{argsJson}}}}}"""
        return! McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock "/archive" None json
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
        try
            do! seedDocs db
            let! response = callTool db "hermes_contacts_backfill" "{}"
            let result = parseResult response
            Assert.Equal("backfill_complete", result.GetProperty("status").GetString())
            Assert.True(result.GetProperty("processed").GetInt32() >= 2)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_ContactsList_ReturnsContacts`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            do! seedDocs db
            let! _ = callTool db "hermes_contacts_backfill" "{}"
            let! response = callTool db "hermes_contacts" "{}"
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
        try
            do! seedDocs db
            let! _ = callTool db "hermes_contacts_backfill" "{}"
            let! response = callTool db "hermes_contacts" """{"query":"Acme"}"""
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
        try
            do! seedDocs db
            let! _ = callTool db "hermes_contacts_backfill" "{}"
            let! response = callTool db "hermes_contacts" """{"contact_type":"employer"}"""
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
        try
            do! seedDocs db
            let! _ = callTool db "hermes_contacts_backfill" "{}"
            let! listResp = callTool db "hermes_contacts" """{"query":"Acme"}"""
            let contactId = (parseResult listResp).GetProperty("contacts").[0].GetProperty("id").GetString()

            let! response = callTool db "hermes_contact_detail" $"""{{"contact_id":"{contactId}"}}"""
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
        try
            let! response = callTool db "hermes_contact_detail" """{"contact_id":"nonexistent"}"""
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
        try
            do! seedDocs db
            let! _ = callTool db "hermes_contacts_backfill" "{}"
            let! listResp = callTool db "hermes_contacts" """{"query":"Globex"}"""
            let contactId = (parseResult listResp).GetProperty("contacts").[0].GetProperty("id").GetString()

            let! response = callTool db "hermes_contact_set_tax_relevant" $"""{{"contact_id":"{contactId}","tax_relevant":"true"}}"""
            let result = parseResult response
            Assert.Equal("updated", result.GetProperty("status").GetString())

            let! filtered = callTool db "hermes_contacts" """{"tax_relevant":"true"}"""
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
        try
            let! response = callTool db "hermes_contact_set_tax_relevant" """{"contact_id":"nonexistent","tax_relevant":"true"}"""
            let result = parseResult response
            Assert.True(result.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }
