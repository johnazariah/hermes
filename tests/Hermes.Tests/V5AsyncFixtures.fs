module Hermes.Tests.V5AsyncFixtures

open System
open System.IO
open System.Text
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

module Control = Hermes.Tests.V5AsyncSupport
module CoreFixture = Hermes.Tests.CoreRegressionSupport

type Fixture =
    { Store: CoreFixture.Fixture
      Observation: CoreFixture.Probe
      Gpu: PipelineV5.GpuScheduler }
    interface IDisposable with
        member this.Dispose() = (this.Store :> IDisposable).Dispose()

type Before =
    { Publications: int64
      Artifact: string option
      Revision: int64 }

let sourceText: string =
    [ "Synthetic owner document suspensionmarker"
      "Vendor: Async Workshop"
      "Date: 2026-10-20"
      "Invoice total: $64.00"
      "This document and every identifier in it are synthetic." ]
    |> String.concat "\n"

let financeJson: string =
    """{"document_type":"invoice","confidence":0.95,"summary":"Synthetic invoice requires deep comprehension.","fields":{},"tags":["draft"]}"""

let triageJson: string =
    """{"document_type":"letter","confidence":0.6,"summary":"Synthetic owner correspondence.","fields":{"sender_name":"Async Workshop","amount":64.00},"tags":["async","owner-copy"]}"""

let deepJson: string =
    """{"document_type":"invoice","confidence":0.92,"summary":"Synthetic workshop invoice.","fields":{"sender_name":"Async Workshop","amount":64.00,"date":"2026-10-20"},"tags":["async","owner-copy"]}"""

let conflictingJson: string =
    """{"document_type":"payslip","confidence":0.99,"summary":"This retry must not replace the durable invoice.","fields":{"sender_name":"Different Employer","amount":999.00},"tags":["wrong-retry"]}"""

let expected (kind: Control.Kind) : ComprehensionSchema.NormalisedResponse =
    match kind with
    | Control.Kind.Triage ->
        { DocumentType = "letter"; CanonicalCategory = "unsorted"
          Confidence = 0.6; Summary = "Synthetic owner correspondence."
          Tags = [ "async"; "owner-copy" ]; RawJson = triageJson }
    | Control.Kind.Deep ->
        { DocumentType = "invoice"; CanonicalCategory = "invoices"
          Confidence = 0.92; Summary = "Synthetic workshop invoice."
          Tags = [ "async"; "owner-copy" ]; RawJson = deepJson }

let create () : Fixture =
    let observation = CoreFixture.probe "V5 async observation"
    { Store = CoreFixture.fixture (); Observation = observation
      Gpu = PipelineV5.createGpuScheduler observation.Logger }

let sourcePath: string = CoreFixture.sourcePath 1L
let savedPath: string = CoreFixture.savedPath 1L
let folderPath: string = Path.GetDirectoryName(sourcePath) |> Option.ofObj |> Option.defaultValue ""
let artifactPath: string = Path.Combine(folderPath, Control.artifactName)
let sha256: string = sourceText |> Encoding.UTF8.GetBytes |> SourceFiles.hash

let folder () : PublicationFence.ArtifactFolder =
    PublicationFence.ArtifactFolder.tryFromMetadata CoreFixture.archiveDir savedPath None
    |> Option.defaultWith (fun () -> failwith "Synthetic artifact folder did not resolve")

let dependencies
    (fixture: Fixture) (db: Algebra.Database) (fs: Algebra.FileSystem)
    (provider: Algebra.ChatProvider) : Stages.Deps =
    { Fs = fs; Db = db; Logger = fixture.Observation.Logger
      Clock = Interpreters.systemClock
      Extractor =
        { extractPdf = fun _ -> invalidOp "Unexpected PDF extraction"
          extractImage = fun _ -> invalidOp "Unexpected image extraction" }
      Embedder = None; ChatProvider = Some provider; TriageProvider = Some provider
      ContentRules = []; ComprehensionPrompt = None; TriagePrompt = None
      Preferences = Control.preferences; ArchiveDir = CoreFixture.archiveDir }

let definitions
    (fixture: Fixture) (control: Control.Control) : PipelineV5.StageDefinition list =
    let database = Control.database control fixture.Store.Db
    let provider = Control.provider control fixture.Store.Db sourceText
    dependencies fixture database (Control.files control fixture.Store.Memory.Fs) provider
    |> StagesV5.standardStages

let dag (definitions: PipelineV5.StageDefinition list) : PipelineV5.Dag =
    definitions |> PipelineV5.buildDag |> Result.defaultWith failwith

let execute
    (fixture: Fixture) (db: Algebra.Database) (definitions: PipelineV5.StageDefinition list)
    (stageName: string) (gpu: PipelineV5.GpuScheduler) (ct: CancellationToken) : Task<int> =
    let graph = dag definitions
    PipelineV5.processStageForDag
        graph graph.Stages[stageName] db fixture.Observation.Logger gpu
        2 Hermes.Tests.AsyncCapabilityControl.timeout ct

