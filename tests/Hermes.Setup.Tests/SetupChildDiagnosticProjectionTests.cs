using Hermes.Servicing;
using Hermes.Servicing.Acceptance;
using Xunit;
using Xunit.Sdk;

namespace Hermes.Setup.Tests;

public sealed partial class SetupChildTransportTests
{
    private static async Task ObserveDiagnosticFailureAsync(Child child)
    {
        var observed = DiagnosticSource<MsiCases.SetupDrains>();
        var operation = MsiCases.MainAsync(
            child, SetupVerb.Install, Budget, observed, CancellationToken.None);
        var bounded = operation.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await FaultAsync(bounded, SetupChildFault.InvalidDiagnostics);
        }
        catch (XunitException initiating)
        {
            PublishDiagnosticFailure(
                initiating,
                () => ProjectDiagnosticFailure(
                    child, operation, bounded, observed.Task).ToLine(),
                Console.Error.WriteLine);
            throw;
        }
    }

    private static void PublishDiagnosticFailure(
        XunitException initiating, Func<string> project, Action<string> emit)
    {
        try { emit(project()); }
        catch (Exception secondary)
        {
            throw new AggregateException(initiating, secondary);
        }
    }

    private sealed record DiagnosticFailureProjection(
        int Bounded, int Operation, int Kind, int OperationKind, int Observed,
        int Stdout, int Stderr, int Exit, int Fault, int OperationFault,
        int Initiating, int Checks, int Closed, int Exited, int Kills)
    {
        internal string ToLine() => FormattableString.Invariant(
            $"HERMES_SETUP_DIAGNOSTIC_ASSERT v=1 bounded={Bounded} operation={Operation} kind={Kind} operationKind={OperationKind} observed={Observed} stdout={Stdout} stderr={Stderr} exit={Exit} fault={Fault} operationFault={OperationFault} initiating={Initiating} checks={Checks} closed={Closed} exited={Exited} kills={Kills}");
    }

    private static DiagnosticFailureProjection ProjectDiagnosticFailure(
        Child child, Task<SetupChildResult> operation, Task<SetupChildResult> bounded,
        Task<MsiCases.SetupDrains> observed)
    {
        var drains = observed.IsCompletedSuccessfully ? observed.Result : null;
        var actual = DiagnosticException(operation);
        var error = DiagnosticException(bounded);
        return new(
            (int)bounded.Status, (int)operation.Status,
            DiagnosticKind(bounded), DiagnosticKind(operation), drains is null ? 0 : 1,
            DiagnosticStatus(drains?.Output), DiagnosticStatus(drains?.Error),
            DiagnosticStatus(drains?.Exit), DiagnosticFault(error), DiagnosticFault(actual),
            DiagnosticInitiating(actual), DiagnosticChecks(error),
            (child.StandardOutput.CanRead ? 0 : 1) |
                (child.StandardError.CanRead ? 0 : 2),
            child.HasExited ? 1 : 0, Math.Clamp(child.Kills, 0, 2));
    }

    private static int DiagnosticStatus(Task? task) =>
        task is { } value ? (int)value.Status : -1;

    private static Exception? DiagnosticException(Task task) =>
        task.Exception is { InnerExceptions: [var error] } ? error : null;

    private static int DiagnosticKind(Task task) => task switch
    {
        { IsCompleted: false } => 0,
        { IsCompletedSuccessfully: true } => 1,
        { IsCanceled: true } => 4,
        _ => DiagnosticException(task) switch
        {
            SetupChildException => 2,
            TimeoutException => 3,
            _ => 5
        }
    };

    private static int DiagnosticFault(Exception? error) => error switch
    {
        SetupChildException child when Enum.IsDefined(child.Fault) => (int)child.Fault,
        SetupChildException => -1,
        _ => 0
    };

    private static int DiagnosticInitiating(Exception? error) => error switch
    {
        SetupChildException
        {
            InnerException: AggregateException { InnerExceptions: [var first, ..] }
        } => DiagnosticFault(first),
        _ => 0
    };

    private static int DiagnosticChecks(Exception? error) =>
        error is SetupChildException child
            ? 1 |
                (child.Fault == SetupChildFault.InvalidDiagnostics ? 2 : 0) |
                (child.HResult == unchecked((int)0x8004A102) ? 4 : 0) |
                (child.Message == "Setup child transport failed." ? 8 : 0)
            : 0;

    private static TaskCompletionSource<T> DiagnosticSource<T>() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostic_projection_preserves_assertion_when_evidence_production_fails(
        bool projectionFails)
    {
        var initiating = new XunitException("synthetic initiating assertion");
        Exception secondary = projectionFails
            ? new InvalidOperationException() : new IOException();
        var writes = 0;
        var aggregate = Assert.Throws<AggregateException>(() => PublishDiagnosticFailure(
            initiating, () => projectionFails ? throw secondary : "0",
            _ => { writes++; throw secondary; }));
        Assert.Collection(aggregate.InnerExceptions,
            error => Assert.Same(initiating, error),
            error => Assert.Same(secondary, error));
        Assert.Equal(projectionFails ? 0 : 1, writes);
    }

    // Synthetic tasks below are pure controls, never subject-observation substitutes.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Diagnostic_projection_reports_published_control_task_states(bool publish)
    {
        using var child = new Child(Bytes(""), Bytes(""));
        var operation = DiagnosticSource<SetupChildResult>();
        var observed = DiagnosticSource<MsiCases.SetupDrains>();
        if (publish)
            observed.SetResult(new(
                Task.FromResult(""), Task.FromCanceled<string>(new(true)),
                DiagnosticSource<int>().Task));
        var value = ProjectDiagnosticFailure(
            child, operation.Task, operation.Task, observed.Task);
        Assert.Equal(publish ? 1 : 0, value.Observed);
        Assert.Equal(publish ? (int)TaskStatus.RanToCompletion : -1, value.Stdout);
        Assert.Equal(publish ? (int)TaskStatus.Canceled : -1, value.Stderr);
        Assert.Equal(publish ? (int)TaskStatus.WaitingForActivation : -1, value.Exit);
        Assert.Equal((int)TaskStatus.WaitingForActivation, value.Operation);
        Assert.Equal(0, value.OperationKind);
    }

    [Theory]
    [InlineData(2, 2, 15, 0)]
    [InlineData(4, 4, 9, 0)]
    [InlineData(99, -1, 9, 0)]
    [InlineData(11, 11, 9, 2)]
    public void Diagnostic_projection_keeps_fault_and_predicate_results(
        int fault, int expectedFault, int checks, int initiating)
    {
        using var child = new Child(Bytes(""), Bytes(""));
        var inner = fault == (int)SetupChildFault.CleanupFailed
            ? new AggregateException(
                new SetupChildException(SetupChildFault.InvalidDiagnostics))
            : null;
        var task = Task.FromException<SetupChildResult>(
            new SetupChildException((SetupChildFault)fault, inner));
        var value = ProjectDiagnosticFailure(
            child, task, task, DiagnosticSource<MsiCases.SetupDrains>().Task);
        Assert.Equal(2, value.Kind);
        Assert.Equal(2, value.OperationKind);
        Assert.Equal(expectedFault, value.Fault);
        Assert.Equal(expectedFault, value.OperationFault);
        Assert.Equal(checks, value.Checks);
        Assert.Equal(initiating, value.Initiating);
    }

    [Fact]
    public void Diagnostic_projection_distinguishes_outer_timeout_from_pending_operation()
    {
        using var child = new Child(Bytes(""), Bytes(""));
        var operation = DiagnosticSource<SetupChildResult>();
        var bounded = Task.FromException<SetupChildResult>(new TimeoutException());
        var observed = DiagnosticSource<MsiCases.SetupDrains>();
        observed.SetResult(new(
            Task.FromResult(""), Task.FromResult(""), Task.FromResult(0)));
        var value = ProjectDiagnosticFailure(child, operation.Task, bounded, observed.Task);
        Assert.False(operation.Task.IsCompleted);
        Assert.Equal(
            "HERMES_SETUP_DIAGNOSTIC_ASSERT v=1 bounded=7 operation=1 kind=3 operationKind=0 observed=1 stdout=5 stderr=5 exit=5 fault=0 operationFault=0 initiating=0 checks=0 closed=0 exited=1 kills=0",
            value.ToLine());
    }

    [Theory]
    [InlineData(false, 5, 7)]
    [InlineData(true, 4, 6)]
    public void Diagnostic_projection_does_not_reclassify_other_control_failures(
        bool cancelled, int kind, int status)
    {
        using var child = new Child(Bytes(""), Bytes(""));
        var task = cancelled
            ? Task.FromCanceled<SetupChildResult>(new(true))
            : Task.FromException<SetupChildResult>(new InvalidOperationException());
        var value = ProjectDiagnosticFailure(
            child, task, task, DiagnosticSource<MsiCases.SetupDrains>().Task);
        Assert.Equal(kind, value.Kind);
        Assert.Equal(kind, value.OperationKind);
        Assert.Equal(status, value.Bounded);
        Assert.Equal(0, value.Fault);
        Assert.Equal(0, value.OperationFault);
        Assert.Equal(0, value.Checks);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 1, 1)]
    [InlineData(2, 2, 2)]
    [InlineData(3, 3, 2)]
    public void Diagnostic_projection_reports_closed_pipes_and_bounded_kill_count(
        int closed, int kills, int expectedKills)
    {
        using var child = new Child(Bytes(""), Bytes(""), exited: false);
        if ((closed & 1) != 0)
            child.CloseStandardOutput();
        if ((closed & 2) != 0)
            child.CloseStandardError();
        foreach (var attempt in Enumerable.Range(0, kills))
            child.Kill();
        var task = Task.FromResult(new SetupChildResult(0, SuccessReport));
        var value = ProjectDiagnosticFailure(
            child, task, task, DiagnosticSource<MsiCases.SetupDrains>().Task);
        Assert.Equal(closed, value.Closed);
        Assert.Equal(expectedKills, value.Kills);
        Assert.Equal(kills == 0 ? 0 : 1, value.Exited);
        Assert.Equal(1, value.Kind);
        Assert.Equal(0, value.Checks);
    }
}
