module Hermes.Tests.EmailChannelOwnershipTests

open System
open System.IO
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

[<Literal>]
let private existingContent = "previous archived source bytes"

let private seedDestination
    (fixture: Fixture) (incoming: Domain.EmailMessage)
    (file: Domain.EmailAttachment) (registered: bool) : Task<string> =
    task {
        let path = attachmentPath fixture incoming file
        fixture.Files.Put path existingContent
        if registered then
            let! _ = seedOwner fixture path existingContent
            return path
        else
            return path
    }

let private assertRefused (fixture: Fixture) (result: RunResult) : unit =
    assertStorageFailure result
    Assert.Contains<string>(
        fixture.Logs,
        Predicate<string>(fun text ->
            text.Contains("ownership conflict", StringComparison.OrdinalIgnoreCase)))

[<Theory>]
[<InlineData(true)>]
[<InlineData(false)>]
[<Trait("Category", "Integration")>]
let ``Channel attachment collision preserves owned or unindexed existing bytes`` (registered: bool) =
    withFixture (fun fixture ->
        task {
            let incoming = message "collision-one" "collision-thread" None
            let file = attachment "source.pdf" "incoming attachment bytes"
            let! path = seedDestination fixture incoming file registered
            let! result = run fixture (single incoming [ file ]) 1
            assertRefused fixture result
            do! assertAcknowledged fixture incoming.ProviderId 0L
            Assert.Equal(Some existingContent, fixture.Files.Get path)
            let! count = documentCount fixture
            Assert.Equal((if registered then 1L else 0L), count)
        })

let private seedMetadataOwner
    (fixture: Fixture) (incoming: Domain.EmailMessage) : Task<string> =
    task {
        let path = Path.Combine(folder fixture incoming, ".hermes.json")
        fixture.Files.Put path existingContent
        let! _ = seedOwner fixture path existingContent
        return path
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Channel sidecar collision preserves its owner and rolls back the failed email claim`` () =
    withFixture (fun fixture ->
        task {
            let incoming = message "metadata-one" "metadata-thread" None
            let file = attachment "source.pdf" "staged attachment bytes"
            let! metadata = seedMetadataOwner fixture incoming
            let! result = run fixture (single incoming [ file ]) 1
            assertRefused fixture result
            Assert.Equal(Some existingContent, fixture.Files.Get metadata)
            let path = attachmentPath fixture incoming file
            let! bytes = fixture.Files.Fs.readAllBytes path
            Assert.True(file.Content = bytes)
            let! claims = fixture.Database.execScalar "SELECT COUNT(*) FROM archive_source_claims" []
            Assert.Equal(0L, Assert.IsType<int64>(claims))
            do! assertAcknowledged fixture incoming.ProviderId 0L
            let! count = documentCount fixture
            Assert.Equal(1L, count)
        })
