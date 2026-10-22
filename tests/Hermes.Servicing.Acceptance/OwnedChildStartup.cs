using System.Diagnostics;

namespace Hermes.Servicing.Acceptance;

internal interface IOwnedChildImage
{
    bool HasExited { get; }
    bool HasMainModule { get; }
    bool WaitForExit(int milliseconds);
}

internal interface IOwnedChildBinding : IOwnedChildImage, IDisposable
{
    void Kill();
}

// Borrows the Process returned by Start; never opens an unrelated PID.
internal sealed class StartedChildImage(Process process) : IOwnedChildBinding
{
    public bool HasExited => process.HasExited;

    public bool HasMainModule
    {
        get
        {
            // An early empty module snapshot must not survive the next observation.
            process.Refresh();
            return process.MainModule is { };
        }
    }

    public bool WaitForExit(int milliseconds) => process.WaitForExit(milliseconds);

    public void Kill() => process.Kill(entireProcessTree: false);

    public void Dispose() => process.Dispose();
}

internal static class OwnedChildStartup
{
    private static readonly TimeSpan ImageTimeout = TimeSpan.FromSeconds(5);
    private const int PollMilliseconds = 10;

    // MainModule documents null during loading. Exceptions are not that state.
    // This establishes readiness only; Files.Identity still supplies the proof.
    internal static void RequireImage(IOwnedChildImage child, TimeProvider clock)
    {
        var started = clock.GetTimestamp();
        while (true)
        {
            RequireLive(child.HasExited);
            _ = Remaining(clock, started);
            var ready = child.HasMainModule;
            RequireLive(child.HasExited);
            var remaining = Remaining(clock, started);
            if (ready)
                return;
            RequireLive(child.WaitForExit(
                Math.Min(PollMilliseconds,
                    checked((int)Math.Ceiling(remaining.TotalMilliseconds)))));
        }
    }

    private static TimeSpan Remaining(TimeProvider clock, long started)
    {
        var remaining = ImageTimeout - clock.GetElapsedTime(started);
        return remaining > TimeSpan.Zero
            ? remaining
            : throw new TimeoutException("Owned child image readiness timed out.");
    }

    private static void RequireLive(bool exited)
    {
        if (exited)
            throw new InvalidOperationException("Owned child exited before image binding.");
    }
}
