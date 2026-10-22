namespace Hermes.Servicing.Acceptance;

internal enum HoldPoint
{
    None,
    OwnPreflight,
    OwnTransaction,
    NestedPreflight,
    ApiReturned,
    AfterOwnFenceAction
}

internal sealed record RootResult(
    SetupReport Report,
    int ExitCode,
    string DiagnosticCode,
    GateEvidence Gate,
    Guid? ReadyProductCode,
    bool ReadyAbsent,
    JournalMark Journal,
    ImmutableArray<EngineReceipt> EngineReceipts,
    ImmutableArray<OwnedFileState> OwnedFiles)
{
    public ImmutableArray<MaintenanceKind> Operations { get; init; } = [];
}

internal sealed record RootEvent(
    long Sequence,
    string Kind,
    Stamp Coordinator,
    GateEvidence Gate,
    JournalMark Journal,
    ControlFenceObservation? Fence,
    InstallerCallReceipt? Receipt,
    RootResult? Result)
{
    public MsiObservedMessage? MsiMessage { get; init; }
    public int ObservationFailures { get; init; }
}

internal sealed class ObservedFiles(string gatePath) : ISetupFileSystem
{
    private readonly PhysicalSetupFileSystem _files = new();
    private SafeFileHandle? _gateHandle;

    public DiskEntry Entry(string path) => _files.Entry(path);
    public IReadOnlyList<string> Children(string directory) => _files.Children(directory);
    public void CreateDirectory(string path) => _files.CreateDirectory(path);
    public void Flush(FileStream stream) => _files.Flush(stream);
    public void DeleteFile(string path) => _files.DeleteFile(path);

    public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share)
    {
        var stream = _files.Open(path, mode, access, share);
        if (InstalledSource.SamePath(path, gatePath) &&
            access == FileAccess.ReadWrite && share == FileShare.None)
            _gateHandle = stream.SafeFileHandle;
        return stream;
    }

    internal GateEvidence Capture() => _gateHandle is { IsClosed: false } handle
        ? GateReader.Read(gatePath, handle)
        : GateReader.Read(gatePath);
}

internal sealed class RootTrace(
    ObservedFiles files,
    FileOperationJournal journal,
    HoldPoint hold) : IMsiMessageObserver
{
    private readonly object _sync = new();
    private readonly Stamp _coordinator = Files.Current();
    private long _sequence;
    private int _held;
    private MsiObservedMessage? _pauseMessage;

    public void ObserveMsi(MsiObservedMessage message)
    {
        Emit("msi-message", message: message);
        if (hold == HoldPoint.AfterOwnFenceAction &&
            message is { Kind: MsiObservedKind.ActionStart, Action: "RemoveExistingProducts" })
        {
            _pauseMessage = message;
            Pause(null, null);
        }
    }

    public void Observe(ControlObservationPoint point, ControlFenceObservation observation)
    {
        Emit(point.ToString(), observation);
        if (point == ControlObservationPoint.BeforeCanonicalObservation && Matches(observation))
            Pause(observation, null);
    }

    private bool Matches(ControlFenceObservation observation) => hold switch
    {
        HoldPoint.OwnPreflight => observation is { OwnSession: true, Phase: GuardPhase.Preflight },
        HoldPoint.OwnTransaction => observation is { OwnSession: true, Phase: GuardPhase.Transaction },
        HoldPoint.NestedPreflight => observation is { OwnSession: false, Phase: GuardPhase.Preflight },
        _ => false
    };

    internal void Returned(InstallerCallReceipt receipt)
    {
        Emit("api-returned", receipt: receipt);
        if (hold == HoldPoint.ApiReturned)
            Pause(null, receipt);
    }

    private void Pause(
        ControlFenceObservation? observation, InstallerCallReceipt? receipt)
    {
        if (Interlocked.CompareExchange(ref _held, 1, 0) != 0)
            return;
        Emit("paused", observation, receipt);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var line = Console.In.ReadLineAsync(deadline.Token).AsTask().GetAwaiter().GetResult();
            Emit(line == "continue" ? "continued" : "pause-invalid-input", observation, receipt);
        }
        catch (OperationCanceledException)
        {
            Emit("pause-expired", observation, receipt);
            Environment.Exit(86);
        }
    }

    internal void Emit(string kind, ControlFenceObservation? fence = null,
        InstallerCallReceipt? receipt = null, RootResult? result = null,
        MsiObservedMessage? message = null)
    {
        lock (_sync)
        {
            Require.That(++_sequence <= 512);
            Console.WriteLine(EvidenceJson.Encode(new RootEvent(
                _sequence, kind, _coordinator, files.Capture(),
                Mark(journal.Read() ?? throw new InvalidDataException()),
                fence, receipt, result)
            {
                MsiMessage = message ?? _pauseMessage,
                ObservationFailures = DiagnosticLog.Failures
            }));
        }
    }

    internal static JournalMark Mark(OperationJournal operation) => new(
        operation.OperationId, operation.ActiveRunId, operation.RevokedRunIds,
        operation.Media.Select(media => new MediaEvidence(
            media.Evidence.Identity.ProductCode, media.PackagePath,
            media.Evidence.PackageSha256, media.ManifestPath,
            media.Evidence.ManifestSha256)).ToImmutableArray());
}

