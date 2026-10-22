namespace Hermes.Servicing.Acceptance;

internal sealed record ProbeOutcome(
    string Outcome,
    string? Reason,
    bool LeaseDisposable,
    bool AcquiringThreadExited);

internal sealed record ProbeMessage(
    string Kind,
    Stamp Process,
    LoadedCode Loaded,
    ProbeOutcome? Admission,
    bool EffectExists);

public static class RuntimeProbe
{
    public static async Task<int> RunAsync(
        string[] arguments, CancellationToken cancellationToken)
    {
        CiAdmission.Read(cleanOwner: true).Require();
        Require.That(arguments is [_, _, _, _]);
        var root = arguments[0];
        var gate = arguments[1];
        var effect = arguments[2];
        TemporaryPaths.Require(root, directory: true);
        if (arguments[3] != "-")
            AssemblyLoadContext.Default.LoadFromAssemblyPath(arguments[3]);
        Write("mapped", null, effect);
        Require.That(await Console.In.ReadLineAsync(cancellationToken) == "enter");
        var outcome = await EnterOnTerminatingThreadAsync(
            root, gate, effect, cancellationToken);
        Write("entered", outcome, effect);
        Require.That(await Console.In.ReadLineAsync(cancellationToken) == "exit");
        return 0;
    }

    private static async Task<ProbeOutcome> EnterOnTerminatingThreadAsync(
        string root, string gate, string effect, CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<ProbeOutcome>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => Enter(root, gate, effect, completed));
        thread.Start();
        var outcome = await completed.Task.WaitAsync(cancellationToken);
        Require.That(thread.Join(TimeSpan.FromSeconds(10)));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return outcome with { AcquiringThreadExited = !thread.IsAlive };
    }

    private static void Enter(string root, string gate, string effect,
        TaskCompletionSource<ProbeOutcome> completed)
    {
        try
        {
            var admission = WindowsStartupAdmission.Create(root,
                new WindowsPhysicalPaths(), new WindowsRuntimeGate(gate),
                new CurrentLoadedCode(), ProcessLeaseRoot.Instance);
            var result = Describe(admission.Enter(RuntimeEntry.Service));
            if (result.Outcome is "Admitted" or "Outside")
                File.WriteAllText(effect, "SYNTHETIC post-admission dispatch");
            completed.TrySetResult(result);
        }
        catch (Exception error) { completed.TrySetException(error); }
    }

    private static ProbeOutcome Describe(Admission admission) => admission switch
    {
        Admitted(var lease) => new("Admitted", null, lease is IDisposable, false),
        Refused(var reason) => new("Refused", reason.ToString(), false, false),
        OutsideKnownInstallation => new("Outside", null, false, false),
        _ => throw new InvalidDataException()
    };

    private static void Write(string kind, ProbeOutcome? admission, string effect) =>
        Console.WriteLine(EvidenceJson.Encode(new ProbeMessage(
            kind, Files.Current(), new CurrentLoadedCode().Read(),
            admission, File.Exists(effect))));
}
