using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hermes.Tray.Access;

namespace Hermes.Tray.Tests;

internal static class AccessFixture
{
    public const string Id = "AAAAAAAAAAAAAAAAAAAAAA";
    public const string OtherId = "AQEBAQEBAQEBAQEBAQEBAQ";
    public const string Challenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public const string Code = "0123-4567-89AB";
    public static DateTimeOffset Now { get; } = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
    public static OwnerTarget Target() =>
        OwnerTarget.Create(Path.Combine(Path.GetTempPath(), "hermes-access-synthetic"), 31991);
    public static OwnerProcessIdentity Process { get; } = new(4123, Now.AddMinutes(-1));
    public static OwnerInstance Instance(OwnerTarget target) =>
        new(Id, Process.ProcessId, Process.ProcessStartTimeUtc, target.ApiOrigin, target.ConfigKey);
    public static OwnerPairing Pairing(OwnerTarget target) =>
        new(OtherId, Code, Challenge, target.ApiOrigin, target.ApiOrigin,
            AccessCapabilities.Ui, Now, Now.AddSeconds(180), PairingState.Pending);
    public static OwnerApproval Approval(OwnerTarget target) =>
        new(OtherId, AccessCapabilities.Ui, Pairing(target).ExpiresAt.AddSeconds(-120));

    public static byte[] Reply(OwnerTarget target, string requestId, object result) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, requestId,
            instance = new
            {
                instanceId = Id, processId = Process.ProcessId,
                processStartTimeUtc = Process.ProcessStartTimeUtc,
                origin = target.ApiOrigin, configKey = target.ConfigKey
            },
            ok = true, result, error = (object?)null
        });

    public static byte[] StatusReply(OwnerTarget target, string requestId) =>
        Reply(target, requestId, new
        {
            phase = "ready", capabilities = new[] { "archive.read", "config.read", "model.invoke", "mutate" }
        });

    public static byte[] Frame(byte[] payload)
    {
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame, 4);
        return frame;
    }

    public static byte[] PairingNotice(AccessReady ready, OwnerPairing? pairing = null) =>
        JsonSerializer.SerializeToUtf8Bytes(new
    {
        version = 1, type = "hermes.access.pairing", navigationId = ready.NavigationId,
        instanceId = ready.InstanceId, pairingId = pairing?.PairingId ?? OtherId,
        userCode = pairing?.UserCode ?? Code, challenge = pairing?.Challenge ?? Challenge
    });

    public static byte[] StatusNotice(AccessReady ready, DateTimeOffset expiresAt) =>
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = 1, type = "hermes.access.status", navigationId = ready.NavigationId,
            instanceId = ready.InstanceId, state = "authenticated",
            capabilities = new[] { "archive.read", "config.read", "model.invoke", "mutate" }, expiresAt
        });
}

internal sealed class AccessTestClock : TimeProvider
{
    private DateTimeOffset _utc = AccessFixture.Now;
    private long _timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => _utc;
    public override long GetTimestamp() => _timestamp;
    public void Advance(TimeSpan elapsed) => (_utc, _timestamp) = (_utc + elapsed, _timestamp + elapsed.Ticks);
    public void RollBackUtc(TimeSpan elapsed) => _utc -= elapsed;
}

internal sealed class AccessTestProcess(OwnerProcessIdentity identity) : IAccessProcessHandle
{
    public OwnerProcessIdentity Identity { get; } = identity;
    public bool Alive { get; set; } = true;
    public bool IsAlive => Alive;
}

internal sealed class AccessTestEnvironment(AccessEnvironmentSnapshot snapshot) : IAccessEnvironment
{
    private AccessEnvironmentSnapshot _snapshot = snapshot;
    public AccessEnvironmentSnapshot Read() => _snapshot;
    public void Change(Func<AccessEnvironmentSnapshot, AccessEnvironmentSnapshot> change) =>
        _snapshot = change(_snapshot);
}

internal sealed class AccessTestOwner(OwnerTarget target) : IOwnerControlClient
{
    public int StatusCalls { get; private set; }
    public int ResolveCalls { get; private set; }
    public int ApproveCalls { get; private set; }
    public Func<CancellationToken, Task<OwnerResult<OwnerStatus>>> Status { get; set; } =
        _ => Task.FromResult<OwnerResult<OwnerStatus>>(new OwnerResult<OwnerStatus>.Success(
            new(AccessFixture.Instance(target), OwnerPhase.Ready, AccessCapabilities.EffectiveUi)));
    public Func<CancellationToken, Task<OwnerResult<OwnerPairing>>> Resolve { get; set; } =
        _ => Task.FromResult<OwnerResult<OwnerPairing>>(
            new OwnerResult<OwnerPairing>.Success(AccessFixture.Pairing(target)));
    public Func<CancellationToken, Task<OwnerResult<OwnerApproval>>> Approve { get; set; } =
        _ => Task.FromResult<OwnerResult<OwnerApproval>>(
            new OwnerResult<OwnerApproval>.Success(AccessFixture.Approval(target)));

