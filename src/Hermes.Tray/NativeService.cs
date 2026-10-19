using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace Hermes.Tray;

internal sealed class JsonServiceLog : IServiceLog
{
    private readonly object _gate = new();
    private readonly string _path;

    public JsonServiceLog(TrayOptions options)
    {
        Directory.CreateDirectory(options.LogDirectory);
        _path = Path.Combine(options.LogDirectory, "tray-lifecycle.jsonl");
        Write(new(EventCode.Configuration, Detail: JsonSerializer.Serialize(new
        {
            options.ServicePath, options.ConfigDirectory, options.Port, LogPath = _path
        })));
    }

    public void Write(LogEntry entry)
    {
        try
        {
            lock (_gate)
                File.AppendAllText(_path, JsonSerializer.Serialize(new { At = DateTimeOffset.UtcNow, Entry = entry }) + "\n");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceError("Hermes lifecycle log write failed: {0}", SafeFailure.Describe(error));
        }
    }
}

internal sealed class HttpHealthProbe : IHealthProbe, IHealthDiagnostics, IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly HealthProbePolicy _policy;
    private HealthProbeDiagnostic? _first;
    private HealthProbeDiagnostic? _last;

    public HttpHealthProbe(HttpClient? http = null, HealthProbePolicy? policy = null)
    {
        _policy = policy ?? HealthProbePolicy.Default;
        _policy.Validate();
        _http = http ?? new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseProxy = false, ConnectTimeout = _policy.ConnectTimeout
        });
        _http.Timeout = Timeout.InfiniteTimeSpan;
        _http.MaxResponseContentBufferSize = 4096;
    }

    public HealthProbeDiagnostic? FirstDiagnostic => Volatile.Read(ref _first);
    public HealthProbeDiagnostic? LastDiagnostic => Volatile.Read(ref _last);

    public async Task<HealthObservation> ProbeAsync(Uri origin, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_policy.RequestTimeout);
        try
        {
            var read = await ReadClassifiedAsync(timeout.Token, cancellationToken, origin);
            cancellationToken.ThrowIfCancellationRequested();
            return Publish(origin.Port, started, read);
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            Publish(origin.Port, started, new(new(true, false, null),
                HealthProbeOutcome.Cancelled, Failure: SafeFailure.Describe(error)));
            throw;
        }
    }

    private async Task<HealthRead> ReadClassifiedAsync(
        CancellationToken timeoutToken, CancellationToken callerToken, Uri origin)
    {
        try { return await ReadAsync(origin, timeoutToken); }
        catch (OperationCanceledException error) when (!callerToken.IsCancellationRequested)
        {
            return new(new(true, false, null), HealthProbeOutcome.Timeout,
                Failure: SafeFailure.Describe(error));
        }
        catch (HttpRequestException error) { return ClassifyTransportFailure(error); }
        catch (JsonException error)
        {
            return new(new(true, false, null), HealthProbeOutcome.InvalidJson,
                StatusCode: 200, Failure: SafeFailure.Describe(error));
        }
    }

    private static HealthRead ClassifyTransportFailure(HttpRequestException error)
    {
        var socketError = FindSocketError(error);
        var refused = error.HttpRequestError == HttpRequestError.ConnectionError &&
            socketError == SocketError.ConnectionRefused;
        return new(new(!refused, false, null),
            refused ? HealthProbeOutcome.ConnectionRefused : HealthProbeOutcome.TransportFailure,
            SocketError: socketError, HttpError: error.HttpRequestError,
            Failure: SafeFailure.Describe(error));
    }

    private static SocketError? FindSocketError(Exception? error) => error switch
    {
        SocketException socket => socket.SocketErrorCode,
        { InnerException: { } inner } => FindSocketError(inner),
        _ => null
    };

    private HealthObservation Publish(int port, long started, HealthRead read)
    {
        var diagnostic = new HealthProbeDiagnostic(port, read.Outcome, read.Observation,
            Stopwatch.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond,
            _policy.ConnectTimeout.Ticks / TimeSpan.TicksPerMillisecond,
            _policy.RequestTimeout.Ticks / TimeSpan.TicksPerMillisecond,
            read.SocketError, read.HttpError, read.StatusCode, read.Failure);
        Interlocked.CompareExchange(ref _first, diagnostic, null);
        Volatile.Write(ref _last, diagnostic);
        return read.Observation;
    }

    private async Task<HealthRead> ReadAsync(Uri origin, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(origin, "health"), cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            return new(new(true, false, null), HealthProbeOutcome.HttpStatus,
                StatusCode: (int)response.StatusCode);
        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var body = JsonSerializer.Deserialize<HealthPayload>(content, Json);
        var healthy = body is { Status: "healthy", Service: "hermes", ProcessId: > 0 };
        var observation = new HealthObservation(true, healthy,
            body is { ProcessId: > 0 } ? body.ProcessId : null);
        return new(observation,
            healthy ? HealthProbeOutcome.Healthy : HealthProbeOutcome.InvalidIdentity, StatusCode: 200);
    }

    public void Dispose() => _http.Dispose();
    private sealed record HealthPayload(string Status, string Service, int ProcessId);
    private sealed record HealthRead(
        HealthObservation Observation, HealthProbeOutcome Outcome,
        SocketError? SocketError = null, HttpRequestError? HttpError = null,
        int? StatusCode = null, string? Failure = null);
}