internal sealed class ObservedEngine(RootTrace trace) : IGuardedEngine
{
    public async Task<Effect<IPreparedMsiCall>> PrepareAsync(
        ISetupLease lease, EngineRequest request, CancellationToken cancellationToken)
    {
        var plan = new ControlPlan(request.Step.Package.Identity.ProductCode,
            request.Step.Operation, request.Scope.AppDirectory,
            request.Step.NestedRemovalProducts,
            request.Step.Operation == MaintenanceKind.Install ? request.Media.PackagePath : null,
            request.Step.AllowRollback, request.Step.Quiet);
        var call = await GuardedInstallerCall.StartAsync(
            new Ownership(lease), plan, trace, cancellationToken);
        return Effect<IPreparedMsiCall>.Ok(new Prepared(call, trace));
    }

    private sealed class Ownership(ISetupLease lease) : IClosedGateOwnership
    {
        public string GatePath => lease.GatePath;
        public bool IsHeldAndClosed => lease.IsHeldAndClosed;
    }

    private sealed class Prepared(
        GuardedInstallerCall call, RootTrace trace) : IPreparedMsiCall
    {
        public Guid RunId => call.RunId;

        public Effect<EngineReceipt> Execute()
        {
            var receipt = call.Execute();
            trace.Returned(receipt);
            bool Has(GuardPhase phase) => receipt.Fences.Any(fence =>
                fence.RunId == RunId && fence.OwnSession && fence.Phase == phase);
            return Effect<EngineReceipt>.Ok(new(receipt.RunId,
                receipt.ApiEntered, receipt.ApiReturned, receipt.MsiExitCode,
                receipt.ControlFaulted, Has(GuardPhase.Preflight), Has(GuardPhase.Transaction)));
        }

        public ValueTask DisposeAsync() => call.DisposeAsync();
    }
}

internal static class NativeRoot
{
    internal static async Task<int> RunAsync(
        AcceptanceContext context, string operation, HoldPoint hold,
        CancellationToken cancellationToken)
    {
        context.Verify();
        await WorkerHandshake.WaitAsync(cancellationToken);
        var scope = WindowsSetupScope.ForCurrentUser();
        var files = new ObservedFiles(scope.GatePath);
        var disk = new SetupDisk(files, new WindowsSetupPathSafety(), new WindowsAtomicFileCommit());
        var journal = new FileOperationJournal(disk, scope);
        var trace = new RootTrace(files, journal, hold);
        await using var application = Create(context, scope, disk, trace);
        var execution = await application.ExecuteAsync(Command(context, operation), cancellationToken);
        var record = journal.Read() ?? throw new InvalidDataException();
        var result = Result(execution, scope, disk, record);
        OwnerFixture.RequireExpected(context.Options.OwnerReceipt, context.Owner);
        trace.Emit("result", result: result);
        return (int)execution.ExitCode;
    }

    private static SetupApplication Create(AcceptanceContext context,
        UserInstallationScope scope, SetupDisk disk, RootTrace trace)
    {
        var database = new WindowsPackageDatabase();
        var images = new[]
        {
            Environment.ProcessPath ?? throw new InvalidDataException(),
            typeof(NativeRoot).Assembly.Location
        }.ToImmutableArray();
        var capabilities = new SetupCapabilities(scope,
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            images, disk, database, database, new WindowsRegistrationInventory(),
            new WindowsPathIdentityQuery(),
            new FileAdmissionSource(disk, scope, new SharedSetupGateCodec()),
            new ObservedEngine(trace));
        return SetupComposition.Create(capabilities, context.ApprovedGuard());
    }

    private static SetupCommand Command(AcceptanceContext context, string operation) =>
        operation switch
        {
            "old" => new InstallCommand(context.Options.OldMsi, true, true),
            "new" => new InstallCommand(context.Options.NewMsi, false, true),
            "repair" => new MaintenanceCommand(SetupVerb.Repair, true),
            "uninstall" => new MaintenanceCommand(SetupVerb.Uninstall, true),
            "recover" => new MaintenanceCommand(SetupVerb.Recover, true),
            _ => throw new ArgumentException("Unknown diagnostic operation.")
        };

    private static RootResult Result(SetupExecution execution,
        UserInstallationScope scope, SetupDisk disk, OperationJournal journal)
    {
        Guid? product = null;
        var absent = false;
        if (execution.Report.Ready)
        {
            using var reader = new FileStream(scope.GatePath,
                FileMode.Open, FileAccess.Read, FileShare.Read);
            var ready = GateCodec.ReadReady(reader);
            product = ready.Installation?.ProductCode;
            absent = ready.Installation is null;
        }
        return new(execution.Report, (int)execution.ExitCode, execution.Code.ToString(),
            GateReader.Read(scope.GatePath), product, absent, RootTrace.Mark(journal),
            journal.Receipts, Inventory(scope, disk, journal))
        {
            Operations = journal.Plan.Steps.Select(step => step.Operation).ToImmutableArray()
        };
    }

    private static ImmutableArray<OwnedFileState> Inventory(
        UserInstallationScope scope, SetupDisk disk, OperationJournal journal)
    {
        var media = new PackageMediaReader(disk, scope, new WindowsPackageDatabase());
        var inventory = new OwnedPayloadInventory(disk, scope);
        return journal.Media.Select(item => media.Read(item.PackagePath).Manifest)
            .SelectMany(manifest => inventory.Read(manifest))
            .DistinctBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(file => file.RelativePath, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}
