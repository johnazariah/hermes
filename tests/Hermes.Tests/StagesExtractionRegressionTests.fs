module Hermes.Tests.StagesExtractionRegressionTests

open System.Collections.Concurrent
open System.IO
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.CoreRegressionSupport

let private savedImage = Path.Combine("local", "stage-source.png")
let private fullImage = Path.Combine(archiveDir, savedImage)
let private sourceText = "synthetic archived image fixture"
let private sourceBytes = Encoding.UTF8.GetBytes sourceText
let private extractedText = "Vendor: Stage Fixture\nPublic stage extraction regression marker"
let private generation = 7L

let private seedImage (fixture: Fixture) : Task<Document.T> =
    task {
        fullImage |> Path.GetDirectoryName |> Option.ofObj |> Option.iter fixture.Memory.Fs.createDirectory
        fixture.Memory.Put fullImage sourceText
        let! _ =
            fixture.Db.execNonQuery
                """INSERT INTO documents(id,source_type,saved_path,category,sha256)
                   VALUES (1,'manual_drop',@path,'original',@sha);
                   INSERT INTO document_generations(document_id,generation) VALUES (1,@generation);"""
                [ "@path", Database.boxVal savedImage
                  "@sha", Database.boxVal (SourceFiles.hash sourceBytes)
                  "@generation", Database.boxVal generation ]
        return! row fixture.Db 1L
    }

let private dependencies (fixture: Fixture) (observation: Probe)
    (calls: ConcurrentQueue<byte array>) : Stages.Deps =
    { Fs = fixture.Memory.Fs; Db = fixture.Db; Logger = observation.Logger
      Clock = TestHelpers.defaultClock
      Extractor =
        { Interpreters.nullTextExtractor with
            extractImage = fun bytes ->
                calls.Enqueue bytes
                Task.FromResult(Ok extractedText) }
      Embedder = None; ChatProvider = None; TriageProvider = None
      ContentRules = []; ComprehensionPrompt = None; TriagePrompt = None
      Preferences = ""; ArchiveDir = archiveDir }

let private assertOutput (fixture: Fixture) (document: Document.T) =
    Assert.Equal(1L, Document.id document)
    Assert.Equal("extracted", Document.stage document)
    Assert.Equal<string option>(Some "Stage Fixture", Document.decode<string> "extracted_vendor" document)
    Assert.Equal<string option>(Some "ollama_vision", Document.decode<string> "extraction_method" document)
    Assert.Equal<float option>(Some 0.8, Document.decode<float> "ocr_confidence" document)
    Assert.Equal<string option>(Some stamp, Document.decode<string> "extracted_at" document)
    Assert.Equal<string option>(Some sourceText, fixture.Memory.Get fullImage)
    Assert.Equal<string option>(Some extractedText, fixture.Memory.Get (fullImage + ".extracted.md"))

let private assertPublication (fixture: Fixture) (original: Document.T) : Task<unit> =
    task {
        let! persisted = row fixture.Db 1L
        let! current = Generation.current fixture.Db 1L
        let! completions = count fixture.Db "stage_completions"
        let! indexed = fixture.Db.execReader "SELECT rowid,text,kind FROM document_content_fts" []
        let expected : Document.T =
            Map.ofList [ "rowid", Database.boxVal 1L; "text", Database.boxVal extractedText; "kind", Database.boxVal "extraction" ]
        Assert.Equal(1, List.length indexed)
        Assert.True((indexed = [ expected ]), "Expected the exact extraction FTS row")
        Assert.Equal<Document.T>(original, persisted)
        Assert.Equal(generation, current.Value)
        Assert.Equal(0L, completions)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Public extract uses the current generation and publishes image extraction without persisting the document`` () =
    task {
        use fixture = fixture ()
        let! original = seedImage fixture
        let observation = probe "unexpected extract-stage log"
        let calls = ConcurrentQueue<byte array>()
        let deps = dependencies fixture observation calls
        let extraction = Stages.extract deps original
        let! enriched = extraction
        assertOutput fixture enriched
        Assert.Equal<byte>(sourceBytes, Assert.Single<byte array>(calls))
        Assert.Empty(observation.Events)
        do! assertPublication fixture original
    }
