using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Hermes.Servicing;

namespace Hermes.Setup;

internal enum DiskEntryKind { Missing, File, Directory }

internal sealed record DiskEntry(DiskEntryKind Kind, bool ReparsePoint);

internal interface ISetupFileSystem
{
    DiskEntry Entry(string path);
    IReadOnlyList<string> Children(string directory);
    void CreateDirectory(string path);
    FileStream Open(string path, FileMode mode, FileAccess access, FileShare share);
    void Flush(FileStream stream);
    void DeleteFile(string path);
}

internal interface ISetupPathSafety
{
    void RequireDirectory(string path);
    void RequireFile(string path);
}

internal interface IAtomicFileCommit
{
    void Commit(string temporary, string destination, bool overwrite);
}

internal sealed class SetupIoException(
    SetupCode code, SetupRefusalEvidence? evidence = null) : Exception(code.ToString())
{
    internal SetupCode Code { get; } = code;
    internal SetupRefusalEvidence? Evidence { get; } = evidence;
}

internal static class SetupIo
{
    internal static void Require(
        bool condition, SetupCode code, SetupRefusalEvidence? evidence = null)
    {
        if (!condition)
            throw new SetupIoException(code, evidence);
    }

    internal static Task<Effect<T>> RunAsync<T>(
        Func<T> action, SetupCode failure, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Effect<T>.Ok(action()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Task.FromResult(Effect<T>.Fail(SetupCode.Cancelled)); }
        catch (SetupIoException error)
        { return Task.FromResult(Effect<T>.Fail(error.Code, error.Evidence)); }
        catch (Exception error) when (Expected(error))
        { return Task.FromResult(Effect<T>.Fail(failure)); }
    }

    internal static bool Expected(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException or FormatException
            or OverflowException or BadImageFormatException or Win32Exception;
}

internal sealed class PhysicalSetupFileSystem : ISetupFileSystem
{
    public DiskEntry Entry(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return new(attributes.HasFlag(FileAttributes.Directory)
                ? DiskEntryKind.Directory : DiskEntryKind.File,
                attributes.HasFlag(FileAttributes.ReparsePoint));
        }
        catch (FileNotFoundException) { return new(DiskEntryKind.Missing, false); }
        catch (DirectoryNotFoundException) { return new(DiskEntryKind.Missing, false); }
    }

    public IReadOnlyList<string> Children(string directory) =>
        Directory.GetFileSystemEntries(directory);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
        new(path, mode, access, share, 65_536, FileOptions.None);

    public void Flush(FileStream stream) => stream.Flush(flushToDisk: true);
    public void DeleteFile(string path) => File.Delete(path);
}

