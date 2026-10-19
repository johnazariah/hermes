module Hermes.Tests.ProducerCycleIntakeTests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.ProducerCycleTestSupport

[<Fact>]
let ``Cycle registers a drop removes its sidecar and does not ingest metadata files`` () =
    withScenario (fun scenario ->
        task {
            let path = drop scenario "synthetic.pdf" "synthetic drop"
            scenario.Fixture.Files.Put (path + ".meta.json") "{}"
            let metadata = drop scenario "unrelated.META.JSON" "metadata only"
            do! run scenario.Context
            let! _ = assertIntake scenario path "synthetic drop"
            do! assertCount scenario 1
            Assert.False(scenario.Context.Fs.fileExists (path + ".meta.json"))
            Assert.Equal(Some "metadata only", scenario.Fixture.Files.Get metadata)
            assertLog scenario "Folder watcher: ingested 'synthetic.pdf'"
        })

[<Fact>]
let ``An unclaimed duplicate is removed without changing its registered original`` () =
    withScenario (fun scenario ->
        task {
            let original = drop scenario "original.pdf" "same source"
            do! run scenario.Context
            let! before = assertIntake scenario original "same source"
            let duplicate = drop scenario "duplicate.pdf" "same source"
            scenario.Fixture.Files.Put (duplicate + ".meta.json") "{}"
            do! run scenario.Context
            let! after = assertIntake scenario original "same source"
            Assert.Equal<Map<string, obj>>(before, after)
            Assert.False(scenario.Context.Fs.fileExists duplicate)
            Assert.False(scenario.Context.Fs.fileExists (duplicate + ".meta.json"))
            do! assertCount scenario 1
            assertLog scenario "Duplicate detected"
        })

[<Fact>]
let ``A staged duplicate and its metadata survive guarded duplicate cleanup`` () =
    withScenario (fun scenario ->
        task {
            let original = drop scenario "original.pdf" "same source"
            do! run scenario.Context
            let duplicate = drop scenario "claimed.pdf" "same source"
            let bytes = Encoding.UTF8.GetBytes("same source")
            do! SourceFiles.writeNew scenario.Context.Db scenario.Context.Fs duplicate bytes
            scenario.Fixture.Files.Put (duplicate + ".meta.json") "retain metadata"
            do! run scenario.Context
            let! _ = assertIntake scenario original "same source"
            do! assertClaim scenario.Fixture duplicate (SourceFiles.hash bytes)
            Assert.Equal(Some "same source", scenario.Fixture.Files.Get duplicate)
            Assert.Equal(Some "retain metadata", scenario.Fixture.Files.Get (duplicate + ".meta.json"))
            do! assertCount scenario 1
            assertLog scenario "ownership conflict with a staged source"
        })

[<Fact>]
let ``A failed content read is reported while other drops still register`` () =
    withScenario (fun scenario ->
        task {
            let broken = drop scenario "broken.pdf" "retain unreadable source"
            let good = drop scenario "good.pdf" "good source"
            let fs =
                { scenario.Context.Fs with
                    readAllBytes = fun path ->
                        if scenario.Fixture.Files.Norm path = scenario.Fixture.Files.Norm broken then
                            Task.FromException<byte array>(IOException("synthetic intake read failure"))
                        else scenario.Context.Fs.readAllBytes path }
            do! run { scenario.Context with Fs = fs }
            let! _ = assertIntake scenario good "good source"
            do! assertCount scenario 1
            Assert.Equal(Some "retain unreadable source", scenario.Fixture.Files.Get broken)
            assertLog scenario "Folder intake failed for broken.pdf: synthetic intake read failure"
        })

[<Fact>]
let ``Registration rejects bytes inconsistent with a staged source claim`` () =
    withScenario (fun scenario ->
        task {
            let path = drop scenario "changed.pdf" "claimed content"
            let claimed = Encoding.UTF8.GetBytes("claimed content")
            do! SourceFiles.writeNew scenario.Context.Db scenario.Context.Fs path claimed
            do! scenario.Context.Fs.writeAllBytes path (Encoding.UTF8.GetBytes("changed outside intake"))
            do! run scenario.Context
            do! assertCount scenario 0
            do! assertClaim scenario.Fixture path (SourceFiles.hash claimed)
            let! actual = scenario.Context.Fs.readAllBytes path
            Assert.Equal("changed outside intake", Encoding.UTF8.GetString actual)
            assertLog scenario "Folder intake failed for changed.pdf"
            assertLog scenario "Saved path source claim hash conflict"
        })

[<Fact>]
let ``A noninteger duplicate scalar cannot silently discard a new source`` () =
    withScenario (fun scenario ->
        task {
            let path = drop scenario "new.pdf" "new source"
            let db =
                { scenario.Context.Db with
                    execScalar = fun sql parameters ->
                        if sql.Contains("SELECT COUNT(*) FROM documents WHERE sha256", StringComparison.Ordinal) then
                            Task.FromResult<obj | null>(null)
                        else scenario.Context.Db.execScalar sql parameters }
            do! run { scenario.Context with Db = db }
            let! _ = assertIntake scenario path "new source"
            do! assertCount scenario 1
            Assert.Equal(Some "new source", scenario.Fixture.Files.Get path)
        })
