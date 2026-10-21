using System.Buffers.Binary;

namespace Hermes.Servicing.Acceptance;

// No generated ToString, identity properties, SID properties, or privilege
// properties: callers can only compare private snapshots as a Boolean.
internal sealed class AcceptanceParentSecuritySnapshot(
    long identity,
    int session,
    ImmutableArray<(long Identity, uint Attributes)> privileges,
    ImmutableArray<byte> groups)
{
    private readonly long _identity = identity;
    private readonly int _session = session;
    private readonly ImmutableArray<(long Identity, uint Attributes)> _privileges = privileges;
    private readonly ImmutableArray<byte> _groups = groups;

    internal bool SameFinalState(AcceptanceParentSecuritySnapshot other) =>
        _identity == other._identity && _session == other._session &&
        _privileges.SequenceEqual(other._privileges) &&
        _groups.SequenceEqual(other._groups);
}

internal sealed unsafe partial class WindowsRestrictedOwnerTokens : IAcceptanceChildFactory
{
    public IAcceptanceOwnedChild Open()
    {
        RequireDisposableJob();
        return new OwnedInertChild();
    }

    internal IAcceptanceChildFactory PowerShellForObservation { get; } =
        new PowerShellProbeChildren(AcceptancePowerShellProbeConsole.Detached);

    internal IAcceptanceChildFactory PowerShellNoWindowForObservation { get; } =
        new PowerShellProbeChildren(AcceptancePowerShellProbeConsole.NoWindow);

    internal IAcceptanceChildFactory PowerShellNewConsoleForObservation { get; } =
        new PowerShellProbeChildren(AcceptancePowerShellProbeConsole.NewConsole);

    internal IAcceptanceChildFactory PowerShellDetachedSelfControlForObservation { get; } =
        new PowerShellProbeChildren(AcceptancePowerShellProbeConsole.Detached, ownedSelfControl: true);

    internal IAcceptanceChildFactory PowerShellNoWindowSelfControlForObservation { get; } =
        new PowerShellProbeChildren(AcceptancePowerShellProbeConsole.NoWindow, ownedSelfControl: true);

    private sealed class PowerShellProbeChildren(
        AcceptancePowerShellProbeConsole console, bool ownedSelfControl = false)
        : IAcceptanceChildFactory
    {
        public IAcceptanceOwnedChild Open()
        {
            Instance.RequireDisposableJob();
            return new OwnedInertChild(
                ownedSelfControl: ownedSelfControl, powerShellProbe: true, powerShellConsole: console);
        }
    }

