using System.Buffers.Binary;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Servicing;

[SupportedOSPlatform("windows")]
public sealed class WindowsRuntimeGate : IRuntimeGate
{
    private readonly string _path;
    private readonly StartupRefusalEvidenceCapture? _evidence;

    public WindowsRuntimeGate(string path) : this(path, null) { }

    internal WindowsRuntimeGate(string path, StartupRefusalEvidenceCapture? evidence)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows file sharing is required.");
        (_path, _evidence) = (path, evidence);
    }

    public GateOpen TryOpen()
    {
        try
        {
            return new GateOpened(new FileReadLease(new FileStream(
                _path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.RandomAccess)));
        }
        catch (IOException error)
        { return RuntimeGateFailures.From(error, _evidence); }
        catch (UnauthorizedAccessException error)
        { return RuntimeGateFailures.From(error, _evidence); }
    }

    private sealed class FileReadLease(FileStream stream) : IRuntimeReadLease
    {
        public GateReadyState ReadReady() => GateCodec.ReadReady(stream);
        public void Dispose() => stream.Dispose();
    }
}

// Trusted coordinator primitive, not a completion/recovery decision.
// Its caller must hold the validated operation and completed-outcome evidence.
[SupportedOSPlatform("windows")]
internal sealed class ExclusiveGate : IDisposable
{
    private readonly FileStream _stream;
    private ExclusiveGate(FileStream stream) => _stream = stream;

    internal static ExclusiveGate Open(string path, bool create = false)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows file sharing is required.");
        return new(new FileStream(path,
            create ? FileMode.OpenOrCreate : FileMode.Open,
            FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.RandomAccess));
    }

    internal void CloseAdmission() => GateCodec.Close(_stream);
    internal void PublishReady(GateReadyState state) => GateCodec.PublishReady(_stream, state);
    public void Dispose() => _stream.Dispose();
}

internal enum GateStatus : byte { Closed = 0x43, Ready = 0x52 }

internal static class GateCodec
{
    internal const int HeaderSize = 16;
    internal const int MaximumBodyLength = 1_048_576;
    private const int StatusOffset = 9;
    private static ReadOnlySpan<byte> Magic => "HRMSA2\r\n"u8;

    internal static GateReadyState ReadReady(Stream stream)
    {
        try { return ReadState(stream); }
        catch (JsonException)
        { throw new AdmissionException(RefusalReason.InvalidState); }
        catch (EndOfStreamException)
        { throw new AdmissionException(RefusalReason.InvalidState); }
    }

    private static GateReadyState ReadState(Stream stream)
    {
        stream.Position = 0;
        var header = new byte[HeaderSize];
        stream.ReadExactly(header);
        RequireHeader(header);
        if (header[StatusOffset] == (byte)GateStatus.Closed)
            throw new AdmissionException(RefusalReason.Closed);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
        if (header[StatusOffset] != (byte)GateStatus.Ready ||
            length is 0 or > MaximumBodyLength || stream.Length != HeaderSize + (long)length)
            throw new AdmissionException(RefusalReason.InvalidState);
        var body = new byte[(int)length];
        stream.ReadExactly(body);
        return DecodeReady(body);
    }

    private static GateReadyState DecodeReady(byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        RequireUniqueProperties(document.RootElement);
        var state = document.RootElement.Deserialize(ServicingJson.Default.GateReadyState)
            ?? throw new AdmissionException(RefusalReason.InvalidState);
        if (state.Installation is { } installation)
            ReadyValidation.Require(installation);
        return state;
    }

    private static void RequireUniqueProperties(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = value.EnumerateObject().ToArray();
                if (properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != properties.Length)
                    throw new AdmissionException(RefusalReason.InvalidState);
                foreach (var property in properties)
                    RequireUniqueProperties(property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    RequireUniqueProperties(item);
                break;
        }
    }

    internal static void Close(FileStream stream)
    {
        stream.Position = 0;
        stream.Write(Header(GateStatus.Closed, 0));
        stream.Flush(flushToDisk: true);
    }

    internal static void PublishReady(FileStream stream, GateReadyState state)
    {
        if (state.Installation is { } installation)
            ReadyValidation.Require(installation);
        var body = JsonSerializer.SerializeToUtf8Bytes(state, ServicingJson.Default.GateReadyState);
        if (body.Length > MaximumBodyLength)
            throw new AdmissionException(RefusalReason.InvalidState,
                invariant: AdmissionInvariant.ReadyBodyTooLarge);
        Close(stream);
        stream.Position = HeaderSize;
        stream.Write(body);
        stream.SetLength(HeaderSize + body.Length);
        stream.Position = 0;
        stream.Write(Header(GateStatus.Closed, body.Length));
        stream.Flush(flushToDisk: true);
        stream.Position = StatusOffset;
        stream.WriteByte((byte)GateStatus.Ready);
        stream.Flush(flushToDisk: true);
    }

    internal static byte[] Header(GateStatus status, int length)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        header[8] = HermesServicingProtocol.AdmissionAbi;
        header[StatusOffset] = (byte)status;
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), checked((uint)length));
        return header;
    }

    private static void RequireHeader(byte[] header)
    {
        if (!header.AsSpan(0, 8).SequenceEqual(Magic) || header[10] != 0 || header[11] != 0)
            throw new AdmissionException(RefusalReason.InvalidState);
        if (header[8] != HermesServicingProtocol.AdmissionAbi)
            throw new AdmissionException(RefusalReason.UnsupportedProtocol);
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(GateReadyState))]
[JsonSerializable(typeof(SetupReport))]
internal partial class ServicingJson : JsonSerializerContext;

