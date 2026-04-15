module Hermes.Tests.SenderClassificationTests

open Xunit
open Hermes.Core

// ─── extractDomain tests ─────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_extractDomain_ValidEmail_ReturnsDomain`` () =
    let result = SenderClassification.extractDomain "test@commbank.com.au"
    Assert.Equal("commbank.com.au", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_extractDomain_NoAtSign_ReturnsInputLowered`` () =
    let result = SenderClassification.extractDomain "noemail"
    Assert.Equal("noemail", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_extractDomain_EmptyString_ReturnsEmpty`` () =
    let result = SenderClassification.extractDomain ""
    Assert.Equal("", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_extractDomain_AngleBracketFormat_ExtractsDomain`` () =
    let result = SenderClassification.extractDomain "CommBank <noreply@commbank.com.au>"
    Assert.Equal("commbank.com.au", result)

// ─── classify: known domain tests ────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_KnownBankDomain_ReturnsBank`` () =
    let result = SenderClassification.classify "statements@commbank.com.au"
    Assert.Equal(SenderClassification.SenderType.Bank, result.SenderType)
    Assert.Equal("CommBank", result.DisplayLabel)
    Assert.True(result.DocumentTypeHints |> List.contains "bank-statement")
    Assert.True(result.DocumentTypeHints |> List.contains "credit-card-statement")
    Assert.Equal(0.8, result.Confidence)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_KnownPropertyManager_ReturnsPropertyManager`` () =
    let result = SenderClassification.classify "noreply@raywhite.com"
    Assert.Equal(SenderClassification.SenderType.PropertyManager, result.SenderType)
    Assert.Equal("Ray White", result.DisplayLabel)
    Assert.True(result.DocumentTypeHints |> List.contains "agent-statement")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_KnownGovernment_ReturnsGovernment`` () =
    let result = SenderClassification.classify "noreply@ato.gov.au"
    Assert.Equal(SenderClassification.SenderType.Government, result.SenderType)
    Assert.Equal("ATO", result.DisplayLabel)
    Assert.True(result.DocumentTypeHints |> List.contains "tax-return")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_KnownUtility_ReturnsUtility`` () =
    let result = SenderClassification.classify "billing@originenergy.com.au"
    Assert.Equal(SenderClassification.SenderType.Utility, result.SenderType)
    Assert.Equal("Origin Energy", result.DisplayLabel)
    Assert.True(result.DocumentTypeHints |> List.contains "utility-bill")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_KnownInsurance_ReturnsInsurance`` () =
    let result = SenderClassification.classify "policy@nrma.com.au"
    Assert.Equal(SenderClassification.SenderType.Insurance, result.SenderType)
    Assert.Equal("NRMA", result.DisplayLabel)
    Assert.True(result.DocumentTypeHints |> List.contains "insurance-policy")

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_KnownEmployer_ReturnsEmployer`` () =
    let result = SenderClassification.classify "payroll@microsoft.com"
    Assert.Equal(SenderClassification.SenderType.Employer, result.SenderType)
    Assert.Equal("Microsoft", result.DisplayLabel)
    Assert.True(result.DocumentTypeHints |> List.contains "payslip")

// ─── classify: fallback + unknown tests ──────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_UnknownDomain_FallsBackToDisplayName`` () =
    let result = SenderClassification.classify "Commonwealth Bank <unknown@email.com>"
    Assert.Equal(SenderClassification.SenderType.Bank, result.SenderType)
    Assert.Equal("CommBank", result.DisplayLabel)
    Assert.Equal(0.8, result.Confidence)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_UnknownBoth_ReturnsUnknown`` () =
    let result = SenderClassification.classify "someone@example.com"
    Assert.Equal(SenderClassification.SenderType.Unknown, result.SenderType)
    Assert.Equal(0.0, result.Confidence)
    Assert.Empty(result.DocumentTypeHints)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_EmptyInput_ReturnsUnknown`` () =
    let result = SenderClassification.classify ""
    Assert.Equal(SenderClassification.SenderType.Unknown, result.SenderType)
    Assert.Equal(0.0, result.Confidence)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_WhitespaceOnly_ReturnsUnknown`` () =
    let result = SenderClassification.classify "   "
    Assert.Equal(SenderClassification.SenderType.Unknown, result.SenderType)
    Assert.Equal(0.0, result.Confidence)

// ─── classify: case + subdomain tests ────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_CaseInsensitiveDomain_Matches`` () =
    let result = SenderClassification.classify "user@COMMBANK.COM.AU"
    Assert.Equal(SenderClassification.SenderType.Bank, result.SenderType)
    Assert.Equal("CommBank", result.DisplayLabel)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_classify_SubdomainMatch_Matches`` () =
    let result = SenderClassification.classify "user@mail.commbank.com.au"
    Assert.Equal(SenderClassification.SenderType.Bank, result.SenderType)
    Assert.Equal("CommBank", result.DisplayLabel)

// ─── formatHint tests ────────────────────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_formatHint_Bank_FormatsCorrectly`` () =
    let hint : SenderClassification.SenderHint =
        { SenderType = SenderClassification.SenderType.Bank
          DisplayLabel = "CommBank"
          DocumentTypeHints = [ "bank-statement"; "credit-card-statement" ]
          Confidence = 0.8 }

    let result = SenderClassification.formatHint hint

    Assert.Contains("Bank", result)
    Assert.Contains("CommBank", result)
    Assert.Contains("bank-statement", result)
    Assert.Contains("credit-card-statement", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_formatHint_Unknown_ReturnsEmpty`` () =
    let hint : SenderClassification.SenderHint =
        { SenderType = SenderClassification.SenderType.Unknown
          DisplayLabel = ""
          DocumentTypeHints = []
          Confidence = 0.0 }

    let result = SenderClassification.formatHint hint
    Assert.Equal("", result)

[<Fact>]
[<Trait("Category", "Unit")>]
let ``SenderClassification_formatHint_Utility_IncludesTypeAndLabel`` () =
    let hint : SenderClassification.SenderHint =
        { SenderType = SenderClassification.SenderType.Utility
          DisplayLabel = "Origin Energy"
          DocumentTypeHints = [ "utility-bill" ]
          Confidence = 0.8 }

    let result = SenderClassification.formatHint hint

    Assert.Contains("Utility", result)
    Assert.Contains("Origin Energy", result)
    Assert.Contains("utility-bill", result)
