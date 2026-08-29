namespace Hermes.Core

#nowarn "3261"

open System
open System.Text
open System.Text.Json.Nodes
open System.Threading.Tasks

/// MCP operator surface for bounded legacy saved-path reclassification.
/// Wraps LegacyReclassification.runPage with an opaque, tamper-evident,
/// explicitly-versioned continuation cursor (Base64Url of a JSON object —
/// never the default serialisation of the RunCursor union). The one-shot
/// LegacyReclassification.detect/repair are intentionally never called
/// here: every scan is a bounded page driven by the caller's cursor.
[<RequireQualifiedAccess>]
module McpLegacyReclassification =

    // ─── JsonNode helpers ────────────────────────────────────────────

    let private tryGetNode (node: JsonNode) (key: string) : JsonNode option =
        let result: JsonNode | null = node.[key]

        match result with
        | null -> None
        | v -> Some v

    let private asJsonObject (node: JsonNode) : JsonObject option =
        match node with
        | :? JsonObject as value -> Some value
        | _ -> None

    let private asJsonArray (node: JsonNode) : JsonArray option =
        match node with
        | :? JsonArray as value -> Some value
        | _ -> None

    /// Non-null elements of a JsonArray, as a plain F# list.
    let private jsonArrayItems (array: JsonArray) : JsonNode list =
        [ for i in 0 .. array.Count - 1 do
              let item: JsonNode | null = array.[i]

              match item with
              | null -> ()
              | v -> yield v ]

    let private optionalString (node: JsonNode) (key: string) : string option =
        tryGetNode node key
        |> Option.bind (fun value ->
            try Some(value.GetValue<string>())
            with _ -> None)

    let private optionalInt64 (node: JsonNode) (key: string) : int64 option =
        tryGetNode node key
        |> Option.bind (fun value ->
            try Some(value.GetValue<int64>())
            with _ -> None)

    let private optionalBool (node: JsonNode) (key: string) (fallback: bool) : bool =
        tryGetNode node key
        |> Option.bind (fun value ->
            try Some(value.GetValue<bool>())
            with _ -> None)
        |> Option.defaultValue fallback

    let private requireString (node: JsonNode) (key: string) : Result<string, string> =
        match tryGetNode node key with
        | None -> Error $"'{key}' is required"
        | Some value ->
            try Ok(value.GetValue<string>())
            with _ -> Error $"'{key}' must be a string"

    let private requireInt (node: JsonNode) (key: string) : Result<int, string> =
        match tryGetNode node key with
        | None -> Error $"'{key}' is required"
        | Some value ->
            try Ok(value.GetValue<int>())
            with _ -> Error $"'{key}' must be an integer"

    let private requireInt64 (node: JsonNode) (key: string) : Result<int64, string> =
        match tryGetNode node key with
        | None -> Error $"'{key}' is required"
        | Some value ->
            try Ok(value.GetValue<int64>())
            with _ -> Error $"'{key}' must be an integer"

    /// Collapse a list of Results into a Result of a list, short-circuiting
    /// on the first error. No mutation.
    let private sequenceResults (items: Result<'a, string> list) : Result<'a list, string> =
        let prepend item acc =
            match acc with
            | Error message -> Error message
            | Ok rest ->
                match item with
                | Error message -> Error message
                | Ok value -> Ok(value :: rest)

        List.foldBack prepend items (Ok [])

    // ─── Cursor wire encoding ─────────────────────────────────────────
    //
    // Explicit, versioned JSON object, Base64Url-encoded. decodeCursor only
    // checks wire shape (right keys, right JSON types, supported version).
    // LegacyReclassification.runPage still runs the full semantic
    // validateRunCursor check (run-id GUID format, epoch, phase invariants,
    // bounds match, archive-root match, candidate shape/duplicates) before
    // the cursor is ever used — that logic is deliberately not duplicated
    // here.

    [<Literal>]
    let private CursorVersion = 1

    let private toBase64Url (bytes: byte array) : string =
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')

    let private tryFromBase64Url (value: string) : byte array option =
        let remainder = value.Length % 4

        let padded =
            if remainder = 0 then value
            else value + String('=', 4 - remainder)

        try Some(Convert.FromBase64String(padded.Replace('-', '+').Replace('_', '/')))
        with _ -> None

    let private phaseToWire =
        function
        | LegacyReclassification.ArchiveScan -> "archive_scan"
        | LegacyReclassification.DocumentScan -> "document_scan"

    let private phaseFromWire (source: JsonNode) : Result<LegacyReclassification.RunPhase, string> =
        match requireString source "phase" with
        | Error message -> Error message
        | Ok "archive_scan" -> Ok LegacyReclassification.ArchiveScan
        | Ok "document_scan" -> Ok LegacyReclassification.DocumentScan
        | Ok _ -> Error "'phase' is invalid"

    let private archiveStateToWire =
        function
        | LegacyReclassification.ArchiveNotStarted -> "not_started", None
        | LegacyReclassification.AfterArchiveFile key -> "after_file", Some key
        | LegacyReclassification.ArchiveCompleted -> "completed", None

    let private archiveFromWire
        (source: JsonNode)
        : Result<LegacyReclassification.ArchiveContinuation, string> =
        match requireString source "archive_state" with
        | Error message -> Error message
        | Ok state ->
            match state, optionalString source "archive_sort_key" with
            | "not_started", _ -> Ok LegacyReclassification.ArchiveNotStarted
            | "completed", _ -> Ok LegacyReclassification.ArchiveCompleted
            | "after_file", Some key -> Ok(LegacyReclassification.AfterArchiveFile key)
            | _ -> Error "'archive_state' is invalid"

    let private documentStateToWire =
        function
        | LegacyReclassification.BeforeFirstDocument -> "before_first", None
        | LegacyReclassification.AfterDocument id -> "after_document", Some id

    let private documentFromWire
        (source: JsonNode)
        : Result<LegacyReclassification.DocumentContinuation, string> =
        match requireString source "document_state" with
        | Error message -> Error message
        | Ok state ->
            match state, optionalInt64 source "document_id" with
            | "before_first", _ -> Ok LegacyReclassification.BeforeFirstDocument
            | "after_document", Some id -> Ok(LegacyReclassification.AfterDocument id)
            | _ -> Error "'document_state' is invalid"

    let private candidatePathToJson (path: LegacyReclassification.CandidatePath) : JsonObject =
        let payload = JsonObject()
        payload["ownership_key"] <- JsonValue.Create(path.OwnershipKey)
        payload["saved_path"] <- JsonValue.Create(path.SavedPath)
        payload

    let private candidatePathFromJson
        (node: JsonNode)
        : Result<LegacyReclassification.CandidatePath, string> =
        match asJsonObject node with
        | None -> Error "cursor candidate path is malformed"
        | Some source ->
            match optionalString source "ownership_key", optionalString source "saved_path" with
            | Some ownershipKey, Some savedPath ->
                let path: LegacyReclassification.CandidatePath =
                    { OwnershipKey = ownershipKey
                      SavedPath = savedPath }

                Ok path
            | _ -> Error "cursor candidate path is malformed"

    let private candidateToJson (candidate: LegacyReclassification.TargetCandidates) : JsonObject =
        let payload = JsonObject()
        let paths = JsonArray()

        for path in candidate.Paths do
            paths.Add(candidatePathToJson path)

        payload["sha256"] <- JsonValue.Create(candidate.Sha256)
        payload["paths"] <- paths
        payload

    let private candidateFromJson
        (node: JsonNode)
        : Result<LegacyReclassification.TargetCandidates, string> =
        match asJsonObject node with
        | None -> Error "cursor candidate is malformed"
        | Some source ->
            match optionalString source "sha256" with
            | None -> Error "cursor candidate is malformed"
            | Some sha256 ->
                match tryGetNode source "paths" |> Option.bind asJsonArray with
                | None -> Error "cursor candidate is malformed"
                | Some pathsArray ->
                    match
                        pathsArray
                        |> jsonArrayItems
                        |> List.map candidatePathFromJson
                        |> sequenceResults
                    with
                    | Error message -> Error message
                    | Ok paths ->
                        let candidate: LegacyReclassification.TargetCandidates =
                            { Sha256 = sha256; Paths = paths }

                        Ok candidate

    let private candidatesToJson
        (candidates: LegacyReclassification.TargetCandidates list)
        : JsonArray =
        let items = JsonArray()

        for candidate in candidates do
            items.Add(candidateToJson candidate)

        items

    let private candidatesFromJson
        (source: JsonNode)
        : Result<LegacyReclassification.TargetCandidates list, string> =
        match tryGetNode source "candidates" |> Option.bind asJsonArray with
        | None -> Error "'candidates' must be an array"
        | Some candidatesArray ->
            candidatesArray
            |> jsonArrayItems
            |> List.map candidateFromJson
            |> sequenceResults

    type private CursorIdentity =
        { RunId: string
          ArchiveRootKey: string
          MaxDocuments: int
          MaxFiles: int }

    let private identityFromWire (source: JsonNode) : Result<CursorIdentity, string> =
        match requireString source "run_id" with
        | Error message -> Error message
        | Ok runId ->
            match requireString source "archive_root_key" with
            | Error message -> Error message
            | Ok archiveRootKey ->
                match requireInt source "max_documents" with
                | Error message -> Error message
                | Ok maxDocuments ->
                    match requireInt source "max_files" with
                    | Error message -> Error message
                    | Ok maxFiles ->
                        let identity: CursorIdentity =
                            { RunId = runId
                              ArchiveRootKey = archiveRootKey
                              MaxDocuments = maxDocuments
                              MaxFiles = maxFiles }

                        Ok identity

    type private CursorState =
        { Epoch: int64
          Phase: LegacyReclassification.RunPhase
          Archive: LegacyReclassification.ArchiveContinuation
          Documents: LegacyReclassification.DocumentContinuation }

    let private stateFromWire (source: JsonNode) : Result<CursorState, string> =
        match requireInt64 source "epoch" with
        | Error message -> Error message
        | Ok epoch ->
            match phaseFromWire source with
            | Error message -> Error message
            | Ok phase ->
                match archiveFromWire source with
                | Error message -> Error message
                | Ok archive ->
                    match documentFromWire source with
                    | Error message -> Error message
                    | Ok documents ->
                        let state: CursorState =
                            { Epoch = epoch
                              Phase = phase
                              Archive = archive
                              Documents = documents }

                        Ok state

    let private buildCursor
        (version: int)
        (identity: CursorIdentity)
        (state: CursorState)
        (candidates: LegacyReclassification.TargetCandidates list)
        : Result<LegacyReclassification.RunCursor, string> =
        if version <> CursorVersion then
            Error "cursor version is not supported"
        else
            let cursor: LegacyReclassification.RunCursor =
                { RunId = LegacyReclassification.RepairRunId identity.RunId
                  ArchiveRootKey = identity.ArchiveRootKey
                  MaxDocuments = identity.MaxDocuments
                  MaxFiles = identity.MaxFiles
                  Epoch = LegacyReclassification.SnapshotEpoch state.Epoch
                  Phase = state.Phase
                  Archive = state.Archive
                  Documents = state.Documents
                  Candidates = candidates }

            Ok cursor

    let private cursorFromJson
        (source: JsonObject)
        : Result<LegacyReclassification.RunCursor, string> =
        match requireInt source "v" with
        | Error message -> Error message
        | Ok version ->
            match identityFromWire source with
            | Error message -> Error message
            | Ok identity ->
                match stateFromWire source with
                | Error message -> Error message
                | Ok state ->
                    match candidatesFromJson source with
                    | Error message -> Error message
                    | Ok candidates -> buildCursor version identity state candidates

    let private cursorToJson (cursor: LegacyReclassification.RunCursor) : JsonObject =
        let (LegacyReclassification.RepairRunId runId) = cursor.RunId
        let (LegacyReclassification.SnapshotEpoch epoch) = cursor.Epoch
        let archiveState, archiveSortKey = archiveStateToWire cursor.Archive
        let documentState, documentId = documentStateToWire cursor.Documents

        let payload = JsonObject()
        payload["v"] <- JsonValue.Create(CursorVersion)
        payload["run_id"] <- JsonValue.Create(runId)
        payload["archive_root_key"] <- JsonValue.Create(cursor.ArchiveRootKey)
        payload["max_documents"] <- JsonValue.Create(cursor.MaxDocuments)
        payload["max_files"] <- JsonValue.Create(cursor.MaxFiles)
        payload["epoch"] <- JsonValue.Create(epoch)
        payload["phase"] <- JsonValue.Create(phaseToWire cursor.Phase)
        payload["archive_state"] <- JsonValue.Create(archiveState)

        archiveSortKey
        |> Option.iter (fun key -> payload["archive_sort_key"] <- JsonValue.Create(key))

        payload["document_state"] <- JsonValue.Create(documentState)

        documentId
        |> Option.iter (fun id -> payload["document_id"] <- JsonValue.Create(id))

        payload["candidates"] <- candidatesToJson cursor.Candidates
        payload

    let private parseCursorPayload (json: string) : Result<LegacyReclassification.RunCursor, string> =
        try
            let parsed: JsonNode | null = JsonNode.Parse(json)

            match parsed with
            | null -> Error "cursor payload is not valid JSON"
            | node ->
                match asJsonObject node with
                | Some source -> cursorFromJson source
                | None -> Error "cursor payload must be a JSON object"
        with _ -> Error "cursor payload is not valid JSON"

    /// Encode a run cursor as an opaque Base64Url token. The wire format is
    /// an explicit, versioned JSON object — never the default serialisation
    /// of the RunCursor union.
    let encodeCursor (cursor: LegacyReclassification.RunCursor) : string =
        cursor
        |> cursorToJson
        |> fun payload -> payload.ToJsonString()
        |> Encoding.UTF8.GetBytes
        |> toBase64Url

    /// Decode an opaque cursor token. Only checks wire shape; runPage still
    /// runs the full semantic validateRunCursor check before using it.
    let decodeCursor (token: string) : Result<LegacyReclassification.RunCursor, string> =
        if String.IsNullOrWhiteSpace token then
            Error "cursor must not be empty"
        else
            match tryFromBase64Url token with
            | None -> Error "cursor is not valid base64url"
            | Some bytes -> parseCursorPayload (Encoding.UTF8.GetString bytes)

    // ─── Request parsing ──────────────────────────────────────────────

    type private PageRequest =
        { Bounds: LegacyReclassification.ScanBounds
          Mode: LegacyReclassification.RunMode
          Continuation: LegacyReclassification.RunCursor option }

    let private parseBounds (args: JsonNode) : Result<LegacyReclassification.ScanBounds, string> =
        match requireInt args "max_documents" with
        | Error message -> Error message
        | Ok maxDocuments ->
            match requireInt args "max_files" with
            | Error message -> Error message
            | Ok maxFiles -> LegacyReclassification.createBounds maxDocuments maxFiles

    let private parseMode (args: JsonNode) : LegacyReclassification.RunMode =
        if optionalBool args "apply" false then LegacyReclassification.Apply
        else LegacyReclassification.DryRun

    let private parseCursorArg
        (args: JsonNode)
        : Result<LegacyReclassification.RunCursor option, string> =
        match tryGetNode args "cursor" with
        | None -> Ok None
        | Some node ->
            try
                match decodeCursor (node.GetValue<string>()) with
                | Error message -> Error $"Invalid cursor: {message}"
                | Ok cursor -> Ok(Some cursor)
            with _ -> Error "'cursor' must be a string"

    let private parseRequest (args: JsonNode) : Result<PageRequest, string> =
        match parseBounds args with
        | Error message -> Error message
        | Ok bounds ->
            match parseCursorArg args with
            | Error message -> Error message
            | Ok continuation ->
                let request: PageRequest =
                    { Bounds = bounds
                      Mode = parseMode args
                      Continuation = continuation }

                Ok request

    // ─── Response shaping ─────────────────────────────────────────────

    let private stringArrayJson (values: string list) : JsonArray =
        let items = JsonArray()

        for value in values do
            items.Add(JsonValue.Create(value))

        items

    let private int64ArrayJson (values: int64 list) : JsonArray =
        let items = JsonArray()

        for value in values do
            items.Add(JsonValue.Create(value))

        items

    let private evidenceToJson (evidence: LegacyReclassification.Evidence) : JsonObject =
        let payload = JsonObject()

        match evidence with
        | LegacyReclassification.UniqueShaMatch savedPath ->
            payload["type"] <- JsonValue.Create("unique_sha_match")
            payload["saved_path"] <- JsonValue.Create(savedPath)
        | LegacyReclassification.AmbiguousShaMatches savedPaths ->
            payload["type"] <- JsonValue.Create("ambiguous_sha_matches")
            payload["saved_paths"] <- stringArrayJson savedPaths
        | LegacyReclassification.MissingShaMatch -> payload["type"] <- JsonValue.Create("missing_sha_match")
        | LegacyReclassification.ShaMismatch actualSha256 ->
            payload["type"] <- JsonValue.Create("sha_mismatch")
            payload["actual_sha256"] <- JsonValue.Create(actualSha256)
        | LegacyReclassification.InconclusiveScan possibleMatches ->
            payload["type"] <- JsonValue.Create("inconclusive_scan")
            payload["possible_matches"] <- stringArrayJson possibleMatches

        payload

    let private failureToJson (failure: LegacyReclassification.RepairFailure) : JsonObject =
        let payload = JsonObject()

        match failure with
        | LegacyReclassification.CandidateDisappeared path ->
            payload["type"] <- JsonValue.Create("candidate_disappeared")
            payload["path"] <- JsonValue.Create(path)
        | LegacyReclassification.CandidateChanged actualSha256 ->
            payload["type"] <- JsonValue.Create("candidate_changed")
            payload["actual_sha256"] <- JsonValue.Create(actualSha256)
        | LegacyReclassification.DocumentChanged -> payload["type"] <- JsonValue.Create("document_changed")
        | LegacyReclassification.DatabaseFailure message ->
            payload["type"] <- JsonValue.Create("database_failure")
            payload["message"] <- JsonValue.Create(message)

        payload

    let private dispositionToJson (disposition: LegacyReclassification.RepairDisposition) : JsonObject =
        let payload = JsonObject()

        match disposition with
        | LegacyReclassification.Repaired savedPath ->
            payload["type"] <- JsonValue.Create("repaired")
            payload["saved_path"] <- JsonValue.Create(savedPath)
        | LegacyReclassification.Unchanged savedPath ->
            payload["type"] <- JsonValue.Create("unchanged")
            payload["saved_path"] <- JsonValue.Create(savedPath)
        | LegacyReclassification.Skipped evidence ->
            payload["type"] <- JsonValue.Create("skipped")
            payload["evidence"] <- evidenceToJson evidence
        | LegacyReclassification.Conflict conflict ->
            payload["type"] <- JsonValue.Create("conflict")
            payload["candidate_saved_path"] <- JsonValue.Create(conflict.CandidateSavedPath)
            payload["owner_document_ids"] <- int64ArrayJson conflict.OwnerDocumentIds
        | LegacyReclassification.Failed failure ->
            payload["type"] <- JsonValue.Create("failed")
            payload["reason"] <- failureToJson failure

        payload

    let private findingToJson (finding: LegacyReclassification.Finding) : JsonObject =
        let payload = JsonObject()
        payload["document_id"] <- JsonValue.Create(finding.DocumentId)
        payload["saved_path"] <- JsonValue.Create(finding.SavedPath)
        payload["sha256"] <- JsonValue.Create(finding.Sha256)
        payload["evidence"] <- evidenceToJson finding.Evidence
        payload

    let private outcomeToJson (outcome: LegacyReclassification.RepairOutcome) : JsonObject =
        let payload = JsonObject()
        payload["document_id"] <- JsonValue.Create(outcome.DocumentId)
        payload["disposition"] <- dispositionToJson outcome.Disposition
        payload

    let private progressToJson (progress: LegacyReclassification.PageProgress) : JsonObject =
        let payload = JsonObject()
        payload["documents_scanned"] <- JsonValue.Create(progress.DocumentsScanned)
        payload["files_hashed"] <- JsonValue.Create(progress.FilesHashed)
        payload["archive_complete"] <- JsonValue.Create(progress.ArchiveComplete)
        payload

    let private findingsToJson (findings: LegacyReclassification.Finding list) : JsonArray =
        let items = JsonArray()

        for finding in findings do
            items.Add(findingToJson finding)

        items

    let private outcomesToJson (outcomes: LegacyReclassification.RepairOutcome list) : JsonArray =
        let items = JsonArray()

        for outcome in outcomes do
            items.Add(outcomeToJson outcome)

        items

    let private modeText =
        function
        | LegacyReclassification.DryRun -> "dry_run"
        | LegacyReclassification.Apply -> "apply"

    let private stabilityText =
        function
        | LegacyReclassification.InProgress -> "in_progress"
        | LegacyReclassification.StablePassCompleted -> "stable_pass_completed"
        | LegacyReclassification.SnapshotChanged _ -> "snapshot_changed"

    let private snapshotChangedMessage
        (findings: LegacyReclassification.Finding list)
        (outcomes: LegacyReclassification.RepairOutcome list)
        : string =
        match findings, outcomes with
        | [], [] ->
            "The archive changed underneath the scan, so this page reports no findings or outcomes. Resubmit the returned cursor to restart the pass against the new, stable snapshot."
        | _, [] ->
            "The archive changed underneath the scan after these findings were detected against the prior snapshot. Nothing was applied; they are retained below for visibility. Resubmit the returned cursor to restart the pass against the new, stable snapshot."
        | _, _ ->
            "The archive changed underneath the scan after this page's findings and outcomes were already committed against the prior snapshot. They are retained below and have already been applied; restart is required — resubmit the returned cursor to continue against the new, stable snapshot."

    let private stabilityMessage
        (page: LegacyReclassification.RunPageResult)
        : string =
        match page.Stability with
        | LegacyReclassification.InProgress ->
            "This page is not final; resubmit the returned cursor to continue the scan."
        | LegacyReclassification.StablePassCompleted ->
            "The pass completed against a stable snapshot; there is no cursor to continue."
        | LegacyReclassification.SnapshotChanged _ ->
            snapshotChangedMessage page.Findings page.Outcomes

    let private toResponse
        (mode: LegacyReclassification.RunMode)
        (page: LegacyReclassification.RunPageResult)
        : JsonNode =
        let payload = JsonObject()
        payload["mode"] <- JsonValue.Create(modeText mode)
        payload["stability"] <- JsonValue.Create(stabilityText page.Stability)
        payload["message"] <- JsonValue.Create(stabilityMessage page)

        page.Cursor
        |> Option.iter (fun cursor -> payload["cursor"] <- JsonValue.Create(encodeCursor cursor))

        payload["progress"] <- progressToJson page.Progress
        payload["findings"] <- findingsToJson page.Findings
        payload["outcomes"] <- outcomesToJson page.Outcomes
        payload :> JsonNode

    let private errorResult (message: string) : JsonNode =
        let payload = JsonObject()
        payload["error"] <- JsonValue.Create(message)
        payload :> JsonNode

    // ─── MCP entry point ──────────────────────────────────────────────

    /// One bounded dry-run/apply page of the legacy saved-path
    /// reclassification scan. Always LegacyReclassification.runPage —
    /// never the one-shot detect/repair.
    let run
        (db: Algebra.Database)
        (fs: Algebra.FileSystem)
        (archiveDir: string)
        (args: JsonNode)
        : Task<JsonNode> =
        task {
            match parseRequest args with
            | Error message -> return errorResult message
            | Ok request ->
                let! result =
                    LegacyReclassification.runPage
                        db fs archiveDir request.Bounds request.Mode request.Continuation

                match result with
                | Error message -> return errorResult message
                | Ok page -> return toResponse request.Mode page
        }
