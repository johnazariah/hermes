namespace Hermes.Core

open System
open System.Threading.Tasks

/// Shared combinators used across all modules.
/// foldTask for async loops, TaskResult for Result-in-Task chaining,
/// RowReader for typed DB row access.
[<RequireQualifiedAccess>]
module Prelude =

    /// Async fold over a list, threading state through each step.
    /// Uses mutable internally for performance — the one acceptable use.
    let foldTask (f: 'State -> 'T -> Task<'State>) (init: 'State) (items: 'T list) : Task<'State> =
        task {
            let mutable state = init
            for item in items do
                let! next = f state item
                state <- next
            return state
        }

    /// Map Result inside a Task.
    module TaskResult =
        let map (f: 'a -> 'b) (t: Task<Result<'a, 'e>>) : Task<Result<'b, 'e>> =
            task { let! r = t in return Result.map f r }

        let bind (f: 'a -> Task<Result<'b, 'e>>) (t: Task<Result<'a, 'e>>) : Task<Result<'b, 'e>> =
            task { let! r = t in match r with Ok v -> return! f v | Error e -> return Error e }

        let mapError (f: 'e1 -> 'e2) (t: Task<Result<'a, 'e1>>) : Task<Result<'a, 'e2>> =
            task { let! r = t in return Result.mapError f r }

    /// Typed accessor for database rows. Eliminates repeated Map.tryFind + casting boilerplate.
    type RowReader(row: Map<string, obj>) =

        member _.String (key: string) (fallback: string) : string =
            row
            |> Map.tryFind key
            |> Option.bind (fun v ->
                match v with
                | :? DBNull -> None
                | :? string as s -> Some s
                | _ -> None)
            |> Option.defaultValue fallback

        member _.OptString (key: string) : string option =
            row
            |> Map.tryFind key
            |> Option.bind (fun v ->
                match v with
                | :? DBNull -> None
                | :? string as s -> Some s
                | _ -> None)

        member _.Int64 (key: string) (fallback: int64) : int64 =
            row
            |> Map.tryFind key
            |> Option.bind (fun v ->
                match v with
                | :? int64 as i -> Some i
                | :? int as i -> Some (int64 i)
                | _ -> None)
            |> Option.defaultValue fallback

        member _.OptInt64 (key: string) : int64 option =
            row
            |> Map.tryFind key
            |> Option.bind (fun v ->
                match v with
                | :? int64 as i -> Some i
                | :? int as i -> Some (int64 i)
                | _ -> None)

        member _.Float (key: string) (fallback: float) : float =
            row
            |> Map.tryFind key
            |> Option.bind (fun v ->
                match v with
                | :? float as f -> Some f
                | :? int64 as i -> Some (float i)
                | :? decimal as d -> Some (float d)
                | _ -> None)
            |> Option.defaultValue fallback

        member _.OptFloat (key: string) : float option =
            row
            |> Map.tryFind key
            |> Option.bind (fun v ->
                match v with
                | :? float as f -> Some f
                | :? int64 as i -> Some (float i)
                | :? decimal as d -> Some (float d)
                | _ -> None)

        member _.OptDateTimeOffset (key: string) : DateTimeOffset option =
            row
            |> Map.tryFind key
            |> Option.bind (fun v ->
                match v with
                | :? string as s ->
                    match DateTimeOffset.TryParse(s) with
                    | true, d -> Some d
                    | _ -> None
                | _ -> None)

        member _.Raw : Map<string, obj> = row
