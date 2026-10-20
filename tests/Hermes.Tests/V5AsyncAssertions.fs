namespace Hermes.Tests

open System
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core

module V5AsyncState =
    module Fixture = V5AsyncFixtures
    module Control = V5AsyncSupport

    let private expectedSourceOwner: SourceFileAccess.Owner =
        { DocumentId = 1L
          Sha256 = Fixture.sha256 }

    let expectRows
        (db: Algebra.Database) (sql: string) (expected: Document.T list) : Task<unit> =
        task {
            let! actual = db.execReader sql []
            Assert.Equal<Document.T list>(expected, actual)
        }

    let count (db: Algebra.Database) (table: string) (expected: int64) : Task<unit> =
        task {
            let! actual = CoreRegressionSupport.count db table
            Assert.Equal<int64>(expected, actual)
        }

    let publicationCount
        (db: Algebra.Database) (kind: Control.Kind) (expected: int64) : Task<unit> =
        task {
            let! actual =
                db.execScalar "SELECT COUNT(*) FROM stage_publications WHERE stage_name=@stage"
                    [ "@stage", Database.boxVal (Control.name kind) ]
            Assert.Equal<int64>(expected, Assert.IsType<int64>(actual))
        }

    let source (fixture: Fixture.Fixture) (db: Algebra.Database) : Task<unit> =
        task {
            let! bytes = fixture.Store.Memory.Fs.readAllBytes Fixture.sourcePath
            Assert.True(Encoding.UTF8.GetBytes(Fixture.sourceText) = bytes)
            let! owners = SourceFileAccess.owners db Fixture.sourcePath
            Assert.Equal<SourceFileAccess.Owner list>(
                [ expectedSourceOwner ], owners)
            do! count db "archive_source_claims" 1L
            do! expectRows db "SELECT sha256 FROM archive_source_claims"
                    [ Map.ofList [ "sha256", Fixture.sha256 :> obj ] ]
        }

    let metadata (db: Algebra.Database) (kind: Control.Kind) : Task<unit> =
        task {
            let! document = CoreRegressionSupport.row db 1L
            Assert.Equal(Some Fixture.savedPath, Document.decode<string> "saved_path" document)
            Assert.Equal(Some Fixture.sha256, Document.decode<string> "sha256" document)
            Assert.Equal(Some 1L, Document.decode<int64> "starred" document)
            Assert.Equal(Some "owner-corrected", Document.decode<string> "extracted_vendor" document)
            Assert.Equal(Some 73.25, Document.decode<float> "extracted_amount" document)
            Assert.Equal(Some "2026-10-19", Document.decode<string> "extracted_date" document)
            do! count db "corrections" 3L
            do! count db "tags WHERE source='user'" (if kind = Control.Kind.Deep then 2L else 1L)
        }

    let index (db: Algebra.Database) : Task<unit> =
        task {
            do! count db "document_content_fts" 1L
            do! expectRows db
                    """SELECT text,kind FROM document_content_fts
                       WHERE document_content_fts MATCH 'suspensionmarker'"""
                    [ Map.ofList [ "text", Fixture.sourceText :> obj; "kind", "extraction" :> obj ] ]
            do! expectRows db
                    "SELECT extracted_date,extracted_amount,extracted_vendor,method,confidence FROM extraction"
                    [ Map.ofList
                        [ "extracted_date", "2026-10-20" :> obj
                          "extracted_amount", 64.0 :> obj; "extracted_vendor", "Async Workshop" :> obj
                          "method", "plaintext" :> obj; "confidence", 1.0 :> obj ] ]
        }

    let stable
        (fixture: Fixture.Fixture) (db: Algebra.Database) (kind: Control.Kind) : Task<unit> =
        task {
            do! source fixture db
            do! metadata db kind
            do! index db
        }

    let ledger
        (db: Algebra.Database) (stages: string list) (attempts: int64) : Task<unit> =
        task {
            let expected =
                stages |> List.sort
                |> List.map (fun stage -> Map.ofList [ "stage_name", stage :> obj ])
            do! expectRows db "SELECT stage_name FROM stage_completions ORDER BY stage_name" expected
            do! count db "pipeline_stage_attempts" attempts
        }

    let effective
        (db: Algebra.Database) (kind: Control.Kind) (completed: bool) : Task<unit> =
        task {
            let! document = CoreRegressionSupport.row db 1L
            let category, tier, confidence =
                match kind, completed with
                | Control.Kind.Deep, _ -> "owner-reviewed", Some "manual", Some 0.99
                | Control.Kind.Triage, true -> "unsorted", Some "triage_review", Some 0.6
                | Control.Kind.Triage, false -> "original", None, None
            Assert.Equal(Some category, Document.decode<string> "category" document)
            Assert.Equal(tier, Document.decode<string> "classification_tier" document)
            Assert.Equal(confidence, Document.decode<float> "classification_confidence" document)
        }

    let signals
        (db: Algebra.Database) (kind: Control.Kind) (present: bool) : Task<unit> =
        task {
            let one = if present then 1L else 0L
            do! count db "contacts" one
            do! count db "document_contacts" one
            do! count db "learned_pattern_evidence" one
            do! count db "learned_patterns" one
            do! count db "tags WHERE source='comprehension'" (2L * one)
            do! count db "suggestions"
                    (if present && kind = Control.Kind.Triage then 1L else 0L)
        }

    let revision (db: Algebra.Database) (expected: int64) : Task<unit> =
        task {
            let! actual = ArtifactRevision.current db (Fixture.folder ())
            Assert.Equal<int64>(expected, actual.Value)
        }

    let errorEvidence
        (db: Algebra.Database) (stage: string) (error: string) (dismissed: int64) : Task<unit> =
        task {
            do! expectRows db "SELECT stage,error,retry_count,dismissed FROM dead_letters"
                    [ Map.ofList
                        [ "stage", stage :> obj; "error", error :> obj
                          "retry_count", 0L :> obj; "dismissed", dismissed :> obj ] ]
            do! expectRows db "SELECT details FROM activity_log WHERE level='error'"
                    [ Map.ofList [ "details", error :> obj ] ]
        }

