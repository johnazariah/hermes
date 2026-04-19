namespace Hermes.Core

open System
open System.IO
open System.Threading.Tasks

/// Load external prompt templates, render with variable substitution,
/// and normalise LLM comprehension responses to canonical categories.
[<RequireQualifiedAccess>]
module PromptLoader =

    // ─── Types ───────────────────────────────────────────────────────

    type ParsedPrompt =
        { System: string
          UserTemplate: string }

    // ─── Parsing ─────────────────────────────────────────────────────

    let [<Literal>] private SystemDelimiter = "---SYSTEM---"
    let [<Literal>] private UserDelimiter = "---USER---"

    let parse (content: string) : Result<ParsedPrompt, string> =
        let sysIdx = content.IndexOf(SystemDelimiter)
        let usrIdx = content.IndexOf(UserDelimiter)

        if sysIdx < 0 then
            Error "Missing ---SYSTEM--- delimiter"
        elif usrIdx < 0 then
            Error "Missing ---USER--- delimiter"
        else
            let systemText =
                content.Substring(sysIdx + SystemDelimiter.Length, usrIdx - sysIdx - SystemDelimiter.Length).Trim()

            let userText =
                content.Substring(usrIdx + UserDelimiter.Length).Trim()

            match String.IsNullOrWhiteSpace systemText, String.IsNullOrWhiteSpace userText with
            | true, _ -> Error "System section is empty"
            | _, true -> Error "User section is empty"
            | _ -> Ok { System = systemText; UserTemplate = userText }

    // ─── Rendering ───────────────────────────────────────────────────

    let [<Literal>] private MaxChars = 3000

    let private truncate (maxChars: int) (text: string) =
        if text.Length <= maxChars then text
        else text.Substring(0, maxChars) + "\n[... truncated]"

    let render (prompt: ParsedPrompt) (documentText: string) (context: string) : string =
        let truncated = documentText |> truncate MaxChars

        prompt.UserTemplate
            .Replace("{{document_text}}", truncated)
            .Replace("{{context}}", context)

    // ─── File loading ────────────────────────────────────────────────

    let loadFromFile (fs: Algebra.FileSystem) (path: string) : Task<Result<ParsedPrompt, string>> =
        task {
            if not (fs.fileExists path) then
                return Error $"Prompt file not found: {path}"
            else
                let! content = fs.readAllText path
                return parse content
        }

    let loadWithFallback
        (fs: Algebra.FileSystem)
        (configDir: string)
        (assemblyDir: string)
        : Task<Result<ParsedPrompt, string>> =
        let configPath = Path.Combine(configDir, "prompts", "comprehension.md")
        let assemblyPath = Path.Combine(assemblyDir, "prompts", "comprehension.md")

        if fs.fileExists configPath then loadFromFile fs configPath
        elif fs.fileExists assemblyPath then loadFromFile fs assemblyPath
        else Task.FromResult(Error $"Prompt file not found in {configPath} or {assemblyPath}")

    let loadTriagePrompt
        (fs: Algebra.FileSystem)
        (configDir: string)
        (assemblyDir: string)
        : Task<Result<ParsedPrompt, string>> =
        let configPath = Path.Combine(configDir, "prompts", "triage.md")
        let assemblyPath = Path.Combine(assemblyDir, "prompts", "triage.md")

        if fs.fileExists configPath then loadFromFile fs configPath
        elif fs.fileExists assemblyPath then loadFromFile fs assemblyPath
        else Task.FromResult(Error $"Triage prompt not found in {configPath} or {assemblyPath}")

    let [<Literal>] private TriageMaxChars = 2000

    let renderTriage (prompt: ParsedPrompt) (documentText: string) (context: string) : string =
        let truncated = documentText |> truncate TriageMaxChars
        prompt.UserTemplate
            .Replace("{{document_text}}", truncated)
            .Replace("{{context}}", context)


