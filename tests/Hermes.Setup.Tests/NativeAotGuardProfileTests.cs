using System.Collections.Immutable;
using System.Security.Cryptography;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void ClosedNativeAotProfileCorrelatesEveryEatPermutation(int permutation)
    {
        var bytes = NativeAotGuardFixture.Create(permutation);
        var point = new CandidateValidatorSourcePoint();
        var observation = GuardImageInspection.Read(bytes.ToImmutableArray(), point);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), observation.Sha256);
        Assert.Equal(new[] { "HermesPreflight", "HermesTransactionFence" },
            observation.Exports.ToArray());
        Assert.Equal(SetupEvidenceCause.CandidateGuardInspection, point.Cause);
    }

    [Theory]
    [InlineData(0x84, 0x0003014cu, 46)] // Wrong machine.
    [InlineData(0x94, 0x002200f0u, 47)] // Not a DLL.
    [InlineData(0x94, 0x202000f0u, 48)] // Not executable.
    [InlineData(0x98, 0x10bu, 51)]      // PE32 rather than PE32+.
    [InlineData(0x17c, uint.MaxValue, 52)] // CLR directory size.
    [InlineData(0x108, 0u, 53)]
    [InlineData(0x10c, 39u, 54)]
    [InlineData(0x414, 0u, 55)]
    [InlineData(0x414, 1u, 55)]
    [InlineData(0x414, 4u, 55)]        // Unknown additional export.
    [InlineData(0x414, uint.MaxValue, 55)]
    [InlineData(0x418, 0u, 56)]
    [InlineData(0x418, 2u, 56)]        // Missing name.
    [InlineData(0x418, 4u, 56)]
    [InlineData(0x500, 88u, 42)]       // Unknown runtime entry.
    [InlineData(0x540, 88u, 42)]       // Unknown callback.
    [InlineData(0x518, 65u, 42)]       // Missing exact NUL terminator.
    [InlineData(0x500, 0x1fu, 42)]     // Non-ASCII name.
    [InlineData(0x458, 0x2140u, 42)]   // Duplicate name.
    [InlineData(0x462, 0x00010001u, 42)] // Duplicate ordinal.
    [InlineData(0x462, 0x00030001u, 42)] // Sparse ordinal.
    [InlineData(0x460, 0x00000001u, 42)] // Wrong name-to-target roles.
    [InlineData(0x448, 0u, 42)]        // Missing function.
    [InlineData(0x448, 0x1000u, 42)]   // Aliased callbacks.
    [InlineData(0x448, 0x3000u, 42)]   // Aliased DATA/callback.
    [InlineData(0x444, 0x2100u, 42)]   // Forwarded callback.
    [InlineData(0x440, 0x2100u, 42)]   // Forwarded DATA.
    [InlineData(0x444, 0x3020u, 42)]   // DATA as callback.
    [InlineData(0x440, 0x1018u, 42)]   // Code as DATA.
    [InlineData(0x1fc, 0xe0000040u, 42)] // Executable DATA.
    [InlineData(0x1fc, 0xc0000060u, 42)] // Code-marked DATA.
    [InlineData(0x1fc, 0x80000040u, 42)] // Unreadable DATA.
    [InlineData(0x1fc, 0x40000040u, 42)] // Read-only DATA.
    [InlineData(0x1fc, 0xc0000080u, 42)] // Uninitialized DATA.
    [InlineData(0x1ac, 0x40000020u, 42)] // Non-executable callback.
    [InlineData(0x1ac, 0x20000020u, 42)] // Unreadable callback.
    [InlineData(0x1ac, 0x60000040u, 42)] // Callback not code.
    [InlineData(0x1d4, 0x00000040u, 42)] // Unreadable export directory.
    [InlineData(0x800, 0u, 42)]        // Wrong cookie.
    [InlineData(0x804, 5u, 42)]        // Unknown major.
    [InlineData(0x804, 0x00010004u, 42)] // Unknown minor.
    [InlineData(0x808, 0u, 42)]        // Wrong pointer width.
    [InlineData(0x808, 3u, 42)]        // Wrong endianness.
    [InlineData(0x808, 0x101u, 42)]    // Unknown flags.
    [InlineData(0x80c, 1u, 42)]        // Reserved padding.
    [InlineData(0x810, 1u, 42)]        // First pointer, low half.
    [InlineData(0x814, 1u, 42)]        // First pointer, high half.
    [InlineData(0x818, 1u, 42)]        // Second pointer, low half.
    [InlineData(0x81c, 1u, 42)]        // Second pointer, high half.
    [InlineData(0x440, 0x3001u, 42)]   // Unaligned DATA.
    [InlineData(0x440, 0x31f0u, 42)]   // DATA past raw/virtual end.
    [InlineData(0x1e0, 16u, 42)]       // DATA past virtual end.
    [InlineData(0x1e8, 16u, 42)]       // DATA past raw end.
    [InlineData(0x41c, uint.MaxValue, 42)]
    [InlineData(0x420, uint.MaxValue, 42)]
    [InlineData(0x424, uint.MaxValue, 42)]
    [InlineData(0x450, uint.MaxValue, 42)]
    [InlineData(0x440, uint.MaxValue, 42)]
    [InlineData(0x41c, 0x23f8u, 42)]   // Function array crosses backing end.
    [InlineData(0x420, 0x23f8u, 42)]   // Name array crosses backing end.
    [InlineData(0x424, 0x23fcu, 42)]   // Ordinal array crosses backing end.
    [InlineData(0x450, 0x23f0u, 42)]   // Name crosses backing end.
    [InlineData(0x10c, 0x1000u, 42)]   // Entire directory must be backed.
    [InlineData(0x1ec, 0xa00u, 42)]    // Raw DATA past file.
    [InlineData(0x1ec, 0x100u, 42)]    // Raw DATA inside headers.
    [InlineData(0x1e4, 0x1000u, 42)]   // Overlapping virtual sections.
    [InlineData(0xd0, 0x3010u, 42)]    // DATA extends past SizeOfImage.
    public async Task ClosedNativeAotMutationsRejectEvenTheirApprovedHashes(
        int offset, uint value, int expectedCause)
    {
        using var world = new RootFixture();
        var bytes = NativeAotGuardFixture.Create();
        AuthoringFixture.Put32(bytes, offset, value);
        var point = new CandidateValidatorSourcePoint();
        var error = Record.Exception(() => GuardImageInspection.Read(bytes.ToImmutableArray(), point));
        Assert.NotNull(error);
        Assert.True(SetupIo.Expected(error));
        Assert.Equal((SetupEvidenceCause)expectedCause, point.Cause);
        await AssertGuardRefusedAsync(world,
            world.New.Authoring.Guard(bytes, approveSyntheticImage: true), point.Cause);
    }

    [Fact]
    public async Task ThreeEntriesWithoutTheKnownRuntimeProfileRemainRejected()
    {
        using var world = new RootFixture();
        var bytes = AuthoringFixture.NativeImage();
        AuthoringFixture.Put32(bytes, 0x314, 3);
        AuthoringFixture.Put32(bytes, 0x318, 3);
        await AssertGuardRefusedAsync(world,
            world.New.Authoring.Guard(bytes, approveSyntheticImage: true),
            SetupEvidenceCause.CandidateGuardInspection);
    }

    [Fact]
    public async Task NativeCallbackVirtualTailIsNotRawBacking()
    {
        using var world = new RootFixture();
        var bytes = NativeAotGuardFixture.Create();
        AuthoringFixture.Put32(bytes, 0x190, 0x300);
        AuthoringFixture.Put32(bytes, 0x444, 0x1250);
        await AssertGuardRefusedAsync(world,
            world.New.Authoring.Guard(bytes, approveSyntheticImage: true),
            SetupEvidenceCause.CandidateGuardInspection);
    }

    [Fact]
    public async Task NativeManagedHeaderRemainsRejected()
    {
        using var world = new RootFixture();
        var bytes = NativeAotGuardFixture.Create();
        AuthoringFixture.Put32(bytes, 0x178, 0x2200);
        AuthoringFixture.Put32(bytes, 0x17c, 72);
        AuthoringFixture.Put32(bytes, 0x600, 72);
        AuthoringFixture.Put32(bytes, 0x604, 0x00050002);
        AuthoringFixture.Put32(bytes, 0x608, 0x2280);
        AuthoringFixture.Put32(bytes, 0x60c, 0x80);
        AuthoringFixture.Put32(bytes, 0x610, 1);
        await AssertGuardRefusedAsync(world,
            world.New.Authoring.Guard(bytes, approveSyntheticImage: true),
            SetupEvidenceCause.CandidateGuardNoClrHeader);
    }

    [Theory]
    [InlineData(0x800)]
    [InlineData(0x81f)]
    public async Task NativeRuntimeObjectMustBeCompletelyPresentOnDisk(int length)
    {
        using var world = new RootFixture();
        var bytes = NativeAotGuardFixture.Create()[..length];
        await AssertGuardRefusedAsync(world,
            world.New.Authoring.Guard(bytes, approveSyntheticImage: true),
            SetupEvidenceCause.CandidateGuardInspection);
    }

    [Theory]
    [InlineData(0x200)]
    [InlineData(0x7ff)]
    [InlineData(0x9ff)]
    [InlineData(-1)]
    public async Task NativeCompatibilityNeverApprovesChangedCodePaddingOrOverlay(int offset)
    {
        using var world = new RootFixture();
        var original = NativeAotGuardFixture.Create();
        var fixture = world.New.Authoring.Guard(original, approveSyntheticImage: true);
        byte[] changed = offset < 0 ? [.. original, (byte)7] : original.ToArray();
        if (offset >= 0)
            changed[offset] ^= 1;
        var observation = GuardImageInspection.Read(changed.ToImmutableArray());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(changed)), observation.Sha256);
        Assert.NotEqual(fixture.Approved.Sha256.Single(), observation.Sha256);
        await AssertGuardRefusedAsync(world,
            fixture.Guard(changed, approveSyntheticImage: false),
            SetupEvidenceCause.CandidateGuardApprovedHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task NativeProfileUsesRealCandidateRetainedAndCacheValidation(int permutation)
    {
        using var world = new RootFixture();
        var fixture = world.New.Authoring.Guard(
            NativeAotGuardFixture.Create(permutation), approveSyntheticImage: true);
        var profiles = await NativeCatalogProfilesAsync(world, fixture, CancellationToken.None);
        Assert.Equal(new[] { PackageAuthoringMode.CandidateSource,
            PackageAuthoringMode.RetainedSource, PackageAuthoringMode.RegisteredCache },
            profiles.Select(profile => profile.Mode).ToArray());
        Assert.All(profiles, profile =>
        {
            Assert.Equal(fixture.Approved.Sha256.Single(), profile.Guard.Sha256);
            Assert.Equal(new[] { "HermesPreflight", "HermesTransactionFence" },
                profile.Guard.Exports.ToArray());
            Assert.Equal(profile.Mode != PackageAuthoringMode.RegisteredCache,
                profile.HasCompleteSource);
        });
    }

    [Fact]
    public async Task ApprovedNativeProfileReachesLaterCompositionDenialWithoutMutation()
    {
        using var world = new RootFixture();
        var fixture = world.New.Authoring.Guard(
            NativeAotGuardFixture.Create(), approveSyntheticImage: true)
            .Cell("Shortcut", 0, 5, "sensitive");
        var database = new BoundaryDatabase(world.Database, authoring: fixture.Snapshot);
        var factory = new Factory(world.Capabilities with
            { Database = database, Authoring = database }, fixture.Approved);
        AssertRefused(await RunAsync(factory, SetupVerb.Install, world.New.SourcePath),
            SetupVerb.Install, BoundaryEvidence(SetupEvidenceCause.CandidateValidatorShortcut),
            SetupCode.MediaInvalid);
        AssertNoMutation(world);
        Assert.Equal(world.New.SourceBytes.ToArray(), world.Files.RawBytes(world.New.SourcePath));
    }

    [Fact]
    public async Task RuntimeDataCannotBecomeAnAuthoredCallback()
    {
        using var world = new RootFixture();
        var fixture = world.New.Authoring.Guard(
            NativeAotGuardFixture.Create(), approveSyntheticImage: true)
            .Cell("CustomAction", 0, 3, "DotNetRuntimeDebugHeader");
        await AssertGuardRefusedAsync(world, fixture, SetupEvidenceCause.CandidateValidatorActions);
    }

    private static async Task<ImmutableArray<ValidatedPackageProfile>> NativeCatalogProfilesAsync(
        RootFixture world, AuthoringFixture fixture, CancellationToken cancellationToken)
    {
        var database = new NativeGuardDatabase(world.Database,
            fixture.Snapshot.RequireBinary("HermesServicingGuard"));
        using var catalog = new SetupPackageCatalog(world.Disk, world.Scope,
            RootFixture.Programs, world.Media, database, fixture.Approved);
        var candidate = catalog.Source(world.New.SourcePath, PackageAuthoringMode.CandidateSource);
        using var retention = new FileMediaRetention(world.Disk, world.Scope, world.Media);
        AggregateResult.Require(await retention.RetainAsync([candidate.Evidence], cancellationToken));
        var retained = catalog.Retained(candidate.Evidence);
        world.Files.Put(world.New.CachedPath, world.New.SourceBytes.AsSpan());
        var cached = catalog.Cached(world.New.CachedPath);
        SetupPackageCatalog.RequireCachedMatches(cached, retained);
        return [candidate.Profile, retained.Profile, cached.Profile];
    }

    private sealed class NativeGuardDatabase(
        RootDatabase inner, ImmutableArray<byte> guard) : IPackageAuthoringDatabase
    {
        public PackageAuthoringSnapshot ReadAuthoring(string packagePath)
        {
            var snapshot = inner.ReadAuthoring(packagePath);
            return snapshot with
            {
                Binaries = snapshot.Binaries.SetItem("HermesServicingGuard", guard)
            };
        }
    }
}
