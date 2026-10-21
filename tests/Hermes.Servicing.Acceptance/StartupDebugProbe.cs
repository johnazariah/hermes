using System.ComponentModel;

namespace Hermes.Servicing.Acceptance;

internal interface IStartupDebugHost
{
    void RequireContext();
    int RunFixedProbe();
}

internal static class StartupDebugProbe
{
    internal const string Command = "startup-debug-probe";

    internal static int Dispatch(string[] arguments, IStartupDebugHost host)
    {
        if (arguments is not [Command])
            return 2;
        try
        {
            host.RequireContext();
            return host.RunFixedProbe();
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(FormattableString.Invariant(
                $"HERMES_STARTUP_REFUSED v=1 error={ErrorCode(error)}"));
            return 1;
        }
    }

    internal static int ErrorCode(Exception error) => error switch
    {
        Win32Exception { NativeErrorCode: > 0 and <= 65535 } native =>
            native.NativeErrorCode,
        OperationCanceledException => 1223,
        _ => 13
    };

    internal static (int Original, int Cleanup) SplitFailure(Exception error) =>
        error switch
        {
            AcceptanceChildCleanupException cleanup => (0, FirstCode(cleanup)),
            AggregateException aggregate => aggregate.InnerExceptions
                .Select(SplitFailure).Aggregate((Original: 0, Cleanup: 0),
                    (left, right) => (
                        left.Original != 0 ? left.Original : right.Original,
                        left.Cleanup != 0 ? left.Cleanup : right.Cleanup)),
            _ => (ErrorCode(error), 0)
        };

    private static int FirstCode(Exception error) => error switch
    {
        AggregateException { InnerExceptions: [var first, ..] } => FirstCode(first),
        _ => ErrorCode(error)
    };
}

internal sealed record StartupDebugIdentity(
    uint Pid, long Born, bool ImageMatched, bool DebuggerPresent)
{
    internal static StartupDebugIdentity Empty { get; } = new(0, 0, false, false);

    internal void Require()
    {
        if (Pid == 0 || Born <= 0 || !ImageMatched || !DebuggerPresent)
            throw RestrictedOwnerToken.Failure(13);
    }
}

internal interface IAcceptanceStartupChild
{
    StartupDebugIdentity ReadStartupIdentity();
    uint WaitForStartupTrace(CancellationToken cancellationToken);
}

internal interface IStartupDebugControl
{
    void AwaitArming(
        StartupDebugIdentity identity, CancellationToken cancellationToken);
}

internal sealed class StartupDebugChildren(
    IAcceptanceChildFactory children,
    IStartupDebugControl control,
    CancellationToken cancellationToken) : IAcceptanceChildFactory
{
    private StartupDebugChild? _child;

    internal StartupDebugChild? Child => _child;

    public IAcceptanceOwnedChild Open()
    {
        if (_child is { })
            throw RestrictedOwnerToken.Failure(13);
        _child = new(children.Open(), control, cancellationToken);
        return _child;
    }
}

// The existing RestrictedOwnerChild sequence still owns verification and cleanup.
// Only this closed diagnostic substitutes the wait and inserts an arming barrier.
internal sealed class StartupDebugChild(
    IAcceptanceOwnedChild child,
    IStartupDebugControl control,
    CancellationToken cancellationToken) : IAcceptanceOwnedChild
{
    private RestrictedOwnerTokenFacts? _facts;
    private MsiContextRead _thread = MsiContextRead.NotObserved;
    private StartupDebugIdentity _identity = StartupDebugIdentity.Empty;
    private bool _resumeAttempted;

    internal StartupDebugIdentity Identity => _identity;
    public bool IsClosed => child.IsClosed;

    public void CreateKillOnCloseJob() => child.CreateKillOnCloseJob();
    public void CreateSuspended(IAcceptanceRestrictedToken token) =>
        child.CreateSuspended(token);
    public void AssignJob() => child.AssignJob();

    public RestrictedOwnerTokenFacts ReadPrimaryFacts()
    {
        _facts = child.ReadPrimaryFacts();
        return _facts;
    }

    public MsiContextRead ReadThreadToken()
    {
        _thread = child.ReadThreadToken();
        return _thread;
    }

    public AcceptanceChildAccessResult ReadObjectAccess() => child.ReadObjectAccess();

    public void Resume()
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_resumeAttempted || _facts is not { } facts ||
            _thread != MsiContextRead.Absent ||
            child is not IAcceptanceStartupChild startup)
            throw RestrictedOwnerToken.Failure(13);
        _resumeAttempted = true;
        facts.Require();
        _identity = startup.ReadStartupIdentity();
        _identity.Require();
        control.AwaitArming(_identity, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        child.Resume();
    }

    public uint Wait(CancellationToken cancellationToken) =>
        child is IAcceptanceStartupChild startup
            ? startup.WaitForStartupTrace(cancellationToken)
            : throw RestrictedOwnerToken.Failure(13);

    public void Dispose() => child.Dispose();
}

internal sealed record StartupDebugOutcome(
    uint Exit, bool Cleanup, int Error, int CleanupError);

internal sealed class WindowsStartupDebugHost : IStartupDebugHost
{
    public void RequireContext()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        WindowsRestrictedOwnerTokens.RequireStartupDebugger();
        if (!StartupDebugParent.IsHighSource(StartupDebugParent.ReadContext()))
            throw RestrictedOwnerToken.Failure(5);
    }

    public int RunFixedProbe()
    {
        var before = StartupDebugParent.Read();
        using var control = FileStartupDebugControl.Open();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(165));
        var api = WindowsRestrictedOwnerTokens.Instance;
        var children = new StartupDebugChildren(
            api.PowerShellNoWindowForObservation, control, deadline.Token);
        var outcome = Observe(api, children, deadline.Token);
        var (parent, error) = ReadFinalParent(before);
        outcome = outcome with { Error = outcome.Error != 0 ? outcome.Error : error };
        control.Finish(children.Child?.Identity ?? StartupDebugIdentity.Empty,
            outcome, parent);
        return outcome is { Error: 0, CleanupError: 0, Cleanup: true } && parent
            ? 0 : 1;
    }

    private static StartupDebugOutcome Observe(
        WindowsRestrictedOwnerTokens api, StartupDebugChildren children,
        CancellationToken cancellationToken)
    {
        uint exit = 0;
        try
        {
            using var token = RestrictedOwnerToken.Create(api);
            var result = RestrictedOwnerChild.RunInert(token, children, cancellationToken);
            exit = result.ExitCode;
            _ = token.Verify();
            return new(exit, result.CleanupCompleted, 0, 0);
        }
        catch (Exception error)
        {
            var (original, cleanup) = StartupDebugProbe.SplitFailure(error);
            return new(exit, children.Child?.IsClosed ?? false, original, cleanup);
        }
    }

    private static (bool Same, int Error) ReadFinalParent(StartupDebugParent before)
    {
        try { return (before.SameFinalState(StartupDebugParent.Read()), 0); }
        catch (Exception error) { return (false, StartupDebugProbe.ErrorCode(error)); }
    }
}
