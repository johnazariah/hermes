namespace Hermes.Servicing.Acceptance;

internal sealed unsafe partial class WindowsRestrictedOwnerTokens
{
    internal static (int Pointer, int Startup, int Extended)
        PowerShellProbeIoLayout =>
        (sizeof(nint), sizeof(ChildStartupInfo), sizeof(PowerShellProbeStartupInfoEx));

    private sealed partial class OwnedInertChild
    {
        private PowerShellProbeStdio? _powerShellIo;

        private void StartPowerShellProbe(
            SafeAccessTokenHandle token, string application, char* command,
            string directory, ChildStartupInfo* startup, byte* security)
        {
            AcceptancePowerShellProbeIoPolicy.RequireLayout(PowerShellProbeIoLayout);
            var io = PowerShellProbeStdio.Open();
            _powerShellIo = io;
            LaunchPowerShellWithIo(
                token, application, command, directory, startup, security, io);
            io.ChildCreated();
        }

        private void LaunchPowerShellWithIo(
            SafeAccessTokenHandle token, string application, char* command,
            string directory, ChildStartupInfo* startup, byte* security,
            PowerShellProbeStdio io)
        {
            var children = io.ChildHandles;
            AcceptancePowerShellProbeIoPolicy.RequireHandles(children);
            fixed (nint* inherited = children)
            {
                // Both the attribute list and its value storage live through
                // CreateProcessAsUser; deletion occurs before unpinning.
                using var attributes = PowerShellProbeAttributes.Create((nint)inherited);
                var extended = PowerShellStartup(*startup, children, attributes.Pointer);
                StartPowerShellProcess(
                    token, application, command, directory, &extended, security);
            }
        }

        private static PowerShellProbeStartupInfoEx PowerShellStartup(
            ChildStartupInfo original, nint[] children, nint attributes) =>
            new(original with
            {
                Size = (uint)sizeof(PowerShellProbeStartupInfoEx),
                Flags = original.Flags | AcceptancePowerShellProbeIoPolicy.StartupFlags,
                StandardInput = children[0],
                StandardOutput = children[1],
                StandardError = children[2]
            }, attributes);

