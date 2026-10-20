using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Hermes.Servicing;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

public sealed class WindowsPlacementTests
{
    private const string ImageName = "Hermes.Service.dll";
    private static readonly Guid ImageMvid = new("11111111-1111-1111-1111-111111111111");

    public enum CasePlacement { Outside, InstalledAlias, MissingImage }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outside_source_with_missing_root_and_gate_creates_no_installer_directories(
        bool parentExists)
    {
        using var sandbox = new Sandbox();
        var code = sandbox.Image(sandbox.Source);
        if (parentExists)
            Directory.CreateDirectory(sandbox.HermesDirectory);
        Assert.False(Directory.Exists(sandbox.AppDirectory));
        Assert.IsType<OutsideKnownInstallation>(sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.False(Directory.Exists(sandbox.AppDirectory));
        Assert.Equal(parentExists, Directory.Exists(sandbox.HermesDirectory));
        Assert.False(Directory.Exists(sandbox.GateDirectory));
    }

    [Theory]
    [InlineData(CasePlacement.Outside)]
    [InlineData(CasePlacement.InstalledAlias)]
    [InlineData(CasePlacement.MissingImage)]
    public void Case_distinct_loaded_paths_use_each_actual_physical_placement(CasePlacement placement)
    {
        using var sandbox = new Sandbox();
        var code = sandbox.CaseDistinctImages(placement);
        Admission expected = placement switch
        {
            CasePlacement.Outside => new OutsideKnownInstallation(),
            CasePlacement.InstalledAlias => new Refused(RefusalReason.WrongGeneration),
            CasePlacement.MissingImage => new Refused(RefusalReason.Unavailable),
            _ => throw new ArgumentOutOfRangeException(nameof(placement))
        };
        Assert.Equal(expected, sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.False(File.Exists(sandbox.GatePath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_dangling_reserved_root_or_ancestor_is_unavailable_not_outside(bool ancestor)
    {
        using var sandbox = new Sandbox();
        var code = sandbox.Image(sandbox.Source);
        var target = Path.Combine(sandbox.Source, "DeletedTarget");
        Directory.CreateDirectory(target);
        if (!ancestor)
            Directory.CreateDirectory(sandbox.HermesDirectory);
        sandbox.Junction(ancestor ? sandbox.HermesDirectory : sandbox.AppDirectory, target);
        Directory.Delete(target);
        Assert.Equal(new Refused(RefusalReason.Unavailable),
            sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Inside_missing_root_and_gate_is_not_outside()
    {
        using var sandbox = new Sandbox();
        var code = At(Path.Combine(sandbox.AppDirectory, "service", ImageName));
        Assert.Equal(new Refused(RefusalReason.UnownedInstallation),
            sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.False(Directory.Exists(sandbox.HermesDirectory));
    }

    [Fact]
    public void A_source_junction_into_installation_is_not_outside()
    {
        using var sandbox = new Sandbox();
        var directory = Path.Combine(sandbox.AppDirectory, "service");
        sandbox.Image(directory);
        sandbox.Junction(sandbox.Alias, directory);
        var code = At(Path.Combine(sandbox.Alias, ImageName));
        Assert.Equal(new Refused(RefusalReason.WrongGeneration),
            sandbox.Client(code).Enter(RuntimeEntry.Service));
    }

    [Fact]
    public void A_reserved_root_junction_to_source_is_not_outside()
    {
        using var sandbox = new Sandbox();
        var code = sandbox.Image(sandbox.Source);
        Directory.CreateDirectory(sandbox.HermesDirectory);
        sandbox.Junction(sandbox.AppDirectory, sandbox.Source);
        Assert.Equal(new Refused(RefusalReason.WrongGeneration),
            sandbox.Client(code).Enter(RuntimeEntry.Service));
    }

    [Fact]
    public void A_junction_to_a_separate_outside_directory_can_be_positive()
    {
        using var sandbox = new Sandbox();
        sandbox.Image(sandbox.Source);
        sandbox.Junction(sandbox.Alias, sandbox.Source);
        var code = At(Path.Combine(sandbox.Alias, ImageName));
        Assert.IsType<OutsideKnownInstallation>(sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.False(Directory.Exists(sandbox.HermesDirectory));
    }

    [Fact]
    public void An_outside_hard_link_to_installed_bytes_is_ambiguous_not_outside()
    {
        using var sandbox = new Sandbox();
        var installed = sandbox.Image(Path.Combine(sandbox.AppDirectory, "service"));
        Directory.CreateDirectory(sandbox.Source);
        var alias = Path.Combine(sandbox.Source, ImageName);
        Assert.True(CreateHardLink(alias, installed.EntryAssemblyPath, 0),
            $"CreateHardLinkW failed: {Marshal.GetLastWin32Error()}");
        Assert.Equal(new Refused(RefusalReason.InvalidState),
            sandbox.Client(At(alias)).Enter(RuntimeEntry.Service));
    }

    [Fact]
    public void A_missing_outside_image_is_unavailable_not_positive()
    {
        using var sandbox = new Sandbox();
        var code = At(Path.Combine(sandbox.Source, ImageName));
        Assert.Equal(new Refused(RefusalReason.Unavailable),
            sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.False(Directory.Exists(sandbox.HermesDirectory));
    }

    [Fact]
    public void A_malformed_existing_gate_is_not_absence_after_outside_proof()
    {
        using var sandbox = new Sandbox();
        var code = sandbox.Image(sandbox.Source);
        Directory.CreateDirectory(sandbox.GateDirectory);
        byte[] bytes = [.. GateCodec.Header(GateStatus.Ready, 2), .. "{}"u8.ToArray()];
        File.WriteAllBytes(sandbox.GatePath, bytes);
        Assert.Equal(new Refused(RefusalReason.InvalidState),
            sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.Equal(bytes, File.ReadAllBytes(sandbox.GatePath));
    }

    [Fact]
    public void Outside_verified_absence_releases_its_temporary_gate_reader()
    {
        using var sandbox = new Sandbox();
        var code = sandbox.Image(sandbox.Source);
        sandbox.Publish(new GateReadyState(null));
        Assert.IsType<OutsideKnownInstallation>(sandbox.Client(code).Enter(RuntimeEntry.Service));
        using var exclusive = ExclusiveGate.Open(sandbox.GatePath);
    }

    [Fact]
    public void Actual_assembly_object_metadata_can_drive_outside_classification()
    {
        using var sandbox = new Sandbox();
        var code = new CurrentLoadedCode().Read();
        var entry = Assert.IsAssignableFrom<Assembly>(Assembly.GetEntryAssembly());
        var current = typeof(WindowsPlacementTests).Assembly;
        Assert.Equal(entry.ManifestModule.ModuleVersionId, code.EntryModuleVersionId);
        Assert.Contains(code.Images, image =>
            image.AssemblyPath == current.Location &&
            image.ModuleVersionId == current.ManifestModule.ModuleVersionId);
        Assert.IsType<OutsideKnownInstallation>(sandbox.Client(code).Enter(RuntimeEntry.Service));
        Assert.False(Directory.Exists(sandbox.HermesDirectory));
    }

    private static LoadedCode At(string path) =>
        new(path, ImageMvid, [new(path, ImageMvid)]);

    private static void EnableCaseSensitive(string directory)
    {
        const uint writeAttributes = 0x0100;
        const uint backupSemantics = 0x02000000;
        const int caseSensitiveInfo = 23;
        using var handle = OpenDirectory(directory, writeAttributes,
            FileShare.ReadWrite | FileShare.Delete, 0, FileMode.Open, backupSemantics, 0);
        Assert.False(handle.IsInvalid, $"CreateFileW failed: {Marshal.GetLastWin32Error()}");
        byte[] flags = [1, 0, 0, 0]; // FILE_CS_FLAG_CASE_SENSITIVE_DIR.
        Assert.True(WriteInformation(handle, caseSensitiveInfo, flags, (uint)flags.Length),
            $"SetFileInformationByHandle failed: {Marshal.GetLastWin32Error()}");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenDirectory(
        string path, uint desiredAccess, FileShare shareMode, nint securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle",
        ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteInformation(
        SafeFileHandle handle, int informationClass, byte[] information, uint length);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string alias, string existing, nint securityAttributes);

    private sealed record FixedLoaded(LoadedCode Code) : ILoadedCode
    {
        public LoadedCode Read() => Code;
    }

    private sealed class UnexpectedLifetime : ILeaseLifetime
    {
        public IProcessLease Retain(IRuntimeReadLease lease) =>
            throw new InvalidOperationException("Outside/refusal tests must not retain a lease.");
    }

    private sealed class Sandbox : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"hermes-placement-{Guid.NewGuid():N}");
        private readonly List<string> _junctions = [];

        internal string HermesDirectory => Path.Combine(_directory, "Hermes");
        internal string AppDirectory => Path.Combine(HermesDirectory, "App");
        internal string Source => Path.Combine(_directory, "Source");
        internal string Alias => Path.Combine(_directory, "Development Alias");
        internal string GateDirectory => Path.Combine(HermesDirectory, "Servicing",
            HermesServicingProtocol.UpgradeCode.ToString("D"));
        internal string GatePath => Path.Combine(GateDirectory, "admission.bin");

        internal Sandbox() => Directory.CreateDirectory(_directory);

        internal LoadedCode Image(string directory)
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, ImageName);
            File.WriteAllText(path, "Synthetic file; no PE identity claim.");
            return At(path);
        }

        internal LoadedCode CaseDistinctImages(CasePlacement placement)
        {
            var directory = Path.Combine(_directory, "CaseSensitive");
            Directory.CreateDirectory(directory);
            EnableCaseSensitive(directory);
            var upper = Image(Path.Combine(directory, "Build"));
            var lowerDirectory = Path.Combine(directory, "build");
            var lower = placement switch
            {
                CasePlacement.Outside => Image(lowerDirectory),
                CasePlacement.InstalledAlias => JunctionImage(
                    lowerDirectory, Path.Combine(AppDirectory, "service")),
                CasePlacement.MissingImage => At(Path.Combine(lowerDirectory, ImageName)),
                _ => throw new ArgumentOutOfRangeException(nameof(placement))
            };
            return upper with { Images = [upper.Images[0], lower.Images[0]] };
        }

        private LoadedCode JunctionImage(string link, string target)
        {
            Image(target);
            Junction(link, target);
            return At(Path.Combine(link, ImageName));
        }

        internal IStartupAdmission Client(LoadedCode code) =>
            WindowsStartupAdmission.Create(AppDirectory, new WindowsPhysicalPaths(),
                new WindowsRuntimeGate(GatePath), new FixedLoaded(code), new UnexpectedLifetime());

        internal void Publish(GateReadyState state)
        {
            Directory.CreateDirectory(GateDirectory);
            using var writer = ExclusiveGate.Open(GatePath, create: true);
            writer.PublishReady(state);
        }

        internal void Junction(string link, string target)
        {
            Assert.DoesNotContain("%", link, StringComparison.Ordinal);
            Assert.DoesNotContain("%", target, StringComparison.Ordinal);
            var start = new ProcessStartInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                Arguments = $"/d /v:off /s /c \"mklink /J \"{link}\" \"{target}\"\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(start)
                ?? throw new InvalidOperationException("Could not start the junction helper.");
            if (!process.WaitForExit(10_000))
            { process.Kill(entireProcessTree: true); throw new TimeoutException("Junction helper timed out."); }
            Assert.Equal(0, process.ExitCode);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            _junctions.Add(link);
        }

        public void Dispose()
        {
            foreach (var junction in _junctions)
                Directory.Delete(junction);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
