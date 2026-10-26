using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace Hermes.Servicing.Acceptance;

// No executable, command, token, desktop, configuration, or handle input.
internal interface IAcceptanceSourceControlFactory
{
    IAcceptanceSourceControlScope OpenSourceControl();
}

internal interface IAcceptanceSourceControlScope : IDisposable
{
    bool IsClosed { get; }
    AcceptanceSourceControlProgress Progress { get; }
    void VerifySourceHighSnapshot();
    void CreateKillOnCloseJob();
    void CreateSuspended();
    void AssignJob();
    void VerifyChildHighSnapshotAndThreadAbsence();
    void ReadObjectAccess();
    void Resume();
    uint Wait(CancellationToken cancellationToken);
}

internal interface IAcceptanceSourceControlOutput
{
    void WriteLine(string row);
}

// Deliberately separate from the filtered Medium-role facts.
internal sealed record AcceptanceSourceHighFacts(
    int TokenType,
    int ElevationType,
    int Elevated,
    int Integrity,
    int AdminEnabled,
    int AdminDenyOnly,
    int SameUser,
    int SameSession)
{
    internal static AcceptanceSourceHighFacts Required { get; } =
        new(1, 1, 1, 12288, 1, 0, 1, 1);

    internal void Require()
    {
        if (this != Required)
            throw new Win32Exception(13, "Owned source High verification refused.");
    }
}

internal sealed record AcceptanceSourceControlIo(
    bool Created,
    bool ImageMatched,
    int SourceBytes,
    bool StdinEof,
    bool PipesClosed,
    bool DrainsClosed,
    long OutputBytes,
    long ErrorBytes,
    int OutputState,
    int ErrorState,
    int OutputError,
    int ErrorError)
{
    internal static AcceptanceSourceControlIo Empty { get; } =
        new(false, false, 0, false, false, false, 0, 0, 0, 0, 0, 0);

    internal bool Complete =>
        Created && ImageMatched && SourceBytes is 8 or 9 &&
        StdinEof && PipesClosed && DrainsClosed &&
        OutputBytes >= 0 && ErrorBytes >= 0 &&
        OutputState == 1 && ErrorState == 1 &&
        OutputError == 0 && ErrorError == 0;
}

internal sealed record AcceptanceSourceControlProgress(
    bool SourceHigh,
    bool ChildHigh,
    bool Resumed,
    AcceptanceSourceControlIo Io)
{
    internal static AcceptanceSourceControlProgress Empty { get; } =
        new(false, false, false, AcceptanceSourceControlIo.Empty);
}

internal sealed record AcceptanceSourceControlObservation(
    uint? ExitCode,
    int Error,
    int CleanupError,
    bool CleanupCompleted,
    AcceptanceSourceControlProgress Progress)
{
    internal const uint CreationFlags = 0x08080004u;

    // Conclusive observation is not an acceptance predicate for the exit value.
    internal bool IsConclusive =>
        ExitCode is { } && Error == 0 && CleanupError == 0 &&
        CleanupCompleted && Progress.SourceHigh && Progress.ChildHigh &&
        Progress.Resumed && Progress.Io.Complete;

    internal string Row => FormattableString.Invariant(
        $"HERMES_RESTRICTED_SOURCE_CONTROL v=1 role=1 flags={CreationFlags} state={Bit(IsConclusive)} sourceHigh={Bit(Progress.SourceHigh)} childHigh={Bit(Progress.ChildHigh)} resumed={Bit(Progress.Resumed)} exitObserved={Bit(ExitCode is { })} exit={ExitCode.GetValueOrDefault()} error={Error} cleanupError={CleanupError} closed={Bit(CleanupCompleted)} created={Bit(Progress.Io.Created)} imageMatch={Bit(Progress.Io.ImageMatched)} sourceBytes={Progress.Io.SourceBytes} stdinEof={Bit(Progress.Io.StdinEof)} pipesClosed={Bit(Progress.Io.PipesClosed)} drainsClosed={Bit(Progress.Io.DrainsClosed)} outBytes={Progress.Io.OutputBytes} errBytes={Progress.Io.ErrorBytes} outState={Progress.Io.OutputState} errState={Progress.Io.ErrorState} outError={Progress.Io.OutputError} errError={Progress.Io.ErrorError}");

    private static int Bit(bool value) => value ? 1 : 0;
}

internal sealed class AcceptanceSourceControlCreationDeniedException(int code)
    : Win32Exception(code, "Owned source control creation denied.");

internal sealed record AcceptanceSourceControlAttempt(
    AcceptanceSourceControlObservation Observation,
    Exception? Failure);

internal static class AcceptanceSourceControlOrchestration
{
    // Formatting and output are both inside Capture. Neither can skip the gate.
    // The original gate is primary even though reporting happens first.
    internal static void Complete(
        AcceptanceSourceControlAttempt attempt,
        IAcceptanceSourceControlOutput output,
        Action originalGate)
    {
        var reporting = Capture(() => output.WriteLine(attempt.Observation.Row));
        var original = Capture(originalGate);
        Throw(Combine(original, attempt.Failure, reporting));
    }

    internal static Exception? Capture(Action operation)
    {
        try { operation(); return null; }
        catch (Exception error) { return error; }
    }

    internal static void RunAll(params Action[] operations) =>
        Throw(Combine(operations.Select(Capture).ToArray()));

    internal static void Throw(Exception? failure)
    {
        if (failure is { } error)
            ExceptionDispatchInfo.Capture(error).Throw();
    }

