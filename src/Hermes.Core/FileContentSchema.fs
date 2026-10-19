namespace Hermes.Core

/// Additive, rebuildable indexes. No source or workflow tables are replaced.
[<RequireQualifiedAccess>]
module internal FileContentSchema =

    let statements =
        [ """
          CREATE VIRTUAL TABLE IF NOT EXISTS document_content_fts USING fts5(
              text,
              kind UNINDEXED
          );
          """
          """
          CREATE TRIGGER IF NOT EXISTS content_fts_document_delete
          AFTER DELETE ON documents
          BEGIN
              DELETE FROM document_content_fts WHERE rowid = old.id;
          END;
          """
          // Replace the earlier schema16 trigger on already-initialized archives.
          // Core schema installs generations and the completion ledger first.
          "DROP TRIGGER IF EXISTS content_fts_source_change;"
          """
          CREATE TRIGGER content_fts_source_change
          AFTER UPDATE OF saved_path, sha256, source_type ON documents
          WHEN old.saved_path IS NOT new.saved_path
            OR old.sha256 IS NOT new.sha256
            OR old.source_type IS NOT new.source_type
          BEGIN
              INSERT INTO document_generations(document_id, generation, updated_at)
              VALUES (old.id, 1, datetime('now'))
              ON CONFLICT(document_id) DO UPDATE SET
                  generation = document_generations.generation + 1,
                  updated_at = datetime('now');
              DELETE FROM stage_completions WHERE document_id = old.id;
              DELETE FROM document_content_fts WHERE rowid = old.id;
              UPDATE documents SET stage = 'received' WHERE id = old.id;
          END;
          """
          """
          CREATE TRIGGER IF NOT EXISTS content_fts_extract_completion_delete
          AFTER DELETE ON stage_completions
          WHEN old.stage_name = 'extract'
          BEGIN
              DELETE FROM document_content_fts
              WHERE rowid = old.document_id AND kind = 'extraction';
          END;
          """ ]
