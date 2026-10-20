using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Hermes.Servicing;
using Hermes.Setup;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

public sealed class WindowsPlacementTests
{
    private const string ImageName = "Hermes.Service.dll";
    private static readonly Guid ImageMvid = new("11111111-1111-1111-1111-111111111111");

    public enum CasePlacement { Outside, InstalledAlias, MissingImage }

    [Theory]
    [InlineData("")]
    [InlineData("Hermes")]
    [InlineData("Programs/Hermes")]
    public void Native_shortcut_missing_file_or_ancestors_are_proven_without_creation(
        string relativeDirectory)
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Source);
        var path = Path.Combine(sandbox.Source, relativeDirectory, "Hermes.lnk");
        Assert.Equal(ReadObservation<ShortcutFields>.Missing(),
            new WindowsShellLinkQuery().Read(path));
        Assert.Empty(Directory.GetFileSystemEntries(sandbox.Source));
    }

    [Fact]
    public void Native_missing_ancestor_load_exposes_managed_path_not_found()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Source);
        var path = Path.Combine(sandbox.Source, "Hermes", "Hermes.lnk");
        var error = Record.Exception(() => new WindowsShellLinkLoader().Load(path));
        Assert.True(error is DirectoryNotFoundException);
        Assert.Equal(0x80070003u, unchecked((uint)(error?.HResult ?? 0)));
        Assert.Equal(ReadObservation<ShortcutFields>.Missing(),
            new WindowsShellLinkQuery().Read(path));
        Assert.Empty(Directory.GetFileSystemEntries(sandbox.Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_redirected_or_dangling_shortcut_ancestor_is_not_absence(bool dangling)
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Source);
        sandbox.Junction(sandbox.Alias, sandbox.Source);
        if (dangling)
            Directory.Delete(sandbox.Source);
        var path = Path.Combine(sandbox.Alias, "Hermes", "Hermes.lnk");
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(ObservationFault.Identity),
            new WindowsShellLinkQuery().Read(path));
    }

    [Fact]
    public void Native_shortcut_directory_collision_is_not_absence()
    {
        using var sandbox = new Sandbox();
        var path = Path.Combine(sandbox.Source, "Hermes.lnk");
        Directory.CreateDirectory(path);
        Assert.Equal(ReadObservation<ShortcutFields>.Unknown(ObservationFault.Identity),
            new WindowsShellLinkQuery().Read(path));
        Assert.Empty(Directory.GetFileSystemEntries(path));
    }

    [Fact]
    public void Native_malformed_shortcut_is_not_absence()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Source);
        var path = Path.Combine(sandbox.Source, "Hermes.lnk");
        byte[] bytes = [0x01, 0x02, 0x03];
        File.WriteAllBytes(path, bytes);
        var observed = new WindowsShellLinkQuery().Read(path);
        Assert.Equal(ObservationState.Unknown, observed.State);
        Assert.Equal(ObservationFault.Unreadable, observed.Fault);
        Assert.NotEqual(0u, observed.NativeCode);
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Native_exclusively_held_shortcut_is_not_absence()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Source);
        var path = Path.Combine(sandbox.Source, "Hermes.lnk");
        using var held = new FileStream(path, FileMode.CreateNew,
            FileAccess.ReadWrite, FileShare.None);
        held.Write([0x01, 0x02, 0x03]);
        held.Flush(flushToDisk: true);
        var observed = new WindowsShellLinkQuery().Read(path);
        Assert.Equal(ObservationState.Unknown, observed.State);
        Assert.Contains(observed.Fault,
            new[] { ObservationFault.Identity, ObservationFault.Unreadable });
    }

    [Fact]
    public void Native_present_shortcut_retains_its_fields_without_resolving_or_running_target()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Source);
        var target = Path.Combine(sandbox.Source, "tray.exe");
        File.WriteAllText(target, "Synthetic target; never executed.");
        var expected = new ShortcutFields(target, "--synthetic", sandbox.Source);
        var path = Path.Combine(sandbox.Source, "Hermes.lnk");
        SaveShellLink(path, expected);
        Assert.Equal(ShortcutComparison.Matched, ShortcutClassifier.Compare(
            new WindowsShellLinkQuery().Read(path), expected, new WindowsPathIdentityQuery()));
    }

    private static void SaveShellLink(string path, ShortcutFields fields)
    {
        var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)
            ?? throw new InvalidOperationException();
        var instance = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException();
        try
        {
            if (instance is not IWindowsShellLink link || instance is not IPersistFile file)
                throw new InvalidOperationException();
            link.SetPath(fields.Target);
            link.SetArguments(fields.Arguments);
            link.SetWorkingDirectory(fields.WorkingDirectory);
            file.Save(path, true);
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    [Fact]
    public void Production_current_user_composition_resolves_without_opening_profile_files()
    {
        var capture = new StartupRefusalEvidenceCapture();
        Assert.IsType<FixedRootStartupAdmission>(
            WindowsStartupAdmission.ForCurrentUser(capture));
        Assert.Null(capture.Evidence);
        Assert.IsType<FixedRootStartupAdmission>(WindowsStartupAdmission.ForCurrentUser());
    }

    [Fact]
    public void Native_gate_directory_denial_records_hresult_without_path_content()
    {
        using var sandbox = new Sandbox();
        Directory.CreateDirectory(sandbox.Source);
        var capture = new StartupRefusalEvidenceCapture();
        var gate = new WindowsRuntimeGate(sandbox.Source, capture);
        Assert.Equal(new GateUnavailable(RefusalReason.Unavailable), gate.TryOpen());
        var evidence = Assert.IsType<StartupRefusalEvidence>(capture.Evidence);
        Assert.Equal(new StartupRefusalEvidence(
            StartupEvidenceStage.Gate, StartupEvidenceOperation.OpenGate,
            StartupEvidenceFailure.ManagedException,
            StartupEvidenceErrorKind.HResult, unchecked((int)0x80070005)), evidence);
        Assert.DoesNotContain(sandbox.Source, evidence.ToDiagnosticLine());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Outside_source_with_missing_root_and_gate_creates_no_installer_directories(
        bool parentExists)
    {
        using var sandbox = new Sandbox();
        var code = sandbox.Image(sandbox.Source);
        var capture = new StartupRefusalEvidenceCapture();
        if (parentExists)
            Directory.CreateDirectory(sandbox.HermesDirectory);
        Assert.False(Directory.Exists(sandbox.AppDirectory));
        Assert.IsType<OutsideKnownInstallation>(
            sandbox.Client(code, capture).Enter(RuntimeEntry.Service));
        Assert.Null(capture.Evidence);
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
        var capture = new StartupRefusalEvidenceCapture();
        var target = Path.Combine(sandbox.Source, "DeletedTarget");
        Directory.CreateDirectory(target);
        if (!ancestor)
            Directory.CreateDirectory(sandbox.HermesDirectory);
        sandbox.Junction(ancestor ? sandbox.HermesDirectory : sandbox.AppDirectory, target);
        Directory.Delete(target);
        Assert.Equal(new Refused(RefusalReason.Unavailable),
            sandbox.Client(code, capture).Enter(RuntimeEntry.Service));
        Assert.Equal(new StartupRefusalEvidence(
            StartupEvidenceStage.ReservedRoot, StartupEvidenceOperation.OpenEntry,
            StartupEvidenceFailure.ExistingEntry, Position: ancestor ? 1 : 0), capture.Evidence);
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
    public async Task An_outside_hard_link_to_installed_bytes_is_ambiguous_not_outside()
    {
        using var sandbox = new Sandbox();
        var installed = sandbox.Image(Path.Combine(sandbox.AppDirectory, "service"));
        Directory.CreateDirectory(sandbox.Source);
        var alias = Path.Combine(sandbox.Source, ImageName);
        Assert.True(CreateHardLink(alias, installed.EntryAssemblyPath, 0),
            $"CreateHardLinkW failed: {Marshal.GetLastWin32Error()}");
        Assert.Equal(new Refused(RefusalReason.InvalidState),
            sandbox.Client(At(alias)).Enter(RuntimeEntry.Service));
        var error = Assert.Throws<AdmissionException>(() =>
            new WindowsPhysicalPaths().Image(alias));
        Assert.Equal(AdmissionInvariant.ImageLinkCountNotOne, error.Invariant);
        await AssertManagedSetupEvidenceAsync(error, SetupEvidenceCause.ImageLinkCountNotOne);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_file_type_collisions_retain_their_managed_source_point(bool image)
    {
        using var sandbox = new Sandbox();
        if (image)
            Directory.CreateDirectory(sandbox.Source);
        else
            File.WriteAllText(sandbox.Source, "Synthetic file; never executed.");
        var physical = new WindowsPhysicalPaths();
        Func<string> read = image
            ? () => physical.Image(sandbox.Source) : () => physical.Directory(sandbox.Source);
        var error = Assert.Throws<AdmissionException>(() => read());
        Assert.Equal(image ? AdmissionInvariant.ImageIsDirectory
            : AdmissionInvariant.DirectoryIsNotDirectory, error.Invariant);
        await AssertManagedSetupEvidenceAsync(error, image
            ? SetupEvidenceCause.ImageIsDirectory : SetupEvidenceCause.DirectoryIsNotDirectory);
    }

    [Fact]
    public async Task Native_missing_ancestor_bound_does_not_become_positive_absence()
    {
        using var sandbox = new Sandbox();
        var deep = Path.Combine(
            new[] { sandbox.Source }.Concat(Enumerable.Repeat("d", 66)).ToArray());
        var error = Assert.Throws<AdmissionException>(() =>
            new WindowsPhysicalPaths().Directory(deep));
        Assert.Equal(AdmissionInvariant.AncestorDepthExceeded, error.Invariant);
        await AssertManagedSetupEvidenceAsync(error, SetupEvidenceCause.AncestorDepthExceeded);
        Assert.False(Directory.Exists(sandbox.Source));
        Assert.False(Directory.Exists(sandbox.HermesDirectory));
    }

    [Fact]
    public async Task A_missing_outside_image_is_unavailable_not_positive()
    {
        using var sandbox = new Sandbox();
        var capture = new StartupRefusalEvidenceCapture();
        var code = At(Path.Combine(sandbox.Source, ImageName));
        Assert.Equal(new Refused(RefusalReason.Unavailable),
            sandbox.Client(code, capture).Enter(RuntimeEntry.Service));
        var evidence = Assert.IsType<StartupRefusalEvidence>(capture.Evidence);
        Assert.Equal(StartupEvidenceStage.LoadedImage, evidence.Stage);
        Assert.Equal(StartupEvidenceOperation.OpenFollowing, evidence.Operation);
        Assert.Equal(StartupEvidenceFailure.NativeCallFailed, evidence.Failure);
        Assert.Equal(StartupEvidenceErrorKind.Win32, evidence.ErrorKind);
        Assert.Contains(evidence.ErrorCode, new[] { 2, 3 });
        Assert.Equal(0, evidence.Position);
        Assert.False(Directory.Exists(sandbox.HermesDirectory));
        await AssertNativeSetupEvidenceAsync(evidence);
    }

    private static async Task AssertManagedSetupEvidenceAsync(
        AdmissionException error, SetupEvidenceCause cause)
    {
        Assert.Equal(RefusalReason.InvalidState, error.Reason);
        Assert.Equal(new Exception().HResult, error.HResult);
        Assert.Null(error.Evidence);
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        var exit = await SetupEntryPoint.NativeRefusalAsync(
            ["recover"], output, diagnostics, error.Evidence, error.HResult, error.Invariant);
        Assert.Equal(1603, exit);
        Assert.Equal(SetupExecution.Refused(SetupVerb.Recover, SetupCode.Unavailable)
            .Report.ToJson() + Environment.NewLine, output.ToString());
        var expected = new SetupRefusalEvidence(SetupEvidenceStage.Entry, cause);
        Assert.Equal("Hermes Setup: Unavailable." + Environment.NewLine +
            expected.ToDiagnosticLine() + Environment.NewLine, diagnostics.ToString());
    }

    private static async Task AssertNativeSetupEvidenceAsync(StartupRefusalEvidence evidence)
    {
        using var output = new StringWriter();
        using var diagnostics = new StringWriter();
        var source = Path.Combine(Path.GetTempPath(), "not-opened.msi");
        var exit = await SetupEntryPoint.NativeRefusalAsync(
            ["install", "--package", source], output, diagnostics, evidence, 0);
        Assert.Equal(1603, exit);
        Assert.Equal(SetupExecution.Refused(SetupVerb.Install, SetupCode.Unavailable)
            .Report.ToJson() + Environment.NewLine, output.ToString());
        var expected = new SetupRefusalEvidence(
            SetupEvidenceStage.Entry, SetupEvidenceCause.ExpectedException,
            ErrorKind: evidence.ErrorKind, ErrorCode: evidence.ErrorCode);
        Assert.Equal("Hermes Setup: Unavailable." + Environment.NewLine +
            expected.ToDiagnosticLine() + Environment.NewLine, diagnostics.ToString());
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

        internal IStartupAdmission Client(
            LoadedCode code, StartupRefusalEvidenceCapture? evidence = null) =>
            WindowsStartupAdmission.Create(AppDirectory, new WindowsPhysicalPaths(),
                new WindowsRuntimeGate(GatePath, evidence),
                new FixedLoaded(code), new UnexpectedLifetime(), evidence);

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
