using System.ComponentModel;
using System.Runtime.ExceptionServices;

namespace Hermes.Servicing.Acceptance;

internal enum AcceptancePowerShellProbeConsole { Detached, NoWindow, NewConsole }

internal interface IAcceptancePowerShellProbeImage
{
    string Read();
}

internal interface IAcceptancePowerShellProbeObservation
{
    AcceptancePowerShellProbeIoFacts PowerShellIoFacts { get; }
}

internal sealed record AcceptancePowerShellProbeIoFacts(
    uint CreationFlags,
    bool ProcessCreated,
    bool ImageMatched,
    int SourceBytes,
    bool StdinEof,
    bool PipesClosed,
    bool DrainsClosed,
    AcceptancePowerShellProbeDrainFacts Output,
    AcceptancePowerShellProbeDrainFacts Error)
{
    internal ImmutableArray<string> Rows(int arm) =>
    [
        FormattableString.Invariant(
            $"HERMES_RESTRICTED_POWERSHELL_LAUNCH v=1 arm={arm} flags={CreationFlags} created={Bit(ProcessCreated)} imageMatch={Bit(ImageMatched)} sourceBytes={SourceBytes}"),
        FormattableString.Invariant(
            $"HERMES_RESTRICTED_POWERSHELL_IO v=2 arm={arm} stdinEof={Bit(StdinEof)} pipesClosed={Bit(PipesClosed)} drainsClosed={Bit(DrainsClosed)}"),
        DrainRow(Output, arm, 0),
        DrainRow(Error, arm, 1)
    ];

    private static int Bit(bool value) => value ? 1 : 0;

    private static string DrainRow(
        AcceptancePowerShellProbeDrainFacts facts, int arm, int stream) =>
        FormattableString.Invariant(
            $"HERMES_RESTRICTED_POWERSHELL_DRAIN v=1 arm={arm} stream={stream} bytes={facts.Total} kept={facts.Retained} dropped={facts.Discarded} state={(int)facts.State} error={facts.Error}");
}

internal enum AcceptancePowerShellProbeReadKind { Pending, Data, End }

internal readonly record struct AcceptancePowerShellProbeRead(
    AcceptancePowerShellProbeReadKind Kind, int Count = 0);

// This capability reads only one of the closed probe's two output pipes.
// It cannot select an executable, command, token, handle, or source file.
internal interface IAcceptancePowerShellProbeOutput
{
    AcceptancePowerShellProbeRead Read(Span<byte> buffer);
}

internal enum AcceptancePowerShellProbeDrainState
{
    Draining,
    End,
    Cancelled,
    Faulted
}

internal sealed record AcceptancePowerShellProbeDrainFacts(
    long Total,
    int Retained,
    long Discarded,
    AcceptancePowerShellProbeDrainState State,
    int Error)
{
    internal static AcceptancePowerShellProbeDrainFacts Empty { get; } =
        new(0, 0, 0, AcceptancePowerShellProbeDrainState.Draining, 0);
}

internal sealed class AcceptancePowerShellProbeDrainOutcome(
    AcceptancePowerShellProbeDrainFacts facts, Exception? failure)
{
    internal AcceptancePowerShellProbeDrainFacts Facts { get; } = facts;
    internal Exception? Failure { get; } = failure;

    internal void ThrowIfFailed()
    {
        if (Failure is { } error)
            ExceptionDispatchInfo.Capture(error).Throw();
    }
}

internal static class AcceptancePowerShellProbeIoPolicy
{
    internal const int BufferBytes = 4096;
    internal const int RetainedBytes = 4096;
    internal const int DrainGraceMilliseconds = 5000;
    internal const int StopGraceMilliseconds = 1000;
    internal const uint CreationFlags = 0x00000004u | 0x00000008u | 0x00080000u;
    internal const uint StartupFlags = 0x00000100u;
    internal const int InheritHandles = 1;

