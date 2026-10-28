using Hermes.Tray.Smoke;

namespace Hermes.Tray;

internal sealed class StableProcessVerifier(
    IOwnershipVerifier ownership, string expectedImage, IProcessIdentitySource processes)
    : IOwnershipVerifier
{
    private readonly object _gate = new();
    private ProcessStamp? _bound;

    public ProcessStamp? Bound
    {
        get { lock (_gate) { return _bound; } }
    }

    public async Task<OwnedIdentity> VerifyOwnershipAsync(
        OwnedIdentity expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyStamp(expected.ProcessId);
        var actual = await ownership.VerifyOwnershipAsync(expected, cancellationToken);
        if (actual != expected)
            throw new InvalidOperationException("Owned health identity changed.");
        VerifyStamp(expected.ProcessId);
        return actual;
    }

    private void VerifyStamp(int processId)
    {
        var actual = processes.Read(processId);
        lock (_gate)
        {
            if (actual.ProcessId != processId || actual.StartUtcTicks <= 0 ||
                !InstalledSource.SamePath(actual.ImagePath, expectedImage))
                throw new InvalidOperationException("Owned OS process identity is invalid.");
            _bound ??= actual;
            if (_bound.ProcessId != actual.ProcessId || _bound.StartUtcTicks != actual.StartUtcTicks ||
                !InstalledSource.SamePath(_bound.ImagePath, actual.ImagePath))
                throw new InvalidOperationException("Owned OS process instance changed.");
        }
    }
}