module V5AsyncAssertions =
    module Fixture = V5AsyncFixtures
    module Control = V5AsyncSupport
    module State = V5AsyncState
    module Suspension = AsyncCapabilityControl

    let private expectedEffectiveMetadata: ComprehensionContract.EffectiveMetadata =
        { Category = "owner-reviewed"
          Date = Some "2026-10-19"
          Amount = Some 73.25
          Vendor = Some "owner-corrected" }

    let private artifactCheckpoint
        (fixture: Fixture.Fixture) (control: Control.Control)
        (before: Fixture.Before) (phase: Control.Effect) (db: Algebra.Database) : Task<unit> =
        task {
            let output = phase = Control.Effect.Output
            let artifact = if output then Some (Fixture.expected control.Kind).RawJson else before.Artifact
            Assert.Equal(artifact, fixture.Store.Memory.Get Fixture.artifactPath)
            let advanced = output && control.Kind = Control.Kind.Triage
            do! State.revision db (before.Revision + if advanced then 1L else 0L)
        }

    let checkpoint
        (fixture: Fixture.Fixture) (control: Control.Control)
        (before: Fixture.Before) (phase: Control.Effect) (db: Algebra.Database) : Task<unit> =
        task {
            Assert.False((Control.gate control phase).EffectStarted)
            if phase <> Control.Effect.Provider then Assert.NotSame(fixture.Store.Db, db)
            do! State.stable fixture db control.Kind
            do! State.effective db control.Kind false
            do! State.ledger db (Control.prerequisites control.Kind) 1L
            do! State.count db (Control.table control.Kind) 0L
            do! State.publicationCount db control.Kind
                    (if phase = Control.Effect.Output then 1L else before.Publications)
            do! State.signals db control.Kind (phase = Control.Effect.Output)
            do! artifactCheckpoint fixture control before phase db
        }

    let release
        (fixture: Fixture.Fixture) (control: Control.Control) (before: Fixture.Before)
        (phase: Control.Effect) (pending: Task<int>) : Task<unit> =
        Suspension.release (Control.gate control phase) pending
            (checkpoint fixture control before phase)

    let drive
        (fixture: Fixture.Fixture) (control: Control.Control)
        (before: Fixture.Before) (pending: Task<int>) : Task<unit> =
        task {
            do! release fixture control before Control.Effect.Provider pending
            do! release fixture control before Control.Effect.Claim pending
            do! release fixture control before Control.Effect.Output pending
        }

    let canonical (fixture: Fixture.Fixture) (control: Control.Control) : Task<unit> =
        task {
            let expected = Fixture.expected control.Kind
            let! rows =
                fixture.Store.Db.execReader
                    "SELECT response_json,generation,current_category FROM stage_publications WHERE stage_name=@stage"
                    [ "@stage", Database.boxVal (Control.name control.Kind) ]
            let row = Assert.Single<Document.T>(rows)
            Assert.Equal(Some expected.RawJson, Document.decode<string> "response_json" row)
            Assert.Equal(Some 0L, Document.decode<int64> "generation" row)
            Assert.Equal(Some (if control.Kind = Control.Kind.Deep then "invoices" else "original"),
                         Document.decode<string> "current_category" row)
            Assert.Equal(Some expected.RawJson, fixture.Store.Memory.Get Fixture.artifactPath)
            let actualJson =
                Document.decode<string> "response_json" row
                |> Option.defaultWith (fun () -> failwith "Canonical JSON was missing")
            let actual = ComprehensionSchema.normaliseResponse actualJson |> Result.defaultWith failwith
            Assert.Equal<ComprehensionSchema.NormalisedResponse>(expected, actual)
        }

    let private learned (db: Algebra.Database) (kind: Control.Kind) : Task<unit> =
        let expected = Fixture.expected kind
        State.expectRows db
            "SELECT stage_name,generation,sender_domain,document_type,confidence FROM learned_pattern_evidence"
            [ Map.ofList
                [ "stage_name", Control.name kind :> obj; "generation", 0L :> obj
                  "sender_domain", "async.example.test" :> obj
                  "document_type", expected.DocumentType :> obj
                  "confidence", expected.Confidence :> obj ] ]

    let private contacts (db: Algebra.Database) (kind: Control.Kind) : Task<unit> =
        State.expectRows db
            """SELECT c.name,c.canonical_name,c.email,c.contact_type,dc.role
               FROM contacts c JOIN document_contacts dc ON dc.contact_id=c.id"""
            [ Map.ofList
                [ "name", "Async Workshop" :> obj; "canonical_name", "async workshop" :> obj
                  "email", "synthetic@async.example.test" :> obj; "role", "issuer" :> obj
                  "contact_type", (if kind = Control.Kind.Deep then "supplier" else "unknown") :> obj ] ]

    let private derived (db: Algebra.Database) (kind: Control.Kind) : Task<unit> =
        task {
            let expected = Fixture.expected kind
            do! State.expectRows db
                    $"SELECT document_type,category,confidence FROM {Control.table kind}"
                    [ Map.ofList
                        [ "document_type", expected.DocumentType :> obj
                          "category", expected.CanonicalCategory :> obj
                          "confidence", expected.Confidence :> obj ] ]
            do! State.signals db kind true
            do! learned db kind
            do! contacts db kind
            do! State.expectRows db "SELECT tag FROM tags WHERE source='comprehension' ORDER BY tag"
                    [ Map.ofList [ "tag", "async" :> obj ]; Map.ofList [ "tag", "owner-copy" :> obj ] ]
        }

    let private persisted (fixture: Fixture.Fixture) : Task<unit> =
        task {
            let! result =
                PersistedComprehension.read fixture.Store.Db fixture.Store.Memory.Fs
                    CoreRegressionSupport.archiveDir 1L
            let snapshot: ComprehensionContract.Snapshot =
                result |> Result.defaultWith (fun error -> failwith $"{error}")
            Assert.Equal(0L, snapshot.Generation)
            Assert.Equal(0L, snapshot.PublicationGeneration)
            Assert.Equal(Fixture.sha256, snapshot.Source.Sha256)
            Assert.Equal(Fixture.deepJson, snapshot.Canonical.GetRawText())
            Assert.Equal(Fixture.deepJson |> Encoding.UTF8.GetBytes |> SourceFiles.hash,
                         snapshot.PublicationSha256)
            Assert.Equal<ComprehensionContract.EffectiveMetadata>(
                expectedEffectiveMetadata, snapshot.Effective)
        }

    let published
        (fixture: Fixture.Fixture) (control: Control.Control) (before: Fixture.Before) : Task<unit> =
        task {
            do! State.stable fixture fixture.Store.Db control.Kind
            do! State.effective fixture.Store.Db control.Kind true
            do! State.ledger fixture.Store.Db
                    (Control.name control.Kind :: Control.prerequisites control.Kind) 0L
            do! canonical fixture control
            do! derived fixture.Store.Db control.Kind
            do! State.revision fixture.Store.Db (before.Revision + 1L)
            let! generation = Generation.current fixture.Store.Db 1L
            Assert.Equal(0L, generation.Value)
            if control.Kind = Control.Kind.Deep then do! persisted fixture
        }

