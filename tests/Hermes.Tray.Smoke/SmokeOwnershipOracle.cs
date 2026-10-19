using System.Collections.Immutable;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal sealed record OwnershipProof(string Step, OwnedIdentity Before, OwnedIdentity After);
internal sealed record SmokeOwnershipVerdict(
    bool Passed, string? Failure, OwnedIdentity? Identity,
    OwnedStop? Stop, ImmutableArray<OwnershipProof> Checks);

internal sealed class SmokeOwnershipOracle(IOwnershipVerifier verifier) : IServiceObserver
{
    private const string FinalStopStep = "final-stop";
    private readonly object _gate = new();
    private ImmutableArray<OwnershipProof> _checks = [];
    private OwnedIdentity? _identity;
    private string? _failure;
    private bool _stopRequested;

    public OwnedIdentity Bind(ServiceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_failure is not null || _identity is not null ||
                snapshot is not { Phase: ServicePhase.Running, ProcessId: > 0 })
                throw new InvalidOperationException("Smoke cannot bind a stable owned child.");
            _identity = new(snapshot.ProcessId.Value, snapshot.Restart);
            return _identity;
        }
    }

    public void Changed(ServiceSnapshot snapshot)
    {
        lock (_gate)
        {
            if (snapshot.Phase == ServicePhase.Failed)
                _failure ??= $"Terminal supervision failure: {snapshot.Reason}.";
            else if (_identity is { } identity && !IsExpected(snapshot, identity))
                _failure ??= $"Owned child changed during smoke: {snapshot.Phase}/{snapshot.Reason}.";
        }
    }

    private bool IsExpected(ServiceSnapshot snapshot, OwnedIdentity identity) =>
        snapshot.Restart == identity.Restart &&
        ((snapshot.Phase == ServicePhase.Running && snapshot.ProcessId == identity.ProcessId) ||
         (_stopRequested && (snapshot.Phase, snapshot.Reason) is
             (ServicePhase.Stopping, EventCode.Cancelled) or (ServicePhase.Stopped, EventCode.Stopped)));

    public void Invalidate(string failure)
    {
        lock (_gate) { _failure ??= failure; }
    }

    private OwnedIdentity RequireIdentity()
    {
        lock (_gate)
        {
            return (_failure, _identity, _stopRequested) switch
            {
                (null, { } identity, false) => identity,
                _ => throw new InvalidOperationException("Smoke ownership proof is no longer valid.")
            };
        }
    }

    public async Task<T> ProveAsync<T>(
        string step, Func<CancellationToken, Task<T>> proof, CancellationToken cancellationToken)
    {
        try
        {
            var identity = RequireIdentity();
            var before = await verifier.VerifyOwnershipAsync(identity, cancellationToken);
            var result = await proof(cancellationToken);
            var after = await verifier.VerifyOwnershipAsync(identity, cancellationToken);
            Record(step, identity, before, after);
            return result;
        }
        catch (Exception error)
        {
            Invalidate(SafeFailure.Describe(error));
            throw;
        }
    }

    public Task ProveAsync(
        string step, Func<CancellationToken, Task> proof, CancellationToken cancellationToken) =>
        ProveAsync(step, async token => { await proof(token); return true; }, cancellationToken);

    private void Record(string step, OwnedIdentity identity, OwnedIdentity before, OwnedIdentity after)
    {
        lock (_gate)
        {
            if (RequireIdentity() != identity || before != identity || after != identity)
                throw new InvalidOperationException("Smoke operation did not retain its owned identity.");
            _checks = _checks.Add(new(step, before, after));
        }
    }

    public async Task PrepareStopAsync(CancellationToken cancellationToken)
    {
        await ProveAsync(FinalStopStep, _ => Task.FromResult(true), cancellationToken);
        lock (_gate)
        {
            _ = RequireIdentity();
            _stopRequested = true;
        }
    }

    public SmokeOwnershipVerdict Evaluate(SupervisionResult final, bool proofPassed)
    {
        lock (_gate)
        {
            var passed = proofPassed && _failure is null && _identity is { } identity &&
                MatchesFinal(final, identity);
            var stop = final.Stops.LastOrDefault(item => item.Identity == _identity);
            return new(passed,
                passed ? null : _failure ?? "Final owned proof and graceful stop did not match.",
                _identity, stop, _checks);
        }
    }

    private bool MatchesFinal(SupervisionResult final, OwnedIdentity identity) =>
        _stopRequested && _checks.Length >= 2 && _checks[^1].Step == FinalStopStep &&
        final is
        {
            Disposition: SupervisionDisposition.Cancelled,
            Snapshot: { Phase: ServicePhase.Stopped, Reason: EventCode.Stopped }
        } &&
        final.Snapshot.Restart == identity.Restart && final.LastOwned == identity &&
        !final.Stops.IsEmpty && final.Stops[^1].Identity == identity &&
        final.Stops[^1].Outcome is { Graceful: true, Forced: false, Exited: true, ExitCode: 0 };
}
