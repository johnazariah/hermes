namespace Hermes.Servicing.Acceptance;

internal sealed class OwnedChild : IDisposable
{
    private readonly Process _process;
    internal Stamp Identity { get; }

    private OwnedChild(Process process)
    {
        _process = process;
        Identity = Files.Identity(process);
    }

    internal static OwnedChild Start(
        string executable, IEnumerable<string> arguments, string work)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
                ?? throw new InvalidDataException()
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        HideSdk(start, work);
        return new(Process.Start(start) ?? throw new InvalidOperationException());
    }

    private static void HideSdk(ProcessStartInfo start, string work)
    {
        start.Environment["PATH"] = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32");
        start.Environment["DOTNET_ROOT"] = Path.Combine(work, "absent-sdk");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(work, "absent-sdk");
    }

    internal async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        var line = await _process.StandardOutput.ReadLineAsync(cancellationToken)
            ?? throw new EndOfStreamException("Owned child ended before evidence.");
        Require.That(line.Length <= EvidenceJson.MaximumEventCharacters);
        return line;
    }

    internal async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    internal async Task<int> WaitAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        return _process.ExitCode;
    }

    internal async Task CrashAsync(CancellationToken cancellationToken)
    {
        Require.That(!_process.HasExited);
        Require.That(Files.Identity(_process) == Identity);
        _process.Kill(entireProcessTree: false);
        await _process.WaitForExitAsync(cancellationToken);
    }

    internal async Task StopProbeAsync(CancellationToken cancellationToken)
    {
        await SendAsync("exit", cancellationToken);
        Require.That(await WaitAsync(cancellationToken) == 0);
    }

    public void Dispose() => _process.Dispose();
}
