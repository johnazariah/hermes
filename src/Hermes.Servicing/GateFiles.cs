using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Servicing;

[SupportedOSPlatform("windows")]
public sealed class WindowsRuntimeGate : IRuntimeGate
{
    private readonly string _path;

    public WindowsRuntimeGate(string path)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows file sharing is required.");
        _path = path;
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
        {
            return new GateUnavailable((error.HResult & 0xffff) switch
            {
                32 or 33 => RefusalReason.Busy,
                2 or 3 => RefusalReason.UnownedInstallation,
                _ => RefusalReason.Unavailable
            });
        }
        catch (UnauthorizedAccessException)
        { return new GateUnavailable(RefusalReason.Unavailable); }
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
            throw new AdmissionException(RefusalReason.InvalidState);
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
    private const string Kernel32 = "kernel32.dll";
    private const int MaximumPathCharacters = 32_768;
    private const int MaximumAncestorDepth = 64;
    private const int DirectoryOffset = 21;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;

    public string Directory(string path) => ReadDirectory(RuntimePaths.Canonical(path), 0);

    public string Image(string path)
    {
        using var handle = Open(RuntimePaths.Canonical(path));
        if (handle.IsInvalid)
            throw new AdmissionException(RefusalReason.Unavailable);
        var information = Information(handle);
        if (information[DirectoryOffset] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(16)) != 1)
            throw new AdmissionException(RefusalReason.InvalidState);
        return FinalPath(handle);
    }

    private static string ReadDirectory(string path, int depth)
    {
        if (depth > MaximumAncestorDepth)
            throw new AdmissionException(RefusalReason.InvalidState);
        using var handle = Open(path);
        if (!handle.IsInvalid)
        {
            if (Information(handle)[DirectoryOffset] == 0)
                throw new AdmissionException(RefusalReason.InvalidState);
            return FinalPath(handle);
        }
        var error = Marshal.GetLastWin32Error();
        if (error is not (2 or 3) || Path.GetDirectoryName(path) is not { } parent)
            throw new AdmissionException(RefusalReason.Unavailable);
        RequireMissing(path);
        return RuntimePaths.Canonical(Path.Combine(
            ReadDirectory(parent, depth + 1), Path.GetFileName(path)));
    }

    private static void RequireMissing(string path)
    {
        // A following open can report absence for an existing dangling reparse point.
        using var entry = Open(path, OpenReparsePoint);
        if (!entry.IsInvalid || Marshal.GetLastWin32Error() is not (2 or 3))
            throw new AdmissionException(RefusalReason.Unavailable);
    }

    private static SafeFileHandle Open(string path, uint flags = 0) =>
        OpenPath(path, 0, FileShare.ReadWrite | FileShare.Delete,
            0, FileMode.Open, BackupSemantics | flags, 0);

    private static byte[] Information(SafeFileHandle handle)
    {
        // FILE_STANDARD_INFO: two Int64s, UInt32 link count, two BOOLEANs, padding.
        var information = new byte[24];
        if (!ReadInformation(handle, 1, information, (uint)information.Length) ||
            information[20] != 0)
            throw new AdmissionException(RefusalReason.Unavailable);
        return information;
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(MaximumPathCharacters);
        var length = ReadFinalPath(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= (uint)buffer.Capacity)
            throw new AdmissionException(RefusalReason.Unavailable);
        var ordinary = buffer.ToString() switch
        {
            var unc when unc.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) =>
                @"\\" + unc[8..],
            var dos when dos.StartsWith(@"\\?\", StringComparison.Ordinal) => dos[4..],
            _ => throw new AdmissionException(RefusalReason.InvalidState)
        };
        return RuntimePaths.Canonical(ordinary);
    }

    [DllImport(Kernel32, EntryPoint = "CreateFileW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenPath(
        string path, uint desiredAccess, FileShare shareMode, nint securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx",
        ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadInformation(
        SafeFileHandle handle, int informationClass, [Out] byte[] information, uint length);

    [DllImport(Kernel32, EntryPoint = "GetFinalPathNameByHandleW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ReadFinalPath(
        SafeFileHandle handle, [Out] StringBuilder path, uint length, uint flags);
}
