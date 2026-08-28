namespace Hermes.Core

#nowarn "3261"

open System
open System.Threading.Tasks

/// Monotonic revision for artifacts shared by sibling documents in one folder.
/// Sibling documents own independent document generations, so generation
/// fencing alone cannot order two publishers that write the same file. This
/// revision is captured before slow work and revalidated under the publication
/// fence, so a stale sibling is rejected before it writes any bytes.
[<RequireQualifiedAccess>]
module ArtifactRevision =

    type Token =
        { Identities: string list
          Value: int64 }

    let private bumpSql =
        """INSERT INTO artifact_folder_revisions
             (folder_identity, revision, updated_at)
           VALUES (@identity, @revision, datetime('now'))
           ON CONFLICT(folder_identity) DO UPDATE SET
             revision = MAX(artifact_folder_revisions.revision, excluded.revision),
             updated_at = datetime('now')"""

    let private readSql (count: int) =
        let placeholders =
            List.init count (fun index -> $"@identity{index}")
            |> String.concat ", "
        $"""SELECT COALESCE(MAX(revision), 0)
            FROM artifact_folder_revisions
            WHERE folder_identity IN ({placeholders})"""

    let private readParameters identities =
        identities
        |> List.mapi (fun index identity ->
            ($"@identity{index}", Database.boxVal identity))

    let private revisionValue (value: obj | null) : int64 =
        match value with
        | :? int64 as revision -> revision
        | :? int as revision -> int64 revision
        | _ -> invalidOp "Artifact revision read did not return a revision"

    let private readWith
        (execScalar:
            string -> (string * obj) list -> Task<obj | null>)
        (identities: string list)
        : Task<Token> =
        task {
            match identities with
            | [] -> return { Identities = []; Value = 0L }
            | _ ->
                let! value =
                    execScalar
                        (readSql (List.length identities))
                        (readParameters identities)
                return
                    { Identities = identities
                      Value = revisionValue value }
        }

    let private identitiesOf folder =
        PublicationFence.ArtifactFolder.identities folder

    /// Captured outside any transaction, before slow work begins.
    let current (db: Algebra.Database) folder : Task<Token> =
        readWith db.execScalar (identitiesOf folder)

    /// Revalidated inside the publication transaction that holds the fence.
    let isCurrentIn
        (scope: Algebra.TransactionScope)
        (token: Token)
        : Task<bool> =
        task {
            let! latest = readWith scope.execScalar token.Identities
            return latest.Value = token.Value
        }

    let private bumpOne
        (scope: Algebra.TransactionScope)
        (revision: int64)
        ()
        (identity: string)
        : Task<unit> =
        task {
            let! _ =
                scope.execNonQuery
                    bumpSql
                    [ ("@identity", Database.boxVal identity)
                      ("@revision", Database.boxVal revision) ]
            return ()
        }

    /// Raises every identity of the folder above the value any sibling could
    /// have captured, so overlapping identity sets always observe the change.
    let bumpIn
        (scope: Algebra.TransactionScope)
        (folder: PublicationFence.ArtifactFolder)
        : Task<unit> =
        task {
            let identities = identitiesOf folder
            let! latest = readWith scope.execScalar identities
            do!
                identities
                |> Prelude.foldTask (bumpOne scope (latest.Value + 1L)) ()
        }

