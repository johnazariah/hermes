module Hermes.Tests.SyntheticKeywordCorpus

open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core

type Entry =
    { DocumentId: int64
      SavedPath: string
      Marker: string }

let private sourceText shared index marker =
    if index % 3 = 1 then $"Synthetic unique evidence {marker}."
    else $"Synthetic {shared} evidence {marker}."

let private seedOne (db: Algebra.Database) (files: TestHelpers.MemFs) shared index =
    task {
        let marker = $"uniquecorpus{index:D4}marker"
        let path = $"/archive/synthetic/corpus-{index:D4}.md"
        let bytes = sourceText shared index marker |> Encoding.UTF8.GetBytes
        let subject = if index % 3 = 2 then "Unrelated synthetic metadata" else shared
        do! SourceFiles.writeNew db files.Fs path bytes
        let! value =
            db.execScalar
                """INSERT INTO documents(source_type,saved_path,category,sha256,original_name,subject)
                   VALUES ('email_body',@path,'tax',@sha,@name,@subject) RETURNING id"""
                [ "@path", Database.boxVal path; "@sha", Database.boxVal (SourceFiles.hash bytes)
                  "@name", Database.boxVal $"corpus-{index:D4}.md"; "@subject", Database.boxVal subject ]
        let entry: Entry =
            { DocumentId = Assert.IsType<int64>(value); SavedPath = path; Marker = marker }
        return entry
    }

let private includeEntry db files shared collected index =
    task {
        let! entry = seedOne db files shared index
        return entry :: collected
    }

let seed db files count shared =
    task {
        let! reversed = [ 1 .. count ] |> Prelude.foldTask (includeEntry db files shared) []
        return List.rev reversed
    }

let private rebuildBatch db (files: TestHelpers.MemFs) () (ids: int64 list) =
    task {
        Assert.InRange(ids.Length, 1, FileContentReindex.MaxDocuments)
        let! result = FileContentReindex.rebuild db files.Fs TestHelpers.silentLogger "/archive" ids
        let report = result |> Result.defaultWith failwith
        Assert.Equal<int64 list>(ids, report.Indexed)
        Assert.Empty(report.MetadataOnly)
    }

let rebuild db files entries =
    entries
    |> List.map (fun entry -> entry.DocumentId)
    |> List.chunkBySize FileContentReindex.MaxDocuments
    |> Prelude.foldTask (rebuildBatch db files) ()

let private includeHash (files: TestHelpers.MemFs) collected entry =
    task {
        let! bytes = files.Fs.readAllBytes entry.SavedPath
        return collected |> Map.add entry.SavedPath (SourceFiles.hash bytes)
    }

let hashes files entries =
    entries |> Prelude.foldTask (includeHash files) Map.empty
