namespace Hermes.Tests

open System
open System.Text
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.AsyncCapabilityControl
open Hermes.Tests.AsyncEmailConformanceSupport
open Hermes.Tests.AsyncEmailFixture
open Hermes.Tests.AsyncEmailAssertions
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

module AsyncEmailConformanceFlow =
    let controls (input: Input) : Plan =
        AsyncEmailConformanceSupport.plan
            [ input.Incoming.ProviderId; input.Sibling.ProviderId ] input.File.FileName

    let expect (gate: Gate) entered effected =
        Assert.Equal(entered, gate.WasEntered)
        Assert.Equal(effected, gate.EffectStarted)

    let successDriver
        (fixture: Fixture) (input: Input) (control: Plan) (siblingAlready: bool)
        (baseline: Baseline) (reader: ChannelReader<Document.T>)
        (pending: Task<EmailSync.SyncResult>) : Task<unit> =
        task {
            do! drive (prefix control input.Incoming.ProviderId) pending (before fixture input baseline reader)
            do! release control.Store pending (pendingStore fixture input siblingAlready reader)
            if not siblingAlready then
                do! drive (sibling control input.Sibling.ProviderId) pending (between fixture input 3L 2 reader)
            do! release control.Watermark pending
                    (between fixture input 4L (if siblingAlready then 2 else 3) reader)
        }

    let success (fixture: Fixture) (input: Input) (siblingAlready: bool) : Task<RunResult> =
        task {
            let control = controls input
            let initial = AsyncEmailFixture.baseline fixture (if siblingAlready then 2L else 1L) input.Initial
            let! result =
                AsyncEmailConformanceSupport.run fixture control (feed input |> since input.Initial)
                    (successDriver fixture input control siblingAlready initial)
            prefix control input.Incoming.ProviderId |> List.iter (fun gate -> expect gate true true)
            sibling control input.Sibling.ProviderId
            |> List.iter (fun gate -> expect gate (not siblingAlready) (not siblingAlready))
            expect control.Store true true
            expect control.Watermark true true
            return result
        }

    let failureDriver
        (fixture: Fixture) (input: Input) (control: Plan)
        (baseline: Baseline) (reader: ChannelReader<Document.T>)
        (pending: Task<EmailSync.SyncResult>) : Task<unit> =
        task {
            do! drive (prefix control input.Incoming.ProviderId) pending (before fixture input baseline reader)
            do! release control.Store pending (pendingStore fixture input false reader)
            do! drive (sibling control input.Sibling.ProviderId) pending (between fixture input 1L 0 reader)
        }

    let quietDriver
        (fixture: Fixture) (input: Input) (gates: Gate list) (control: Plan)
        (baseline: Baseline) (reader: ChannelReader<Document.T>)
        (pending: Task<EmailSync.SyncResult>) : Task<unit> =
        task {
            do! drive gates pending (before fixture input baseline reader)
            do! release control.Watermark pending (before fixture input baseline reader)
        }

    let replay (fixture: Fixture) (input: Input) : Task<unit> =
        task {
            let control = controls input
            let saved = AsyncEmailFixture.baseline fixture 4L (fixture.Clock.utcNow ())
            let! result =
                AsyncEmailConformanceSupport.run fixture control (feed input |> since saved.Watermark)
                    (quietDriver fixture input [ control.Load; control.Page ] control saved)
            assertSuccess 0 0 result
            Assert.Equal(2, result.Summary.DuplicatesSkipped)
            (prefix control input.Incoming.ProviderId |> List.skip 2)
            @ sibling control input.Sibling.ProviderId
            |> List.iter (fun gate -> expect gate false false)
            expect control.Store false false
            Assert.Equal<Map<string, string>>(saved.Files, snapshot fixture)
            do! finished fixture input result
        }

    let duplicateContent (fixture: Fixture) (input: Input) : Task<unit> =
        task {
            let duplicate = { input.Incoming with ProviderId = "async-content-duplicate"; ThreadId = "duplicate-thread"; BodyText = None }
            let control = AsyncEmailConformanceSupport.plan [ duplicate.ProviderId ] input.File.FileName
            let saved = AsyncEmailFixture.baseline fixture 4L (fixture.Clock.utcNow ())
            let source = single duplicate [ input.File ] |> since saved.Watermark
            let! result =
                AsyncEmailConformanceSupport.run fixture control source
                    (quietDriver fixture input (prefix control duplicate.ProviderId) control saved)
            assertSuccess 1 0 result
            expect control.Store false false
            Assert.Equal<Map<string, string>>(saved.Files, snapshot fixture)
            do! assertAcknowledged fixture duplicate.ProviderId 1L
            do! finished fixture input result
        }

