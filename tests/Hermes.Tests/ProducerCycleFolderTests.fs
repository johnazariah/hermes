module Hermes.Tests.ProducerCycleFolderTests

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.ProducerCycleTestSupport

let private watched scenario name text =
    let directory = "/watch"
    scenario.Fixture.Files.Fs.createDirectory directory
    scenario.Fixture.Files.Put (Path.Combine(directory, name)) text
    { scenario.Context with
        Config =
            { scenario.Context.Config with
                WatchFolders = [ { Path = directory; Patterns = [ "*.pdf" ] } ] } }

[<Fact>]
let ``Cycle registers canonical watched copies and repeat scans retain their source claims`` () =
    withScenario (fun scenario ->
        task {
            let context = watched scenario "synthetic.pdf" "watched source"
            let path = canonicalPath scenario "synthetic.pdf" "watched source"
            do! run context
            let! before = assertIntake scenario path "watched source"
            do! assertClaim scenario.Fixture path (required "sha256" before)
            Assert.Equal(Some "watched source", scenario.Fixture.Files.Get "/watch/synthetic.pdf")
            do! run context
            let! after = assertIntake scenario path "watched source"
            Assert.Equal<Map<string, obj>>(before, after)
            do! assertCount scenario 1
            assertLog scenario "Folder scan examined 1 file(s)"
        })

[<Fact>]
let ``A scan failure is logged without losing independent unclassified intake`` () =
    withScenario (fun scenario ->
        task {
            let context = watched scenario "unreadable.pdf" "watch source"
            let path = drop scenario "available.pdf" "available source"
            let fs =
                { context.Fs with
                    getFiles = fun directory pattern ->
                        if scenario.Fixture.Files.Norm directory = "/watch" then
                            raise (IOException("synthetic scan failure"))
                        context.Fs.getFiles directory pattern }
            do! run { context with Fs = fs }
            let! _ = assertIntake scenario path "available source"
            do! assertCount scenario 1
            assertLog scenario "Folder scan error: synthetic scan failure"
        })

[<Fact>]
let ``Canonical copy conflicts preserve the prior staged source instead of registering replacement bytes`` () =
    withScenario (fun scenario ->
        task {
            let context = watched scenario "conflict.pdf" "incoming content"
            let path = canonicalPath scenario "conflict.pdf" "incoming content"
            let original = Encoding.UTF8.GetBytes("previously claimed content")
            do! SourceFiles.writeNew context.Db context.Fs path original
            do! run context
            do! assertCount scenario 0
            do! assertClaim scenario.Fixture path (SourceFiles.hash original)
            Assert.Equal(Some "previously claimed content", scenario.Fixture.Files.Get path)
            Assert.Equal(Some "incoming content", scenario.Fixture.Files.Get "/watch/conflict.pdf")
            assertLog scenario "Failed to process conflict.pdf"
            assertLog scenario "ownership conflict"
        })

[<Fact>]
let ``Watch patterns exclude nonmatching files from registration`` () =
    withScenario (fun scenario ->
        task {
            let context = watched scenario "allowed.pdf" "allowed source"
            scenario.Fixture.Files.Put "/watch/ignored.txt" "ignored source"
            do! run context
            let! _ =
                assertIntake scenario
                    (canonicalPath scenario "allowed.pdf" "allowed source") "allowed source"
            do! assertCount scenario 1
            Assert.Equal(Some "ignored source", scenario.Fixture.Files.Get "/watch/ignored.txt")
            assertLog scenario "Folder scan examined 2 file(s)"
        })