let start
    (fixture: Fixture) (control: Control.Control)
    (gpu: PipelineV5.GpuScheduler) (ct: CancellationToken) : Task<int> =
    execute fixture (Control.database control fixture.Store.Db)
        (definitions fixture control) (Control.name control.Kind) gpu ct

let private initialDefinitions (fixture: Fixture) : PipelineV5.StageDefinition list =
    let provider : Algebra.ChatProvider =
        { complete = fun _ _ -> Task.FromResult(Ok financeJson) }
    dependencies fixture fixture.Store.Db fixture.Store.Memory.Fs provider
    |> StagesV5.standardStages

let private seed (fixture: Fixture) : Task<unit> =
    task {
        do! SourceFiles.writeNew fixture.Store.Db fixture.Store.Memory.Fs sourcePath
                (Encoding.UTF8.GetBytes sourceText)
        let! _ =
            fixture.Store.Db.execNonQuery
                """INSERT INTO documents
                     (id,source_type,original_name,saved_path,category,sha256,sender,starred)
                   VALUES (1,'manual_drop','doc-1.txt',@path,'original',@sha,
                           'synthetic@async.example.test',1)"""
                [ "@path", Database.boxVal savedPath; "@sha", Database.boxVal sha256 ]
        return ()
    }

let private correctMetadata (fixture: Fixture) : Task<int> =
    fixture.Store.Db.execNonQuery
        """UPDATE documents SET extracted_vendor='owner-corrected',
                  extracted_amount=73.25, extracted_date='2026-10-19' WHERE id=1;
           INSERT INTO corrections(document_id,field,corrected_value) VALUES
                  (1,'extracted_vendor','owner-corrected'),
                  (1,'extracted_amount','73.25'),
                  (1,'extracted_date','2026-10-19');
           INSERT INTO tags(document_id,tag,source) VALUES (1,'owner-pinned','user');""" []

let private prepareDeep
    (fixture: Fixture) (definitions: PipelineV5.StageDefinition list) : Task<unit> =
    task {
        let! completed =
            execute fixture fixture.Store.Db definitions "triage" fixture.Gpu CancellationToken.None
        Assert.Equal(1, completed)
        let! _ =
            fixture.Store.Db.execNonQuery
                """UPDATE documents SET category='owner-reviewed',
                          classification_tier='manual',classification_confidence=0.99
                   WHERE id=1""" []
        return ()
    }

let arrange (fixture: Fixture) (kind: Control.Kind) : Task<unit> =
    task {
        let stages = initialDefinitions fixture
        do! PipelineV5.initSchema fixture.Store.Db stages
        do! seed fixture
        let! extracted =
            execute fixture fixture.Store.Db stages "extract" fixture.Gpu CancellationToken.None
        Assert.Equal(1, extracted)
        let! _ = correctMetadata fixture
        if kind = Control.Kind.Deep then do! prepareDeep fixture stages
    }

let before (fixture: Fixture) (control: Control.Control) : Task<Before> =
    task {
        let! publications =
            fixture.Store.Db.execScalar
                "SELECT COUNT(*) FROM stage_publications WHERE stage_name=@stage"
                [ "@stage", Database.boxVal (Control.name control.Kind) ]
        let! revision = ArtifactRevision.current fixture.Store.Db (folder ())
        return
            { Publications = Assert.IsType<int64>(publications)
              Artifact = fixture.Store.Memory.Get artifactPath
              Revision = revision.Value }
    }

module Embedding =
    module Encoder = Hermes.Tests.V5AsyncSupport.Embedding

    let private stageDependencies
        (fixture: Fixture) (control: Encoder.Control) : Stages.Deps =
        let unused: Algebra.ChatProvider =
            { complete = fun _ _ -> invalidOp "Embedding must not invoke chat" }
        let source = dependencies fixture fixture.Store.Db fixture.Store.Memory.Fs unused
        { source with Embedder = Some (Encoder.client fixture.Store.Db control) }

    let definitions
        (fixture: Fixture) (control: Encoder.Control) : PipelineV5.StageDefinition list =
        stageDependencies fixture control |> StagesV5.standardStages

    let start (fixture: Fixture) (control: Encoder.Control) : Task<int> =
        execute fixture fixture.Store.Db (definitions fixture control)
            "embed" fixture.Gpu CancellationToken.None

    // Public stage publication without pretending that framework settlement ran.
    let publishOnly
        (fixture: Fixture) (control: Encoder.Control) : Task<PipelineV5.StageOutcome> =
        StagesV5.embed (stageDependencies fixture control)
            fixture.Store.Db fixture.Observation.Logger 1L
