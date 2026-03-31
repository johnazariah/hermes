module Hermes.Tests.EmailBodyTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core

// --- Helpers ---

let insertTestMessage (db: Algebra.Database) (account: string) (gmailId: string) (sender: string) (subject: string) (bodyText: string) : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO messages (gmail_id, account, sender, subject, body_text, has_attachments, processed_at)
                   VALUES (@gid, @acc, @sender, @subject, @body, 0, datetime('now'))"""
                ([ ("@gid", Database.boxVal gmailId)
                   ("@acc", Database.boxVal account)
                   ("@sender", Database.boxVal sender)
                   ("@subject", Database.boxVal subject)
                   ("@body", Database.boxVal bodyText) ] : (string * obj) list)
        ()
    }

let insertTestDocument (db: Algebra.Database) (sender: string) (subject: string) (category: string) (originalName: string) (extractedText: string) : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                   (source_type, saved_path, category, sha256, sender, subject, original_name, extracted_text)
                   VALUES
                   ('manual_drop', @sp, @cat, @sha, @sender, @subject, @name, @text)"""
                ([ ("@sp", Database.boxVal (category + "/" + originalName))
                   ("@cat", Database.boxVal category)
                   ("@sha", Database.boxVal (Guid.NewGuid().ToString("N")))
                   ("@sender", Database.boxVal sender)
                   ("@subject", Database.boxVal subject)
                   ("@name", Database.boxVal originalName)
                   ("@text", Database.boxVal extractedText) ] : (string * obj) list)
        ()
    }

