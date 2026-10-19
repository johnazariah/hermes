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
        KeywordSql.sanitiseQuery raw

    // ─── SQL query building ──────────────────────────────────────────

    let internal criteria (filter: SearchFilter) : KeywordSql.Criteria =
        { Query = filter.Query
          Category = filter.Category; Sender = filter.Sender
          DateFrom = filter.DateFrom; DateTo = filter.DateTo
          Account = filter.Account; SourceType = filter.SourceType
          Limit = filter.Limit }

    /// Build the SQL query and parameters for a search.
    let buildQuery (filter: SearchFilter) : string * (string * obj) list =
        let query = filter |> criteria |> KeywordSql.metadata
        query.Sql, query.Parameters

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
          ResultType =
              if r.String "source_type" "" = "email_body" then "email"
              else "document" }

    /// Map an email row from messages_fts to a SearchResult.
    let private mapEmailRow (row: Map<string, obj>) : SearchResult =
        let r = Prelude.RowReader(row)
        { DocumentId = r.Int64 "id" 0L
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
        let query = filter |> criteria |> KeywordSql.messages
        query.Sql, query.Parameters

    // ─── Execute search ──────────────────────────────────────────────

    let private identity (result: SearchResult) =
        result.ResultType, result.DocumentId

    /// Keep the best rank, but prefer a matching file's snippet over metadata.
    let private merge limit (content: SearchResult list) (metadata: SearchResult list) =
        let snippets = content |> List.map (fun hit -> identity hit, hit.Snippet) |> Map.ofList
        (content @ metadata)
        |> List.groupBy identity
        |> List.map (fun (key, hits) ->
            let best = hits |> List.minBy (fun hit -> hit.RelevanceScore)
            let snippet = snippets |> Map.tryFind key |> Option.flatten |> Option.orElse best.Snippet
            { best with Snippet = snippet })
        |> List.sortBy (fun hit -> hit.RelevanceScore, hit.DocumentId, hit.ResultType)
        |> List.truncate (max 0 limit)

    let private executeDocuments (db: Algebra.Database) (filter: SearchFilter) offset =
        task {
            let! extractionExists = db.tableExists "extraction"
            let query = KeywordSql.documentPage extractionExists offset (criteria filter)
            let! rows = db.execReader query.Sql query.Parameters
            return rows |> List.map mapRow
        }

    /// Page stable document identities after metadata/content ranking and deduplication.
    let executePage (db: Algebra.Database) (filter: SearchFilter) (offset: int) =
        task {
            if offset < 0 || filter.Limit <= 0 then
                return Error "Offset must be nonnegative and limit must be positive"
            elif String.IsNullOrWhiteSpace(sanitiseQuery filter.Query) then
                return Ok []
            else
                let! hits = executeDocuments db filter offset
                return Ok hits
        }

    /// Use the same ranking and preferred-snippet rule as paged document search.
    let execute (db: Algebra.Database) (filter: SearchFilter) : Task<SearchResult list> =
        task {
            if filter.Limit <= 0 || String.IsNullOrWhiteSpace(sanitiseQuery filter.Query) then
                return []
            else
                return! executeDocuments db filter 0
        }

    let private executeMessages (db: Algebra.Database) (filter: SearchFilter) =
        task {
            let sql, parameters = buildEmailQuery filter
            let! rows = db.execReader sql parameters
            return rows |> List.map mapEmailRow
        }

    /// Includes immutable file-backed bodies and legacy metadata-only messages.
    let executeEmailSearch (db: Algebra.Database) (filter: SearchFilter) : Task<SearchResult list> =
        task {
            let! bodies =
                if filter.SourceType |> Option.exists ((<>) "email_body") then
                    Task.FromResult []
                else execute db { filter with SourceType = Some "email_body" }
            let! messages = executeMessages db filter
            return merge filter.Limit bodies messages
        }

    /// Unified search: query both document and email indexes, merge by relevance.
    let executeUnified (db: Algebra.Database) (filter: SearchFilter) : Task<SearchResult list> =
        task {
            let! docResults = execute db filter
            let! emailResults = executeMessages db filter
            return merge filter.Limit docResults emailResults
        }
