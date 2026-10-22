namespace Hermes.Service

open System
open System.Collections.Generic
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Hermes.Core.LocalAccess

/// Owns downstream application invocations, not connections or RequestAborted.
[<RequireQualifiedAccess>]
module HttpRequestLifetime =
    type Capability =
        { Invoke: HttpContext -> RequestDelegate -> Task
          Close: unit -> Task
          Drained: Task }

    // Mutation is confined to this synchronized resource-owner interpreter.
    type private State =
        { Gate: obj
          Active: HashSet<obj>
          Closed: TaskCompletionSource<unit>
          Drained: TaskCompletionSource<unit> }

    let private cell () : TaskCompletionSource<unit> =
        TaskCompletionSource<unit>(
            TaskCreationOptions.RunContinuationsAsynchronously)

    let private signalDrained (state: State) : unit =
        if state.Closed.Task.IsCompleted && state.Active.Count = 0 then
            state.Drained.TrySetResult(()) |> ignore

    let private enter (state: State) : obj option =
        lock state.Gate (fun () ->
            if state.Closed.Task.IsCompleted then None
            else
                let ticket = obj ()
                state.Active.Add(ticket) |> ignore
                Some ticket)

    let private leave (state: State) (ticket: obj) : unit =
        lock state.Gate (fun () ->
            state.Active.Remove(ticket) |> ignore
            signalDrained state)

    let private close (state: State) () : Task =
        lock state.Gate (fun () ->
            state.Closed.TrySetResult(()) |> ignore
            signalDrained state
            state.Drained.Task :> Task)

    let private run (state: State) (ticket: obj)
        (context: HttpContext) (next: RequestDelegate) : Task =
        task {
            try
                do! next.Invoke(context)
            finally
                leave state ticket
        } :> Task

    let private invoke (instance: InstanceId) (state: State)
        (context: HttpContext) (next: RequestDelegate) : Task =
        enter state
        |> Option.map (fun ticket -> run state ticket context next)
        |> Option.defaultWith (fun () ->
            HttpAccessResponse.failure instance Set.empty context
                HttpAccessValues.unavailable)

    let create (instance: InstanceId) : Capability =
        let state: State =
            { Gate = obj (); Active = HashSet<obj>()
              Closed = cell (); Drained = cell () }
        { Invoke = invoke instance state
          Close = close state
          Drained = state.Drained.Task }