module V5AsyncEmbeddingState =
    module Fixture = V5AsyncFixtures
    module Control = V5AsyncSupport
    module Encoder = V5AsyncSupport.Embedding
    module State = V5AsyncState
    module Suspension = AsyncCapabilityControl

    type Snapshot =
        { Chunks: Document.T list
          Output: Document.T list
          Projection: Document.T list
          Annotations: Document.T list
          Files: Map<string, string> }

    let capture (fixture: Fixture.Fixture) (db: Algebra.Database) : Task<Snapshot> =
        task {
            let! chunks = db.execReader
                            """SELECT id,document_id,chunk_index,chunk_text,
                                      hex(embedding) AS vector_hex,embedded_at
                               FROM document_chunks ORDER BY id""" []
            let! output = db.execReader "SELECT * FROM embedding ORDER BY document_id" []
            let! projection = db.execReader "SELECT id,embedded_at,chunk_count FROM documents ORDER BY id" []
            let! annotations = db.execReader
                                """SELECT 'correction' AS kind,field AS name,corrected_value AS value FROM corrections
                                   UNION ALL SELECT 'tag',tag,source FROM tags WHERE source='user'
                                   ORDER BY kind,name""" []
            return
                { Chunks = chunks; Output = output; Projection = projection; Annotations = annotations
                  Files = fixture.Store.Memory.Files |> Seq.map (fun pair -> pair.Key, pair.Value) |> Map.ofSeq }
        }

    let preserved
        (fixture: Fixture.Fixture) (before: Snapshot)
        (generation: int64) (db: Algebra.Database) : Task<unit> =
        task {
            let! actual = capture fixture db
            Assert.Equal<Map<string, string>>(before.Files, actual.Files)
            Assert.Equal<Document.T list>(before.Annotations, actual.Annotations)
            do! State.stable fixture db Control.Kind.Triage
            do! State.effective db Control.Kind.Triage false
            do! State.revision db 0L
            let! token = Generation.current db 1L
            Assert.Equal<int64>(generation, token.Value)
        }

    let unchanged
        (fixture: Fixture.Fixture) (before: Snapshot)
        (generation: int64) (db: Algebra.Database) : Task<unit> =
        task {
            let! actual = capture fixture db
            Assert.Equal<Snapshot>(before, actual)
            do! preserved fixture before generation db
        }

    let checkpoint
        (fixture: Fixture.Fixture) (before: Snapshot) (generation: int64)
        (attempts: int64) (completed: bool) (db: Algebra.Database) : Task<unit> =
        task {
            Assert.Same(fixture.Store.Db, db)
            do! unchanged fixture before generation db
            let stages = if completed then [ "extract"; "embed" ] else [ "extract" ]
            do! State.ledger db stages attempts
        }

    let private vectors
        (db: Algebra.Database) (generation: int64) (vector: float32 array) : Task<unit> =
        State.expectRows db
            """SELECT c.document_id,c.chunk_index,c.chunk_text,hex(c.embedding) AS vector_hex,
                      d.saved_path,d.sha256,COALESCE(g.generation,0) AS generation
               FROM document_chunks c JOIN documents d ON d.id=c.document_id
               LEFT JOIN document_generations g ON g.document_id=d.id"""
            [ Map.ofList
                [ "document_id", 1L :> obj; "chunk_index", 0L :> obj
                  "chunk_text", Fixture.sourceText :> obj
                  "vector_hex", Convert.ToHexString(Embeddings.embeddingToBlob vector) :> obj
                  "saved_path", Fixture.savedPath :> obj; "sha256", Fixture.sha256 :> obj
                  "generation", generation :> obj ] ]

    let published
        (fixture: Fixture.Fixture) (before: Snapshot)
        (generation: int64) (vector: float32 array) : Task<unit> =
        task {
            let db = fixture.Store.Db
            do! preserved fixture before generation db
            do! vectors db generation vector
            do! State.expectRows db "SELECT document_id,chunk_count FROM embedding"
                    [ Map.ofList [ "document_id", 1L :> obj; "chunk_count", 1L :> obj ] ]
            do! State.expectRows db "SELECT stage,chunk_count FROM documents"
                    [ Map.ofList [ "stage", "extracted" :> obj; "chunk_count", 1L :> obj ] ]
            do! State.count db "document_chunks WHERE embedded_at IS NOT NULL" 1L
            do! State.count db "embedding WHERE embedded_at IS NOT NULL" 1L
            do! State.count db "documents WHERE embedded_at IS NOT NULL" 1L
            do! State.ledger db [ "extract"; "embed" ] 0L
        }

    let private expectedHit: Search.SearchResult =
        { DocumentId = 1L; SavedPath = Fixture.savedPath; OriginalName = Some "doc-1.txt"
          Category = "original"; Sender = Some "synthetic@async.example.test"
          Subject = None; EmailDate = None; ExtractedVendor = Some "owner-corrected"
          ExtractedAmount = Some 73.25; RelevanceScore = 1.0
          Snippet = Some Fixture.sourceText; ResultType = "document" }

    let private expectedSearch: SearchContract.Success =
        { Mode = SearchContract.Semantic; Results = [ expectedHit ]
          Coverage = Some { IndexedDocuments = 1; EligibleDocuments = 1 } }

    let private expectedResult (visible: bool) : Result<SearchContract.Success, SearchContract.Failure> =
        if visible then Ok expectedSearch
        else Error(SearchContract.Unavailable SearchContract.IndexUnavailable)

    let search
        (fixture: Fixture.Fixture) (generation: int64)
        (vector: float32 array) (visible: bool) : Task<unit> =
        task {
            let! before = capture fixture fixture.Store.Db
            let control = Encoder.create "suspensionmarker" (Ok vector)
            let request: SearchContract.Request =
                { Mode = SearchContract.Semantic
                  Filter = { Search.defaultFilter control.Input with Limit = 1 } }
            let! actual =
                Suspension.exercise (Encoder.gates control)
                    (fun () -> SearchQuery.execute fixture.Store.Db
                                    (Some (Encoder.client fixture.Store.Db control)) request)
                    (fun pending -> Encoder.drive control pending
                                        (checkpoint fixture before generation 0L visible))
            Encoder.assertUsed control
            Assert.Equal<Result<SearchContract.Success, SearchContract.Failure>>(expectedResult visible, actual)
        }
