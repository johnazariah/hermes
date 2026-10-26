using System.ComponentModel;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using Hermes.Installer.Control;
using Hermes.Servicing.Acceptance;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
[Collection(nameof(RestrictedOwnerNativeCollection))]
public sealed partial class RestrictedOwnerChildTests(ITestOutputHelper output)
{
    private static readonly ChildPoint[] CleanupPoints =
    [
        ChildPoint.CloseJob, ChildPoint.Stop, ChildPoint.Reap,
        ChildPoint.CloseThread, ChildPoint.CloseProcess
    ];

    [Fact]
    public void Owned_child_is_verified_before_one_resume_and_complete_cleanup()
    {
        var fake = new FakeChildren();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var result = RestrictedOwnerChild.RunInert(token, fake);
        Assert.Equal(RestrictedOwnerTokenFacts.Required, result.Facts);
        Assert.Equal(MsiContextRead.Absent, result.Thread);
        Assert.Equal(37u, result.ExitCode);
        Assert.True(result.CleanupCompleted && fake.Child.IsClosed);
        Assert.Equal(new[]
        {
            ChildPoint.Open, ChildPoint.Job, ChildPoint.Create, ChildPoint.Assign,
            ChildPoint.Facts, ChildPoint.Thread, ChildPoint.Inventory,
            ChildPoint.Resume, ChildPoint.Wait, ChildPoint.CloseJob,
            ChildPoint.Stop, ChildPoint.Reap, ChildPoint.CloseThread, ChildPoint.CloseProcess
        }, fake.Calls);
        Assert.Equal(1, fake.Resumes);
        fake.Child.Dispose();
        Assert.Equal(1, fake.Resumes);
        var calls = fake.Calls.Count;
        var error = Assert.Throws<Win32Exception>(() => fake.Child.Resume());
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Equal(calls, fake.Calls.Count);
        Assert.False(token.IsClosed);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, -1)]
    [InlineData(2, 1)]
    [InlineData(2, -1)]
    [InlineData(3, 12288)]
    [InlineData(3, 4096)]
    [InlineData(3, -1)]
    [InlineData(4, 1)]
    [InlineData(4, -1)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    [InlineData(6, 0)]
    [InlineData(6, -1)]
    [InlineData(7, 0)]
    [InlineData(7, -1)]
    public void Adverse_actual_child_facts_never_resume(int predicate, int value)
    {
        var fake = new FakeChildren(facts: Changed(predicate, value));
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(1 << (predicate - 1), error.FailedPredicates);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Only_observed_child_thread_absence_allows_resume(int state)
    {
        var fake = new FakeChildren(thread: (MsiContextRead)state);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(128, error.FailedPredicates);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Capability_failures_have_no_launch_or_resume_fallback(int point)
    {
        var fake = new FakeChildren();
        var fault = new Win32Exception(1314, "Synthetic numeric failure.");
        fake.Fail((ChildPoint)point, fault);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<Win32Exception>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Same(fault, error);
        Assert.Equal(point >= 7 ? 1 : 0, fake.Resumes);
        Assert.Equal(point == 1 ? 0 : 5, CleanupCalls(fake).Count());
        if (point != 1)
            AssertCompleteCleanup(fake);
    }

    [Fact]
    public void Closed_token_is_refused_before_opening_a_child_scope()
    {
        var fake = new FakeChildren();
        var token = RestrictedOwnerToken.Create(fake.Tokens);
        token.Dispose();
        var error = Assert.Throws<Win32Exception>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Token_closed_after_job_creation_is_refused_before_process_creation()
    {
        var fake = new FakeChildren();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        fake.Observe(point =>
        {
            if (point == ChildPoint.Job)
                token.Dispose();
        });
        var error = Assert.Throws<Win32Exception>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(6, error.NativeErrorCode);
        Assert.DoesNotContain(ChildPoint.Create, fake.Calls);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Fact]
    public void Final_parent_and_borrowed_token_reverification_precedes_resume()
    {
        var fake = new FakeChildren();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        fake.Observe(point =>
        {
            if (point == ChildPoint.Thread)
                fake.Tokens.ChangeFacts(Changed(6, 0));
        });
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(32, error.FailedPredicates);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Fact]
    public void Unexpected_programming_failure_is_preserved_after_cleanup()
    {
        var fake = new FakeChildren();
        var fault = new InvalidOperationException("Synthetic program fault.");
        fake.Fail(ChildPoint.Assign, fault);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<InvalidOperationException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Same(fault, error);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    [InlineData(8)]
    public void Cancellation_cleans_up_every_acquired_scope(int point)
    {
        var fake = new FakeChildren();
        using var cancellation = new CancellationTokenSource();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        fake.Observe(current =>
        {
            if ((int)current == point)
                cancellation.Cancel();
        });
        if (point == 0)
            cancellation.Cancel();
        var error = Assert.Throws<OperationCanceledException>(
            () => RestrictedOwnerChild.RunInert(token, fake, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(point >= 7 ? 1 : 0, fake.Resumes);
        Assert.Equal(point == 0 ? 0 : 5, CleanupCalls(fake).Count());
    }

    [Theory]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    public void Each_cleanup_failure_is_visible_and_other_cleanup_is_attempted(int point)
    {
        var fake = new FakeChildren();
        var fault = new Win32Exception(123, "Synthetic numeric failure.");
        fake.Fail((ChildPoint)point, fault);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<AcceptanceChildCleanupException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Same(fault, Assert.Single(error.InnerExceptions));
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.False(fake.Child.IsClosed);
        Assert.Equal(1, fake.Resumes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Original_and_cleanup_failures_are_both_preserved(bool cancellation)
    {
        var fake = new FakeChildren();
        Exception original = cancellation
            ? new OperationCanceledException()
            : new InvalidOperationException("Synthetic program fault.");
        var cleanup = new Win32Exception(123, "Synthetic numeric failure.");
        fake.Fail(ChildPoint.Wait, original);
        fake.Fail(ChildPoint.CloseJob, cleanup);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<AggregateException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(2, error.InnerExceptions.Count);
        Assert.Same(original, error.InnerExceptions[0]);
        var incomplete = Assert.IsType<AcceptanceChildCleanupException>(error.InnerExceptions[1]);
        Assert.Same(cleanup, Assert.Single(incomplete.InnerExceptions));
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.False(fake.Child.IsClosed);
    }

    [Fact]
    public void Multiple_cleanup_failures_are_all_retained()
    {
        var fake = new FakeChildren();
        foreach (var point in CleanupPoints)
            fake.Fail(point, new Win32Exception(123, "Synthetic numeric failure."));
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Assert.Throws<AcceptanceChildCleanupException>(
            () => RestrictedOwnerChild.RunInert(token, fake));
        Assert.Equal(5, error.InnerExceptions.Count);
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.False(fake.Child.IsClosed);
    }

    [Fact]
    public void Actual_suspended_restricted_child_is_verified_then_exits_without_parent_changes()
    {
        try { RequireGenuineProof(); }
        catch (Exception error)
        {
            var leaves = Leaves(error).ToArray();
            var code = leaves.OfType<Win32Exception>()
                .Select(value => value.NativeErrorCode).FirstOrDefault(13);
            var failed = leaves.OfType<RestrictedOwnerTokenVerificationException>()
                .Aggregate(0, (mask, value) => mask | value.FailedPredicates);
            output.WriteLine(
                $"HERMES_RESTRICTED_CHILD_TEST v=1 completed=0 error={code} failed={failed} cleanupNotCompleted={(HasCleanupFailure(error) ? 1 : 0)}");
            throw;
        }
    }

    [Fact]
    public void Actual_restricted_PowerShell_initialises_without_parent_changes()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        var before = ReadParent();
        Assert.True(IsActualSource(before), "Required genuine source context absent.");
        var security = api.ReadParentSecurity();
        var desktop = api.ReadParentDesktop();
        var error = Record.Exception(() => RequirePowerShellProof(api));
        var parent = before == ReadParent() &&
            security.SameFinalState(api.ReadParentSecurity());
        var connected = desktop.SameConnection(api.ReadParentDesktop());
        output.WriteLine(
            $"HERMES_RESTRICTED_POWERSHELL_FINAL v=1 parent={(parent ? 1 : 0)} parentConnection={(connected ? 1 : 0)}");
        if (error is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(parent && connected, "PowerShell probe changed parent final state.");
    }

    private void RequirePowerShellProof(WindowsRestrictedOwnerTokens api)
    {
        using var token = RestrictedOwnerToken.Create(api);
        var proof = MeasurePowerShellArm(token, api.PowerShellForObservation, 5);
        var comparison = MeasurePowerShellArm(token, api.PowerShellNoWindowForObservation, 6);
        var detachedSelf = MeasurePowerShellArm(token, api.PowerShellDetachedSelfControlForObservation, 7);
        var noWindowSelf = MeasurePowerShellArm(token, api.PowerShellNoWindowSelfControlForObservation, 8);
        var newConsole = MeasurePowerShellArm(token, api.PowerShellNewConsoleForObservation, 9);
        var sourceControl = RestrictedSourcePowerShellControl.Observe(api);
        AcceptanceSourceControlOrchestration.Complete(
            sourceControl, new SourceControlOutput(output),
            () => RequireOriginalPowerShellProof(
                token, proof, comparison, detachedSelf, noWindowSelf, newConsole));
    }

    private static void RequireOriginalPowerShellProof(
        RestrictedOwnerToken token, RestrictedOwnerChildResult proof,
        RestrictedOwnerChildResult comparison, RestrictedOwnerChildResult detachedSelf,
        RestrictedOwnerChildResult noWindowSelf, RestrictedOwnerChildResult newConsole)
    {
        _ = token.Verify();
        Assert.Equal(RestrictedOwnerTokenFacts.Required, proof.Facts);
        Assert.Equal(MsiContextRead.Absent, proof.Thread);
        Assert.Equal(37u, proof.ExitCode);
        Assert.True(proof.Access.ResourcesClosed);
        RequirePowerShellArm(comparison);
        RequirePowerShellArm(detachedSelf);
        RequireFullOwnedSelf(detachedSelf.Access.Process, AcceptanceChildObject.Process);
        RequireFullOwnedSelf(detachedSelf.Access.Thread, AcceptanceChildObject.Thread);
        RequirePowerShellArm(noWindowSelf);
        RequireFullOwnedSelf(noWindowSelf.Access.Process, AcceptanceChildObject.Process);
        RequireFullOwnedSelf(noWindowSelf.Access.Thread, AcceptanceChildObject.Thread);
        RequirePowerShellArm(newConsole);
    }

    private sealed class SourceControlOutput(ITestOutputHelper output)
        : IAcceptanceSourceControlOutput
    {
        public void WriteLine(string row) => output.WriteLine(row);
    }

    private static void RequirePowerShellArm(RestrictedOwnerChildResult proof)
    {
        Assert.Equal(RestrictedOwnerTokenFacts.Required, proof.Facts);
        Assert.Equal(MsiContextRead.Absent, proof.Thread);
        Assert.Equal(37u, proof.ExitCode);
        Assert.True(proof.Access.ResourcesClosed);
    }

    private RestrictedOwnerChildResult MeasurePowerShellArm(
        RestrictedOwnerToken token, IAcceptanceChildFactory factory, int arm)
    {
        var children = new RecordingChildren(factory);
        try
        {
            var proof = RestrictedOwnerChild.RunInert(token, children);
            return WriteDesktopArm(proof, children.Child, arm);
        }
        catch (Exception error)
        {
            WriteDesktopArmFailure(error, arm);
            throw;
        }
        finally
        {
            if (children.PowerShellIoFacts is { } facts)
                foreach (var row in facts.Rows(arm))
                    output.WriteLine(row);
        }
    }

    [Fact]
    public void Automatic_desktop_remains_null_without_reading_parent_names()
    {
        var names = new FakeDesktopNames(
            string.Empty, new InvalidOperationException("Synthetic read fault."));
        Assert.Null(AcceptanceChildDesktop.Resolve(
            AcceptanceDesktopSelection.Automatic, names));
        Assert.Equal(0, names.Reads);
    }

    [Fact]
    public void Explicit_existing_desktop_reads_one_validated_binding()
    {
        var names = new FakeDesktopNames("station\\desktop");
        Assert.Equal("station\\desktop", AcceptanceChildDesktop.Resolve(
            AcceptanceDesktopSelection.ExistingParent, names));
        Assert.Equal(1, names.Reads);
    }

    [Fact]
    public void Existing_desktop_read_failure_is_preserved_without_null_fallback()
    {
        var fault = new Win32Exception(5, "Synthetic numeric failure.");
        var names = new FakeDesktopNames(string.Empty, fault);
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptanceChildDesktop.Resolve(
                AcceptanceDesktopSelection.ExistingParent, names));
        Assert.Same(fault, error);
        Assert.Equal(1, names.Reads);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public void Unknown_desktop_selection_is_refused_before_reading_names(int value)
    {
        var names = new FakeDesktopNames(
            string.Empty, new InvalidOperationException("Synthetic read fault."));
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptanceChildDesktop.Resolve((AcceptanceDesktopSelection)value, names));
        Assert.Equal(13, error.NativeErrorCode);
        Assert.Equal(0, names.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("station")]
    [InlineData("\\desktop")]
    [InlineData("station\\")]
    [InlineData("station\\desk\\extra")]
    [InlineData("station\\de\0sk")]
    [InlineData("station\\de\nsk")]
    public void Malformed_existing_binding_is_refused_without_substitution(string value)
    {
        var names = new FakeDesktopNames(value);
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptanceChildDesktop.Resolve(
                AcceptanceDesktopSelection.ExistingParent, names));
        Assert.Equal(13, error.NativeErrorCode);
        Assert.Equal(1, names.Reads);
    }

    [Fact]
    public void Existing_desktop_components_are_bounded_without_truncation()
    {
        var oversized = new string('x', AcceptanceChildDesktop.NameCapacity);
        var station = Assert.Throws<Win32Exception>(() =>
            AcceptanceChildDesktop.RequireName($"{oversized}\\desktop"));
        var desktop = Assert.Throws<Win32Exception>(() =>
            AcceptanceChildDesktop.RequireName($"station\\{oversized}"));
        Assert.Equal(13, station.NativeErrorCode);
        Assert.Equal(13, desktop.NativeErrorCode);
    }

    [Fact]
    public void Object_name_decoder_uses_only_the_reported_terminated_utf16_content()
    {
        Assert.Equal("ab", AcceptanceChildDesktop.DecodeName("ab\0\0".AsSpan(), 6));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(10u)]
    [InlineData(4u)]
    [InlineData(8u)]
    public void Invalid_object_name_metadata_is_refused(uint bytes)
    {
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptanceChildDesktop.DecodeName("ab\0\0".AsSpan(), bytes));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Fact]
    public void Actual_null_and_existing_parent_desktop_counterfactual_preserves_all_guards()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        var before = ReadParent();
        Assert.True(IsActualSource(before), "Required genuine source context absent.");
        var security = api.ReadParentSecurity();
        var desktop = api.ReadParentDesktop();
        var error = Record.Exception(() =>
        {
            RequireDesktopAccessObservation(api);
            RequireDesktopCounterfactual(api);
        });
        var parent = before == ReadParent() &&
            security.SameFinalState(api.ReadParentSecurity());
        var connected = desktop.SameConnection(api.ReadParentDesktop());
        output.WriteLine(
            $"HERMES_RESTRICTED_DESKTOP_AB_FINAL v=1 parent={(parent ? 1 : 0)} parentConnection={(connected ? 1 : 0)}");
        if (error is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(parent && connected, "Counterfactual changed parent final state.");
    }

    private void RequireDesktopCounterfactual(WindowsRestrictedOwnerTokens api)
    {
        using var token = RestrictedOwnerToken.Create(api);
        var automatic = ObserveDesktopArm(token, api, 0);
        var existing = ObserveDesktopArm(
            token, api.ExistingParentDesktopForObservation, 1);
        RequireOwnedSelfCounterfactual(token, api.OwnedSelfControlForObservation);
        // Record BOTH outcomes before asserting either exit. This is not a
        // fallback, nor permission to accept the original DLL-init failure.
        Assert.True(automatic.ExitCode == 37 && existing.ExitCode == 37,
            "Counterfactual recorded; both genuine fixed exits remain required.");
    }

    private void RequireOwnedSelfCounterfactual(
        RestrictedOwnerToken token, IAcceptanceChildFactory factory)
    {
        try { RequireOwnedSelfProof(token, factory); }
        catch (Exception error)
        {
            WriteDesktopArmFailure(error, 3);
            throw;
        }
    }

    private void RequireOwnedSelfProof(
        RestrictedOwnerToken token, IAcceptanceChildFactory factory)
    {
        var children = new RecordingChildren(factory);
        var proof = RestrictedOwnerChild.RunInert(token, children);
        WriteChildAccess(proof.Access, 3);
        var closed = children.Child.IsClosed && ClosedUseRefused(children.Child);
        children.Child.Dispose();
        _ = token.Verify();
        output.WriteLine(
            $"HERMES_RESTRICTED_OWNED_SELF_CF v=1 arm=3 objectSecurity=1 completed=1 failed={proof.Facts.FailedPredicates} resumed=1 exit={proof.ExitCode} disposed={(proof.CleanupCompleted ? 1 : 0)} closed={(closed ? 1 : 0)} tokenClosed={(token.IsClosed ? 1 : 0)} error=0");
        Assert.True(proof.Facts.FailedPredicates == 0 && proof.Thread == MsiContextRead.Absent &&
            proof.ExitCode == 37 && proof.CleanupCompleted && closed,
            "Owned-only descriptor counterfactual requires the genuine fixed exit.");
        Assert.False(token.IsClosed);
        Assert.True(proof.Access.ResourcesClosed);
        RequireFullOwnedSelf(proof.Access.Process, AcceptanceChildObject.Process);
        RequireFullOwnedSelf(proof.Access.Thread, AcceptanceChildObject.Thread);
    }

    private static void RequireFullOwnedSelf(
        AcceptanceChildObjectAccess value, AcceptanceChildObject target)
    {
        Assert.Equal(target, value.Object);
        Assert.Equal(AcceptanceChildSubject.ActualTokenVerified, value.Subject);
        Assert.Equal(new AcceptanceAuthzObservation(
            AcceptanceAuthzState.Granted, 0, 0, 0x001fffff), value.Authz);
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.Present, 8192, 3, 0, 0), value.Label);
    }

    private RestrictedOwnerChildResult ObserveDesktopArm(
        RestrictedOwnerToken token, IAcceptanceChildFactory factory, int arm)
    {
        var children = new RecordingChildren(factory);
        try
        {
            var proof = RestrictedOwnerChild.RunInert(token, children);
            return WriteDesktopArm(proof, children.Child, arm);
        }
        catch (Exception error)
        {
            WriteDesktopArmFailure(error, arm);
            throw;
        }
    }

    private RestrictedOwnerChildResult WriteDesktopArm(
        RestrictedOwnerChildResult proof, IAcceptanceOwnedChild child, int arm)
    {
        WriteChildAccess(proof.Access, arm);
        var verified = proof.Facts.FailedPredicates == 0 &&
            proof.Thread == MsiContextRead.Absent;
        var closed = child.IsClosed && ClosedUseRefused(child);
        child.Dispose();
        output.WriteLine(
            $"HERMES_RESTRICTED_DESKTOP_AB v=1 arm={arm} completed=1 observed={(verified ? 1 : 0)} failed={proof.Facts.FailedPredicates} resumed=1 exit={proof.ExitCode} disposed={(proof.CleanupCompleted ? 1 : 0)} closed={(closed ? 1 : 0)} error=0");
        Assert.True(verified && proof.CleanupCompleted && closed,
            "Counterfactual child verification or cleanup incomplete.");
        return proof;
    }

    private void WriteDesktopArmFailure(Exception error, int arm)
    {
        var leaves = Leaves(error).ToArray();
        var code = leaves.OfType<Win32Exception>()
            .Select(value => value.NativeErrorCode).FirstOrDefault(13);
        var failed = leaves.OfType<RestrictedOwnerTokenVerificationException>()
            .Aggregate(0, (mask, value) => mask | value.FailedPredicates);
        output.WriteLine(
            $"HERMES_RESTRICTED_DESKTOP_AB v=1 arm={arm} completed=0 error={code} failed={failed} cleanupNotCompleted={(HasCleanupFailure(error) ? 1 : 0)}");
    }

    private void WriteChildAccess(AcceptanceChildAccessResult result, int arm)
    {
        WriteChildObjectAccess(result.Process, arm, result.ResourcesClosed);
        WriteChildObjectAccess(result.Thread, arm, result.ResourcesClosed);
        WriteChildObjectAccess(result.Token, arm, result.ResourcesClosed);
    }

    private void WriteChildObjectAccess(
        AcceptanceChildObjectAccess value, int arm, bool resourcesClosed)
    {
        var authz = value.Authz;
        var label = value.Label;
        output.WriteLine(
            $"HERMES_RESTRICTED_CHILD_AUTHZ v=1 arm={arm} object={(int)value.Object} subject={(int)value.Subject} descriptorScope=7 desired=33554432 micObserved=0 status={(int)authz.State} error={authz.Error} replyError={authz.ReplyError} grantedMask={authz.GrantedMask} labelScope=16 labelState={(int)label.State} rid={label.IntegrityRid} policy={label.Policy} aceFlags={label.AceFlags} labelError={label.Error} resourcesClosed={(resourcesClosed ? 1 : 0)}");
    }

    private sealed class FakeDesktopNames(string name, Exception? fault = null)
        : IAcceptanceParentDesktopNames
    {
        private int _reads;
        internal int Reads => _reads;

        public string ReadStartupName()
        {
            _reads++;
            if (fault is { } error)
                throw error;
            return name;
        }
    }

    [Fact]
    public void Desktop_access_borrows_only_the_verified_restricted_candidate()
    {
        var tokens = new FakeTokens();
        using var token = RestrictedOwnerToken.Create(tokens);
        var access = new FakeDesktopAccess(tokens);
        var result = token.ReadExistingParentDesktopAccess(access);
        Assert.Equal(1, access.Reads);
        Assert.True(result.ProbeClosed);
        Assert.False(token.IsClosed);
    }

    [Fact]
    public void Closed_restricted_token_is_refused_before_desktop_access()
    {
        var tokens = new FakeTokens();
        var token = RestrictedOwnerToken.Create(tokens);
        token.Dispose();
        var access = new FakeDesktopAccess(tokens);
        var error = Assert.Throws<Win32Exception>(() =>
            token.ReadExistingParentDesktopAccess(access));
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Equal(0, access.Reads);
    }

    [Fact]
    public void Desktop_access_capability_failure_is_preserved_without_fallback()
    {
        var tokens = new FakeTokens();
        using var token = RestrictedOwnerToken.Create(tokens);
        var fault = new Win32Exception(5, "Synthetic numeric failure.");
        var access = new FakeDesktopAccess(tokens, fault: fault);
        var error = Assert.Throws<Win32Exception>(() =>
            token.ReadExistingParentDesktopAccess(access));
        Assert.Same(fault, error);
        Assert.Equal(1, access.Reads);
        Assert.False(token.IsClosed);
    }

    [Fact]
    public void Restricted_token_is_reverified_after_desktop_access()
    {
        var tokens = new FakeTokens();
        using var token = RestrictedOwnerToken.Create(tokens);
        var access = new FakeDesktopAccess(
            tokens, () => tokens.ChangeFacts(Changed(6, 0)));
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(() =>
            token.ReadExistingParentDesktopAccess(access));
        Assert.Equal(32, error.FailedPredicates);
        Assert.Equal(1, access.Reads);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(122)]
    [InlineData(0)]
    public void Failed_access_check_ignores_all_poisoned_outputs(int error)
    {
        var reply = new AcceptanceAccessReply(
            0, 1, uint.MaxValue, error, uint.MaxValue, uint.MaxValue);
        var result = AcceptanceDesktopDacl.Decide(reply, 0x000f01ff);
        Assert.Equal(new AcceptanceDaclObservation(
            0, -1, 0, error > 0 ? error : 13), result);
    }

    [Theory]
    [InlineData(0, 0u, 5)]
    [InlineData(1, 0x00020001u, 1314)]
    public void Successful_access_check_distinguishes_decision_from_stale_error(
        int status, uint granted, int error)
    {
        var reply = new AcceptanceAccessReply(1, status, granted, error, 8, 0);
        var result = AcceptanceDesktopDacl.Decide(reply, 0x000f01ff);
        Assert.Equal(1, result.Observed);
        Assert.Equal(status, result.Allowed);
        Assert.Equal(granted, result.GrantedMask);
        Assert.Equal(status == 0 ? error : 0, result.Error);
    }

    [Theory]
    [InlineData(7u, 0u, 1u, 1)]
    [InlineData(4097u, 0u, 1u, 1)]
    [InlineData(8u, 1u, 1u, 1)]
    [InlineData(8u, 0u, 0x80000000u, 1)]
    [InlineData(8u, 0u, 1u, 0)]
    public void Malformed_success_outputs_are_unobserved_not_denied(
        uint bytes, uint count, uint granted, int status)
    {
        var reply = new AcceptanceAccessReply(1, status, granted, 5, bytes, count);
        Assert.Equal(new AcceptanceDaclObservation(0, -1, 0, 13),
            AcceptanceDesktopDacl.Decide(reply, 0x000f01ff));
    }

    [Theory]
    [InlineData(1, true, 0x00020303u, 0x0002001cu, 0x00020060u, 0x000f037fu)]
    [InlineData(1, false, 0x00020103u, 0x0002000cu, 0x00020060u, 0x000f016fu)]
    [InlineData(2, true, 0x00020041u, 0x000200beu, 0x00020100u, 0x000f01ffu)]
    public void Desktop_object_mappings_match_the_documented_rights(
        int target, bool interactive, uint read, uint write, uint execute, uint all)
    {
        Assert.Equal(new AcceptanceDesktopGenericMapping(read, write, execute, all),
            AcceptanceDesktopDacl.Mapping((AcceptanceDesktopObject)target, interactive));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 12288)]
    [InlineData(4, 1)]
    [InlineData(5, 0)]
    [InlineData(6, 0)]
    [InlineData(7, 0)]
    public void Adverse_query_duplicate_facts_are_refused(int predicate, int value)
    {
        var facts = predicate == 1
            ? RestrictedOwnerTokenFacts.Required with { TokenType = value }
            : Changed(predicate, value) with { TokenType = 2 };
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(() =>
            AcceptanceDesktopDacl.RequireProbe(facts, 2));
        Assert.Equal(1 << (predicate - 1), error.FailedPredicates);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void Query_duplicate_requires_the_requested_impersonation_level(int level)
    {
        var facts = RestrictedOwnerTokenFacts.Required with { TokenType = 2 };
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(() =>
            AcceptanceDesktopDacl.RequireProbe(facts, level));
        Assert.Equal(256, error.FailedPredicates);
    }

    [Fact]
    public void Concrete_self_relative_descriptor_round_trips_with_owner_and_group()
    {
        var normalized = AcceptanceDesktopDacl.NormalizeDescriptor(
            SyntheticDesktopDescriptor(), true);
        var parsed = new RawSecurityDescriptor(normalized, 0);
        Assert.True(parsed.Owner is not null && parsed.Group is not null);
        Assert.Null(parsed.SystemAcl);
        Assert.InRange(normalized.Length, 20, AcceptanceDesktopDacl.DescriptorLimit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Malformed_descriptor_metadata_is_refused_before_native_access(int fault)
    {
        var bytes = SyntheticDesktopDescriptor();
        switch (fault)
        {
            case 0: bytes[0] = 0; break;
            case 1: bytes[3] &= 0x7f; break;
            case 2: Array.Fill(bytes, (byte)0xff, 4, 4); break;
            case 3: Array.Clear(bytes, 4, 4); break;
            case 4: Array.Clear(bytes, 8, 4); break;
            case 5: bytes = bytes[..19]; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptanceDesktopDacl.NormalizeDescriptor(bytes));
        Assert.Equal(13, error.NativeErrorCode);
    }

    [Fact]
    public void Undocumented_noninteractive_desktop_generic_mapping_is_not_invented()
    {
        var descriptor = new RawSecurityDescriptor("O:SYG:SYD:(A;;GA;;;SY)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptanceDesktopDacl.NormalizeDescriptor(bytes, true));
        Assert.Equal(50, error.NativeErrorCode);
    }

    [Fact]
    public void Actual_restricted_token_existing_desktop_dacl_access_is_observed_read_only()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        var before = ReadParent();
        Assert.True(IsActualSource(before), "Required genuine source context absent.");
        var security = api.ReadParentSecurity();
        var stamp = api.ReadCurrentStamp();
        var desktop = api.ReadParentDesktop();
        var error = Record.Exception(() => RequireDesktopAccessObservation(api));
        var parent = before == ReadParent() && stamp == api.ReadCurrentStamp() &&
            security.SameFinalState(api.ReadParentSecurity());
        var connected = desktop.SameConnection(api.ReadParentDesktop());
        output.WriteLine(
            $"HERMES_RESTRICTED_DESKTOP_ACCESS_FINAL v=1 parent={(parent ? 1 : 0)} parentConnection={(connected ? 1 : 0)}");
        if (error is { } failure)
            ExceptionDispatchInfo.Capture(failure).Throw();
        Assert.True(parent && connected, "Read-only access observation changed parent state.");
    }

    private void RequireDesktopAccessObservation(WindowsRestrictedOwnerTokens api)
    {
        try
        {
            using var token = RestrictedOwnerToken.Create(api);
            var result = token.ReadExistingParentDesktopAccess(api);
            AcceptanceDesktopDacl.RequireProbe(result.ProbeFacts, result.ImpersonationLevel);
            WriteDesktopAccessObject(1, result.Station);
            WriteDesktopAccessObject(2, result.Desktop);
            WriteDesktopLabels(result);
            output.WriteLine(
                $"HERMES_RESTRICTED_DESKTOP_ACCESS_PROBE v=1 verified=1 type={result.ProbeFacts.TokenType} level={result.ImpersonationLevel} stationFlags={result.StationFlags} closed={(result.ProbeClosed ? 1 : 0)}");
            Assert.True(result.ProbeClosed &&
                result.Station.Observed == 1 && result.Desktop.Observed == 1,
                "Existing-object DACL access observation incomplete.");
        }
        catch (Exception error) { WriteDesktopAccessFailure(error); throw; }
    }

    private void WriteDesktopAccessObject(int target, AcceptanceDaclObservation value) =>
        output.WriteLine(
            $"HERMES_RESTRICTED_DESKTOP_ACCESS v=1 object={target} descriptorScope=7 desired=33554432 micObserved=0 observed={value.Observed} allowed={value.Allowed} grantedMask={value.GrantedMask} error={value.Error}");

    private void WriteDesktopLabels(AcceptanceDesktopAccessResult result)
    {
        WriteDesktopLabel(1, result.ProbeFacts.Integrity, result.StationLabel);
        WriteDesktopLabel(2, result.ProbeFacts.Integrity, result.DesktopLabel);
        // A classified query failure is visible evidence, not an access denial.
        // Do not prevent either child arm merely because a label query failed.
        Assert.NotEqual(AcceptanceMandatoryLabelState.Unknown, result.StationLabel.State);
        Assert.NotEqual(AcceptanceMandatoryLabelState.Unknown, result.DesktopLabel.State);
    }

    private void WriteDesktopLabel(
        int target, int tokenIntegrity, AcceptanceMandatoryLabelObservation value) =>
        output.WriteLine(
            $"HERMES_RESTRICTED_PARENT_USER_LABEL v=1 object={target} descriptorScope=16 tokenIntegrity={tokenIntegrity} state={(int)value.State} rid={value.IntegrityRid} policy={value.Policy} aceFlags={value.AceFlags} error={value.Error}");

    private void WriteDesktopAccessFailure(Exception error)
    {
        var leaves = Leaves(error).ToArray();
        var code = leaves.OfType<Win32Exception>()
            .Select(value => value.NativeErrorCode).FirstOrDefault(13);
        var failed = leaves.OfType<RestrictedOwnerTokenVerificationException>()
            .Aggregate(0, (mask, value) => mask | value.FailedPredicates);
        output.WriteLine(
            $"HERMES_RESTRICTED_DESKTOP_ACCESS_FAILURE v=1 error={code} failed={failed} cleanupNotCompleted={(HasCleanupFailure(error) ? 1 : 0)}");
    }

    private static byte[] SyntheticDesktopDescriptor()
    {
        var descriptor = new RawSecurityDescriptor("O:SYG:SYD:(A;;0x000F01FF;;;SY)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        return bytes;
    }

    private sealed class FakeDesktopAccess(
        FakeTokens tokens, Action? observe = null, Exception? fault = null)
        : IAcceptanceDesktopAccess
    {
        private int _reads;
        internal int Reads => _reads;

        public AcceptanceDesktopAccessResult Read(IAcceptanceRestrictedToken token)
        {
            _reads++;
            Assert.Same(tokens.Candidate, token);
            Assert.False(token.IsClosed);
            observe?.Invoke();
            if (fault is { } error)
                throw error;
            return new(new(1, 1, 0x00020001, 0), new(1, 1, 0x00020001, 0),
                RestrictedOwnerTokenFacts.Required with { TokenType = 2 }, 2, 0, true);
        }
    }

    [Fact]
    public void Unprovided_label_metadata_is_unknown_and_closed_tokens_remain_refused()
    {
        var tokens = new FakeTokens();
        using var token = RestrictedOwnerToken.Create(tokens);
        var access = new FakeDesktopAccess(tokens);
        var result = token.ReadExistingParentDesktopAccess(access);
        var unknown = new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.Unknown, -1, -1, -1, 0);
        Assert.Equal(unknown, result.StationLabel);
        Assert.Equal(unknown, result.DesktopLabel);
        token.Dispose();
        var error = Assert.Throws<Win32Exception>(() =>
            token.ReadExistingParentDesktopAccess(access));
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Equal(1, access.Reads);
    }

    [Theory]
    [InlineData(0u, 0u, 0)]
    [InlineData(4096u, 1u, 0)]
    [InlineData(8192u, 2u, 0)]
    [InlineData(8448u, 3u, 16)]
    [InlineData(12288u, 4u, 3)]
    [InlineData(16384u, 5u, 0)]
    [InlineData(uint.MaxValue, 6u, 0)]
    [InlineData(8192u, 7u, 31)]
    public void Label_queries_report_the_numeric_ace_without_inferring_access(
        uint rid, uint policy, int flags)
    {
        var query = new FakeMandatoryLabelQuery(
            SyntheticMandatoryLabel(rid, policy, checked((byte)flags)));
        var result = AcceptanceDesktopMandatoryLabel.Read(query);
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.Present, rid, checked((int)policy), flags, 0),
            result);
        Assert.Equal(new[] { 0, 48 }, query.Sizes);
        Assert.Equal(16u, AcceptanceDesktopMandatoryLabel.DescriptorScope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Missing_labels_do_not_fabricate_a_measured_medium_rid_or_policy(int kind)
    {
        var bytes = SyntheticMandatoryLabel();
        bytes[2] = kind == 0 ? (byte)0 : (byte)0x10;
        bytes[12] = kind < 2 ? (byte)0 : (byte)20;
        bytes[20] = kind == 3 ? (byte)4 : (byte)2;
        bytes[22] = 8;
        bytes[24] = 0;
        var query = new FakeMandatoryLabelQuery(bytes);
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.Missing, -1, -1, -1, 0),
            AcceptanceDesktopMandatoryLabel.Read(query));
        Assert.Equal(new[] { 0, 48 }, query.Sizes);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(3, 0)]
    [InlineData(4, 20)]
    [InlineData(8, 20)]
    [InlineData(16, 20)]
    [InlineData(2, 0)]
    [InlineData(12, 19)]
    [InlineData(12, 21)]
    [InlineData(12, 48)]
    [InlineData(15, 255)]
    [InlineData(20, 1)]
    [InlineData(21, 1)]
    [InlineData(22, 7)]
    [InlineData(22, 24)]
    [InlineData(22, 29)]
    [InlineData(22, 32)]
    [InlineData(24, 2)]
    [InlineData(26, 1)]
    [InlineData(28, 0)]
    [InlineData(29, 32)]
    [InlineData(30, 19)]
    [InlineData(31, 1)]
    [InlineData(32, 8)]
    [InlineData(36, 2)]
    [InlineData(37, 2)]
    [InlineData(38, 1)]
    [InlineData(43, 5)]
    public void Adverse_label_descriptor_metadata_is_not_missing_or_denied(
        int offset, int value)
    {
        var bytes = SyntheticMandatoryLabel();
        bytes[offset] = checked((byte)value);
        RequireInvalidMandatoryLabel(AcceptanceDesktopMandatoryLabel.Decode(bytes));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(19)]
    [InlineData(27)]
    [InlineData(47)]
    [InlineData(65537)]
    public void Label_descriptor_envelopes_are_bounded_before_decoding(int length)
    {
        var source = SyntheticMandatoryLabel();
        var bytes = new byte[length];
        source.AsSpan(0, Math.Min(source.Length, length)).CopyTo(bytes);
        RequireInvalidMandatoryLabel(AcceptanceDesktopMandatoryLabel.Decode(bytes));
    }

    [Theory]
    [InlineData(0, 4, 13)]
    [InlineData(5, 4, 5)]
    [InlineData(50, 3, 50)]
    [InlineData(120, 3, 120)]
    [InlineData(87, 4, 87)]
    [InlineData(1314, 4, 1314)]
    public void Failed_label_queries_ignore_poisoned_outputs_without_denial_inference(
        int error, int state, int code)
    {
        var failure = new AcceptanceLabelQueryReply(0, uint.MaxValue, error);
        var first = new FakeMandatoryLabelQuery(
            SyntheticMandatoryLabel(), measure: failure);
        var second = new FakeMandatoryLabelQuery(
            SyntheticMandatoryLabel(), read: failure);
        var expected = new AcceptanceMandatoryLabelObservation(
            (AcceptanceMandatoryLabelState)state, -1, -1, -1, code);
        Assert.Equal(expected, AcceptanceDesktopMandatoryLabel.Read(first));
        Assert.Equal(expected, AcceptanceDesktopMandatoryLabel.Read(second));
        Assert.Equal(new[] { 0 }, first.Sizes);
        Assert.Equal(new[] { 0, 48 }, second.Sizes);
    }

    [Theory]
    [InlineData(1, 48u, 0)]
    [InlineData(1, 48u, 122)]
    [InlineData(0, 0u, 122)]
    [InlineData(0, 19u, 122)]
    [InlineData(0, 65537u, 122)]
    public void Invalid_label_measurements_never_allocate_or_issue_a_second_query(
        int succeeded, uint bytes, int error)
    {
        var query = new FakeMandatoryLabelQuery(SyntheticMandatoryLabel(),
            measure: new AcceptanceLabelQueryReply(succeeded, bytes, error));
        RequireInvalidMandatoryLabel(AcceptanceDesktopMandatoryLabel.Read(query));
        Assert.Equal(new[] { 0 }, query.Sizes);
    }

    [Fact]
    public void A_growing_label_descriptor_is_reported_without_an_unbounded_retry()
    {
        var query = new FakeMandatoryLabelQuery(SyntheticMandatoryLabel(),
            read: new AcceptanceLabelQueryReply(0, 49, 122));
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.QueryFailed, -1, -1, -1, 122),
            AcceptanceDesktopMandatoryLabel.Read(query));
        Assert.Equal(new[] { 0, 48 }, query.Sizes);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(19u)]
    [InlineData(47u)]
    [InlineData(49u)]
    [InlineData(uint.MaxValue)]
    public void Successful_label_reads_require_exact_returned_buffer_metadata(uint actual)
    {
        var query = new FakeMandatoryLabelQuery(SyntheticMandatoryLabel(),
            read: new AcceptanceLabelQueryReply(1, actual, 1314));
        RequireInvalidMandatoryLabel(AcceptanceDesktopMandatoryLabel.Read(query));
        Assert.Equal(new[] { 0, 48 }, query.Sizes);
    }

    [Fact]
    public void Maximum_bounded_label_buffer_accepts_revision_four_and_unused_acl_space()
    {
        var bytes = new byte[AcceptanceDesktopDacl.DescriptorLimit];
        SyntheticMandatoryLabel().AsSpan().CopyTo(bytes);
        bytes[20] = 4;
        bytes[22] = 32;
        var query = new FakeMandatoryLabelQuery(bytes);
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.Present, 8192, 1, 0, 0),
            AcceptanceDesktopMandatoryLabel.Read(query));
        Assert.Equal(new[] { 0, AcceptanceDesktopDacl.DescriptorLimit }, query.Sizes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Label_capability_exceptions_are_preserved_without_fabricated_results(int point)
    {
        var fault = new InvalidOperationException("Synthetic query fault.");
        var query = new FakeMandatoryLabelQuery(
            SyntheticMandatoryLabel(), faultAt: point, fault: fault);
        var error = Assert.Throws<InvalidOperationException>(() =>
            AcceptanceDesktopMandatoryLabel.Read(query));
        Assert.Same(fault, error);
        Assert.Equal(point, query.Sizes.Count);
    }

    private static void RequireInvalidMandatoryLabel(
        AcceptanceMandatoryLabelObservation value) =>
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.InvalidData, -1, -1, -1, 13), value);

    private static byte[] SyntheticMandatoryLabel(
        uint rid = 8192, uint policy = 1, byte flags = 0)
    {
        // Synthetic label-only self-relative SD, ACL, and mandatory-label ACE.
        byte[] bytes =
        [
            1, 0, 0x10, 0x80, 0, 0, 0, 0, 0, 0, 0, 0,
            20, 0, 0, 0, 0, 0, 0, 0,
            2, 0, 28, 0, 1, 0, 0, 0,
            17, 0, 20, 0, 0, 0, 0, 0,
            1, 1, 0, 0, 0, 0, 0, 16, 0, 0, 0, 0
        ];
        bytes[29] = flags;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), policy);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(44), rid);
        return bytes;
    }

    private sealed class FakeMandatoryLabelQuery(
        byte[] descriptor,
        AcceptanceLabelQueryReply? measure = null,
        AcceptanceLabelQueryReply? read = null,
        int faultAt = 0,
        Exception? fault = null) : IAcceptanceMandatoryLabelQuery
    {
        private readonly List<int> _sizes = [];
        internal IReadOnlyList<int> Sizes => _sizes;

        public AcceptanceLabelQueryReply Query(Span<byte> buffer)
        {
            _sizes.Add(buffer.Length);
            if (_sizes.Count == faultAt && fault is { } error)
                throw error;
            buffer.Fill(0xff);
            descriptor.AsSpan(0, Math.Min(buffer.Length, descriptor.Length)).CopyTo(buffer);
            return buffer.IsEmpty
                ? measure ?? new AcceptanceLabelQueryReply(0, (uint)descriptor.Length, 122)
                : read ?? new AcceptanceLabelQueryReply(1, (uint)descriptor.Length, 1314);
        }
    }

    private void RequireGenuineProof()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        var before = ReadParent();
        Assert.True(IsActualSource(before), "Required genuine source context absent.");
        var security = api.ReadParentSecurity();
        var children = new RecordingChildren(api);
        var proof = MeasureChild(api, children);
        WriteChildAccess(proof.Access, 2);
        var parent = before == ReadParent() &&
            security.SameFinalState(api.ReadParentSecurity());
        var closed = children.Child.IsClosed && ClosedUseRefused(children.Child);
        children.Child.Dispose();
        var verified = proof.Facts.FailedPredicates == 0 &&
            proof.Thread == MsiContextRead.Absent;
        output.WriteLine(
            $"HERMES_RESTRICTED_CHILD_TEST v=1 completed=1 observed={(verified ? 1 : 0)} failed={proof.Facts.FailedPredicates} resumed=1 exit={proof.ExitCode} parent={(parent ? 1 : 0)} disposed={(proof.CleanupCompleted ? 1 : 0)} closed={(closed ? 1 : 0)} error=0");
        Assert.True(verified && proof.ExitCode == 37 && parent &&
            proof.CleanupCompleted && closed, "Restricted inert-child proof incomplete.");
    }

    private static RestrictedOwnerChildResult MeasureChild(
        WindowsRestrictedOwnerTokens api, IAcceptanceChildFactory children)
    {
        using var token = RestrictedOwnerToken.Create(api);
        return RestrictedOwnerChild.RunInert(token, children);
    }

    private static bool ClosedUseRefused(IAcceptanceOwnedChild child)
    {
        var error = Assert.Throws<Win32Exception>(() => child.Resume());
        return error.NativeErrorCode == 6;
    }

    private static IEnumerable<Exception> Leaves(Exception error) => error switch
    {
        AggregateException aggregate => aggregate.InnerExceptions.SelectMany(Leaves),
        _ => [error]
    };

    private static bool HasCleanupFailure(Exception error) =>
        error is AcceptanceChildCleanupException ||
        error is AggregateException aggregate &&
        aggregate.InnerExceptions.Any(HasCleanupFailure);

    private static MsiCallContextObservation ReadParent() =>
        MsiCallContextObservation.Read(WindowsMsiCallContextTokens.Instance);

    private static bool IsActualSource(MsiCallContextObservation value) => value is
    {
        Process: MsiContextRead.Observed,
        Elevated: 1,
        ElevationType: 1,
        Integrity: 12288,
        Admin: 1,
        Thread: MsiContextRead.Absent,
        Linked: MsiContextRead.Absent,
        SameOwner: -1,
        LinkedElevated: -1
    };

    private static void AssertCompleteCleanup(FakeChildren fake)
    {
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.True(fake.Child.IsClosed);
    }

    private static IEnumerable<ChildPoint> CleanupCalls(FakeChildren fake) =>
        fake.Calls.Where(CleanupPoints.Contains);

    private static RestrictedOwnerTokenFacts Changed(int predicate, int value) =>
        predicate switch
        {
            1 => RestrictedOwnerTokenFacts.Required with { TokenType = value },
            2 => RestrictedOwnerTokenFacts.Required with { Elevated = value },
            3 => RestrictedOwnerTokenFacts.Required with { Integrity = value },
            4 => RestrictedOwnerTokenFacts.Required with { AdminEnabled = value },
            5 => RestrictedOwnerTokenFacts.Required with { AdminDenyOnly = value },
            6 => RestrictedOwnerTokenFacts.Required with { SameOwner = value },
            7 => RestrictedOwnerTokenFacts.Required with { SameSession = value },
            _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };

    private enum ChildPoint
    {
        Open = 1,
        Job,
        Create,
        Assign,
        Facts,
        Thread,
        Resume,
        Wait,
        CloseJob,
        Stop,
        Reap,
        CloseThread,
        CloseProcess,
        Inventory
    }

    private sealed class RecordingChildren(IAcceptanceChildFactory native)
        : IAcceptanceChildFactory
    {
        private IAcceptanceOwnedChild? _child;
        internal IAcceptanceOwnedChild Child =>
            _child ?? throw new XunitException("Owned child scope absent.");

        internal AcceptancePowerShellProbeIoFacts? PowerShellIoFacts =>
            _child is IAcceptancePowerShellProbeObservation observed
                ? observed.PowerShellIoFacts : null;

        public IAcceptanceOwnedChild Open()
        {
            var child = native.Open();
            _child = child;
            return child;
        }
    }

    private sealed class FakeChildren : IAcceptanceChildFactory
    {
        private readonly List<ChildPoint> _calls = [];
        private readonly Dictionary<ChildPoint, Exception> _faults = [];
        private readonly RestrictedOwnerTokenFacts _facts;
        private readonly MsiContextRead _thread;
        private Action<ChildPoint>? _observe;

        internal FakeChildren(
            RestrictedOwnerTokenFacts? facts = null,
            MsiContextRead thread = MsiContextRead.Absent)
        {
            _facts = facts ?? RestrictedOwnerTokenFacts.Required;
            _thread = thread;
            Child = new(this);
        }

        internal FakeTokens Tokens { get; } = new();
        internal FakeChild Child { get; }
        internal AcceptanceChildAccessResult Access { get; init; } =
            AcceptanceChildAccessResult.Unknown;
        internal uint ExitCode { get; init; } = 37;
        internal IReadOnlyList<ChildPoint> Calls => _calls;
        internal int Resumes => _calls.Count(point => point == ChildPoint.Resume);

        internal void Fail(ChildPoint point, Exception error) => _faults.Add(point, error);
        internal void Observe(Action<ChildPoint> observe) => _observe = observe;

        public IAcceptanceOwnedChild Open()
        {
            At(ChildPoint.Open);
            return Child;
        }

        private void At(ChildPoint point)
        {
            _calls.Add(point);
            _observe?.Invoke(point);
            if (_faults.TryGetValue(point, out var fault))
                throw fault;
        }

        internal sealed class FakeChild(FakeChildren owner) : IAcceptanceOwnedChild
        {
            private bool _disposed;
            private bool _completed;
            public bool IsClosed => _disposed && _completed;

            public void CreateKillOnCloseJob() => Call(ChildPoint.Job);

            public void CreateSuspended(IAcceptanceRestrictedToken token)
            {
                Call(ChildPoint.Create);
                Assert.Same(owner.Tokens.Candidate, token);
                Assert.False(token.IsClosed);
            }

            public void AssignJob() => Call(ChildPoint.Assign);

            public RestrictedOwnerTokenFacts ReadPrimaryFacts()
            {
                Call(ChildPoint.Facts);
                return owner._facts;
            }

            public MsiContextRead ReadThreadToken()
            {
                Call(ChildPoint.Thread);
                return owner._thread;
            }

            public AcceptanceChildAccessResult ReadObjectAccess()
            {
                Call(ChildPoint.Inventory);
                return owner.Access;
            }

            public void Resume() => Call(ChildPoint.Resume);

            public uint Wait(CancellationToken cancellationToken)
            {
                Call(ChildPoint.Wait);
                cancellationToken.ThrowIfCancellationRequested();
                return owner.ExitCode;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;
                _disposed = true;
                AcceptanceChildCleanup.Complete(
                    () => owner.At(ChildPoint.CloseJob),
                    () => owner.At(ChildPoint.Stop),
                    () => owner.At(ChildPoint.Reap),
                    () => owner.At(ChildPoint.CloseThread),
                    () => owner.At(ChildPoint.CloseProcess));
                _completed = true;
            }

            private void Call(ChildPoint point)
            {
                if (_disposed)
                    throw RestrictedOwnerToken.Failure(6);
                owner.At(point);
            }
        }
    }

    private sealed class FakeSource : IAcceptanceSourceToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }

    private sealed class FakeRestricted : IAcceptanceRestrictedToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }

    private sealed class FakeTokens : IRestrictedOwnerTokens
    {
        private RestrictedOwnerTokenFacts _facts = RestrictedOwnerTokenFacts.Required;
        internal FakeRestricted Candidate { get; } = new();
        internal void ChangeFacts(RestrictedOwnerTokenFacts facts) => _facts = facts;

        public void RequireDisposableJob() { }
        public MsiContextRead ReadThreadToken() => MsiContextRead.Absent;
        public IAcceptanceSourceToken OpenCurrentPrimary() => new FakeSource();

        public IAcceptanceRestrictedToken Filter(
            IAcceptanceSourceToken source, AcceptanceTokenFilter flags) => Candidate;

        public void SetMediumIntegrity(IAcceptanceRestrictedToken token) =>
            Assert.Same(Candidate, token);

        public RestrictedOwnerTokenFacts ReadFacts(
            IAcceptanceRestrictedToken token, IAcceptanceSourceToken source)
        {
            Assert.Same(Candidate, token);
            Assert.False(token.IsClosed);
            Assert.False(source.IsClosed);
            return _facts;
        }
    }
}
