using System.ComponentModel;

namespace Hermes.Servicing.Acceptance;

[Flags]
internal enum StepJobLimits : uint
{
    None = 0,
    Breakaway = 0x0800,
    SilentBreakaway = 0x1000,
    KillOnClose = 0x2000
}

internal sealed record StepJobContext(
    bool Windows,
    string? Actions,
    string? RunnerOs,
    string? RunnerEnvironment,
    string? Job,
    string? Server,
    string? Repository)
{
    internal void Require()
    {
        if (this is not
            {
                Windows: true, Actions: "true", RunnerOs: "Windows",
                RunnerEnvironment: "github-hosted", Job: "installer",
                Server: "https://github.com", Repository: "johnazariah/hermes"
            })
            throw new InvalidOperationException("Private step-job admission refused.");
    }
}

internal sealed record StepJobReadback(
    StepJobLimits Limits,
    bool CurrentIsMember,
    uint UiRestrictions,
    uint HandleFlags,
    uint ActiveProcesses)
{
    internal void Require(
        bool currentIsMember, uint activeProcesses, StepJobLimits limits)
    {
        if (Limits != limits || CurrentIsMember != currentIsMember ||
            UiRestrictions != 0 || HandleFlags != 0 ||
            ActiveProcesses != activeProcesses)
            throw new InvalidDataException("Private step-job readback refused.");
    }
}

internal sealed record StepJob(
    Action Configure,
    Action AssignCurrent,
    Func<StepJobReadback> Read,
    Action ClearKillOnClose,
    Action CloseEmpty,
    Action AbortAssigned,
    Action ReleaseVerified);

internal sealed record StepJobCapabilities(
    Func<StepJobContext> ReadContext,
    Func<StepJob> Create);

internal sealed class StepJobBootstrap(StepJobCapabilities capabilities)
{
    private int _attempted;

    internal StepJobScope Begin()
    {
        capabilities.ReadContext().Require();
        if (Interlocked.CompareExchange(ref _attempted, 1, 0) != 0)
            throw new InvalidOperationException("Private step-job bootstrap is single-use.");
        return StepJobScope.Open(capabilities.Create());
    }
}

internal sealed class StepJobScope : IDisposable
{
    private readonly StepJob _job;
    private readonly object _gate = new();
    private bool _assigned;
    private bool _closed;

    private StepJobScope(StepJob job) => _job = job;

    internal static StepJobScope Open(StepJob job)
    {
        var scope = new StepJobScope(job);
        var transferred = false;
        try
        {
            scope.Prepare();
            transferred = true;
            return scope;
        }
        finally
        {
            if (!transferred)
                scope.Abandon();
        }
    }

    private void Prepare()
    {
        _job.Configure();
        _job.Read().Require(false, 0, StepJobLimits.KillOnClose);
        _job.AssignCurrent();
        _assigned = true;
        _job.Read().Require(true, 1, StepJobLimits.KillOnClose);
    }

    internal void CompleteAfterChildrenExited()
    {
        lock (_gate)
            Complete();
    }

    private void Complete()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        var completed = false;
        try
        {
            _job.Read().Require(true, 1, StepJobLimits.KillOnClose);
            _job.ClearKillOnClose();
            _job.Read().Require(true, 1, StepJobLimits.None);
            _job.ReleaseVerified();
            _closed = true;
            completed = true;
        }
        finally
        {
            if (!completed)
                Abandon();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_closed)
                return;
            Abandon();
            throw new InvalidOperationException(
                "Private step-job scope was abandoned, not completed.");
        }
    }

    private void Abandon()
    {
        if (_closed)
            return;
        _closed = true;
        if (_assigned)
            _job.AbortAssigned();
        else
            _job.CloseEmpty();
    }
}

internal enum StepJobHandleOperation
{
    Close,
    Terminate
}

internal sealed record StepJobHandleFailure(
    StepJobHandleOperation Operation,
    int Error)
{
    internal Win32Exception ToException() => new(Error, Operation switch
    {
        StepJobHandleOperation.Terminate => "Private step-job termination failed.",
        _ => "Private step-job handle close failed."
    });
}

internal sealed record StepJobHandleFinalization(StepJobHandleFailure? Failure)
{
    internal bool Released => Failure is null;
}

internal sealed record StepJobHandleCapabilities(
    Func<int> Close,
    Func<int> Terminate);

// The Windows adapter invalidates its SafeHandle only when Close returns zero.
// Failed checked operations retain ownership and remain retryable until finalization.
internal sealed class StepJobHandleOwner(StepJobHandleCapabilities capabilities)
{
    private readonly object _gate = new();
    private bool _terminateOnAbandon;
    private bool _closed;
    private StepJobHandleFinalization? _finalization;

    internal void ArmTermination()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed || _finalization is not null, this);
            _terminateOnAbandon = true;
        }
    }

    // SafeHandle cannot retry after ReleaseHandle. Freeze one final attempt:
    // failure records an unrecoverable, process-lifetime raw resource, not
    // reusable SafeHandle ownership. There is no host-crash/throw fallback and
    // no guarantee that children died, particularly after KillOnClose cleared.
    internal StepJobHandleFinalization FinalizeOwnership()
    {
        lock (_gate)
            return _finalization ??= new(Abandon());
    }

    internal StepJobHandleFailure? ReleaseVerified()
    {
        lock (_gate)
            return Close();
    }

    internal StepJobHandleFailure? Abandon()
    {
        lock (_gate)
        {
            if (_finalization is { } finalization)
                return finalization.Failure;
            if (_closed)
                return null;
            if (_terminateOnAbandon &&
                capabilities.Terminate() is not 0 and var error)
                return new(StepJobHandleOperation.Terminate, error);
            return Close();
        }
    }

    private StepJobHandleFailure? Close()
    {
        if (_finalization is { } finalization)
            return finalization.Failure;
        if (_closed)
            return null;
        if (capabilities.Close() is not 0 and var error)
            return new(StepJobHandleOperation.Close, error);
        _closed = true;
        return null;
    }
}

internal sealed class StepJobOwnedHandles(
    StepJobHandleOwner job,
    StepJobHandleOwner? current)
{
    internal void ReleaseVerified()
    {
        // Failure must stop release while the job is still owned and armed.
        // Neither checked close changes termination intent before native success.
        if (current?.ReleaseVerified() is { } currentFailure)
            throw currentFailure.ToException();
        if (job.ReleaseVerified() is { } jobFailure)
            throw jobFailure.ToException();
    }

    internal void Abandon()
    {
        // Terminate the assigned job before another process-handle close can fail.
        // A termination error leaves the job owned; report both cleanup failures.
        var errors = new[] { job.Abandon(), current?.Abandon() }
            .OfType<StepJobHandleFailure>()
            .Select(failure => (Exception)failure.ToException())
            .ToArray();
        if (errors.Length != 0)
            throw errors.Length == 1 ? errors[0] : new AggregateException(errors);
    }
}
