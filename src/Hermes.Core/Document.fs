namespace Hermes.Core

#nowarn "3261"

open System
open System.Threading.Tasks

/// Document as a property bag — the unit of work flowing through the pipeline.
/// Typed access via decode<'T>, untyped storage as Map<string, obj>.
/// SQL mapping via toParams / fromRow at the persistence boundary.
[<RequireQualifiedAccess>]
module Document =

    /// A document is a property bag. Keys are column names. Values are typed at access time.
    type T = Map<string, obj>

    /// Try to decode a typed value from the document.
    let decode<'T> (key: string) (doc: T) : 'T option =
        doc
        |> Map.tryFind key
        |> Option.bind (fun v ->
            match v with
            | :? DBNull -> None
            | null -> None
            | :? 'T as typed -> Some typed
            | _ ->
                // Handle common SQLite type conversions
                try
                    Some (Convert.ChangeType(v, typeof<'T>) :?> 'T)
                with _ -> None)

    /// Encode a value into the document.
    let encode (key: string) (value: obj) (doc: T) : T =
        doc |> Map.add key value

    /// Check if a key exists and is non-null.
    let hasKey (key: string) (doc: T) : bool =
        doc
        |> Map.tryFind key
        |> Option.exists (fun v ->
            match v with
            | :? DBNull | null -> false
            | _ -> true)

    /// Get the document ID (always present after INSERT).
    let id (doc: T) : int64 =
        doc |> decode<int64> "id" |> Option.defaultValue 0L

    /// Get the current pipeline stage.
    let stage (doc: T) : string =
        doc |> decode<string> "stage" |> Option.defaultValue "received"

    // ─── SQL mapping ─────────────────────────────────────────────

    /// All document table columns in schema order.
    let private columns =
        [| "id"; "stage"; "source_type"; "gmail_id"; "thread_id"; "account"
           "sender"; "subject"; "email_date"; "original_name"; "saved_path"
           "category"; "mime_type"; "size_bytes"; "sha256"; "source_path"
           "extracted_text"; "extracted_markdown"; "extracted_date"
           "extracted_amount"; "extracted_vendor"; "extracted_abn"
           "ocr_confidence"; "extraction_method"; "extraction_confidence"
           "classification_tier"; "classification_confidence"
           "extracted_at"; "embedded_at"; "chunk_count"; "starred"
           "ingested_at" |]

    /// Convert a DB row (from execReader) to a Document.
    let fromRow (row: Map<string, obj>) : T = row

    /// Convert a Document to SQL parameters for a full-row UPDATE.
    /// Excludes 'id' (used in WHERE clause separately).
    let toUpdateParams (doc: T) : (string * obj) list =
        columns
        |> Array.filter (fun c -> c <> "id")
        |> Array.map (fun col ->
            let value =
                doc
                |> Map.tryFind col
                |> Option.defaultValue (box DBNull.Value)
            ($"@{col}", value))
        |> Array.toList

    /// Build the UPDATE SQL for a full-row write-aside.
    let updateSql : string =
        let setClauses =
            columns
            |> Array.filter (fun c -> c <> "id")
            |> Array.map (fun c -> $"{c} = @{c}")
            |> String.concat ", "
        $"UPDATE documents SET {setClauses} WHERE id = @id"

    /// Write a document back to the database (full-row write-aside).
    let persist (db: Algebra.Database) (doc: T) : Task<unit> =
        task {
            let docId = id doc
            let ps = ("@id", box docId) :: toUpdateParams doc
            let! _ = db.execNonQuery updateSql ps
            ()
        }

    /// Hydrate all incomplete documents from the database.
    let hydrate (db: Algebra.Database) : Task<T list> =
        task {
            let! rows =
                db.execReader
                    "SELECT * FROM documents WHERE stage NOT IN ('embedded', 'failed')"
                    []
            return rows |> List.map fromRow
        }

    /// Query pipeline stage counts for the dashboard.
    let stageCounts (db: Algebra.Database) : Task<Map<string, int64>> =
        task {
            let! rows =
                db.execReader
                    "SELECT stage, COUNT(*) as cnt FROM documents GROUP BY stage"
                    []
            return
                rows
                |> List.choose (fun row ->
                    let s = row |> decode<string> "stage"
                    let c = row |> decode<int64> "cnt"
                    match s, c with
                    | Some stage, Some count -> Some (stage, count)
                    | _ -> None)
                |> Map.ofList
        }
