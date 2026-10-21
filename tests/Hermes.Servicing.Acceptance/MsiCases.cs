namespace Hermes.Servicing.Acceptance;

internal sealed record MainResult(
    Stamp Process,
    int ExitCode,
    SetupReport Report);

internal sealed record CrashCase(
    string Name,
    string? Baseline,
    string Operation,
    HoldPoint Hold,
    bool RequiresTwoRecoveryCalls);

internal sealed record RecoveryAttempt(
    Stamp Process,
    int ExitCode,
    ImmutableArray<RootEvent> Events,
    RootResult Result);

internal static partial class MsiCases
{
    internal static ImmutableArray<CrashCase> Matrix { get; } =
    [
        new("interrupted-install", null, "old", HoldPoint.OwnPreflight, false),
        new("interrupted-repair", "old", "repair", HoldPoint.OwnTransaction, false),
        new("interrupted-upgrade", "old", "new", HoldPoint.AfterOwnFenceAction, false),
        new("interrupted-explicit-rollback", "new", "old", HoldPoint.AfterOwnFenceAction, false),
        new("interrupted-uninstall", "old", "uninstall", HoldPoint.OwnTransaction, false),
        new("already-absent-bootstrap", "old", "uninstall", HoldPoint.ApiReturned, true)
    ];

    internal static async Task<ImmutableArray<CaseReport>> RunAsync(
        AcceptanceContext context, CancellationToken cancellationToken)
    {
        var reports = ImmutableArray.CreateBuilder<CaseReport>();
        foreach (var item in Matrix)
            reports.Add(await CrashAsync(context, item, cancellationToken));
        return reports.ToImmutable();
    }

    internal static async Task<MainResult> MainAsync(
        AcceptanceContext context, string operation, CancellationToken cancellationToken)
    {
        var arguments = MainArguments(context, operation);
        using var process = OwnedChild.Start(
            context.Options.SetupExecutable, arguments, context.Options.Work,
            captureStandardError: true);
        var result = await MainAsync(process,
            Enum.Parse<SetupVerb>(arguments[0], ignoreCase: true),
            SetupChildBudget.Default, cancellationToken);
        return new(process.Identity, result.ExitCode, result.Report);
    }

    private static ImmutableArray<string> MainArguments(
        AcceptanceContext context, string operation) => operation switch
    {
        "old" => ["install", "--package", context.Options.OldMsi, "--allow-rollback", "--quiet"],
        "new" => ["install", "--package", context.Options.NewMsi, "--quiet"],
        "recover" or "uninstall" or "repair" => [operation, "--quiet"],
        _ => throw new ArgumentException("Unsupported Setup operation.")
    };

    private static async Task<CaseReport> CrashAsync(AcceptanceContext context,
        CrashCase item, CancellationToken cancellationToken)
    {
        var evidence = ImmutableArray.CreateBuilder<object>();
        await PrepareAsync(context, item, evidence, cancellationToken);
        try
        {
            await using var observer = await IndependentMsiObserver.StartAsync(
                context, cancellationToken);
            return await ObservedCrashAsync(context, item, observer, evidence, cancellationToken);
        }
        catch (EvidenceGap gap)
        {
            var cleanup = await CleanupAsync(context, cancellationToken);
            evidence.Add(cleanup);
            return new(item.Name, "actual-msi-root-composition",
                cleanup.Completed ? CaseState.Blocked : CaseState.Failed,
                evidence.ToImmutable(), gap.Code);
        }
    }

    private static async Task PrepareAsync(AcceptanceContext context, CrashCase item,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var ready = ReadReady();
        if (ready.Installation is not null)
        {
            var removed = await MainAsync(context, "uninstall", cancellationToken);
            RequireCompleted(removed);
            evidence.Add(removed);
        }
        Require.That(ReadReady().Installation is null);
        if (item.Baseline is { } baseline)
        {
            var installed = await MainAsync(context, baseline, cancellationToken);
            RequireCompleted(installed);
            evidence.Add(installed);
        }
    }

