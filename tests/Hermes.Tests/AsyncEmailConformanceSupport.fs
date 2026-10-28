module Hermes.Tests.AsyncEmailConformanceSupport

open System
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Xunit
open Hermes.Core
open Hermes.Tests.AsyncCapabilityControl
open Hermes.Tests.EmailChannelFixtures

type Plan =
    { Load: Gate
      Page: Gate
      Messages: Map<string, Gate>
      Attachments: Map<string, Gate>
      Store: Gate
      Watermark: Gate
      FileName: string }

let plan (ids: string list) (fileName: string) : Plan =
    let gates label =
        ids |> List.map (fun id -> id, Gate($"{label}/{id}")) |> Map.ofList
    { Load = Gate("load watermark")
      Page = Gate("list provider IDs")
      Messages = gates "get full message"
      Attachments = gates "get attachments"
      Store = Gate("insert attachment document")
      Watermark = Gate("publish watermark")
      FileName = fileName }

let private all (control: Plan) =
    [ control.Load; control.Page; control.Store; control.Watermark ]
    @ (control.Messages |> Map.values |> Seq.toList)
    @ (control.Attachments |> Map.values |> Seq.toList)

let prefix (control: Plan) (id: string) =
    [ control.Load; control.Page
      control.Messages[id]; control.Attachments[id] ]

let sibling (control: Plan) (id: string) =
    [ control.Messages[id]; control.Attachments[id] ]

let private invoke gate database effect =
    gate
    |> Option.map (fun (control: Gate) -> control.Invoke database effect)
    |> Option.defaultWith effect

let private scalarGate (control: Plan) (sql: string) parameters =
    if sql.Contains("SELECT last_sync_at FROM sync_state", StringComparison.Ordinal) then
        Some control.Load
    elif sql.Contains("INSERT INTO documents", StringComparison.Ordinal)
         && (parameters
             |> List.exists (fun (key, value) ->
                 key = "@orig" && value = (control.FileName :> obj))) then
        Some control.Store
    else
        None

let private writeGate (control: Plan) (sql: string) =
    if sql.Contains("INSERT INTO sync_state", StringComparison.Ordinal) then
        Some control.Watermark
    else
        None

// Recursively wrap the database supplied to each transaction callback.
// Wrapping just the root database would miss the actual document INSERT.
let rec private transaction control (source: Algebra.Database) : Algebra.Transaction =
    { new Algebra.Transaction with
        member _.Run<'T>(work: Algebra.Database -> Task<'T>) =
            source.transaction.Run(fun scoped -> work (database control scoped)) }

and private database control (source: Algebra.Database) : Algebra.Database =
    { source with
        execScalar = fun sql parameters ->
            invoke (scalarGate control sql parameters) source
                (fun () -> source.execScalar sql parameters)
        execNonQuery = fun sql parameters ->
            invoke (writeGate control sql) source
                (fun () -> source.execNonQuery sql parameters)
        transaction = transaction control source
        inTransaction = fun callback ->
            TransactionExecution.run (transaction control source) callback }

let private provider control database (source: Algebra.EmailProvider) =
    { source with
        listStubPage = fun token query limit ->
            control.Page.Invoke database
                (fun () -> source.listStubPage token query limit)
        getFullMessage = fun id ->
            control.Messages[id].Invoke database (fun () -> source.getFullMessage id)
        getAttachments = fun id ->
            control.Attachments[id].Invoke database (fun () -> source.getAttachments id) }

let private start
    (fixture: Fixture) control source
    (writer: ChannelWriter<Document.T>) (ct: CancellationToken) =
    EmailSync.syncAccountWithChannel
        fixture.Files.Fs (database control fixture.Database) fixture.Logger fixture.Clock
        (provider control fixture.Database source) fixture.Config fixture.Account writer 1 ct

let rec private drain remaining collected (reader: ChannelReader<Document.T>) =
    match reader.TryRead() with
    | true, document when remaining > 0 ->
        drain (remaining - 1) (document :: collected) reader
    | true, _ -> invalidOp "Async conformance output exceeded its finite bound"
    | false, _ -> List.rev collected

let run
    (fixture: Fixture) control source
    (verify: ChannelReader<Document.T> -> Task<EmailSync.SyncResult> -> Task<unit>)
    : Task<RunResult> =
    task {
        use cancellation = new CancellationTokenSource(timeout)
        let output = Channel.CreateBounded<Document.T>(8)
        try
            let! summary =
                exercise (all control)
                    (fun () -> start fixture control source output.Writer cancellation.Token)
                    (verify output.Reader)
            Assert.True(output.Writer.TryComplete())
            return { Summary = summary; Documents = drain 8 [] output.Reader }
        finally
            output.Writer.TryComplete() |> ignore
    }
