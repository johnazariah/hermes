module Hermes.Tests.EmailChannelFixtures

open System
open System.Collections.Concurrent
open System.Text
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core

type Fixture =
    { Database: Algebra.Database
      Files: TestHelpers.MemFs
      Logs: ConcurrentQueue<string>
      Logger: Algebra.Logger
      Clock: Algebra.Clock
      Config: Domain.HermesConfig
      Account: string }

type Page =
    { Token: string option
      Ids: string list
      NextToken: string option }

type RunResult =
    { Summary: EmailSync.SyncResult
      Documents: Document.T list }

let private create () : Fixture =
    let logs = ConcurrentQueue<string>()
    { Database = TestHelpers.createDb ()
      Files = TestHelpers.memFs ()
      Logs = logs
      Logger =
          { TestHelpers.silentLogger with
              warn = logs.Enqueue
              error = logs.Enqueue }
      Clock = TestHelpers.defaultClock
      Config = { TestHelpers.testConfig "/archive" with MinAttachmentSize = 4 }
      Account = "channel-account" }

let withFixture (verify: Fixture -> Task<unit>) : Task<unit> =
    task {
        let fixture = create ()
        try
            do! verify fixture
        finally
            fixture.Database.dispose ()
    }

let message (id: string) (threadId: string) (body: string option) : Domain.EmailMessage =
    { ProviderId = id
      ThreadId = threadId
      Sender = Some "synthetic@example.test"
      Subject = Some "Synthetic channel document"
      Date = Some (DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero))
      Labels = []
      HasAttachments = true
      BodyText = body }

let attachment (name: string) (text: string) : Domain.EmailAttachment =
    let bytes = Encoding.UTF8.GetBytes(text)
    { FileName = name
      MimeType = "application/pdf"
      SizeBytes = int64 bytes.Length
      Content = bytes }

let pages (messages: Domain.EmailMessage list) : Page list =
    [ { Token = None
        Ids = messages |> List.map (fun value -> value.ProviderId)
        NextToken = None } ]

let twoPages (first: Domain.EmailMessage) (second: Domain.EmailMessage) : Page list =
    let continuation = "synthetic-next-page"
    [ { Token = None; Ids = [ first.ProviderId ]; NextToken = Some continuation }
      { Token = Some continuation; Ids = [ second.ProviderId ]; NextToken = None } ]

let private stubPage
    (source: Page list) (token: string option) (query: string option) (limit: int)
    : Task<Algebra.StubPage> =
    let requiredQuery =
        query |> Option.defaultWith (fun () -> failwith "Channel enumeration omitted its query")
    Assert.StartsWith("after:", requiredQuery)
    Assert.Equal(500, limit)
    let page = source |> List.find (fun value -> value.Token = token)
    Task.FromResult
        { Ids = page.Ids
          NextPageToken = page.NextToken
          ResultSizeEstimate = source |> List.sumBy (fun value -> value.Ids.Length) |> int64 }

let provider
    (source: Page list)
    (messages: Domain.EmailMessage list)
    (attachments: Map<string, Domain.EmailAttachment list>) : Algebra.EmailProvider =
    { TestHelpers.mockProvider messages attachments with
        listStubPage = stubPage source }

let single
    (incoming: Domain.EmailMessage)
    (attachments: Domain.EmailAttachment list) : Algebra.EmailProvider =
    provider (pages [ incoming ]) [ incoming ]
        (Map.ofList [ incoming.ProviderId, attachments ])

let since (timestamp: DateTimeOffset) (source: Algebra.EmailProvider) : Algebra.EmailProvider =
    { source with
        listStubPage = fun token query limit ->
            Assert.Equal(Some $"after:{timestamp.ToUnixTimeSeconds()}", query)
            source.listStubPage token query limit }

/// Both consumers must enter getFullMessage before either can proceed.
let simultaneous
    (messages: Domain.EmailMessage list)
    (source: Algebra.EmailProvider) : Algebra.EmailProvider =
    let signals =
        messages
        |> List.map (fun value ->
            value.ProviderId,
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))
        |> Map.ofList
    let ready = signals |> Map.values |> Seq.map (fun value -> value.Task) |> Seq.toArray
    { source with
        getFullMessage = fun id ->
            task {
                signals[id].TrySetResult(()) |> ignore
                let! _ = Task.WhenAll(ready)
                return! source.getFullMessage id
            } }

let rec private drain
    (reader: ChannelReader<Document.T>) (collected: Document.T list)
    : Task<Document.T list> =
    task {
        let! available = reader.WaitToReadAsync().AsTask()
        if available then
            let! document = reader.ReadAsync().AsTask()
            return! drain reader (document :: collected)
        else
            return List.rev collected
    }

let run
    (fixture: Fixture) (source: Algebra.EmailProvider) (concurrency: int)
    : Task<RunResult> =
    task {
        use cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30.0))
        let output = Channel.CreateUnbounded<Document.T>()
        let! summary =
            EmailSync.syncAccountWithChannel
                fixture.Files.Fs fixture.Database fixture.Logger fixture.Clock source
                fixture.Config fixture.Account output.Writer concurrency cancellation.Token
        Assert.True(output.Writer.TryComplete())
        let! documents = drain output.Reader []
        return { Summary = summary; Documents = documents }
    }
