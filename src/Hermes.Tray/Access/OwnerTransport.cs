using System.Buffers.Binary;
using System.IO.Pipes;
using System.Security;
using System.Security.Principal;

namespace Hermes.Tray.Access;

internal sealed record OwnerBudgets(
    TimeSpan Connect, TimeSpan Exchange, TimeSpan Header, TimeSpan Payload)
{
    public static OwnerBudgets Default { get; } = new(
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

    public void Validate()
    {
        if (Connect <= TimeSpan.Zero || Connect > Default.Connect ||
            Exchange <= TimeSpan.Zero || Exchange > Default.Exchange ||
            Header <= TimeSpan.Zero || Header > Default.Header ||
            Payload <= TimeSpan.Zero || Payload > Default.Payload)
            throw new ArgumentOutOfRangeException(nameof(OwnerBudgets));
    }
}

internal interface IOwnerConnection : IDisposable
{
    Stream Stream { get; }
    Task ConnectAsync(CancellationToken cancellationToken);
}

internal interface IOwnerConnectionFactory
{
    IOwnerConnection Create(OwnerTarget target);
}

internal sealed class OwnerPipeConnectionFactory : IOwnerConnectionFactory
{
    public IOwnerConnection Create(OwnerTarget target) => new OwnerPipeConnection(target);
}

internal sealed class OwnerPipeConnection(OwnerTarget target) : IOwnerConnection
{
    // Client-side CurrentUserOnly is essential: constructor use alone does not validate a peer.
    private readonly NamedPipeClientStream _pipe = new(
        ".", target.PipeName, PipeDirection.InOut,
        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
        TokenImpersonationLevel.Anonymous, HandleInheritability.None);

    public Stream Stream => _pipe;
    public Task ConnectAsync(CancellationToken cancellationToken) =>
        _pipe.ConnectAsync(cancellationToken);
    public void Dispose() => _pipe.Dispose();
}

internal static class OwnerFrames
{
    public static async Task WriteAsync(
        Stream stream, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length is < 1 or > 8192)
            throw new AccessProtocolFault(OwnerFailure.Protocol);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]> ReadAsync(
        Stream stream, OwnerBudgets budgets, CancellationToken cancellationToken)
    {
        try
        {
            var header = new byte[4];
            await ReadPartAsync(stream, header, budgets.Header, cancellationToken);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length is < 1 or > 8192)
                throw new AccessProtocolFault(OwnerFailure.Protocol);
            var payload = new byte[checked((int)length)];
            await ReadPartAsync(stream, payload, budgets.Payload, cancellationToken);
            return payload;
        }
        catch (EndOfStreamException) { throw new AccessProtocolFault(OwnerFailure.Protocol); }
    }

    private static async Task ReadPartAsync(
        Stream stream, Memory<byte> buffer, TimeSpan budget, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(budget);
        using var interrupted = deadline.Token.Register(stream.Dispose);
        try { await stream.ReadExactlyAsync(buffer, deadline.Token); }
        catch (Exception error) when (
            deadline.IsCancellationRequested && error is IOException or ObjectDisposedException)
        {
            throw new OperationCanceledException(deadline.Token);
        }
    }
}

internal sealed class OwnerTransport
{
    private readonly IOwnerConnectionFactory _connections;
    private readonly OwnerBudgets _budgets;

    public OwnerTransport(IOwnerConnectionFactory connections, OwnerBudgets? budgets = null)
    {
        _connections = connections;
        _budgets = budgets ?? OwnerBudgets.Default;
        _budgets.Validate();
    }

    public async Task<byte[]> ExchangeAsync(
        OwnerTarget target, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var connection = _connections.Create(target);
            await ConnectAsync(connection, cancellationToken);
            return await ExchangeConnectedAsync(connection, request, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AccessProtocolFault(OwnerFailure.Timeout);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            SecurityException or NotSupportedException or ObjectDisposedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new AccessProtocolFault(OwnerFailure.Unavailable);
        }
    }

    private async Task ConnectAsync(IOwnerConnection connection, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_budgets.Connect);
        using var interrupted = deadline.Token.Register(connection.Dispose);
        try { await connection.ConnectAsync(deadline.Token); }
        catch (Exception error) when (
            deadline.IsCancellationRequested && error is IOException or ObjectDisposedException)
        {
            throw new OperationCanceledException(deadline.Token);
        }
    }

    private async Task<byte[]> ExchangeConnectedAsync(
        IOwnerConnection connection, ReadOnlyMemory<byte> request, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_budgets.Exchange);
        using var interrupted = deadline.Token.Register(connection.Dispose);
        try
        {
            await OwnerFrames.WriteAsync(connection.Stream, request, deadline.Token);
            return await OwnerFrames.ReadAsync(connection.Stream, _budgets, deadline.Token);
        }
        catch (Exception error) when (
            deadline.IsCancellationRequested && error is IOException or ObjectDisposedException)
        {
            throw new OperationCanceledException(deadline.Token);
        }
    }
}
