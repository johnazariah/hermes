namespace Hermes.Core

#nowarn "3261"

open System

/// Content-based classification (Tier 2): match extracted markdown against
/// keyword and table-header rules to classify documents Tier 1 couldn't handle.
[<RequireQualifiedAccess>]
module ContentClassifier =

    // ─── Condition evaluators ────────────────────────────────────────

    let private textContains (text: string) (keyword: string) =
        text.Contains(keyword, StringComparison.OrdinalIgnoreCase)

    let private evaluateCondition
        (markdown: string)
        (tables: PdfStructure.Table list)
        (hasAmount: bool)
        (hasDate: bool)
        (condition: Domain.ContentMatch) : bool =
        match condition with
        | Domain.ContentAny keywords ->
            keywords |> List.exists (textContains markdown)
        | Domain.ContentAll keywords ->
            keywords |> List.forall (textContains markdown)
        | Domain.HasTable ->
            not tables.IsEmpty
        | Domain.TableHeadersAny headers ->
            tables
            |> List.exists (fun t ->
                headers
                |> List.exists (fun h ->
                    t.Headers |> List.exists (fun th ->
                        th.Contains(h, StringComparison.OrdinalIgnoreCase))))
        | Domain.TableHeadersAll headers ->
            tables
            |> List.exists (fun t ->
                headers
                |> List.forall (fun h ->
                    t.Headers |> List.exists (fun th ->
                        th.Contains(h, StringComparison.OrdinalIgnoreCase))))
        | Domain.HasAmount -> hasAmount
        | Domain.HasDate -> hasDate

    // ─── Rule evaluation ─────────────────────────────────────────────

    /// Evaluate a single content rule. Returns (category, confidence) if all conditions match.
    let evaluateRule
        (markdown: string)
        (tables: PdfStructure.Table list)
        (amount: decimal option)
        (rule: Domain.ContentRule)
        : (string * float) option =
        let hasAmount = amount.IsSome
        let hasDate = Extraction.tryExtractDate markdown |> Option.isSome
        let allMatch =
            rule.Conditions
            |> List.forall (evaluateCondition markdown tables hasAmount hasDate)
        if allMatch then Some (rule.Category, rule.Confidence)
        else None

    /// Evaluate all content rules, return best match (highest confidence).
    let classify
        (markdown: string)
        (tables: PdfStructure.Table list)
        (amount: decimal option)
        (rules: Domain.ContentRule list)
        : (string * float) option =
        rules
        |> List.choose (evaluateRule markdown tables amount)
        |> List.sortByDescending snd
        |> List.tryHead

    // ─── Tier 3: LLM classification ──────────────────────────────────

    let private maxPromptChars = 2000

    /// Build a classification prompt with truncated document content.
    let buildClassificationPrompt (markdown: string) (categories: string list) : string =
        let truncated =
            if markdown.Length <= maxPromptChars then markdown
            else markdown.Substring(0, maxPromptChars) + "\n[... truncated]"
        let catList = categories |> String.concat ", "
        $"""Classify this document into one of these categories: {catList}

Respond with ONLY a JSON object:
{{"category": "<category>", "confidence": <0.0-1.0>, "reasoning": "<brief explanation>"}}

Document content:
{truncated}"""

    /// Parse LLM JSON response into (category, confidence, reasoning).
    let parseClassificationResponse (response: string) : (string * float * string) option =
        try
            let trimmed = response.Trim()
            // Extract JSON if wrapped in markdown code block
            let json =
                if trimmed.StartsWith("```") then
                    let startIdx = trimmed.IndexOf('{')
                    let endIdx = trimmed.LastIndexOf('}')
                    if startIdx >= 0 && endIdx > startIdx then
                        trimmed.Substring(startIdx, endIdx - startIdx + 1)
                    else trimmed
                else trimmed
            let doc = System.Text.Json.JsonDocument.Parse(json)
            let root = doc.RootElement
            let category =
                match root.TryGetProperty("category") with
                | true, v -> Some (v.GetString())
                | _ -> None
            let confidence =
                match root.TryGetProperty("confidence") with
                | true, v -> Some (v.GetDouble())
                | _ -> None
            let reasoning =
                match root.TryGetProperty("reasoning") with
                | true, v -> v.GetString() |> Option.ofObj |> Option.defaultValue ""
                | _ -> ""
            match category, confidence with
            | Some cat, Some conf when not (String.IsNullOrEmpty(cat)) -> Some (cat, conf, reasoning)
            | _ -> None
        with _ -> None
