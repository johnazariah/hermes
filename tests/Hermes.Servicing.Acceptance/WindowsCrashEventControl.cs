using System.Runtime.ExceptionServices;

namespace Hermes.Servicing.Acceptance;

internal static partial class WindowsCrashControl
{
    // No second child factory/publication implementation. Start retains the
    // existing published-image/source proof and the same real child handle.
    internal static CrashEventControlEffects EventEffects => new(
        RequireEventContext, EventSchemaReceipt.Open, Start,
        new(WindowsCrashEventQuery.EnumerateCompletedCandidate,
            TimeProvider.System, Task.Delay),
        PublishEventBase, PublishEventCorrelation, Console.Write);

    private static void RequireEventContext()
    {
        RequireContext();
        CrashCorrelationErrors.Require(
            RuntimeInformation.ProcessArchitecture == Architecture.X64,
            CrashCorrelationReason.Guard);
    }

    private static void PublishEventBase(byte[] bytes)
    {
        RequireEventContext();
        Publish(bytes);
    }

    private static void PublishEventCorrelation(byte[] bytes)
    {
        RequireEventContext();
        CrashCorrelationErrors.Require(bytes.Length is > 0 and <= CrashControl.MaximumReportBytes,
            CrashCorrelationReason.Publication);
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? throw new CrashCorrelationException(CrashCorrelationReason.Publication);
        WindowsRestrictedOwnerTokens.RequireStartupFile(workspace);
        var artifacts = ReportDirectory(workspace, ".artifacts");
        var evidence = ReportDirectory(artifacts, "installer-evidence");
        using var file = new FileStream(Path.Combine(evidence, "event-correlation.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    // Local stability/freshness checks only. The parent must independently bind
    // the fixed publishing step, reviewed script, checkout and current job.
    // No environment variable is spoofed and no schema script is invoked here.
    private sealed class EventSchemaReceipt(
        string workspace, string metadataPath, string sourcePath) : ICrashSchemaReceipt
    {
        private readonly List<IDisposable> _resources = [];
        private readonly List<SafeFileHandle> _directories = [];
        private FileStream? _metadata;
        private FileStream? _source;
        private string _sourceHash = CrashControl.EmptyHash;
        private DateTimeOffset _created;
        private DateTimeOffset _written;
        private DateTimeOffset _opened;
        private bool _closed;

        internal static ICrashSchemaReceipt Open()
        {
            RequireEventContext();
            var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
                ?? throw new CrashCorrelationException(CrashCorrelationReason.Receipt);
            WindowsRestrictedOwnerTokens.RequireStartupFile(workspace);
            var receipt = new EventSchemaReceipt(workspace,
                Path.Combine(workspace, ".artifacts", "installer-evidence", "startup-event-schema.json"),
                Path.Combine(workspace, "installer", "windows", "Invoke-StartupEventSchema.ps1"));
            try { receipt.Pin(); _ = receipt.Read(); return receipt; }
            catch (Exception failure)
            {
                var error = CrashCorrelationErrors.Capture(receipt.Dispose, failure);
                CrashCorrelationErrors.Throw(error);
                throw;
            }
        }

        private void Pin()
        {
            RequirePaths();
            foreach (var directory in Ancestors(metadataPath).Concat(Ancestors(sourcePath))
                .Distinct(StringComparer.OrdinalIgnoreCase))
                PinDirectory(directory);
            _metadata = PinFile(metadataPath);
            _source = PinFile(sourcePath);
            _sourceHash = Hash(_source);
            _created = File.GetCreationTimeUtc(metadataPath);
            _written = File.GetLastWriteTimeUtc(metadataPath);
            _opened = DateTimeOffset.UtcNow;
            RequirePaths();
        }

        private void PinDirectory(string path)
        {
            var handle = EventOpenDirectory(path, 0, 3, 0, 3, 0x02200000, 0);
            _resources.Add(handle);
            CrashCorrelationErrors.Require(!handle.IsInvalid,
                CrashCorrelationReason.Receipt);
            _directories.Add(handle);
            var attributes = File.GetAttributes(path);
            CrashCorrelationErrors.Require(
                (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) ==
                    FileAttributes.Directory, CrashCorrelationReason.Receipt);
        }

        private FileStream PinFile(string path)
        {
            WindowsRestrictedOwnerTokens.RequireStartupFile(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _resources.Add(stream);
            CrashCorrelationErrors.Require(
                (File.GetAttributes(path) & FileAttributes.Directory) == 0,
                CrashCorrelationReason.Receipt);
            WindowsRestrictedOwnerTokens.RequireStartupFile(path);
            return stream;
        }

        public CrashSchemaSnapshot Read()
        {
            RequireEventContext();
            CrashCorrelationErrors.Require(!_closed &&
                _directories.All(handle => !handle.IsClosed && !handle.IsInvalid),
                CrashCorrelationReason.Receipt);
            RequirePaths();
            var metadata = _metadata
                ?? throw new CrashCorrelationException(CrashCorrelationReason.Receipt);
            var source = _source
                ?? throw new CrashCorrelationException(CrashCorrelationReason.Receipt);
            RequireTimes();
            CrashCorrelationErrors.Require(Hash(source) == _sourceHash,
                CrashCorrelationReason.ReceiptChanged);
            var snapshot = new CrashSchemaSnapshot(ReadMetadata(metadata), _sourceHash);
            _ = CrashEventSchema.Admit(snapshot);
            RequirePaths();
            RequireTimes();
            return snapshot;
        }

        private void RequireTimes()
        {
            CrashCorrelationErrors.Require(
                new DateTimeOffset(File.GetCreationTimeUtc(metadataPath)) == _created &&
                new DateTimeOffset(File.GetLastWriteTimeUtc(metadataPath)) == _written,
                CrashCorrelationReason.ReceiptChanged);
            CrashEventSchema.RequireFresh(_created, _written, _opened, DateTimeOffset.UtcNow);
        }

        private void RequirePaths()
        {
            WindowsRestrictedOwnerTokens.RequireStartupFile(workspace);
            WindowsRestrictedOwnerTokens.RequireStartupFile(metadataPath);
            WindowsRestrictedOwnerTokens.RequireStartupFile(sourcePath);
        }

        private static byte[] ReadMetadata(FileStream stream)
        {
            CrashCorrelationErrors.Require(
                !stream.SafeFileHandle.IsClosed &&
                stream.Length is > 0 and <= CrashEventSchema.MaximumBytes,
                CrashCorrelationReason.Receipt);
            var bytes = new byte[checked((int)stream.Length)];
            stream.Position = 0;
            stream.ReadExactly(bytes);
            CrashCorrelationErrors.Require(stream.Length == bytes.Length,
                CrashCorrelationReason.ReceiptChanged);
            return bytes;
        }

        private static string Hash(FileStream stream)
        {
            CrashCorrelationErrors.Require(!stream.SafeFileHandle.IsClosed &&
                stream.Length is > 0 and <= 65536, CrashCorrelationReason.Receipt);
            stream.Position = 0;
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        private static IEnumerable<string> Ancestors(string file)
        {
            for (var path = Path.GetDirectoryName(file); path is { };
                path = Path.GetDirectoryName(path))
                yield return path;
        }

        public void Dispose()
        {
            if (_closed)
                return;
            _closed = true;
            var errors = new List<Exception>();
            foreach (var resource in _resources.AsEnumerable().Reverse())
            {
                try { resource.Dispose(); }
                catch (Exception error) { errors.Add(error); }
            }
            if (errors.Count == 1)
                ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1)
                throw new AggregateException("Schema receipt cleanup failed.", errors);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW",
        StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle EventOpenDirectory(
        string path, uint access, uint share, nint security,
        uint disposition, uint flags, nint template);
}