    private static void RequireObservation(int failures)
    {
        if (failures != 0)
            throw new EvidenceGap("SourceObserverFailure");
    }

    private static void RequireCompleted(MainResult result) =>
        Require.That(result.ExitCode == 0 &&
            result.Report is { Ready: true, Outcome: SetupOutcome.Completed });

    private static async Task<OwnedChild> StartWorkerAsync(
        AcceptanceContext context, string operation, HoldPoint hold,
        IndependentMsiObserver? observer, CancellationToken cancellationToken)
    {
        var child = OwnedChild.Start(
            Environment.ProcessPath ?? throw new InvalidDataException(),
            ["root-worker", context.Options.RequestPath, operation, hold.ToString()],
            context.Options.Work);
        try
        {
            var hello = EvidenceJson.Decode<WorkerHello>(await child.ReadAsync(cancellationToken));
            Require.That(hello.Kind == "worker-armed" && hello.Coordinator == child.Identity);
            observer?.Arm(child.Identity);
            await child.SendAsync("begin", cancellationToken);
            return child;
        }
        catch { child.Dispose(); throw; }
    }

    private static async Task<ImmutableArray<RootEvent>> UntilPausedAsync(
        OwnedChild process, CancellationToken cancellationToken)
    {
        var events = ImmutableArray.CreateBuilder<RootEvent>();
        while (events.Count < 512)
        {
            var item = EvidenceJson.Decode<RootEvent>(await process.ReadAsync(cancellationToken));
            Require.That(item.Coordinator == process.Identity);
            RequireObservation(item.ObservationFailures);
            events.Add(item);
            Require.That(item.Kind is not ("pause-expired" or "result"));
            if (item.Kind == "paused")
                return events.ToImmutable();
        }
        throw new InvalidDataException("Bounded callback pause was not reached.");
    }

    private static void ValidatePause(CrashCase item,
        OwnedChild process, ImmutableArray<RootEvent> events)
    {
        var paused = events[^1];
        Require.That(paused.Kind == "paused" && paused.Coordinator == process.Identity);
        Require.That(paused.Gate.HeaderState == "Closed");
        Require.That(paused.Journal.ActiveRunId != Guid.Empty);
        Require.That(paused.Journal.Media.All(MediaIntact));
        RequireObservation(paused.ObservationFailures);
        if (item.Hold == HoldPoint.AfterOwnFenceAction)
            Require.That(events.Any(IsOwnTransactionAccepted) &&
                paused.MsiMessage is
                {
                    Kind: MsiObservedKind.ActionStart,
                    Action: "RemoveExistingProducts"
                });
        if (item.Hold == HoldPoint.ApiReturned)
            Require.That(paused.Receipt is { MayEvaluateRecovery: true, MsiExitCode: 0 });
    }

    private static bool IsOwnTransactionAccepted(RootEvent item) =>
        item.Kind == nameof(ControlObservationPoint.CanonicalObservationAccepted) &&
        item.Fence is { OwnSession: true, Phase: GuardPhase.Transaction };

    private static bool MediaIntact(MediaEvidence media) =>
        Files.Hash(media.PackagePath) == media.PackageSha256 &&
        Files.Hash(media.ManifestPath) == media.ManifestSha256;

