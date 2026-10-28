using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
internal sealed class ControlServer : IAsyncDisposable
{
    private readonly CallAuthority _authority;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _loop;

    private ControlServer(CallAuthority authority)
    {
        _authority = authority;
        _loop = ListenAsync();
    }

    internal static async Task<ControlServer> StartAsync(
        CallAuthority authority, CancellationToken cancellationToken)
    {
        var server = new ControlServer(authority);
        try
        {
            await server._ready.Task.WaitAsync(cancellationToken);
            return server;
        }
        catch
        {
            await server.DisposeAsync();
            throw;
        }
    }

    private async Task ListenAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
                await AcceptAsync(_shutdown.Token);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception error)
        {
            _authority.Fault();
            _ready.TrySetException(error);
        }
        finally { _ready.TrySetCanceled(); }
    }

    private async Task AcceptAsync(CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeServerStream(_authority.Endpoint.PipeName,
            PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        _ready.TrySetResult();
        await pipe.WaitForConnectionAsync(cancellationToken);
        await HandleAsync(pipe, cancellationToken);
    }

    private async Task HandleAsync(
        NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var exchange = await ControlPackets.ReadAsync(
                pipe, ControlJson.Default.GuardExchange, deadline.Token);
            if (WindowsPeers.ClientSid(pipe) != _authority.Endpoint.UserSid)
                throw new InvalidDataException("Wrong pipe principal.");
            await ServeAsync(pipe, exchange, WindowsPeers.Client(pipe), deadline.Token);
        }
        catch (Exception error) when (error is IOException or JsonException or
            OperationCanceledException or TimeoutException or Win32Exception or
            UnauthorizedAccessException or InvalidOperationException or ArgumentException) { }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, GuardExchange exchange,
        ProcessInstance client, CancellationToken cancellationToken)
    {
        var pending = _authority.Prepare(exchange, client);
        var reply = new ControlReply(ControlStep.Refuse,
            _authority.Endpoint.RunId, exchange.Challenge);
        if (pending is null)
        {
            await ReplyAsync(pipe, reply, cancellationToken);
            return;
        }
        try
        {
            await ReplyAsync(pipe, reply with { Step = ControlStep.Prepared }, cancellationToken);
            await pending.Completion.Task.WaitAsync(cancellationToken);
            await ReplyAsync(pipe, reply with { Step = ControlStep.Permit }, cancellationToken);
        }
        finally { _authority.Release(exchange.Challenge); }
    }

    private static Task ReplyAsync(NamedPipeServerStream pipe,
        ControlReply reply, CancellationToken cancellationToken) =>
        ControlPackets.WriteAsync(pipe, reply, ControlJson.Default.ControlReply, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        await _loop;
        _shutdown.Dispose();
    }
}
