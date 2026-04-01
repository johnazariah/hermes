namespace Hermes.Core

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
