namespace Hermes.Servicing.Acceptance;

// Only a fixed inert child is supported. This is not an installer launcher.
internal interface IAcceptanceChildFactory
{
    IAcceptanceOwnedChild Open();
}

internal interface IAcceptanceOwnedChild : IDisposable
{
    bool IsClosed { get; }
    void CreateKillOnCloseJob();
    void CreateSuspended(IAcceptanceRestrictedToken token);
    void AssignJob();
    RestrictedOwnerTokenFacts ReadPrimaryFacts();
    MsiContextRead ReadThreadToken();
    void Resume();
    uint Wait(CancellationToken cancellationToken);
}

internal sealed record RestrictedOwnerChildResult(
    RestrictedOwnerTokenFacts Facts,
    MsiContextRead Thread,
    uint ExitCode,
    bool CleanupCompleted);

internal sealed class AcceptanceChildCleanupException(IEnumerable<Exception> errors)
    : AggregateException("Acceptance child cleanup incomplete.", errors);

internal static class AcceptanceChildCleanup
{
    // Each operation is attempted, even when an earlier cleanup operation fails.
    internal static void Complete(params Action[] operations)
    {
        List<Exception> failures = [];
        foreach (var operation in operations)
        {
            try { operation(); }
            catch (Exception error) { failures.Add(error); }
        }
        if (failures.Count != 0)
            throw new AcceptanceChildCleanupException(failures);
    }
}

internal static class RestrictedOwnerChild
{
    internal static RestrictedOwnerChildResult RunInert(
        RestrictedOwnerToken token,
        IAcceptanceChildFactory children,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = token.Verify();
        var child = children.Open();
        return RunOwned(token, child, cancellationToken);
    }

    private static RestrictedOwnerChildResult RunOwned(
        RestrictedOwnerToken token, IAcceptanceOwnedChild child,
        CancellationToken cancellationToken)
    {
        RestrictedOwnerChildResult result;
        try { result = Execute(token, child, cancellationToken); }
        catch (Exception original)
        {
            try { child.Dispose(); }
            catch (Exception cleanup)
            {
                throw new AggregateException(
                    "Acceptance child failed; cleanup incomplete.", original, cleanup);
            }
            throw;
        }
        child.Dispose();
        if (!child.IsClosed)
            throw new AcceptanceChildCleanupException([RestrictedOwnerToken.Failure(6)]);
        return result with { CleanupCompleted = true };
    }

    private static RestrictedOwnerChildResult Execute(
        RestrictedOwnerToken token, IAcceptanceOwnedChild child,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        child.CreateKillOnCloseJob();
        cancellationToken.ThrowIfCancellationRequested();
        token.CreateSuspendedInertChild(child);
        child.AssignJob();
        var facts = child.ReadPrimaryFacts();
        facts.Require();
        var thread = child.ReadThreadToken();
        if (thread != MsiContextRead.Absent)
            throw new RestrictedOwnerTokenVerificationException(128);
        _ = token.Verify();
        cancellationToken.ThrowIfCancellationRequested();
        child.Resume();
        var exit = child.Wait(cancellationToken);
        return new(facts, thread, exit, false);
    }
}
