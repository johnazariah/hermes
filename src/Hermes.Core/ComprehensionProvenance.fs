namespace Hermes.Core

/// Explicit origin links for comprehension left current by an accepted reembed.
[<RequireQualifiedAccess>]
module internal ComprehensionProvenance =

    let currentOutputs =
        """EXISTS(SELECT 1 FROM extraction e WHERE e.document_id = d.id)
           AND EXISTS(SELECT 1 FROM comprehension c WHERE c.document_id = d.id)
           AND EXISTS(SELECT 1 FROM stage_completions s
                      WHERE s.document_id = d.id AND s.stage_name = 'extract')
           AND EXISTS(SELECT 1 FROM stage_completions s
                      WHERE s.document_id = d.id AND s.stage_name = 'deep-comprehend')"""

    // The generation expression is an internal SQL fragment, never user input.
    // A link resolves exactly one origin; missing/corrupt origins cannot fall
    // back to another generation or another document.
    let joins (generation: string) =
        $"""LEFT JOIN comprehension_provenance cp
              ON cp.document_id = d.id AND cp.generation = {generation}
            LEFT JOIN stage_publications p
              ON p.document_id = d.id AND p.stage_name = 'deep-comprehend'
             AND p.generation = COALESCE(cp.publication_generation, {generation})"""

    let private carrySql =
        $"""INSERT INTO comprehension_provenance
                (document_id, generation, publication_generation, operation_id)
            SELECT d.id, @generation, p.generation, @operation
            FROM documents d
            {joins "@previous"}
            WHERE d.id = @id AND p.generation IS NOT NULL
              AND {currentOutputs}
            ON CONFLICT(document_id, generation) DO NOTHING"""

    /// Called only for a DAG-validated reembed, before invalidation, inside the
    /// same transaction as its generation bump and operation/stage audit rows.
    /// Resolving the immediately preceding generation makes repeated carries
    /// transitive without manufacturing a new stage_publications row.
    let carryForwardIn
        (scope: Algebra.TransactionScope)
        (generation: Generation.Token)
        (operationId: int64)
        =
        let parameters =
            [ "@id", Database.boxVal generation.DocumentId
              "@generation", Database.boxVal generation.Value
              "@previous", Database.boxVal (generation.Value - 1L)
              "@operation", Database.boxVal operationId ]
        task {
            let! _ = scope.execNonQuery carrySql parameters
            return ()
        }
