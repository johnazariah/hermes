using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Hermes.Tray;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class NativeBoundaryTests
{
    [Theory]
    [InlineData("""{"status":"healthy","service":"hermes","processId":123}""", true, 123)]
    [InlineData("""{"status":"healthy","service":"hermes"}""", false, null)]
    [InlineData("""{"status":"healthy","service":"other","processId":321}""", false, 321)]
    [InlineData("not-json", false, null)]
    public async Task Health_requires_wire_identity(string body, bool healthy, int? pid)
    {
        using var probe = new HttpHealthProbe(new HttpClient(new StaticHandler(HttpStatusCode.OK, body)));
        var result = await probe.ProbeAsync(new("http://127.0.0.1:31991/"), CancellationToken.None);
        Assert.True(result.Occupied);
        Assert.Equal(healthy, result.Healthy);
        Assert.Equal(pid, result.ProcessId);
    }

    [Fact]
    public async Task Non_success_response_is_occupied_not_available_for_adoption()
    {
        using var probe = new HttpHealthProbe(new HttpClient(new StaticHandler(HttpStatusCode.NotFound, "")));
        Assert.Equal(new(true, false, null),
            await probe.ProbeAsync(new("http://127.0.0.1:31991/"), CancellationToken.None));
    }

    [Fact]
    public void Launch_uses_apphost_control_stdin_and_explicit_environment()
    {
        var options = new TrayOptions(@"C:\synthetic\config", 31991, @"C:\synthetic\service\Hermes.Service.exe");
        var start = NativeProcessLauncher.CreateStartInfo(options);
        Assert.Equal(options.ServicePath, start.FileName);
        Assert.Equal(@"C:\synthetic\service", start.WorkingDirectory);
        Assert.Equal([ServiceWire.ControlArgument], start.ArgumentList);
        Assert.Equal(options.ConfigDirectory, start.Environment[ServiceWire.ConfigEnvironment]);
        Assert.Equal("31991", start.Environment[ServiceWire.PortEnvironment]);
        Assert.Equal("http://127.0.0.1:31991/", start.Environment["ASPNETCORE_URLS"]);
        Assert.False(start.UseShellExecute);
    }

    [Fact]
    public void Synthetic_launch_does_not_inherit_application_environment()
    {
        var options = new TrayOptions(@"C:\synthetic\config", 31991, @"C:\synthetic\service.exe", true);
        var start = NativeProcessLauncher.CreateStartInfo(options);
        Assert.Equal(options.ConfigDirectory, start.Environment["USERPROFILE"]);
        Assert.Equal(options.ConfigDirectory, start.Environment["APPDATA"]);
        Assert.DoesNotContain("HERMES_MCP_TOKEN", start.Environment.Keys);
        Assert.DoesNotContain("AZURE_OPENAI_API_KEY", start.Environment.Keys);
    }

    [Fact]
    public async Task Both_real_process_pipes_are_drained_without_logging_their_content()
    {
        var log = new MemoryLog();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var start = PipeChild();
        await using var process = NativeOwnedProcess.Start(start, log);
        await process.RequestShutdownAsync(deadline.Token);
        await process.WaitForExitAsync(deadline.Token);
        await process.DrainOutputAsync(deadline.Token);
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(2, log.Entries.Count(entry => entry.Code == EventCode.OutputDrained));
        Assert.All(log.Entries.Where(entry => entry.Code == EventCode.OutputDrained),
            entry => Assert.True(entry.Characters > 128 * 1024));
        Assert.DoesNotContain("synthetic-private-output", JsonSerializer.Serialize(log.Entries));
    }

    private static ProcessStartInfo PipeChild()
    {
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(executable) { CreateNoWindow = true };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command" })
            start.ArgumentList.Add(argument);
        start.ArgumentList.Add(
            "1..10000 | ForEach-Object { [Console]::Out.WriteLine('synthetic-private-output'); " +
            "[Console]::Error.WriteLine('synthetic-private-output') }; " +
            "if ([Console]::ReadLine() -eq 'shutdown') { exit 0 } else { exit 7 }");
        return start;
    }
}

internal sealed class StaticHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}
