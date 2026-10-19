namespace Hermes.Core

open System

/// Parameterised keyword SQL shared by metadata and file-content searches.
[<RequireQualifiedAccess>]
module internal KeywordSql =

    type Criteria =
        { Query: string
          Category: string option
          Sender: string option
          DateFrom: string option
          DateTo: string option
          Account: string option
          SourceType: string option
          Limit: int }

    type Query =
        { Sql: string
          Parameters: (string * obj) list }

    type private Predicate =
        { Sql: string
          Parameter: string * obj }

    let sanitiseQuery (raw: string) =
        if String.IsNullOrWhiteSpace raw then ""
        else
            raw
            |> String.collect (function
                | '"' | '*' | '+' | '-' | '(' | ')' | '{' | '}' | '^' | '~' | ':' -> ""
                | character -> string character)
            |> fun value -> value.Split([| ' '; '\t'; '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun token -> "\"" + token + "\"")
            |> String.concat " "

    let private optional name sql value =
        value
        |> Option.map (fun text ->
            { Sql = sql; Parameter = name, Database.boxVal text })

    let private predicates alias dateColumn (filter: Criteria) =
        [ optional "@sender" $"{alias}.sender LIKE @sender"
              (filter.Sender |> Option.map (fun value -> "%" + value + "%"))
          optional "@dateFrom" $"{alias}.{dateColumn} >= @dateFrom" filter.DateFrom
          optional "@dateTo" $"{alias}.{dateColumn} <= @dateTo" filter.DateTo
          optional "@account" $"{alias}.account = @account" filter.Account ]
        |> List.choose id

    let private documentPredicates filter =
        predicates "d" "email_date" filter
        @ ([ optional "@category" "d.category = @category" filter.Category
             optional "@sourceType" "d.source_type = @sourceType" filter.SourceType ]
           |> List.choose id)

    let private assemble projection source required filters (criteria: Criteria) =
        let query = sanitiseQuery criteria.Query
        if String.IsNullOrWhiteSpace query || criteria.Limit <= 0 then
            { Sql = "SELECT 0 WHERE 0"; Parameters = [] }
        else
            let optionalSql = filters |> List.map (fun value -> value.Sql)
            let where = required :: optionalSql |> String.concat " AND "
            { Sql = $"SELECT {projection} FROM {source} WHERE {where} ORDER BY rank, id LIMIT @limit"
              Parameters =
                [ "@query", Database.boxVal query; "@limit", Database.boxVal criteria.Limit ]
                @ (filters |> List.map (fun value -> value.Parameter)) }

    let private documentProjection =
        """d.id, d.saved_path, d.original_name, d.category, d.sender, d.subject,
           d.email_date, d.extracted_vendor, d.extracted_amount, d.source_type"""

    let metadata filter =
        assemble
            (documentProjection
             + ", bm25(documents_fts) AS rank, snippet(documents_fts, -1, '', '', '...', 32) AS snippet")
            "documents_fts JOIN documents d ON d.id = documents_fts.rowid"
            "documents_fts MATCH @query"
            (documentPredicates filter) filter

    let private contentCurrent extractionExists =
        let extraction =
            if extractionExists then
                """document_content_fts.kind = 'extraction' AND d.source_type <> 'email_body'
                   AND EXISTS(SELECT 1 FROM extraction e WHERE e.document_id = d.id)
                   AND EXISTS(SELECT 1 FROM stage_completions s
                              WHERE s.document_id = d.id AND s.stage_name = 'extract')"""
            else "0"
        $"""((document_content_fts.kind = 'source' AND d.source_type = 'email_body')
             OR ({extraction}))"""

    let content extractionExists filter =
        assemble
            (documentProjection
             + ", bm25(document_content_fts) AS rank, snippet(document_content_fts, 0, '', '', '...', 32) AS snippet")
            "document_content_fts JOIN documents d ON d.id = document_content_fts.rowid"
            ("document_content_fts MATCH @query AND " + contentCurrent extractionExists)
            (documentPredicates filter) filter

    let private preferredSnippet extractionExists =
        $"""COALESCE(
              (SELECT snippet(document_content_fts, 0, '', '', '...', 32)
               FROM document_content_fts
               WHERE document_content_fts.rowid = r.id
                 AND document_content_fts MATCH @query
                 AND {contentCurrent extractionExists}),
              r.snippet)"""

    let private documentPageSql extractionExists (metadataSql: string) (contentSql: string) =
        $"""WITH metadata_hits AS MATERIALIZED ({metadataSql}),
                 content_hits AS MATERIALIZED ({contentSql}),
                 matches AS (
                   SELECT id, rank, snippet, 0 AS content_match FROM metadata_hits
                   UNION ALL
                   SELECT id, rank, snippet, 1 AS content_match FROM content_hits
                 ),
                 ranked AS (
                   SELECT id, MIN(rank) AS rank,
                          COALESCE(MAX(CASE WHEN content_match = 1 THEN snippet END),
                                   MAX(CASE WHEN content_match = 0 THEN snippet END)) AS snippet
                   FROM matches GROUP BY id
                 ),
                 selected AS MATERIALIZED (
                   SELECT * FROM ranked ORDER BY rank, id LIMIT @pageLimit OFFSET @offset
                 )
            SELECT {documentProjection}, r.rank, {preferredSnippet extractionExists} AS snippet
            FROM selected r JOIN documents d ON d.id = r.id
            ORDER BY r.rank, d.id"""

    /// Bounded prefixes determine page identities and ranks. Selected identities
    /// resolve current matching content snippets independently of those prefixes.
    let documentPage extractionExists offset (filter: Criteria) : Query =
        let metadataQuery = metadata filter
        let contentQuery = content extractionExists filter
        { Sql = documentPageSql extractionExists metadataQuery.Sql contentQuery.Sql
          Parameters =
            (metadataQuery.Parameters |> List.filter (fun (name, _) -> name <> "@limit"))
            @ [ "@limit", Database.boxVal (int64 offset + int64 filter.Limit)
                "@pageLimit", Database.boxVal filter.Limit
                "@offset", Database.boxVal offset ] }

    let messages (filter: Criteria) =
        let incompatibleCategory = filter.Category |> Option.exists ((<>) "email")
        let incompatibleSource = filter.SourceType |> Option.exists ((<>) "email_body")
        let eligible = if incompatibleCategory || incompatibleSource then "0" else "1"
        let required =
            $"""messages_fts MATCH @query AND {eligible}
                AND NOT EXISTS(SELECT 1 FROM documents d
                               WHERE d.source_type = 'email_body'
                                 AND d.account = m.account AND d.gmail_id = m.gmail_id)"""
        assemble
            """-m.rowid AS id, m.sender, m.subject, m.date, m.account,
               bm25(messages_fts) AS rank,
               snippet(messages_fts, -1, '', '', '...', 32) AS snippet"""
            "messages_fts JOIN messages m ON m.rowid = messages_fts.rowid"
            required (predicates "m" "date" filter) filter
