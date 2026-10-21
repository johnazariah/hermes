using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Text;
using Hermes.Servicing;

namespace Hermes.Servicing.Acceptance;

internal interface ISetupChild
{
    Stream StandardOutput { get; }
    Stream StandardError { get; }
    void CloseStandardOutput();
    void CloseStandardError();
    bool HasExited { get; }
    bool IsOwned { get; }
    void Kill();
    Task<int> WaitAsync(CancellationToken cancellationToken);
}

internal sealed record SetupChildBudget(
    TimeSpan Run, TimeSpan Cleanup, TimeProvider Clock)
{
    internal static SetupChildBudget Default { get; } =
        new(TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10), TimeProvider.System);
}

internal sealed record SetupChildResult(int ExitCode, SetupReport Report);

internal enum SetupChildFault
{
    InvalidReport = 1,
    InvalidDiagnostics,
    UnexpectedExit,
    ExitMismatch,
    PrematureEof,
    StdoutLimit,
    StderrLimit,
    Timeout,
    Cancelled,
    OsFailure,
    CleanupFailed
}

// ProofFiles.Failure already records exception type and HRESULT, not raw output.
internal sealed class SetupChildException : IOException
{
    internal SetupChildFault Fault { get; }

    internal SetupChildException(SetupChildFault fault, Exception? inner = null)
        : base("Setup child transport failed.", inner)
    {
        Fault = fault;
        HResult = unchecked((int)0x8004A100) | (int)fault;
    }
}

internal static partial class MsiCases
{
    internal const int SetupOutputBytes = 4096;

    internal sealed record SetupDrains(
        Task<string> Output, Task<string> Error, Task<int> Exit)
    {
        internal IReadOnlyList<Task> Tasks => [Output, Error, Exit];
    }

    // This is the consumer called by the real context-based MainAsync.
    internal static Task<SetupChildResult> MainAsync(
        ISetupChild child, SetupVerb operation, SetupChildBudget budget,
        CancellationToken cancellationToken) =>
        MainAsync(child, operation, budget, null, cancellationToken);

    // Test-only observation publishes the actual operations, never substitutes I/O.
    internal static async Task<SetupChildResult> MainAsync(
        ISetupChild child, SetupVerb operation, SetupChildBudget budget,
        TaskCompletionSource<SetupDrains>? observedDrains,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(budget.Run, budget.Clock);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        var drains = StartSetupDrains(child, lifetime.Token);
        observedDrains?.TrySetResult(drains);
        return await RunSetupAsync(child, operation, budget, drains, lifetime,
            cancellationToken, deadline.Token);
    }

    private static async Task<SetupChildResult> RunSetupAsync(
        ISetupChild child, SetupVerb operation, SetupChildBudget budget,
        SetupDrains drains, CancellationTokenSource lifetime,
        CancellationToken caller, CancellationToken deadline)
    {
        SetupChildResult result;
        try { result = await DrainSetupAsync(child, operation, drains, lifetime); }
        catch (Exception error)
        {
            lifetime.Cancel();
            var failure = SetupFailure(error, caller, deadline);
            await CleanupSetupAsync(child, drains, budget, failure);
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        await CleanupSetupAsync(child, drains, budget, null);
        return result;
    }

    private static SetupDrains StartSetupDrains(
        ISetupChild child, CancellationToken cancellationToken)
    {
        var drains = new SetupDrains(
            ReadSetupPipeAsync(child, false, cancellationToken),
            ReadSetupPipeAsync(child, true, cancellationToken),
            WaitSetupAsync(child, cancellationToken));
        foreach (var task in drains.Tasks)
            _ = ObserveSetupTask(task);
        return drains;
    }

    private static async Task<SetupChildResult> DrainSetupAsync(
        ISetupChild child, SetupVerb operation, SetupDrains drains,
        CancellationTokenSource lifetime)
    {
        var stdout = SetupPartAsync(drains.Output, lifetime);
        var stderr = SetupPartAsync(drains.Error, lifetime);
        var exit = SetupPartAsync(drains.Exit, lifetime);
        await FinishSetupPartsAsync([stdout, stderr, exit]);
        lifetime.Token.ThrowIfCancellationRequested();
        SetupOutput.Require(child.HasExited, SetupChildFault.UnexpectedExit);
        return SetupOutput.Parse(await stdout, await stderr, await exit, operation);
    }

    private static async Task<T> SetupPartAsync<T>(
        Task<T> task, CancellationTokenSource lifetime)
    {
        try { return await task.WaitAsync(lifetime.Token); }
        catch { lifetime.Cancel(); throw; }
    }

    private static async Task FinishSetupPartsAsync(IReadOnlyList<Task> tasks)
    {
        try { await Task.WhenAll(tasks); }
        catch
        {
            var failures = tasks.Where(task => task.IsFaulted)
                .SelectMany(task => task.Exception is { } error
                    ? error.InnerExceptions.AsEnumerable()
                    : Enumerable.Empty<Exception>()).ToArray();
            if (failures.Length > 0)
                ExceptionDispatchInfo.Capture(failures switch
                {
                    [var failure] => failure,
                    _ => new AggregateException(failures)
                }).Throw();
            throw;
        }
    }

    private static async Task<string> ReadSetupPipeAsync(
        ISetupChild child, bool stderr, CancellationToken cancellationToken) =>
        await ReadSetupAsync(
            stderr ? child.StandardError : child.StandardOutput,
            stderr ? SetupChildFault.StderrLimit : SetupChildFault.StdoutLimit,
            cancellationToken);

    private static async Task<int> WaitSetupAsync(
        ISetupChild child, CancellationToken cancellationToken) =>
        await child.WaitAsync(cancellationToken);

    private static async Task<string> ReadSetupAsync(
        Stream stream, SetupChildFault limit, CancellationToken cancellationToken)
    {
        var bytes = new byte[SetupOutputBytes + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken);
            if (read == 0)
                return DecodeSetup(bytes, count, limit);
            count += read;
        }
        throw new SetupChildException(limit);
    }

