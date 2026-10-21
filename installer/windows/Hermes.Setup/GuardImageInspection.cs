using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Text;
using static Hermes.Setup.SetupEvidenceCause;

namespace Hermes.Setup;

// Structural observations only. No LoadLibrary, reflection loading or execution.
internal static class GuardImageInspection
{
    internal static GuardImageObservation Read(ImmutableArray<byte> bytes) =>
        Read(bytes, new CandidateValidatorSourcePoint());

    internal static GuardImageObservation Read(
        ImmutableArray<byte> bytes, CandidateValidatorSourcePoint point)
    {
        using var pe = point.Read(CandidateGuardPeReader, () => new PEReader(bytes));
        var optional = Headers(pe, point);
        Require(point, CandidateGuardPe32Plus, () => optional.Magic == PEMagic.PE32Plus);
        Require(point, CandidateGuardNoClrDirectory,
            () => optional.CorHeaderTableDirectory.Size == 0);
        var directory = optional.ExportTableDirectory;
        Require(point, CandidateGuardExportRva, () => directory.RelativeVirtualAddress > 0);
        Require(point, CandidateGuardExportSize, () => directory.Size >= 40);
        var exports = Exports(pe, directory, point);
        return point.Read(CandidateGuardInspection,
            () => new GuardImageObservation(PackageMediaReader.Hash(bytes.ToArray()), exports));
    }

    private static PEHeader Headers(PEReader pe, CandidateValidatorSourcePoint point)
    {
        var headers = point.Read(CandidateGuardPeHeaders, () => pe.PEHeaders);
        Require(point, CandidateGuardMachine, () => headers.CoffHeader.Machine == Machine.Amd64);
        Require(point, CandidateGuardDll,
            () => (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0);
        Require(point, CandidateGuardExecutable,
            () => (headers.CoffHeader.Characteristics & Characteristics.ExecutableImage) != 0);
        Require(point, CandidateGuardNoClrHeader, () => headers.CorHeader is null);
        return point.Read(CandidateGuardOptionalHeader,
            () => headers.PEHeader ?? throw Invalid());
    }

    private static ImmutableArray<string> Exports(
        PEReader pe, DirectoryEntry directory, CandidateValidatorSourcePoint point)
    {
        var start = directory.RelativeVirtualAddress;
        var functionCount = point.Read(CandidateGuardFunctionCountRead,
            () => U32(pe, start + 20));
        Require(point, CandidateGuardFunctionCount, () => functionCount == 2);
        var nameCount = point.Read(CandidateGuardNameCountRead,
            () => U32(pe, start + 24));
        Require(point, CandidateGuardNameCount, () => nameCount == 2);
        var functions = point.Read(CandidateGuardFunctionTableRva,
            () => Rva(pe, start + 28));
        var names = point.Read(CandidateGuardNameTableRva,
            () => Rva(pe, start + 32));
        var ordinals = point.Read(CandidateGuardOrdinalTableRva,
            () => Rva(pe, start + 36));
        ValidateFunctions(pe, functions, directory, point);
        ValidateOrdinals(pe, ordinals, point);
        return ExportNames(pe, names, point);
    }

    private static void ValidateFunctions(
        PEReader pe, int functions, DirectoryEntry directory, CandidateValidatorSourcePoint point)
    {
        var addresses = point.Read(CandidateGuardFunctionAddresses, () =>
            Enumerable.Range(0, 2).Select(index =>
                Rva(pe, checked(functions + index * 4))).ToArray());
        Require(point, CandidateGuardDistinctFunctions, () => addresses.Distinct().Count() == 2);
        Require(addresses.All(address => IsCode(pe, address, directory, point)));
    }

    private static void ValidateOrdinals(
        PEReader pe, int ordinals, CandidateValidatorSourcePoint point)
    {
        var indexes = point.Read(CandidateGuardOrdinalIndexes, () =>
            Enumerable.Range(0, 2).Select(index =>
                U16(pe, checked(ordinals + index * 2))).ToArray());
        Require(point, CandidateGuardOrdinalPermutation,
            () => indexes.Order().SequenceEqual(new ushort[] { 0, 1 }));
    }

    private static ImmutableArray<string> ExportNames(
        PEReader pe, int names, CandidateValidatorSourcePoint point)
    {
        var result = Enumerable.Range(0, 2).Select(index =>
            Name(pe, point.Read(CandidateGuardNameRvas,
                () => Rva(pe, checked(names + index * 4))), point)).ToImmutableArray();
        Require(point, CandidateGuardExportNames, () =>
            result.Order(StringComparer.Ordinal).SequenceEqual(
                new[] { "HermesPreflight", "HermesTransactionFence" }.Order(StringComparer.Ordinal)));
        return result;
    }

    private static bool IsCode(
        PEReader pe, int address, DirectoryEntry exports, CandidateValidatorSourcePoint point) =>
        point.Read(CandidateGuardPositiveTarget, () => address > 0) &&
        point.Read(CandidateGuardNonForwardedTarget, () =>
            address < exports.RelativeVirtualAddress ||
            (long)address >= (long)exports.RelativeVirtualAddress + exports.Size) &&
        point.Read(CandidateGuardExecutableTarget, () =>
            pe.PEHeaders.SectionHeaders.Any(section =>
                (section.SectionCharacteristics & SectionCharacteristics.MemExecute) != 0 &&
                (section.SectionCharacteristics & SectionCharacteristics.ContainsCode) != 0 &&
                address >= section.VirtualAddress &&
                (long)address - section.VirtualAddress <
                    Math.Min(section.VirtualSize, section.SizeOfRawData)));

    private static string Name(PEReader pe, int address, CandidateValidatorSourcePoint point)
    {
        var content = point.Read(CandidateGuardNameData,
            () => pe.GetSectionData(address).GetContent()).AsSpan();
        var limit = content[..Math.Min(content.Length, 128)];
        var end = limit.IndexOf((byte)0);
        Require(point, CandidateGuardNameTerminator, () => end > 0);
        var name = limit[..end].ToArray();
        Require(point, CandidateGuardNameAscii,
            () => name.All(value => value is >= 32 and <= 126));
        return Encoding.ASCII.GetString(limit[..end]);
    }

    private static uint U32(PEReader pe, int address) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            pe.GetSectionData(address).GetContent(0, 4).AsSpan());

    private static ushort U16(PEReader pe, int address) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            pe.GetSectionData(address).GetContent(0, 2).AsSpan());

    private static int Rva(PEReader pe, int address) => checked((int)U32(pe, address));

    private static void Require(
        CandidateValidatorSourcePoint point, SetupEvidenceCause cause, Func<bool> condition) =>
        point.Check(cause, () => Require(condition()));

    private static void Require(bool condition)
    {
        if (!condition)
            throw Invalid();
    }

    private static BadImageFormatException Invalid() => new("Invalid native guard image.");
}