/// Monotonic per-document generation fencing.
[<RequireQualifiedAccess>]
module Generation =

    type Token =
        { DocumentId: int64
          Value: int64 }

    type Publication<'a> =
        | Published of 'a
        | Superseded

    let private currentSql =
        """SELECT COALESCE(
               (SELECT generation
                FROM document_generations
                WHERE document_id = @doc), 0)"""

    let private parameters (documentId: int64) : (string * obj) list =
        [ ("@doc", Database.boxVal documentId) ]

    let private generationValue
        (context: string)
        (value: obj | null)
        : int64 =
        match value with
        | :? int64 as generation -> generation
        | :? int as generation -> int64 generation
        | _ -> invalidOp $"{context} did not return a generation"

    let private readWith
        (execScalar:
            string -> (string * obj) list -> Task<obj | null>)
        (documentId: int64)
        : Task<Token> =
        task {
            let! value = execScalar currentSql (parameters documentId)
            return
                { DocumentId = documentId
                  Value = generationValue "Generation read" value }
        }

    let current (db: Algebra.Database) documentId : Task<Token> =
        readWith db.execScalar documentId

    let currentIn (scope: Algebra.TransactionScope) documentId : Task<Token> =
        readWith scope.execScalar documentId

    let bump (scope: Algebra.TransactionScope) documentId : Task<Token> =
        task {
            let! value =
                scope.execScalar
                    """INSERT INTO document_generations
                         (document_id, generation, updated_at)
                       VALUES (@doc, 1, datetime('now'))
                       ON CONFLICT(document_id) DO UPDATE SET
                         generation = document_generations.generation + 1,
                         updated_at = datetime('now')
                       RETURNING generation"""
                    (parameters documentId)
            return
                { DocumentId = documentId
                  Value = generationValue "Generation bump" value }
        }

    let isCurrentIn
        (scope: Algebra.TransactionScope)
        (token: Token)
        : Task<bool> =
        task {
            let! currentToken = currentIn scope token.DocumentId
            return currentToken.Value = token.Value
        }

    let isCurrent
        (db: Algebra.Database)
        (token: Token)
        : Task<bool> =
        task {
            let! currentToken = current db token.DocumentId
            return currentToken.Value = token.Value
        }

    let private publishTransaction
        (token: Token)
        (publish: Algebra.TransactionScope -> Task<'a>)
        (captured: TaskCompletionSource<Publication<'a>>)
        (scope: Algebra.TransactionScope)
        : Task<Result<unit, string>> =
        task {
            let! currentToken = isCurrentIn scope token
            if currentToken then
                let! value = publish scope
                captured.TrySetResult(Published value) |> ignore
            else
                captured.TrySetResult(Superseded) |> ignore
            return Ok ()
        }

    let private publishCore
        (db: Algebra.Database)
        (token: Token)
        (publication: Algebra.TransactionScope -> Task<'a>)
        : Task<Publication<'a>> =
        task {
            let captured =
                TaskCompletionSource<Publication<'a>>(
                    TaskCreationOptions.RunContinuationsAsynchronously)
            let! result =
                db.inTransaction
                    (publishTransaction token publication captured)
            match result with
            | Error error ->
                return invalidOp $"Generation-fenced publication failed: {error}"
            | Ok () ->
                return! captured.Task
        }

    /// Runs a body under the document's publication fence, so that no reflow
    /// acceptance and no competing publisher can interleave with it.
    let fenced (documentId: int64) (body: unit -> Task<'a>) : Task<'a> =
        PublicationFence.withDocument documentId body

    /// Fences both a document generation and its shared folder artifact.
    let fencedArtifact
        (documentId: int64)
        (folder: PublicationFence.ArtifactFolder)
        (body: unit -> Task<'a>)
        : Task<'a> =
        PublicationFence.withDocumentAndArtifact documentId folder body

    /// Reads a file-derived value while excluding publishers for the same
    /// document and folder, and rejects it if its generation changes.
    let readArtifactStable
        (db: Algebra.Database)
        (token: Token)
        (folder: PublicationFence.ArtifactFolder)
        (read: unit -> Task<'a>)
        : Task<Publication<'a>> =
        fencedArtifact token.DocumentId folder (fun () ->
            task {
                let! currentBefore = isCurrent db token
                if not currentBefore then
                    return Superseded
                else
                    let! value = read ()
                    let! currentAfter = isCurrent db token
                    return
                        if currentAfter then Published value
                        else Superseded
            })

    /// Executes all database publication in the same transaction as the
    /// generation check. Transaction isolation is the fence for database-only
    /// publication, so the document fence is deliberately not taken here: it
    /// would deadlock when this runs inside an already fenced region.
    let publish
        (db: Algebra.Database)
        (token: Token)
        (publication: Algebra.TransactionScope -> Task<'a>)
        : Task<Publication<'a>> =
        publishCore db token publication

    /// Rejects the claim when a sibling has already advanced the shared folder,
    /// so no stale canonical value is ever durably claimed.
    let private claimWhenArtifactCurrent
        (artifact: ArtifactRevision.Token)
        (claim: Algebra.TransactionScope -> Task<'canonical>)
        (scope: Algebra.TransactionScope)
        : Task<'canonical option> =
        task {
            match! ArtifactRevision.isCurrentIn scope artifact with
            | false -> return None
            | true ->
                let! canonical = claim scope
                return Some canonical
        }

    /// Publishes derived data and advances the folder revision in the same
    /// transaction, so the bump can never commit without its publication.
    let private publishAndBump
        (folder: PublicationFence.ArtifactFolder)
        (artifact: ArtifactRevision.Token)
        (publication: Algebra.TransactionScope -> Task<unit>)
        (scope: Algebra.TransactionScope)
        : Task<bool> =
        task {
            match! ArtifactRevision.isCurrentIn scope artifact with
            | false -> return false
            | true ->
                do! publication scope
                do! ArtifactRevision.bumpIn scope folder
                return true
        }

    /// Claims a durable canonical value, writes its artifact, then publishes
    /// all derived data from that canonical value. The folder revision is
    /// captured before slow work and revalidated here under the folder fence,
    /// so a slow sibling can never overwrite a newer sibling's shared artifact.
    let publishCanonical
        (db: Algebra.Database)
        (token: Token)
        (folder: PublicationFence.ArtifactFolder)
        (artifact: ArtifactRevision.Token)
        (claim: Algebra.TransactionScope -> Task<'canonical>)
        (writeArtifact: 'canonical -> Task<unit>)
        (publication:
            'canonical -> Algebra.TransactionScope -> Task<unit>)
        : Task<Publication<'canonical>> =
        fencedArtifact token.DocumentId folder (fun () ->
            task {
                match!
                    publishCore db token
                        (claimWhenArtifactCurrent artifact claim)
                    with
                | Superseded -> return Superseded
                | Published None -> return Superseded
                | Published (Some canonical) ->
                    do! writeArtifact canonical
                    match!
                        publishCore db token
                            (publishAndBump
                                folder artifact (publication canonical))
                        with
                    | Superseded -> return Superseded
                    | Published false -> return Superseded
                    | Published true -> return Published canonical
            })

    let private validationPassed = function
        | Published true -> true
        | Published false
        | Superseded -> false

    let private validateCurrent
        db token
        (validate: Algebra.TransactionScope -> Task<bool>) =
        task {
            let! publication = publishCore db token validate
            return validationPassed publication
        }

    /// One transactional predicate for both fences: the folder revision
    /// captured before slow work, and the caller's generation and output
    /// validation.
    let private artifactAndValidationCurrentIn
        (artifact: ArtifactRevision.Token)
        (validate: Algebra.TransactionScope -> Task<bool>)
        (scope: Algebra.TransactionScope)
        : Task<bool> =
        task {
            match! ArtifactRevision.isCurrentIn scope artifact with
            | false -> return false
            | true -> return! validate scope
        }

    /// Advances the folder revision in the same transaction that revalidates
    /// the republication, so the bump can never commit without it and no
    /// sibling can keep a token captured before these bytes were written.
    let private validateAndBumpIn
        (folder: PublicationFence.ArtifactFolder)
        (stillCurrent: Algebra.TransactionScope -> Task<bool>)
        (scope: Algebra.TransactionScope)
        : Task<bool> =
        task {
            match! stillCurrent scope with
            | false -> return false
            | true ->
                do! ArtifactRevision.bumpIn scope folder
                return true
        }

    let private writeWhenCurrent
        db token
        (stillCurrent: Algebra.TransactionScope -> Task<bool>)
        (commit: Algebra.TransactionScope -> Task<bool>)
        (writeArtifact: 'content -> Task<unit>)
        (content: 'content)
        : Task<Publication<'content>> =
        task {
            let! current = validateCurrent db token stillCurrent
            if not current then
                return Superseded
            else
                do! writeArtifact content
                let! committed = validateCurrent db token commit
                return
                    if committed then Published content
                    else Superseded
        }

    /// Re-reads and merges a shared artifact while holding its folder fence,
    /// then transactionally revalidates generation, output currentness and the
    /// folder revision captured before the caller's slow work. That revision is
    /// advanced with the write it publishes, so a sibling holding an older
    /// token is rejected before it writes any bytes.
    let republishArtifact
        (db: Algebra.Database)
        (token: Token)
        (folder: PublicationFence.ArtifactFolder)
        (artifact: ArtifactRevision.Token)
        (validate: Algebra.TransactionScope -> Task<bool>)
        (prepare: unit -> Task<Result<'content, string>>)
        (writeArtifact: 'content -> Task<unit>)
        : Task<Result<Publication<'content>, string>> =
        let stillCurrent = artifactAndValidationCurrentIn artifact validate
        let commit = validateAndBumpIn folder stillCurrent
        fencedArtifact token.DocumentId folder (fun () ->
            task {
                let! current = validateCurrent db token stillCurrent
                if not current then
                    return Ok Superseded
                else
                    match! prepare () with
                    | Error error -> return Error error
                    | Ok content ->
                        let! publication =
                            writeWhenCurrent
                                db token stillCurrent commit
                                writeArtifact content
                        return Ok publication
            })

    /// Replaces a shared artifact and publishes its database rows inside one
    /// fenced region. The generation check, the canonical file replacement and
    /// the transactional publication cannot straddle a reflow commit, so a
    /// superseded publisher never writes bytes at all.
    let publishArtifact
        (db: Algebra.Database)
        (token: Token)
        (folder: PublicationFence.ArtifactFolder)
        (writeArtifact: unit -> Task<unit>)
        (publication: Algebra.TransactionScope -> Task<'a>)
        : Task<Publication<'a>> =
        fencedArtifact token.DocumentId folder (fun () ->
            task {
                let! current = isCurrent db token
                if not current then
                    return Superseded
                else
                    do! writeArtifact ()
                    return! publishCore db token publication
            })

    let private confirmCurrent (_: Algebra.TransactionScope) : Task<unit> =
        Task.FromResult(())

    /// Fences a side effect that cannot participate in a database transaction.
    /// The effect is skipped entirely once the generation has moved on, so a
    /// stale publisher can never overwrite newer content and then report
    /// Superseded.
    let publishEffect
        (db: Algebra.Database)
        (token: Token)
        (folder: PublicationFence.ArtifactFolder)
        (effect: unit -> Task<unit>)
        : Task<Publication<unit>> =
        publishArtifact db token folder effect confirmCurrent

    /// Keeps a file-derived value only when its generation is stable across
    /// both the read and the caller-supplied output-ledger validation.
    let readStable
        (db: Algebra.Database)
        (documentId: int64)
        (read: unit -> Task<'a option>)
        : Task<'a option> =
        task {
            let! before = current db documentId
            let! value = read ()
            let! after = current db documentId
            return
                if before.Value = after.Value then value
                else None
        }