internal sealed class SetupDisk(
    ISetupFileSystem files,
    ISetupPathSafety paths,
    IAtomicFileCommit commits)
{
    internal ISetupFileSystem Files => files;
    internal ISetupPathSafety Paths => paths;

    internal void EnsureDirectory(string path)
    {
        paths.RequireDirectory(path);
        files.CreateDirectory(path);
        paths.RequireDirectory(path);
    }

    internal FileStream Read(string path)
    {
        paths.RequireFile(path);
        return files.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    internal byte[] ReadBytes(string path, int maximumLength)
    {
        using var stream = Read(path);
        SetupIo.Require(stream.Length is > 0 && stream.Length <= maximumLength,
            SetupCode.InvalidEvidence);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal void WriteAtomic(string path, ReadOnlySpan<byte> bytes)
    {
        var parent = Path.GetDirectoryName(path)
            ?? throw new SetupIoException(SetupCode.InvalidEvidence);
        EnsureDirectory(parent);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            WriteNew(temporary, bytes);
            commits.Commit(temporary, path, overwrite: true);
            paths.RequireFile(path);
        }
        finally { DeleteTemporary(temporary); }
    }

    internal void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        using var stream = files.Open(path, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        files.Flush(stream);
    }

    internal void CopyNew(string source, string destination)
    {
        using var input = Read(source);
        using var output = files.Open(destination, FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        files.Flush(output);
    }

    internal void CommitNew(string temporary, string destination)
    {
        commits.Commit(temporary, destination, overwrite: false);
        paths.RequireFile(destination);
    }

    internal void DeleteTemporary(string path)
    {
        if (files.Entry(path).Kind != DiskEntryKind.Missing)
            files.DeleteFile(path);
    }
}

internal static class StrictSetupJson
{
    internal static T Read<T>(byte[] bytes, JsonTypeInfo<T> type) where T : class
    {
        using var document = JsonDocument.Parse(bytes);
        RequireUnique(document.RootElement);
        return JsonSerializer.Deserialize(bytes, type)
            ?? throw new SetupIoException(SetupCode.InvalidEvidence);
    }

    private static void RequireUnique(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var properties = element.EnumerateObject().ToArray();
                SetupIo.Require(properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() == properties.Length,
                    SetupCode.InvalidEvidence);
                foreach (var property in properties)
                    RequireUnique(property.Value);
                break;
            case JsonValueKind.Array:
                foreach (var value in element.EnumerateArray())
                    RequireUnique(value);
                break;
        }
    }
}

internal sealed record DiskGateSnapshot(
    GateDisposition Disposition,
    GateReadyState? Ready);

internal interface ISetupGateCodec
{
    DiskGateSnapshot Read(FileStream stream);
    void Close(FileStream stream);
    void Publish(FileStream stream, GateReadyState ready);
}

internal interface IReadableSetupLease : ISetupLease
{
    DiskGateSnapshot ReadSnapshot();
}

internal sealed class FileAdmissionSource(
    SetupDisk disk,
    UserInstallationScope scope,
    ISetupGateCodec codec) : IAdmissionSource
{
    public Effect<ISetupLease> TryAcquire()
    {
        try
        {
            disk.EnsureDirectory(scope.ServicingDirectory);
            RequireExistingGate();
            var stream = disk.Files.Open(scope.GatePath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return Effect<ISetupLease>.Ok(new Lease(stream, scope.GatePath, codec));
        }
        catch (SetupIoException error)
        { return Effect<ISetupLease>.Fail(error.Code, error.Evidence); }
        catch (IOException error)
        {
            return Effect<ISetupLease>.Fail((error.HResult & 0xffff) is 32 or 33
                ? SetupCode.Busy : SetupCode.Unavailable);
        }
        catch (UnauthorizedAccessException)
        { return Effect<ISetupLease>.Fail(SetupCode.Unavailable); }
    }

    private void RequireExistingGate()
    {
        var entry = disk.Files.Entry(scope.GatePath);
        SetupIo.Require(entry.Kind != DiskEntryKind.Directory && !entry.ReparsePoint,
            SetupCode.OwnershipRefused);
        if (entry.Kind == DiskEntryKind.File)
            disk.Paths.RequireFile(scope.GatePath);
    }

    private sealed class Lease(
        FileStream stream, string path, ISetupGateCodec codec) : IReadableSetupLease
    {
        private bool _held = true;
        private bool _closed;

        public string GatePath => path;
        public bool IsHeldAndClosed => _held && _closed;

        public DiskGateSnapshot ReadSnapshot()
        {
            SetupIo.Require(_held, SetupCode.InvalidEvidence);
            var snapshot = codec.Read(stream);
            _closed = snapshot.Disposition == GateDisposition.Closed;
            return snapshot;
        }

        public Task<Effect<Unit>> CloseAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetupIo.Require(_held, SetupCode.InvalidEvidence);
            codec.Close(stream);
            _closed = true;
            return Task.FromResult(Effect<Unit>.Ok(default));
        }

        public Task<Effect<Unit>> PublishReadyAsync(
            GateReadyState ready, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SetupIo.Require(IsHeldAndClosed, SetupCode.InvalidEvidence);
            codec.Publish(stream, ready);
            _closed = false;
            return Task.FromResult(Effect<Unit>.Ok(default));
        }

        public void Dispose()
        {
            try { stream.Dispose(); }
            finally { _held = false; }
        }
    }
}