    internal static uint CreationFlagsFor(AcceptancePowerShellProbeConsole console) =>
        console switch
        {
            AcceptancePowerShellProbeConsole.Detached => CreationFlags,
            AcceptancePowerShellProbeConsole.NoWindow =>
                (CreationFlags & ~0x00000008u) | 0x08000000u,
            AcceptancePowerShellProbeConsole.NewConsole =>
                (CreationFlags & ~0x00000008u) | 0x00000010u,
            _ => throw RestrictedOwnerToken.Failure(13)
        };

    internal static uint StartupFlagsFor(AcceptancePowerShellProbeConsole console) =>
        console switch
        {
            AcceptancePowerShellProbeConsole.Detached => StartupFlags,
            AcceptancePowerShellProbeConsole.NoWindow => StartupFlags,
            AcceptancePowerShellProbeConsole.NewConsole => StartupFlags | 0x00000001u,
            _ => throw RestrictedOwnerToken.Failure(13)
        };

    internal static void RequireImage(
        string expected, IAcceptancePowerShellProbeImage image)
    {
        if (!string.Equals(expected, image.Read(), StringComparison.OrdinalIgnoreCase))
            throw RestrictedOwnerToken.Failure(13);
    }

    internal static void RequireLayout((int Pointer, int Startup, int Extended) layout)
    {
        if (layout != (8, 104, 112))
            throw new InvalidDataException("Fixed PowerShell probe ABI refused.");
    }

    internal static void RequireHandles(ReadOnlySpan<nint> handles)
    {
        if (handles.Length != 3 ||
            handles[0] <= 0 || handles[1] <= 0 || handles[2] <= 0 ||
            handles[0] == handles[1] || handles[0] == handles[2] ||
            handles[1] == handles[2])
            throw new InvalidDataException("Fixed PowerShell probe handle list refused.");
    }

    internal static void RequireRead(AcceptancePowerShellProbeRead read)
    {
        var valid = read switch
        {
            { Kind: AcceptancePowerShellProbeReadKind.Pending, Count: 0 } => true,
            { Kind: AcceptancePowerShellProbeReadKind.End, Count: 0 } => true,
            { Kind: AcceptancePowerShellProbeReadKind.Data,
                Count: > 0 and <= BufferBytes } => true,
            _ => false
        };
        if (!valid)
            throw new InvalidDataException("Fixed PowerShell probe pipe read refused.");
    }

    internal static void CleanupAfterFailure(
        Exception original, params Action[] cleanup)
    {
        try { AcceptanceChildCleanup.Complete(cleanup); }
        catch (Exception failure)
        {
            throw new AggregateException(
                "Fixed PowerShell probe failed; cleanup incomplete.", original, failure);
        }
    }
}

internal sealed class AcceptancePowerShellProbeDrain(
    IAcceptancePowerShellProbeOutput output)
{
    private readonly byte[] _buffer =
        new byte[AcceptancePowerShellProbeIoPolicy.BufferBytes];
    private readonly byte[] _retained =
        new byte[AcceptancePowerShellProbeIoPolicy.RetainedBytes];
    private AcceptancePowerShellProbeDrainFacts _facts =
        AcceptancePowerShellProbeDrainFacts.Empty;
    private bool _started;

    internal AcceptancePowerShellProbeDrainOutcome Run(
        CancellationToken cancellationToken)
    {
        if (_started)
            throw RestrictedOwnerToken.Failure(6);
        _started = true;
        try
        {
            ReadToEnd(cancellationToken);
            return new(_facts, null);
        }
        catch (Exception error)
        {
            RecordFailure(error, cancellationToken);
            return new(_facts, error);
        }
        finally { Array.Clear(_buffer); Array.Clear(_retained); }
    }

    private void ReadToEnd(CancellationToken cancellationToken)
    {
        while (_facts.State == AcceptancePowerShellProbeDrainState.Draining)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = output.Read(_buffer);
            AcceptancePowerShellProbeIoPolicy.RequireRead(read);
            switch (read.Kind)
            {
                case AcceptancePowerShellProbeReadKind.Pending:
                    Thread.Sleep(10);
                    break;
                case AcceptancePowerShellProbeReadKind.Data:
                    KeepPrefixAndCountDiscarded(read.Count);
                    break;
                case AcceptancePowerShellProbeReadKind.End:
                    _facts = _facts with { State = AcceptancePowerShellProbeDrainState.End };
                    break;
            }
        }
    }

    private void KeepPrefixAndCountDiscarded(int count)
    {
        var kept = Math.Min(_retained.Length - _facts.Retained, count);
        _buffer.AsSpan(0, kept).CopyTo(_retained.AsSpan(_facts.Retained));
        _facts = _facts with
        {
            Total = checked(_facts.Total + count),
            Retained = _facts.Retained + kept,
            Discarded = checked(_facts.Discarded + count - kept)
        };
    }

    private void RecordFailure(Exception error, CancellationToken cancellationToken)
    {
        var cancelled = error is OperationCanceledException &&
            cancellationToken.IsCancellationRequested;
        var code = error switch
        {
            OperationCanceledException when cancelled => 1223,
            Win32Exception { NativeErrorCode: > 0 } native => native.NativeErrorCode,
            _ => 13
        };
        _facts = _facts with
        {
            State = cancelled ? AcceptancePowerShellProbeDrainState.Cancelled
                : AcceptancePowerShellProbeDrainState.Faulted,
            Error = code
        };
    }
}

