namespace Hermes.Servicing.Acceptance;

public enum CaseState { Passed, Failed, Blocked }

public static class ReportContract
{
    public static int ExitCode(IReadOnlyList<CaseState> states) => states switch
    {
        [] => 1,
        _ when states.Contains(CaseState.Failed) => 1,
        _ when states.Contains(CaseState.Blocked) => 3,
        _ => 0
    };
}

internal sealed record CaseReport(
    string Name,
    string Scope,
    CaseState State,
    ImmutableArray<object> Evidence,
    string? MissingEvidence = null);

internal sealed record AcceptanceReport(
    int SchemaVersion,
    string Kind,
    bool Passed,
    bool Complete,
    string Scope,
    FixtureJob Job,
    Guid OwnerFixtureId,
    ImmutableArray<Asset> Inputs,
    ImmutableArray<FileEvidence> OwnerUnknownBefore,
    ImmutableArray<FileEvidence> OwnerUnknownAfter,
    ImmutableArray<CaseReport> Cases,
    CleanupReport Cleanup,
    string? Failure);

internal sealed record CleanupReport(
    bool Attempted,
    bool Completed,
    string FinalAdmission,
    ImmutableArray<object> Evidence,
    string? Failure);

internal sealed record Asset(string Path, string Sha256);

internal sealed record FileEvidence(
    string Path,
    bool Exists,
    long? Length,
    string? Sha256);

internal sealed record Stamp(
    int ProcessId,
    long StartUtcFileTime,
    string ExecutablePath,
    string ExecutableSha256);

internal sealed record GateEvidence(
    string Path,
    string FileIdentity,
    long Length,
    string HeaderHex,
    string HeaderState);

internal sealed record MediaEvidence(
    Guid ProductCode,
    string PackagePath,
    string PackageSha256,
    string ManifestPath,
    string ManifestSha256);

internal sealed record JournalMark(
    Guid OperationId,
    Guid ActiveRunId,
    ImmutableArray<Guid> RevokedRunIds,
    ImmutableArray<MediaEvidence> Media);

internal static class EvidenceJson
{
    internal const int MaximumReportBytes = 32 * 1024 * 1024;
    internal const int MaximumEventCharacters = 2 * 1024 * 1024;

    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        options.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
        return options;
    }

    internal static string Encode<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    internal static T Decode<T>(string text) =>
        JsonSerializer.Deserialize<T>(text, Options)
        ?? throw new InvalidDataException("Missing diagnostic value.");

    internal static T Read<T>(string path, int maximum = MaximumReportBytes)
    {
        using var stream = File.OpenRead(path);
        Require.That(stream.Length is > 0 && stream.Length <= maximum);
        using var reader = new StreamReader(stream);
        return Decode<T>(reader.ReadToEnd());
    }

    internal static void WriteNew<T>(string path, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        Require.That(bytes.Length <= MaximumReportBytes);
        using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}

internal static class Require
{
    internal static void That(bool condition)
    {
        if (!condition)
            throw new InvalidDataException("Acceptance assertion failed.");
    }
}

internal static class Files
{
    internal static Asset Bind(string path) =>
        new(Path.GetFullPath(path), Hash(path));

    internal static string Hash(string path)
    {
        OwnerFixtureStore.RequireRegular(File.GetAttributes(path));
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    internal static FileEvidence Capture(string path)
    {
        if (!File.Exists(path))
            return new(path, false, null, null);
        return new(path, true, new FileInfo(path).Length, Hash(path));
    }

    internal static Guid Mvid(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }

    internal static Stamp Identity(Process process)
    {
        var image = process.MainModule?.FileName
            ?? throw new InvalidDataException("Process image is unavailable.");
        return new(process.Id, process.StartTime.ToUniversalTime().ToFileTimeUtc(),
            Path.GetFullPath(image), Hash(image));
    }

    internal static Stamp Current()
    {
        using var process = Process.GetCurrentProcess();
        return Identity(process);
    }

    internal static void RequireSame(
        ImmutableArray<FileEvidence> before, ImmutableArray<FileEvidence> after) =>
        Require.That(before.SequenceEqual(after));
}

internal static class GateReader
{
    internal static GateEvidence Read(string path)
    {
        using var stream = new FileStream(path, FileMode.Open,
            FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        return Read(path, stream.SafeFileHandle);
    }

    internal static GateEvidence Read(string path, SafeFileHandle handle)
    {
        var header = new byte[GateCodec.HeaderSize];
        var count = RandomAccess.Read(handle, header, 0);
        return new(path, Identity(handle), RandomAccess.GetLength(handle),
            Convert.ToHexStringLower(header.AsSpan(0, count)), State(header, count));
    }

    private static string State(byte[] header, int count) => count switch
    {
        GateCodec.HeaderSize when header.SequenceEqual(GateCodec.Header(GateStatus.Closed, 0))
            => "Closed",
        GateCodec.HeaderSize when header.AsSpan(0, 8).SequenceEqual("HRMSA2\r\n"u8) &&
            header[8] == HermesServicingProtocol.AdmissionAbi &&
            header[9] == (byte)GateStatus.Ready => "ReadyHeader",
        _ => "Invalid"
    };

    private static string Identity(SafeFileHandle handle)
    {
        var bytes = new byte[24]; // FILE_ID_INFO: volume serial + FILE_ID_128.
        if (!GetFileInformationByHandleEx(handle, 18, bytes, (uint)bytes.Length))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return Convert.ToHexStringLower(bytes);
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle handle, int informationClass,
        [Out] byte[] information, uint length);
}
