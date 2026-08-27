namespace Hermes.Core

#nowarn "3261"

open System
open System.Threading.Tasks

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

    /// Claims a durable canonical value, writes its artifact, then publishes
    /// all derived data from that canonical value.
    let publishCanonical
        (db: Algebra.Database)
        (token: Token)
        (folder: PublicationFence.ArtifactFolder)
        (claim: Algebra.TransactionScope -> Task<'canonical>)
        (writeArtifact: 'canonical -> Task<unit>)
        (publication:
            'canonical -> Algebra.TransactionScope -> Task<unit>)
        : Task<Publication<'canonical>> =
        fencedArtifact token.DocumentId folder (fun () ->
            task {
                match! publishCore db token claim with
                | Superseded -> return Superseded
                | Published canonical ->
                    do! writeArtifact canonical
                    match! publishCore db token (publication canonical) with
                    | Superseded -> return Superseded
                    | Published () -> return Published canonical
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

    let private writeWhenCurrent
        db token validate
        (writeArtifact: 'artifact -> Task<unit>)
        (artifact: 'artifact)
        : Task<Publication<'artifact>> =
        task {
            let! current = validateCurrent db token validate
            if not current then
                return Superseded
            else
                do! writeArtifact artifact
                let! committed = validateCurrent db token validate
                return
                    if committed then Published artifact
                    else Superseded
        }

    /// Re-reads and merges a shared artifact while holding its folder fence,
    /// then transactionally revalidates generation and output currentness.
    let republishArtifact
        (db: Algebra.Database)
        (token: Token)
        (folder: PublicationFence.ArtifactFolder)
        (validate: Algebra.TransactionScope -> Task<bool>)
        (prepare: unit -> Task<Result<'artifact, string>>)
        (writeArtifact: 'artifact -> Task<unit>)
        : Task<Result<Publication<'artifact>, string>> =
        fencedArtifact token.DocumentId folder (fun () ->
            task {
                let! current = validateCurrent db token validate
                if not current then
                    return Ok Superseded
                else
                    match! prepare () with
                    | Error error -> return Error error
                    | Ok artifact ->
                        let! publication =
                            writeWhenCurrent
                                db token validate writeArtifact artifact
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