// Metadata-only probes. They neither create paths nor supply assembly MVIDs.
[SupportedOSPlatform("windows")]
internal sealed class WindowsPhysicalPaths : IPhysicalPaths
{
    private const int MaximumAncestorDepth = 64;
    private const int DirectoryOffset = 21;
    private const uint OpenReparsePoint = 0x00200000;
    private readonly IWindowsPathApi _api;

    public WindowsPhysicalPaths() : this(new WindowsPathApi()) { }

    internal WindowsPhysicalPaths(IWindowsPathApi api) => _api = api;

    public string Directory(string path) => ReadDirectory(RuntimePaths.Canonical(path), 0);

    public string Image(string path)
    {
        var opened = _api.Open(RuntimePaths.Canonical(path), 0);
        using var handle = opened.Handle;
        var probe = new Probe(StartupEvidenceStage.LoadedImage, 0);
        if (handle.IsInvalid)
            throw Failure(probe, StartupEvidenceOperation.OpenFollowing,
                StartupEvidenceFailure.NativeCallFailed, opened.Error);
        var information = Information(handle, probe);
        if (information[DirectoryOffset] != 0)
            throw new AdmissionException(RefusalReason.InvalidState,
                invariant: AdmissionInvariant.ImageIsDirectory);
        if (BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(16)) != 1)
            throw new AdmissionException(RefusalReason.InvalidState,
                invariant: AdmissionInvariant.ImageLinkCountNotOne);
        return FinalPath(handle, probe);
    }

    private string ReadDirectory(string path, int depth)
    {
        if (depth > MaximumAncestorDepth)
            throw new AdmissionException(RefusalReason.InvalidState,
                invariant: AdmissionInvariant.AncestorDepthExceeded);
        var opened = _api.Open(path, 0);
        using var handle = opened.Handle;
        if (handle.IsInvalid)
            return ReadMissingDirectory(path, depth, opened.Error);
        var probe = new Probe(StartupEvidenceStage.ReservedRoot, depth);
        if (Information(handle, probe)[DirectoryOffset] == 0)
            throw new AdmissionException(RefusalReason.InvalidState,
                invariant: AdmissionInvariant.DirectoryIsNotDirectory);
        return FinalPath(handle, probe);
    }

    private string ReadMissingDirectory(string path, int depth, int error)
    {
        var probe = new Probe(StartupEvidenceStage.ReservedRoot, depth);
        if (error is not (2 or 3))
            throw Failure(probe, StartupEvidenceOperation.OpenFollowing,
                StartupEvidenceFailure.NativeCallFailed, error);
        if (Path.GetDirectoryName(path) is not { } parent)
            throw Failure(probe, StartupEvidenceOperation.OpenFollowing,
                StartupEvidenceFailure.NoParent, error);
        RequireMissing(path, probe);
        return RuntimePaths.Canonical(Path.Combine(
            ReadDirectory(parent, depth + 1), Path.GetFileName(path)));
    }

    private void RequireMissing(string path, Probe probe)
    {
        // A following open can report absence for an existing dangling reparse point.
        var opened = _api.Open(path, OpenReparsePoint);
        using var entry = opened.Handle;
        if (!entry.IsInvalid)
            throw Failure(probe, StartupEvidenceOperation.OpenEntry,
                StartupEvidenceFailure.ExistingEntry);
        if (opened.Error is not (2 or 3))
            throw Failure(probe, StartupEvidenceOperation.OpenEntry,
                StartupEvidenceFailure.NativeCallFailed, opened.Error);
    }

    private byte[] Information(SafeFileHandle handle, Probe probe)
    {
        // FILE_STANDARD_INFO: two Int64s, UInt32 link count, two BOOLEANs, padding.
        var result = _api.Information(handle);
        if (!result.Succeeded)
            throw Failure(probe, StartupEvidenceOperation.StandardInformation,
                StartupEvidenceFailure.NativeCallFailed, result.Error);
        if (result.Information[20] != 0)
            throw Failure(probe, StartupEvidenceOperation.StandardInformation,
                StartupEvidenceFailure.DeletePending);
        return result.Information;
    }

    private string FinalPath(SafeFileHandle handle, Probe probe)
    {
        var result = _api.FinalPath(handle);
        if (result.Length == 0)
            throw Failure(probe, StartupEvidenceOperation.FinalPath,
                StartupEvidenceFailure.NativeCallFailed, result.Error);
        if (result.Length >= WindowsPathApi.MaximumPathCharacters)
            throw Failure(probe, StartupEvidenceOperation.FinalPath,
                StartupEvidenceFailure.InvalidReturnedLength);
        var ordinary = result.Path switch
        {
            var unc when unc.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) =>
                @"\\" + unc[8..],
            var dos when dos.StartsWith(@"\\?\", StringComparison.Ordinal) => dos[4..],
            _ => throw new AdmissionException(RefusalReason.InvalidState,
                invariant: AdmissionInvariant.FinalPathPrefixUnsupported)
        };
        return RuntimePaths.Canonical(ordinary);
    }

    private static AdmissionException Failure(
        Probe probe, StartupEvidenceOperation operation,
        StartupEvidenceFailure failure, int? nativeError = null) =>
        new(RefusalReason.Unavailable, new(
            probe.Stage, operation, failure,
            nativeError is null ? StartupEvidenceErrorKind.None : StartupEvidenceErrorKind.Win32,
            nativeError ?? 0, probe.Position));

    private readonly record struct Probe(StartupEvidenceStage Stage, int Position);
}