    private static async Task<ImmutableArray<RecoveryAttempt>> RecoverAsync(
        AcceptanceContext context, Guid oldRun, IndependentMsiObserver? observer,
        CancellationToken cancellationToken)
    {
        var attempts = ImmutableArray.CreateBuilder<RecoveryAttempt>();
        for (var attempt = 0; attempt < 96; attempt++)
        {
            var result = await RecoverOnceAsync(context, observer, cancellationToken);
            attempts.Add(result);
            if (result.ExitCode != 1618)
                return attempts.ToImmutable();
            Require.That(!result.Result.Report.Ready &&
                result.Result.Gate.HeaderState == "Closed");
            Require.That(result.Result.Journal.ActiveRunId != oldRun);
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        throw new InvalidDataException("Recovery remained contended; admission is not proven ready.");
    }

    private static async Task<RecoveryAttempt> RecoverOnceAsync(
        AcceptanceContext context, IndependentMsiObserver? observer,
        CancellationToken cancellationToken)
    {
        using var process = await StartWorkerAsync(
            context, "recover", HoldPoint.None, observer, cancellationToken);
        var events = ImmutableArray.CreateBuilder<RootEvent>();
        while (events.Count < 512)
        {
            var item = EvidenceJson.Decode<RootEvent>(await process.ReadAsync(cancellationToken));
            Require.That(item.Coordinator == process.Identity);
            RequireObservation(item.ObservationFailures);
            events.Add(item);
            if (item.Result is { } result)
                return new(process.Identity, await process.WaitAsync(cancellationToken),
                    events.ToImmutable(), result);
        }
        throw new InvalidDataException("Recovery exceeded its event bound.");
    }

    private static void ValidateRecovery(
        CrashCase item, RootEvent paused, RecoveryAttempt recovery)
    {
        var result = recovery.Result;
        Require.That(recovery.ExitCode == 0 && result.ExitCode == 0);
        Require.That(result.Report is { Ready: true, Outcome: SetupOutcome.Completed });
        Require.That(result.Journal.ActiveRunId != paused.Journal.ActiveRunId &&
            result.Journal.RevokedRunIds.Contains(paused.Journal.ActiveRunId));
        Require.That(result.Gate.FileIdentity == paused.Gate.FileIdentity);
        Require.That(result.EngineReceipts.All(receipt =>
            receipt.CompletedApi && receipt.MsiExitCode == 0 &&
            receipt.OwnPreflightOccurred && receipt.OwnTransactionFenceOccurred));
        var raw = recovery.Events.Where(value => value.Kind == "api-returned")
            .Select(value => value.Receipt ?? throw new InvalidDataException()).ToArray();
        foreach (var receipt in raw)
            RequireObservation(receipt.ObservationFaults);
        Require.That(raw.Length == result.EngineReceipts.Length &&
            raw.All(receipt => receipt.MayEvaluateRecovery));
        if (item.RequiresTwoRecoveryCalls)
            Require.That(raw.Length == 2 && result.ReadyAbsent);
    }

    internal static GateReadyState ReadReady()
    {
        var path = WindowsSetupScope.ForCurrentUser().GatePath;
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return GateCodec.ReadReady(stream);
    }

    internal static async Task<CleanupReport> CleanupAsync(
        AcceptanceContext context, CancellationToken cancellationToken)
    {
        var evidence = ImmutableArray.CreateBuilder<object>();
        try
        {
            await RecoverIfClosedAsync(context, evidence, cancellationToken);
            if (ReadReady().Installation is not null)
            {
                var result = await MainAsync(context, "uninstall", cancellationToken);
                evidence.Add(result);
                RequireCompleted(result);
            }
            Require.That(ReadReady().Installation is null);
            return new(true, true, "ReadyAbsent", evidence.ToImmutable(), null);
        }
        catch (Exception error)
        {
            return new(true, false, SafeAdmission(), evidence.ToImmutable(),
                ProofFiles.Failure(error));
        }
    }

    private static async Task RecoverIfClosedAsync(AcceptanceContext context,
        ImmutableArray<object>.Builder evidence, CancellationToken cancellationToken)
    {
        var gate = GateReader.Read(WindowsSetupScope.ForCurrentUser().GatePath);
        if (gate.HeaderState != "Closed")
            return;
        var recovery = await RecoverAsync(context, Guid.Empty, null, cancellationToken);
        evidence.Add(recovery);
        Require.That(recovery[^1].ExitCode == 0 && recovery[^1].Result.Report.Ready);
    }

    private static string SafeAdmission()
    {
        try { return GateReader.Read(WindowsSetupScope.ForCurrentUser().GatePath).HeaderState; }
        catch { return "UnknownOrBusy"; }
    }
}
