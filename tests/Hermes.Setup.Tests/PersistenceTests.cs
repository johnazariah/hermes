using System.Text;
using System.Text.Json;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task JournalFlushPrecedesAtomicReplacement()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1, 2, 3], [4, 5, 6]);
        var journal = new FileOperationJournal(fixture.Disk, fixture.Scope);
        var result = await journal.SaveAsync(fixture.Journal(evidence), CancellationToken.None);
        Assert.IsType<Effect<Unit>.Success>(result);
        Assert.Equal(new[] { DiskEvent.Flush, DiskEvent.Commit }, fixture.Files.Events);
    }

    [Fact]
    public async Task JournalRebindsRecoverySourcesToRetainedMedia()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1, 2, 3], [4, 5, 6]);
        var journal = new FileOperationJournal(fixture.Disk, fixture.Scope);
        await journal.SaveAsync(fixture.Journal(evidence), CancellationToken.None);
        var loaded = Assert.IsType<Effect<OperationJournal?>.Success>(
            await journal.LoadAsync(CancellationToken.None));
        var record = Assert.IsType<OperationJournal>(loaded.Value);
        var desired = Assert.IsType<PresentTarget>(record.Plan.Desired);
        Assert.Equal(MediaCachePaths.Package(fixture.Scope, evidence.PackageSha256),
            desired.Package.SourcePath);
        Assert.Equal(MediaCachePaths.Manifest(fixture.Scope, evidence.PackageSha256),
            desired.Package.ManifestSourcePath);
    }

    [Fact]
    public async Task JournalRetainsRevokedRunIds()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1], [2]);
        var old = Guid.NewGuid();
        var record = fixture.Journal(evidence) with { RevokedRunIds = [old] };
        var journal = new FileOperationJournal(fixture.Disk, fixture.Scope);
        await journal.SaveAsync(record, CancellationToken.None);
        var loaded = Assert.IsType<Effect<OperationJournal?>.Success>(
            await journal.LoadAsync(CancellationToken.None));
        var restored = Assert.IsType<OperationJournal>(loaded.Value);
        Assert.Contains(old, restored.RevokedRunIds);
        Assert.Equal(record.ActiveRunId, restored.ActiveRunId);
    }

    [Fact]
    public async Task JournalRejectsAnActiveRunAlsoMarkedRevoked()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1], [2]);
        var record = fixture.Journal(evidence);
        var journal = new FileOperationJournal(fixture.Disk, fixture.Scope);
        var result = await journal.SaveAsync(
            record with { RevokedRunIds = [record.ActiveRunId] }, CancellationToken.None);
        var failure = Assert.IsType<Effect<Unit>.Failure>(result);
        Assert.Equal(SetupCode.JournalInvalid, failure.Code);
        Assert.Empty(fixture.Files.Events);
    }

    [Fact]
    public async Task JournalCannotSupplyAnotherInstallationRoot()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1], [2]);
        var document = JournalDocuments.Encode(fixture.Journal(evidence), fixture.Scope);
        document = document with { AppDirectory = Path.Combine(fixture.Scope.AppDirectory, "other") };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document, SetupStorageJson.Default.JournalDocument);
        fixture.Disk.WriteAtomic(Path.Combine(
            fixture.Scope.ServicingDirectory, "operation.json"), bytes);
        var journal = new FileOperationJournal(fixture.Disk, fixture.Scope);
        var failure = Assert.IsType<Effect<OperationJournal?>.Failure>(
            await journal.LoadAsync(CancellationToken.None));
        Assert.Equal(SetupCode.JournalInvalid, failure.Code);
    }

    [Fact]
    public void DuplicateJsonPropertiesAreRejected()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"schemaVersion":1,"schemaVersion":1}""");
        Assert.Throws<SetupIoException>(() =>
            StrictSetupJson.Read(bytes, SetupStorageJson.Default.JournalDocument));
    }

    [Fact]
    public void UnknownJsonPropertiesAreRejected()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1], [2]);
        var document = JournalDocuments.Encode(fixture.Journal(evidence), fixture.Scope);
        var json = JsonSerializer.Serialize(document, SetupStorageJson.Default.JournalDocument);
        var modified = Encoding.UTF8.GetBytes(json[..^1] + ""","unknown":true}""");
        Assert.Throws<JsonException>(() =>
            StrictSetupJson.Read(modified, SetupStorageJson.Default.JournalDocument));
    }

    [Fact]
    public async Task CancellationBeforeJournalWriteDoesNotCreateRecord()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1], [2]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var journal = new FileOperationJournal(fixture.Disk, fixture.Scope);
        var failure = Assert.IsType<Effect<Unit>.Failure>(
            await journal.SaveAsync(fixture.Journal(evidence), cancellation.Token));
        Assert.Equal(SetupCode.Cancelled, failure.Code);
        Assert.Empty(fixture.Files.Events);
    }

    [Fact]
    public async Task AdmissionFileIsNotReplacedByCloseAndReacquire()
    {
        using var fixture = new IoFixture();
        var source = new FileAdmissionSource(fixture.Disk, fixture.Scope, new SyntheticGateCodec());
        var acquired = Assert.IsType<Effect<ISetupLease>.Success>(source.TryAcquire());
        using (var lease = acquired.Value)
            await lease.CloseAsync(CancellationToken.None);
        var creation = File.GetCreationTimeUtc(fixture.Scope.GatePath);
        var again = Assert.IsType<Effect<ISetupLease>.Success>(source.TryAcquire());
        using (var lease = again.Value)
        {
            var readable = Assert.IsAssignableFrom<IReadableSetupLease>(lease);
            Assert.Equal(GateDisposition.Closed, readable.ReadSnapshot().Disposition);
        }
        Assert.Equal(creation, File.GetCreationTimeUtc(fixture.Scope.GatePath));
        Assert.DoesNotContain(DiskEvent.Commit, fixture.Files.Events);
    }

    [Fact]
    public void MalformedAdmissionIsNotTreatedAsReadyOrMissing()
    {
        using var fixture = new IoFixture();
        fixture.Disk.EnsureDirectory(fixture.Scope.ServicingDirectory);
        File.WriteAllBytes(fixture.Scope.GatePath, [99]);
        var source = new FileAdmissionSource(fixture.Disk, fixture.Scope, new SyntheticGateCodec());
        var acquired = Assert.IsType<Effect<ISetupLease>.Success>(source.TryAcquire());
        using var lease = acquired.Value;
        var readable = Assert.IsAssignableFrom<IReadableSetupLease>(lease);
        Assert.Throws<SetupIoException>(() => readable.ReadSnapshot());
    }

    [Fact]
    public async Task CacheRetainsCompleteSourceBytesAndExactManifestBytes()
    {
        using var fixture = new IoFixture();
        byte[] source = [10, 20, 30, 40, 50, 60];
        byte[] manifest = [70, 80, 90];
        var evidence = fixture.Evidence(source, manifest);
        using var cache = new FileMediaRetention(fixture.Disk, fixture.Scope,
            new ScriptedMediaReader(fixture.Media(evidence, manifest)));
        var result = Assert.IsType<Effect<System.Collections.Immutable.ImmutableArray<RetainedMedia>>.Success>(
            await cache.RetainAsync([evidence], CancellationToken.None));
        var retained = Assert.Single(result.Value);
        Assert.Equal(source, File.ReadAllBytes(retained.PackagePath));
        Assert.Equal(manifest, File.ReadAllBytes(retained.ManifestPath));
        Assert.Equal(new[] { DiskEvent.Flush, DiskEvent.Commit,
            DiskEvent.Flush, DiskEvent.Commit }, fixture.Files.Events);
    }

    [Fact]
    public async Task ExistingCorruptCachedMsiIsNotOverwrittenOrAdopted()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1, 2, 3], [4, 5]);
        var path = MediaCachePaths.Package(fixture.Scope, evidence.PackageSha256);
        fixture.Disk.EnsureDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException());
        File.WriteAllBytes(path, [99]);
        using var cache = new FileMediaRetention(fixture.Disk, fixture.Scope,
            new ScriptedMediaReader(fixture.Media(evidence, [4, 5])));
        var failure = Assert.IsType<Effect<System.Collections.Immutable.ImmutableArray<RetainedMedia>>.Failure>(
            await cache.RetainAsync([evidence], CancellationToken.None));
        Assert.Equal(SetupCode.MediaIdentityMismatch, failure.Code);
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ExistingCorruptManifestIsNotOverwrittenOrAdopted()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1, 2, 3], [4, 5]);
        var path = MediaCachePaths.Manifest(fixture.Scope, evidence.PackageSha256);
        fixture.Disk.EnsureDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException());
        File.WriteAllBytes(path, [99]);
        using var cache = new FileMediaRetention(fixture.Disk, fixture.Scope,
            new ScriptedMediaReader(fixture.Media(evidence, [4, 5])));
        var failure = Assert.IsType<Effect<System.Collections.Immutable.ImmutableArray<RetainedMedia>>.Failure>(
            await cache.RetainAsync([evidence], CancellationToken.None));
        Assert.Equal(SetupCode.MediaIdentityMismatch, failure.Code);
        Assert.Equal(new byte[] { 99 }, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task ChangedSourceAfterInspectionFailsBeforeCachePublication()
    {
        using var fixture = new IoFixture();
        var evidence = fixture.Evidence([1, 2, 3], [4, 5]);
        File.WriteAllBytes(fixture.Source, [9, 9, 9]);
        using var cache = new FileMediaRetention(fixture.Disk, fixture.Scope,
            new ScriptedMediaReader(fixture.Media(evidence, [4, 5])));
        var failure = Assert.IsType<Effect<System.Collections.Immutable.ImmutableArray<RetainedMedia>>.Failure>(
            await cache.RetainAsync([evidence], CancellationToken.None));
        Assert.Equal(SetupCode.MediaIdentityMismatch, failure.Code);
        Assert.DoesNotContain(DiskEvent.Commit, fixture.Files.Events);
    }

    [Theory]
    [InlineData("../package")]
    [InlineData("not-a-hash")]
    [InlineData("")]
    public void CacheDirectoryDoesNotAcceptArbitraryPathIdentity(string identity)
    {
        using var fixture = new IoFixture();
        Assert.Throws<SetupIoException>(() =>
            MediaCachePaths.Directory(fixture.Scope, identity));
    }
}
