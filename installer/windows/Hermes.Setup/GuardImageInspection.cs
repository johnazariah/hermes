using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using System.Text;

namespace Hermes.Setup;

// Structural observations only. No LoadLibrary, reflection loading or execution.
internal static class GuardImageInspection
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
