using System.Collections.Immutable;
using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;

namespace Hermes.Setup.Tests;

internal enum DiskEvent { Flush, Commit }

internal sealed class IoFixture : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "Hermes.Setup.Tests", Guid.NewGuid().ToString("N"));

    internal IoFixture()
    {
        Directory.CreateDirectory(_root);
        Scope = UserInstallationScope.FromKnownFolder(Path.Combine(_root, "profile"));
        Files = new RecordingFiles();
        Commits = new RecordingCommits(Files.Events);
        Disk = new(Files, new TestPaths(_root), Commits);
        Directory.CreateDirectory(Path.Combine(_root, "media"));
    }

    internal UserInstallationScope Scope { get; }
    internal RecordingFiles Files { get; }
    internal RecordingCommits Commits { get; }
    internal SetupDisk Disk { get; }
    internal string Source => Path.Combine(_root, "media", "Hermes source.msi");

    internal PackageEvidence Evidence(byte[] source, byte[] manifest)
    {
        File.WriteAllBytes(Source, source);
        return new(new(HermesServicingProtocol.UpgradeCode, Guid.NewGuid(), Guid.NewGuid(),
                new Version(1, 0, 0), HermesServicingProtocol.AdmissionAbi, PackageScope.PerUser),
            Scope.AppDirectory, Source, Source + ".manifest",
            PackageMediaReader.Hash(source), PackageMediaReader.Hash(manifest),
            [new("service/Hermes.Service.dll", Guid.NewGuid()),
             new("tray/Hermes.Tray.dll", Guid.NewGuid())]);
    }

    internal OperationJournal Journal(PackageEvidence evidence)
    {
        var media = new RetainedMedia(evidence,
            MediaCachePaths.Package(Scope, evidence.PackageSha256),
            MediaCachePaths.Manifest(Scope, evidence.PackageSha256));
        var target = new PresentTarget(evidence);
        var step = new EngineStep(MaintenanceKind.Install, evidence, [], false, true);
        var plan = new OperationPlan(SetupVerb.Install, Scope,
            AbsentTarget.Instance, target, [step], null);
        return new(Guid.NewGuid(), Guid.NewGuid(), [], plan, [media], []);
    }

    internal PackageMedia Media(PackageEvidence evidence, byte[] bytes)
    {
        var files = evidence.Images.Select((image, index) => new PayloadFile(
            image.RelativePath, new string('a', 64), index + 1, image.ModuleVersionId,
            "File" + index, "Component" + index, Guid.NewGuid())).ToImmutableArray();
        var manifest = new PayloadManifest(1, 1, PackageMediaReader.RootToken,
            PackageMediaReader.ScopeToken, evidence.Identity.UpgradeCode,
            evidence.Identity.ProductCode, evidence.Identity.PackageCode,
            evidence.Identity.Version, files);
        return new(evidence, manifest, bytes.ToImmutableArray());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    internal sealed class RecordingFiles : ISetupFileSystem
    {
        private readonly PhysicalSetupFileSystem _inner = new();
        internal List<DiskEvent> Events { get; } = [];
        public DiskEntry Entry(string path) => _inner.Entry(path);
        public IReadOnlyList<string> Children(string path) => _inner.Children(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public FileStream Open(string path, FileMode mode, FileAccess access, FileShare share) =>
            _inner.Open(path, mode, access, share);
        public void DeleteFile(string path) => _inner.DeleteFile(path);

        public void Flush(FileStream stream)
        {
            _inner.Flush(stream);
            Events.Add(DiskEvent.Flush);
        }
    }

    internal sealed class RecordingCommits(List<DiskEvent> events) : IAtomicFileCommit
    {
        public void Commit(string temporary, string destination, bool overwrite)
        {
            events.Add(DiskEvent.Commit);
            File.Move(temporary, destination, overwrite);
        }
    }

    private sealed class TestPaths(string root) : ISetupPathSafety
    {
        public void RequireDirectory(string path) => RequireOwned(path);
        public void RequireFile(string path) => RequireOwned(path);

        private void RequireOwned(string path) =>
            SetupIo.Require(Path.GetFullPath(path).StartsWith(
                root + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                SetupCode.OwnershipRefused);
    }
}

internal sealed class SyntheticGateCodec : ISetupGateCodec
{
    public DiskGateSnapshot Read(FileStream stream)
    {
        if (stream.Length == 0)
            return new(GateDisposition.Missing, null);
        stream.Position = 0;
        return stream.ReadByte() switch
        {
            0 => new(GateDisposition.Closed, null),
            1 => new(GateDisposition.Ready, new GateReadyState(null)),
            _ => throw new SetupIoException(SetupCode.InvalidEvidence)
        };
    }

    public void Close(FileStream stream)
    {
        stream.Position = 0;
        stream.WriteByte(0);
        stream.Flush(flushToDisk: true);
    }

    public void Publish(FileStream stream, GateReadyState ready)
    {
        stream.Position = 0;
        stream.WriteByte(1);
        stream.Flush(flushToDisk: true);
    }
}

internal sealed class ScriptedMediaReader(PackageMedia media) : IPackageMediaReader
{
    public PackageMedia Read(string path) => media with
    {
        Evidence = media.Evidence with { SourcePath = path }
    };
}
