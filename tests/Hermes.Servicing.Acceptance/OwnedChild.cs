namespace Hermes.Servicing.Acceptance;

internal sealed class OwnedChild : IDisposable, ISetupChild
{
    private readonly Process _process;
    internal Stamp Identity { get; }

    private OwnedChild(Process process)
    {
        _process = process;
        Identity = Files.Identity(process);
    }

    Stream ISetupChild.StandardOutput => _process.StandardOutput.BaseStream;
    Stream ISetupChild.StandardError => _process.StandardError.BaseStream;
    void ISetupChild.CloseStandardOutput() => _process.StandardOutput.Dispose();
    void ISetupChild.CloseStandardError() => _process.StandardError.Dispose();
    bool ISetupChild.HasExited => _process.HasExited;
    bool ISetupChild.IsOwned =>
        _process.Id == Identity.ProcessId &&
        _process.StartTime.ToUniversalTime().ToFileTimeUtc() == Identity.StartUtcFileTime;

    void ISetupChild.Kill() => _process.Kill(entireProcessTree: false);

    Task<int> ISetupChild.WaitAsync(CancellationToken cancellationToken) =>
        WaitAsync(cancellationToken);

    internal static OwnedChild Start(
        string executable, IEnumerable<string> arguments, string work,
        bool captureStandardError = false)
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
        return Bind(Process.Start(start) ?? throw new InvalidOperationException(),
            captureStandardError);
    }

    private static OwnedChild Bind(Process process, bool checkedSetup)
    {
        try { return new(process); }
        catch (Exception failure) when (checkedSetup)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: false);
                if (!process.WaitForExit(
                    checked((int)SetupChildBudget.Default.Cleanup.TotalMilliseconds)))
                    throw new TimeoutException();
            }
            catch (Exception cleanup)
            {
                throw new SetupChildException(SetupChildFault.CleanupFailed,
                    new AggregateException(failure, cleanup));
            }
            finally { process.Dispose(); }
            throw;
        }
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