internal sealed class NativeProcessLauncher(IServiceLog log) : IProcessLauncher
{
    public IOwnedProcess Start(TrayOptions options) =>
        NativeOwnedProcess.Start(CreateStartInfo(options), log);

    internal static ProcessStartInfo CreateStartInfo(TrayOptions options)
    {
        var start = new ProcessStartInfo
        {
            FileName = options.ServicePath,
            WorkingDirectory = Path.GetDirectoryName(options.ServicePath) ?? options.ConfigDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add(ServiceWire.ControlArgument);
        if (options.Synthetic)
            IsolateEnvironment(start, options);
        start.Environment[ServiceWire.ConfigEnvironment] = options.ConfigDirectory;
        start.Environment[ServiceWire.PortEnvironment] = options.Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["ASPNETCORE_URLS"] = options.BaseAddress.AbsoluteUri;
        return start;
    }

    private static void IsolateEnvironment(ProcessStartInfo start, TrayOptions options)
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        start.Environment.Clear();
        start.Environment["SystemRoot"] = windows;
        start.Environment["WINDIR"] = windows;
        start.Environment["PATH"] = Path.Combine(windows, "System32");
        foreach (var name in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP" })
            start.Environment[name] = options.ConfigDirectory;
    }
}

internal sealed class NativeOwnedProcess : IOwnedProcess
{
    private readonly Process _process;
    private readonly IServiceLog _log;
    private readonly CancellationTokenSource _drainCancellation = new();
    private readonly Task _stdout;
    private readonly Task _stderr;
    private int _disposed;

    private NativeOwnedProcess(Process process, IServiceLog log)
    {
        (_process, _log) = (process, log);
        _stdout = DrainAsync(process.StandardOutput, OutputStream.StandardOutput, _drainCancellation.Token);
        _stderr = DrainAsync(process.StandardError, OutputStream.StandardError, _drainCancellation.Token);
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;
    public int? ExitCode => HasExited ? _process.ExitCode : null;

    internal static NativeOwnedProcess Start(ProcessStartInfo start, IServiceLog log)
    {
        start.UseShellExecute = false;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        var process = new Process { StartInfo = start };
        var started = false;
        try
        {
            started = process.Start();
            return started ? new(process, log) : throw new Win32Exception("Service process did not start.");
        }
        catch
        {
            AbortPartialStart(process, started);
            throw;
        }
    }

    private static void AbortPartialStart(Process process, bool started)
    {
        try
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
        finally { process.Dispose(); }
    }

    public async Task RequestShutdownAsync(CancellationToken cancellationToken)
    {
        await _process.StandardInput.WriteLineAsync(ServiceWire.ShutdownCommand.AsMemory(), cancellationToken);
        await _process.StandardInput.FlushAsync(cancellationToken);
    }

    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        _process.WaitForExitAsync(cancellationToken);

    public Task DrainOutputAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_stdout, _stderr).WaitAsync(cancellationToken);

    public void KillTree()
    {
        try
        {
            if (!HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (HasExited) { }
    }

    private async Task DrainAsync(StreamReader reader, OutputStream stream, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        long count = 0;
        try
        {
            while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) is > 0 and var read)
                count += read;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException error)
        {
            _log.Write(new(EventCode.PipeFailed, Id, Stream: stream, Detail: SafeFailure.Describe(error)));
        }
        finally { _log.Write(new(EventCode.OutputDrained, Id, Stream: stream, Characters: count)); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _drainCancellation.Cancel();
        _process.StandardOutput.Dispose();
        _process.StandardError.Dispose();
        try { await Task.WhenAll(_stdout, _stderr); }
        finally
        {
            _process.Dispose();
            _drainCancellation.Dispose();
        }
    }
}
