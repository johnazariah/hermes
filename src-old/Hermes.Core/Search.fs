namespace Hermes.Core

open System
open System.Threading.Tasks

/// Full-text search over the documents index.
/// Parameterised over Algebra.Database for testability.
[<RequireQualifiedAccess>]
module Search =

    // ─── Result & filter types ───────────────────────────────────────

    /// A single search result with relevance scoring.
    type SearchResult =
        { DocumentId: int64
          SavedPath: string
          OriginalName: string option
          Category: string
          Sender: string option
          Subject: string option
          EmailDate: string option
          ExtractedVendor: string option
          ExtractedAmount: float option
          RelevanceScore: float
          Snippet: string option
          ResultType: string }  // "document" or "email"

    /// Filters for search queries.
    type SearchFilter =
        { Query: string
          Category: string option
          Sender: string option
          DateFrom: string option
          DateTo: string option
          Account: string option
          SourceType: string option
          Limit: int }

    /// Create a default filter with just a query string.
    let defaultFilter (query: string) : SearchFilter =
        { Query = query
          Category = None
          Sender = None
          DateFrom = None
          DateTo = None
          Account = None
          SourceType = None
          Limit = 20 }

    // ─── Query sanitisation ──────────────────────────────────────────

    /// Sanitise a user query for FTS5. Strips special FTS5 syntax characters
    /// and wraps each token in double quotes to prevent syntax errors.
    let sanitiseQuery (raw: string) : string =
        if String.IsNullOrWhiteSpace(raw) then
            ""
        else
            let cleaned =
                raw
                |> String.collect (fun c ->
                    match c with
                    | '"' | '*' | '+' | '-' | '(' | ')' | '{' | '}' | '^' | '~' | ':' -> ""
                    | _ -> string c)

            let tokens =
                cleaned.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

            if tokens.Length = 0 then
                ""
            else
                tokens
                |> Array.map (fun token -> "\"" + token + "\"")
                |> String.concat " "

    // ─── SQL query building ──────────────────────────────────────────

    /// Build the SQL query and parameters for a search.
    let buildQuery (filter: SearchFilter) : string * (string * obj) list =
        let sanitised = sanitiseQuery filter.Query

        if String.IsNullOrWhiteSpace(sanitised) then
            "SELECT 0 WHERE 0", []
        else

        let conditions = ResizeArray<string>()
        let parameters = ResizeArray<string * obj>()

        conditions.Add("documents_fts MATCH @query")
        parameters.Add(("@query", Database.boxVal sanitised))

        match filter.Category with
        | Some cat ->
            conditions.Add("d.category = @category")
            parameters.Add(("@category", Database.boxVal cat))
        | None -> ()

        match filter.Sender with
        | Some s ->
            conditions.Add("d.sender LIKE @sender")
            parameters.Add(("@sender", Database.boxVal ("%" + s + "%")))
        | None -> ()

        match filter.DateFrom with
        | Some df ->
            conditions.Add("d.email_date >= @dateFrom")
            parameters.Add(("@dateFrom", Database.boxVal df))
        | None -> ()

        match filter.DateTo with
        | Some dt ->
            conditions.Add("d.email_date <= @dateTo")
            parameters.Add(("@dateTo", Database.boxVal dt))
        | None -> ()

        match filter.Account with
        | Some acc ->
            conditions.Add("d.account = @account")
            parameters.Add(("@account", Database.boxVal acc))
        | None -> ()

        match filter.SourceType with
        | Some st ->
            conditions.Add("d.source_type = @sourceType")
            parameters.Add(("@sourceType", Database.boxVal st))
        | None -> ()

        let whereClause = conditions |> Seq.toArray |> String.concat " AND "

        let sql =
            "SELECT "
            + "d.id, "
            + "d.saved_path, "
            + "d.original_name, "
            + "d.category, "
            + "d.sender, "
            + "d.subject, "
            + "d.email_date, "
            + "d.extracted_vendor, "
            + "d.extracted_amount, "
            + "bm25(documents_fts) AS rank, "
            + "snippet(documents_fts, 4, '', '', '...', 32) AS snippet "
            + "FROM documents_fts "
            + "JOIN documents d ON d.id = documents_fts.rowid "
            + "WHERE "
            + whereClause
            + " ORDER BY rank "
            + "LIMIT @limit"

        parameters.Add(("@limit", Database.boxVal filter.Limit))

        (sql, parameters |> Seq.toList)

    // ─── Result mapping ──────────────────────────────────────────────

    /// Map a database row to a SearchResult.
    let mapRow (row: Map<string, obj>) : SearchResult =
        let r = Prelude.RowReader(row)
        { DocumentId = r.Int64 "id" 0L
          SavedPath = r.String "saved_path" ""
          OriginalName = r.OptString "original_name"
          Category = r.String "category" ""
          Sender = r.OptString "sender"
          Subject = r.OptString "subject"
          EmailDate = r.OptString "email_date"
          ExtractedVendor = r.OptString "extracted_vendor"
          ExtractedAmount = r.OptFloat "extracted_amount"
          RelevanceScore = r.Float "rank" 0.0
          Snippet = r.OptString "snippet"
          ResultType = "document" }

    /// Map an email row from messages_fts to a SearchResult.
    let private mapEmailRow (row: Map<string, obj>) : SearchResult =
        let r = Prelude.RowReader(row)
        { DocumentId = 0L
          SavedPath = ""
          OriginalName = None
          Category = "email"
          Sender = r.OptString "sender"
          Subject = r.OptString "subject"
          EmailDate = r.OptString "date"
          ExtractedVendor = None
          ExtractedAmount = None
          RelevanceScore = r.Float "rank" 0.0
          Snippet = r.OptString "snippet"
          ResultType = "email" }

    /// Build the SQL for email body search.
    let private buildEmailQuery (filter: SearchFilter) : string * (string * obj) list =
        let sanitised = sanitiseQuery filter.Query

        if String.IsNullOrWhiteSpace(sanitised) then
            "SELECT 0 WHERE 0", []
        else

        let conditions = ResizeArray<string>()
        let parameters = ResizeArray<string * obj>()

        conditions.Add("messages_fts MATCH @query")
        parameters.Add(("@query", Database.boxVal sanitised))

        match filter.Sender with
        | Some s ->
            conditions.Add("m.sender LIKE @sender")
            parameters.Add(("@sender", Database.boxVal ("%" + s + "%")))
        | None -> ()

        match filter.DateFrom with
        | Some df ->
            conditions.Add("m.date >= @dateFrom")
            parameters.Add(("@dateFrom", Database.boxVal df))
        | None -> ()

        match filter.DateTo with
        | Some dt ->
            conditions.Add("m.date <= @dateTo")
            parameters.Add(("@dateTo", Database.boxVal dt))
        | None -> ()

        match filter.Account with
        | Some acc ->
            conditions.Add("m.account = @account")
            parameters.Add(("@account", Database.boxVal acc))
        | None -> ()

        let whereClause = conditions |> Seq.toArray |> String.concat " AND "

        let sql =
            "SELECT "
            + "m.rowid AS id, "
            + "m.sender, "
            + "m.subject, "
            + "m.date, "
            + "m.account, "
            + "bm25(messages_fts) AS rank, "
            + "snippet(messages_fts, 2, '', '', '...', 32) AS snippet "
            + "FROM messages_fts "
            + "JOIN messages m ON m.rowid = messages_fts.rowid "
            + "WHERE "
            + whereClause
            + " ORDER BY rank "
            + "LIMIT @limit"

        parameters.Add(("@limit", Database.boxVal filter.Limit))

        (sql, parameters |> Seq.toList)

    // ─── Execute search ──────────────────────────────────────────────

    /// Execute a full-text search against the documents index.
    let execute (db: Algebra.Database) (filter: SearchFilter) : Task<SearchResult list> =
        task {
            let sql, parameters = buildQuery filter
            let! rows = db.execReader sql parameters
            return rows |> List.map mapRow
        }

    /// Execute a full-text search against the email body index.
    let executeEmailSearch (db: Algebra.Database) (filter: SearchFilter) : Task<SearchResult list> =
        task {
            let sql, parameters = buildEmailQuery filter
            let! rows = db.execReader sql parameters
            return rows |> List.map mapEmailRow
        }

    /// Unified search: query both document and email indexes, merge by relevance.
    let executeUnified (db: Algebra.Database) (filter: SearchFilter) : Task<SearchResult list> =
        task {
            let! docResults = execute db filter
            let! emailResults = executeEmailSearch db filter

            // Merge and sort by relevance (BM25 rank — lower is better)
            let combined =
                (docResults @ emailResults)
                |> List.sortBy (fun r -> r.RelevanceScore)
                |> List.truncate filter.Limit

            return combined
        }
