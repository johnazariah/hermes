module Hermes.Tests.EmailChannelCallbackTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.EmailChannelFixtures
open Hermes.Tests.EmailChannelTestSupport

let private probeBeforePersistence (fixture: Fixture) : Task<unit> =
    fixture.Database.transaction.Run(fun transaction ->
        task {
            let! count = transaction.execScalar "SELECT COUNT(*) FROM messages" []
            Assert.Equal(0L, Assert.IsType<int64>(count))
        })

let private fetchedOutsideGate
    (fixture: Fixture) (source: Algebra.EmailProvider) : Algebra.EmailProvider =
    { source with
        getFullMessage = fun id ->
            task {
                do! (probeBeforePersistence fixture).WaitAsync(TimeSpan.FromSeconds(10.0))
                return! source.getFullMessage id
            }
        getAttachments = fun id ->
            task {
                do! (probeBeforePersistence fixture).WaitAsync(TimeSpan.FromSeconds(10.0))
                return! source.getAttachments id
            } }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Both provider fetches finish before the message write gate is held`` () =
    withFixture (fun fixture ->
        task {
            let incoming = message "fetch-one" "fetch-thread" (Some "fetched body")
            let feed =
                single incoming [ attachment "source.pdf" "fetched attachment" ]
                |> fetchedOutsideGate fixture
            let! result = run fixture feed 1
            assertSuccess 1 2 result
            for document in result.Documents do do! assertRegistered fixture document
        })

let private notifyCommitted
    (fixture: Fixture) (seen: ConcurrentQueue<int64>) (id: int64) (path: string) : Task<unit> =
    task {
        let! count = documentCount fixture
        Assert.Equal(2L, count)
        let! rows =
            fixture.Database.execReader "SELECT saved_path FROM documents WHERE id = @id"
                [ "@id", Database.boxVal id ]
        Assert.Equal(path, Prelude.RowReader(rows.Head).String "saved_path" "")
        let! messages = fixture.Database.execScalar "SELECT COUNT(*) FROM messages" []
        Assert.Equal(1L, Assert.IsType<int64>(messages))
        seen.Enqueue(id)
    }

[<Fact>]
[<Trait("Category", "Integration")>]
let ``Public consumer callbacks read the complete committed email without gate reentrancy`` () =
    withFixture (fun fixture ->
        task {
            let incoming = message "callback-one" "callback-thread" (Some "callback body")
            let input = Channel.CreateUnbounded<string>()
            Assert.True(input.Writer.TryWrite(incoming.ProviderId))
            input.Writer.Complete()
            let seen = ConcurrentQueue<int64>()
            let processed = ref 0
            let operation =
                EmailSync.processMessageConsumer fixture.Files.Fs fixture.Database fixture.Logger
                    fixture.Clock (single incoming [ attachment "source.pdf" "callback attachment" ])
                    fixture.Config fixture.Account input.Reader (notifyCommitted fixture seen)
                    processed 1 CancellationToken.None
            let! committed, downloaded = operation.WaitAsync(TimeSpan.FromSeconds(30.0))
            Assert.Equal(1, committed)
            Assert.Equal(2, downloaded)
            Assert.Equal(2, seen.Count)
            Assert.Equal(1, processed.Value)
        })