module AsyncEmailConformanceTests =
    open AsyncEmailConformanceFlow

    [<Fact>]
    [<Trait("Category", "Integration")>]
    let ``Pending provider and database effects publish exact sources once and preserve processed history`` () =
        withFixture (fun fixture ->
            task {
                let! input = arrange fixture
                let! result = success fixture input false
                assertSuccess 2 3 result
                do! finished fixture input result
                do! replay fixture input
                do! duplicateContent fixture input
            })

    let private storageRetry (fixture: Fixture) : Task<unit> =
        task {
            let! input = arrange fixture
            do! rejectAttachment fixture
            let control = controls input
            let saved = AsyncEmailFixture.baseline fixture 1L input.Initial
            let! failed = AsyncEmailConformanceSupport.run fixture control (feed input |> since input.Initial)
                                (failureDriver fixture input control saved)
            expect control.Store true true
            expect control.Watermark false false
            do! rejected fixture input storageError failed
            do! totals fixture 2L 2L
            do! allowAttachment fixture
            let! retried = success fixture input true
            assertSuccess 1 2 retried
            Assert.Equal(1, retried.Summary.DuplicatesSkipped)
            do! finished fixture input retried
        }

    [<Fact>]
    [<Trait("Category", "Integration")>]
    let ``A faulting INSERT after suspension rolls back the whole email but not its later sibling`` () =
        withFixture storageRetry

    let private providerFaultDriver
        (fixture: Fixture) (input: Input) (control: Plan) (boundary: int)
        (saved: Baseline) (reader: ChannelReader<Document.T>)
        (pending: Task<EmailSync.SyncResult>) : Task<unit> =
        task {
            let steps = prefix control input.Incoming.ProviderId
            do! drive (steps |> List.take boundary) pending (before fixture input saved reader)
            let gate = steps |> List.item boundary
            let! database = gate.Await(pending :> Task)
            do! before fixture input saved reader database
            gate.Fail(InvalidOperationException(providerError))
            if boundary > 1 then
                do! drive (sibling control input.Sibling.ProviderId) pending (between fixture input 1L 0 reader)
        }

    let private assertProviderFault
        (fixture: Fixture) (input: Input) (control: Plan)
        (boundary: int) (result: RunResult) : Task<unit> =
        task {
            prefix control input.Incoming.ProviderId
            |> List.iteri (fun index gate -> expect gate (index <= boundary) (index < boundary))
            expect control.Store false false
            expect control.Watermark false false
            if boundary = 1 then
                assertStorageFailure result
                Assert.Contains(providerError, Assert.Single<string>(result.Summary.Errors))
                do! state fixture input fixture.Database 1L
                do! totals fixture 1L 1L
                do! watermark fixture input.Initial
            else
                do! rejected fixture input providerError result
                do! totals fixture 2L 2L
        }

    // Prefix positions: 0 = DB watermark read; 1/2/3 = list/get/attachments.
    [<Theory>]
    [<InlineData(1)>]
    [<InlineData(2)>]
    [<InlineData(3)>]
    [<Trait("Category", "Integration")>]
    let ``Provider faults after genuine suspension retain retryable source state`` (boundary: int) =
        withFixture (fun fixture ->
            task {
                let! input = arrange fixture
                let control = controls input
                let saved = AsyncEmailFixture.baseline fixture 1L input.Initial
                let! failed = AsyncEmailConformanceSupport.run fixture control (feed input |> since input.Initial)
                                    (providerFaultDriver fixture input control boundary saved)
                do! assertProviderFault fixture input control boundary failed
                let! retried = success fixture input (boundary > 1)
                assertSuccess (if boundary > 1 then 1 else 2) (if boundary > 1 then 2 else 3) retried
                Assert.Equal((if boundary > 1 then 1 else 0), retried.Summary.DuplicatesSkipped)
                do! finished fixture input retried
            })

    let private plantOwner (fixture: Fixture) (input: Input) : Task<unit> =
        task {
            let path = attachmentPath fixture input.Incoming input.File
            do! SourceFiles.writeNew fixture.Database fixture.Files.Fs path (Encoding.UTF8.GetBytes(ownerText))
            let! _ = seedOwner fixture path ownerText
            return ()
        }

    let private collisionDriver
        (fixture: Fixture) (input: Input) (control: Plan)
        (saved: Baseline) (reader: ChannelReader<Document.T>)
        (pending: Task<EmailSync.SyncResult>) : Task<unit> =
        task {
            do! drive (prefix control input.Incoming.ProviderId |> List.take 3) pending
                    (before fixture input saved reader)
            let! database = control.Attachments[input.Incoming.ProviderId].Await(pending :> Task)
            do! before fixture input saved reader database
            do! plantOwner fixture input
            control.Attachments[input.Incoming.ProviderId].Release()
            do! drive (sibling control input.Sibling.ProviderId) pending (between fixture input 2L 0 reader)
        }

    let private assertOwner (fixture: Fixture) (input: Input) : Task<unit> =
        task {
            let path = attachmentPath fixture input.Incoming input.File
            let! rows = fixture.Database.execReader
                            "SELECT * FROM documents WHERE hermes_saved_path_key(saved_path) = hermes_saved_path_key(@path)"
                            [ "@path", Database.boxVal path ]
            let document = Assert.Single<Map<string, obj>>(rows)
            Assert.Equal("manual_drop", required "source_type" document)
            Assert.Equal("prior-category", required "category" document)
            Assert.Equal(SourceFiles.hash (Encoding.UTF8.GetBytes(ownerText)), required "sha256" document)
            do! assertRegistered fixture document
            let! bytes = fixture.Files.Fs.readAllBytes path
            Assert.True(Encoding.UTF8.GetBytes(ownerText) = bytes)
        }

    [<Fact>]
    [<Trait("Category", "Integration")>]
    let ``An owner arriving during provider suspension wins the canonical path without losing sibling work`` () =
        withFixture (fun fixture ->
            task {
                let! input = arrange fixture
                let control = controls input
                let saved = AsyncEmailFixture.baseline fixture 1L input.Initial
                let! result = AsyncEmailConformanceSupport.run fixture control (feed input |> since input.Initial)
                                    (collisionDriver fixture input control saved)
                expect control.Store false false
                expect control.Watermark false false
                do! rejected fixture input "ownership conflict" result
                do! assertOwner fixture input
                do! totals fixture 3L 2L
            })
