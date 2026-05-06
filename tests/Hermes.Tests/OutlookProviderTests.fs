module Hermes.Tests.OutlookProviderTests

#nowarn "3261"

open Xunit
open Hermes.Core

// ─── Sidecar backwards compatibility ─────────────────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_ProviderIdField_ParsesProviderId`` () =
    let json = """{
  "source_type": "email_attachment",
  "account": "work",
  "provider_id": "abc",
  "original_name": "doc.pdf",
  "sha256": "aaa"
}"""
    match Classifier.parseSidecar json with
    | Ok meta -> Assert.Equal("abc", meta.ProviderId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_LegacyGmailId_FallsBackToProviderId`` () =
    let json = """{
  "source_type": "email_attachment",
  "account": "personal",
  "gmail_id": "xyz",
  "original_name": "invoice.pdf",
  "sha256": "bbb"
}"""
    match Classifier.parseSidecar json with
    | Ok meta -> Assert.Equal("xyz", meta.ProviderId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Classifier_ParseSidecar_BothProviderIdAndGmailId_ProviderIdWins`` () =
    let json = """{
  "source_type": "email_attachment",
  "account": "dual",
  "provider_id": "new-id",
  "gmail_id": "old-id",
  "original_name": "report.pdf",
  "sha256": "ccc"
}"""
    match Classifier.parseSidecar json with
    | Ok meta -> Assert.Equal("new-id", meta.ProviderId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

// ─── Config parsing: Outlook account fields ──────────────────────────

let private testEnv =
    TestHelpers.fakeEnvironment "/home/test" "/home/test/.config/hermes" "/home/test/Documents"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_OutlookAccount_ParsesClientAndTenant`` () =
    let yaml = """
accounts:
  - label: work-outlook
    provider: outlook
    client_id: my-client-id
    tenant_id: my-tenant
    redirect_port: 8080
"""
    match Config.parseYaml testEnv yaml with
    | Ok config ->
        Assert.Equal(1, config.Accounts.Length)
        let acct = config.Accounts.[0]
        Assert.Equal("outlook", acct.Provider)
        Assert.Equal("my-client-id", acct.ClientId)
        Assert.Equal("my-tenant", acct.TenantId)
        Assert.Equal(8080, acct.RedirectPort)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_OutlookAccount_DefaultTenantId`` () =
    let yaml = """
accounts:
  - label: work-outlook
    provider: outlook
    client_id: some-client
"""
    match Config.parseYaml testEnv yaml with
    | Ok config ->
        let acct = config.Accounts.[0]
        Assert.Equal("common", acct.TenantId)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Config_ParseYaml_OutlookAccount_DefaultRedirectPort`` () =
    let yaml = """
accounts:
  - label: work-outlook
    provider: outlook
    client_id: some-client
"""
    match Config.parseYaml testEnv yaml with
    | Ok config ->
        let acct = config.Accounts.[0]
        Assert.Equal(53682, acct.RedirectPort)
    | Error e -> failwith $"Expected Ok, got Error: {e}"

// ─── Extraction dispatch: PPTX file detection ───────────────────────

[<Theory>]
[<InlineData("report.pptx", true)>]
[<InlineData("report.PPTX", true)>]
[<InlineData("SLIDES.Pptx", true)>]
[<InlineData("report.pdf", false)>]
[<InlineData("report.docx", false)>]
[<InlineData("report.xlsx", false)>]
[<Trait("Category", "Unit")>]
let ``Extraction_IsPptx_DetectsCorrectly`` (path: string, expected: bool) =
    Assert.Equal(expected, Extraction.isPptx path)
