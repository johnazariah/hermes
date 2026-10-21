using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using Hermes.Servicing;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed partial class SetupEvidenceTests
{
    [Theory]
    [InlineData(45)]
    [InlineData(46)]
    [InlineData(47)]
    [InlineData(48)]
    [InlineData(49)]
    [InlineData(50)]
    [InlineData(51)]
    [InlineData(52)]
    [InlineData(53)]
    [InlineData(54)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(57)]
    [InlineData(58)]
    [InlineData(59)]
    [InlineData(60)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(66)]
    [InlineData(67)]
    [InlineData(68)]
    [InlineData(69)]
    [InlineData(70)]
    [InlineData(71)]
    [InlineData(72)]
    [InlineData(73)]
    public async Task GuardPredicatesPreserveFrozenPolicyAndIdentifyTheFirstFailure(int value)
    {
        using var world = new RootFixture();
        var bytes = GuardPredicateMutation(value);
        var point = new CandidateValidatorSourcePoint();
        AssertGuardRejectionUnchanged(bytes.ToImmutableArray(), point);
        Assert.Equal((SetupEvidenceCause)value, point.Cause);
        var changed = world.New.Authoring.Guard(bytes, approveSyntheticImage: true);
        await AssertGuardRefusedAsync(world, changed, (SetupEvidenceCause)value);
    }

    [Fact]
    public void GuardPeReaderConstructionKeepsItsOriginalException()
    {
        var point = new CandidateValidatorSourcePoint();
        AssertGuardRejectionUnchanged(default, point);
        Assert.Equal(SetupEvidenceCause.CandidateGuardPeReader, point.Cause);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PositiveSyntheticPeVariantsMatchFrozenPolicy(int variant)
    {
        var bytes = PositiveGuardVariant(variant);
        var point = new CandidateValidatorSourcePoint();
        var expected = FrozenGuardPolicy.Read(bytes.ToImmutableArray());
        var actual = GuardImageInspection.Read(bytes.ToImmutableArray(), point);
        Assert.Equal(expected.Sha256, actual.Sha256);
        Assert.Equal(expected.Exports.ToArray(), actual.Exports.ToArray());
        Assert.Equal(SetupEvidenceCause.CandidateGuardInspection, point.Cause);
        Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
            AuthoringFixture.Create().Guard(bytes, approveSyntheticImage: true).Validate());
    }

    [Fact]
    public async Task FunctionCountDenialStillPrecedesNameCountAndLaterDenials()
    {
        using var world = new RootFixture();
        var bytes = GuardWords((0x314, 3), (0x318, 3), (0x340, 0));
        var changed = world.New.Authoring.Guard(bytes, approveSyntheticImage: true)
            .Cell("Shortcut", 0, 5, "sensitive").Cell("RemoveFile", 0, 2, "*");
        await AssertGuardRefusedAsync(world, changed, SetupEvidenceCause.CandidateGuardFunctionCount);
    }

    [Fact]
    public async Task MissingBoundedNameTerminatorStillRejectsBeforeNameComparison()
    {
        using var world = new RootFixture();
        var bytes = AuthoringFixture.NativeImage();
        bytes.AsSpan(0x360, 128).Fill((byte)'A');
        var point = new CandidateValidatorSourcePoint();
        AssertGuardRejectionUnchanged(bytes.ToImmutableArray(), point);
        Assert.Equal(SetupEvidenceCause.CandidateGuardNameTerminator, point.Cause);
        await AssertGuardRefusedAsync(world,
            world.New.Authoring.Guard(bytes, approveSyntheticImage: true), point.Cause);
    }

    [Fact]
    public void GuardPredicateSourcePointsPreserveExceptionIdentityAndNativePriority()
    {
        foreach (var value in Enumerable.Range(44, 30))
        {
            AssertGuardPredicateExceptionIdentity((SetupEvidenceCause)value);
            AssertGuardPredicateNativePriority((SetupEvidenceCause)value);
        }
    }

    [Fact]
    public void GuardPredicateEvidenceCombinationsRemainClosed()
    {
        foreach (var value in Enumerable.Range(44, 30))
        {
            var evidence = BoundaryEvidence((SetupEvidenceCause)value);
            Assert.True(evidence.IsCandidateMedia);
            SetupRefusalEvidence[] invalid = [
                evidence with { Stage = SetupEvidenceStage.CurrentPackage },
                evidence with { Fault = ObservationFault.NativeQuery },
                evidence with { ErrorKind = StartupEvidenceErrorKind.Win32 },
                evidence with { ErrorCode = 1 },
                evidence with { Cause = (SetupEvidenceCause)74 }];
            Assert.All(invalid, item => Assert.False(item.IsCandidateMedia));
        }
    }

    private static void AssertGuardRejectionUnchanged(
        ImmutableArray<byte> bytes, CandidateValidatorSourcePoint point)
    {
        var expected = Record.Exception(() => FrozenGuardPolicy.Read(bytes));
        var actual = Record.Exception(() => GuardImageInspection.Read(bytes, point));
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
        Assert.Equal(expected.HResult, actual.HResult);
        Assert.True(SetupIo.Expected(actual));
    }

    private static void AssertGuardPredicateExceptionIdentity(SetupEvidenceCause cause)
    {
        Exception[] errors = [
            new IOException("sensitive"), new UnauthorizedAccessException("sensitive"),
            new JsonException("sensitive"), new ArgumentException("sensitive"),
            new NotSupportedException("sensitive"), new FormatException("sensitive"),
            new OverflowException("sensitive"), new BadImageFormatException("sensitive"),
            new Win32Exception(87, "sensitive"), new OperationCanceledException("sensitive"),
            new InvalidOperationException("sensitive")];
        foreach (var error in errors)
        {
            var point = new CandidateValidatorSourcePoint();
            var actual = Assert.Throws(error.GetType(), () =>
                point.Check(cause, () => point.Read<int>(cause, () => throw error)));
            Assert.Same(error, actual);
            Assert.Equal(cause, point.Cause);
        }
    }

    private static void AssertGuardPredicateNativePriority(SetupEvidenceCause cause)
    {
        var evidence = new SetupRefusalEvidence(SetupEvidenceStage.Unspecified,
            SetupEvidenceCause.MsiObservation, ObservationFault.NativeQuery,
            StartupEvidenceErrorKind.Win32, 87);
        var error = new SetupIoException(SetupCode.MediaInvalid, evidence);
        var point = new CandidateValidatorSourcePoint();
        var actual = Assert.Throws<SetupIoException>(() =>
            CandidateMediaEvidence.Read<int>(cause,
                () => point.Read<int>(cause, () => throw error)));
        Assert.Same(error, actual);
        Assert.Same(evidence, actual.Evidence);
        Assert.Equal(SetupCode.MediaInvalid, actual.Code);
    }

    private static byte[] GuardPredicateMutation(int cause) => cause switch
    {
        <= 54 => GuardHeaderMutation(cause),
        <= 64 => GuardFunctionMutation(cause),
        _ => GuardNameMutation(cause)
    };

    private static byte[] GuardHeaderMutation(int cause) => cause switch
    {
        45 => [0],
        46 => GuardWord(0x84, 0x14c),
        47 => GuardWord(0x96, 0x22),
        48 => GuardWord(0x96, 0x2020),
        49 => GuardManagedImage(),
        50 => GuardCoffOnly(),
        51 => GuardPe32(),
        52 => GuardWords((0x17c, uint.MaxValue)),
        53 => GuardWords((0x108, 0)),
        54 => GuardWords((0x10c, 39)),
        _ => throw new ArgumentOutOfRangeException(nameof(cause))
    };

    private static byte[] GuardFunctionMutation(int cause) => cause switch
    {
        55 => GuardWords((0x314, 3)),
        56 => GuardWords((0x318, 3)),
        57 => GuardWords((0x31c, uint.MaxValue)),
        58 => GuardWords((0x320, uint.MaxValue)),
        59 => GuardWords((0x324, uint.MaxValue)),
        60 => GuardWords((0x340, uint.MaxValue)),
        61 => GuardWords((0x344, 0x1000)),
        62 => GuardWords((0x340, 0)),
        63 => GuardWords((0x340, 0x1100)),
        64 => GuardWords((0x1ac, 0x40000020)),
        _ => throw new ArgumentOutOfRangeException(nameof(cause))
    };

    private static byte[] GuardNameMutation(int cause) => cause switch
    {
        65 => GuardWords((0x324, 0x13ff)),
        66 => GuardWord(0x352, 0),
        67 => GuardWords((0x348, uint.MaxValue)),
        68 => GuardUnreadableNameData(),
        69 => GuardByte(0x360, 0),
        70 => GuardByte(0x360, 31),
        71 => GuardByte(0x360, (byte)'X'),
        72 => GuardWords((0x108, 0x13e9)),
        73 => GuardWords((0x108, 0x13e8), (0x5fc, 2)),
        _ => throw new ArgumentOutOfRangeException(nameof(cause))
    };

    private static byte[] GuardWords(params (int Offset, uint Value)[] changes)
    {
        var bytes = AuthoringFixture.NativeImage();
        foreach (var (offset, value) in changes)
            AuthoringFixture.Put32(bytes, offset, value);
        return bytes;
    }

    private static byte[] GuardWord(int offset, ushort value)
    {
        var bytes = AuthoringFixture.NativeImage();
        AuthoringFixture.Put16(bytes, offset, value);
        return bytes;
    }

    private static byte[] GuardByte(int offset, byte value)
    {
        var bytes = AuthoringFixture.NativeImage();
        bytes[offset] = value;
        return bytes;
    }

    private static byte[] GuardManagedImage()
    {
        var bytes = GuardWords(
            (0x178, 0x1200), (0x17c, 72),
            (0x400, 72), (0x404, 0x00050002),
            (0x408, 0x1280), (0x40c, 0x80),
            (0x410, (uint)CorFlags.ILOnly));
        using var pe = new PEReader(bytes.ToImmutableArray());
        Assert.NotNull(pe.PEHeaders.CorHeader);
        return bytes;
    }

    private static byte[] GuardCoffOnly()
    {
        var bytes = new byte[20];
        AuthoringFixture.Put16(bytes, 0, 0x8664);
        AuthoringFixture.Put16(bytes, 18, 0x2022);
        return bytes;
    }

    private static byte[] GuardPe32()
    {
        var bytes = AuthoringFixture.NativeImage();
        Array.Copy(bytes, 0x108, bytes, 0xf8, 0x80);
        Array.Copy(bytes, 0x188, bytes, 0x178, 0x28);
        AuthoringFixture.Put16(bytes, 0x94, 0xe0);
        AuthoringFixture.Put16(bytes, 0x98, 0x10b);
        AuthoringFixture.Put32(bytes, 0xf4, 16);
        return bytes;
    }

    private static byte[] GuardUnreadableNameData()
    {
        var bytes = GuardWords((0x1b8, 0x100), (0x1bc, 0x2000),
            (0x1c0, 0x100), (0x1c4, 0x1000), (0x1d4, 0x40000040),
            (0x348, 0x2000));
        AuthoringFixture.Put16(bytes, 0x86, 2);
        return bytes;
    }

    private static byte[] PositiveGuardVariant(int variant)
    {
        var bytes = AuthoringFixture.NativeImage();
        switch (variant)
        {
            case 1: AuthoringFixture.Put32(bytes, 0x10c, 40); break;
            case 2:
                AuthoringFixture.Put16(bytes, 0x350, 1);
                AuthoringFixture.Put16(bytes, 0x352, 0);
                break;
            case 3:
                AuthoringFixture.Put32(bytes, 0x348, 0x1180);
                AuthoringFixture.Put32(bytes, 0x34c, 0x1160);
                break;
        }
        return bytes;
    }

    // Frozen pre-instrumentation policy; synthetic fixtures are not producer proof.
    private static class FrozenGuardPolicy
    {
        internal static GuardImageObservation Read(ImmutableArray<byte> bytes)
        {
            using var pe = new PEReader(bytes);
            var headers = pe.PEHeaders;
            Require(headers.CoffHeader.Machine == Machine.Amd64 &&
                (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0 &&
                (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) != 0 &&
                headers.CorHeader is null);
            var optional = headers.PEHeader ?? throw Invalid();
            Require(optional.Magic == PEMagic.PE32Plus &&
                optional.CorHeaderTableDirectory.Size == 0);
            var directory = optional.ExportTableDirectory;
            Require(directory.RelativeVirtualAddress > 0 && directory.Size >= 40);
            var exports = Exports(pe, directory);
            return new(PackageMediaReader.Hash(bytes.ToArray()), exports);
        }

        private static ImmutableArray<string> Exports(PEReader pe, DirectoryEntry directory)
        {
            var start = directory.RelativeVirtualAddress;
            Require(U32(pe, start + 20) == 2 && U32(pe, start + 24) == 2);
            var functions = Rva(pe, start + 28);
            var names = Rva(pe, start + 32);
            var ordinals = Rva(pe, start + 36);
            var addresses = Enumerable.Range(0, 2).Select(index =>
                Rva(pe, checked(functions + index * 4))).ToArray();
            Require(addresses.Distinct().Count() == 2 &&
                addresses.All(address => IsCode(pe, address, directory)));
            var indexes = Enumerable.Range(0, 2).Select(index =>
                U16(pe, checked(ordinals + index * 2))).ToArray();
            Require(indexes.Order().SequenceEqual(new ushort[] { 0, 1 }));
            var result = Enumerable.Range(0, 2).Select(index =>
                Name(pe, Rva(pe, checked(names + index * 4)))).ToImmutableArray();
            Require(result.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "HermesPreflight", "HermesTransactionFence" }.Order(StringComparer.Ordinal)));
            return result;
        }

        private static bool IsCode(PEReader pe, int address, DirectoryEntry exports) =>
            address > 0 &&
            (address < exports.RelativeVirtualAddress ||
                (long)address >= (long)exports.RelativeVirtualAddress + exports.Size) &&
            pe.PEHeaders.SectionHeaders.Any(section =>
                (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 &&
                (section.SectionCharacteristics & SectionCharacteristics.ContainsCode) != 0 &&
                address >= section.VirtualAddress &&
                (long)address - section.VirtualAddress <
                    Math.Min(section.VirtualSize, section.SizeOfRawData));

        private static string Name(PEReader pe, int address)
        {
            var content = pe.GetSectionData(address).GetContent().AsSpan();
            var limit = content[..Math.Min(content.Length, 128)];
            var end = limit.IndexOf((byte)0);
            Require(end > 0);
            Require(limit[..end].ToArray().All(value => value is >= 32 and <= 126));
            return Encoding.ASCII.GetString(limit[..end]);
        }

        private static uint U32(PEReader pe, int address) =>
            BinaryPrimitives.ReadUInt32LittleEndian(
                pe.GetSectionData(address).GetContent(0, 4).AsSpan());

        private static ushort U16(PEReader pe, int address) =>
            BinaryPrimitives.ReadUInt16LittleEndian(
                pe.GetSectionData(address).GetContent(0, 2).AsSpan());

        private static int Rva(PEReader pe, int address) => checked((int)U32(pe, address));

        private static void Require(bool condition)
        {
            if (!condition)
                throw Invalid();
        }

        private static BadImageFormatException Invalid() => new("Invalid native guard image.");
    }
}
