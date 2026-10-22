using System.Text;
using System.Text.Json;
using Hermes.Tray.Access;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OwnedNavigationAccessTests
{
    [Fact]
    public async Task Completed_trusted_document_gets_exact_ready_but_approval_does_not_unlock()
    {
        using var fixture = new NavigationFixture();
        var ready = await fixture.ReadyAsync();
        using var document = JsonDocument.Parse(AccessBridge.Ready(ready));
        AccessJson.Object(document.RootElement, "version", "type", "navigationId", "instanceId");
        Assert.Equal("hermes.access.ready", AccessJson.Text(document.RootElement, "type"));
        Assert.Equal(ready.NavigationId, AccessJson.Identifier(document.RootElement, "navigationId"));
        await fixture.PairAsync(ready);
        Assert.Equal(1, fixture.Owner.ResolveCalls);
        Assert.Equal(1, fixture.Owner.ApproveCalls);
        Assert.Equal(NativeAccessPhase.AwaitingExchange, fixture.Adapter.View.Phase);
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Authenticated, fixture.Adapter.View.Phase);
        Assert.Equal("Unlocked", fixture.Adapter.View.Text);
    }

    [Theory]
    [InlineData("incomplete")]
    [InlineData("static")]
    [InlineData("foreign")]
    [InlineData("dead")]
    [InlineData("navigation")]
    public async Task Untrusted_or_incomplete_documents_never_discover_or_pair(string change)
    {
        using var fixture = new NavigationFixture();
        fixture.Environment.Change(state => change switch
        {
            "incomplete" => state with { Completed = false },
            "static" => state with { Document = AccessDocument.Other },
            "foreign" => state with { Source = new Uri("http://localhost:31991/") },
            "navigation" => state with { NativeNavigationId = 2 },
            _ => state
        });
        if (change == "dead")
            fixture.Process.Alive = false;
        Assert.Null(await fixture.Adapter.NavigationCompletedAsync(1, CancellationToken.None));
        Assert.Equal(0, fixture.Owner.StatusCalls);
        Assert.Equal(0, fixture.Owner.ApproveCalls);
    }

    [Theory]
    [InlineData("iframe")]
    [InlineData("source")]
    [InlineData("nativeId")]
    [InlineData("navigationId")]
    [InlineData("instanceId")]
    public async Task Messages_require_exact_current_top_level_source_and_both_public_ids(string change)
    {
        using var fixture = new NavigationFixture();
        var ready = await fixture.ReadyAsync();
        var source = fixture.Source;
        source = change switch
        {
            "iframe" => source with { IsTopLevel = false },
            "source" => source with { Source = new Uri(fixture.Target.ApiOrigin + "/other") },
            "nativeId" => source with { NativeNavigationId = 2 },
            _ => source
        };
        ready = change switch
        {
            "navigationId" => ready with { NavigationId = AccessFixture.OtherId },
            "instanceId" => ready with { InstanceId = AccessFixture.OtherId },
            _ => ready
        };
        await fixture.Adapter.ReceiveAsync(source, AccessFixture.PairingNotice(ready), CancellationToken.None);
        Assert.Equal(0, fixture.Owner.ResolveCalls);
        Assert.Equal(0, fixture.Owner.ApproveCalls);
    }

    [Theory]
    [InlineData("pairing")]
    [InlineData("code")]
    [InlineData("challenge")]
    [InlineData("browserOrigin")]
    [InlineData("apiOrigin")]
    [InlineData("capabilities")]
    [InlineData("expired")]
    public async Task Resolve_is_bound_to_every_notice_origin_permission_and_expiry_field(string change)
    {
        using var fixture = new NavigationFixture();
        var pairing = AccessFixture.Pairing(fixture.Target);
        pairing = change switch
        {
            "pairing" => pairing with { PairingId = AccessFixture.Id },
            "code" => pairing with { UserCode = "AAAA-BBBB-CCCC" },
            "challenge" => pairing with { Challenge = AccessJson.Encode(Enumerable.Repeat((byte)1, 32).ToArray()) },
            "browserOrigin" => pairing with { BrowserOrigin = "http://localhost:31991" },
            "apiOrigin" => pairing with { ApiOrigin = "http://127.0.0.1:31992" },
            "capabilities" => pairing with { RequestedCapabilities = [AccessCapability.Mutate] },
            "expired" => pairing with { CreatedAt = AccessFixture.Now.AddMinutes(-5), ExpiresAt = AccessFixture.Now },
            _ => pairing
        };
        fixture.Owner.Resolve = _ => Task.FromResult<OwnerResult<OwnerPairing>>(
            new OwnerResult<OwnerPairing>.Success(pairing));
        await fixture.PairAsync(await fixture.ReadyAsync());
        Assert.Equal(0, fixture.Owner.ApproveCalls);
        Assert.True(fixture.Adapter.View.CanReconnect);
    }

    [Theory]
    [InlineData("handle")]
    [InlineData("generation")]
    [InlineData("startTime")]
    [InlineData("source")]
    [InlineData("dead")]
    public async Task Late_resolve_cannot_approve_after_owned_or_navigation_state_changes(string change)
    {
        using var fixture = new NavigationFixture();
        var completion = new TaskCompletionSource<OwnerResult<OwnerPairing>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owner.Resolve = _ => completion.Task;
        var pending = fixture.PairAsync(await fixture.ReadyAsync());
        fixture.ChangeOwned(change);
        completion.SetResult(new OwnerResult<OwnerPairing>.Success(AccessFixture.Pairing(fixture.Target)));
        await pending;
        Assert.Equal(0, fixture.Owner.ApproveCalls);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
        Assert.Null(fixture.Adapter.CurrentReady);
    }

    [Fact]
    public async Task Navigation_start_cancels_inflight_resolve_and_old_status_cannot_revive_authority()
    {
        using var fixture = new NavigationFixture();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owner.Resolve = async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cancelled.TrySetResult(); }
            return new OwnerResult<OwnerPairing>.Failure(OwnerFailure.Unavailable);
        };
        var oldReady = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(oldReady);
        fixture.Adapter.NavigationStarting(2, fixture.Source.Source);
        await pending;
        await cancelled.Task;
        await fixture.StatusAsync(oldReady);
        Assert.Equal(0, fixture.Owner.ApproveCalls);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
    }

    [Fact]
    public async Task Late_discovery_from_previous_navigation_cannot_publish_ready()
    {
        using var fixture = new NavigationFixture();
        var completion = new TaskCompletionSource<OwnerResult<OwnerStatus>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owner.Status = _ => completion.Task;
        var pending = fixture.Adapter.NavigationCompletedAsync(1, CancellationToken.None);
        fixture.Adapter.NavigationStarting(2, fixture.Source.Source);
        completion.SetResult(new OwnerResult<OwnerStatus>.Success(
            new(AccessFixture.Instance(fixture.Target), OwnerPhase.Ready, AccessCapabilities.EffectiveUi)));
        Assert.Null(await pending);
        Assert.Null(fixture.Adapter.CurrentReady);
    }

    [Fact]
    public async Task Late_approval_after_restart_cannot_accept_status()
    {
        using var fixture = new NavigationFixture();
        var completion = new TaskCompletionSource<OwnerResult<OwnerApproval>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owner.Approve = _ => completion.Task;
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        fixture.ChangeOwned("generation");
        completion.SetResult(new OwnerResult<OwnerApproval>.Success(AccessFixture.Approval(fixture.Target)));
        await pending;
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
    }

    [Fact]
    public async Task Exchange_status_racing_the_pipe_reply_waits_for_validated_approval()
    {
        using var fixture = new NavigationFixture();
        var completion = new TaskCompletionSource<OwnerResult<OwnerApproval>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owner.Approve = _ => completion.Task;
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Approving, fixture.Adapter.View.Phase);
        completion.SetResult(new OwnerResult<OwnerApproval>.Success(AccessFixture.Approval(fixture.Target)));
        await pending;
        Assert.Equal(NativeAccessPhase.Authenticated, fixture.Adapter.View.Phase);
    }

    [Fact]
    public async Task Failed_approval_discards_early_status_and_shows_safe_reconnect()
    {
        using var fixture = new NavigationFixture();
        var completion = new TaskCompletionSource<OwnerResult<OwnerApproval>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Owner.Approve = _ => completion.Task;
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        await fixture.StatusAsync(ready);
        completion.SetResult(new OwnerResult<OwnerApproval>.Failure(OwnerFailure.PairingConflict));
        await pending;
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
        Assert.Equal("Locked. Reconnect to unlock.", fixture.Adapter.View.Text);
        Assert.True(fixture.Adapter.View.CanReconnect);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("capabilities")]
    [InlineData("extendedExpiry")]
    [InlineData("expired")]
    public async Task Approval_response_must_match_the_exact_grant_without_extending_expiry(string change)
    {
        using var fixture = new NavigationFixture();
        var approval = AccessFixture.Approval(fixture.Target);
        approval = change switch
        {
            "id" => approval with { PairingId = AccessFixture.Id },
            "capabilities" => approval with { Capabilities = [AccessCapability.Mutate] },
            "extendedExpiry" => approval with { ExpiresAt = AccessFixture.Now.AddMinutes(10) },
            "expired" => approval with { ExpiresAt = AccessFixture.Now },
            _ => approval
        };
        fixture.Owner.Approve = _ => Task.FromResult<OwnerResult<OwnerApproval>>(
            new OwnerResult<OwnerApproval>.Success(approval));
        var ready = await fixture.ReadyAsync();
        await fixture.PairAsync(ready);
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
    }

    [Fact]
    public async Task Expiry_is_monotonic_and_duplicate_status_does_not_refresh_it()
    {
        using var fixture = new NavigationFixture();
        var ready = await fixture.ReadyAsync();
        await fixture.PairAsync(ready);
        await fixture.StatusAsync(ready);
        fixture.Clock.Advance(TimeSpan.FromMinutes(29));
        fixture.Clock.RollBackUtc(TimeSpan.FromDays(1));
        await fixture.StatusAsync(ready);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_approval_preserves_original_deadline_when_utc_rolls_back(bool duringApproval)
    {
        var owner = new AccessPendingApprovalOwner(AccessFixture.Target());
        using var fixture = new NavigationFixture(owner);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        Assert.Equal(1, owner.ApproveCalls);
        Assert.False(pending.IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        if (duringApproval)
            fixture.Clock.RollBackUtc(TimeSpan.FromHours(1));
        owner.Complete();
        await pending;
        if (!duringApproval)
            fixture.Clock.RollBackUtc(TimeSpan.FromHours(1));
        await AssertOriginalExpiryAsync(fixture, ready);
    }

    [Fact]
    public async Task Pending_approval_without_rollback_also_expires_at_the_original_exact_boundary()
    {
        var owner = new AccessPendingApprovalOwner(AccessFixture.Target());
        using var fixture = new NavigationFixture(owner);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        Assert.Equal(1, owner.ApproveCalls);
        Assert.False(pending.IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        owner.Complete();
        await pending;
        await AssertOriginalExpiryAsync(fixture, ready);
    }

    [Fact]
    public async Task Matching_status_just_before_original_expiry_still_authenticates_after_rollback()
    {
        var owner = new AccessPendingApprovalOwner(AccessFixture.Target());
        using var fixture = new NavigationFixture(owner);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        Assert.False(pending.IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        fixture.Clock.RollBackUtc(TimeSpan.FromHours(1));
        owner.Complete();
        await pending;
        fixture.Clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Authenticated, fixture.Adapter.View.Phase);
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(NativeAccessPhase.Authenticated, fixture.Adapter.View.Phase);
    }

    [Fact]
    public async Task Expiry_cancels_approval_and_holds_the_gate_until_the_actual_operation_settles()
    {
        var owner = new AccessPendingApprovalOwner(AccessFixture.Target());
        using var fixture = new NavigationFixture(owner);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        Assert.Equal(1, owner.ApproveCalls);
        Assert.False(pending.IsCompleted);
        await fixture.StatusAsync(ready);
        fixture.Clock.Advance(TimeSpan.FromSeconds(2));
        await AssertCancellingAndBlockedAsync(fixture, owner, ready, pending);
        owner.Complete();
        await pending;
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
        Assert.Equal(NativeAccessFailure.Expired, fixture.Adapter.View.Failure);
        Assert.True(fixture.Adapter.View.CanReconnect);
        await AssertNewPairingWorksAsync(fixture, owner, ready);
    }

    private static async Task AssertOriginalExpiryAsync(NavigationFixture fixture, AccessReady ready)
    {
        Assert.Equal(NativeAccessPhase.AwaitingExchange, fixture.Adapter.View.Phase);
        fixture.Clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond - 1));
        Assert.Equal(NativeAccessPhase.AwaitingExchange, fixture.Adapter.View.Phase);
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
        Assert.Equal(NativeAccessFailure.Expired, fixture.Adapter.View.Failure);
        Assert.True(fixture.Adapter.View.CanReconnect);
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
    }

    private static async Task AssertCancellingAndBlockedAsync(
        NavigationFixture fixture, AccessPendingApprovalOwner owner, AccessReady ready, Task pending)
    {
        var view = fixture.Adapter.View;
        Assert.Equal(NativeAccessPhase.Cancelling, view.Phase);
        Assert.Equal(NativeAccessFailure.Expired, view.Failure);
        Assert.False(view.CanReconnect);
        Assert.Equal("Locked. Cancelling unlock…", view.Text);
        Assert.True(owner.ApprovalToken.IsCancellationRequested);
        Assert.False(pending.IsCompleted);
        await fixture.Adapter.ReceiveAsync(fixture.Source,
            AccessFixture.PairingNotice(ready, owner.NextPairing), CancellationToken.None);
        Assert.Equal(1, owner.ResolveCalls);
        Assert.Equal(1, owner.ApproveCalls);
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Cancelling, fixture.Adapter.View.Phase);
        Assert.False(pending.IsCompleted);
    }

    private static async Task AssertNewPairingWorksAsync(
        NavigationFixture fixture, AccessPendingApprovalOwner owner, AccessReady ready)
    {
        await fixture.Adapter.ReceiveAsync(fixture.Source,
            AccessFixture.PairingNotice(ready, owner.NextPairing), CancellationToken.None);
        Assert.Equal(2, owner.ResolveCalls);
        Assert.Equal(2, owner.ApproveCalls);
        Assert.Equal(NativeAccessPhase.AwaitingExchange, fixture.Adapter.View.Phase);
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Authenticated, fixture.Adapter.View.Phase);
    }

    [Fact]
    public async Task Status_before_pairing_or_after_approval_expiry_does_not_authenticate()
    {
        using var fixture = new NavigationFixture();
        var ready = await fixture.ReadyAsync();
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.AwaitingPairing, fixture.Adapter.View.Phase);
        await fixture.PairAsync(ready);
        fixture.Clock.Advance(TimeSpan.FromSeconds(61));
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
    }

    [Theory]
    [InlineData("""{"version":1,"type":"hermes.access.status","verifier":"synthetic"}""")]
    [InlineData("""{"version":1,"type":"hermes.access.pairing","accessToken":"synthetic"}""")]
    [InlineData("""{"version":1,"type":"hermes.access.status","type":"hermes.access.pairing"}""")]
    [InlineData("""{"version":1,"type":"unknown"}""")]
    public async Task Unknown_or_secret_carrying_bridge_shapes_never_reach_owner_control(string json)
    {
        using var fixture = new NavigationFixture();
        await fixture.ReadyAsync();
        await fixture.Adapter.ReceiveAsync(fixture.Source, Encoding.UTF8.GetBytes(json), CancellationToken.None);
        Assert.Equal(0, fixture.Owner.ResolveCalls);
        Assert.Equal(0, fixture.Owner.ApproveCalls);
    }

    [Fact]
    public async Task Owner_failure_and_nonready_status_are_safe_unpaired_domain_states()
    {
        using var fixture = new NavigationFixture();
        fixture.Owner.Status = _ => Task.FromResult<OwnerResult<OwnerStatus>>(
            new OwnerResult<OwnerStatus>.Failure(OwnerFailure.Timeout));
        Assert.Null(await fixture.Adapter.NavigationCompletedAsync(1, CancellationToken.None));
        Assert.True(fixture.Adapter.View.CanReconnect);
        Assert.DoesNotContain("WebView", fixture.Adapter.View.Text);
    }

    [Fact]
    public async Task Explicit_caller_cancellation_propagates_without_approving()
    {
        using var fixture = new NavigationFixture();
        fixture.Owner.Resolve = async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new OwnerResult<OwnerPairing>.Failure(OwnerFailure.Unavailable);
        };
        var ready = await fixture.ReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var pending = fixture.Adapter.ReceiveAsync(
            fixture.Source, AccessFixture.PairingNotice(ready), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fixture.Owner.ApproveCalls);
        Assert.True(fixture.Adapter.View.CanReconnect);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_owner_settlement_after_caller_cancellation_joins_and_recovers(bool approving)
    {
        using var fixture = new NavigationFixture();
        using var cancellation = new CancellationTokenSource();
        var settlement = new SuccessfulOwnerSettlement(fixture, approving);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.Adapter.ReceiveAsync(fixture.Source, AccessFixture.PairingNotice(ready), cancellation.Token);
        try
        {
            cancellation.Cancel();
            await AssertCancelledOwnerStillBlocksAsync(fixture, settlement, ready, pending, approving);
        }
        finally
        {
            settlement.Complete();
            await AssertCallerCancellationAsync(pending, cancellation.Token);
        }
        await AssertCancellationRecoveryAsync(fixture, ready, approving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_owner_settlement_completed_before_caller_cancellation_is_observed(bool approving)
    {
        using var fixture = new NavigationFixture();
        using var cancellation = new CancellationTokenSource();
        var settlement = new SuccessfulOwnerSettlement(fixture, approving, cancellation);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.Adapter.ReceiveAsync(fixture.Source, AccessFixture.PairingNotice(ready), cancellation.Token);
        try
        {
            Assert.True(settlement.OwnerToken.IsCancellationRequested);
        }
        finally
        {
            settlement.Complete();
            await AssertCallerCancellationAsync(pending, cancellation.Token);
        }
        await AssertCancellationRecoveryAsync(fixture, ready, approving);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_owner_settlement_without_caller_cancellation_authenticates(bool approving)
    {
        using var fixture = new NavigationFixture();
        var settlement = new SuccessfulOwnerSettlement(fixture, approving);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.PairAsync(ready);
        try
        {
            Assert.False(pending.IsCompleted || settlement.OwnerToken.IsCancellationRequested);
            await fixture.StatusAsync(ready);
        }
        finally
        {
            settlement.Complete();
            await pending;
        }
        Assert.Equal((1, 1), (fixture.Owner.ResolveCalls, fixture.Owner.ApproveCalls));
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Authenticated, fixture.Adapter.View.Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Successful_owner_settlement_cannot_release_or_reset_a_new_navigation_early(bool approving)
    {
        using var fixture = new NavigationFixture();
        using var cancellation = new CancellationTokenSource();
        var settlement = new SuccessfulOwnerSettlement(fixture, approving);
        var ready = await fixture.ReadyAsync();
        var pending = fixture.Adapter.ReceiveAsync(fixture.Source, AccessFixture.PairingNotice(ready), cancellation.Token);
        AccessReady? replacement = null;
        try
        {
            cancellation.Cancel();
            replacement = await AssertReplacementBlockedAsync(fixture, pending, approving);
        }
        finally
        {
            settlement.Complete();
            await AssertCallerCancellationAsync(pending, cancellation.Token);
        }
        await AssertReplacementRecoveryAsync(fixture, Assert.IsType<AccessReady>(replacement), approving);
    }

    [Fact]
    public async Task Duplicate_completion_and_pairing_notices_do_not_dispatch_twice()
    {
        using var fixture = new NavigationFixture();
        var ready = await fixture.ReadyAsync();
        Assert.Null(await fixture.Adapter.NavigationCompletedAsync(1, CancellationToken.None));
        await fixture.PairAsync(ready);
        await fixture.PairAsync(ready);
        Assert.Equal(1, fixture.Owner.StatusCalls);
        Assert.Equal(1, fixture.Owner.ResolveCalls);
        Assert.Equal(1, fixture.Owner.ApproveCalls);
        fixture.Adapter.Invalidate();
        fixture.Adapter.Dispose();
        fixture.Adapter.Dispose();
        Assert.Null(fixture.Adapter.CurrentReady);
        Assert.Throws<ObjectDisposedException>(() =>
            fixture.Adapter.NavigationStarting(3, fixture.Source.Source));
    }

    private static async Task AssertCallerCancellationAsync(Task pending, CancellationToken cancellationToken)
    {
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellationToken, error.CancellationToken);
        Assert.True(pending.IsCanceled);
    }

    private static async Task AssertCancelledOwnerStillBlocksAsync(
        NavigationFixture fixture, SuccessfulOwnerSettlement settlement,
        AccessReady ready, Task pending, bool approving)
    {
        Assert.True(settlement.OwnerToken.IsCancellationRequested);
        Assert.False(pending.IsCompleted);
        Assert.False(fixture.Adapter.View.CanReconnect);
        Assert.Equal(approving ? NativeAccessPhase.Approving : NativeAccessPhase.Pairing,
            fixture.Adapter.View.Phase);
        await fixture.PairAsync(ready);
        await fixture.StatusAsync(ready);
        Assert.Equal((1, approving ? 1 : 0), (fixture.Owner.ResolveCalls, fixture.Owner.ApproveCalls));
        Assert.False(pending.IsCompleted);
    }

    private static async Task AssertCancellationRecoveryAsync(
        NavigationFixture fixture, AccessReady ready, bool approving)
    {
        Assert.Equal(new NativeAccessView(NativeAccessPhase.Unpaired, NativeAccessFailure.Cancelled),
            fixture.Adapter.View);
        Assert.True(fixture.Adapter.View.CanReconnect);
        Assert.Equal(ready, fixture.Adapter.CurrentReady);
        Assert.Equal((1, approving ? 1 : 0), (fixture.Owner.ResolveCalls, fixture.Owner.ApproveCalls));
        await fixture.StatusAsync(ready);
        Assert.Equal(NativeAccessPhase.Unpaired, fixture.Adapter.View.Phase);
        await AssertNewAttemptAuthenticatesAsync(fixture, fixture.Source, ready, approving);
    }

    private static async Task<AccessReady> AssertReplacementBlockedAsync(
        NavigationFixture fixture, Task pending, bool approving)
    {
        var source = fixture.Source with { NativeNavigationId = 2 };
        fixture.Adapter.NavigationStarting(2, source.Source);
        fixture.Environment.Change(state => state with { NativeNavigationId = 2 });
        var ready = await fixture.Adapter.NavigationCompletedAsync(2, CancellationToken.None)
            ?? throw new InvalidOperationException("Replacement navigation did not become ready.");
        await fixture.Adapter.ReceiveAsync(source, AccessFixture.PairingNotice(ready), CancellationToken.None);
        Assert.Equal(NativeAccessPhase.AwaitingPairing, fixture.Adapter.View.Phase);
        Assert.Equal((1, approving ? 1 : 0), (fixture.Owner.ResolveCalls, fixture.Owner.ApproveCalls));
        Assert.False(pending.IsCompleted);
        return ready;
    }

    private static async Task AssertReplacementRecoveryAsync(
        NavigationFixture fixture, AccessReady ready, bool approving)
    {
        Assert.Equal(ready, fixture.Adapter.CurrentReady);
        Assert.Equal(new NativeAccessView(NativeAccessPhase.AwaitingPairing, NativeAccessFailure.None),
            fixture.Adapter.View);
        var source = fixture.Source with { NativeNavigationId = 2 };
        await AssertNewAttemptAuthenticatesAsync(fixture, source, ready, approving);
    }

    private static async Task AssertNewAttemptAuthenticatesAsync(
        NavigationFixture fixture, AccessMessageSource source, AccessReady ready, bool approving)
    {
        await fixture.Adapter.ReceiveAsync(source, AccessFixture.PairingNotice(ready), CancellationToken.None);
        Assert.Equal((2, approving ? 2 : 1), (fixture.Owner.ResolveCalls, fixture.Owner.ApproveCalls));
        Assert.Equal(NativeAccessPhase.AwaitingExchange, fixture.Adapter.View.Phase);
        await fixture.Adapter.ReceiveAsync(source,
            AccessFixture.StatusNotice(ready, AccessFixture.Now.AddMinutes(30)), CancellationToken.None);
        Assert.Equal(NativeAccessPhase.Authenticated, fixture.Adapter.View.Phase);
    }

    private sealed class SuccessfulOwnerSettlement
    {
        private readonly OwnerTarget _target;
        private readonly TaskCompletionSource<OwnerResult<OwnerPairing>> _resolve =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<OwnerResult<OwnerApproval>> _approval =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationToken _ownerToken;

        public CancellationToken OwnerToken => _ownerToken;

        public SuccessfulOwnerSettlement(
            NavigationFixture fixture, bool approving, CancellationTokenSource? cancelAfterSuccess = null)
        {
            _target = fixture.Target;
            if (approving)
                fixture.Owner.Approve = token => Hold(token, _approval.Task, cancelAfterSuccess);
            else
                fixture.Owner.Resolve = token => Hold(token, _resolve.Task, cancelAfterSuccess);
        }

        private Task<T> Hold<T>(
            CancellationToken token, Task<T> task, CancellationTokenSource? cancelAfterSuccess)
        {
            _ownerToken = token;
            if (cancelAfterSuccess is { } cancellation)
            {
                Complete();
                Assert.True(task.IsCompletedSuccessfully);
                cancellation.Cancel();
            }
            return task;
        }

        public void Complete()
        {
            _resolve.TrySetResult(new OwnerResult<OwnerPairing>.Success(AccessFixture.Pairing(_target)));
            _approval.TrySetResult(new OwnerResult<OwnerApproval>.Success(AccessFixture.Approval(_target)));
        }
    }
}

internal sealed class NavigationFixture : IDisposable
{
    public OwnerTarget Target { get; } = AccessFixture.Target();
    public AccessTestClock Clock { get; } = new();
    public AccessTestProcess Process { get; } = new(AccessFixture.Process);
    public AccessTestEnvironment Environment { get; }
    public AccessTestOwner Owner { get; }
    public OwnedNavigationAccess Adapter { get; }
    public AccessMessageSource Source => new(1, new Uri(Target.ApiOrigin + "/"), true);

    public NavigationFixture(IOwnerControlClient? owner = null)
    {
        Environment = new(new(Process, 0, 1, Source.Source, true, AccessDocument.CanonicalReactApplication));
        Owner = new(Target);
        Adapter = new(Target, owner ?? Owner, Environment, Clock);
        Adapter.NavigationStarting(1, Source.Source);
    }

    public async Task<AccessReady> ReadyAsync() =>
        await Adapter.NavigationCompletedAsync(1, CancellationToken.None)
        ?? throw new InvalidOperationException("Synthetic navigation did not become ready.");
    public Task PairAsync(AccessReady ready) =>
        Adapter.ReceiveAsync(Source, AccessFixture.PairingNotice(ready), CancellationToken.None);
    public Task StatusAsync(AccessReady ready) =>
        Adapter.ReceiveAsync(Source,
            AccessFixture.StatusNotice(ready, AccessFixture.Now.AddMinutes(30)), CancellationToken.None);

    public void ChangeOwned(string change)
    {
        if (change == "dead")
            Process.Alive = false;
        Environment.Change(state => change switch
        {
            "handle" => state with { Process = new AccessTestProcess(AccessFixture.Process) },
            "generation" => state with { RestartGeneration = state.RestartGeneration + 1 },
            "startTime" => state with
            {
                Process = new AccessTestProcess(AccessFixture.Process with
                {
                    ProcessStartTimeUtc = AccessFixture.Process.ProcessStartTimeUtc.AddSeconds(1)
                })
            },
            "source" => state with { Source = new Uri(Target.ApiOrigin + "/replacement") },
            _ => state
        });
    }

    public void Dispose() => Adapter.Dispose();
}
