module Hermes.Tests.RulesTests

open System
open System.Threading.Tasks
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
        ({ SourceType = "email_attachment"
           Account = "test"
           GmailId = "msg123"
           ThreadId = "thread123"
           Sender = sender
           Subject = subject
           EmailDate = Some "2025-03-15T10:30:00+11:00"
           OriginalName = "test.pdf"
           SavedAs = "test.pdf"
           Sha256 = "abc123"
           DownloadedAt = "2025-03-15T10:30:00Z" } : Domain.SidecarMetadata)

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

// ─── parseContentRules tests ─────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseContentRules_ValidYaml_ReturnsRules`` () =
    let yaml =
        """
rules: []
default_category: unsorted
content_rules:
  - name: invoice-content
    match:
      content_any: ["invoice", "amount due"]
      has_table: true
      table_headers_all: ["date", "amount"]
    category: invoices
    confidence: 0.8
"""

    let result = Rules.parseContentRules yaml
    Assert.Equal(1, result.Length)
    Assert.Equal("invoice-content", result.[0].Name)
    Assert.Equal("invoices", result.[0].Category)
    Assert.Equal(0.8, result.[0].Confidence)
    Assert.True(result.[0].Conditions.Length >= 3, $"Expected >=3 conditions, got {result.[0].Conditions.Length}")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseContentRules_EmptyYaml_ReturnsEmpty`` () =
    let result = Rules.parseContentRules ""
    Assert.Empty(result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseContentRules_NoContentRulesSection_ReturnsEmpty`` () =
    let yaml =
        """
rules:
  - name: test
    match:
      filename: "(?i)test"
    category: tests
default_category: unsorted
"""

    let result = Rules.parseContentRules yaml
    Assert.Empty(result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseContentRules_ContentAnyAndHasAmount`` () =
    let yaml =
        """
rules: []
default_category: unsorted
content_rules:
  - name: bill-content
    match:
      content_any: ["bill", "payment"]
      has_amount: true
    category: bills
    confidence: 0.7
"""

    let result = Rules.parseContentRules yaml
    Assert.Equal(1, result.Length)
    let conditions = result.[0].Conditions
    let hasContentAny = conditions |> List.exists (fun c -> match c with Domain.ContentAny _ -> true | _ -> false)
    let hasAmount = conditions |> List.exists (fun c -> match c with Domain.HasAmount -> true | _ -> false)
    Assert.True(hasContentAny, "Expected ContentAny condition")
    Assert.True(hasAmount, "Expected HasAmount condition")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseContentRules_ZeroConfidence_DefaultsToHalf`` () =
    let yaml =
        """
rules: []
default_category: unsorted
content_rules:
  - name: test
    match:
      content_any: ["test"]
    category: misc
    confidence: 0.0
"""

    let result = Rules.parseContentRules yaml
    Assert.Equal(1, result.Length)
    Assert.Equal(0.5, result.[0].Confidence)

// ─── classifyWithRules edge cases ────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ClassifyWithRules_EmptyRules_ReturnsDefault`` () =
    let result = Rules.classifyWithRules [] "unsorted" None "test.pdf"
    Assert.Equal("unsorted", result.Category)
    Assert.Equal(Domain.DefaultRule, result.MatchedRule)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ClassifyWithRules_EmptyRules_CustomDefault_Respected`` () =
    let result = Rules.classifyWithRules [] "misc" None "test.pdf"
    Assert.Equal("misc", result.Category)
    Assert.Equal(Domain.DefaultRule, result.MatchedRule)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ClassifyWithRules_SubjectMatchOnly_WhenNoSender`` () =
    let rules, defaultCat = loadTestRules ()
    let sidecar = makeSidecar None (Some "ATO tax notice")
    let result = Rules.classifyWithRules rules defaultCat sidecar "document.pdf"
    Assert.Equal("tax", result.Category)
    match result.MatchedRule with
    | Domain.SubjectRule(name, _) -> Assert.Equal("tax-by-subject", name)
    | other -> failwith $"Expected SubjectRule, got {other}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ClassifyWithRules_EmptySubject_SkipsSubjectRules`` () =
    let rules, defaultCat = loadTestRules ()
    let sidecar = makeSidecar (Some "someone@example.com") (Some "")
    let result = Rules.classifyWithRules rules defaultCat sidecar "random.pdf"
    Assert.Equal("unsorted", result.Category)
    Assert.Equal(Domain.DefaultRule, result.MatchedRule)

// ─── Rules.fromFile tests ────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_FromFile_ValidYaml_LoadsRules`` () =
    let m = TestHelpers.memFs ()
    let logger = Logging.silent
    let yaml = """
rules:
  - name: test-rule
    match:
      filename: "(?i)invoice"
    category: invoices
default_category: unsorted
"""
    m.Put "/config/rules.yaml" yaml
    let engine = Rules.fromFile m.Fs logger "/config/rules.yaml" |> Async.AwaitTask |> Async.RunSynchronously
    let result = engine.classify None "Invoice-March.pdf"
    Assert.Equal("invoices", result.Category)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_FromFile_MissingFile_UsesDefaults`` () =
    let m = TestHelpers.memFs ()
    let logger = Logging.silent
    let engine = Rules.fromFile m.Fs logger "/nonexistent/rules.yaml" |> Async.AwaitTask |> Async.RunSynchronously
    let result = engine.classify None "anything.pdf"
    Assert.Equal("unsorted", result.Category)
    Assert.Equal(Domain.DefaultRule, result.MatchedRule)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_FromFile_Reload_UpdatesRules`` () =
    task {
        let m = TestHelpers.memFs ()
        let logger = Logging.silent
        m.Put "/config/rules.yaml" """
rules:
  - name: old-rule
    match:
      filename: "(?i)old"
    category: archive
default_category: unsorted
"""
        let! engine = Rules.fromFile m.Fs logger "/config/rules.yaml"

        // Initial classification
        let r1 = engine.classify None "old-document.pdf"
        Assert.Equal("archive", r1.Category)

        // Update rules
        m.Put "/config/rules.yaml" """
rules:
  - name: new-rule
    match:
      filename: "(?i)new"
    category: recent
default_category: misc
"""
        let! reloadResult = engine.reload ()
        Assert.True(Result.isOk reloadResult)

        // Should now classify differently
        let r2 = engine.classify None "new-document.pdf"
        Assert.Equal("recent", r2.Category)
    }

// ─── parseRulesYaml edge cases ───────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseYaml_InvalidYaml_ReturnsError`` () =
    match Rules.parseRulesYaml "invalid: yaml: [broken" with
    | Error _ -> ()
    | Ok _ -> failwith "Expected Error for invalid YAML"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseYaml_NullRules_ReturnsEmptyList`` () =
    let yaml = """
default_category: misc
"""
    match Rules.parseRulesYaml yaml with
    | Ok (rules, defaultCat) ->
        Assert.Empty(rules)
        Assert.Equal("misc", defaultCat)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

// ─── Rules branch coverage: invalid regex, missing match, read exception ─

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseYaml_InvalidRegex_SkipsRule`` () =
    let yaml = """
rules:
  - name: bad-regex-rule
    match:
      filename: "[invalid"
    category: broken
  - name: good-rule
    match:
      filename: "(?i)invoice"
    category: invoices
default_category: unsorted
"""
    match Rules.parseRulesYaml yaml with
    | Ok (rules, defaultCat) ->
        Assert.Equal("unsorted", defaultCat)
        let filenameRules =
            rules |> List.filter (fun r ->
                match r.Kind with
                | Rules.FilenameMatch _ -> true
                | _ -> false)
        Assert.Equal(1, filenameRules.Length)
        Assert.Equal("good-rule", filenameRules.[0].Name)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_ParseYaml_MissingMatch_SkipsRule`` () =
    let yaml = """
rules:
  - name: no-match-rule
    category: broken
  - name: valid-rule
    match:
      sender_domain: example.com
    category: emails
default_category: unsorted
"""
    match Rules.parseRulesYaml yaml with
    | Ok (rules, _) ->
        Assert.Equal(1, rules.Length)
        Assert.Equal("valid-rule", rules.[0].Name)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Rules_FromFile_ReadException_ReturnsDefaults`` () =
    task {
        let m = TestHelpers.memFs ()
        let throwingFs =
            { m.Fs with
                fileExists = fun path -> path.Replace('\\', '/').Contains("rules.yaml")
                readAllText = fun _ -> task { return failwith "disk read error" } }
        let! engine = Rules.fromFile throwingFs TestHelpers.silentLogger "/config/rules.yaml"
        let result = engine.classify None "anything.pdf"
        Assert.Equal("unsorted", result.Category)
        Assert.Equal(Domain.DefaultRule, result.MatchedRule)
    }