// --- HTML stripping tests ---

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_StripHtml_RemovesTags`` () =
    let result = EmailSync.stripHtml "<p>Hello <b>world</b></p>"
    Assert.Equal("Hello world", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_StripHtml_DecodesEntities`` () =
    let result = EmailSync.stripHtml "Tom &amp; Jerry &lt;3 &gt; &quot;fun&quot; &#39;yes&#39;"
    Assert.Contains("Tom & Jerry", result)
    Assert.Contains("<3", result)
    Assert.Contains(">", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_StripHtml_CollapsesWhitespace`` () =
    let result = EmailSync.stripHtml "<div>  Hello   \n\n  world  </div>"
    Assert.Equal("Hello world", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_StripHtml_EmptyInput_ReturnsEmpty`` () =
    Assert.Equal("", EmailSync.stripHtml "")
    Assert.Equal("", EmailSync.stripHtml "   ")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_StripHtml_PlainText_Unchanged`` () =
    let result = EmailSync.stripHtml "Just plain text content"
    Assert.Equal("Just plain text content", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_StripHtml_ComplexHtml_ProducesCleanText`` () =
    let html = @"<html><head><style>body{color:red}</style></head><body><h1>Invoice</h1><p>Amount: $500</p><br/><a href=""http://example.com"">Link</a>&nbsp;here</body></html>"
    let result = EmailSync.stripHtml html
    Assert.Contains("Invoice", result)
    Assert.Contains("Link", result)
    Assert.Contains("here", result)
    Assert.DoesNotContain("<", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``EmailSync_StripHtml_DecodesNbsp`` () =
    let result = EmailSync.stripHtml "Hello&nbsp;world"
    Assert.Equal("Hello world", result)

// --- Messages FTS5 tests ---

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MessagesFts_InsertTrigger_IndexesBodyText`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-100" "alice@example.com" "Quarterly Report" "Revenue increased by 15 percent in Q3"
            let! result = db.execScalar "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'revenue'" []
            Assert.True((result :?> int64) > 0L, "Should find 'revenue' in messages_fts")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MessagesFts_SearchBySubject_FindsMessage`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-101" "bob@example.com" "Monthly Invoice" "Please pay the attached invoice"
            let! result = db.execScalar "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'invoice'" []
            Assert.True((result :?> int64) > 0L, "Should find 'invoice' via subject or body")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MessagesFts_SearchBySender_FindsMessage`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-102" "alice@example.com" "Hello" "Test body"
            let! result = db.execScalar "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'alice'" []
            Assert.True((result :?> int64) > 0L, "Should find sender in messages_fts")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MessagesFts_NoMatch_ReturnsZero`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-103" "bob@test.com" "Hello" "World"
            let! result = db.execScalar "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'xyznonexistent'" []
            Assert.True((result :?> int64) = 0L, "Should not find non-existent term")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``MessagesFts_UpdateTrigger_ReindexesOnUpdate`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-104" "bob@test.com" "Old Subject" "Old body content"
            let! _ = db.execNonQuery "UPDATE messages SET body_text = 'New updated body content about plumbing' WHERE gmail_id = 'msg-104' AND account = 'test-acct'" []
            let! result = db.execScalar "SELECT COUNT(*) FROM messages_fts WHERE messages_fts MATCH 'plumbing'" []
            Assert.True((result :?> int64) > 0L, "Should find updated body text in FTS")
        finally
            db.dispose ()
    }

// --- Email search tests ---

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_ExecuteEmailSearch_FindsMessageByBody`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-200" "alice@example.com" "Report" "The quarterly financial report shows growth"
            let filter = Search.defaultFilter "financial"
            let! results = Search.executeEmailSearch db filter
            Assert.NotEmpty(results)
            Assert.Equal("email", results.[0].ResultType)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_ExecuteEmailSearch_ReturnsSnippet`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-201" "bob@test.com" "Payment" "Please process the payment for invoice number twelve"
            let filter = Search.defaultFilter "payment"
            let! results = Search.executeEmailSearch db filter
            Assert.NotEmpty(results)
            Assert.True(results.[0].Snippet.IsSome, "Expected snippet")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_ExecuteEmailSearch_NoMatch_ReturnsEmpty`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestMessage db "test-acct" "msg-202" "bob@test.com" "Hello" "World"
            let filter = Search.defaultFilter "xyznonexistent"
            let! results = Search.executeEmailSearch db filter
            Assert.Empty(results)
        finally
            db.dispose ()
    }

// --- Unified search tests ---

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_ExecuteUnified_MergesDocumentAndEmailResults`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "alice@example.com" "Doc Invoice" "invoices" "inv.pdf" "This document is an invoice for services"
            do! insertTestMessage db "test-acct" "msg-300" "bob@test.com" "Email Invoice" "Please find the invoice attached to this email"
            let filter = Search.defaultFilter "invoice"
            let! results = Search.executeUnified db filter
            Assert.True(results.Length >= 2, sprintf "Expected at least 2 results, got %d" results.Length)
            let resultTypes = results |> List.map (fun r -> r.ResultType) |> List.distinct |> List.sort
            Assert.Contains("document", resultTypes)
            Assert.Contains("email", resultTypes)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_ExecuteUnified_RespectsLimit`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            for i in 1..5 do
                do! insertTestDocument db "a@test.com" (sprintf "Invoice %d" i) "invoices" (sprintf "inv%d.pdf" i) (sprintf "Invoice document number %d" i)
                do! insertTestMessage db "test-acct" (sprintf "msg-%d" i) "b@test.com" (sprintf "Email Invoice %d" i) (sprintf "Invoice email number %d" i)
            let filter = { Search.defaultFilter "invoice" with Limit = 3 }
            let! results = Search.executeUnified db filter
            Assert.True(results.Length <= 3, sprintf "Expected at most 3, got %d" results.Length)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Search_ExecuteUnified_SortedByRelevance`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            do! insertTestDocument db "a@test.com" "Water Bill" "utilities" "water.pdf" "Water usage charges"
            do! insertTestMessage db "test-acct" "msg-400" "b@test.com" "Water Notice" "Important water supply notice"
            let filter = Search.defaultFilter "water"
            let! results = Search.executeUnified db filter
            Assert.NotEmpty(results)
            let scores = results |> List.map (fun r -> r.RelevanceScore)
            let sorted = scores |> List.sort
            Assert.Equal<float list>(sorted, scores)
        finally
            db.dispose ()
    }

// --- Schema v2 tests ---

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_SchemaV2_HasBodyTextColumn`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! _ = db.execNonQuery "INSERT INTO messages (gmail_id, account, body_text, has_attachments) VALUES ('test', 'acct', 'body content', 0)" []
            let! result = db.execScalar "SELECT body_text FROM messages WHERE gmail_id = 'test' AND account = 'acct'" []
            match result with
            | null -> failwith "Expected body_text value"
            | v -> Assert.Equal("body content", v :?> string)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_SchemaV2_HasThreadIdColumn`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! _ = db.execNonQuery "INSERT INTO messages (gmail_id, account, thread_id, has_attachments) VALUES ('test', 'acct', 'thread-123', 0)" []
            let! result = db.execScalar "SELECT thread_id FROM messages WHERE gmail_id = 'test' AND account = 'acct'" []
            match result with
            | null -> failwith "Expected thread_id value"
            | v -> Assert.Equal("thread-123", v :?> string)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_SchemaV2_HasMessagesFts`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! exists = db.tableExists "messages_fts"
            Assert.True(exists, "messages_fts table should exist")
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Database_SchemaV2_VersionIs2`` () =
    task {
        let db = TestHelpers.createRawDb ()
        try
            let! _ = db.initSchema ()
            let! version = db.schemaVersion ()
            Assert.Equal(Database.CurrentSchemaVersion, version)
        finally
            db.dispose ()
    }

// --- Body fetch during sync tests ---

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_FetchesBodyWhenMissing`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let clock = TestHelpers.fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = TestHelpers.testConfig "/archive"
        let msg : Domain.EmailMessage =
            { ProviderId = "msg-body-001"
              ThreadId = "thread-001"
              Sender = Some "alice@example.com"
              Subject = Some "Test"
              Date = Some (DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero))
              Labels = [ "INBOX" ]
              HasAttachments = false
              BodyText = None }
        let provider : Algebra.EmailProvider =
            { listNewMessages = fun _ -> task { return [ msg ] }
              getAttachments = fun _ -> task { return [] }
              getMessageBody = fun _ -> task { return Some "<p>Hello <b>World</b></p>" }
              listMessagePage = fun _ _ _ -> task { return { Algebra.MessagePage.Messages = []; NextPageToken = None; ResultSizeEstimate = 0L } } }
        try
            let! _ = db.initSchema ()
            let! _ = EmailSync.syncAccount m.Fs db logger clock provider config "test-account"
            let! result = db.execScalar "SELECT body_text FROM messages WHERE gmail_id = 'msg-body-001' AND account = 'test-account'" []
            match result with
            | null -> failwith "Expected body_text to be stored"
            | :? DBNull -> failwith "Expected body_text, got DBNull"
            | v -> Assert.Equal("Hello World", v :?> string)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``EmailSync_SyncAccount_SkipsBodyFetchWhenPresent`` () =
    task {
        let m = TestHelpers.memFs ()
        let db = TestHelpers.createRawDb ()
        let logger = Logging.silent
        let clock = TestHelpers.fixedClock (DateTimeOffset(2024, 6, 15, 12, 0, 0, TimeSpan.Zero))
        let config = TestHelpers.testConfig "/archive"
        let mutable bodyFetched = false
        let msg : Domain.EmailMessage =
            { ProviderId = "msg-body-002"
              ThreadId = "thread-002"
              Sender = Some "bob@example.com"
              Subject = Some "Test"
              Date = Some (DateTimeOffset(2024, 6, 15, 10, 0, 0, TimeSpan.Zero))
              Labels = [ "INBOX" ]
              HasAttachments = false
              BodyText = Some "Already has body" }
        let provider : Algebra.EmailProvider =
            { listNewMessages = fun _ -> task { return [ msg ] }
              getAttachments = fun _ -> task { return [] }
              getMessageBody = fun _ -> task {
                  bodyFetched <- true
                  return Some "Should not be called"
              }
              listMessagePage = fun _ _ _ -> task { return { Algebra.MessagePage.Messages = []; NextPageToken = None; ResultSizeEstimate = 0L } } }
        try
            let! _ = db.initSchema ()
            let! _ = EmailSync.syncAccount m.Fs db logger clock provider config "test-account"
            Assert.False(bodyFetched, "Should not call getMessageBody when BodyText is already present")
        finally
            db.dispose ()
    }
