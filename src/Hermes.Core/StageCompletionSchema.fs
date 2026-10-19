namespace Hermes.Core

/// Shared prerequisite for pipeline scheduling and file-content invalidation.
[<RequireQualifiedAccess>]
module internal StageCompletionSchema =

    let statements =
        [| """
        CREATE TABLE IF NOT EXISTS stage_completions (
            document_id     INTEGER NOT NULL REFERENCES documents(id),
            stage_name      TEXT NOT NULL,
            completed_at    TEXT NOT NULL DEFAULT (datetime('now')),
            PRIMARY KEY (document_id, stage_name)
        );
        """
           "CREATE INDEX IF NOT EXISTS idx_stage_completions_stage ON stage_completions(stage_name);" |]