    private static string DecodeSetup(byte[] bytes, int count, SetupChildFault limit)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, count); }
        catch (DecoderFallbackException error)
        {
            throw new SetupChildException(limit == SetupChildFault.StdoutLimit
                ? SetupChildFault.InvalidReport : SetupChildFault.InvalidDiagnostics, error);
        }
    }

    private static Exception SetupFailure(
        Exception error, CancellationToken caller, CancellationToken deadline) => error switch
    {
        SetupChildException => error,
        OperationCanceledException when caller.IsCancellationRequested =>
            new SetupChildException(SetupChildFault.Cancelled, error),
        OperationCanceledException when deadline.IsCancellationRequested =>
            new SetupChildException(SetupChildFault.Timeout, error),
        IOException or Win32Exception or UnauthorizedAccessException =>
            new SetupChildException(SetupChildFault.OsFailure, error),
        _ => error
    };

    private static async Task CleanupSetupAsync(
        ISetupChild child, SetupDrains drains, SetupChildBudget budget,
        Exception? failure)
    {
        using var deadline = new CancellationTokenSource(budget.Cleanup, budget.Clock);
        var cleanup = StartSetupCleanup(child, drains, deadline.Token);
        try
        {
            await ObserveSetupTask(Task.WhenAll(cleanup)).WaitAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            RequireSetupDrainsSettled(drains, failure);
        }
        catch (Exception error)
        {
            var cause = SetupCleanupCause(error, cleanup, drains, failure);
            throw new SetupChildException(SetupChildFault.CleanupFailed,
                failure is { } initiating
                    ? new AggregateException(initiating, cause) : cause);
        }
    }

    private static IReadOnlyList<Task> StartSetupCleanup(
        ISetupChild child, SetupDrains drains, CancellationToken cancellationToken) =>
        [
            ObserveSetupTask(Task.Run(child.CloseStandardOutput)),
            ObserveSetupTask(Task.Run(child.CloseStandardError)),
            ObserveSetupTask(Task.Run(() => ReapSetupAsync(child, cancellationToken))),
            .. drains.Tasks.Select(SettleSetupTask)
        ];

    private static async Task ReapSetupAsync(
        ISetupChild child, CancellationToken cancellationToken)
    {
        StopOwnedSetup(child);
        await child.WaitAsync(cancellationToken);
        SetupOutput.Require(child.HasExited, SetupChildFault.CleanupFailed);
    }

    private static TTask ObserveSetupTask<TTask>(TTask task) where TTask : Task
    {
        _ = SettleSetupTask(task);
        return task;
    }

    // Observe faults even when cleanup times out. Settlement alone is not success:
    // RequireSetupDrainsSettled also checks faults against the initiating failure.
    private static Task SettleSetupTask(Task task) =>
        task.ContinueWith(completed => { _ = completed.Exception; },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static IEnumerable<Exception> SetupTaskFaults(IEnumerable<Task> tasks) =>
        tasks.Where(task => task.IsFaulted)
            .SelectMany(task => task.Exception is { } error
                ? error.Flatten().InnerExceptions.AsEnumerable()
                : Enumerable.Empty<Exception>());

    private static IEnumerable<Exception> UnexpectedSetupDrainFaults(
        SetupDrains drains, Exception? failure) =>
        SetupTaskFaults(drains.Tasks).Where(error =>
            error is not (OperationCanceledException or ObjectDisposedException) &&
            !IsInitiatingSetupFailure(failure, error));

    private static bool IsInitiatingSetupFailure(
        Exception? failure, Exception error) =>
        ReferenceEquals(failure, error) || (failure switch
        {
            AggregateException aggregate => aggregate.InnerExceptions.Any(
                inner => IsInitiatingSetupFailure(inner, error)),
            { InnerException: { } inner } => IsInitiatingSetupFailure(inner, error),
            _ => false
        });

    private static void RequireSetupDrainsSettled(
        SetupDrains drains, Exception? failure)
    {
        SetupOutput.Require(drains.Tasks.All(task => task.IsCompleted),
            SetupChildFault.CleanupFailed);
        var faults = UnexpectedSetupDrainFaults(drains, failure).ToArray();
        if (faults.Length > 0)
            throw new AggregateException(faults);
    }

    private static Exception SetupCleanupCause(
        Exception error, IReadOnlyList<Task> cleanup, SetupDrains drains,
        Exception? failure)
    {
        var faults = SetupTaskFaults(cleanup)
            .Concat(UnexpectedSetupDrainFaults(drains, failure))
            .Where(fault => !ReferenceEquals(fault, error))
            .Prepend(error).ToArray();
        return faults switch
        {
            [var single] => single,
            _ => new AggregateException(faults)
        };
    }

    private static void StopOwnedSetup(ISetupChild child)
    {
        try
        {
            if (child.HasExited)
                return;
            SetupOutput.Require(child.IsOwned, SetupChildFault.CleanupFailed);
            child.Kill();
        }
        catch (Exception error) when (
            (error is InvalidOperationException or Win32Exception) && child.HasExited)
        {
            // The owned process exited between the live check and termination.
        }
    }
}
