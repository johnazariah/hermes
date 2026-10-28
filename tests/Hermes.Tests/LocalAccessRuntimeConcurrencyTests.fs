module Hermes.Tests.LocalAccessRuntimeConcurrencyTests

open System
open System.Threading
open System.Threading.Tasks
open Xunit
open Hermes.Core.LocalAccess
open Hermes.Tests.LocalAccessDomainTests
open Hermes.Tests.LocalAccessRuntimeTests.Probe
open Hermes.Tests.LocalAccessRuntimeTests.Flow

module Ordering =
    [<Fact>]
    let ``concurrent valid exchanges issue exactly once and all others are consumed`` () =
        task {
            use rig = make ()
            do! ready rig
            let! pairing = approved rig
            let! held = hold rig (fun () -> exchange rig pairing token)
            let pending = List.init 15 (fun _ -> exchange rig pairing token)
            let! first = resume held
            session first |> ignore
            let! replies = Task.WhenAll(pending).WaitAsync(timeout)
            replies |> Array.iter (error AccessError.PairingConsumed)
            Assert.Empty(rig.Effects.Notifications)
        }

    [<Fact>]
    let ``revocation orders admission on the same serialized authority`` () =
        task {
            use rig = make ()
            do! ready rig
            let! issued = issue rig
            let context = Fixture.supplied issued None
            let! held = hold rig (fun () -> rig.Runtime.Public.Admit Fixture.all context token)
            let removal = rig.Runtime.Public.Revoke context token
            let following = rig.Runtime.Public.Admit Fixture.all context token
            let! accepted = resume held
            admitted accepted |> ignore
            let! removed = removal.WaitAsync(timeout)
            revoked removed
            let! denied = following.WaitAsync(timeout)
            error AccessError.InvalidCredential denied
        }

module Cancellation =
    [<Fact>]
    let ``cancellation before admission performs no clock entropy or domain work`` () =
        use rig = make ()
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()
        let pending =
            [ rig.Runtime.Public.Create (Fixture.creation Fixture.all [ Fixture.origin ]) cancellation.Token
              rig.Runtime.Owner.ControlStatus None cancellation.Token
              rig.Runtime.Lifecycle.StartReady cancellation.Token ]
        pending |> List.iter (fun work -> Assert.True(work.IsCanceled))
        Assert.Empty(rig.Effects.Reads)
        Assert.Empty(rig.Effects.Sizes)

    let private cancelQueued (cancellation: CancellationTokenSource)
        (pending: AccessReply list) : unit =
        cancellation.Cancel()
        pending |> List.iter (fun work -> Assert.True(work.IsCanceled))

    let private capacity (rig: Rig) : unit =
        [ 1 .. 128 ] |> List.iter (fun _ ->
            let rejected = rig.Runtime.Public.Create (Fixture.creation Fixture.all [ Fixture.origin ]) token
            Assert.True(rejected.IsCompletedSuccessfully)
            rejected.GetAwaiter().GetResult() |> error AccessError.AccessCapacityReached)
        rig.Runtime.Owner.ControlStatus None token
        |> fun work -> work.GetAwaiter().GetResult()
        |> error AccessError.AccessCapacityReached

    let private skipped (rig: Rig) (reads: int) : unit =
        Assert.Equal(reads + 1, rig.Effects.Reads.Count)
        Assert.Equal(rig.Effects.Reads.Count * 3, rig.Effects.Sizes.Count)

    [<Fact>]
    let ``cancelled queued senders retain capacity until removal and cannot retry mint`` () =
        task {
            use rig = make ()
            do! ready rig
            use cancellation = new CancellationTokenSource()
            let! held = hold rig (fun () -> status rig)
            let reads = rig.Effects.Reads.Count
            let queued = List.init (LocalAccessRuntime.QueueCapacity - 1) (fun _ ->
                rig.Runtime.Public.Create (Fixture.creation Fixture.all [ Fixture.origin ]) cancellation.Token)
            let sentinel = status rig
            cancelQueued cancellation queued
            capacity rig
            let! _ = resume held
            let! _ = sentinel.WaitAsync(timeout)
            skipped rig reads
            let! _ = issue rig
            return ()
        }

    let private consumed (held: Held) (reply: Result<Response, AccessError>) : unit =
        error AccessError.PairingConsumed reply
        Assert.True(held.Pending.IsCompleted)

    [<Theory>]
    [<InlineData(false)>]
    [<InlineData(true)>]
    let ``accepted exchange completes after sender or independent observer cancellation`` (sender: bool) =
        task {
            use rig = make ()
            do! ready rig
            let! pairing = approved rig
            use cancellation = new CancellationTokenSource()
            let supplied = if sender then cancellation.Token else token
            let! held = hold rig (fun () -> exchange rig pairing supplied)
            let observer = if sender then held.Pending else held.Pending.WaitAsync(cancellation.Token)
            cancellation.Cancel()
            Assert.True(observer.IsCanceled)
            let repeated = exchange rig pairing token
            release held.Gate
            let! reply = repeated.WaitAsync(timeout)
            consumed held reply
        }

module Shutdown =
    let private settled (pending: AccessReply list) : unit =
        pending |> List.iter (fun work ->
            Assert.True(work.IsCompletedSuccessfully)
            work.GetAwaiter().GetResult() |> error AccessError.AccessUnavailable)

    let private stopped (rig: Rig) (held: Held) (pending: AccessReply list)
        (completion: Task<unit>) : unit =
        Assert.True(held.Pending.IsCompletedSuccessfully)
        held.Pending.GetAwaiter().GetResult() |> admitted |> ignore
        settled pending
        Assert.Same(completion, rig.Runtime.Lifecycle.Stop())
        rig.Runtime.Lifecycle.StartReady token
        |> fun work -> work.GetAwaiter().GetResult()
        |> error AccessError.AccessUnavailable
        Assert.Empty(rig.Effects.Notifications)

    [<Fact>]
    let ``stop closes admission drains every waiter joins and cannot become ready again`` () =
        task {
            use rig = make ()
            do! ready rig
            let! issued = issue rig
            let context = Fixture.supplied issued None
            let! held = hold rig (fun () -> rig.Runtime.Public.Admit Fixture.all context token)
            let pending = List.init LocalAccessRuntime.QueueCapacity (fun _ ->
                rig.Runtime.Public.Admit Fixture.all context token)
            let completion = rig.Runtime.Lifecycle.Stop()
            Assert.False(completion.IsCompleted)
            let! unavailable = status rig
            error AccessError.AccessUnavailable unavailable
            release held.Gate
            do! completion.WaitAsync(timeout)
            stopped rig held pending completion
        }

    [<Fact>]
    let ``fatal capture closes a full queue and joins all original responses`` () =
        task {
            use rig = make ()
            do! ready rig
            let! held = hold rig (fun () -> status rig)
            let pending = List.init LocalAccessRuntime.QueueCapacity (fun _ -> status rig)
            rig.Effects.ClockFaults.Enqueue(InvalidOperationException("Synthetic clock failure."))
            let! failed = resume held
            error AccessError.AccessUnavailable failed
            do! rig.Runtime.Lifecycle.Stop().WaitAsync(timeout)
            settled pending
            Assert.Single(rig.Effects.Notifications) |> ignore
            Assert.Equal(2, rig.Effects.Reads.Count)
            Assert.Equal(3, rig.Effects.Sizes.Count)
        }
