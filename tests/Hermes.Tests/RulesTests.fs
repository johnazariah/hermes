module Hermes.Tests.RulesTests

open System
open Xunit
open Hermes.Core

// ─── Rules YAML parsing tests ────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseYaml_ValidRules_ParsesCorrectly`` () =
    let yaml =
        """
rules:
  - name: plumber-invoices
    match:
      sender_domain: plumbing.com.au
    category: invoices

  - name: invoices-by-filename
    match:
      filename: "(?i)invoice"
    category: invoices

  - name: tax-by-subject
    match:
      subject: "(?i)tax|ato"
    category: tax

default_category: unsorted
"""

    match Rules.parseRulesYaml yaml with
    | Ok(rules, defaultCat) ->
        Assert.Equal("unsorted", defaultCat)
        Assert.Equal(3, rules.Length)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseYaml_EmptyYaml_ReturnsEmptyRules`` () =
    match Rules.parseRulesYaml "" with
    | Ok(rules, defaultCat) ->
        Assert.Empty(rules)
        Assert.Equal("unsorted", defaultCat)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseYaml_CustomDefaultCategory_Respected`` () =
    let yaml =
        """
rules: []
default_category: misc
"""

    match Rules.parseRulesYaml yaml with
    | Ok(_, defaultCat) -> Assert.Equal("misc", defaultCat)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

// ─── Classification cascade tests ────────────────────────────────────

let private testRulesYaml =
    """
rules:
  - name: plumber-domain
    match:
      sender_domain: plumbing.com.au
    category: trades

  - name: invoices-by-filename
    match:
      filename: "(?i)invoice"
    category: invoices

  - name: receipts-by-filename
    match:
      filename: "(?i)receipt"
    category: receipts

  - name: tax-by-subject
    match:
      subject: "(?i)tax|ato|mygov"
    category: tax

default_category: unsorted
"""

let private loadTestRules () =
    match Rules.parseRulesYaml testRulesYaml with
    | Ok(rules, defaultCat) -> rules, defaultCat
    | Error e -> failwith $"Failed to load test rules: {e}"

