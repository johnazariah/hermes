namespace Hermes.Servicing.Acceptance;

internal sealed record WorkerHello(string Kind, Stamp Coordinator);

internal static class WorkerHandshake
{
    internal static async Task WaitAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine(EvidenceJson.Encode(new WorkerHello("worker-armed", Files.Current())));
        Require.That(await Console.In.ReadLineAsync(cancellationToken) == "begin");
    }
}