    // Suppress only later input roots reachable from earlier retained roots.
    // Retained graphs, including prior Combine results, are never rewritten:
    // repetitions, shared descendants, and later envelopes remain intact.
    internal static Exception? Combine(params Exception?[] errors)
    {
        var represented = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        return Pack(errors.OfType<Exception>()
            .Where(error => RememberGraph(error, represented)).ToArray());
    }

    private static bool RememberGraph(
        Exception error, HashSet<Exception> represented)
    {
        if (!represented.Add(error))
            return false;
        IEnumerable<Exception> children = error switch
        {
            AggregateException aggregate => aggregate.InnerExceptions,
            { InnerException: { } inner } => [inner],
            _ => []
        };
        foreach (var child in children)
            RememberGraph(child, represented);
        return true;
    }

    private static Exception? Pack(IReadOnlyList<Exception> errors) => errors switch
    {
        [] => null,
        [var only] => only,
        _ => new AggregateException("Owned source control proof failed.", errors)
    };

    internal static IEnumerable<Exception> Leaves(Exception error) =>
        error is AggregateException { InnerExceptions.Count: > 0 } aggregate
            ? aggregate.InnerExceptions.SelectMany(Leaves)
            : [error];

    internal static int Code(Exception? error) => error is null
        ? 0
        : Leaves(error).Select(CodeLeaf).FirstOrDefault(13);

    private static int CodeLeaf(Exception error) => error switch
    {
        Win32Exception { NativeErrorCode: > 0 } native => native.NativeErrorCode,
        OperationCanceledException => 1223,
        _ => 13
    };
}

// The production native scope and portable controls share this ownership order.
// The held source is not released before child cleanup and final verification.
internal sealed class AcceptanceSourceControlLifetime(
    Action closeChild,
    Action verifySource,
    Action closeSource,
    Func<bool> resourcesClosed) : IDisposable
{
    private bool _disposed;
    private bool _completed;

    internal bool IsDisposed => _disposed;
    internal bool IsClosed => _disposed && _completed && resourcesClosed();

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AcceptanceSourceControlOrchestration.RunAll(
            closeChild, verifySource, closeSource);
        _completed = true;
    }
}

internal static class RestrictedSourcePowerShellControl
{
    private sealed record Completion(
        AcceptanceSourceControlProgress Progress,
        bool Closed,
        Exception? Failure);

    internal static AcceptanceSourceControlAttempt Observe(
        IAcceptanceSourceControlFactory factory,
        CancellationToken cancellationToken = default)
    {
        IAcceptanceSourceControlScope? scope = null;
        Execution? execution = null;
        uint? exit = null;
        var failure = AcceptanceSourceControlOrchestration.Capture(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            scope = factory.OpenSourceControl();
            execution = new(scope, cancellationToken);
            exit = execution.Run();
        });
        return Finish(exit, failure, execution?.Creating == true, Close(scope));
    }

    private static Completion Close(IAcceptanceSourceControlScope? scope)
    {
        if (scope is null)
            return new(AcceptanceSourceControlProgress.Empty, true, null);
        var progress = AcceptanceSourceControlProgress.Empty;
        var closed = false;
        var dispose = AcceptanceSourceControlOrchestration.Capture(scope.Dispose);
        var evidence = AcceptanceSourceControlOrchestration.Capture(
            () => progress = scope.Progress);
        var state = AcceptanceSourceControlOrchestration.Capture(
            () => closed = scope.IsClosed);
        var failure = AcceptanceSourceControlOrchestration.Combine(dispose, evidence, state);
        failure ??= closed ? null :
            new InvalidOperationException("Owned source cleanup incomplete.");
        return new(progress, closed, failure);
    }

    private static AcceptanceSourceControlAttempt Finish(
        uint? exit, Exception? failure, bool creating, Completion completion)
    {
        var observation = new AcceptanceSourceControlObservation(
            exit, AcceptanceSourceControlOrchestration.Code(failure),
            AcceptanceSourceControlOrchestration.Code(completion.Failure),
            completion.Closed, completion.Progress);
        var denied = creating && failure is AcceptanceSourceControlCreationDeniedException
            { NativeErrorCode: 5 or 1314 } && completion.Failure is null &&
            completion.Closed && completion.Progress.SourceHigh &&
            !completion.Progress.Io.Created;
        var unsafeFailure = denied ? null :
            AcceptanceSourceControlOrchestration.Combine(failure, completion.Failure);
        if (!denied && unsafeFailure is null && !observation.IsConclusive)
            unsafeFailure = new InvalidOperationException("Owned source evidence incomplete.");
        return new(observation with
        {
            Error = AcceptanceSourceControlOrchestration.Code(failure ?? unsafeFailure)
        }, unsafeFailure);
    }

    private sealed class Execution(
        IAcceptanceSourceControlScope scope, CancellationToken cancellationToken)
    {
        internal bool Creating { get; private set; }

        internal uint Run()
        {
            Prepare();
            Creating = true;
            scope.CreateSuspended();
            Creating = false;
            scope.AssignJob();
            scope.VerifyChildHighSnapshotAndThreadAbsence();
            scope.VerifySourceHighSnapshot();
            cancellationToken.ThrowIfCancellationRequested();
            scope.ReadObjectAccess();
            scope.VerifySourceHighSnapshot();
            cancellationToken.ThrowIfCancellationRequested();
            scope.Resume();
            return scope.Wait(cancellationToken);
        }

        private void Prepare()
        {
            scope.VerifySourceHighSnapshot();
            cancellationToken.ThrowIfCancellationRequested();
            scope.CreateKillOnCloseJob();
            cancellationToken.ThrowIfCancellationRequested();
            scope.VerifySourceHighSnapshot();
        }
    }
}
