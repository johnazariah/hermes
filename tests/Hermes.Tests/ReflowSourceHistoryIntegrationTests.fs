module Hermes.Tests.ReflowSourceHistoryIntegrationTests

open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailHistoryFixtures

let private dependencies (fixture: Fixture) : Stages.Deps =
    { Fs = fixture.Files.Fs; Db = fixture.Database
      Logger = fixture.Logger; Clock = fixture.Clock
      Extractor = Interpreters.nullTextExtractor; Embedder = None
      ChatProvider = None; TriageProvider = None; ContentRules = []
      ComprehensionPrompt = None; TriagePrompt = None
      Preferences = ""; ArchiveDir = fixture.Config.ArchiveDir }

let private completed (db: Algebra.Database) documentId =
    task {
        let! rows =
            db.execReader
                "SELECT stage_name FROM stage_completions WHERE document_id = @id"
                [ "@id", Database.boxVal documentId ]
        return
            rows
            |> List.map (fun row -> (Prelude.RowReader row).String "stage_name" "")
            |> Set.ofList
    }

let private classifyRecent (fixture: Fixture) (history: History) =
    task {
        let id = Document.id history.Document
        let! before = Generation.current fixture.Database id
        let! result =
            DocumentManagement.reclassifyDetailed
                fixture.Database fixture.Files.Fs fixture.Config.ArchiveDir id "reviewed"
        let outcome = result |> Result.defaultWith (Reclassification.describeError >> failwith)
        Assert.Equal(required "saved_path" history.Document, outcome.SavedPath)
        Assert.Equal(required "sha256" history.Document, outcome.Sha256)
        let! after = Generation.current fixture.Database id
        Assert.Equal<Generation.Token>(before, after)
        let! ledger = completed fixture.Database id
        Assert.Equal<Set<string>>(set [ "extract"; "triage"; "deep-comprehend"; "embed" ], ledger)
    }

let private recomprehend (fixture: Fixture) documentId =
    task {
        let! result =
            Reflow.request fixture.Database fixture.Logger
                (TestHelpers.standardV5Dag ()) documentId Reflow.Recomprehend Reflow.Apply
        let accepted = result |> Result.defaultWith failwith
        Assert.Equal<string list>([ "triage"; "deep-comprehend" ], accepted.Plan.InvalidatedStages)
        let! ledger = completed fixture.Database documentId
        Assert.Equal<Set<string>>(set [ "extract"; "embed" ], ledger)
    }

let private assertRecentSourceAndUserTags (fixture: Fixture) (history: History) =
    task {
        let id = Document.id history.Document
        history.Files
        |> Map.iter (fun path content -> Assert.Equal(Some content, fixture.Files.Get path))
        let! rows =
            fixture.Database.execReader
                "SELECT saved_path, sha256, category FROM documents WHERE id = @id"
                [ "@id", Database.boxVal id ]
        let current = rows |> List.exactlyOne
        Assert.Equal(required "saved_path" history.Document, required "saved_path" current)
        Assert.Equal(required "sha256" history.Document, required "sha256" current)
        Assert.Equal("reviewed", required "category" current)
        let! tags =
            fixture.Database.execScalar
                "SELECT COUNT(*) FROM tags WHERE document_id = @id AND source = 'user'"
                [ "@id", Database.boxVal id ]
        Assert.Equal(2L, unbox<int64> tags)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``New message reclassification and reflow preserve the earlier processed body and user state`` () =
    withFixture (fun fixture ->
        task {
            let pair = prefixPair ()
            let! earlier = seedProcessedBody fixture pair.Previous
            let! recent = seedProcessedBody fixture pair.Replacement
            Assert.NotEqual<string>(required "saved_path" earlier.Document, required "saved_path" recent.Document)
            do! classifyRecent fixture recent
            do! recomprehend fixture (Document.id recent.Document)
            do! assertPreserved fixture earlier
            do! assertRecentSourceAndUserTags fixture recent
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Fenced extraction cannot overwrite an artifact path claimed as a source`` () =
    withFixture (fun fixture ->
        task {
            let! history = seedProcessedBody fixture (prefixPair ()).Previous
            let path = fullPath fixture (required "saved_path" history.Document) + ".extracted.md"
            let content = history.Files[path]
            do! SourceFiles.writeNew fixture.Database fixture.Files.Fs path
                    (Encoding.UTF8.GetBytes content)
            let! outcome =
                StagesV5.extract (dependencies fixture) fixture.Database fixture.Logger
                    (Document.id history.Document)
            match outcome with
            | PipelineV5.Failed error -> Assert.Contains("ownership conflict", error)
            | other -> failwith $"Expected guarded publication failure, got {other}"
            do! assertPreserved fixture history
        })

let private generatedTags (fixture: Fixture) documentId =
    task {
        let! value =
            fixture.Database.execScalar
                """SELECT COUNT(*) FROM tags WHERE document_id = @id
                   AND source = 'classifier' AND created_by = 'reclassification'"""
                [ "@id", Database.boxVal documentId ]
        return unbox<int64> value
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Recomprehend clears generated classifier category tags but retains user annotations`` () =
    withFixture (fun fixture ->
        task {
            let! history = seedProcessedBody fixture (prefixPair ()).Previous
            let id = Document.id history.Document
            let! result =
                Reclassification.reclassifyFromContent fixture.Database fixture.Files.Fs
                    fixture.Config.ArchiveDir id "automated" 0.9
            Assert.True(Result.isOk result)
            let! before = generatedTags fixture id
            Assert.Equal(1L, before)
            do! recomprehend fixture id
            let! after = generatedTags fixture id
            Assert.Equal(0L, after)
            Assert.Equal(Some history.Files[fullPath fixture (required "saved_path" history.Document)],
                         fixture.Files.Get(fullPath fixture (required "saved_path" history.Document)))
        })
