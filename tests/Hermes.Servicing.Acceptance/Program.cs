namespace Hermes.Servicing.Acceptance;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(12));
        try
        {
            return arguments switch
            {
                ["run", .. var flags] =>
                    await RunAsync(AcceptanceOptions.Parse(flags), deadline.Token),
                ["root-worker", var request, var operation, var hold] =>
                    await NativeRoot.RunAsync(AcceptanceContext.Read(request), operation,
                        Enum.Parse<HoldPoint>(hold, ignoreCase: false), deadline.Token),
                _ => 2
            };
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(ProofFiles.Failure(error));
            return 1;
        }
    }

    private static async Task<int> RunAsync(
        AcceptanceOptions options, CancellationToken cancellationToken)
    {
        var context = AcceptanceContext.Create(options);
        var before = context.OwnerUnknown();
        var cases = ImmutableArray.CreateBuilder<CaseReport>();
        string? failure = null;
        try
        {
            Require.That(MsiCases.ReadReady().Installation is null);
            cases.Add(await KernelCases.RunAsync(context, cancellationToken));
            cases.AddRange(await MsiCases.RunAsync(context, cancellationToken));
        }
        catch (Exception error)
        {
            failure = ProofFiles.Failure(error);
            cases.Add(new("execution", "diagnostic", CaseState.Failed, [], failure));
        }
        return await FinishAsync(context, before, cases.ToImmutable(), failure);
    }

    private static async Task<int> FinishAsync(AcceptanceContext context,
        ImmutableArray<FileEvidence> before, ImmutableArray<CaseReport> cases,
        string? failure)
    {
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var cleanup = await MsiCases.CleanupAsync(context, cleanupDeadline.Token);
        var after = CaptureAfter(context);
        var preserved = before.SequenceEqual(after);
        var resultCases = cases.Add(Preservation(preserved, before, after));
        if (!cleanup.Completed)
            resultCases = resultCases.Add(new("cleanup", "actual-setup-main",
                CaseState.Failed, [cleanup], cleanup.Failure));
        var code = ReportContract.ExitCode(resultCases.Select(item => item.State).ToArray());
        var report = new AcceptanceReport(1, "hermes-a2-actual-component",
            code == 0, code == 0, "component; not complete installed A2 acceptance",
            context.Job, context.Owner.FixtureId, context.Inputs,
            before, after, resultCases, cleanup, failure);
        EvidenceJson.WriteNew(context.Options.ReportPath, report);
        Console.WriteLine(EvidenceJson.Encode(new { report = context.Options.ReportPath, exitCode = code }));
        return code;
    }

    private static ImmutableArray<FileEvidence> CaptureAfter(AcceptanceContext context)
    {
        try { return context.OwnerUnknown(); }
        catch { return []; }
    }

    private static CaseReport Preservation(bool preserved,
        ImmutableArray<FileEvidence> before, ImmutableArray<FileEvidence> after) =>
        new("owner-and-unknown-preservation", "job-bound-synthetic-fixture",
            preserved ? CaseState.Passed : CaseState.Failed,
            [new { before, after }]);
}
