using System.ComponentModel;
using System.Diagnostics;

namespace Hermes.Servicing.Acceptance;

internal sealed partial class OwnedChild
{
    private static readonly TimeSpan BindingCleanupTimeout = TimeSpan.FromSeconds(5);
    private const string BindingCleanupTimeoutMessage =
        "Owned child binding cleanup timed out.";

    internal static ProcessStartInfo CreateStartInfo(
        string executable, IEnumerable<string> arguments, string work,
        bool captureStandardError)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = captureStandardError,
            WorkingDirectory = Path.GetDirectoryName(executable)
                ?? throw new InvalidDataException()
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        HideSdk(start, work);
        return start;
    }

    private static void HideSdk(ProcessStartInfo start, string work)
    {
        start.Environment["PATH"] = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        start.Environment["DOTNET_ROOT"] = Path.Combine(work, "absent-sdk");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(work, "absent-sdk");
    }

    // Start uses this exact path. Its factory constructs OwnedChild and runs the
    // unchanged Files.Identity proof; readiness alone never returns an owner.
    internal static TOwner Bind<TOwner>(
        IOwnedChildBinding child, TimeProvider clock, Func<TOwner> createOwner)
        where TOwner : notnull
    {
        try
        {
            OwnedChildStartup.RequireImage(child, clock);
            return createOwner();
        }
        catch (Exception failure)
        {
            CleanupBinding(child, clock, failure);
            throw;
        }
    }

    private static void CleanupBinding(
        IOwnedChildBinding child, TimeProvider clock, Exception failure)
    {
        var started = clock.GetTimestamp();
        var stop = CaptureBindingCleanup(child.Kill);
        var reap = CaptureBindingCleanup(() => ReapBinding(child, clock, started));
        var dispose = CaptureBindingCleanup(
            () => DisposeBinding(child, clock, started, reap is null));
        var faults = new[] { stop, reap, dispose }.OfType<Exception>()
            .Where(error => !(ReferenceEquals(error, stop) && reap is null &&
                error is (InvalidOperationException or Win32Exception))).ToArray();
        if (faults.Length > 0)
            throw new SetupChildException(SetupChildFault.CleanupFailed,
                new AggregateException(new[] { failure }.Concat(faults)));
    }

    private static void ReapBinding(
        IOwnedChildBinding child, TimeProvider clock, long started)
    {
        var remaining = BindingCleanupTimeout - clock.GetElapsedTime(started);
        var milliseconds = remaining > TimeSpan.Zero
            ? checked((int)Math.Ceiling(remaining.TotalMilliseconds))
            : 0;
        var exited = child.WaitForExit(milliseconds);
        if (!exited || clock.GetElapsedTime(started) >= BindingCleanupTimeout)
            throw new TimeoutException(BindingCleanupTimeoutMessage);
    }

    private static void DisposeBinding(
        IOwnedChildBinding child, TimeProvider clock, long started, bool reaped)
    {
        child.Dispose();
        if (reaped && clock.GetElapsedTime(started) >= BindingCleanupTimeout)
            throw new TimeoutException(BindingCleanupTimeoutMessage);
    }

    private static Exception? CaptureBindingCleanup(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }
}
