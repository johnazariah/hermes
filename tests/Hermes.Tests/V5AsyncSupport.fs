module Hermes.Tests.V5AsyncSupport

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core

module Suspension = Hermes.Tests.AsyncCapabilityControl

[<RequireQualifiedAccess>]
type Kind = Triage | Deep

[<RequireQualifiedAccess>]
type Effect = Provider | Claim | Output | Artifact

type Event =
    | Entered of Effect
    | Applied of Effect
    | Transaction

type Control =
    { Kind: Kind
      Suspended: bool
      Response: Result<string, string>
      Provider: Suspension.Gate
      Claim: Suspension.Gate
      Output: Suspension.Gate
      Events: ConcurrentQueue<Event> }

let artifactName: string = "thread.comprehension.json"
let preferences: string = "Keep the owner's corrected metadata."

let name: Kind -> string = function
    | Kind.Triage -> "triage"
    | Kind.Deep -> "deep-comprehend"

let table: Kind -> string = function
    | Kind.Triage -> "triage"
    | Kind.Deep -> "comprehension"

let prerequisites: Kind -> string list = function
    | Kind.Triage -> [ "extract" ]
    | Kind.Deep -> [ "extract"; "triage" ]

let create (kind: Kind) (suspended: bool) (response: Result<string, string>) : Control =
    { Kind = kind; Suspended = suspended; Response = response
      Provider = Suspension.Gate("V5 provider")
      Claim = Suspension.Gate("V5 canonical claim")
      Output = Suspension.Gate("V5 derived output")
      Events = ConcurrentQueue<Event>() }

let gates (control: Control) : Suspension.Gate list =
    [ control.Provider; control.Claim; control.Output ]

let gate (control: Control) : Effect -> Suspension.Gate = function
    | Effect.Provider -> control.Provider
    | Effect.Claim -> control.Claim
    | Effect.Output -> control.Output
    | Effect.Artifact -> invalidArg "effect" "Artifact writes are observed, not gated"

let private invoke
    (control: Control) (effect: Effect) (source: Algebra.Database)
    (work: unit -> Task<'T>) : Task<'T> =
    let apply () : Task<'T> =
        control.Events.Enqueue(Applied effect)
        work ()
    control.Events.Enqueue(Entered effect)
    if control.Suspended then (gate control effect).Invoke source apply
    else apply ()

let private intercepted (control: Control) (sql: string) : Effect option =
    if sql.Contains("INSERT INTO stage_publications", StringComparison.Ordinal) then
        Some Effect.Claim
    elif sql.Contains($"INSERT OR REPLACE INTO {table control.Kind}", StringComparison.Ordinal) then
        Some Effect.Output
    else
        None

// Each callback receives a wrapper around its ACTUAL scoped database.
// The captured source is also the database exposed at the suspension gate.
let rec private transaction (control: Control) (source: Algebra.Database) : Algebra.Transaction =
    { new Algebra.Transaction with
        member _.Run<'T>(work: Algebra.Database -> Task<'T>) =
            control.Events.Enqueue Transaction
            source.transaction.Run(fun scoped -> work (database control scoped)) }

and database (control: Control) (source: Algebra.Database) : Algebra.Database =
    { source with
        execNonQuery = fun sql parameters ->
            intercepted control sql
            |> Option.map (fun effect ->
                invoke control effect source (fun () -> source.execNonQuery sql parameters))
            |> Option.defaultWith (fun () -> source.execNonQuery sql parameters)
        transaction = transaction control source
        inTransaction = fun callback ->
            TransactionExecution.run (transaction control source) callback }

let files (control: Control) (source: Algebra.FileSystem) : Algebra.FileSystem =
    { source with
        writeAllBytes = fun path bytes ->
            if path.EndsWith(artifactName, StringComparison.Ordinal) then
                control.Events.Enqueue(Applied Effect.Artifact)
            source.writeAllBytes path bytes }

let provider
    (control: Control) (database: Algebra.Database)
    (expectedText: string) : Algebra.ChatProvider =
    { complete = fun systemPrompt userPrompt ->
        Assert.False(String.IsNullOrWhiteSpace systemPrompt)
        Assert.Contains(expectedText, userPrompt)
        Assert.Contains(preferences, userPrompt)
        invoke control Effect.Provider database (fun () -> Task.FromResult control.Response) }

let assertEvents
    (control: Control) (transactions: int)
    (effects: (Effect * int * int) list) : unit =
    let expected =
        (Transaction, transactions)
        :: (effects |> List.collect (fun (effect, entered, applied) ->
            [ Entered effect, entered; Applied effect, applied ]))
        |> List.filter (fun (_, count) -> count <> 0)
        |> Map.ofList
    let actual = control.Events |> Seq.countBy id |> Map.ofSeq
    Assert.Equal<Map<Event, int>>(expected, actual)

let private cancelled (start: unit -> Task<int>) : Task<bool> =
    task {
        try
            let! _ = start ()
            return false
        with :? OperationCanceledException ->
            return true
    }

let private inspectCancellation
    (cancellation: CancellationTokenSource) (entered: Task)
    (verify: unit -> Task<unit>) (pending: Task<bool>) : Task<unit> =
    task {
        try
            do! entered.WaitAsync(Suspension.timeout)
            Assert.False(pending.IsCompleted, "Queued work acquired a held GPU permit")
            do! verify ()
            cancellation.Cancel()
            let! observed = pending.WaitAsync(Suspension.timeout)
            Assert.True(observed, "Queued work did not propagate cancellation")
        finally
            cancellation.Cancel()
    }

// Cancels AND joins the queued task even if its checkpoint assertion fails.
let cancelQueued
    (start: CancellationToken -> Task<int>) (entered: Task)
    (verify: unit -> Task<unit>) : Task<unit> =
    task {
        use cancellation = new CancellationTokenSource(Suspension.timeout)
        let! observed =
            Suspension.exercise []
                (fun () -> cancelled (fun () -> start cancellation.Token))
                (inspectCancellation cancellation entered verify)
        Assert.True(observed)
    }

module Embedding =
    type Control =
        { Available: Suspension.Gate
          Encode: Suspension.Gate
          Input: string
          Response: Result<float32 array, string> }

    let create (input: string) (response: Result<float32 array, string>) : Control =
        { Available = Suspension.Gate("embedding availability")
          Encode = Suspension.Gate("embedding encode")
          Input = input; Response = response }

    let gates (control: Control) : Suspension.Gate list =
        [ control.Available; control.Encode ]

    let client (database: Algebra.Database) (control: Control) : Algebra.EmbeddingClient =
        { dimensions = 3
          isAvailable = fun () ->
              control.Available.Invoke database (fun () -> Task.FromResult true)
          embed = fun text ->
              Assert.Equal<string>(control.Input, text)
              control.Encode.Invoke database (fun () -> Task.FromResult control.Response) }

    let drive
        (control: Control) (pending: Task<'T>)
        (verify: Algebra.Database -> Task<unit>) : Task<unit> =
        task {
            do! Suspension.release control.Available pending verify
            do! Suspension.release control.Encode pending verify
        }

    let assertUsed (control: Control) : unit =
        gates control
        |> List.iter (fun gate ->
            Assert.True(gate.WasEntered)
            Assert.True(gate.EffectStarted))
