using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Hermes.Setup;
using Hermes.Setup.Tests;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class WindowsSignatureDatabaseTests
{
    // Imported through MSI's own IDT implementation, independently of the
    // production numeric type words and the synthetic observation fixture.
    private static ImmutableArray<string> Types { get; } =
        ["s72", "s255", "S20", "S20", "I4", "I4", "I4", "I4", "S255"];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualReaderPreservesAbsenceOrExactObservedEmptySchema(bool present)
    {
        using var database = new ScratchSignatureMsi(present ? Types : []);
        var snapshot = new WindowsPackageDatabase().ReadAuthoring(database.PackagePath);
        Assert.Equal(present, snapshot.HasTable(SignatureFixture.Name));
        Assert.Empty(snapshot.OptionalTable(SignatureFixture.Name));
        if (present)
            AssertSignatureDefinitions(snapshot);
        foreach (var mode in Enum.GetValues<PackageAuthoringMode>())
            Assert.IsType<Effect<ValidatedPackageProfile>.Success>(
                PackageAuthoringValidator.Validate(snapshot, mode, database.Authoring.Approved));
    }

    [Theory]
    [InlineData("ExistingInstallDirectory")]
    [InlineData("UnreferencedSignature")]
    public void ActualReaderDoesNotDiscardSchemaCorrectSignatureRows(string key)
    {
        using var database = new ScratchSignatureMsi(Types, key);
        var snapshot = new WindowsPackageDatabase().ReadAuthoring(database.PackagePath);
        AssertSignatureDefinitions(snapshot);
        var row = Assert.Single(snapshot.RequireTable(SignatureFixture.Name));
        Assert.Equal<string>(SignatureFixture.Row.SetItem(0, key), row);
        AssertRefused(snapshot, database.Authoring.Approved);
    }

    [Theory]
    [InlineData(1, "l255")]
    [InlineData(1, "S255")]
    [InlineData(1, "L255")]
    [InlineData(1, "s254")]
    [InlineData(2, "s20")]
    [InlineData(2, "S21")]
    [InlineData(4, "I2")]
    [InlineData(8, "L255")]
    public void ActualReaderRetainsTypeDifferencesForStrictValidation(int index, string type)
    {
        using var database = new ScratchSignatureMsi(Types.SetItem(index, type));
        var snapshot = new WindowsPackageDatabase().ReadAuthoring(database.PackagePath);
        Assert.True(snapshot.HasTable(SignatureFixture.Name));
        Assert.Empty(snapshot.RequireTable(SignatureFixture.Name));
        AssertRefused(snapshot, database.Authoring.Approved);
    }

    private static void AssertSignatureDefinitions(PackageAuthoringSnapshot snapshot)
    {
        var actual = snapshot.ColumnDefinitions
            .Where(row => row[0] == SignatureFixture.Name)
            .OrderBy(row => int.Parse(row[1], CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(SignatureFixture.Definitions.Length, actual.Length);
        foreach (var pair in SignatureFixture.Definitions.Zip(actual))
            Assert.Equal<string>(pair.First, pair.Second);
    }

    private static void AssertRefused(
        PackageAuthoringSnapshot snapshot, ApprovedGuardProfile approved)
    {
        foreach (var mode in Enum.GetValues<PackageAuthoringMode>())
            Assert.Equal(SetupCode.MediaInvalid,
                Assert.IsType<Effect<ValidatedPackageProfile>.Failure>(
                    PackageAuthoringValidator.Validate(snapshot, mode, approved)).Code);
    }
}

// Scratch database authoring and readback only. This is not genuine candidate
// media, an installed-product fixture, or evidence of successful installation.
[SupportedOSPlatform("windows")]
internal sealed class ScratchSignatureMsi : IDisposable
{
    private readonly string _root =
        Directory.CreateTempSubdirectory("Hermes.Signature.Tests-").FullName;

    internal AuthoringFixture Authoring { get; } = AuthoringFixture.Create();
    internal string PackagePath => Path.Combine(_root, "source.msi");

    internal ScratchSignatureMsi(ImmutableArray<string> types, string signatureKey = "")
    {
        try { Create(types, signatureKey); }
        catch { Dispose(); throw; }
    }

    private void Create(ImmutableArray<string> types, string signatureKey)
    {
        using var installer = ComObject.Installer();
        using var database = new ComObject(
            (object)installer.Value.OpenDatabase(PackagePath, 3));
        ImportBaseTables(installer.Instance, database.Instance);
        ImportBinaries(installer.Instance, database.Instance);
        if (!types.IsDefaultOrEmpty)
            ImportSignature(installer.Instance, database.Instance, types, signatureKey);
        WriteCabinet(installer.Instance, database.Instance);
        WriteSummary(database.Instance);
        database.Value.Commit();
    }

    private void ImportBaseTables(object installer, object database)
    {
        foreach (var pair in Authoring.Snapshot.Tables)
        {
            var columns = PackageAuthoringSchema.Columns[pair.Key];
            var types = columns.Select((_, index) => index == 0 ? "s72" : "S0")
                .ToImmutableArray();
            ImmutableArray<string> keys = pair.Key switch
            {
                "FeatureComponents" => [columns[0], columns[1]],
                "Upgrade" => [columns[0], columns[^1]],
                _ => [columns[0]]
            };
            Import(installer, database, pair.Key, columns, types, keys, pair.Value);
        }
    }

    private void ImportBinaries(object installer, object database)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, "Binary"));
        foreach (var pair in Authoring.Snapshot.Binaries)
            File.WriteAllBytes(Path.Combine(directory.FullName, pair.Key), pair.Value.ToArray());
        var rows = Authoring.Snapshot.Binaries.Keys
            .Select(name => ImmutableArray.Create(name, name)).ToImmutableArray();
        Import(installer, database, "Binary",
            ["Name", "Data"], ["s72", "v0"], ["Name"], rows);
    }

    private void ImportSignature(
        object installer, object database, ImmutableArray<string> types, string key)
    {
        ImmutableArray<ImmutableArray<string>> rows = key.Length == 0
            ? [] : [SignatureFixture.Row.SetItem(0, key)];
        Import(installer, database, SignatureFixture.Name,
            SignatureFixture.Definitions.Select(row => row[2]).ToImmutableArray(),
            types, [SignatureFixture.Name], rows);
    }

    private void Import(
        object installer, object database, string table,
        ImmutableArray<string> columns, ImmutableArray<string> types,
        ImmutableArray<string> keys, ImmutableArray<ImmutableArray<string>> rows)
    {
        var filename = table + ".idt";
        var lines = new[]
        {
            string.Join('\t', columns),
            string.Join('\t', types),
            string.Join('\t', keys.Prepend(table))
        }.Concat(rows.Select(row => string.Join('\t', row)));
        File.WriteAllText(Path.Combine(_root, filename),
            string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
        try { ((dynamic)database).Import(_root, filename); }
        catch (COMException) { ReportImportError(installer); throw; }
    }

    private static void ReportImportError(object installer)
    {
        string id;
        try { id = ReadImportErrorId(installer); }
        catch { id = "unknown:capture-failed"; }
        try { Console.Error.WriteLine("MSI_IMPORT_ERROR_ID=" + id); }
        catch { /* Optional diagnostics must not replace the Import exception. */ }
    }

    private static string ReadImportErrorId(object installer)
    {
        if (((dynamic)installer).LastErrorRecord is not object error)
            return "unknown:no-last-error-record";
        using var record = new ComObject(error);
        var id = (int)record.Value.IntegerData[1];
        return id switch
        {
            int.MinValue => "unknown:no-integer-error-id",
            _ => id.ToString(CultureInfo.InvariantCulture)
        };
    }

    private void WriteCabinet(object installer, object database)
    {
        var path = Path.Combine(_root, "payload.cab");
        File.WriteAllBytes(path, [1]);
        using var record = new ComObject((object)((dynamic)installer).CreateRecord(2));
        record.Value.StringData[1] = "payload.cab";
        record.Value.SetStream(2, path);
        using var view = new ComObject((object)((dynamic)database).OpenView(
            "INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)"));
        view.Value.Execute(record.Value);
    }

    private void WriteSummary(object database)
    {
        using var summary = new ComObject(
            (object)((dynamic)database).SummaryInformation[20]);
        summary.Value.Property[1] = (short)1252;
        summary.Value.Property[7] = Authoring.Snapshot.Package.Template;
        summary.Value.Property[9] = Authoring.Snapshot.Package.PackageCode;
        summary.Value.Property[15] = Authoring.Snapshot.Package.WordCount;
        summary.Value.Persist();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class ComObject(object instance) : IDisposable
    {
        internal object Instance => instance;
        internal dynamic Value => instance;

        internal static ComObject Installer()
        {
            var type = Type.GetTypeFromProgID("WindowsInstaller.Installer")
                ?? throw new InvalidOperationException("Windows Installer COM is unavailable.");
            return new(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException("Windows Installer COM creation failed."));
        }

        public void Dispose() => Marshal.FinalReleaseComObject(instance);
    }
}
