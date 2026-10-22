using System.Collections.Immutable;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

internal enum EffectEvent
{
    Acquire,
    Inspect,
    Close,
    Retain,
    Prepare,
    SaveJournal,
    Execute,
    DisposeCall,
    Verify,
    PublishReady,
    DisposeLease
}

internal enum LeaseFault
{
    None,
    Failure,
    IoException,
    AccessDenied,
    Cancelled,
    UnconfirmedSuccess,
    FailureAfterChange
}

internal sealed record Scenario
{
    internal SetupCode? AcquireError { get; init; }
    internal SetupCode? InspectionError { get; init; }
    internal SetupCode? RetentionError { get; init; }
    internal SetupCode? PreparationError { get; init; }
    internal bool CloseFails { get; init; }
    internal bool ReadyFails { get; init; }
    internal LeaseFault ReadyFault { get; init; }
    internal LeaseFault ReclosureFault { get; init; }
    internal Task? ReclosureWait { get; init; }
    internal bool TamperRetainedManifest { get; init; }
    internal bool MissingOwnFence { get; init; }
    internal bool ApiDoesNotReturn { get; init; }
    internal bool ControlFaulted { get; init; }
    internal int JournalFailureAt { get; init; }
    internal Guid? ReusedRunId { get; init; }
    internal ImmutableArray<uint> MsiCodes { get; init; } = [0];
    internal Func<InstallationTarget, VerificationEvidence>? Verification { get; init; }
    internal Action? OnExecute { get; init; }
}

internal static class Samples
{
    internal static UserInstallationScope Scope { get; } =
        UserInstallationScope.FromKnownFolder(Path.GetFullPath("/synthetic-profile"));

    internal static PackageEvidence First { get; } = Package(0, 'a');
    internal static PackageEvidence Second { get; } = Package(1, 'b');

    private static PackageEvidence Package(int minor, char hash)
    {
        var identity = new PackageIdentity(HermesServicingProtocol.UpgradeCode,
            Guid.NewGuid(), Guid.NewGuid(), new Version(1, minor, 0),
            HermesServicingProtocol.AdmissionAbi, PackageScope.PerUser);
        return new(identity, Scope.AppDirectory,
            Path.GetFullPath($"/media/Hermes {minor}.msi"),
            Path.GetFullPath($"/media/{minor}/payload-inventory.json"),
            new string(hash, 64), new string(hash, 64),
            [new("service/Hermes.Service.dll", Guid.NewGuid()),
             new("tray/Hermes.Tray.dll", Guid.NewGuid())]);
    }

    internal static Inspection Fresh(PackageEvidence? candidate = null) =>
        new(Scope, GateDisposition.Missing, AbsentTarget.Instance,
            candidate ?? First, null);

    internal static Inspection Installed(
        PackageEvidence current, PackageEvidence? candidate = null) =>
        new(Scope, GateDisposition.Ready, new PresentTarget(current), candidate, null);

    internal static Inspection Interrupted(
        InstallationTarget observed, InstallationTarget previous,
        InstallationTarget desired, ImmutableArray<PackageEvidence> packages)
    {
        var interrupted = new InterruptedOperation(
            Guid.NewGuid(), Guid.NewGuid(), [], previous, desired, packages);
        return new(Scope, GateDisposition.Closed, observed, null, interrupted);
    }

    internal static InstallCommand Install(PackageEvidence package, bool rollback = false) =>
        new(package.SourcePath, rollback, true);

    internal static MaintenanceCommand Maintenance(SetupVerb verb) => new(verb, true);
}

