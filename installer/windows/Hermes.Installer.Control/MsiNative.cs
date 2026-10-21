using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Hermes.Installer.Control;

[SupportedOSPlatform("windows")]
internal static unsafe partial class MsiNative
{
    private const string Library = "msi.dll";
    internal const uint InfoMessage = 0x04000000;
    internal const uint ErrorMessage = 0x01000000;
    internal const uint InfoFilter = 0x10;
    internal const uint ActionStartMessage = 0x08000000;
    internal const uint InstallStartMessage = 0x1a000000;
    internal const uint InstallEndMessage = 0x1b000000;

    internal static string Property(uint session, string name)
    {
        uint length = 0;
        var result = GetProperty(session, name, null, &length);
        if (result is not 0 and not 234 || length > 8192)
            throw new Win32Exception(checked((int)result));
        var buffer = new char[checked((int)length + 1)];
        fixed (char* pointer = buffer)
        {
            var capacity = checked((uint)buffer.Length);
            result = GetProperty(session, name, pointer, &capacity);
            if (result != 0) throw new Win32Exception(checked((int)result));
            return new string(pointer, 0, checked((int)capacity));
        }
    }

    internal static string RecordText(uint record) => RecordField(record, 0);

    internal static string RecordField(uint record, uint field)
    {
        uint length = 0;
        var result = GetRecordString(record, field, null, &length);
        if (result is not 0 and not 234 || length > 512)
            throw new Win32Exception(checked((int)result));
        var buffer = new char[checked((int)length + 1)];
        fixed (char* pointer = buffer)
        {
            var capacity = checked((uint)buffer.Length);
            result = GetRecordString(record, field, pointer, &capacity);
            if (result != 0)
                throw new Win32Exception(checked((int)result));
            return new(pointer, 0, checked((int)capacity));
        }
    }

    internal static int RecordInteger(uint record, uint field)
    {
        var value = GetRecordInteger(record, field);
        return value != int.MinValue
            ? value : throw new InvalidDataException("Missing MSI integer field.");
    }

    internal static int? RecordNullableInteger(uint record, uint field) =>
        GetRecordInteger(record, field) switch
        {
            int.MinValue => null,
            var value => value
        };

    [LibraryImport(Library, EntryPoint = "MsiRecordGetInteger")]
    private static partial int GetRecordInteger(uint record, uint field);

    internal static void Message(uint session, uint kind, string text)
    {
        var record = CreateRecord(0);
        if (record == 0) throw new InvalidOperationException("MSI record creation failed.");
        try
        {
            if (SetRecordString(record, 0, text) != 0 ||
                ProcessMessage(session, kind, record) < 0)
                throw new InvalidOperationException("MSI message delivery failed.");
        }
        finally { CloseHandle(record); }
    }

    internal static bool TryMessage(uint session, uint kind, string text)
    {
        var record = CreateRecord(0);
        if (record == 0)
            return false;
        try
        {
            return SetRecordString(record, 0, text) == 0 &&
                ProcessMessage(session, kind, record) >= 0;
        }
        finally { CloseHandle(record); }
    }

    [LibraryImport(Library, EntryPoint = "MsiGetPropertyW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetProperty(uint session, string name, char* value, uint* length);

    [LibraryImport(Library, EntryPoint = "MsiRecordGetStringW")]
    private static partial uint GetRecordString(uint record, uint field, char* value, uint* length);

    [LibraryImport(Library, EntryPoint = "MsiGetMode")]
    internal static partial int GetMode(uint session, int mode);

    [LibraryImport(Library, EntryPoint = "MsiCreateRecord")]
    private static partial uint CreateRecord(uint fields);

    [LibraryImport(Library, EntryPoint = "MsiRecordSetStringW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint SetRecordString(uint record, uint field, string text);

    [LibraryImport(Library, EntryPoint = "MsiProcessMessage")]
    private static partial int ProcessMessage(uint session, uint kind, uint record);

    [LibraryImport(Library, EntryPoint = "MsiCloseHandle")]
    private static partial uint CloseHandle(uint handle);

    [LibraryImport(Library, EntryPoint = "MsiInstallProductW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint InstallProduct(string package, string properties);

    [LibraryImport(Library, EntryPoint = "MsiConfigureProductExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint ConfigureProduct(string product, int level, int state, string properties);

    [LibraryImport(Library, EntryPoint = "MsiSetInternalUI")]
    internal static partial uint SetInternalUi(uint level, nint* window);

    [LibraryImport(Library, EntryPoint = "MsiSetExternalUIRecord")]
    internal static partial uint SetExternalUi(nint handler, uint filter, nint context, nint* previous);
}
