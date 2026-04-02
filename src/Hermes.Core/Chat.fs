namespace Hermes.Core

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Chat interface: query Hermes index, optionally enhance with LLM.
/// The LLM backend is abstracted via Algebra.ChatProvider (Tagless-Final).
[<RequireQualifiedAccess>]
module Chat =

    /// A single chat response with optional AI summary.
    type ChatResponse =
        { Query: string
          Results: Search.SearchResult list
          AiSummary: string option }

    // ─── Shared prompt building ──────────────────────────────────────

    /// Format search results as context for the LLM prompt.
    let formatResultsForPrompt (results: Search.SearchResult list) : string =
        results
        |> List.truncate 10
        |> List.mapi (fun i r ->
            let date = r.EmailDate |> Option.defaultValue "unknown date"
            let amount = r.ExtractedAmount |> Option.map (sprintf "$%.2f") |> Option.defaultValue ""
            let vendor = r.ExtractedVendor |> Option.defaultValue ""
            let snippet = r.Snippet |> Option.defaultValue ""
            let name = r.OriginalName |> Option.defaultValue "unknown"
            let kind = r.ResultType
            let sender = r.Sender |> Option.defaultValue ""
            let subject = r.Subject |> Option.defaultValue ""
            let lines = ResizeArray<string>()
            lines.Add($"{i + 1}. [{r.Category}/{kind}] {name}")
            if sender <> "" then lines.Add($"   From: {sender}")
            if subject <> "" then lines.Add($"   Subject: {subject}")
            if date <> "unknown date" then lines.Add($"   Date: {date}")
            if vendor <> "" then lines.Add($"   Vendor: {vendor}")
            if amount <> "" then lines.Add($"   Amount: {amount}")
            if snippet <> "" then lines.Add($"   Content: {snippet}")
            lines |> String.concat "\n")
        |> String.concat "\n\n"

    /// The system prompt sent to the LLM.
    let systemPrompt =
        """You are Hermes, a personal document intelligence assistant.
You have access to the user's indexed archive of emails, invoices, bank statements, receipts, and other documents.
Answer questions by referencing specific documents with their names, dates, amounts, and vendors.
Be concise but specific. If multiple documents are relevant, mention the most important ones.
If the documents don't contain enough information to answer, say so honestly."""

    /// Build the user prompt from query + search results.
    let buildUserPrompt (query: string) (context: string) : string =
        $"""Documents found:
{context}

Question: {query}

Answer briefly and specifically."""

    // ─── Provider factories ──────────────────────────────────────────

    /// Create a ChatProvider backed by Ollama's /api/generate endpoint.
    let ollamaProvider (client: HttpClient) (baseUrl: string) (model: string) : Algebra.ChatProvider =
        { complete = fun systemMsg userMsg ->
            task {
                try
                    let prompt = $"{systemMsg}\n\n{userMsg}"
                    let payload = JsonSerializer.Serialize({| model = model; prompt = prompt; stream = false |})
                    let content = new StringContent(payload, Encoding.UTF8, "application/json")
                    let! response = client.PostAsync($"{baseUrl.TrimEnd('/')}/api/generate", content)
                    let! body = response.Content.ReadAsStringAsync()

                    if not response.IsSuccessStatusCode then
                        return Error $"Ollama returned {response.StatusCode}"
                    else
                        let doc = JsonDocument.Parse(body)
                        let answer = doc.RootElement.GetProperty("response").GetString() |> Option.ofObj
                        match answer with
                        | None -> return Ok "No answer generated."
                        | Some s when String.IsNullOrWhiteSpace(s) -> return Ok "No answer generated."
                        | Some s -> return Ok s
                with ex ->
                    return Error $"Ollama error: {ex.Message}"
            } }

    /// Create a ChatProvider backed by Azure OpenAI's Chat Completions API.
    let azureOpenAIProvider (client: HttpClient) (config: Domain.AzureOpenAIConfig) : Algebra.ChatProvider =
        { complete = fun systemMsg userMsg ->
            task {
                try
                    let endpoint = config.Endpoint.TrimEnd('/')
                    let url = $"{endpoint}/openai/deployments/{config.DeploymentName}/chat/completions?api-version=2024-06-01"

                    let payload =
                        JsonSerializer.Serialize(
                            {| messages =
                                [| {| role = "system"; content = systemMsg |}
                                   {| role = "user"; content = userMsg |} |]
                               max_tokens = config.MaxTokens
                               temperature = 0.3 |})

                    let content = new StringContent(payload, Encoding.UTF8, "application/json")
                    client.DefaultRequestHeaders.Add("api-key", config.ApiKey)

                    let! response = client.PostAsync(url, content)
                    let! body = response.Content.ReadAsStringAsync()

                    if not response.IsSuccessStatusCode then
                        return Error $"Azure OpenAI returned {response.StatusCode}: {body}"
                    else
                        let doc = JsonDocument.Parse(body)
                        let choices = doc.RootElement.GetProperty("choices")

                        if choices.GetArrayLength() = 0 then
                            return Ok "No answer generated."
                        else
                            let messageContent =
                                choices.[0]
                                    .GetProperty("message")
                                    .GetProperty("content")
                                    .GetString()
                                |> Option.ofObj

                            match messageContent with
                            | None -> return Ok "No answer generated."
                            | Some s when String.IsNullOrWhiteSpace(s) -> return Ok "No answer generated."
                            | Some s -> return Ok s
                with ex ->
                    return Error $"Azure OpenAI error: {ex.Message}"
            } }

    /// Create the appropriate ChatProvider from config.
    let providerFromConfig (client: HttpClient) (chatConfig: Domain.ChatConfig) (ollamaUrl: string) (ollamaModel: string) : Algebra.ChatProvider =
        match chatConfig.Provider with
        | Domain.ChatProviderKind.AzureOpenAI
            when not (String.IsNullOrWhiteSpace(chatConfig.AzureOpenAI.Endpoint))
              && not (String.IsNullOrWhiteSpace(chatConfig.AzureOpenAI.ApiKey)) ->
            azureOpenAIProvider client chatConfig.AzureOpenAI
        | _ ->
            ollamaProvider client ollamaUrl ollamaModel

    // ─── Public API ──────────────────────────────────────────────────

    /// Run a chat query: search the index, optionally enhance with LLM via the provided ChatProvider.
    let query
        (db: Algebra.Database)
        (chat: Algebra.ChatProvider)
        (useAi: bool)
        (queryText: string)
        : Task<ChatResponse> =
        task {
            let filter : Search.SearchFilter =
                { Query = queryText
                  Category = None; Sender = None; DateFrom = None
                  DateTo = None; Account = None; SourceType = None
                  Limit = 20 }

            let! results = Search.executeUnified db filter

            let! aiSummary =
                if useAi && not results.IsEmpty then
                    task {
                        let context = formatResultsForPrompt results
                        let userMsg = buildUserPrompt queryText context
                        let! result = chat.complete systemPrompt userMsg
                        return
                            match result with
                            | Ok s -> Some s
                            | Error e -> Some $"(AI unavailable: {e})"
                    }
                else
                    task { return None }

            return { Query = queryText; Results = results; AiSummary = aiSummary }
        }