    private static (string Application, char[] Command, FileStream Source)
        OpenPowerShellProbe()
    {
        var programFiles = RequirePowerShellProbePath(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
        var workspace = RequirePowerShellProbePath(
            Environment.GetEnvironmentVariable("GITHUB_WORKSPACE")
                ?? throw RestrictedOwnerToken.Failure(13));
        var application = RequirePowerShellProbePath(
            Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe"));
        var script = RequirePowerShellProbePath(
            Path.Combine(workspace, "installer", "windows", "Test-RestrictedPowerShell.ps1"));
        var command =
            $"\"{application}\" -NoLogo -NoProfile -NonInteractive -File \"{script}\"\0".ToCharArray();
        return (application, command, OpenPowerShellProbeSource(script));
    }

    private static string RequirePowerShellProbePath(string value)
    {
        if (value.Length is < 4 or > 240 ||
            !char.IsAsciiLetter(value[0]) || value[1] != ':' || value[2] != '\\' ||
            !value.Split('\\').Skip(1).All(IsPowerShellProbeComponent) ||
            !Path.IsPathFullyQualified(value) ||
            !string.Equals(value, Path.GetFullPath(value), StringComparison.OrdinalIgnoreCase))
            throw RestrictedOwnerToken.Failure(13);
        for (string? path = value; path is { }; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw RestrictedOwnerToken.Failure(13);
        return value;
    }

    private static bool IsPowerShellProbeComponent(string component) =>
        component.Length > 0 && component[^1] is not ('.' or ' ') &&
        component.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !component.Any(char.IsControl);

    private static FileStream OpenPowerShellProbeSource(string path)
    {
        var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (source.Length is not (8 or 9))
                throw RestrictedOwnerToken.Failure(13);
            Span<byte> bytes = stackalloc byte[checked((int)source.Length)];
            source.ReadExactly(bytes);
            if (!bytes.SequenceEqual("exit 37\n"u8) && !bytes.SequenceEqual("exit 37\r\n"u8))
                throw RestrictedOwnerToken.Failure(13);
            return source;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    internal AcceptanceParentSecuritySnapshot ReadParentSecurity()
    {
        RequireDisposableJob();
        using var source = OpenCurrentPrimary();
        var handle = SourceHandle(source);
        using var statistics = TokenBuffer.Read(handle, StatisticsInformation);
        if (statistics.Length != 56)
            throw RestrictedOwnerToken.Failure(13);
        // CreateProcessAsUser can temporarily enable existing privileges.
        // TokenId must match; ModifiedId is deliberately not compared.
        return new(Marshal.ReadInt64(statistics.Pointer),
            ReadInt(handle, SessionInformation),
            ReadParentPrivileges(handle), ReadParentGroups(handle));
    }

    private static ImmutableArray<(long Identity, uint Attributes)> ReadParentPrivileges(
        SafeAccessTokenHandle token)
    {
        using var data = TokenBuffer.Read(token, 3);
        var count = Marshal.ReadInt32(data.Pointer);
        if (count < 0 || count != (data.Length - sizeof(uint)) / 12 ||
            data.Length != sizeof(uint) + 12 * count)
            throw RestrictedOwnerToken.Failure(13);
        return Enumerable.Range(0, count)
            .Select(index => (
                Identity: Marshal.ReadInt64(data.Pointer, 4 + 12 * index),
                Attributes: unchecked((uint)Marshal.ReadInt32(data.Pointer, 12 + 12 * index))))
            .OrderBy(value => value.Identity)
            .ToImmutableArray();
    }

    private static ImmutableArray<byte> ReadParentGroups(SafeAccessTokenHandle token)
    {
        using var data = TokenBuffer.Read(token, GroupsInformation);
        return ReadGroups(data).SelectMany(CopyParentGroup).ToImmutableArray();
    }

    private static IEnumerable<byte> CopyParentGroup(SidAndAttributes group)
    {
        var length = 8 + 4 * Marshal.ReadByte(group.Sid, 1);
        var bytes = new byte[length + sizeof(uint)];
        Marshal.Copy(group.Sid, bytes, 0, length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(length), group.Attributes);
        return bytes;
    }

    private enum ChildState
    {
        Empty,
        JobReady,
        Suspended,
        Assigned,
        PrimaryVerified,
        ThreadVerified,
        AccessObserved,
        ResumeAttempted,
        Running,
        Exited
    }

    private sealed partial class OwnedInertChild(
        AcceptanceDesktopSelection desktop = AcceptanceDesktopSelection.Automatic,
        bool ownedSelfControl = false,
        bool powerShellProbe = false,
        AcceptancePowerShellProbeConsole powerShellConsole = AcceptancePowerShellProbeConsole.Detached)
        : IAcceptanceOwnedChild
    {
        private SafeChildHandle? _job;
        private SafeChildHandle? _process;
        private SafeChildHandle? _thread;
        private SafeChildHandle? _primary;
        private FileStream? _powerShellSource;
        private ChildState _state;
        private bool _disposed;
        private bool _cleanupCompleted;

        public bool IsClosed => _disposed && _cleanupCompleted;

        public void CreateKillOnCloseJob()
        {
            Require(ChildState.Empty);
            var raw = CreateJobObjectW(0, 0);
            if (raw == 0)
                throw NativeFailure();
            _job = new(raw);
            var basic = new JobBasicLimits(0, 0, 0x2000, 0, 0, 0, 0, 0, 0);
            var limits = new JobExtendedLimits(basic, default, 0, 0, 0, 0);
            if (SetInformationJobObject(Handle(_job), 9,
                &limits, (uint)sizeof(JobExtendedLimits)) == 0)
                throw NativeFailure();
            _state = ChildState.JobReady;
        }

        public void CreateSuspended(IAcceptanceRestrictedToken token)
        {
            Require(ChildState.JobReady);
            var directory = SystemDirectory();
            var application = Path.Combine(directory, "cmd.exe");
            var command = $"\"{application}\" /d /q /c exit 37\0".ToCharArray();
            if (powerShellProbe)
                (application, command, _powerShellSource) = OpenPowerShellProbe();
            var descriptor = ownedSelfControl ? ReadOwnedSelfDescriptor(token) : null;
            Launch(FilteredHandle(token), application, command, directory, descriptor);
            _state = ChildState.Suspended;
        }

        private void Launch(
            SafeAccessTokenHandle token, string application,
            char[] command, string directory, byte[]? descriptor)
        {
            var selected = AcceptanceChildDesktop.Resolve(desktop, Instance);
            fixed (char* desktopName = selected)
            fixed (char* mutableCommand = command)
            fixed (byte* security = descriptor)
            {
                var startup = new ChildStartupInfo(
                    (uint)sizeof(ChildStartupInfo), Desktop: (nint)desktopName);
                if (powerShellProbe)
                    StartPowerShellProbe(
                        token, application, mutableCommand, directory, &startup, security);
                else
                    StartOwned(token, application, mutableCommand, directory, &startup, security);
            }
        }

        private void StartOwned(
            SafeAccessTokenHandle token, string application, char* command,
            string directory, ChildStartupInfo* startup, byte* security)
        {
            var attributes = new AcceptanceOwnedObjectAttributes(
                (uint)sizeof(AcceptanceOwnedObjectAttributes), (nint)security, 0);
            var supplied = security == null ? 0 : (nint)(&attributes);
            var process = default(ChildProcessInformation);
            const uint flags = 0x00000004u | 0x00000008u;
            if (CreateProcessAsUserW(token, application, command,
                supplied, supplied, 0, flags,
                0, directory, startup, &process) == 0)
                throw NativeFailure();
            _process = new(process.Process);
            _thread = new(process.Thread);
        }

        public void AssignJob()
        {
            Require(ChildState.Suspended);
            if (AssignProcessToJobObject(Handle(_job), Handle(_process)) == 0)
                throw NativeFailure();
            var assigned = 0;
            if (IsProcessInJob(Handle(_process), Handle(_job), &assigned) == 0)
                throw NativeFailure();
            if (assigned != 1)
                throw RestrictedOwnerToken.Failure(13);
            _state = ChildState.Assigned;
        }

        public RestrictedOwnerTokenFacts ReadPrimaryFacts()
        {
            Require(ChildState.Assigned);
            _primary = OpenChildPrimary();
            var candidate = Handle(_primary);
            using var source = Instance.OpenCurrentPrimary();
            var current = SourceHandle(source);
            var (enabled, denyOnly) = ReadAdministrator(candidate);
            var facts = new RestrictedOwnerTokenFacts(
                ReadInt(candidate, TypeInformation),
                ReadInt(candidate, ElevationInformation), ReadIntegrity(candidate),
                enabled, denyOnly, SameOwner(candidate, current),
                SameSession(candidate, current));
            facts.Require();
            _state = ChildState.PrimaryVerified;
            return facts;
        }

        private SafeChildHandle OpenChildPrimary()
        {
            nint raw = 0;
            if (OpenChildProcessToken(Handle(_process), 0x0008, &raw) == 0)
                throw NativeFailure();
            var token = new SafeChildHandle(raw);
            if (!token.IsInvalid)
                return token;
            token.CloseChecked();
            throw RestrictedOwnerToken.Failure(13);
        }

        public MsiContextRead ReadThreadToken()
        {
            Require(ChildState.PrimaryVerified);
            nint raw = 0;
            if (OpenChildThreadToken(Handle(_thread), 0x0008, 1, &raw) != 0)
            {
                using var token = Own(raw);
                return MsiContextRead.Observed;
            }
            var error = Marshal.GetLastPInvokeError();
            if (error != 1008)
                throw RestrictedOwnerToken.Failure(error);
            _state = ChildState.ThreadVerified;
            return MsiContextRead.Absent;
        }

        public AcceptanceChildAccessResult ReadObjectAccess()
        {
            Require(ChildState.ThreadVerified);
            var result = AcceptanceChildAccess.Read(new ChildAuthzScope(
                Handle(_primary), Handle(_process), Handle(_thread)));
            _state = ChildState.AccessObserved;
            return result;
        }

        public void Resume()
        {
            Require(ChildState.AccessObserved);
            // Set before the call: failure or an unexpected suspend count
            // cannot turn into a retry.
            _state = ChildState.ResumeAttempted;
            var previous = ResumeOwnedThread(Handle(_thread));
            if (previous == uint.MaxValue)
                throw NativeFailure();
            if (previous != 1)
                throw RestrictedOwnerToken.Failure(13);
            _state = ChildState.Running;
        }

        public uint Wait(CancellationToken cancellationToken)
        {
            Require(ChildState.Running);
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
            {
                cancellationToken.ThrowIfCancellationRequested();
                _powerShellIo?.Check();
                var result = WaitForOwnedObject(Handle(_process), 100);
                if (result == 0)
                    return ReadExit();
                if (result != 258)
                    throw WaitFailure(result);
            }
            throw RestrictedOwnerToken.Failure(1460);
        }

        private uint ReadExit()
        {
            uint exit = 0;
            if (GetOwnedExitCode(Handle(_process), &exit) == 0)
                throw NativeFailure();
            _state = ChildState.Exited;
            return exit;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            AcceptanceChildCleanup.Complete(
                () => _job?.CloseChecked(),
                StopOwnedProcess,
                ReapOwnedProcess,
                () => _primary?.CloseChecked(),
                () => _thread?.CloseChecked(),
                () => _process?.CloseChecked(),
                () => _powerShellIo?.Dispose(),
                () => _powerShellSource?.Dispose());
            _cleanupCompleted = true;
        }

        private void StopOwnedProcess()
        {
            if (_process is not { IsClosed: false, IsInvalid: false } process)
                return;
            var result = WaitForOwnedObject(process, 0);
            if (result == 0)
                return;
            if (result != 258)
                throw WaitFailure(result);
            // Covers failure before assignment as well as a failed job close.
            // This is only the owned CreateProcessAsUser process handle.
            if (TerminateOwnedProcess(process, 1223) == 0)
                throw NativeFailure();
        }

        private void ReapOwnedProcess()
        {
            if (_process is not { IsClosed: false, IsInvalid: false } process)
                return;
            var result = WaitForOwnedObject(process, 5000);
            if (result != 0)
                throw WaitFailure(result);
        }

        private void Require(ChildState expected)
        {
            if (_disposed)
                throw RestrictedOwnerToken.Failure(6);
            Instance.RequireDisposableJob();
            if (_state != expected)
                throw RestrictedOwnerToken.Failure(13);
        }

        private static SafeChildHandle Handle(SafeChildHandle? handle) =>
            handle is { IsClosed: false, IsInvalid: false } owned
                ? owned : throw RestrictedOwnerToken.Failure(6);

        private static System.ComponentModel.Win32Exception WaitFailure(uint result) =>
            result == uint.MaxValue ? NativeFailure()
                : RestrictedOwnerToken.Failure(result == 258 ? 1460 : 13);

        private static string SystemDirectory()
        {
            const int capacity = 512;
            char* buffer = stackalloc char[capacity];
            var length = GetSystemDirectoryW(buffer, capacity);
            if (length == 0)
                throw NativeFailure();
            if (length >= capacity)
                throw RestrictedOwnerToken.Failure(13);
            var directory = new string(buffer, 0, checked((int)length));
            if (!Path.IsPathFullyQualified(directory) || directory.Contains('"'))
                throw RestrictedOwnerToken.Failure(13);
            return directory;
        }
    }

    private sealed class SafeChildHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private int _closeError;

        internal SafeChildHandle(nint value) : base(ownsHandle: true) =>
            SetHandle(value);

        internal void CloseChecked()
        {
            Dispose();
            if (_closeError != 0)
                throw RestrictedOwnerToken.Failure(_closeError);
        }

        protected override bool ReleaseHandle()
        {
            if (CloseOwnedHandle(handle) != 0)
                return true;
            var error = Marshal.GetLastPInvokeError();
            _closeError = error > 0 ? error : 13;
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ChildStartupInfo(
        uint Size,
        nint Reserved = default,
        nint Desktop = default,
        nint Title = default,
        uint X = default,
        uint Y = default,
        uint XSize = default,
        uint YSize = default,
        uint XCountChars = default,
        uint YCountChars = default,
        uint FillAttribute = default,
        uint Flags = default,
        ushort ShowWindow = default,
        ushort ReservedSize = default,
        nint ReservedBytes = default,
        nint StandardInput = default,
        nint StandardOutput = default,
        nint StandardError = default);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct ChildProcessInformation(
        nint Process, nint Thread, uint ProcessId, uint ThreadId);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct JobBasicLimits(
        long ProcessTime, long JobTime, uint Flags,
        nuint MinimumWorkingSet, nuint MaximumWorkingSet,
        uint ActiveProcesses, nuint Affinity, uint Priority, uint Scheduling);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct JobIoCounters(
        ulong ReadOperations, ulong WriteOperations, ulong OtherOperations,
        ulong ReadBytes, ulong WriteBytes, ulong OtherBytes);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct JobExtendedLimits(
        JobBasicLimits Basic, JobIoCounters Io,
        nuint ProcessMemory, nuint JobMemory,
        nuint PeakProcessMemory, nuint PeakJobMemory);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint CreateJobObjectW(nint attributes, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetInformationJobObject(
        SafeChildHandle job, int information, JobExtendedLimits* limits, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int AssignProcessToJobObject(
        SafeChildHandle job, SafeChildHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int IsProcessInJob(
        SafeChildHandle process, SafeChildHandle job, int* assigned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetSystemDirectoryW(char* buffer, uint capacity);

    [LibraryImport("advapi32.dll", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateProcessAsUserW(
        SafeAccessTokenHandle token, string application, char* command,
        nint processAttributes, nint threadAttributes, int inheritHandles,
        uint flags, nint environment, string directory,
        ChildStartupInfo* startup, ChildProcessInformation* process);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenProcessToken", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int OpenChildProcessToken(
        SafeChildHandle process, uint access, nint* token);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenThreadToken", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int OpenChildThreadToken(
        SafeChildHandle thread, uint access, int openAsSelf, nint* token);

    [LibraryImport("kernel32.dll", EntryPoint = "ResumeThread", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint ResumeOwnedThread(SafeChildHandle thread);

    [LibraryImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint WaitForOwnedObject(SafeChildHandle process, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "GetExitCodeProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetOwnedExitCode(SafeChildHandle process, uint* exit);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int TerminateOwnedProcess(SafeChildHandle process, uint exit);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CloseOwnedHandle(nint handle);
}
