using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Xunit;

namespace Hermes.Installer.Control.Tests;

public sealed class MsiCallContextTests
{
    private enum ReadPoint { None, Open, Primary, Linked, Thread }

    [Theory]
    [InlineData(3, -1, -1)]
    [InlineData(1, 1, 0)]
    public void Observed_fields_are_fixed_and_handles_are_disposed(
        int linkedState, int sameOwner, int elevated)
    {
        var linked = (MsiContextRead)linkedState;
        var tokens = new Tokens(linked);
        var value = MsiCallContextObservation.Read(tokens);
        Assert.Equal(new(MsiContextRead.Observed, 1, 1, 12288, 1,
            MsiContextRead.Absent, linked, sameOwner, elevated), value);
        Assert.Equal(1, tokens.Opens);
        Assert.Equal(1, tokens.ThreadReads);
        Assert.Equal(1, tokens.Disposals);
        Assert.DoesNotContain("PRIVATE_SECRET", value.ToDiagnosticSuffix());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Native_read_failures_are_not_false_or_absent(int point)
    {
        var tokens = new Tokens(failAt: (ReadPoint)point,
            failure: new Win32Exception(5, "PRIVATE_SECRET"));
        var value = MsiCallContextObservation.Read(tokens);
        Assert.Equal(point is 1 or 2 ? MsiContextRead.Failed : MsiContextRead.Observed,
            value.Process);
        Assert.Equal(point is 1 or 3 ? MsiContextRead.Failed : MsiContextRead.Absent,
            value.Linked);
        Assert.Equal(point == 4 ? MsiContextRead.Failed : MsiContextRead.Absent, value.Thread);
        Assert.Equal(point is 1 or 2 ? -1 : 1, value.Elevated);
        Assert.Equal(-1, value.SameOwner);
        Assert.Equal(-1, value.LinkedElevated);
        Assert.Equal(point == 1 ? 0 : 1, tokens.Disposals);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Expected_identity_read_failures_have_no_exception_text(int kind)
    {
        Exception error = kind switch
        {
            0 => new Win32Exception(5, "PRIVATE_SECRET"),
            1 => new UnauthorizedAccessException("PRIVATE_SECRET"),
            _ => new SecurityException("PRIVATE_SECRET")
        };
        var value = MsiCallContextObservation.Read(
            new Tokens(failAt: ReadPoint.Primary, failure: error));
        Assert.Equal(MsiContextRead.Failed, value.Process);
        Assert.DoesNotContain("PRIVATE_SECRET", value.ToDiagnosticSuffix());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Programming_read_faults_are_not_negative_measurements(int point)
    {
        var expected = new InvalidOperationException("PRIVATE_SECRET");
        var tokens = new Tokens(failAt: (ReadPoint)point, failure: expected);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() =>
            MsiCallContextObservation.Read(tokens)));
        Assert.Equal(point == 1 ? 0 : 1, tokens.Disposals);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1603u)]
    public void Actual_invocation_seam_captures_once_before_call_and_never_supplies_fences(uint code)
    {
        var authority = Create();
        var tokens = new Tokens();
        var receipt = authority.Execute(() => new Lifetime(),
            () => authority.InvokeObserved(tokens, () => Native(tokens, code)), static _ => { });
        Assert.Equal(code, receipt.MsiExitCode);
        Assert.Equal(MsiContextRead.Observed, receipt.CallContext.Process);
        Assert.Empty(receipt.Fences);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.False(receipt.ControlFaulted);
        authority.InvokeObserved(tokens, static () => 1603);
        Assert.Equal(1, tokens.Opens);
        Assert.DoesNotContain("callContext",
            JsonSerializer.Serialize(receipt, ControlJson.Default.InstallerCallReceipt));
    }

    [Fact]
    public void Observation_programming_fault_is_handed_off_after_native_return_and_publication()
    {
        var authority = Create();
        var expected = new NotSupportedException("PRIVATE_SECRET");
        var tokens = new Tokens(failAt: ReadPoint.Primary, failure: expected);
        InstallerCallReceipt? published = null;
        var invoked = false;
        Assert.Same(expected, Assert.Throws<NotSupportedException>(() => authority.Execute(
            () => new Lifetime(), () => authority.InvokeObserved(tokens,
                () => { invoked = true; return 1603; }), value => published = value)));
        var receipt = Assert.IsType<InstallerCallReceipt>(published);
        Assert.True(invoked && receipt.ApiEntered && receipt.ApiReturned);
        Assert.Equal(1603u, receipt.MsiExitCode);
        Assert.False(receipt.ControlFaulted || receipt.MayEvaluateRecovery);
        Assert.Empty(receipt.Fences);
        Assert.Equal(MsiCallContextObservation.Missing, receipt.CallContext);
    }

