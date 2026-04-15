namespace Hermes.Core

open System

/// Classify email senders into document-type hints for LLM prompt enrichment.
/// Provides richer document-type hints alongside Rules.fs sender_domain matching.
/// The LLM still makes the final call — these are strong hints, not hard classifications.
[<RequireQualifiedAccess>]
module SenderClassification =

    // ─── Types ───────────────────────────────────────────────────────

    type SenderType =
        | Bank
        | PropertyManager
        | Employer
        | Government
        | Utility
        | Insurance
        | Retailer
        | Unknown

    module SenderType =
        let toDisplayString =
            function
            | Bank -> "Bank"
            | PropertyManager -> "Property Manager"
            | Employer -> "Employer"
            | Government -> "Government"
            | Utility -> "Utility"
            | Insurance -> "Insurance"
            | Retailer -> "Retailer"
            | Unknown -> "Unknown"

    type SenderHint =
        { SenderType: SenderType
          DisplayLabel: string
          DocumentTypeHints: string list
          Confidence: float }

    // ─── Lookup data ─────────────────────────────────────────────────

    type private Entry =
        { Type: SenderType
          Label: string
          Hints: string list }

    let private entry t l h = { Type = t; Label = l; Hints = h }

    let private domainEntries =
        [ "commbank.com.au",        entry Bank "CommBank" [ "bank-statement"; "credit-card-statement" ]
          "westpac.com.au",         entry Bank "Westpac" [ "bank-statement" ]
          "nab.com.au",             entry Bank "NAB" [ "bank-statement" ]
          "anz.com",                entry Bank "ANZ" [ "bank-statement" ]
          "anz.com.au",             entry Bank "ANZ" [ "bank-statement" ]
          "citibank.com",           entry Bank "Citibank" [ "bank-statement"; "credit-card-statement" ]
          "citi.com",               entry Bank "Citibank" [ "bank-statement"; "credit-card-statement" ]
          "ing.com.au",             entry Bank "ING" [ "bank-statement" ]
          "macquarie.com",          entry Bank "Macquarie" [ "bank-statement" ]
          "macquarie.com.au",       entry Bank "Macquarie" [ "bank-statement" ]
          "raywhite.com",           entry PropertyManager "Ray White" [ "agent-statement"; "rental-statement" ]
          "ljhooker.com",           entry PropertyManager "LJ Hooker" [ "agent-statement" ]
          "ljhooker.com.au",        entry PropertyManager "LJ Hooker" [ "agent-statement" ]
          "barryplant.com.au",      entry PropertyManager "Barry Plant" [ "agent-statement" ]
          "holidayhub.com.au",      entry PropertyManager "Holiday Hub" [ "agent-statement" ]
          "microsoft.com",          entry Employer "Microsoft" [ "payslip"; "payroll-statement" ]
          "ato.gov.au",             entry Government "ATO" [ "tax-return"; "payg-instalment"; "notification" ]
          "mygov.au",               entry Government "myGov" [ "notification" ]
          "my.gov.au",              entry Government "myGov" [ "notification" ]
          "sro.vic.gov.au",         entry Government "State Revenue" [ "land-tax"; "council-rates" ]
          "revenue.nsw.gov.au",     entry Government "State Revenue" [ "land-tax"; "council-rates" ]
          "osr.qld.gov.au",         entry Government "State Revenue" [ "land-tax"; "council-rates" ]
          "origin.com.au",          entry Utility "Origin Energy" [ "utility-bill" ]
          "originenergy.com.au",    entry Utility "Origin Energy" [ "utility-bill" ]
          "agl.com.au",             entry Utility "AGL" [ "utility-bill" ]
          "energyaustralia.com.au", entry Utility "Energy Australia" [ "utility-bill" ]
          "allianz.com.au",         entry Insurance "Allianz" [ "insurance-policy" ]
          "nrma.com.au",            entry Insurance "NRMA" [ "insurance-policy" ]
          "suncorp.com.au",         entry Insurance "Suncorp" [ "insurance-policy" ] ]

    let private displayNameEntries =
        [ "ray white",          entry PropertyManager "Ray White" [ "agent-statement"; "rental-statement" ]
          "lj hooker",          entry PropertyManager "LJ Hooker" [ "agent-statement" ]
          "barry plant",        entry PropertyManager "Barry Plant" [ "agent-statement" ]
          "holiday hub",        entry PropertyManager "Holiday Hub" [ "agent-statement" ]
          "holidayrentals",     entry PropertyManager "Holiday Hub" [ "agent-statement" ]
          "commbank",           entry Bank "CommBank" [ "bank-statement"; "credit-card-statement" ]
          "commonwealth bank",  entry Bank "CommBank" [ "bank-statement"; "credit-card-statement" ]
          "westpac",            entry Bank "Westpac" [ "bank-statement" ]
          "macquarie",          entry Bank "Macquarie" [ "bank-statement" ] ]

    // ─── Helpers ─────────────────────────────────────────────────────

    let private knownConfidence = 0.8

    let private unknownHint =
        { SenderType = Unknown
          DisplayLabel = ""
          DocumentTypeHints = []
          Confidence = 0.0 }

    let private toHint (e: Entry) =
        { SenderType = e.Type
          DisplayLabel = e.Label
          DocumentTypeHints = e.Hints
          Confidence = knownConfidence }

    let private domainMatches (domain: string) (pattern: string) =
        if pattern.Contains('.') then
            domain = pattern || domain.EndsWith("." + pattern)
        else
            domain.Contains(pattern)

    let private tryMatchDomain (domain: string) =
        domainEntries
        |> List.tryFind (fun (pattern, _) -> domainMatches domain pattern)
        |> Option.map snd

    let private extractDisplayName (sender: string) =
        match sender.IndexOf('<') with
        | lt when lt > 0 -> sender.Substring(0, lt).Trim().Trim('"')
        | _ -> sender.Trim()

    let private tryMatchDisplayName (name: string) =
        let lower = name.ToLowerInvariant()
        displayNameEntries
        |> List.tryFind (fun (pattern, _) -> lower.Contains(pattern))
        |> Option.map snd

    // ─── Public API ──────────────────────────────────────────────────

    let extractDomain (sender: string) : string =
        let trimmed = sender.Trim()
        let emailPart =
            match trimmed.IndexOf('<'), trimmed.IndexOf('>') with
            | lt, gt when lt >= 0 && gt > lt ->
                trimmed.Substring(lt + 1, gt - lt - 1).Trim()
            | _ -> trimmed

        match emailPart.LastIndexOf('@') with
        | -1 -> emailPart.ToLowerInvariant()
        | idx -> emailPart.Substring(idx + 1).ToLowerInvariant()

    let classify (sender: string) : SenderHint =
        if String.IsNullOrWhiteSpace(sender) then
            unknownHint
        else
            sender
            |> extractDomain
            |> tryMatchDomain
            |> Option.orElseWith (fun () -> sender |> extractDisplayName |> tryMatchDisplayName)
            |> Option.map toHint
            |> Option.defaultValue unknownHint

    let formatHint (hint: SenderHint) : string =
        match hint.SenderType with
        | Unknown -> ""
        | _ ->
            let typeLabel = hint.SenderType |> SenderType.toDisplayString
            let hints = hint.DocumentTypeHints |> String.concat " or "
            $"Sender classified as: {typeLabel} ({hint.DisplayLabel}). This document is likely a {hints}."