internal sealed class FakeEffects :
    IAdmissionSource,
    IInstallationInspection,
    IMediaRetention,
    IOperationJournal,
    IGuardedEngine,
    IInstallationVerification
{
    private readonly Inspection _inspection;
    private readonly Scenario _scenario;
    private readonly List<EffectEvent> _events = [];
    private readonly List<OperationJournal> _records = [];
    private readonly List<EngineRequest> _requests = [];
    private OperationJournal? _current;
    private bool _retained;
    private int _preparations;
    private int _saves;

    internal FakeEffects(Inspection inspection, Scenario? scenario = null)
    {
        _inspection = inspection;
        _scenario = scenario ?? new();
        Lease = new FakeLease(this, inspection.Gate);
    }

    internal FakeLease Lease { get; }
    internal IReadOnlyList<EffectEvent> Events => _events;
    internal IReadOnlyList<OperationJournal> Records => _records;
    internal IReadOnlyList<EngineRequest> Requests => _requests;

    internal Task<SetupExecution> RunAsync(
        SetupCommand command, CancellationToken cancellationToken = default) =>
        new SetupOrchestrator(this, this, this, this, this, this)
            .ExecuteAsync(command, cancellationToken);

    public Effect<ISetupLease> TryAcquire()
    {
        _events.Add(EffectEvent.Acquire);
        return _scenario.AcquireError is { } error
            ? Effect<ISetupLease>.Fail(error) : Effect<ISetupLease>.Ok(Lease);
    }

    public Task<Effect<Inspection>> InspectAsync(
        SetupCommand command, ISetupLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add(EffectEvent.Inspect);
        return Task.FromResult(_scenario.InspectionError is { } error
            ? Effect<Inspection>.Fail(error) : Effect<Inspection>.Ok(_inspection));
    }

    public Task<Effect<ImmutableArray<RetainedMedia>>> RetainAsync(
        IReadOnlyList<PackageEvidence> packages, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add(EffectEvent.Retain);
        Assert.True(Lease.IsHeldAndClosed);
        if (_scenario.RetentionError is { } error)
            return Task.FromResult(Effect<ImmutableArray<RetainedMedia>>.Fail(error));
        var retained = packages.Select(Retain).ToImmutableArray();
        _retained = true;
        return Task.FromResult(Effect<ImmutableArray<RetainedMedia>>.Ok(retained));
    }

    private RetainedMedia Retain(PackageEvidence package)
    {
        var evidence = _scenario.TamperRetainedManifest
            ? package with { ManifestSha256 = new string('f', 64) } : package;
        var directory = Path.Combine(Samples.Scope.PackagesDirectory, package.PackageSha256);
        return new(evidence, Path.Combine(directory, "package.msi"),
            Path.Combine(directory, "payload-inventory.json"));
    }

    public Task<Effect<Unit>> SaveAsync(
        OperationJournal journal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add(EffectEvent.SaveJournal);
        Assert.True(Lease.IsHeldAndClosed);
        _saves++;
        if (_saves == _scenario.JournalFailureAt)
            return Task.FromResult(Effect<Unit>.Fail(SetupCode.JournalPublicationFailed));
        _current = journal;
        _records.Add(journal);
        return Task.FromResult(Effect<Unit>.Ok(default));
    }

    public Task<Effect<IPreparedMsiCall>> PrepareAsync(
        ISetupLease lease, EngineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add(EffectEvent.Prepare);
        Assert.True(lease.IsHeldAndClosed);
        Assert.True(_retained);
        if (_scenario.PreparationError is { } error)
            return Task.FromResult(Effect<IPreparedMsiCall>.Fail(error));
        _requests.Add(request);
        var run = _scenario.ReusedRunId ?? Guid.NewGuid();
        var code = _scenario.MsiCodes[_preparations++];
        return Task.FromResult(Effect<IPreparedMsiCall>.Ok(new FakeCall(this, run, code)));
    }

    public Task<Effect<VerificationEvidence>> VerifyAsync(
        OperationJournal journal, InstallationTarget expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _events.Add(EffectEvent.Verify);
        Assert.True(Lease.IsHeldAndClosed);
        Assert.True(journal.Receipts[^1].ApiReturned);
        Assert.Equal(journal, _current);
        var evidence = _scenario.Verification?.Invoke(expected) ?? new(expected, false);
        return Task.FromResult(Effect<VerificationEvidence>.Ok(evidence));
    }

    private EngineReceipt Execute(Guid run, uint code)
    {
        _events.Add(EffectEvent.Execute);
        Assert.True(Lease.IsHeldAndClosed);
        Assert.True(_retained);
        var current = _current ?? throw new InvalidOperationException("Journal was not saved.");
        Assert.Equal(run, current.ActiveRunId);
        Assert.DoesNotContain(run, current.RevokedRunIds);
        _scenario.OnExecute?.Invoke();
        return new(run, true, !_scenario.ApiDoesNotReturn, code,
            _scenario.ControlFaulted, true, !_scenario.MissingOwnFence);
    }

    internal sealed class FakeLease(FakeEffects owner, GateDisposition initialGate) : ISetupLease
    {
        private bool _held = true;
        private bool _readyAttempted;
        private GateDisposition _state = initialGate;

        public string GatePath => Samples.Scope.GatePath;
        public bool IsHeldAndClosed => _held && IsClosed;
        internal bool IsClosed => _state == GateDisposition.Closed;
        internal bool IsHeld => _held;
        internal GateDisposition State => _state;
        internal GateReadyState? Published { get; private set; }

        public async Task<Effect<Unit>> CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner._events.Add(EffectEvent.Close);
            Assert.True(_held);
            var fault = _readyAttempted
                ? await ReclosureFaultAsync(cancellationToken)
                : owner._scenario.CloseFails ? LeaseFault.Failure : LeaseFault.None;
            if (fault is LeaseFault.None or LeaseFault.FailureAfterChange)
                _state = GateDisposition.Closed;
            return Complete(fault, SetupCode.ClosedPublicationFailed);
        }

        private async Task<LeaseFault> ReclosureFaultAsync(CancellationToken cancellationToken)
        {
            Assert.Equal(CancellationToken.None, cancellationToken);
            await (owner._scenario.ReclosureWait ?? Task.CompletedTask);
            Assert.True(_held);
            return owner._scenario.ReclosureFault;
        }

        public Task<Effect<Unit>> PublishReadyAsync(
            GateReadyState ready, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner._events.Add(EffectEvent.PublishReady);
            Assert.True(IsHeldAndClosed);
            _readyAttempted = true;
            if (owner._scenario.ReadyFails)
                return Task.FromResult(Effect<Unit>.Fail(SetupCode.ReadyPublicationFailed));
            Published = ready;
            _state = GateDisposition.Ready;
            return Task.FromResult(Complete(owner._scenario.ReadyFault,
                SetupCode.ReadyPublicationFailed));
        }

        private static Effect<Unit> Complete(LeaseFault fault, SetupCode code) => fault switch
        {
            LeaseFault.None or LeaseFault.UnconfirmedSuccess => Effect<Unit>.Ok(default),
            LeaseFault.Failure or LeaseFault.FailureAfterChange => Effect<Unit>.Fail(code),
            LeaseFault.IoException => throw new IOException(),
            LeaseFault.AccessDenied => throw new UnauthorizedAccessException(),
            LeaseFault.Cancelled => throw new OperationCanceledException(),
            _ => throw new InvalidOperationException("Unsupported synthetic lease fault.")
        };

        public void Dispose()
        {
            owner._events.Add(EffectEvent.DisposeLease);
            _held = false;
        }
    }

    private sealed class FakeCall(FakeEffects owner, Guid run, uint code) : IPreparedMsiCall
    {
        public Guid RunId => run;
        public Effect<EngineReceipt> Execute() => Effect<EngineReceipt>.Ok(owner.Execute(run, code));

        public ValueTask DisposeAsync()
        {
            owner._events.Add(EffectEvent.DisposeCall);
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class SimulatedInterruptionException : Exception;
