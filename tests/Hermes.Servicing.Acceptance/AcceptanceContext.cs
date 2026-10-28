namespace Hermes.Servicing.Acceptance;

internal sealed record AcceptanceOptions(
    string OldMsi,
    string NewMsi,
    string SetupExecutable,
    string Guard,
    string ProbeOld,
    string ProbeNew,
    string OwnerReceipt,
    string Archive,
    string Work)
{
    internal string RequestPath => Path.Combine(Work, "request.json");
    internal string ReportPath => Path.Combine(Work, "actual-servicing.json");

    internal static AcceptanceOptions Parse(string[] arguments)
    {
        string[] names = ["--old-msi", "--new-msi", "--setup", "--guard",
            "--probe-old", "--probe-new", "--owner-fixture-receipt", "--archive", "--work"];
        var pairs = arguments.Chunk(2).Select(Pair).ToImmutableDictionary();
        Require.That(pairs.Count == names.Length && names.All(pairs.ContainsKey));
        string Get(string key) => InstalledSmokeSettings.Absolute(pairs[key]);
        return new(Get(names[0]), Get(names[1]), Get(names[2]), Get(names[3]),
            Get(names[4]), Get(names[5]), Get(names[6]), Get(names[7]), Get(names[8]));
    }

    private static KeyValuePair<string, string> Pair(string[] pair) => pair switch
    {
        [var key, var value] when !string.IsNullOrWhiteSpace(value) => new(key, value),
        _ => throw new ArgumentException("Expected diagnostic argument pairs.")
    };
}

internal sealed record AcceptanceContext(
    AcceptanceOptions Options,
    FixtureJob Job,
    OwnerFixtureReceipt Owner,
    ImmutableArray<Asset> Inputs)
{
    internal static AcceptanceContext Create(AcceptanceOptions options)
    {
        AdmitEnvironment();
        var owner = OwnerFixture.RequireAdmission(options.OwnerReceipt)
            ?? throw new InvalidDataException("A job-bound owner fixture is required.");
        TemporaryPaths.Require(options.Archive, directory: true);
        TemporaryPaths.RequireNewFile(options.Work);
        new WindowsOwnerFixturePublisher().CreateStage(options.Work);
        var context = new AcceptanceContext(options, FixtureJob.Current(),
            owner, BindInputs(options));
        context.Verify();
        EvidenceJson.WriteNew(options.RequestPath, context);
        return context;
    }

    internal static AcceptanceContext Read(string path)
    {
        TemporaryPaths.Require(path);
        var context = EvidenceJson.Read<AcceptanceContext>(path, 64 * 1024);
        Require.That(InstalledSource.SamePath(path, context.Options.RequestPath));
        context.Verify();
        return context;
    }

    internal void Verify()
    {
        AdmitEnvironment();
        Require.That(Job == FixtureJob.Current());
        OwnerFixture.RequireExpected(Options.OwnerReceipt, Owner);
        TemporaryPaths.Require(Options.Work, directory: true);
        Require.That(Inputs.All(asset => Files.Hash(asset.Path) == asset.Sha256));
        RequireSetupAssembly();
        _ = ApprovedGuard();
    }

    internal ApprovedGuardProfile ApprovedGuard()
    {
        var metadata = typeof(WindowsSetupFactory).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Select(value => new KeyValuePair<string, string?>(value.Key, value.Value))
            .ToImmutableArray();
        var approved = AggregateResult.Require(CompiledGuardTrust.Read(metadata));
        var hashes = metadata.Where(value => value.Key == CompiledGuardTrust.MetadataName)
            .SelectMany(value => (value.Value ?? "").Split(';'));
        Require.That(hashes.Contains(Files.Hash(Options.Guard), StringComparer.Ordinal));
        return approved;
    }

    private void RequireSetupAssembly()
    {
        var distributed = Path.Combine(
            Path.GetDirectoryName(Options.SetupExecutable)
                ?? throw new InvalidDataException(), "Hermes.Setup.dll");
        Require.That(Files.Hash(distributed) ==
            Files.Hash(typeof(WindowsSetupFactory).Assembly.Location));
    }

    private static void AdmitEnvironment()
    {
        CiAdmission.Read(cleanOwner: true).Require();
        Require.That(string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("HERMES_CONFIG_DIR")));
        Require.That(string.IsNullOrEmpty(
            Environment.GetEnvironmentVariable("HERMES_PORT")));
    }

    private static ImmutableArray<Asset> BindInputs(AcceptanceOptions options) =>
        new[]
        {
            options.OldMsi, options.NewMsi, options.SetupExecutable, options.Guard,
            Path.Combine(options.ProbeOld, "Hermes.Service.dll"),
            Path.Combine(options.ProbeNew, "Hermes.Service.dll"),
            typeof(WindowsSetupFactory).Assembly.Location
        }.Select(Files.Bind).ToImmutableArray();

    internal ImmutableArray<FileEvidence> OwnerUnknown()
    {
        OwnerFixture.RequireExpected(Options.OwnerReceipt, Owner);
        var owner = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "hermes");
        var app = WindowsSetupScope.ForCurrentUser().AppDirectory;
        return new[]
        {
            Path.Combine(owner, "config.yaml"),
            Path.Combine(owner, "synthetic-settings.txt"),
            Path.Combine(owner, OwnerFixtureStore.MarkerName),
            Options.OwnerReceipt,
            Path.Combine(Options.Archive, "source.txt"),
            Path.Combine(app, "user-data", "keep.txt"),
            Path.Combine(app, "service", "unknown-user-file.txt"),
            Path.Combine(app, "tray", "user-data", "keep.txt")
        }.Select(CaptureRequired).ToImmutableArray();
    }

    private static FileEvidence CaptureRequired(string path)
    {
        TemporaryPaths.RequirePlainDirectory(
            Path.GetDirectoryName(path) ?? throw new InvalidDataException());
        var evidence = Files.Capture(path);
        Require.That(evidence.Exists);
        return evidence;
    }
}
