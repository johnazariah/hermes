using System.Text;

namespace Hermes.Setup.Tests;

// Independent encoding ported from the temporary producer-profile controls.
// The DATA bytes follow runtime v9.0.21 DebugHeader.cpp, not production builders.
// This synthetic image is neither actual producer evidence nor executable trust.
internal static class NativeAotGuardFixture
{
    internal static byte[] Create(int permutation = 0)
    {
        var bytes = new byte[0xa00];
        Headers(bytes);
        Sections(bytes);
        Directory(bytes);
        Names(bytes);
        Convert.FromHexString(
            "444E444804000000010000000000000000000000000000000000000000000000")
            .CopyTo(bytes, 0x800);
        Permute(bytes, permutation);
        return bytes;
    }

    private static void Headers(byte[] bytes)
    {
        AuthoringFixture.Put16(bytes, 0, 0x5a4d);
        AuthoringFixture.Put32(bytes, 0x3c, 0x80);
        AuthoringFixture.Put32(bytes, 0x80, 0x4550);
        AuthoringFixture.Put16(bytes, 0x84, 0x8664);
        AuthoringFixture.Put16(bytes, 0x86, 3);
        AuthoringFixture.Put16(bytes, 0x94, 0xf0);
        AuthoringFixture.Put16(bytes, 0x96, 0x2022);
        AuthoringFixture.Put16(bytes, 0x98, 0x20b);
        AuthoringFixture.Put32(bytes, 0xa8, 0x1000);
        AuthoringFixture.Put32(bytes, 0xac, 0x1000);
        AuthoringFixture.Put32(bytes, 0xb8, 0x1000);
        AuthoringFixture.Put32(bytes, 0xbc, 0x200);
        AuthoringFixture.Put32(bytes, 0xd0, 0x4000);
        AuthoringFixture.Put32(bytes, 0xd4, 0x200);
        AuthoringFixture.Put16(bytes, 0xdc, 2);
        AuthoringFixture.Put32(bytes, 0x104, 16);
    }

    private static void Sections(byte[] bytes)
    {
        Section(bytes, 0x188, 0x1000, 0x200, 0x200, 0x60000020);
        Section(bytes, 0x1b0, 0x2000, 0x400, 0x400, 0x40000040);
        Section(bytes, 0x1d8, 0x3000, 0x800, 0x200, 0xc0000040);
    }

    private static void Section(
        byte[] bytes, int offset, uint rva, uint raw, uint size, uint flags)
    {
        AuthoringFixture.Put32(bytes, offset + 8, size);
        AuthoringFixture.Put32(bytes, offset + 12, rva);
        AuthoringFixture.Put32(bytes, offset + 16, size);
        AuthoringFixture.Put32(bytes, offset + 20, raw);
        AuthoringFixture.Put32(bytes, offset + 36, flags);
    }

    private static void Directory(byte[] bytes)
    {
        AuthoringFixture.Put32(bytes, 0x108, 0x2000);
        AuthoringFixture.Put32(bytes, 0x10c, 0x200);
        AuthoringFixture.Put32(bytes, 0x40c, 0x20a0);
        AuthoringFixture.Put32(bytes, 0x410, 1);
        AuthoringFixture.Put32(bytes, 0x414, 3);
        AuthoringFixture.Put32(bytes, 0x418, 3);
        AuthoringFixture.Put32(bytes, 0x41c, 0x2040);
        AuthoringFixture.Put32(bytes, 0x420, 0x2050);
        AuthoringFixture.Put32(bytes, 0x424, 0x2060);
        AuthoringFixture.Put32(bytes, 0x450, 0x2100);
        AuthoringFixture.Put32(bytes, 0x454, 0x2140);
        AuthoringFixture.Put32(bytes, 0x458, 0x2180);
    }

    private static void Names(byte[] bytes)
    {
        Encoding.ASCII.GetBytes("Synthetic.dll\0").CopyTo(bytes, 0x4a0);
        Encoding.ASCII.GetBytes("DotNetRuntimeDebugHeader\0").CopyTo(bytes, 0x500);
        Encoding.ASCII.GetBytes("HermesPreflight\0").CopyTo(bytes, 0x540);
        Encoding.ASCII.GetBytes("HermesTransactionFence\0").CopyTo(bytes, 0x580);
    }

    private static void Permute(byte[] bytes, int permutation)
    {
        int[][] slots = [
            [0, 1, 2], [0, 2, 1], [1, 0, 2],
            [1, 2, 0], [2, 0, 1], [2, 1, 0]];
        uint[] targets = [0x3000, 0x1000, 0x1010];
        foreach (var (role, slot) in slots[permutation].Select((slot, role) => (role, slot)))
        {
            AuthoringFixture.Put32(bytes, 0x440 + slot * 4, targets[role]);
            AuthoringFixture.Put16(bytes, 0x460 + role * 2, checked((ushort)slot));
        }
    }
}