        private void StartPowerShellProcess(
            SafeAccessTokenHandle token, string application, char* command,
            string directory, PowerShellProbeStartupInfoEx* startup, byte* security)
        {
            var attributes = new AcceptanceOwnedObjectAttributes(
                (uint)sizeof(AcceptanceOwnedObjectAttributes), (nint)security, 0);
            var supplied = security == null ? 0 : (nint)(&attributes);
            var process = default(ChildProcessInformation);
            if (CreateProcessAsUserW(token, application, command,
                supplied, supplied, AcceptancePowerShellProbeIoPolicy.InheritHandles,
                AcceptancePowerShellProbeIoPolicy.CreationFlags,
                0, directory, (ChildStartupInfo*)startup, &process) == 0)
                throw NativeFailure();
            _process = new(process.Process);
            _thread = new(process.Thread);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PowerShellProbeStartupInfoEx(
        ChildStartupInfo Startup, nint Attributes);

    private sealed class PowerShellProbeStdio : IDisposable
    {
        private readonly ImmutableArray<PowerShellProbePipe> _pipes;
        private readonly AcceptancePowerShellProbeDrains _drains;
        private bool _disposed;

        private PowerShellProbeStdio(ImmutableArray<PowerShellProbePipe> pipes)
        {
            _pipes = pipes;
            _drains = new(pipes[1], pipes[2]);
        }

        internal nint[] ChildHandles =>
            _pipes.Select(pipe => pipe.ChildValue).ToArray();

        internal static PowerShellProbeStdio Open()
        {
            var pipes = ImmutableArray.CreateBuilder<PowerShellProbePipe>();
            try
            {
                foreach (var childReads in new[] { true, false, false })
                    pipes.Add(PowerShellProbePipe.Open(childReads));
                return new(pipes.ToImmutable());
            }
            catch (Exception original)
            {
                AcceptancePowerShellProbeIoPolicy.CleanupAfterFailure(
                    original, pipes.Select(pipe => (Action)pipe.Dispose).ToArray());
                throw;
            }
        }

        internal void ChildCreated()
        {
            AcceptanceChildCleanup.Complete(
                _pipes[0].CloseChild, _pipes[1].CloseChild, _pipes[2].CloseChild,
                _pipes[0].CloseParent);
            // The child is still suspended. Its stdin now has EOF, and the
            // parent retains neither child output writer.
            _drains.Start();
        }

        internal void Check() => _drains.Check();

        private void EmitDiagnostics()
        {
            var (output, error) = _drains.Facts;
            var closed = _pipes.All(pipe => pipe.IsClosed) ? 1 : 0;
            FormattableString row = $"HERMES_RESTRICTED_POWERSHELL_IO v=1 outBytes={output.Total} outKept={output.Retained} outDropped={output.Discarded} outState={(int)output.State} outError={output.Error} errBytes={error.Total} errKept={error.Retained} errDropped={error.Discarded} errState={(int)error.State} errError={error.Error} pipesClosed={closed} drainsClosed={(_drains.IsClosed ? 1 : 0)}";
            Console.Error.WriteLine(row.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            AcceptanceChildCleanup.Complete(
                _drains.Dispose,
                _pipes[0].Dispose, _pipes[1].Dispose, _pipes[2].Dispose,
                EmitDiagnostics);
        }
    }

    private sealed class PowerShellProbePipe(
        SafeChildHandle parent, SafeChildHandle child)
        : IAcceptancePowerShellProbeOutput, IDisposable
    {
        private bool _parentClosed;
        private bool _childClosed;

        internal bool IsClosed => _parentClosed && _childClosed;
        internal nint ChildValue => child.DangerousGetHandle();

        internal static PowerShellProbePipe Open(bool childReads)
        {
            nint read = 0;
            nint write = 0;
            var attributes = new AcceptanceOwnedObjectAttributes(
                (uint)sizeof(AcceptanceOwnedObjectAttributes), 0, 1);
            if (CreateProbePipe(&read, &write, &attributes, 4096) == 0)
                throw NativeFailure();
            var pipe = childReads
                ? new PowerShellProbePipe(new(write), new(read))
                : new PowerShellProbePipe(new(read), new(write));
            try { pipe.ConfigureInheritance(); return pipe; }
            catch (Exception original)
            {
                AcceptancePowerShellProbeIoPolicy.CleanupAfterFailure(
                    original, pipe.Dispose);
                throw;
            }
        }

        private void ConfigureInheritance()
        {
            if (SetProbeHandleInformation(parent, 1, 0) == 0)
                throw NativeFailure();
            if (SetProbeHandleInformation(child, 1, 1) == 0)
                throw NativeFailure();
        }

        internal void CloseParent()
        {
            parent.CloseChecked();
            _parentClosed = true;
        }

        internal void CloseChild()
        {
            child.CloseChecked();
            _childClosed = true;
        }

        public AcceptancePowerShellProbeRead Read(Span<byte> buffer)
        {
            uint available = 0;
            if (PeekProbePipe(parent, 0, 0, 0, &available, 0) == 0)
                return EndOrFailure();
            if (available == 0)
                return new(AcceptancePowerShellProbeReadKind.Pending);
            uint read = 0;
            fixed (byte* destination = buffer)
                if (ReadProbePipe(parent, destination,
                    Math.Min(available, (uint)buffer.Length), &read, 0) == 0)
                    return EndOrFailure();
            return read == 0
                ? new(AcceptancePowerShellProbeReadKind.End)
                : new(AcceptancePowerShellProbeReadKind.Data, checked((int)read));
        }

        private static AcceptancePowerShellProbeRead EndOrFailure()
        {
            var error = Marshal.GetLastPInvokeError();
            return error == 109
                ? new(AcceptancePowerShellProbeReadKind.End)
                : throw RestrictedOwnerToken.Failure(error);
        }

        public void Dispose() =>
            AcceptanceChildCleanup.Complete(CloseParent, CloseChild);
    }

    private sealed class PowerShellProbeAttributes : SafeHandleZeroOrMinusOneIsInvalid
    {
        private bool _initialized;

        private PowerShellProbeAttributes(nuint size) : base(ownsHandle: true) =>
            SetHandle(Marshal.AllocHGlobal(checked((int)size)));

        internal nint Pointer => DangerousGetHandle();

        internal static PowerShellProbeAttributes Create(nint handles)
        {
            var size = RequiredBytes();
            var attributes = new PowerShellProbeAttributes(size);
            try
            {
                attributes.Initialize(size);
                attributes.SetHandles(handles);
                return attributes;
            }
            catch { attributes.Dispose(); throw; }
        }

        private static nuint RequiredBytes()
        {
            nuint size = 0;
            var success = InitializeProbeAttributes(0, 1, 0, &size);
            var error = Marshal.GetLastPInvokeError();
            if (success != 0 || size is 0 or > 65536)
                throw RestrictedOwnerToken.Failure(13);
            if (error != 122)
                throw RestrictedOwnerToken.Failure(error);
            return size;
        }

        private void Initialize(nuint size)
        {
            if (InitializeProbeAttributes(Pointer, 1, 0, &size) == 0)
                throw NativeFailure();
            _initialized = true;
        }

        private void SetHandles(nint handles)
        {
            const nuint handleList = 0x00020002;
            if (UpdateProbeAttributes(Pointer, 0, handleList,
                handles, (nuint)(3 * sizeof(nint)), 0, 0) == 0)
                throw NativeFailure();
        }

        protected override bool ReleaseHandle()
        {
            // DeleteProcThreadAttributeList returns VOID, not BOOL.
            if (_initialized)
                DeleteProbeAttributes(handle);
            Marshal.FreeHGlobal(handle);
            return true;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreatePipe", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int CreateProbePipe(
        nint* read, nint* write, AcceptanceOwnedObjectAttributes* attributes, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "SetHandleInformation", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetProbeHandleInformation(
        SafeChildHandle handle, uint mask, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "PeekNamedPipe", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int PeekProbePipe(
        SafeChildHandle pipe, nint buffer, uint size, nint read,
        uint* available, nint left);

    [LibraryImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ReadProbePipe(
        SafeChildHandle pipe, byte* buffer, uint count, uint* read, nint overlapped);

    [LibraryImport("kernel32.dll",
        EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int InitializeProbeAttributes(
        nint attributes, uint count, uint flags, nuint* size);

    [LibraryImport("kernel32.dll",
        EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int UpdateProbeAttributes(
        nint attributes, uint flags, nuint attribute, nint value,
        nuint size, nint previous, nint returned);

    [LibraryImport("kernel32.dll", EntryPoint = "DeleteProcThreadAttributeList")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void DeleteProbeAttributes(nint attributes);
}
