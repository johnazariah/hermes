using System.Diagnostics;

namespace Hermes.Servicing.Acceptance;

internal sealed record CrashEventChildCommandEffects(
    Action RequireContext,
    Func<CancellationToken, Task<int>> Child);

internal sealed record CrashEventChildEffects(
    CrashPipe Input,
    Func<CancellationToken, Task> WriteReady,
    Func<uint, int> SetFlags,
    Func<(int HResult, uint Flags)> GetFlags,
    Action<nint, nint, uint> RaiseFailFast);

internal static class CrashEventChild
{
    internal const string Command = "startup-crash-event-native-child";

    internal static async Task<int> DispatchAsync(
        string[] arguments, CrashEventChildCommandEffects effects,
        CancellationToken cancellationToken)
    {
        if (arguments is not [Command])
            return 2;
        try { effects.RequireContext(); }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            return CrashControl.GuardRefused;
        }
        try { return await effects.Child(cancellationToken); }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            return CrashControl.TransportRefused;
        }
    }

    internal static async Task<int> RunAsync(
        CrashEventChildEffects effects, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ConfigureWer(effects))
                return CrashControl.WerRefused;
            return await ArmAndCrashAsync(effects, cancellationToken);
        }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            return CrashControl.TransportRefused;
        }
    }

    private static bool ConfigureWer(CrashEventChildEffects effects)
    {
        try
        {
            if (effects.SetFlags((uint)CrashControl.WerPolicy) != 0)
                return false;
            var read = effects.GetFlags();
            return read.HResult == 0 && read.Flags == (uint)CrashControl.WerPolicy;
        }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            return false;
        }
    }

    private static async Task<int> ArmAndCrashAsync(
        CrashEventChildEffects effects, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await effects.WriteReady(cancellationToken);
        if (!await CrashWire.ReadFixedAsync(
            effects.Input, CrashControl.Arm, static () => { }, cancellationToken))
            return CrashControl.InputRefused;
        cancellationToken.ThrowIfCancellationRequested();
        // Fixed documented flags=0; null EXCEPTION_RECORD and CONTEXT.
        effects.RaiseFailFast(nint.Zero, nint.Zero, 0u);
        // A returning native effect is refusal, never another crash or success.
        return CrashControl.CrashReturned;
    }
}

internal sealed record CrashEventLaunchEffects(
    Action RequireContext,
    Func<StepJobContext> ReadContext,
    Func<ProcessStartInfo> CreateTransport);

internal static class CrashEventLaunch
{
    private const string Actions = "GITHUB_ACTIONS";
    private const string RunnerOs = "RUNNER_OS";
    private const string RunnerEnvironment = "RUNNER_ENVIRONMENT";
    private const string Job = "GITHUB_JOB";
    private const string Server = "GITHUB_SERVER_URL";
    private const string Repository = "GITHUB_REPOSITORY";

    internal static StepJobContext ReadContext(
        bool windows, Func<string, string?> read) => new(
            windows, read(Actions), read(RunnerOs), read(RunnerEnvironment),
            read(Job), read(Server), read(Repository));

    internal static ProcessStartInfo CreateStartInfo(CrashEventLaunchEffects effects)
    {
        effects.RequireContext();
        var actual = effects.ReadContext();
        actual.Require();
        var start = effects.CreateTransport();
        start.Arguments = "";
        start.ArgumentList.Clear();
        start.ArgumentList.Add(CrashEventChild.Command);
        PopulateActual(start, actual);
        return start;
    }

    private static void PopulateActual(ProcessStartInfo start, StepJobContext actual)
    {
        // The fixed transport already discarded inherited secrets/profile data.
        // Replace every admission field with its validated actual parent value.
        // These cooperative assertions are not independent host attestation.
        (string Key, string? Value)[] values =
        [
            (Actions, actual.Actions), (RunnerOs, actual.RunnerOs),
            (RunnerEnvironment, actual.RunnerEnvironment), (Job, actual.Job),
            (Server, actual.Server), (Repository, actual.Repository)
        ];
        foreach (var (key, value) in values)
            start.Environment[key] = value;
    }
}