    public Task<OwnerResult<OwnerStatus>> GetStatusAsync(
        OwnerProcessIdentity owned, string? expectedInstanceId, CancellationToken cancellationToken)
    {
        StatusCalls++;
        return Status(cancellationToken);
    }

    public Task<OwnerResult<OwnerPairing>> ResolveAsync(
        OwnerInstance pinned, string userCode, CancellationToken cancellationToken)
    {
        Xunit.Assert.Equal(AccessFixture.Id, pinned.InstanceId);
        Xunit.Assert.Equal(AccessFixture.Code, userCode);
        ResolveCalls++;
        return Resolve(cancellationToken);
    }

    public Task<OwnerResult<OwnerApproval>> ApproveAsync(
        OwnerInstance pinned, OwnerPairing pairing,
        ImmutableArray<AccessCapability> capabilities, CancellationToken cancellationToken)
    {
        Xunit.Assert.Equal(AccessFixture.Id, pinned.InstanceId);
        Xunit.Assert.Equal(AccessFixture.OtherId, pairing.PairingId);
        Xunit.Assert.True(AccessCapabilities.Equal(AccessCapabilities.Ui, capabilities));
        ApproveCalls++;
        return Approve(cancellationToken);
    }
}

internal sealed class AccessPendingApprovalOwner(OwnerTarget target) : IOwnerControlClient
{
    private readonly OwnerInstance _instance = AccessFixture.Instance(target);
    private readonly TaskCompletionSource<OwnerResult<OwnerApproval>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    public OwnerPairing Pairing { get; } =
        AccessFixture.Pairing(target) with { ExpiresAt = AccessFixture.Now.AddSeconds(2) };
    public OwnerPairing NextPairing { get; } =
        AccessFixture.Pairing(target) with { PairingId = AccessFixture.Id };
    public CancellationToken ApprovalToken { get; private set; }
    public int ResolveCalls { get; private set; }
    public int ApproveCalls { get; private set; }

    public Task<OwnerResult<OwnerStatus>> GetStatusAsync(
        OwnerProcessIdentity owned, string? expectedInstanceId, CancellationToken cancellationToken)
    {
        Xunit.Assert.Equal(_instance.Process, owned);
        return Task.FromResult<OwnerResult<OwnerStatus>>(new OwnerResult<OwnerStatus>.Success(
            new(_instance, OwnerPhase.Ready, AccessCapabilities.EffectiveUi)));
    }

    public Task<OwnerResult<OwnerPairing>> ResolveAsync(
        OwnerInstance pinned, string userCode, CancellationToken cancellationToken)
    {
        Xunit.Assert.Equal(_instance, pinned);
        Xunit.Assert.Equal(AccessFixture.Code, userCode);
        var pairing = ResolveCalls++ == 0 ? Pairing : NextPairing;
        return Task.FromResult<OwnerResult<OwnerPairing>>(new OwnerResult<OwnerPairing>.Success(pairing));
    }

    public Task<OwnerResult<OwnerApproval>> ApproveAsync(
        OwnerInstance pinned, OwnerPairing pairing,
        ImmutableArray<AccessCapability> capabilities, CancellationToken cancellationToken)
    {
        Xunit.Assert.Equal(_instance, pinned);
        Xunit.Assert.Equal(ApproveCalls == 0 ? Pairing : NextPairing, pairing);
        Xunit.Assert.True(AccessCapabilities.Equal(AccessCapabilities.Ui, capabilities));
        if (++ApproveCalls == 1)
        {
            ApprovalToken = cancellationToken;
            return _completion.Task;
        }
        return Task.FromResult<OwnerResult<OwnerApproval>>(new OwnerResult<OwnerApproval>.Success(
            new(pairing.PairingId, capabilities, AccessFixture.Now.AddSeconds(60))));
    }

    public void Complete() => _completion.SetResult(new OwnerResult<OwnerApproval>.Success(
        new(Pairing.PairingId, AccessCapabilities.Ui, Pairing.ExpiresAt)));
}

internal sealed class AccessScriptStream(byte[] input, int chunk = 1, bool stallAtEnd = false) : Stream
{
    private readonly MemoryStream _input = new(input);
    private readonly MemoryStream _output = new();
    public bool Disposed { get; private set; }
    public int Remaining => checked((int)(_input.Length - _input.Position));
    public byte[] Written => _output.ToArray();
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_input.Position == _input.Length && stallAtEnd)
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        return _input.Read(buffer.Span[..Math.Min(buffer.Length, chunk)]);
    }

    public override ValueTask WriteAsync(
        ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

internal sealed class AccessTestConnection(AccessScriptStream stream, bool stallConnect = false)
    : IOwnerConnection
{
    public Stream Stream => stream;
    public Task ConnectAsync(CancellationToken cancellationToken) => stallConnect
        ? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken) : Task.CompletedTask;
    public void Dispose() => stream.Dispose();
}

internal sealed class AccessTestConnections(IOwnerConnection connection) : IOwnerConnectionFactory
{
    public int Created { get; private set; }
    public IOwnerConnection Create(OwnerTarget target)
    {
        Created++;
        return connection;
    }
}
