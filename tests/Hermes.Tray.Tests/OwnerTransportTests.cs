using System.Buffers.Binary;
using System.IO.Pipes;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hermes.Tray.Access;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OwnerTransportTests
{
    [Fact]
    public async Task Partial_reads_reassemble_one_frame_and_do_not_consume_a_second_frame()
    {
        var first = AccessFixture.Frame(Encoding.UTF8.GetBytes("{}"));
        var second = AccessFixture.Frame(Encoding.UTF8.GetBytes("""{"ignored":true}"""));
        using var stream = new AccessScriptStream([.. first, .. second]);
        var result = await OwnerFrames.ReadAsync(stream, OwnerBudgets.Default, CancellationToken.None);
        Assert.Equal("{}", Encoding.UTF8.GetString(result));
        Assert.Equal(second.Length, stream.Remaining);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8193)]
    [InlineData(-1)]
    public async Task Invalid_unsigned_lengths_fail_before_payload_allocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, unchecked((uint)length));
        using var stream = new AccessScriptStream(header);
        var error = await Assert.ThrowsAsync<AccessProtocolFault>(() =>
            OwnerFrames.ReadAsync(stream, OwnerBudgets.Default, CancellationToken.None));
        Assert.Equal(OwnerFailure.Protocol, error.Failure);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Truncated_header_or_payload_is_corruption(int bytes)
    {
        var complete = AccessFixture.Frame(Encoding.UTF8.GetBytes("{}"));
        using var stream = new AccessScriptStream(complete[..bytes]);
        var error = await Assert.ThrowsAsync<AccessProtocolFault>(() =>
            OwnerFrames.ReadAsync(stream, OwnerBudgets.Default, CancellationToken.None));
        Assert.Equal(OwnerFailure.Protocol, error.Failure);
    }

    [Fact]
    public async Task Writer_is_big_endian_bounded_and_exact()
    {
        using var stream = new AccessScriptStream([]);
        await OwnerFrames.WriteAsync(stream, Encoding.UTF8.GetBytes("{}"), CancellationToken.None);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 123, 125 }, stream.Written);
        await Assert.ThrowsAsync<AccessProtocolFault>(() =>
            OwnerFrames.WriteAsync(stream, new byte[8193], CancellationToken.None));
        await Assert.ThrowsAsync<AccessProtocolFault>(() =>
            OwnerFrames.WriteAsync(stream, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
    }

    [Theory]
    [InlineData("connect")]
    [InlineData("header")]
    [InlineData("payload")]
    [InlineData("total")]
    public async Task Every_deadline_interrupts_disposes_and_awaits_its_actual_work(string phase)
    {
        var header = new byte[] { 0, 0, 0, 2 };
        using var stream = new AccessScriptStream(phase == "payload" ? header : [], stallAtEnd: true);
        var connection = new AccessTestConnection(stream, phase == "connect");
        var transport = new OwnerTransport(new AccessTestConnections(connection), Budgets(phase));
        using var testLimit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<AccessProtocolFault>(() =>
            transport.ExchangeAsync(AccessFixture.Target(), Encoding.UTF8.GetBytes("{}"), testLimit.Token));
        Assert.Equal(OwnerFailure.Timeout, error.Failure);
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reclassified_as_timeout_and_closes_connection()
    {
        using var stream = new AccessScriptStream([], stallAtEnd: true);
        var factory = new AccessTestConnections(new AccessTestConnection(stream));
        var transport = new OwnerTransport(factory);
        using var cancellation = new CancellationTokenSource();
        var pending = transport.ExchangeAsync(AccessFixture.Target(), Encoding.UTF8.GetBytes("{}"), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(stream.Disposed);
        Assert.Equal(1, factory.Created);
    }

    [Fact]
    public async Task Already_cancelled_request_never_constructs_a_connection()
    {
        using var stream = new AccessScriptStream([]);
        var factory = new AccessTestConnections(new AccessTestConnection(stream));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new OwnerTransport(factory).ExchangeAsync(AccessFixture.Target(), new byte[] { 1 }, cancellation.Token));
        Assert.Equal(0, factory.Created);
    }

    [Fact]
    public async Task Actual_same_owner_byte_pipe_runs_the_real_client_and_exchanges_one_request()
    {
        var target = OwnerTarget.Create(
            Path.Combine(Path.GetTempPath(), "hermes-access-" + Guid.NewGuid().ToString("N")), 31991);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var server = new NamedPipeServerStream(target.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
            0, 0);
        var serverWork = ServeStatusAsync(server, target, deadline.Token);
        var client = new OwnerControlClient(target, new OwnerTransport(new OwnerPipeConnectionFactory()));
        var result = await client.GetStatusAsync(AccessFixture.Process, null, deadline.Token);
        var status = Assert.IsType<OwnerResult<OwnerStatus>.Success>(result).Value;
        Assert.Equal(OwnerPhase.Ready, status.Phase);
        Assert.Equal(AccessFixture.Instance(target), status.Instance);
        await serverWork;
    }

    [Fact]
    public async Task Invalid_local_resolve_returns_a_typed_failure_without_constructing_a_connection()
    {
        var target = AccessFixture.Target();
        using var stream = new AccessScriptStream([]);
        var connections = new AccessTestConnections(new AccessTestConnection(stream));
        var client = new OwnerControlClient(target, new OwnerTransport(connections));
        var pending = client.ResolveAsync(AccessFixture.Instance(target), "bad-code", CancellationToken.None);
        var failure = Assert.IsType<OwnerResult<OwnerPairing>.Failure>(await pending);
        Assert.Equal(OwnerFailure.Protocol, failure.Code);
        Assert.Equal(0, connections.Created);
    }

    [Theory]
    [InlineData("archive")]
    [InlineData("duplicate")]
    [InlineData("order")]
    [InlineData("default")]
    [InlineData("pairingId")]
    [InlineData("userCode")]
    [InlineData("challenge")]
    public async Task Invalid_local_approval_returns_a_typed_failure_without_constructing_a_connection(string input)
    {
        var target = AccessFixture.Target();
        using var stream = new AccessScriptStream([]);
        var connections = new AccessTestConnections(new AccessTestConnection(stream));
        var client = new OwnerControlClient(target, new OwnerTransport(connections));
        var pending = client.ApproveAsync(AccessFixture.Instance(target),
            InvalidLocalPairing(input), InvalidLocalGrant(input), CancellationToken.None);
        var failure = Assert.IsType<OwnerResult<OwnerApproval>.Failure>(await pending);
        var expected = input is "pairingId" or "userCode" or "challenge"
            ? OwnerFailure.Protocol : OwnerFailure.InvalidRequest;
        Assert.Equal(expected, failure.Code);
        Assert.Equal(0, connections.Created);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Already_cancelled_client_calls_observe_cancellation_before_invalid_local_payloads(bool approval)
    {
        var target = AccessFixture.Target();
        using var stream = new AccessScriptStream([]);
        var connections = new AccessTestConnections(new AccessTestConnection(stream));
        var client = new OwnerControlClient(target, new OwnerTransport(connections));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Task pending = approval
            ? client.ApproveAsync(AccessFixture.Instance(target), InvalidLocalPairing("challenge"),
                [AccessCapability.ArchiveRead], cancellation.Token)
            : client.ResolveAsync(AccessFixture.Instance(target), "bad-code", cancellation.Token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, connections.Created);
    }

    [Fact]
    public async Task Valid_local_resolve_reaches_transport_and_bad_peer_response_is_a_typed_failure()
    {
        var target = AccessFixture.Target();
        using var stream = new AccessScriptStream(AccessFixture.Frame(Encoding.UTF8.GetBytes("{}")));
        var connections = new AccessTestConnections(new AccessTestConnection(stream));
        var client = new OwnerControlClient(target, new OwnerTransport(connections));
        var result = await client.ResolveAsync(
            AccessFixture.Instance(target), AccessFixture.Code, CancellationToken.None);
        Assert.Equal(OwnerFailure.Protocol, Assert.IsType<OwnerResult<OwnerPairing>.Failure>(result).Code);
        Assert.Equal(1, connections.Created);
        using var request = JsonDocument.Parse(stream.Written.AsMemory(4));
        Assert.Equal(AccessFixture.Code, AccessJson.Code(request.RootElement.GetProperty("payload"), "userCode"));
    }

    [Fact]
    public async Task Valid_local_approval_keeps_the_actual_challenge_in_the_wire_request()
    {
        var target = AccessFixture.Target();
        using var stream = new AccessScriptStream(AccessFixture.Frame(Encoding.UTF8.GetBytes("{}")));
        var connections = new AccessTestConnections(new AccessTestConnection(stream));
        var client = new OwnerControlClient(target, new OwnerTransport(connections));
        var result = await client.ApproveAsync(AccessFixture.Instance(target),
            AccessFixture.Pairing(target), AccessCapabilities.Ui, CancellationToken.None);
        Assert.Equal(OwnerFailure.Protocol, Assert.IsType<OwnerResult<OwnerApproval>.Failure>(result).Code);
        Assert.Equal(1, connections.Created);
        using var request = JsonDocument.Parse(stream.Written.AsMemory(4));
        Assert.Equal(AccessFixture.Challenge,
            AccessJson.Challenge(request.RootElement.GetProperty("payload"), "challenge"));
    }

    private static OwnerPairing InvalidLocalPairing(string input)
    {
        var pairing = AccessFixture.Pairing(AccessFixture.Target());
        return input switch
        {
            "pairingId" => pairing with { PairingId = "invalid" },
            "userCode" => pairing with { UserCode = "invalid" },
            "challenge" => pairing with { Challenge = "invalid" },
            _ => pairing
        };
    }

    private static ImmutableArray<AccessCapability> InvalidLocalGrant(string input) => input switch
    {
        "archive" => [AccessCapability.ArchiveRead],
        "duplicate" => [AccessCapability.ConfigRead, AccessCapability.ConfigRead],
        "order" => [AccessCapability.Mutate, AccessCapability.ConfigRead],
        "default" => default,
        _ => AccessCapabilities.Ui
    };

    private static async Task ServeStatusAsync(
        NamedPipeServerStream server, OwnerTarget target, CancellationToken cancellationToken)
    {
        await server.WaitForConnectionAsync(cancellationToken);
        var bytes = await OwnerFrames.ReadAsync(server, OwnerBudgets.Default, cancellationToken);
        using var request = JsonDocument.Parse(bytes);
        var root = request.RootElement;
        AccessJson.Object(root, "version", "requestId", "expectedInstanceId", "operation", "payload");
        Assert.Equal("status", AccessJson.Text(root, "operation"));
        Assert.Equal(1, AccessJson.Integer(root, "version"));
        Assert.Equal(JsonValueKind.Null, root.GetProperty("expectedInstanceId").ValueKind);
        AccessJson.Object(root.GetProperty("payload"));
        await OwnerFrames.WriteAsync(server,
            AccessFixture.StatusReply(target, AccessJson.Identifier(root, "requestId")), cancellationToken);
        Assert.Equal(0, await server.ReadAsync(new byte[1], cancellationToken));
    }

    private static OwnerBudgets Budgets(string phase)
    {
        var shortBudget = TimeSpan.FromMilliseconds(30);
        return OwnerBudgets.Default with
        {
            Connect = phase == "connect" ? shortBudget : OwnerBudgets.Default.Connect,
            Header = phase == "header" ? shortBudget : OwnerBudgets.Default.Header,
            Payload = phase == "payload" ? shortBudget : OwnerBudgets.Default.Payload,
            Exchange = phase == "total" ? shortBudget : OwnerBudgets.Default.Exchange
        };
    }

    [Fact]
    public void Production_budgets_cannot_be_weakened()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OwnerTransport(new OwnerPipeConnectionFactory(),
                OwnerBudgets.Default with { Connect = TimeSpan.FromSeconds(3) }));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OwnerTransport(new OwnerPipeConnectionFactory(),
                OwnerBudgets.Default with { Payload = TimeSpan.Zero }));
    }
}
