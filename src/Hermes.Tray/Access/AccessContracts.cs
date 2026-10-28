using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hermes.Tray.Access;

internal enum AccessCapability { ArchiveRead, ConfigRead, ModelInvoke, Mutate }
internal enum OwnerPhase { Starting, Ready, Stopping }
internal enum PairingState { Pending, Approved }
internal enum OwnerFailure
{
    InvalidRequest, UnsupportedVersion, WrongInstance, InstanceNotReady,
    PairingNotFound, PairingExpired, PairingConsumed, PairingConflict,
    InsufficientCapability, AccessCapacityReached, InternalError,
    Unavailable, Timeout, Protocol, IdentityMismatch
}

internal abstract record OwnerResult<T>
{
    public sealed record Success(T Value) : OwnerResult<T>;
    public sealed record Failure(OwnerFailure Code) : OwnerResult<T>;
}

internal sealed record OwnerProcessIdentity(int ProcessId, DateTimeOffset ProcessStartTimeUtc);
internal sealed record OwnerInstance(
    string InstanceId, int ProcessId, DateTimeOffset ProcessStartTimeUtc,
    string Origin, string ConfigKey)
{
    public OwnerProcessIdentity Process => new(ProcessId, ProcessStartTimeUtc);
}

internal sealed record OwnerStatus(
    OwnerInstance Instance, OwnerPhase Phase, ImmutableArray<AccessCapability> Capabilities);
internal sealed record OwnerPairing(
    string PairingId, string UserCode, string Challenge, string BrowserOrigin,
    string ApiOrigin, ImmutableArray<AccessCapability> RequestedCapabilities,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, PairingState State)
{
    public override string ToString() => "OwnerPairing { Challenge = [redacted] }";
}
internal sealed record OwnerApproval(
    string PairingId, ImmutableArray<AccessCapability> Capabilities, DateTimeOffset ExpiresAt);

internal interface IOwnerControlClient
{
    Task<OwnerResult<OwnerStatus>> GetStatusAsync(
        OwnerProcessIdentity owned, string? expectedInstanceId, CancellationToken cancellationToken);
    Task<OwnerResult<OwnerPairing>> ResolveAsync(
        OwnerInstance pinned, string userCode, CancellationToken cancellationToken);
    Task<OwnerResult<OwnerApproval>> ApproveAsync(
        OwnerInstance pinned, OwnerPairing pairing,
        ImmutableArray<AccessCapability> capabilities, CancellationToken cancellationToken);
}

internal sealed record OwnerTarget
{
    public string CanonicalDirectory { get; }
    public int Port { get; }
    public string ConfigKey { get; }
    public string PipeName => "hmop1-" + ConfigKey[..32];
    public string ApiOrigin => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}";

    private OwnerTarget(string directory, int port) =>
        (CanonicalDirectory, Port, ConfigKey) =
            (directory, port, OwnerNaming.HashCanonicalDirectory(directory, port));

    public static OwnerTarget Create(string directory, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        OwnerNaming.RequirePort(port);
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("An absolute configuration directory is required.", nameof(directory));
        var full = Path.GetFullPath(directory);
        while (!Path.EndsInDirectorySeparator(Path.GetPathRoot(full) ?? "") &&
               Path.EndsInDirectorySeparator(full))
            full = Path.TrimEndingDirectorySeparator(full);
        full = TrimSeparators(full);
        return new(OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full, port);
    }

    private static string TrimSeparators(string full)
    {
        var rootLength = (Path.GetPathRoot(full) ?? "").Length;
        while (full.Length > rootLength && Path.EndsInDirectorySeparator(full))
            full = Path.TrimEndingDirectorySeparator(full);
        return full;
    }
}

internal static class OwnerNaming
{
    // Pure hashing boundary: does not claim to normalize a Windows path on Unix.
    public static string HashCanonicalDirectory(string canonicalDirectory, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalDirectory);
        RequirePort(port);
        var input = $"hermes-owner-control-v1\n{canonicalDirectory}\n" +
                    $"{port.ToString(CultureInfo.InvariantCulture)}\n";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input))).ToLowerInvariant();
    }

    public static void RequirePort(int port)
    {
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
    }
}

internal static class AccessCapabilities
{
    public static ImmutableArray<AccessCapability> Ui { get; } =
        [AccessCapability.ConfigRead, AccessCapability.ModelInvoke, AccessCapability.Mutate];
    public static ImmutableArray<AccessCapability> EffectiveUi { get; } =
        [AccessCapability.ArchiveRead, .. Ui];

    public static string Name(AccessCapability capability) => capability switch
    {
        AccessCapability.ArchiveRead => "archive.read",
        AccessCapability.ConfigRead => "config.read",
        AccessCapability.ModelInvoke => "model.invoke",
        AccessCapability.Mutate => "mutate",
        _ => throw new AccessProtocolFault(OwnerFailure.Protocol)
    };

    public static AccessCapability Parse(string value) => value switch
    {
        "archive.read" => AccessCapability.ArchiveRead,
        "config.read" => AccessCapability.ConfigRead,
        "model.invoke" => AccessCapability.ModelInvoke,
        "mutate" => AccessCapability.Mutate,
        _ => throw new AccessProtocolFault(OwnerFailure.Protocol)
    };

    public static bool Equal(
        ImmutableArray<AccessCapability> left, ImmutableArray<AccessCapability> right) =>
        !left.IsDefault && !right.IsDefault && left.SequenceEqual(right);
}

internal sealed class AccessProtocolFault(OwnerFailure failure) : Exception("Owner control failed.")
{
    public OwnerFailure Failure { get; } = failure;
}

internal interface IAccessProcessHandle
{
    OwnerProcessIdentity Identity { get; }
    bool IsAlive { get; }
}

// Borrowed launched Process, not Process.GetProcessById. The supervisor retains ownership.
internal sealed class LaunchedAccessProcess : IAccessProcessHandle
{
    private readonly Process _process;
    public OwnerProcessIdentity Identity { get; }

    public LaunchedAccessProcess(Process launched)
    {
        ArgumentNullException.ThrowIfNull(launched);
        if (launched.SafeHandle.IsInvalid || launched.HasExited)
            throw new InvalidOperationException("The launched process is not live.");
        _process = launched;
        Identity = new(launched.Id, new DateTimeOffset(launched.StartTime.ToUniversalTime()));
    }

    public bool IsAlive
    {
        get
        {
            try { return !_process.HasExited && !_process.SafeHandle.IsClosed; }
            catch (Exception error) when (error is InvalidOperationException or
                System.ComponentModel.Win32Exception or ObjectDisposedException) { return false; }
        }
    }
}
