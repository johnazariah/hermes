module Hermes.Tests.McpLegacyReclassificationTests

#nowarn "3261"
#nowarn "3264"

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Xunit
open Hermes.Core

// ─── DB / archive seeding (mirrors LegacyReclassificationTests.fs) ────

let private hashText (value: string) =
    let bytes = Encoding.UTF8.GetBytes(value)
    let hash: byte array = SHA256.HashData(bytes)
    Convert.ToHexString(hash).ToLowerInvariant()

let private insertLegacy (db: Algebra.Database) (savedPath: string) (sha256: string) =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                   (source_type, saved_path, category, sha256)
                   VALUES ('manual_drop', @path, 'receipts', @sha)"""
                [ "@path", Database.boxVal savedPath
                  "@sha", Database.boxVal sha256 ]
        ()
    }

let private archiveFile (fs: TestHelpers.MemFs) (path: string) (content: string) =
    fs.Fs.createDirectory "/archive"

    match Path.GetDirectoryName(path) with
    | null -> failwith $"Expected parent directory for archive path: {path}"
    | directory -> fs.Fs.createDirectory directory

    fs.Put path content

let private insertDocumentRange (db: Algebra.Database) firstId lastId =
    task {
        let! _ =
            db.execNonQuery
                """WITH RECURSIVE ids(value) AS (
                       SELECT @first
                       UNION ALL
                       SELECT value + 1 FROM ids WHERE value < @last
                   )
                   INSERT INTO documents
                       (id, source_type, saved_path, category, sha256)
                   SELECT
                       value,
                       'manual_drop',
                       printf('missing/%06d.pdf', value),
                       'receipts',
                       printf('sha-%d', value)
                   FROM ids"""
                [ "@first", Database.boxVal firstId
                  "@last", Database.boxVal lastId ]

        return ()
    }

// ─── Cursor construction helpers (codec unit tests) ───────────────────

let private cursorBounds maxDocuments maxFiles =
    LegacyReclassification.createBounds maxDocuments maxFiles
    |> Result.defaultWith failwith

let private freshCursor () : LegacyReclassification.RunCursor =
    LegacyReclassification.createRunCursor "/archive" (cursorBounds 20 100) 0L
    |> Result.defaultWith failwith

let private advancedCursor () : LegacyReclassification.RunCursor =
    let pathA: LegacyReclassification.CandidatePath =
        { OwnershipKey = "KEY-A"; SavedPath = "receipts/a.pdf" }

    let pathB: LegacyReclassification.CandidatePath =
        { OwnershipKey = "KEY-B"; SavedPath = "receipts/b.pdf" }

    let candidate: LegacyReclassification.TargetCandidates =
        { Sha256 = "abc123"; Paths = [ pathA; pathB ] }

    { freshCursor () with
        Phase = LegacyReclassification.DocumentScan
        Archive = LegacyReclassification.ArchiveCompleted
        Documents = LegacyReclassification.AfterDocument 42L
        Candidates = [ candidate ] }

// ─── Base64Url + JSON tamper helpers (test-owned, independent of the
//     production module's private helpers — deliberately black-box) ───

let private base64UrlEncode (text: string) : string =
    Convert.ToBase64String(Encoding.UTF8.GetBytes text).TrimEnd('=').Replace('+', '-').Replace('/', '_')

let private base64UrlDecode (token: string) : string =
    let remainder = token.Length % 4
    let padded = if remainder = 0 then token else token + String('=', 4 - remainder)
    Encoding.UTF8.GetString(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')))

let private tamperField (token: string) (key: string) (value: JsonNode) : string =
    let parsed: JsonNode | null = JsonNode.Parse(base64UrlDecode token)

    match parsed with
    | null -> failwith "Expected a JSON cursor payload"
    | node ->
        match node with
        | :? JsonObject as obj ->
            obj.[key] <- value
            base64UrlEncode (obj.ToJsonString())
        | _ -> failwith "Expected a JSON object cursor payload"

// ─── JSON-RPC helpers (mirrors McpContactTests.fs) ────────────────────

let private callTool (db: Algebra.Database) (m: TestHelpers.MemFs) toolName argsJson =
    task {
        let json =
            $"""{{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{{"name":"{toolName}","arguments":{argsJson}}}}}"""

        return! McpServer.processMessage db m.Fs TestHelpers.silentLogger TestHelpers.defaultClock "/archive" None json
    }

let private parseResult (response: string) : JsonElement =
    let doc = JsonDocument.Parse(response)
    let content = doc.RootElement.GetProperty("result").GetProperty("content")
    let text = content.[0].GetProperty("text").GetString()
    JsonDocument.Parse(text).RootElement

let private cursorField (token: string option) : string =
    match token with
    | Some t -> ",\"cursor\":\"" + t + "\""
    | None -> ""

let private pageArgs (maxDocuments: int) (maxFiles: int) (apply: bool) (cursorToken: string option) : string =
    let applyText = if apply then "true" else "false"

    "{\"max_documents\":" + string maxDocuments
    + ",\"max_files\":" + string maxFiles
    + ",\"apply\":" + applyText
    + cursorField cursorToken
    + "}"

let private stabilityOf (result: JsonElement) : string =
    result.GetProperty("stability").GetString() |> Option.ofObj |> Option.defaultValue ""

let private cursorOf (result: JsonElement) : string option =
    match result.TryGetProperty("cursor") with
    | true, value -> value.GetString() |> Option.ofObj
    | false, _ -> None

let private hasCursor (result: JsonElement) : bool =
    match result.TryGetProperty("cursor") with
    | true, _ -> true
    | false, _ -> false

let private findingDocumentIds (result: JsonElement) : int64 list =
    let findings = result.GetProperty("findings")
    [ for i in 0 .. findings.GetArrayLength() - 1 -> findings.[i].GetProperty("document_id").GetInt64() ]

// ─── Codec: encode/decode round-trip and rejection ────────────────────

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Encode then decode round-trips a fresh cursor`` () =
    let cursor = freshCursor ()
    let token = McpLegacyReclassification.encodeCursor cursor

    match McpLegacyReclassification.decodeCursor token with
    | Ok decoded -> Assert.Equal<LegacyReclassification.RunCursor>(cursor, decoded)
    | Error message -> failwith $"Expected successful decode, got: {message}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Encode then decode round-trips a cursor with candidates and continuations`` () =
    let cursor = advancedCursor ()
    let token = McpLegacyReclassification.encodeCursor cursor

    match McpLegacyReclassification.decodeCursor token with
    | Ok decoded -> Assert.Equal<LegacyReclassification.RunCursor>(cursor, decoded)
    | Error message -> failwith $"Expected successful decode, got: {message}"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Decode rejects an empty cursor`` () =
    match McpLegacyReclassification.decodeCursor "" with
    | Error _ -> ()
    | Ok _ -> failwith "Expected empty cursor to be rejected"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Decode rejects cursor text that is not valid base64url`` () =
    match McpLegacyReclassification.decodeCursor "###not-base64###" with
    | Error _ -> ()
    | Ok _ -> failwith "Expected malformed base64url to be rejected"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Decode rejects a base64url payload that is not JSON`` () =
    let token = base64UrlEncode "not json at all"

    match McpLegacyReclassification.decodeCursor token with
    | Error _ -> ()
    | Ok _ -> failwith "Expected non-JSON payload to be rejected"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Decode rejects a JSON array payload`` () =
    let token = base64UrlEncode "[]"

    match McpLegacyReclassification.decodeCursor token with
    | Error message -> Assert.Contains("object", message)
    | Ok _ -> failwith "Expected array payload to be rejected"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Decode rejects a JSON object missing required fields`` () =
    let token = base64UrlEncode "{}"

    match McpLegacyReclassification.decodeCursor token with
    | Error _ -> ()
    | Ok _ -> failwith "Expected incomplete payload to be rejected"

[<Fact>]
[<Trait("Category", "Unit")>]
let ``Decode rejects an unsupported cursor version`` () =
    let token = McpLegacyReclassification.encodeCursor (freshCursor ())
    let tampered = tamperField token "v" (JsonValue.Create(999))

    match McpLegacyReclassification.decodeCursor tampered with
    | Error message -> Assert.Contains("version", message)
    | Ok _ -> failwith "Expected unsupported version to be rejected"

// ─── Tool dispatch: invalid input ──────────────────────────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_MissingBounds_ReturnsError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()

        try
            let! response = callTool db m "hermes_legacy_reclassify_page" "{}"
            let result = parseResult response
            Assert.True(result.TryGetProperty("error") |> fst)
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_RejectsOutOfRangeBounds`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()

        try
            let! response = callTool db m "hermes_legacy_reclassify_page" (pageArgs 0 20 false None)
            let result = parseResult response
            Assert.Contains("maxDocuments must be between 1 and 1000", result.GetProperty("error").GetString())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_MalformedCursor_ReturnsInvalidCursorError`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()

        try
            let! response =
                callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 100 false (Some "not-a-real-cursor"))

            let result = parseResult response
            Assert.Contains("Invalid cursor", result.GetProperty("error").GetString())
        finally
            db.dispose ()
    }

// ─── Tool dispatch: dry-run start/continuation, apply ─────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_DryRun_StartsInProgressThenContinuesToCompletion`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        archiveFile m "/archive/a/source.pdf" "target"
        archiveFile m "/archive/b/other.pdf" "other"

        try
            do! insertLegacy db "missing/source.pdf" (hashText "target")

            let! first = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 1 false None)
            let firstResult = parseResult first

            Assert.Equal("in_progress", stabilityOf firstResult)
            Assert.Equal(1, firstResult.GetProperty("progress").GetProperty("files_hashed").GetInt32())
            Assert.False(firstResult.GetProperty("progress").GetProperty("archive_complete").GetBoolean())
            Assert.Empty(firstResult.GetProperty("findings").EnumerateArray())

            let cursor =
                cursorOf firstResult |> Option.defaultWith (fun () -> failwith "Expected a continuation cursor")

            let! final = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 1 false (Some cursor))
            let finalResult = parseResult final

            Assert.Equal("stable_pass_completed", stabilityOf finalResult)
            Assert.False(hasCursor finalResult)

            let findings = finalResult.GetProperty("findings")
            Assert.Equal(1, findings.GetArrayLength())
            Assert.Equal("unique_sha_match", findings.[0].GetProperty("evidence").GetProperty("type").GetString())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_Apply_RepairsUniqueMatchAndIsIdempotent`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        archiveFile m "/archive/legacy/source.pdf" "source bytes"

        try
            do! insertLegacy db "missing/source.pdf" (hashText "source bytes")

            let! first = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 100 true None)
            let firstResult = parseResult first

            Assert.Equal("apply", firstResult.GetProperty("mode").GetString())
            Assert.Equal("stable_pass_completed", stabilityOf firstResult)

            let outcomes = firstResult.GetProperty("outcomes")
            Assert.Equal(1, outcomes.GetArrayLength())
            Assert.Equal("repaired", outcomes.[0].GetProperty("disposition").GetProperty("type").GetString())

            let! retry = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 100 true None)
            let retryResult = parseResult retry

            Assert.Equal("stable_pass_completed", stabilityOf retryResult)
            Assert.Empty(retryResult.GetProperty("findings").EnumerateArray())
            Assert.Empty(retryResult.GetProperty("outcomes").EnumerateArray())
        finally
            db.dispose ()
    }

// ─── Tool dispatch: tamper-evidence via validateRunCursor ──────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_CursorBoundsMismatch_IsRejected`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"

        try
            do! insertDocumentRange db 1L 2L

            let! first = callTool db m "hermes_legacy_reclassify_page" (pageArgs 1 20 false None)

            let cursor =
                cursorOf (parseResult first)
                |> Option.defaultWith (fun () -> failwith "Expected a continuation cursor")

            let! response = callTool db m "hermes_legacy_reclassify_page" (pageArgs 2 20 false (Some cursor))
            let result = parseResult response
            Assert.Contains("does not match the requested bounds", result.GetProperty("error").GetString())
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_TamperedArchiveRoot_IsRejected`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"

        try
            do! insertDocumentRange db 1L 2L

            let! first = callTool db m "hermes_legacy_reclassify_page" (pageArgs 1 20 false None)

            let cursor =
                cursorOf (parseResult first)
                |> Option.defaultWith (fun () -> failwith "Expected a continuation cursor")

            let tampered = tamperField cursor "archive_root_key" (JsonValue.Create("bogus-root-key"))

            let! response = callTool db m "hermes_legacy_reclassify_page" (pageArgs 1 20 false (Some tampered))
            let result = parseResult response
            Assert.Contains("archive root", result.GetProperty("error").GetString())
        finally
            db.dispose ()
    }

// ─── Tool dispatch: snapshot restart and eventual retry ────────────────

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_SnapshotChanged_RestartsAndEventuallyStabilizes`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        m.Fs.createDirectory "/archive"

        try
            do! insertDocumentRange db 1001L 2001L

            let! first = callTool db m "hermes_legacy_reclassify_page" (pageArgs 1000 100 false None)

            let staleCursor =
                cursorOf (parseResult first)
                |> Option.defaultWith (fun () -> failwith "Expected a continuation cursor")

            let! _ =
                db.execNonQuery
                    """INSERT INTO documents
                       (id, source_type, saved_path, category, sha256)
                       VALUES
                       (500, 'manual_drop', 'missing/000500.pdf',
                        'receipts', 'sha-500')"""
                    []

            let! changed =
                callTool db m "hermes_legacy_reclassify_page" (pageArgs 1000 100 false (Some staleCursor))

            let changedResult = parseResult changed
            Assert.Equal("snapshot_changed", stabilityOf changedResult)
            Assert.Empty(changedResult.GetProperty("findings").EnumerateArray())
            Assert.Empty(changedResult.GetProperty("outcomes").EnumerateArray())

            let restartCursor =
                cursorOf changedResult |> Option.defaultWith (fun () -> failwith "Expected a restart cursor")

            Assert.NotEqual<string>(staleCursor, restartCursor)

            let! restartedFirst =
                callTool db m "hermes_legacy_reclassify_page" (pageArgs 1000 100 false (Some restartCursor))

            let restartedFirstResult = parseResult restartedFirst

            let restartedCursor =
                cursorOf restartedFirstResult
                |> Option.defaultWith (fun () -> failwith "Expected a continuation cursor")

            let! restartedFinal =
                callTool db m "hermes_legacy_reclassify_page" (pageArgs 1000 100 false (Some restartedCursor))

            let restartedFinalResult = parseResult restartedFinal
            Assert.Equal("stable_pass_completed", stabilityOf restartedFinalResult)

            let stableIds = findingDocumentIds restartedFirstResult @ findingDocumentIds restartedFinalResult
            Assert.Contains(500L, stableIds)
            Assert.Equal(1002, stableIds.Length)
            Assert.Equal(1002, stableIds |> List.distinct |> List.length)
        finally
            db.dispose ()
    }

// ─── Tool dispatch: tampered candidate evidence in a resumed cursor ────

let private decodedCursor (token: string) : LegacyReclassification.RunCursor =
    match McpLegacyReclassification.decodeCursor token with
    | Ok cursor -> cursor
    | Error message -> failwith $"Expected a decodable cursor, got: {message}"

let private withTamperedCandidatePaths
    (mutate: LegacyReclassification.CandidatePath -> LegacyReclassification.CandidatePath)
    (cursor: LegacyReclassification.RunCursor)
    : LegacyReclassification.RunCursor =
    { cursor with
        Candidates =
            cursor.Candidates
            |> List.map (fun candidate ->
                { candidate with Paths = candidate.Paths |> List.map mutate }) }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_TamperedCandidateSavedPath_EscapingArchive_IsRejected`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        archiveFile m "/archive/a/source.pdf" "target"
        archiveFile m "/archive/b/other.pdf" "other"

        try
            do! insertLegacy db "missing/source.pdf" (hashText "target")

            let! first = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 1 true None)

            let cursor =
                cursorOf (parseResult first)
                |> Option.defaultWith (fun () -> failwith "Expected a continuation cursor")
                |> decodedCursor

            Assert.NotEmpty(cursor.Candidates)

            let tampered =
                cursor
                |> withTamperedCandidatePaths (fun path -> { path with SavedPath = "../outside.pdf" })
                |> McpLegacyReclassification.encodeCursor

            let! response = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 1 true (Some tampered))
            let result = parseResult response

            Assert.Contains("escapes the archive directory", result.GetProperty("error").GetString())

            let! path = db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.Equal("missing/source.pdf", Assert.IsType<string>(path))
        finally
            db.dispose ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_TamperedCandidateOwnershipKey_Mismatch_IsRejected`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        archiveFile m "/archive/a/source.pdf" "target"
        archiveFile m "/archive/b/other.pdf" "other"

        try
            do! insertLegacy db "missing/source.pdf" (hashText "target")

            let! first = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 1 true None)

            let cursor =
                cursorOf (parseResult first)
                |> Option.defaultWith (fun () -> failwith "Expected a continuation cursor")
                |> decodedCursor

            Assert.NotEmpty(cursor.Candidates)

            let tampered =
                cursor
                |> withTamperedCandidatePaths (fun path -> { path with OwnershipKey = path.OwnershipKey + "TAMPERED" })
                |> McpLegacyReclassification.encodeCursor

            let! response = callTool db m "hermes_legacy_reclassify_page" (pageArgs 20 1 true (Some tampered))
            let result = parseResult response

            Assert.Contains("does not match its saved_path", result.GetProperty("error").GetString())

            let! path = db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.Equal("missing/source.pdf", Assert.IsType<string>(path))
        finally
            db.dispose ()
    }

// ─── Tool dispatch: truthful reporting when an epoch changes after apply ──

/// Wrap a Database algebra so that, immediately after the *first* real
/// `tryRepairSavedPath` call returns (i.e. right after a repair commits),
/// an independent write lands on `documents` — simulating an external
/// writer racing between this page's repairAll and its epoch reread.
/// Only `tryRepairSavedPath` is replaced; every other field (execNonQuery,
/// execScalar, dispose, ...) still runs against the same connection as `db`.
let private racingAfterFirstRepair
    (db: Algebra.Database)
    (injectExternalChange: Algebra.Database -> Task<unit>)
    : Algebra.Database =
    let hasInjected = ref false

    { db with
        tryRepairSavedPath =
            fun request ->
                task {
                    let! decision = db.tryRepairSavedPath request

                    if not hasInjected.Value then
                        hasInjected.Value <- true
                        do! injectExternalChange db

                    return decision
                } }

let private insertRacingDocument (db: Algebra.Database) : Task<unit> =
    task {
        let! _ =
            db.execNonQuery
                """INSERT INTO documents
                   (id, source_type, saved_path, category, sha256)
                   VALUES
                   (9999, 'manual_drop', 'missing/racing.pdf',
                    'receipts', 'sha-racing')"""
                []
        ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``McpServer_LegacyReclassifyPage_Apply_ExternalWriteAfterRepair_ReportsCommittedOutcomesTruthfully`` () =
    task {
        let db = TestHelpers.createDb ()
        let m = TestHelpers.memFs ()
        archiveFile m "/archive/found/source.pdf" "source bytes"

        try
            do! insertLegacy db "missing/source.pdf" (hashText "source bytes")

            let racingDb = racingAfterFirstRepair db insertRacingDocument

            let! response = callTool racingDb m "hermes_legacy_reclassify_page" (pageArgs 20 100 true None)
            let result = parseResult response

            // The repair genuinely committed, but an external write raced in
            // immediately afterwards — so this page must restart...
            Assert.Equal("snapshot_changed", stabilityOf result)

            // ...while still truthfully reporting what it already committed,
            // rather than falsely claiming there is nothing to see.
            let outcomes = result.GetProperty("outcomes")
            Assert.Equal(1, outcomes.GetArrayLength())
            Assert.Equal("repaired", outcomes.[0].GetProperty("disposition").GetProperty("type").GetString())

            let findings = result.GetProperty("findings")
            Assert.Equal(1, findings.GetArrayLength())

            let message = result.GetProperty("message").GetString()
            Assert.DoesNotContain("no findings or outcomes", message)
            Assert.Contains("already been applied", message)

            // The DB reflects the committed repair regardless of the restart.
            let! path = db.execScalar "SELECT saved_path FROM documents WHERE id = 1" []
            Assert.EndsWith("found/source.pdf", (Assert.IsType<string>(path)).Replace('\\', '/'))
        finally
            db.dispose ()
    }
