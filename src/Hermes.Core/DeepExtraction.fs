namespace Hermes.Core

#nowarn "3261"

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks

/// On-demand deep extraction: type-specific LLM prompts for rich field extraction.
/// Pass 2 of the two-pass comprehension system.
[<RequireQualifiedAccess>]
module DeepExtraction =

    // ─── Types ───────────────────────────────────────────────────────

    /// Metadata stored alongside deep extraction results for cache invalidation.
    type ExtractionMetadata =
        { GeneratedAt: string
          Provider: string
          Model: string
          PromptVersion: string
          SourceHash: string
          SchemaVersion: string }

    /// Result of a deep extraction, ready to persist.
    type DeepResult =
        { Metadata: ExtractionMetadata
          Fields: string }

    // ─── Prompt registry ─────────────────────────────────────────────

    let [<Literal>] private MaxDeepChars = 12000

    /// Truncate text for deep extraction (larger limit than Pass 1).
    let private truncate (maxChars: int) (text: string) =
        if text.Length <= maxChars then text
        else text.Substring(0, maxChars) + "\n[... truncated]"

    /// Map document_type from Pass 1 to deep prompt filename.
    let promptFileForType (documentType: string) : string option =
        match documentType.Trim().ToLowerInvariant() with
        | "payslip" | "payroll-statement" | "paycheck" -> Some "payslip.md"
        | "agent-statement" | "rental-statement" | "property-statement" -> Some "agent-statement.md"
        | "bank-statement" | "credit-card-statement" -> Some "bank-statement.md"
        | _ -> None

    /// Load and validate all deep prompts from a directory at startup.
    let loadPromptRegistry
        (fs: Algebra.FileSystem)
        (promptDir: string)
        : Task<Map<string, PromptLoader.ParsedPrompt>> =
        task {
            let promptFiles = [ "payslip.md"; "agent-statement.md"; "bank-statement.md" ]
            let mutable registry = Map.empty

            for file in promptFiles do
                let path = Path.Combine(promptDir, file)
                if fs.fileExists path then
                    let! content = fs.readAllText path
                    match PromptLoader.parse content with
                    | Ok parsed ->
                        let key = file.Replace(".md", "")
                        registry <- registry |> Map.add key parsed
                    | Error _ -> ()

            return registry
        }

    // ─── Hash computation ────────────────────────────────────────────

    let computeHash (text: string) : string =
        use sha = Security.Cryptography.SHA256.Create()
        let bytes = Text.Encoding.UTF8.GetBytes(text)
        let hash = sha.ComputeHash(bytes)
        Convert.ToHexStringLower(hash).Substring(0, 16)

    // ─── Core extraction ─────────────────────────────────────────────

    /// Extract the document_type from existing comprehension JSON.
    let getDocumentType (comprehensionJson: string) : string option =
        try
            use doc = JsonDocument.Parse(comprehensionJson)
            match doc.RootElement.TryGetProperty("document_type") with
            | true, v when v.ValueKind = JsonValueKind.String -> v.GetString() |> Option.ofObj
            | _ -> None
        with _ -> None

    /// Check if deep extraction results already exist and are fresh.
    let hasValidDeepExtraction (comprehensionJson: string) (sourceHash: string) : bool =
        try
            use doc = JsonDocument.Parse(comprehensionJson)
            match doc.RootElement.TryGetProperty("deep_extraction") with
            | true, de ->
                match de.TryGetProperty("metadata") with
                | true, meta ->
                    match meta.TryGetProperty("source_hash") with
                    | true, sh -> sh.GetString() = sourceHash
                    | _ -> false
                | _ -> false
            | _ -> false
        with _ -> false

    /// Run deep extraction on a document.
    let extract
        (chat: Algebra.ChatProvider)
        (registry: Map<string, PromptLoader.ParsedPrompt>)
        (provider: string)
        (model: string)
        (documentType: string)
        (extractedText: string)
        (context: string)
        : Task<Result<DeepResult, string>> =
        task {
            match promptFileForType documentType with
            | None -> return Error $"No deep extraction prompt for document type: {documentType}"
            | Some promptFile ->
                let key = promptFile.Replace(".md", "")
                match registry |> Map.tryFind key with
                | None -> return Error $"Deep extraction prompt not loaded: {key}"
                | Some prompt ->
                    let truncated = extractedText |> truncate MaxDeepChars
                    let userPrompt =
                        prompt.UserTemplate
                            .Replace("{{document_text}}", truncated)
                            .Replace("{{context}}", context)

                    let! result = chat.complete prompt.System userPrompt
                    match result with
                    | Error e -> return Error $"LLM call failed: {e}"
                    | Ok response ->
                        let stripped = ComprehensionSchema.stripCodeFences response
                        // Validate it's parseable JSON
                        try
                            use _ = JsonDocument.Parse(stripped)
                            let sourceHash = computeHash extractedText
                            return Ok
                                { Metadata =
                                    { GeneratedAt = DateTimeOffset.UtcNow.ToString("o")
                                      Provider = provider
                                      Model = model
                                      PromptVersion = computeHash prompt.System
                                      SourceHash = sourceHash
                                      SchemaVersion = "deep-v1" }
                                  Fields = stripped }
                        with ex ->
                            return Error $"LLM returned invalid JSON: {ex.Message}"
        }

    /// Merge deep extraction result into existing comprehension JSON.
    let mergeIntoComprehension (existingJson: string) (deep: DeepResult) : Result<string, string> =
        try
            let existing = JsonNode.Parse(existingJson)
            let deepObj = JsonObject()
            let metaObj = JsonObject()
            metaObj["generated_at"] <- JsonValue.Create(deep.Metadata.GeneratedAt)
            metaObj["provider"] <- JsonValue.Create(deep.Metadata.Provider)
            metaObj["model"] <- JsonValue.Create(deep.Metadata.Model)
            metaObj["prompt_version"] <- JsonValue.Create(deep.Metadata.PromptVersion)
            metaObj["source_hash"] <- JsonValue.Create(deep.Metadata.SourceHash)
            metaObj["schema_version"] <- JsonValue.Create(deep.Metadata.SchemaVersion)
            deepObj["metadata"] <- metaObj
            deepObj["fields"] <- JsonNode.Parse(deep.Fields)
            existing["deep_extraction"] <- deepObj
            Ok (existing.ToJsonString(JsonSerializerOptions(WriteIndented = false)))
        with ex ->
            Error $"Failed to merge deep extraction: {ex.Message}"