let private makeSidecar
    (sender: string option)
    (subject: string option)
    : Domain.SidecarMetadata option =
    Some
        { SourceType = "email_attachment"
          Account = "test"
          GmailId = "msg123"
          ThreadId = "thread123"
          Sender = sender
          Subject = subject
          EmailDate = Some "2025-03-15T10:30:00+11:00"
          OriginalName = "test.pdf"
          SavedAs = "test.pdf"
          Sha256 = "abc123"
          DownloadedAt = "2025-03-15T10:30:00Z" }

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_DomainMatch_TakesPriority`` () =
    let rules, defaultCat = loadTestRules ()
    let sidecar = makeSidecar (Some "bob@plumbing.com.au") (Some "Tax Invoice March")

    // Even though filename contains "invoice" and subject contains "tax",
    // domain rule should win because it's checked first
    let result = Rules.classifyWithRules rules defaultCat sidecar "Invoice-2025.pdf"

    Assert.Equal("trades", result.Category)

    match result.MatchedRule with
    | Domain.DomainRule(name, _) -> Assert.Equal("plumber-domain", name)
    | other -> failwith $"Expected DomainRule, got {other}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_FilenameMatch_WhenNoDomainMatch`` () =
    let rules, defaultCat = loadTestRules ()
    let sidecar = makeSidecar (Some "alice@example.com") None

    let result = Rules.classifyWithRules rules defaultCat sidecar "Invoice-March-2025.pdf"

    Assert.Equal("invoices", result.Category)

    match result.MatchedRule with
    | Domain.FilenameRule(name, _) -> Assert.Equal("invoices-by-filename", name)
    | other -> failwith $"Expected FilenameRule, got {other}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_SubjectMatch_WhenNoFilenameMatch`` () =
    let rules, defaultCat = loadTestRules ()
    let sidecar = makeSidecar (Some "noreply@ato.gov.au") (Some "Your ATO notice")

    let result = Rules.classifyWithRules rules defaultCat sidecar "document.pdf"

    Assert.Equal("tax", result.Category)

    match result.MatchedRule with
    | Domain.SubjectRule(name, _) -> Assert.Equal("tax-by-subject", name)
    | other -> failwith $"Expected SubjectRule, got {other}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_DefaultRule_WhenNoMatch`` () =
    let rules, defaultCat = loadTestRules ()
    let sidecar = makeSidecar (Some "someone@example.com") (Some "Hello world")

    let result = Rules.classifyWithRules rules defaultCat sidecar "random-file.pdf"

    Assert.Equal("unsorted", result.Category)
    Assert.Equal(Domain.DefaultRule, result.MatchedRule)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_NoSidecar_MatchesFilenameOnly`` () =
    let rules, defaultCat = loadTestRules ()

    let result = Rules.classifyWithRules rules defaultCat None "receipt-2025-03.pdf"

    Assert.Equal("receipts", result.Category)

    match result.MatchedRule with
    | Domain.FilenameRule(name, _) -> Assert.Equal("receipts-by-filename", name)
    | other -> failwith $"Expected FilenameRule, got {other}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_NoSidecar_NoFilenameMatch_DefaultsToUnsorted`` () =
    let rules, defaultCat = loadTestRules ()

    let result = Rules.classifyWithRules rules defaultCat None "random.pdf"

    Assert.Equal("unsorted", result.Category)
    Assert.Equal(Domain.DefaultRule, result.MatchedRule)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_CaseInsensitive_FilenameMatch`` () =
    let rules, defaultCat = loadTestRules ()

    let result = Rules.classifyWithRules rules defaultCat None "INVOICE_MARCH.PDF"

    Assert.Equal("invoices", result.Category)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Classify_CaseInsensitive_SubjectMatch`` () =
    let rules, defaultCat = loadTestRules ()
    let sidecar = makeSidecar None (Some "YOUR TAX RETURN")

    let result = Rules.classifyWithRules rules defaultCat sidecar "document.pdf"

    Assert.Equal("tax", result.Category)

// ─── Cascade priority tests ──────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Cascade_DomainBeatsFilename`` () =
    let rules, defaultCat = loadTestRules ()
    // Domain matches "plumbing.com.au" -> trades
    // Filename matches "invoice" -> invoices
    // Domain should win
    let sidecar = makeSidecar (Some "bob@plumbing.com.au") None

    let result = Rules.classifyWithRules rules defaultCat sidecar "invoice.pdf"

    Assert.Equal("trades", result.Category)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Cascade_FilenameBeatsSubject`` () =
    let rules, defaultCat = loadTestRules ()
    // Filename matches "invoice" -> invoices
    // Subject matches "tax" -> tax
    // Filename should win (no domain match)
    let sidecar = makeSidecar (Some "someone@example.com") (Some "tax invoice")

    let result = Rules.classifyWithRules rules defaultCat sidecar "invoice-2025.pdf"

    Assert.Equal("invoices", result.Category)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_Cascade_SubjectBeatsDefault`` () =
    let rules, defaultCat = loadTestRules ()
    // No domain match, no filename match, subject matches "tax"
    let sidecar = makeSidecar (Some "someone@example.com") (Some "Important tax document")

    let result = Rules.classifyWithRules rules defaultCat sidecar "document.pdf"

    Assert.Equal("tax", result.Category)

// ─── Default rules.yaml from Config ──────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseDefaultRulesYaml_Succeeds`` () =
    let yaml = Config.defaultRulesYaml ()

    match Rules.parseRulesYaml yaml with
    | Ok(rules, defaultCat) ->
        Assert.True(rules.Length > 0, "Default rules should have at least one rule")
        Assert.Equal("unsorted", defaultCat)
    | Error e -> failwith $"Expected Ok, got Error: {e}"
