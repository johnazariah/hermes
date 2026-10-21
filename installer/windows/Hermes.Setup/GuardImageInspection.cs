using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Text;
using static Hermes.Setup.SetupEvidenceCause;

namespace Hermes.Setup;

// Structural observations only. No LoadLibrary, reflection loading or execution.
internal static class GuardImageInspection
{
    private enum ExportRole { Callback, RuntimeData }

    private sealed record NativeExport(string Name, ExportRole Role);

    private static ImmutableArray<string> CallbackNames { get; } =
        ["HermesPreflight", "HermesTransactionFence"];

    private static ImmutableArray<NativeExport> NativeAotRoles { get; } =
    [
        new("DotNetRuntimeDebugHeader", ExportRole.RuntimeData),
        .. CallbackNames.Select(name => new NativeExport(name, ExportRole.Callback))
    ];

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
        Require(point, CandidateGuardFunctionCount, () => functionCount is 2 or 3);
        var nameCount = point.Read(CandidateGuardNameCountRead,
            () => U32(pe, start + 24));
        Require(point, CandidateGuardNameCount, () => nameCount == functionCount);
        return functionCount switch
        {
            2 => LegacyExports(pe, directory, point),
            3 => point.Read(CandidateGuardInspection, () => NativeAotExports(pe, directory)),
            _ => throw Invalid()
        };
    }

    private static ImmutableArray<string> LegacyExports(
        PEReader pe, DirectoryEntry directory, CandidateValidatorSourcePoint point)
    {
        var start = directory.RelativeVirtualAddress;
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
                CallbackNames.Order(StringComparer.Ordinal)));
        return result;
    }

    // Closed Windows NativeAOT producer profile, not a general extra-export policy.
    // DebugHeader.cpp at runtime v9.0.21 defines the on-disk runtime DATA object.
    private static ImmutableArray<string> NativeAotExports(PEReader pe, DirectoryEntry directory)
    {
        var optional = pe.PEHeaders.PEHeader ?? throw Invalid();
        Require(optional.CorHeaderTableDirectory.RelativeVirtualAddress == 0);
        var start = directory.RelativeVirtualAddress;
        _ = BackedBytes(pe, start, directory.Size);
        var addresses = NativeRvas(pe, Rva(pe, checked(start + 28), backed: true));
        var names = NativeRvas(pe, Rva(pe, checked(start + 32), backed: true));
        var ordinals = NativeOrdinals(pe, Rva(pe, checked(start + 36), backed: true));
        Require(addresses.Distinct().Count() == 3);
        Require(ordinals.Order().SequenceEqual(new ushort[] { 0, 1, 2 }));
        foreach (var (export, index) in NativeAotRoles.Select((export, index) => (export, index)))
            ValidateNativeExport(pe, export, names[index], addresses[ordinals[index]], directory);
        // The runtime DATA entry is validated above, never a callback identity.
        return CallbackNames;
    }

    private static ImmutableArray<int> NativeRvas(PEReader pe, int table)
    {
        _ = BackedBytes(pe, table, 12);
        return Enumerable.Range(0, 3).Select(index =>
            Rva(pe, checked(table + index * 4), backed: true)).ToImmutableArray();
    }

    private static ImmutableArray<ushort> NativeOrdinals(PEReader pe, int table)
    {
        _ = BackedBytes(pe, table, 6);
        return Enumerable.Range(0, 3).Select(index =>
            U16(pe, checked(table + index * 2), backed: true)).ToImmutableArray();
    }

    private static void ValidateNativeExport(
        PEReader pe, NativeExport export, int name, int target, DirectoryEntry directory)
    {
        // Exact bounded ASCII including NUL: no aliases, suffixes or unknown names.
        var expected = Encoding.ASCII.GetBytes(export.Name + '\0');
        Require(BackedBytes(pe, name, expected.Length).AsSpan().SequenceEqual(expected));
        var width = export.Role switch { ExportRole.RuntimeData => 32, _ => 1 };
        Require(target > 0 &&
            ((long)target + width <= directory.RelativeVirtualAddress ||
                (long)target >= (long)directory.RelativeVirtualAddress + directory.Size));
        var section = BackedSection(pe, target, width);
        Require(export.Role switch
        {
            ExportRole.Callback =>
                (section.SectionCharacteristics &
                    (SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode)) ==
                (SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode),
            ExportRole.RuntimeData => IsRuntimeData(pe, target, section),
            _ => false
        });
    }

    private static bool IsRuntimeData(PEReader pe, int address, SectionHeader section)
    {
        const SectionCharacteristics required = SectionCharacteristics.MemRead |
            SectionCharacteristics.MemWrite | SectionCharacteristics.ContainsInitializedData;
        const SectionCharacteristics excluded =
            SectionCharacteristics.MemExecute | SectionCharacteristics.ContainsCode;
        if ((section.SectionCharacteristics & required) != required ||
            (section.SectionCharacteristics & excluded) != 0 || address % 8 != 0)
            return false;
        var data = BackedBytes(pe, address, 32).AsSpan();
        return data[..4].SequenceEqual("DNDH"u8) &&
            BinaryPrimitives.ReadUInt16LittleEndian(data[4..]) == 4 &&
            BinaryPrimitives.ReadUInt16LittleEndian(data[6..]) == 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(data[12..]) == 0 &&
            BinaryPrimitives.ReadUInt64LittleEndian(data[16..]) == 0 &&
            BinaryPrimitives.ReadUInt64LittleEndian(data[24..]) == 0;
    }

    private static ImmutableArray<byte> BackedBytes(PEReader pe, int address, int count)
    {
        var section = BackedSection(pe, address, count);
        var offset = checked((int)((long)section.PointerToRawData +
            address - section.VirtualAddress));
        return pe.GetEntireImage().GetContent(offset, count);
    }

    private static SectionHeader BackedSection(PEReader pe, int address, int count)
    {
        var optional = pe.PEHeaders.PEHeader ?? throw Invalid();
        var length = pe.GetEntireImage().Length;
        Require(length > 0 && length <= 32 * 1024 * 1024 &&
            address > 0 && count > 0 && count <= length &&
            (long)address + count <= optional.SizeOfImage);
        var sections = pe.PEHeaders.SectionHeaders.Where(section =>
            (long)address < (long)section.VirtualAddress +
                Math.Max(section.VirtualSize, section.SizeOfRawData) &&
            (long)address + count > section.VirtualAddress).ToArray();
        Require(sections.Length == 1);
        var section = sections[0];
        ValidateBacking(section, optional, length, address, count);
        return section;
    }

    private static void ValidateBacking(
        SectionHeader section, PEHeader optional, int length, int address, int count)
    {
        var delta = (long)address - section.VirtualAddress;
        Require(optional.SizeOfHeaders > 0 &&
            delta >= 0 && delta + count <= Math.Min(section.VirtualSize, section.SizeOfRawData) &&
            (section.SectionCharacteristics & SectionCharacteristics.MemRead) != 0 &&
            section.PointerToRawData >= optional.SizeOfHeaders &&
            (long)section.PointerToRawData + delta + count <= length);
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

    private static uint U32(PEReader pe, int address, bool backed = false) =>
        BinaryPrimitives.ReadUInt32LittleEndian(
            (backed ? BackedBytes(pe, address, 4) :
                pe.GetSectionData(address).GetContent(0, 4)).AsSpan());

    private static ushort U16(PEReader pe, int address, bool backed = false) =>
        BinaryPrimitives.ReadUInt16LittleEndian(
            (backed ? BackedBytes(pe, address, 2) :
                pe.GetSectionData(address).GetContent(0, 2)).AsSpan());

    private static int Rva(PEReader pe, int address, bool backed = false) =>
        checked((int)U32(pe, address, backed));

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
