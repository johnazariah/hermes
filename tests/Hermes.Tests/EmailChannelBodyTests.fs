module Hermes.Tests.EmailChannelBodyTests

open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport
open Hermes.Tests.EmailHistoryFixtures

let private assertAppended
    (fixture: Fixture) (previous: History) (result: RunResult) : Task<unit> =
    task {
        assertSuccess 1 1 result
        let current = source "email_body" result
        do! assertRegistered fixture current
        Assert.NotEqual(Document.id previous.Document, Document.id current)
        Assert.NotEqual<string>(required "saved_path" previous.Document, required "saved_path" current)
        do! assertPreserved fixture previous
        let! count = documentCount fixture
        Assert.Equal(2L, count)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Same-day shared-prefix messages retain processed history and distinct sources`` () =
    withFixture (fun fixture ->
        task {
            let pair = prefixPair ()
            let! previous = seedProcessedBody fixture pair.Previous
            let! result = run fixture (single pair.Replacement []) 1
            do! assertAppended fixture previous result
            assertBody fixture "replacement body" result
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``An undated distinct body is archived without changing processed history`` () =
    withFixture (fun fixture ->
        task {
            let pair = prefixPair ()
            let! previous = seedProcessedBody fixture pair.Previous
            let incoming = { pair.Replacement with Date = None }
            let! result = run fixture (single incoming []) 1
            do! assertAppended fixture previous result
        })

let private invalidatePreviousDate (fixture: Fixture) (id: int64) : Task<unit> =
    task {
        let! _ =
            fixture.Database.execNonQuery
                "UPDATE documents SET email_date = @date WHERE id = @id"
                [ "@date", Database.boxVal "synthetic-invalid-date"
                  "@id", Database.boxVal id ]
        return ()
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Malformed historical dates do not prevent an independent body append`` () =
    withFixture (fun fixture ->
        task {
            let pair = bodyPair ()
            let! previous = seedBody fixture pair.Previous
            do! invalidatePreviousDate fixture (Document.id previous)
            let! result = run fixture (single pair.Replacement []) 1
            assertSuccess 1 1 result
            Assert.True(fixture.Files.Fs.fileExists (fullPath fixture (required "saved_path" previous)))
            let! count = documentCount fixture
            Assert.Equal(2L, count)
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Older distinct messages preserve newer processed history`` () =
    withFixture (fun fixture ->
        task {
            let pair = prefixPair ()
            let! previous = seedProcessedBody fixture pair.Previous
            let incoming =
                { pair.Replacement with Date = pair.Previous.Date |> Option.map (fun date -> date.AddDays(-1.0)) }
            let! result = run fixture (single incoming []) 1
            do! assertAppended fixture previous result
        })

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Equal body content does not discard distinct message provenance`` () =
    withFixture (fun fixture ->
        task {
            let pair = prefixPair ()
            let! previous = seedProcessedBody fixture pair.Previous
            let incoming = { pair.Replacement with BodyText = pair.Previous.BodyText }
            let! result = run fixture (single incoming []) 1
            do! assertAppended fixture previous result
            let current = source "email_body" result
            Assert.Equal(required "sha256" previous.Document, required "sha256" current)
            Assert.Equal(incoming.ProviderId, required "gmail_id" current)
        })
