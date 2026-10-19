module Hermes.Tests.ReembedReadFixtures

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.HttpRegressionFixture
open Hermes.Tests.RetrievalHttpFixtures
open Hermes.Tests.RetrievalRaceFixtures

type Expected =
    { DocumentId: int64
      SavedPath: string
      Sha256: string
      Generation: int64
      PublicationGeneration: int64
      Canonical: string }

let private manualMetadata server documentId =
    executeSql server
        """UPDATE documents
           SET classification_tier='manual',category='tax',
               extracted_amount=42,extracted_vendor='Manual Synthetic'
           WHERE id=@id;
           INSERT INTO corrections(document_id,field,original_value,corrected_value)
           VALUES (@id,'extracted_amount','10','42');"""
        [ "@id", Database.boxVal documentId ]

let seedAt server generation name publication =
    task {
        let path = $"/archive/carried/{name}.png"
        let! id = seedComprehension server path canonical
        do! executeSql server
                "INSERT INTO document_generations(document_id,generation) VALUES (@id,@generation)"
                [ "@id", Database.boxVal id; "@generation", Database.boxVal generation ]
        do! persistCanonical server id generation publication
        do! manualMetadata server id
        server.Files.Put (path + ".extracted.md") "quartz synthetic payslip extraction"
        let expected: Expected =
            { DocumentId = id; SavedPath = path; Generation = generation
              PublicationGeneration = generation; Canonical = publication
              Sha256 = Encoding.UTF8.GetBytes("immutable synthetic source " + path) |> SourceFiles.hash }
        return expected
    }

let private assertSnapshot (expected: Expected) (body: JsonElement) =
    Assert.Equal("available", text "status" body)
    let source = body.GetProperty("source")
    Assert.Equal(expected.DocumentId, source.GetProperty("documentId").GetInt64())
    Assert.Equal(expected.SavedPath, text "savedPath" source)
    Assert.Equal(expected.Sha256, text "sha256" source)
    Assert.Equal(expected.Generation, body.GetProperty("generation").GetInt64())
    Assert.Equal(expected.PublicationGeneration, body.GetProperty("publicationGeneration").GetInt64())
    Assert.Equal((json expected.Canonical).GetRawText(), body.GetProperty("comprehension").GetRawText())
    let digest = expected.Canonical |> Encoding.UTF8.GetBytes |> SourceFiles.hash
    Assert.Equal(digest, text "publicationSha256" body)
    let metadata = body.GetProperty("effectiveMetadata")
    Assert.Equal("tax", text "category" metadata)
    Assert.Equal(42M, metadata.GetProperty("amount").GetDecimal())
    Assert.Equal("Manual Synthetic", text "vendor" metadata)
    Assert.Equal("unavailable", text "status" (body.GetProperty("enrichment")))

let verifyBoth server expected =
    task {
        let! before = checkpoint server
        let! http = get server $"/api/documents/{expected.DocumentId}/comprehension"
        let! mcp = mcpCall server "hermes_get_comprehension" (box {| document_id = expected.DocumentId |})
        assertSnapshot expected http
        assertSnapshot expected (payload mcp)
        Assert.Equal(http.GetRawText(), (payload mcp).GetRawText())
        let! after = checkpoint server
        Assert.Equal<Checkpoint>(before, after)
    }

let private embeddingDeps (server: Server) : Stages.Deps =
    let unused _ = Task.FromResult(Error "Extraction must not run during reembed")
    { Fs = server.Files.Fs; Db = server.Db; Logger = TestHelpers.silentLogger
      Clock = TestHelpers.defaultClock
      Extractor = { extractPdf = unused; extractImage = unused }
      Embedder = Some encoder; ChatProvider = None; TriageProvider = None
      ContentRules = []; ComprehensionPrompt = None; TriagePrompt = None
      Preferences = ""; ArchiveDir = "/archive" }

let completeEmbedding server =
    let dag =
        StagesV5.standardStages (embeddingDeps server)
        |> PipelineV5.buildDag |> Result.defaultWith failwith
    let logger = TestHelpers.silentLogger
    let gpu = PipelineV5.createGpuScheduler logger
    task {
        let! processed =
            PipelineV5.processStageForDag dag dag.Stages.["embed"] server.Db logger gpu
                10 (TimeSpan.FromMinutes 1.0) CancellationToken.None
        Assert.Equal(1, processed)
    }

let verifyPhysical path server expected =
    task {
        let! before = physicalHashes path
        do! verifyBoth server expected
        let! after = physicalHashes path
        Assert.Equal<Map<string, string>>(before, after)
    }

let withPhysicalServer verify =
    task {
        let directory = Path.Combine(Path.GetTempPath(), "hermes-carried-" + Guid.NewGuid().ToString("N"))
        Directory.CreateDirectory(directory) |> ignore
        let path = Path.Combine(directory, "synthetic.sqlite")
        try
            do! withServerUsing (fileDatabase path) None McpAccess.readOnly [] None (verify path)
        finally Directory.Delete(directory, true)
    }