internal sealed class AcceptancePowerShellProbeDrains(
    IAcceptancePowerShellProbeOutput stdout,
    IAcceptancePowerShellProbeOutput stderr) : IDisposable
{
    private readonly ImmutableArray<AcceptancePowerShellProbeDrain> _drains =
        [new(stdout), new(stderr)];
    private readonly List<Task<AcceptancePowerShellProbeDrainOutcome>> _tasks = [];
    private readonly CancellationTokenSource _stop = new();
    private bool _started;
    private bool _disposed;
    private bool _cleanupCompleted;

    internal bool IsClosed => _disposed && _cleanupCompleted;

    internal (AcceptancePowerShellProbeDrainFacts Output,
        AcceptancePowerShellProbeDrainFacts Error) Facts =>
        (ReadFacts(0), ReadFacts(1));

    internal void Start()
    {
        RequireOpen();
        if (_started)
            throw RestrictedOwnerToken.Failure(13);
        _started = true;
        foreach (var drain in _drains)
            _tasks.Add(Task.Factory.StartNew(
                () => drain.Run(_stop.Token), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default));
    }

    internal void Check()
    {
        RequireOpen();
        if (!_started)
            throw RestrictedOwnerToken.Failure(13);
        CheckCompleted();
    }

    private void CheckCompleted()
    {
        foreach (var task in _tasks.Where(task => task.IsCompleted))
            task.GetAwaiter().GetResult().ThrowIfFailed();
    }

    private AcceptancePowerShellProbeDrainFacts ReadFacts(int index) =>
        index < _tasks.Count && _tasks[index].IsCompletedSuccessfully
            ? _tasks[index].Result.Facts
            : AcceptancePowerShellProbeDrainFacts.Empty;

    private void Finish()
    {
        if (!_started)
            return;
        if (_tasks.Count != 2)
            throw RestrictedOwnerToken.Failure(13);
        if (!Task.WaitAll(_tasks.ToArray(),
            AcceptancePowerShellProbeIoPolicy.DrainGraceMilliseconds))
            throw RestrictedOwnerToken.Failure(1460);
        CheckCompleted();
        if (_tasks.Any(task =>
            task.Result.Facts.State != AcceptancePowerShellProbeDrainState.End))
            throw RestrictedOwnerToken.Failure(13);
    }

    private void Stop()
    {
        _stop.Cancel();
        if (!Task.WaitAll(_tasks.ToArray(),
            AcceptancePowerShellProbeIoPolicy.StopGraceMilliseconds))
            throw RestrictedOwnerToken.Failure(1460);
    }

    private void RequireOpen()
    {
        if (_disposed)
            throw RestrictedOwnerToken.Failure(6);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        AcceptanceChildCleanup.Complete(Finish, Stop, _stop.Dispose);
        _cleanupCompleted = true;
    }
}
