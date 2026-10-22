using System.ComponentModel;

namespace Hermes.Servicing.Acceptance;

public static partial class WindowsCrashSupervisor
{
    private static readonly StepJobBootstrap Bootstrap =
        new(new(ReadContext, CurrentStepJob.Create));

    public static WindowsCrashStepScope BeginCurrentStep() =>
        new(Bootstrap.Begin());

    private static StepJobContext ReadContext() => new(
        OperatingSystem.IsWindows(),
        Environment.GetEnvironmentVariable("GITHUB_ACTIONS"),
        Environment.GetEnvironmentVariable("RUNNER_OS"),
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"),
        Environment.GetEnvironmentVariable("GITHUB_JOB"),
        Environment.GetEnvironmentVariable("GITHUB_SERVER_URL"),
        Environment.GetEnvironmentVariable("GITHUB_REPOSITORY"));

    private sealed unsafe partial class CurrentStepJob
    {
        private readonly StepHandle _job;
        private StepHandle? _current;

        private CurrentStepJob(StepHandle job) => _job = job;

        private StepJobOwnedHandles Handles => new(_job.Owner, _current?.Owner);

        private StepHandle Current =>
            _current is { IsClosed: false, IsInvalid: false } current
                ? current
                : throw new InvalidOperationException("Current-step handle unavailable.");

        private StepJob Effects => new(
            Configure, AssignCurrent, Read, ClearKillOnClose,
            Close, Close, ReleaseVerified);

        internal static StepJob Create()
        {
            var raw = CreateJobObjectW(0, 0);
            if (raw == 0)
                throw NativeFailure();
            var owner = new CurrentStepJob(new StepHandle(raw));
            var transferred = false;
            try
            {
                owner._current = RetainCurrent();
                var effects = owner.Effects;
                transferred = true;
                return effects;
            }
            finally
            {
                if (!transferred)
                    owner.Close();
            }
        }

        private static StepHandle RetainCurrent()
        {
            const uint access = 0x0100 | 0x0001 | 0x0400;
            var current = GetCurrentProcess();
            nint retained = 0;
            if (DuplicateHandle(
                current, current, current, &retained, access, 0, 0) == 0)
                throw NativeFailure();
            return new(retained);
        }

        private void Configure() =>
            SetLimits(new(new(Flags: StepJobLimits.KillOnClose)));

        private void AssignCurrent()
        {
            if (AssignProcessToJobObject(_job, Current) == 0)
                throw NativeFailure();
            _job.Owner.ArmTermination();
        }

        private StepJobReadback Read()
        {
            var limits = Query<ExtendedLimits>(JobInformation.ExtendedLimits);
            var ui = Query<uint>(JobInformation.UiRestrictions);
            var accounting = Query<BasicAccounting>(JobInformation.BasicAccounting);
            uint flags = 0;
            int member = 0;
            if (GetHandleInformation(_job, &flags) == 0 ||
                IsProcessInJob(Current, _job, &member) == 0)
                throw NativeFailure();
            return new(limits.Basic.Flags, member != 0, ui,
                flags, accounting.ActiveProcesses);
        }

        private T Query<T>(JobInformation information) where T : unmanaged
        {
            T value = default;
            uint returned = 0;
            if (QueryInformationJobObject(
                _job, information, &value, (uint)sizeof(T), &returned) == 0)
                throw NativeFailure();
            if (returned != (uint)sizeof(T))
                throw new InvalidDataException("Private step-job query size refused.");
            return value;
        }

        private void SetLimits(ExtendedLimits limits)
        {
            if (SetInformationJobObject(
                _job, JobInformation.ExtendedLimits,
                &limits, (uint)sizeof(ExtendedLimits)) == 0)
                throw NativeFailure();
        }

        private void ClearKillOnClose() => SetLimits(default);

        private void ReleaseVerified() => Handles.ReleaseVerified();

        private void Close() => Handles.Abandon();

        private static int LastError() =>
            Marshal.GetLastPInvokeError() is > 0 and var error ? error : 13;

        private static Win32Exception NativeFailure() =>
            new(LastError(), "Private step-job operation refused.");

        private sealed class StepHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal StepJobHandleOwner Owner { get; }

            internal StepHandle(nint value) : base(ownsHandle: true)
            {
                Owner = new(new(
                    CloseNative,
                    () => TerminateJobObject(value, 1) != 0 ? 0 : LastError()));
                SetHandle(value);
            }

            private int CloseNative()
            {
                if (CloseHandle(handle) == 0)
                    return LastError();
                // Scope serialization excludes in-flight interop. A failed close
                // must leave both native ownership and SafeHandle ownership intact.
                SetHandleAsInvalid();
                return 0;
            }

            // Kernel failures are non-success, not a host-crash fallback.
            // SafeHandle consumes this release even on failure; the recorded
            // finalization decision prevents retries through a consumed handle.
            protected override bool ReleaseHandle() =>
                Owner.FinalizeOwnership().Released;
        }

        private enum JobInformation
        {
            BasicAccounting = 1,
            UiRestrictions = 4,
            ExtendedLimits = 9
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct BasicLimits(
            long ProcessTime = default,
            long JobTime = default,
            StepJobLimits Flags = default,
            nuint MinimumWorkingSet = default,
            nuint MaximumWorkingSet = default,
            uint ActiveProcesses = default,
            nuint Affinity = default,
            uint Priority = default,
            uint Scheduling = default);

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct IoCounters(
            ulong ReadOperations,
            ulong WriteOperations,
            ulong OtherOperations,
            ulong ReadBytes,
            ulong WriteBytes,
            ulong OtherBytes);

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct ExtendedLimits(
            BasicLimits Basic,
            IoCounters Io = default,
            nuint ProcessMemory = default,
            nuint JobMemory = default,
            nuint PeakProcessMemory = default,
            nuint PeakJobMemory = default);

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct BasicAccounting(
            long TotalUserTime,
            long TotalKernelTime,
            long PeriodUserTime,
            long PeriodKernelTime,
            uint PageFaultCount,
            uint TotalProcesses,
            uint ActiveProcesses,
            uint TerminatedProcesses);

        [LibraryImport("kernel32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint GetCurrentProcess();

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int DuplicateHandle(
            nint sourceProcess, nint sourceHandle, nint targetProcess,
            nint* targetHandle, uint access, int inherit, uint options);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial nint CreateJobObjectW(nint attributes, nint name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int SetInformationJobObject(
            StepHandle job, JobInformation information, void* value, uint length);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int QueryInformationJobObject(
            StepHandle job, JobInformation information,
            void* value, uint length, uint* returned);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int AssignProcessToJobObject(
            StepHandle job, StepHandle process);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int IsProcessInJob(
            StepHandle process, StepHandle job, int* member);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int GetHandleInformation(
            StepHandle handle, uint* flags);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int TerminateJobObject(nint job, uint exitCode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static partial int CloseHandle(nint handle);
    }
}

public sealed class WindowsCrashStepScope : IDisposable
{
    private readonly StepJobScope _scope;

    internal WindowsCrashStepScope(StepJobScope scope) => _scope = scope;

    // The fixed caller must already have validated probe exit, fresh report,
    // revision bindings, and cleanup. It must not launch concurrently with release.
    public void CompleteAfterChildrenExited() =>
        _scope.CompleteAfterChildrenExited();

    // Without successful completion, disposal requests assigned-job termination.
    // Kernel failures remain explicit; the independent external deadline is required.
    public void Dispose() => _scope.Dispose();
}
