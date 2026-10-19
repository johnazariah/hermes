namespace Hermes.Core

open System
open System.IO
open Microsoft.Data.Sqlite

/// Non-destructive guards: existing conflicting rows remain readable, not writable.
[<RequireQualifiedAccess>]
module SavedPathSchema =

    let statements =
        [ """
          CREATE TABLE IF NOT EXISTS archive_source_claims (
              ownership_key TEXT PRIMARY KEY,
              sha256 TEXT NOT NULL
          );
          """
          // Stop the obsolete retirement protocol without deleting any legacy
          // metadata, claims, documents, annotations, or source files.
          "DROP TRIGGER IF EXISTS doc_retiring_path_insert;"
          "DROP TRIGGER IF EXISTS doc_retiring_path_update;"
          """
          CREATE TRIGGER IF NOT EXISTS doc_saved_path_insert
          BEFORE INSERT ON documents
          BEGIN
              SELECT hermes_saved_path_key(new.saved_path);
              SELECT RAISE(ABORT, 'Saved path ownership conflict')
              WHERE EXISTS (
                  SELECT 1 FROM documents d
                  WHERE hermes_saved_path_key(d.saved_path) =
                        hermes_saved_path_key(new.saved_path)
              );
              SELECT RAISE(ABORT, 'Saved path source claim hash conflict')
              WHERE EXISTS (
                  SELECT 1 FROM archive_source_claims c
                  WHERE c.ownership_key = hermes_saved_path_key(new.saved_path)
                    AND c.sha256 <> lower(new.sha256)
              );
          END;
          """
          """
          CREATE TRIGGER IF NOT EXISTS doc_saved_path_update
          BEFORE UPDATE ON documents
          BEGIN
              SELECT hermes_saved_path_key(new.saved_path);
              SELECT RAISE(ABORT, 'Saved path ownership conflict')
              WHERE EXISTS (
                  SELECT 1 FROM documents d
                  WHERE d.id <> old.id
                    AND hermes_saved_path_key(d.saved_path) IN (
                        hermes_saved_path_key(old.saved_path),
                        hermes_saved_path_key(new.saved_path)
                    )
              );
              SELECT RAISE(ABORT, 'Saved path source claim hash conflict')
              WHERE EXISTS (
                  SELECT 1 FROM archive_source_claims c
                  WHERE c.ownership_key = hermes_saved_path_key(new.saved_path)
                    AND c.sha256 <> lower(new.sha256)
              );
          END;
          """
          """
          CREATE TRIGGER IF NOT EXISTS doc_saved_path_delete
          BEFORE DELETE ON documents
          BEGIN
              SELECT hermes_saved_path_key(old.saved_path);
              SELECT RAISE(ABORT, 'Saved path ownership conflict')
              WHERE EXISTS (
                  SELECT 1 FROM documents d
                  WHERE d.id <> old.id
                    AND hermes_saved_path_key(d.saved_path) =
                        hermes_saved_path_key(old.saved_path)
              );
          END;
          """ ]

    let rec private rejectLinks directory = function
        | [] -> Ok ()
        | segment :: remaining ->
            let next = Path.Combine(directory, segment)
            if String.IsNullOrEmpty(FileInfo(next).LinkTarget) then
                rejectLinks next remaining
            else
                Error $"Saved path traverses a symbolic link: {next}"

    let validateFile archiveDirectory fullPath =
        try
            let root = Path.GetFullPath(archiveDirectory)
            let relative = Path.GetRelativePath(root, fullPath)
            let separator = string Path.DirectorySeparatorChar
            if relative = "." then
                Error "Saved path must name an archive file"
            elif Path.IsPathRooted(relative) || relative = ".."
                 || relative.StartsWith(".." + separator, StringComparison.Ordinal) then
                Error "Saved path escapes the archive directory"
            else
                relative.Split(Path.DirectorySeparatorChar)
                |> Array.toList
                |> rejectLinks root
        with ex ->
            Error $"Saved path validation failed: {ex.Message}"

    let register (connection: SqliteConnection) key =
        connection.CreateFunction<string, string>(
            "hermes_saved_path_key",
            Func<string, string>(fun path -> key path |> Result.defaultWith invalidOp),
            isDeterministic = false)