    [Fact]
    public void Context_fault_precedes_later_callback_fault_after_return_and_publication()
    {
        var authority = Create();
        var expected = new NotSupportedException("PRIVATE_CONTEXT_SECRET");
        var tokens = new Tokens(failAt: ReadPoint.Primary, failure: expected);
        var callback = new CallbackFault(new InvalidOperationException("PRIVATE_CALLBACK_SECRET"));
        InstallerCallReceipt? published = null;
        var actual = Assert.Throws<NotSupportedException>(() => authority.Execute(
            () => new Lifetime(), () => authority.InvokeObserved(tokens,
                () => callback.Invoke(authority)), value => published = value));
        Assert.Same(expected, actual);
        AssertCombinedFaultReceipt(published, faulted: false, returned: true);
        Assert.True(callback.Returned);
        Assert.Equal(1, callback.Reads);
        Assert.Equal(1, tokens.Disposals);
    }

    [Fact]
    public void Earlier_callback_fault_is_not_replaced_by_later_context_fault()
    {
        var authority = Create();
        var expected = new InvalidOperationException("PRIVATE_CALLBACK_SECRET");
        var callback = new CallbackFault(expected);
        var tokens = new Tokens(failAt: ReadPoint.Primary,
            failure: new NotSupportedException("PRIVATE_CONTEXT_SECRET"));
        InstallerCallReceipt? published = null;
        var actual = Assert.Throws<InvalidOperationException>(() => authority.Execute(
            () => new Lifetime(), () =>
            {
                callback.Invoke(authority);
                return authority.InvokeObserved(tokens, static () => 1603);
            }, value => published = value));
        Assert.Same(expected, actual);
        AssertCombinedFaultReceipt(published, faulted: false, returned: true);
        Assert.Equal(1, callback.Reads);
        Assert.Equal(1, tokens.Disposals);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_or_disposal_failure_still_precedes_both_observation_faults(
        bool duringDisposal)
    {
        var authority = Create();
        var tokens = new Tokens(failAt: ReadPoint.Primary,
            failure: new NotSupportedException("PRIVATE_CONTEXT_SECRET"));
        var callback = new CallbackFault(new InvalidOperationException("PRIVATE_CALLBACK_SECRET"));
        InstallerCallReceipt? published = null;
        var receipt = authority.Execute(() => new FailingLifetime(duringDisposal),
            () => authority.InvokeObserved(tokens,
                () => NativeAfterCallback(authority, callback, duringDisposal)),
            value => published = value);
        Assert.Same(receipt, published);
        AssertCombinedFaultReceipt(published, faulted: true, returned: duringDisposal);
        Assert.True(callback.Returned);
        Assert.Equal(1, callback.Reads);
        Assert.Equal(1, tokens.Disposals);
    }

    [Fact]
    public void Missing_and_failed_measurements_are_not_a_non_elevated_claim()
    {
        var missing = MsiCallContextObservation.Missing;
        Assert.Equal(-1, missing.Elevated);
        Assert.Equal(-1, missing.Admin);
        Assert.Equal(-1, missing.Integrity);
        Assert.Equal(" context=1 process=0 elevated=-1 elevationType=-1 integrity=-1 admin=-1 thread=0 linked=0 sameOwner=-1 linkedElevated=-1",
            missing.ToDiagnosticSuffix());
        var failed = MsiCallContextObservation.Read(new Tokens(
            failAt: ReadPoint.Open, failure: new Win32Exception(5)));
        Assert.Equal(MsiContextRead.Failed, failed.Process);
        Assert.Equal(-1, failed.Elevated);
    }

    [Fact]
    public void Invalid_or_contradictory_fields_are_programming_faults()
    {
        var value = MsiCallContextObservation.Read(new Tokens());
        MsiCallContextObservation[] invalid =
        [
            value with { Process = MsiContextRead.Absent },
            value with { Elevated = -1 }, value with { ElevationType = 0 },
            value with { Integrity = 65536 }, value with { Admin = 2 },
            value with { Thread = MsiContextRead.NotObserved },
            value with { Linked = MsiContextRead.Observed },
            value with { SameOwner = 0 }, value with { LinkedElevated = 0 },
            MsiCallContextObservation.Missing with { Thread = MsiContextRead.Absent }
        ];
        Assert.All(invalid, item => Assert.Throws<ArgumentOutOfRangeException>(
            () => item.ToDiagnosticSuffix()));
    }

    private static void AssertCombinedFaultReceipt(
        InstallerCallReceipt? published, bool faulted, bool returned)
    {
        var receipt = Assert.IsType<InstallerCallReceipt>(published);
        Assert.True(receipt.ApiEntered);
        Assert.Equal(returned, receipt.ApiReturned);
        Assert.Equal(returned ? (uint?)1603 : null, receipt.MsiExitCode);
        Assert.Equal(faulted, receipt.ControlFaulted);
        Assert.False(receipt.MayEvaluateRecovery);
        Assert.Empty(receipt.Fences);
        Assert.Equal(MsiCallContextObservation.Missing, receipt.CallContext);
        var json = JsonSerializer.Serialize(receipt, ControlJson.Default.InstallerCallReceipt);
        Assert.DoesNotContain("PRIVATE_", json);
        Assert.DoesNotContain("callContext", json, StringComparison.OrdinalIgnoreCase);
    }

    private static uint NativeAfterCallback(
        CallAuthority authority, CallbackFault callback, bool duringDisposal)
    {
        var code = callback.Invoke(authority);
        return duringDisposal ? code : throw new IOException("SYNTHETIC_NATIVE_FAILURE");
    }

    private static uint Native(Tokens tokens, uint code)
    {
        Assert.Equal(1, tokens.Opens);
        Assert.Equal(1, tokens.ThreadReads);
        Assert.Equal(1, tokens.Disposals);
        return code;
    }

    private static CallAuthority Create()
    {
        var run = Guid.NewGuid();
        var endpoint = new ControlEndpoint(1, run, new(100, 200),
            "synthetic-test-principal", ControlNames.Pipe(run));
        var root = FixedRootFixture.Policy;
        var plan = new ControlPlan(Guid.NewGuid(), MaintenanceKind.Install,
            root.AppDirectory, [], Path.GetFullPath("candidate.msi"), false, true);
        return new(endpoint, plan, new Gate(), root);
    }

    private sealed class Gate : IClosedGateOwnership
    {
        public string GatePath => Path.GetFullPath("synthetic-admission.bin");
        public bool IsHeldAndClosed => true;
    }

    private sealed class Lifetime : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class FailingLifetime(bool fail) : IDisposable
    {
        public void Dispose()
        {
            if (fail)
                throw new IOException("SYNTHETIC_DISPOSAL_FAILURE");
        }
    }

    private sealed class CallbackFault(Exception failure) : IMsiFailureRecords
    {
        private int _reads;
        private bool _returned;
        internal int Reads => _reads;
        internal bool Returned => _returned;

        internal uint Invoke(CallAuthority authority)
        {
            var context = GCHandle.Alloc(authority);
            try
            {
                Assert.Equal(0, ExternalUiScope.Dispatch(
                    GCHandle.ToIntPtr(context), MsiFailureTrace.ActionStartMessage, 1,
                    this, static _ => "", static (_, _, _) => { }));
                _returned = true;
                return 1603;
            }
            finally { context.Free(); }
        }

        public string ReadAction(uint record)
        {
            _reads++;
            throw failure;
        }

        public int? ReadErrorId(uint record)
        {
            _reads++;
            throw failure;
        }
    }

    private sealed class Tokens(
        MsiContextRead linked = MsiContextRead.Absent,
        ReadPoint failAt = ReadPoint.None, Exception? failure = null) : IMsiCallContextTokens
    {
        private readonly MsiContextRead _linked = linked;
        private int _opens;
        private int _threads;
        private int _disposals;
        internal int Opens => _opens;
        internal int ThreadReads => _threads;
        internal int Disposals => _disposals;

        public IMsiCallContextToken OpenProcess()
        {
            _opens++;
            Fail(ReadPoint.Open);
            return new Token(this);
        }

        public MsiContextRead ReadThreadToken()
        {
            _threads++;
            Fail(ReadPoint.Thread);
            return MsiContextRead.Absent;
        }

        private void Fail(ReadPoint point)
        {
            if (point == failAt && failure is { } error)
                throw error;
        }

        private sealed class Token(Tokens owner) : IMsiCallContextToken
        {
            public (int Elevated, int ElevationType, int Integrity, int Admin) ReadPrimary()
            {
                owner.Fail(ReadPoint.Primary);
                return (1, 1, 12288, 1);
            }

            public (MsiContextRead State, int SameOwner, int Elevated) ReadLinked()
            {
                owner.Fail(ReadPoint.Linked);
                var state = owner._linked;
                return state == MsiContextRead.Observed
                    ? (state, 1, 0) : (state, -1, -1);
            }

            public void Dispose() => owner._disposals++;
        }
    }
}
