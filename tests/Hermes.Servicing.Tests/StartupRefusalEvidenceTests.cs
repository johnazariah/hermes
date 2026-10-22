using Hermes.Servicing;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class StartupRefusalEvidenceTests
{
    private const string PrivateDetail = "synthetic-private-detail-not-for-output";
    private static readonly string Profile = Path.GetFullPath("synthetic-current-user");
    private static readonly string Source = Path.GetFullPath("synthetic-current-source");
    private static readonly string Root = Path.Combine(Profile, "Hermes", "App");
    private static readonly Guid Mvid = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void Diagnostic_is_numeric_single_line_and_bounded_even_for_unknown_values()
    {
        var evidence = new StartupRefusalEvidence(
            (StartupEvidenceStage)int.MinValue, (StartupEvidenceOperation)int.MaxValue,
            (StartupEvidenceFailure)int.MinValue, (StartupEvidenceErrorKind)int.MaxValue,
            int.MinValue, int.MaxValue);
        var line = evidence.ToDiagnosticLine();
        Assert.Equal(
            "HERMES_STARTUP_EVIDENCE v=1 stage=-2147483648 operation=2147483647 failure=-2147483648 errorKind=2147483647 error=-2147483648 position=2147483647",
            line);
        Assert.True(line.Length <= StartupRefusalEvidence.MaximumDiagnosticLength);
        Assert.DoesNotContain('\r', line);
        Assert.DoesNotContain('\n', line);
    }

    [Fact]
    public void Capture_retains_only_the_first_failure()
    {
        var capture = new StartupRefusalEvidenceCapture();
        var first = EmptyRoot();
        capture.Record(first);
        capture.Record(first with { Failure = StartupEvidenceFailure.ManagedException });
        Assert.Same(first, capture.Evidence);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Empty_current_user_root_preserves_exception_and_specific_evidence(string root)
    {
        var capture = new StartupRefusalEvidenceCapture();
        var platform = PlatformFor(() => root);
        Assert.Throws<InvalidOperationException>(() =>
            WindowsStartupAdmission.CreateCurrentUser(platform, capture));
        Assert.Equal(EmptyRoot(), capture.Evidence);
        Assert.Null(platform.GatePath);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Known_folder_exceptions_keep_identity_and_do_not_disclose_detail(int kind)
    {
        var error = ManagedFailure(kind);
        var capture = new StartupRefusalEvidenceCapture();
        var platform = PlatformFor(() => throw error);
        Assert.Same(error, Record.Exception(() =>
            WindowsStartupAdmission.CreateCurrentUser(platform, capture)));
        Assert.Equal(new StartupRefusalEvidence(
            StartupEvidenceStage.Composition, StartupEvidenceOperation.KnownFolder,
            StartupEvidenceFailure.ManagedException,
            StartupEvidenceErrorKind.HResult, error.HResult), capture.Evidence);
        Assert.DoesNotContain(PrivateDetail, Evidence(capture).ToDiagnosticLine());
        Assert.Null(platform.GatePath);
    }

    [Fact]
    public void Later_composition_failure_is_not_mislabelled_as_known_folder_failure()
    {
        var error = new InvalidOperationException(PrivateDetail);
        var capture = new StartupRefusalEvidenceCapture();
        var platform = PlatformFor(() => Profile, gateFailure: error);
        Assert.Same(error, Record.Exception(() =>
            WindowsStartupAdmission.CreateCurrentUser(platform, capture)));
        Assert.Equal(new StartupRefusalEvidence(
            StartupEvidenceStage.Composition, StartupEvidenceOperation.Compose,
            StartupEvidenceFailure.ManagedException,
            StartupEvidenceErrorKind.HResult, error.HResult), capture.Evidence);
    }

    [Fact]
    public void Unhandled_composition_exception_is_not_reclassified()
    {
        var error = new FormatException(PrivateDetail);
        var capture = new StartupRefusalEvidenceCapture();
        Assert.Same(error, Record.Exception(() =>
            WindowsStartupAdmission.CreateCurrentUser(PlatformFor(() => throw error), capture)));
        Assert.Null(capture.Evidence);
    }

    [Fact]
    public void Current_user_composition_uses_resolved_root_and_preserves_positive_outside_route()
    {
        var capture = new StartupRefusalEvidenceCapture();
        var paths = new RecordingPaths();
        var gate = new CountingGate(new GateUnavailable(RefusalReason.UnownedInstallation));
        var platform = PlatformFor(() => Profile, paths: paths, gate: gate);
        var client = WindowsStartupAdmission.CreateCurrentUser(platform, capture);
        Assert.Equal(Path.Combine(Profile, "Hermes", "Servicing",
            HermesServicingProtocol.UpgradeCode.ToString("D"), "admission.bin"), platform.GatePath);
        Assert.Equal(0, gate.Opens);
        Assert.Empty(paths.Reads);
        Assert.IsType<OutsideKnownInstallation>(client.Enter(RuntimeEntry.Service));
        Assert.Equal(new[] { Root, Entry(Source), Library(Source) }, paths.Reads);
        Assert.Equal(1, gate.Opens);
        Assert.Null(capture.Evidence);
    }

    [Fact]
    public void Evidence_capture_is_optional_for_current_user_composition()
    {
        var client = WindowsStartupAdmission.CreateCurrentUser(PlatformFor(() => Profile), null);
        Assert.IsType<OutsideKnownInstallation>(client.Enter(RuntimeEntry.Service));
    }

    [Fact]
    public void Installed_closed_gate_and_unsupported_role_still_refuse_without_physical_reads()
    {
        var capture = new StartupRefusalEvidenceCapture();
        var paths = new RecordingPaths();
        var gate = new CountingGate(new GateUnavailable(RefusalReason.Closed));
        var platform = PlatformFor(() => Profile, paths, gate, code: Code(Path.Combine(Root, "service")));
        var client = WindowsStartupAdmission.CreateCurrentUser(platform, capture);
        Assert.Equal(new Refused(RefusalReason.UnsupportedProtocol), client.Enter((RuntimeEntry)999));
        Assert.Equal(0, gate.Opens);
        Assert.Equal(new Refused(RefusalReason.Closed), client.Enter(RuntimeEntry.Service));
        Assert.Equal(1, gate.Opens);
        Assert.Empty(paths.Reads);
        Assert.Null(capture.Evidence);
    }

    [Fact]
    public void Loaded_image_failure_keeps_exact_ordinal_and_precedes_gate_open()
    {
        var capture = new StartupRefusalEvidenceCapture();
        var original = new StartupRefusalEvidence(
            StartupEvidenceStage.LoadedImage, StartupEvidenceOperation.StandardInformation,
            StartupEvidenceFailure.NativeCallFailed, StartupEvidenceErrorKind.Win32, 5, 99);
        var paths = new RecordingPaths(path => path == Library(Source)
            ? throw new AdmissionException(RefusalReason.Unavailable, original) : path);
        var gate = new CountingGate(new GateUnavailable(RefusalReason.UnownedInstallation));
        var client = WindowsStartupAdmission.CreateCurrentUser(
            PlatformFor(() => Profile, paths, gate), capture);
        Assert.Equal(new Refused(RefusalReason.Unavailable), client.Enter(RuntimeEntry.Service));
        Assert.Equal(original with { Position = 1 }, capture.Evidence);
        Assert.Equal(0, gate.Opens);
    }

    [Fact]
    public void Ordinary_physical_exception_still_maps_to_invalid_state_without_invented_evidence()
    {
        var capture = new StartupRefusalEvidenceCapture();
        var paths = new RecordingPaths(_ => throw new IOException(PrivateDetail));
        var client = WindowsStartupAdmission.CreateCurrentUser(
            PlatformFor(() => Profile, paths), capture);
        Assert.Equal(new Refused(RefusalReason.InvalidState), client.Enter(RuntimeEntry.Service));
        Assert.Null(capture.Evidence);
    }

    [Theory]
    [InlineData(2, RefusalReason.UnownedInstallation)]
    [InlineData(3, RefusalReason.UnownedInstallation)]
    [InlineData(32, RefusalReason.Busy)]
    [InlineData(33, RefusalReason.Busy)]
    [InlineData(5, RefusalReason.Unavailable)]
    [InlineData(123, RefusalReason.Unavailable)]
    public void Gate_mapping_is_unchanged_and_only_unavailable_captures(int code, RefusalReason reason)
    {
        var error = new IOException(PrivateDetail, unchecked((int)0x80070000) | code);
        var capture = new StartupRefusalEvidenceCapture();
        Assert.Equal(new GateUnavailable(reason), RuntimeGateFailures.From(error, capture));
        if (reason == RefusalReason.Unavailable)
            Assert.Equal(GateEvidence(error), capture.Evidence);
        else
            Assert.Null(capture.Evidence);
        Assert.Equal(new GateUnavailable(reason), RuntimeGateFailures.From(error, null));
    }

    [Fact]
    public void Gate_access_denial_records_only_hresult()
    {
        var error = new UnauthorizedAccessException(PrivateDetail);
        var capture = new StartupRefusalEvidenceCapture();
        Assert.Equal(new GateUnavailable(RefusalReason.Unavailable),
            RuntimeGateFailures.From(error, capture));
        Assert.Equal(GateEvidence(error), capture.Evidence);
        Assert.DoesNotContain(PrivateDetail, Evidence(capture).ToDiagnosticLine());
    }

    [Theory]
    [InlineData(AdmissionInvariant.CanonicalRootInvalid)]
    [InlineData(AdmissionInvariant.ImageIsDirectory)]
    [InlineData(AdmissionInvariant.ImageLinkCountNotOne)]
    [InlineData(AdmissionInvariant.AncestorDepthExceeded)]
    [InlineData(AdmissionInvariant.DirectoryIsNotDirectory)]
    [InlineData(AdmissionInvariant.FinalPathPrefixUnsupported)]
    [InlineData(AdmissionInvariant.ReadyBindingMissing)]
    [InlineData(AdmissionInvariant.ReadyInventoryInvalid)]
    [InlineData(AdmissionInvariant.ReadyBodyTooLarge)]
    public void Managed_metadata_does_not_change_placement_or_runtime_catches(object source)
    {
        var error = new AdmissionException(RefusalReason.InvalidState,
            invariant: Assert.IsType<AdmissionInvariant>(source));
        var paths = new RecordingPaths(_ => throw error);
        var gate = new CountingGate(new GateUnavailable(RefusalReason.UnownedInstallation));
        var capture = new StartupRefusalEvidenceCapture();
        Assert.Same(error, Record.Exception(() =>
            new FixedRootPlacement(Root, paths).Classify(Code(Source))));
        var client = WindowsStartupAdmission.CreateCurrentUser(
            PlatformFor(() => Profile, paths, gate), capture);
        Assert.Equal(new Refused(RefusalReason.InvalidState), client.Enter(RuntimeEntry.Service));
        Assert.False(StartupAdmission.IsStateFailure(error));
        Assert.Equal(0, gate.Opens);
        Assert.Null(capture.Evidence);
    }

    private static StartupRefusalEvidence EmptyRoot() => new(
        StartupEvidenceStage.Composition, StartupEvidenceOperation.KnownFolder,
        StartupEvidenceFailure.EmptyKnownFolder);

    private static StartupRefusalEvidence GateEvidence(Exception error) => new(
        StartupEvidenceStage.Gate, StartupEvidenceOperation.OpenGate,
        StartupEvidenceFailure.ManagedException, StartupEvidenceErrorKind.HResult, error.HResult);

    private static StartupRefusalEvidence Evidence(StartupRefusalEvidenceCapture capture) =>
        Assert.IsType<StartupRefusalEvidence>(capture.Evidence);

    private static Exception ManagedFailure(int kind) => kind switch
    {
        0 => new IOException(PrivateDetail),
        1 => new UnauthorizedAccessException(PrivateDetail),
        2 => new InvalidOperationException(PrivateDetail),
        3 => new ArgumentException(PrivateDetail),
        _ => new NotSupportedException(PrivateDetail)
    };

    private static string Entry(string directory) => Path.Combine(directory, "Hermes.Service.dll");
    private static string Library(string directory) => Path.Combine(directory, "Hermes.Servicing.dll");

    private static LoadedCode Code(string directory) =>
        new(Entry(directory), Mvid, [new(Entry(directory), Mvid), new(Library(directory), Mvid)]);

    private static Platform PlatformFor(
        Func<string> root, IPhysicalPaths? paths = null, IRuntimeGate? gate = null,
        Exception? gateFailure = null, LoadedCode? code = null) =>
        new(root, paths ?? new RecordingPaths(),
            gate ?? new CountingGate(new GateUnavailable(RefusalReason.UnownedInstallation)),
            code ?? Code(Source), gateFailure);

    private sealed class Platform(
        Func<string> root, IPhysicalPaths paths, IRuntimeGate gate,
        LoadedCode code, Exception? gateFailure) : ICurrentUserStartup
    {
        private string? _gatePath;
        internal string? GatePath => _gatePath;
        public string LocalApplicationData() => root();
        public IPhysicalPaths Physical() => paths;
        public ILoadedCode Loaded() => new FixedCode(code);
        public ILeaseLifetime Lifetime() => new UnexpectedLifetime();

        public IRuntimeGate Gate(string path, StartupRefusalEvidenceCapture? evidence)
        {
            _gatePath = path;
            return gateFailure is { } error ? throw error : gate;
        }
    }

    private sealed record FixedCode(LoadedCode Code) : ILoadedCode
    {
        public LoadedCode Read() => Code;
    }

    private sealed class CountingGate(GateOpen result) : IRuntimeGate
    {
        private int _opens;
        internal int Opens => _opens;

        public GateOpen TryOpen()
        {
            _opens++;
            return result;
        }
    }

    private sealed class RecordingPaths(Func<string, string>? image = null) : IPhysicalPaths
    {
        private readonly List<string> _reads = [];
        internal IReadOnlyList<string> Reads => _reads;

        public string Directory(string path)
        {
            _reads.Add(path);
            return path;
        }

        public string Image(string path)
        {
            _reads.Add(path);
            return image is { } resolve ? resolve(path) : path;
        }
    }

    private sealed class UnexpectedLifetime : ILeaseLifetime
    {
        public IProcessLease Retain(IRuntimeReadLease lease) =>
            throw new InvalidOperationException("Outside/refused admission must not retain.");
    }
}