/// Normalise LLM comprehension output to Hermes canonical categories.
[<RequireQualifiedAccess>]
module ComprehensionSchema =

    // ─── Category mapping ────────────────────────────────────────────

    /// Maps LLM document_type values to Hermes canonical archive categories.
    let canonicalCategories : Map<string, string> =
        [ "payslip", "payslips"
          "agent-statement", "property"
          "mortgage-statement", "property"
          "depreciation-schedule", "property"
          "bank-statement", "bank-statements"
          "donation-receipt", "donations"
          "insurance-policy", "insurance"
          "utility-bill", "utilities"
          "council-rates", "rates-and-tax"
          "land-tax", "rates-and-tax"
          "tax-return", "tax"
          "payg-instalment", "tax"
          "stock-vest", "tax"
          "espp-statement", "tax"
          "dividend-statement", "tax"
          "expense-receipt", "receipts"
          "medical", "medical"
          "legal", "legal"
          "superannuation", "tax"
          "vehicle", "receipts"
          "letter", "unsorted"
          "notification", "unsorted"
          "report", "unsorted"
          "other", "unclassified"
          // Non-financial email categories
          "dev-notifications", "dev-notifications"
          "work-related", "work-related"
          "school-related", "school-related"
          "church-community", "church-community"
          "personal", "personal"
          "household", "household"
          "government", "government"
          "shopping-deals", "shopping-deals"
          "food-dining", "food-dining"
          "travel-offers", "travel-offers"
          "subscriptions", "subscriptions"
          "social-media", "social-media"
          "finance-alerts", "finance-alerts"
          "security-alerts", "security-alerts"
          // Legacy aliases
          "marketing", "shopping-deals"
          "automated", "dev-notifications"
          "social", "social-media"
          "official", "government"
          "work", "work-related"
          "friends", "personal"
          "marketing-material", "shopping-deals"
          // Common aliases
          "invoice", "invoices"
          "receipt", "receipts"
          "paycheck", "payslips"
          "rental-statement", "property"
          "property-statement", "property"
          "water-bill", "utilities"
          "electricity-bill", "utilities"
          "gas-bill", "utilities"
          "phone-bill", "utilities"
          "internet-bill", "utilities" ]
        |> Map.ofList

    let normaliseCategory (rawType: string) : string =
        rawType.Trim().ToLowerInvariant()
        |> fun key -> canonicalCategories |> Map.tryFind key
        |> Option.defaultValue "unclassified"

    // ─── Response normalisation ──────────────────────────────────────

    /// Normalised comprehension result with canonical category applied.
    type NormalisedResponse =
        { DocumentType: string
          CanonicalCategory: string
          Confidence: float
          Summary: string
          RawJson: string }

    let stripCodeFences (text: string) =
        let trimmed = text.Trim()

        if trimmed.StartsWith("```") then
            let startIdx = trimmed.IndexOf('{')
            let endIdx = trimmed.LastIndexOf('}')

            if startIdx >= 0 && endIdx > startIdx then
                trimmed.Substring(startIdx, endIdx - startIdx + 1)
            else
                trimmed
        else
            trimmed

    let private clampConfidence (v: float) : float =
        v |> max 0.0 |> min 1.0

    let private getString (root: Text.Json.JsonElement) (prop: string) (fallback: string) =
        match root.TryGetProperty(prop) with
        | true, v when v.ValueKind = Text.Json.JsonValueKind.String ->
            v.GetString() |> Option.ofObj |> Option.defaultValue fallback
        | _ -> fallback

    let private getFloat (root: Text.Json.JsonElement) (prop: string) (fallback: float) =
        match root.TryGetProperty(prop) with
        | true, v ->
            match v.TryGetDouble() with
            | true, d -> d
            | _ -> fallback
        | _ -> fallback

    let normaliseResponse (rawJson: string) : Result<NormalisedResponse, string> =
        try
            let json = rawJson |> stripCodeFences
            use doc = Text.Json.JsonDocument.Parse(json)
            let root = doc.RootElement

            let docType = getString root "document_type" "unknown"
            let confidence = getFloat root "confidence" 0.5 |> clampConfidence
            let summary = getString root "summary" ""

            Ok
                { DocumentType = docType
                  CanonicalCategory = normaliseCategory docType
                  Confidence = confidence
                  Summary = summary
                  RawJson = json }
        with ex ->
            Error $"JSON parse failed: {ex.Message}"
