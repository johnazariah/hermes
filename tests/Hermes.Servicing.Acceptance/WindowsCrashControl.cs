using System.ComponentModel;

namespace Hermes.Servicing.Acceptance;

internal static partial class WindowsCrashControl
{
    internal static CrashCommandEffects Effects => new(
        RequireContext, RunChildAsync, RunProbeAsync, Publish, Console.Write);

    private static void RequireContext()
    {
        WindowsRestrictedOwnerTokens.Instance.RequireDisposableJob();
        CiAdmission.Read(cleanOwner: true).Require();
    }

    private static async Task<int> RunChildAsync(CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            deadline.Token, cancellationToken);
        using var input = new SafeFileHandle(GetStdHandle(-10), ownsHandle: false);
        using var output = Console.OpenStandardOutput();
        if (input.IsInvalid)
            return CrashControl.TransportRefused;
        return await CrashChild.RunAsync(new(
            new(memory => ReadPipe(input, memory)),
            token => CrashWire.WriteReadyAsync(output, token),
            WerSetFlags, ReadWerFlags,
            static () => Environment.FailFast(CrashControl.Message)), linked.Token);
    }

    private static Task<CrashResult> RunProbeAsync(CancellationToken cancellationToken) =>
        OwnedCrashControl.RunAsync(Start, AttestAsync,
            CrashLimits.Default, cancellationToken);

    private static Task AttestAsync(
        CrashObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = observation.Recheck();
        return Task.CompletedTask;
    }

    private static void Start(
        Action<CrashOwnedChild> adopt, CancellationToken cancellationToken) =>
        StartWithInfo(CreateStartInfo, adopt, cancellationToken);

    private static void StartWithInfo(Func<string, ProcessStartInfo> createStartInfo,
        Action<CrashOwnedChild> adopt, CancellationToken cancellationToken)
    {
        RequireContext();
        cancellationToken.ThrowIfCancellationRequested();
        var publication = Publication.Open();
        var transferred = false;
        try
        {
            StartPublished(publication, createStartInfo, child =>
            {
                adopt(child);
                transferred = true;
            }, cancellationToken);
        }
        catch (Exception error) when (!transferred && CrashControl.Operational(error))
        {
            ReleasePublication(publication, error);
            throw;
        }
    }

    private static void StartPublished(Publication publication,
        Func<string, ProcessStartInfo> createStartInfo,
        Action<CrashOwnedChild> adopt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var process = Process.Start(createStartInfo(publication.ImagePath))
            ?? throw new InvalidOperationException("Crash-control start failed.");
        // Open constructs only lazy capabilities; no native/stream observation
        // precedes adoption. All subsequent failures use coordinator cleanup.
        adopt(new NativeChild(process, publication).Open());
        cancellationToken.ThrowIfCancellationRequested();
        OwnedChildStartup.RequireImage(new StartedChildImage(process), TimeProvider.System);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void ReleasePublication(Publication publication, Exception failure)
    {
        try { publication.Dispose(); }
        catch (Exception cleanup) when (CrashControl.Operational(cleanup))
        {
            throw new CrashStartException(CrashCleanup.Dispose,
                new AggregateException(failure, cleanup));
        }
    }

    private static ProcessStartInfo CreateStartInfo(string image)
    {
        var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP")
            ?? throw new InvalidDataException("Crash-control temporary directory missing.");
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        WindowsRestrictedOwnerTokens.RequireStartupFile(temporary);
        WindowsRestrictedOwnerTokens.RequireStartupFile(windows);
        var start = OwnedChild.CreateStartInfo(
            image, [], temporary, captureStandardError: true);
        CrashLaunch.Restrict(start, windows, temporary);
        return start;
    }

    private sealed class Publication(
        string imagePath, string sourcePath, FileStream image, FileStream source,
        string imageHash, string sourceHash) : IDisposable
    {
        internal string ImagePath => imagePath;
        internal string SourceHash => sourceHash;

        internal static Publication Open()
        {
            var imagePath = Environment.ProcessPath
                ?? throw new InvalidDataException("Crash-control current apphost missing.");
            var sourcePath = typeof(Program).Assembly.Location;
            RequireLayout(imagePath, sourcePath);
            var image = File.Open(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var transferred = false;
            try
            {
                var result = OpenSource(imagePath, sourcePath, image);
                transferred = true;
                return result;
            }
            finally { if (!transferred) image.Dispose(); }
        }

        private static Publication OpenSource(
            string imagePath, string sourcePath, FileStream image)
        {
            var source = File.Open(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var transferred = false;
            try
            {
                var result = new Publication(imagePath, sourcePath, image, source,
                    Files.Hash(imagePath), Files.Hash(sourcePath));
                result.RequireSource(imagePath);
                transferred = true;
                return result;
            }
            finally { if (!transferred) source.Dispose(); }
        }

        private static void RequireLayout(string imagePath, string sourcePath)
        {
            WindowsRestrictedOwnerTokens.RequireStartupFile(imagePath);
            WindowsRestrictedOwnerTokens.RequireStartupFile(sourcePath);
            var root = Path.GetDirectoryName(imagePath)
                ?? throw new InvalidDataException("Crash-control publication missing.");
            Require.That(Path.GetFileName(imagePath) == "Hermes.Servicing.Acceptance.exe");
            Require.That(InstalledSource.SamePath(
                sourcePath, Path.Combine(root, "Hermes.Servicing.Acceptance.dll")));
            Require.That(InstalledSource.SamePath(root, AppContext.BaseDirectory));
            var runtime = Path.Combine(root, "coreclr.dll");
            WindowsRestrictedOwnerTokens.RequireStartupFile(runtime);
        }

        internal string RequireSource(string loadedImage)
        {
            CrashExitChecks.Require(CrashExitCheck.Pins,
                CrashExitChecks.Read(CrashExitCheck.Pins,
                    () => !image.SafeFileHandle.IsClosed && !source.SafeFileHandle.IsClosed));
            CrashExitChecks.Require(CrashExitCheck.PublicationPath,
                CrashExitChecks.Read(CrashExitCheck.PublicationPath,
                    () => InstalledSource.SamePath(loadedImage, imagePath)));
            CrashExitChecks.Require(CrashExitCheck.ImageHash,
                CrashExitChecks.Read(CrashExitCheck.ImageHash, () => Files.Hash(loadedImage)) == imageHash);
            CrashExitChecks.Require(CrashExitCheck.SourceHash,
                CrashExitChecks.Read(CrashExitCheck.SourceHash, () => Files.Hash(sourcePath)) == sourceHash);
            CrashExitChecks.Require(CrashExitCheck.SourceMvid,
                CrashExitChecks.Read(CrashExitCheck.SourceMvid, () => Files.Mvid(sourcePath)) ==
                typeof(Program).Assembly.ManifestModule.ModuleVersionId);
            return imageHash;
        }

        public void Dispose()
        {
            try { image.Dispose(); }
            finally { source.Dispose(); }
        }
    }

    private sealed class NativeChild(Process process, Publication publication)
    {
        private SafeProcessHandle? _handle;

        internal CrashOwnedChild Open()
        {
            var identity = new CrashRetainedIdentity(new(
                ReadSnapshot, ReadImage, RecheckPins, WaitAsync));
            return new(
                new(memory => ReadPipe(
                    CrashWire.BorrowHandle(process.StandardOutput.BaseStream), memory)),
                new(memory => ReadPipe(
                    CrashWire.BorrowHandle(process.StandardError.BaseStream), memory)),
                token => CrashWire.WriteArmAsync(process.StandardInput.BaseStream, token),
                process.StandardInput.BaseStream.Dispose,
                process.StandardOutput.Dispose, process.StandardError.Dispose,
                identity.Read, identity.WaitAsync, Stop, () => Dispose(identity));
        }

        private CrashProcessSnapshot ReadSnapshot()
        {
            _handle ??= CrashExitChecks.Read(CrashExitCheck.Handle, () => process.SafeHandle);
            if (_handle is not { IsClosed: false, IsInvalid: false } handle)
                throw new CrashExitException(CrashExitCheck.Handle);
            var native = ReadProcess(handle);
            CrashExitChecks.Require(CrashExitCheck.ProcessIdMatch,
                native.ProcessId == CrashExitChecks.Read(
                    CrashExitCheck.ProcessIdMatch, () => checked((uint)process.Id)));
            return new(handle, new(native.ProcessId, native.Born),
                native.Exited, native.Code);
        }

        private CrashImagePins RecheckPins(string path)
        {
            var imageHash = publication.RequireSource(path);
            // Keep the original native Windows admission in addition to the
            // portable model's explicitly Windows-targeted qualification.
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidDataException("Crash-control live identity refused.");
            return new(imageHash, publication.SourceHash);
        }

        private static unsafe (uint ProcessId, long Born, long Exited, uint Code)
            ReadProcess(SafeProcessHandle handle)
        {
            long born = 0, exited = 0, kernel = 0, user = 0;
            uint code = 0;
            var pid = GetProcessId(handle);
            if (pid == 0)
                throw new CrashExitException(CrashExitCheck.ProcessId, NativeFailure());
            if (GetProcessTimes(handle, &born, &exited, &kernel, &user) == 0)
                throw new CrashExitException(CrashExitCheck.ProcessTimes, NativeFailure());
            if (GetExitCodeProcess(handle, &code) == 0)
                throw new CrashExitException(CrashExitCheck.ProcessExitCode, NativeFailure());
            return (pid, born, exited, code);
        }

        private static unsafe string ReadImage(SafeProcessHandle handle)
        {
            const int capacity = 512;
            char* buffer = stackalloc char[capacity];
            uint length = capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, &length) == 0)
                throw new CrashExitException(CrashExitCheck.ImageQuery, NativeFailure());
            if (length is 0 or >= capacity)
                throw new CrashExitException(CrashExitCheck.ImageLength);
            return new string(buffer, 0, checked((int)length));
        }

        private async Task<uint> WaitAsync(CancellationToken cancellationToken)
        {
            await process.WaitForExitAsync(cancellationToken);
            return unchecked((uint)process.ExitCode);
        }

        private void Stop()
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: false);
        }

        private void Dispose(CrashRetainedIdentity identity)
        {
            identity.Dispose();
            try { process.Dispose(); }
            finally { publication.Dispose(); }
        }
    }

    private static unsafe CrashRead ReadPipe(SafeHandle pipe, Memory<byte> destination)
    {
        uint available = 0;
        if (PeekNamedPipe(pipe, 0, 0, 0, &available, 0) == 0)
            return PipeEndOrFailure();
        if (available == 0)
            return new(CrashReadKind.Pending);
        uint read = 0;
        fixed (byte* buffer = destination.Span)
            if (ReadFile(pipe, buffer,
                Math.Min(available, checked((uint)destination.Length)), &read, 0) == 0)
                return PipeEndOrFailure();
        return read == 0 ? new(CrashReadKind.End) :
            new(CrashReadKind.Data, checked((int)read));
    }

    private static CrashRead PipeEndOrFailure() =>
        Marshal.GetLastPInvokeError() is 109
            ? new(CrashReadKind.End) : throw NativeFailure();

    private static unsafe (int HResult, uint Flags) ReadWerFlags()
    {
        uint flags = 0;
        var result = WerGetFlags(new nint(-1), &flags);
        return (result, flags);
    }

    private static Win32Exception NativeFailure() =>
        new(Marshal.GetLastPInvokeError(), "Crash-control native operation refused.");

    private static void Publish(byte[] bytes)
    {
        if (bytes.Length is < 1 or > CrashControl.MaximumReportBytes)
            throw new InvalidDataException("Crash-control report length refused.");
        var workspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
            ?? throw new InvalidDataException("Crash-control workspace missing.");
        WindowsRestrictedOwnerTokens.RequireStartupFile(workspace);
        var artifacts = ReportDirectory(workspace, ".artifacts");
        var evidence = ReportDirectory(artifacts, "installer-evidence");
        using var file = new FileStream(
            Path.Combine(evidence, "startup-crash-control.json"),
            FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static string ReportDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        WindowsRestrictedOwnerTokens.RequireStartupFile(path);
        return path;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "WerSetFlags")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int WerSetFlags(uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "WerGetFlags")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int WerGetFlags(nint process, uint* flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetStdHandle(int kind);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetProcessId(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetProcessTimes(
        SafeProcessHandle process, long* born, long* exited, long* kernel, long* user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int GetExitCodeProcess(SafeProcessHandle process, uint* code);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int QueryFullProcessImageName(
        SafeProcessHandle process, uint flags, char* image, uint* length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int PeekNamedPipe(
        SafeHandle pipe, nint buffer, uint size, nint read, uint* available, nint left);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static unsafe partial int ReadFile(
        SafeHandle pipe, byte* buffer, uint size, uint* read, nint overlapped);
}
