module Hermes.Tests.ContactExtractionTests

open Xunit
open Hermes.Core

// ─── normaliseName tests ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_normaliseName_PtyLtd_Stripped`` () =
    let result = ContactExtraction.normaliseName "CommBank Pty Ltd"
    Assert.Equal("commbank", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_normaliseName_PtyDotLtdDot_Stripped`` () =
    let result = ContactExtraction.normaliseName "Acme Pty. Ltd."
    Assert.Equal("acme", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_normaliseName_Inc_Stripped`` () =
    let result = ContactExtraction.normaliseName "Microsoft Inc"
    Assert.Equal("microsoft", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_normaliseName_Limited_Stripped`` () =
    let result = ContactExtraction.normaliseName "Origin Limited"
    Assert.Equal("origin", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_normaliseName_Quotes_Stripped`` () =
    let result = ContactExtraction.normaliseName "'Ray White'"
    Assert.Equal("ray white", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_normaliseName_PlainName_Lowered`` () =
    let result = ContactExtraction.normaliseName "CommBank"
    Assert.Equal("commbank", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_normaliseName_WhitespacePreservedInMiddle`` () =
    let result = ContactExtraction.normaliseName "Ray White Southbank"
    Assert.Equal("ray white southbank", result)

// ─── computeContactId tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_computeContactId_SameInput_SameOutput`` () =
    let id1 = ContactExtraction.computeContactId "commbank" (Some "123")
    let id2 = ContactExtraction.computeContactId "commbank" (Some "123")
    Assert.Equal(id1, id2)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_computeContactId_DifferentAbn_DifferentId`` () =
    let id1 = ContactExtraction.computeContactId "commbank" (Some "111")
    let id2 = ContactExtraction.computeContactId "commbank" (Some "222")
    Assert.NotEqual<string>(id1, id2)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_computeContactId_WithAbn_IncludesAbn`` () =
    let withAbn = ContactExtraction.computeContactId "commbank" (Some "123")
    let withoutAbn = ContactExtraction.computeContactId "commbank" None
    Assert.NotEqual<string>(withAbn, withoutAbn)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_computeContactId_Returns16Chars`` () =
    let id = ContactExtraction.computeContactId "commbank" (Some "123")
    Assert.Equal(16, id.Length)

// ─── contactTypeFromSender tests ─────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_contactTypeFromSender_Bank_Supplier`` () =
    let result = ContactExtraction.contactTypeFromSender SenderClassification.SenderType.Bank
    Assert.Equal("supplier", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_contactTypeFromSender_Employer_Employer`` () =
    let result = ContactExtraction.contactTypeFromSender SenderClassification.SenderType.Employer
    Assert.Equal("employer", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_contactTypeFromSender_Government_Government`` () =
    let result = ContactExtraction.contactTypeFromSender SenderClassification.SenderType.Government
    Assert.Equal("government", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_contactTypeFromSender_Unknown_Unknown`` () =
    let result = ContactExtraction.contactTypeFromSender SenderClassification.SenderType.Unknown
    Assert.Equal("unknown", result)

// ─── harvestFromComprehension tests ──────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_harvestFromComprehension_TopLevelSenderName_ExtractsContact`` () =
    let json = """{"document_type":"bank-statement","sender_name":"CommBank"}"""
    let sender = Some "noreply@commbank.com.au"

    let result = ContactExtraction.harvestFromComprehension json sender

    Assert.True(result.IsSome)
    let contact = result.Value
    Assert.Equal("CommBank", contact.Name)
    Assert.Equal("commbank", contact.CanonicalName)
    Assert.Equal("supplier", contact.ContactType)
    Assert.Equal(Some "noreply@commbank.com.au", contact.Email)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_harvestFromComprehension_NestedFields_ExtractsContact`` () =
    let json =
        """{"document_type":"payslip","fields":{"employer":"Microsoft","abn":"12345678901"}}"""

    let result = ContactExtraction.harvestFromComprehension json None

    Assert.True(result.IsSome)
    let contact = result.Value
    Assert.Equal("Microsoft", contact.Name)
    Assert.Equal(Some "12345678901", contact.Abn)
    Assert.Equal("employer", contact.ContactType)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_harvestFromComprehension_NoName_ReturnsNone`` () =
    let json = """{"document_type":"invoice"}"""

    let result = ContactExtraction.harvestFromComprehension json None

    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_harvestFromComprehension_InvalidJson_ReturnsNone`` () =
    let result = ContactExtraction.harvestFromComprehension "not json" None

    Assert.True(result.IsNone)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_harvestFromComprehension_AngleBracketSender_ExtractsEmail`` () =
    let json = """{"sender_name":"CommBank"}"""
    let sender = Some "CommBank <noreply@commbank.com.au>"

    let result = ContactExtraction.harvestFromComprehension json sender

    Assert.True(result.IsSome)
    Assert.Equal(Some "noreply@commbank.com.au", result.Value.Email)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``ContactExtraction_harvestFromComprehension_AbnFromTopLevel_Extracted`` () =
    let json =
        """{"sender_name":"Ray White","abn":"98765432100","document_type":"agent-statement"}"""

    let result = ContactExtraction.harvestFromComprehension json None

    Assert.True(result.IsSome)
    let contact = result.Value
    Assert.Equal(Some "98765432100", contact.Abn)
    Assert.Equal("supplier", contact.ContactType)

// ─── harvestAndLink integration tests ────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ContactExtraction_harvestAndLink_InsertsContactAndLinks`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (original_name, saved_path, source_type, category, sha256) VALUES (@n, @p, @s, 'unsorted', 'sha1')"
                    [ ("@n", Database.boxVal "test.pdf"); ("@p", Database.boxVal "test.pdf"); ("@s", Database.boxVal "email") ]

            let compJson = """{"document_type":"payslip","sender_name":"Acme Corp Pty Ltd","fields":{"employer":"Acme Corp Pty Ltd","abn":"12345678901"}}"""

            do! ContactExtraction.harvestAndLink db TestHelpers.silentLogger 1L compJson (Some "noreply@acme.com")

            let! contacts = db.execReader "SELECT * FROM contacts" []
            Assert.Equal(1, contacts.Length)
            let r = Prelude.RowReader(contacts.[0])
            Assert.Equal("Acme Corp Pty Ltd", r.String "name" "")
            Assert.Equal("acme corp", r.String "canonical_name" "")
            Assert.Equal(Some "12345678901", r.OptString "abn")
            Assert.Equal("employer", r.String "contact_type" "")

            let! links = db.execReader "SELECT * FROM document_contacts" []
            Assert.Equal(1, links.Length)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ContactExtraction_harvestAndLink_DuplicateContact_UpdatesLastSeen`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (original_name, saved_path, source_type, category, sha256) VALUES ('a.pdf', 'a.pdf', 'email', 'unsorted', 'sha1')" []
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (original_name, saved_path, source_type, category, sha256) VALUES ('b.pdf', 'b.pdf', 'email', 'unsorted', 'sha2')" []

            let comp = """{"document_type":"invoice","sender_name":"CommBank"}"""

            do! ContactExtraction.harvestAndLink db TestHelpers.silentLogger 1L comp (Some "noreply@commbank.com.au")
            do! ContactExtraction.harvestAndLink db TestHelpers.silentLogger 2L comp (Some "noreply@commbank.com.au")

            let! contacts = db.execReader "SELECT * FROM contacts" []
            Assert.Equal(1, contacts.Length)

            let! links = db.execReader "SELECT * FROM document_contacts" []
            Assert.Equal(2, links.Length)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``ContactExtraction_harvestAndLink_NoName_NoContact`` () =
    task {
        let db = TestHelpers.createDb ()
        try
            let! _ =
                db.execNonQuery
                    "INSERT INTO documents (original_name, saved_path, source_type, category, sha256) VALUES ('a.pdf', 'a.pdf', 'email', 'unsorted', 'sha1')" []

            do! ContactExtraction.harvestAndLink db TestHelpers.silentLogger 1L """{"document_type":"unknown"}""" None

            let! contacts = db.execReader "SELECT * FROM contacts" []
            Assert.Equal(0, contacts.Length)
        finally
            db.dispose ()
    }
