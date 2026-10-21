using System.ComponentModel;
using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
[Collection(nameof(RestrictedOwnerNativeCollection))]
public sealed class StartupDebugProbeTests
{
    [Theory]
    [InlineData("")]
    [InlineData("run")]
    [InlineData("Startup-debug-probe")]
    [InlineData("startup-debug-probe x")]
    [InlineData("startup-debug-probe --pid 7")]
    [InlineData("startup-debug-probe --command anything")]
    [InlineData("startup-debug-probe startup-debug-probe")]
    [InlineData("startup-debug-probe x y")]
    public void Finite_arity_refuses_before_any_capability(string text)
    {
        var host = new FakeHost();
        var arguments = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, StartupDebugProbe.Dispatch(arguments, host));
        Assert.Empty(host.Calls);
    }

    [Fact]
    public void Hosted_high_debugger_gate_precedes_the_only_fixed_execution()
    {
        var host = new FakeHost();
        Assert.Equal(0, StartupDebugProbe.Dispatch(["startup-debug-probe"], host));
        Assert.Equal(["gate", "fixed-probe"], host.Calls);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(13)]
    [InlineData(1314)]
    public void Refused_context_never_executes(int code)
    {
        var host = new FakeHost(gateError: new Win32Exception(code));
        Assert.Equal(1, StartupDebugProbe.Dispatch(["startup-debug-probe"], host));
        Assert.Equal(["gate"], host.Calls);
    }

    [Fact]
    public void Actual_child_verification_and_arming_precede_one_resume()
    {
        var fixture = Create();
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var result = RestrictedOwnerChild.RunInert(token, fixture.Children);
        Assert.Equal(37u, result.ExitCode);
        Assert.True(result.CleanupCompleted);
        Assert.Equal(new[]
        {
            "job", "create", "assign", "facts", "thread", "access",
            "identity", "arm", "resume", "trace-wait", "dispose"
        }, fixture.Child.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_cancelled_arm_never_resumes(bool cancellation)
    {
        Exception error = cancellation
            ? new OperationCanceledException()
            : new Win32Exception(13, "Synthetic wrong arming identity.");
        var fixture = Create(armError: error);
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var actual = Record.Exception(() =>
            RestrictedOwnerChild.RunInert(token, fixture.Children));
        Assert.Same(error, actual);
        Assert.DoesNotContain("resume", fixture.Child.Calls);
        Assert.True(fixture.Child.IsClosed);
    }

    [Fact]
    public void Timeout_and_cleanup_fault_remain_distinct()
    {
        var original = new Win32Exception(1460);
        var cleanup = new Win32Exception(6);
        var fixture = Create(waitError: original, cleanupError: cleanup);
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var error = Assert.Throws<AggregateException>(() =>
            RestrictedOwnerChild.RunInert(token, fixture.Children));
        Assert.Same(original, error.InnerExceptions[0]);
        Assert.Equal((1460, 6), StartupDebugProbe.SplitFailure(error));
        Assert.False(fixture.Child.IsClosed);
    }

    [Fact]
    public void Cleanup_only_fault_is_not_relabelled_as_execution_failure()
    {
        var fixture = Create(cleanupError: new Win32Exception(6));
        using var token = RestrictedOwnerToken.Create(fixture.Tokens);
        var error = Assert.Throws<AcceptanceChildCleanupException>(() =>
            RestrictedOwnerChild.RunInert(token, fixture.Children));
        Assert.Equal((0, 6), StartupDebugProbe.SplitFailure(error));
        Assert.DoesNotContain("normal-wait", fixture.Child.Calls);
        Assert.False(fixture.Child.IsClosed);
    }

    [Fact]
    public void Dispatch_does_not_propagate_arbitrary_failure_payload()
    {
        var host = new FakeHost(runError:
            new InvalidOperationException("Synthetic private path and command payload."));
        Assert.Equal(1, StartupDebugProbe.Dispatch(["startup-debug-probe"], host));
        Assert.Equal(["gate", "fixed-probe"], host.Calls);
    }

    private static Fixture Create(
        Exception? armError = null, Exception? waitError = null,
        Exception? cleanupError = null)
    {
        var child = new FakeChild(waitError, cleanupError);
        var control = new FakeControl(child.Calls, armError);
        return new(new FakeTokens(), child,
            new(new SingleChild(child), control, CancellationToken.None));
    }

    private sealed record Fixture(
        FakeTokens Tokens, FakeChild Child, StartupDebugChildren Children);

    private sealed class FakeHost(
        Exception? gateError = null, Exception? runError = null) : IStartupDebugHost
    {
        internal readonly List<string> Calls = [];

        public void RequireContext()
        {
            Calls.Add("gate");
            if (gateError is { } error)
                throw error;
        }

        public int RunFixedProbe()
        {
            Calls.Add("fixed-probe");
            if (runError is { } error)
                throw error;
            return 0;
        }
    }

    private sealed class SingleChild(FakeChild child) : IAcceptanceChildFactory
    {
        public IAcceptanceOwnedChild Open() => child;
    }

    private sealed class FakeControl(
        List<string> calls, Exception? failure) : IStartupDebugControl
    {
        public void AwaitArming(
            StartupDebugIdentity identity, CancellationToken cancellationToken)
        {
            Assert.Equal(new(22, 123, true, true), identity);
            cancellationToken.ThrowIfCancellationRequested();
            calls.Add("arm");
            if (failure is { } error)
                throw error;
        }
    }

    private sealed class FakeChild(
        Exception? waitError, Exception? cleanupError)
        : IAcceptanceOwnedChild, IAcceptanceStartupChild
    {
        internal readonly List<string> Calls = [];
        private bool _closed;
        public bool IsClosed => _closed;
        public void CreateKillOnCloseJob() => Calls.Add("job");
        public void CreateSuspended(IAcceptanceRestrictedToken token) => Calls.Add("create");
        public void AssignJob() => Calls.Add("assign");
        public void Resume() => Calls.Add("resume");

        public RestrictedOwnerTokenFacts ReadPrimaryFacts()
        {
            Calls.Add("facts");
            return RestrictedOwnerTokenFacts.Required;
        }

        public MsiContextRead ReadThreadToken()
        {
            Calls.Add("thread");
            return MsiContextRead.Absent;
        }

        public AcceptanceChildAccessResult ReadObjectAccess()
        {
            Calls.Add("access");
            return AcceptanceChildAccessResult.Unknown;
        }

        public StartupDebugIdentity ReadStartupIdentity()
        {
            Calls.Add("identity");
            return new(22, 123, true, true);
        }

        public uint Wait(CancellationToken cancellationToken)
        {
            Calls.Add("normal-wait");
            throw new InvalidOperationException("Ordinary ten-second wait must not change.");
        }

        public uint WaitForStartupTrace(CancellationToken cancellationToken)
        {
            Calls.Add("trace-wait");
            cancellationToken.ThrowIfCancellationRequested();
            if (waitError is { } error)
                throw error;
            return 37;
        }

        public void Dispose()
        {
            Calls.Add("dispose");
            if (cleanupError is { } error)
                throw new AcceptanceChildCleanupException([error]);
            _closed = true;
        }
    }

    private sealed class FakeTokens : IRestrictedOwnerTokens
    {
        public void RequireDisposableJob() { }
        public MsiContextRead ReadThreadToken() => MsiContextRead.Absent;
        public IAcceptanceSourceToken OpenCurrentPrimary() => new Source();
        public void SetMediumIntegrity(IAcceptanceRestrictedToken token) { }

        public IAcceptanceRestrictedToken Filter(
            IAcceptanceSourceToken source, AcceptanceTokenFilter flags)
        {
            Assert.Equal(AcceptanceTokenFilter.DisableMaxPrivilege |
                AcceptanceTokenFilter.LuaToken, flags);
            return new Filtered();
        }

        public RestrictedOwnerTokenFacts ReadFacts(
            IAcceptanceRestrictedToken token, IAcceptanceSourceToken source) =>
            RestrictedOwnerTokenFacts.Required;
    }

    private sealed class Source : IAcceptanceSourceToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }

    private sealed class Filtered : IAcceptanceRestrictedToken
    {
        private bool _closed;
        public bool IsClosed => _closed;
        public void Dispose() => _closed = true;
    }
}
