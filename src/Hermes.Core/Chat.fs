namespace Hermes.Core

open System
open System.Net.Http
open System.Text
open System.Text.Json
open System.Threading.Tasks

/// Chat interface: query Hermes index, optionally enhance with Ollama LLM.
[<RequireQualifiedAccess>]
module Chat =

    /// A single chat response with optional AI summary.
    type ChatResponse =
        { Query: string
          Results: Search.SearchResult list
          AiSummary: string option }

    /// Format search results as context for the LLM prompt.
    let private formatResultsForPrompt (results: Search.SearchResult list) : string =
        results
        |> List.mapi (fun i r ->
            let date = r.EmailDate |> Option.defaultValue "unknown date"
            let amount = r.ExtractedAmount |> Option.map (sprintf "$%.2f") |> Option.defaultValue ""
            let snippet = r.Snippet |> Option.defaultValue ""
            let name = r.OriginalName |> Option.defaultValue "unknown"
            $"{i + 1}. [{r.Category}] {name} ({date}) {amount}\n   {snippet}")
        |> String.concat "\n"

    /// Build the LLM prompt from query + search results.
    let private buildPrompt (query: string) (context: string) : string =
        $"""You are Hermes, a personal document assistant. Based on the following documents from the user's archive, answer their question concisely.

Documents found:
{context}

Question: {query}

Answer briefly and specifically, referencing document names and dates where relevant."""

    /// Ask Ollama to summarise search results in natural language.
    let askOllama (ollamaUrl: string) (model: string) (query: string) (results: Search.SearchResult list) : Task<Result<string, string>> =
        task {
            if results.IsEmpty then
                return Ok "No documents found matching your query."
            else
                try
                    let context = formatResultsForPrompt results
                    let prompt = buildPrompt query context
                    use client = new HttpClient(Timeout = TimeSpan.FromSeconds(60.0))
                    let payload = JsonSerializer.Serialize({| model = model; prompt = prompt; stream = false |})
                    let content = new StringContent(payload, Encoding.UTF8, "application/json")
                    let! response = client.PostAsync($"{ollamaUrl.TrimEnd('/')}/api/generate", content)
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
        }

    /// Run a chat query: search the index, optionally enhance with LLM.
    let query
        (db: Algebra.Database)
        (ollamaUrl: string)
        (model: string)
        (useAi: bool)
        (queryText: string)
        : Task<ChatResponse> =
        task {
            let filter : Search.SearchFilter =
                { Query = queryText
                  Category = None; Sender = None; DateFrom = None
                  DateTo = None; Account = None; SourceType = None
                  Limit = 10 }

            let! results = Search.executeUnified db filter

            let! aiSummary =
                if useAi && not results.IsEmpty then
                    task {
                        let! result = askOllama ollamaUrl model queryText results
                        return
                            match result with
                            | Ok s -> Some s
                            | Error e -> Some $"(AI unavailable: {e})"
                    }
                else
                    task { return None }

            return { Query = queryText; Results = results; AiSummary = aiSummary }
        }
