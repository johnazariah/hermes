module Hermes.Tests.EmailChannelProducerTests

open System
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Paged channel ingestion registers body and attachment with immutable source claims`` () =
    withFixture (fun fixture ->
        task {
            let clean = "Synthetic body content"
            let first = message "page-one" "thread-one" (Some $"<p>{clean}</p>")
            let second = message "page-two" "thread-two" None
            let file = attachment "source.pdf" "synthetic attachment bytes"
            let tiny = attachment "tiny.pdf" "x"
            let files = Map.ofList [ first.ProviderId, [ file ]; second.ProviderId, [ tiny ] ]
            let! result = run fixture (provider (twoPages first second) [ first; second ] files) 1
            assertSuccess 2 2 result
            for document in result.Documents do do! assertRegistered fixture document
            do! assertAttachment fixture file result
            assertBody fixture clean result
            Assert.Equal(first.ProviderId, required "gmail_id" (source "email_body" result))
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Re-enumerating an acknowledged message leaves registered sources unchanged`` () =
    withFixture (fun fixture ->
        task {
            let incoming = message "replay-one" "replay-thread" (Some "replay body")
            let feed = single incoming [ attachment "source.pdf" "replay attachment" ]
            let! first = run fixture feed 1
            assertSuccess 1 2 first
            let before = snapshot fixture
            let! second = run fixture feed 1
            assertSuccess 0 0 second
            Assert.Equal(1, second.Summary.DuplicatesSkipped)
            Assert.Equal<Map<string, string>>(before, snapshot fixture)
            for document in first.Documents do do! assertRegistered fixture document
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Empty enumeration retains the existing watermark`` () =
    withFixture (fun fixture ->
        task {
            let initial = DateTimeOffset(2025, 2, 1, 0, 0, 0, TimeSpan.Zero)
            do! seedWatermark fixture initial
            let feed = provider (pages []) [] Map.empty |> since initial
            let! result = run fixture feed 1
            assertSuccess 0 0 result
            Assert.Equal(0, result.Summary.DuplicatesSkipped)
            let! watermark = EmailSync.loadSyncState fixture.Database fixture.Account
            Assert.Equal(Some initial, watermark)
            let! count = documentCount fixture
            Assert.Equal(0L, count)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Overlapping channel consumers emit the correct independently owned rows`` () =
    withFixture (fun fixture ->
        task {
            let first = message "parallel-one" "parallel-thread-one" None
            let second = message "parallel-two" "parallel-thread-two" None
            let messages = [ first; second ]
            let files =
                Map.ofList
                    [ first.ProviderId, [ attachment "first.pdf" "first source bytes" ]
                      second.ProviderId, [ attachment "second.pdf" "second source bytes" ] ]
            let feed = provider (pages messages) messages files |> simultaneous messages
            let! result = run fixture feed 2
            assertSuccess 2 2 result
            let actual = result.Documents |> List.map (required "gmail_id") |> Set.ofList
            Assert.Equal<Set<string>>(set [ first.ProviderId; second.ProviderId ], actual)
            for document in result.Documents do do! assertRegistered fixture document
        })
