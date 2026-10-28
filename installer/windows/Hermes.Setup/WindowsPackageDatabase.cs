using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal sealed partial class WindowsPackageDatabase : IPackageDatabase, IPackageAuthoringDatabase
{
    private const string Library = "msi.dll";

    private static readonly ImmutableDictionary<string, string[]> Columns =
        new Dictionary<string, string[]>
        {
            ["Property"] = ["Property", "Value"],
            ["Directory"] = ["Directory", "Directory_Parent", "DefaultDir"],
            ["Component"] = ["Component", "ComponentId", "Directory_", "Attributes", "Condition", "KeyPath"],
            ["File"] = ["File", "Component_", "FileName", "FileSize", "Version", "Language", "Attributes", "Sequence"],
            ["Media"] = ["DiskId", "LastSequence", "DiskPrompt", "Cabinet", "VolumeLabel", "Source"]
        }.ToImmutableDictionary();

    public PackageDatabaseImage Read(string packagePath)
    {
        var opened = new uint[1];
        Check(OpenDatabase(packagePath, 0, opened));
        using var database = new MsiHandle(opened[0]);
        var tables = Columns.ToImmutableDictionary(pair => pair.Key,
            pair => Rows(database.Value, pair.Key, pair.Value));
        var streams = StreamLengths(database.Value);
        var summaryHandle = new uint[1];
        Check(GetSummary(database.Value, null, 0, summaryHandle));
        using var summary = new MsiHandle(summaryHandle[0]);
        return new(Summary(summary.Value, 9).Text, Summary(summary.Value, 7).Text,
            Summary(summary.Value, 15).Number, tables, streams,
            Manifest(database.Value).ToImmutableArray());
    }

    private static ImmutableArray<ImmutableArray<string>> Rows(
        uint database, string table, IReadOnlyList<string> columns)
    {
        var names = string.Join(", ", columns.Select(column => $"`{column}`"));
        using var view = View(database, $"SELECT {names} FROM `{table}`");
        var rows = ImmutableArray.CreateBuilder<ImmutableArray<string>>();
        while (FetchRecord(view.Value) is { } record)
        {
            using (record)
                rows.Add(Enumerable.Range(1, columns.Count)
                    .Select(index => Text(record.Value, (uint)index)).ToImmutableArray());
            SetupIo.Require(rows.Count <= 100_000, SetupCode.MediaInvalid);
        }
        return rows.ToImmutable();
    }

    private static ImmutableDictionary<string, uint> StreamLengths(uint database)
    {
        using var view = View(database, "SELECT `Name`, `Data` FROM `_Streams`");
        var result = ImmutableDictionary.CreateBuilder<string, uint>(StringComparer.Ordinal);
        while (FetchRecord(view.Value) is { } record)
        {
            using (record)
                result.Add(Text(record.Value, 1), DataSize(record.Value, 2));
            SetupIo.Require(result.Count <= 100_000, SetupCode.MediaInvalid);
        }
        return result.ToImmutable();
    }

    private static byte[] Manifest(uint database)
    {
        using var view = View(database,
            "SELECT `Data` FROM `Binary` WHERE `Name` = 'HermesPayloadManifest'");
        using var record = FetchRecord(view.Value)
            ?? throw new SetupIoException(SetupCode.MediaInvalid);
        var length = DataSize(record.Value, 1);
        SetupIo.Require(length is > 0 and <= 16 * 1024 * 1024, SetupCode.MediaInvalid);
        using var output = new MemoryStream();
        ReadBinary(record.Value, output);
        SetupIo.Require(output.Length == length, SetupCode.MediaInvalid);
        using var extra = FetchRecord(view.Value);
        SetupIo.Require(extra is null, SetupCode.MediaInvalid);
        return output.ToArray();
    }

    private static void ReadBinary(uint record, Stream output)
    {
        var buffer = new byte[65_536];
        while (true)
        {
            uint[] length = [(uint)buffer.Length];
            Check(ReadStream(record, 1, buffer, length));
            if (length[0] == 0)
                return;
            output.Write(buffer, 0, checked((int)length[0]));
            SetupIo.Require(output.Length <= 16 * 1024 * 1024, SetupCode.MediaInvalid);
        }
    }

    private static MsiHandle View(uint database, string sql)
    {
        var opened = new uint[1];
        Check(OpenView(database, sql, opened));
        var view = new MsiHandle(opened[0]);
        try { Check(ExecuteView(view.Value, 0)); return view; }
        catch { view.Dispose(); throw; }
    }

    private static MsiHandle? FetchRecord(uint view)
    {
        var record = new uint[1];
        var result = Fetch(view, record);
        if (result == 259)
            return null;
        Check(result);
        return new(record[0]);
    }

    private static string Text(uint record, uint field)
    {
        var value = new StringBuilder(32_768);
        uint[] length = [(uint)value.Capacity];
        Check(GetString(record, field, value, length));
        return value.ToString();
    }

    private static (string Text, int Number) Summary(uint summary, uint property)
    {
        var text = new StringBuilder(32_768);
        var type = new uint[1];
        var number = new int[1];
        uint[] length = [(uint)text.Capacity];
        Check(SummaryProperty(summary, property, type, number, 0, text, length));
        return (text.ToString(), number[0]);
    }

    private static void Check(uint result)
    {
        if (result != 0)
            throw new Win32Exception(checked((int)result));
    }

    private sealed class MsiHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal MsiHandle(uint value) : base(ownsHandle: true) => SetHandle((nint)value);
        internal uint Value => checked((uint)handle.ToInt64());
        protected override bool ReleaseHandle() => CloseHandle(Value) == 0;
    }

    [DllImport(Library, EntryPoint = "MsiOpenDatabaseW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint OpenDatabase(string path, nint persistence, [Out] uint[] database);

    [DllImport(Library, EntryPoint = "MsiDatabaseOpenViewW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint OpenView(uint database, string query, [Out] uint[] view);

    [DllImport(Library, EntryPoint = "MsiViewExecute", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ExecuteView(uint view, uint record);

    [DllImport(Library, EntryPoint = "MsiViewFetch", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint Fetch(uint view, [Out] uint[] record);

    [DllImport(Library, EntryPoint = "MsiRecordGetStringW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetString(uint record, uint field,
        [Out] StringBuilder value, [In, Out] uint[] length);

    [DllImport(Library, EntryPoint = "MsiRecordDataSize", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint DataSize(uint record, uint field);

    [DllImport(Library, EntryPoint = "MsiRecordReadStream", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ReadStream(uint record, uint field,
        [Out] byte[] bytes, [In, Out] uint[] length);

    [DllImport(Library, EntryPoint = "MsiGetSummaryInformationW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSummary(uint database, string? path,
        uint updateCount, [Out] uint[] summary);

    [DllImport(Library, EntryPoint = "MsiSummaryInfoGetPropertyW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint SummaryProperty(uint summary, uint property,
        [Out] uint[] type, [Out] int[] number, nint fileTime,
        [Out] StringBuilder text, [In, Out] uint[] length);

    [DllImport(Library, EntryPoint = "MsiCloseHandle", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint CloseHandle(uint handle);
}
