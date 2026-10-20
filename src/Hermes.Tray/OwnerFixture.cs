using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Hermes.Tray.Smoke;

internal sealed record FixtureJob(string RunId, string Attempt, string Job)
{
    public static FixtureJob Current() => new(
        Required("GITHUB_RUN_ID"), Required("GITHUB_RUN_ATTEMPT"), Required("GITHUB_JOB"));

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 and <= 128 } value
            ? value : throw new InvalidOperationException("GitHub fixture identity is unavailable.");
}

internal sealed record OwnerFixtureReceipt(
    int Schema, string Kind, Guid FixtureId, FixtureJob Job,
    string ConfigSha256, string SettingsSha256);

internal interface IOwnerFixturePublisher
{
    void CreateStage(string path);
    void Publish(string staging, string destination);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsOwnerFixturePublisher : IOwnerFixturePublisher
{
    public void CreateStage(string path)
    {
        if (!CreateDirectoryW(path, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Publish(string staging, string destination) =>
        Directory.Move(staging, destination);

    // CreateDirectoryW fails if the stage already exists; Directory.Move never
    // replaces an existing destination. No ACL or account changes are requested.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, IntPtr securityAttributes);
}

internal sealed class OwnerFixtureStore(
    string ownerDirectory, string? receiptPath, FixtureJob job, IOwnerFixturePublisher publisher)
{
    internal const string MarkerName = ".hermes-installer-fixture.json";
    private const string FixtureKind = "hermes-installer-owner-fixture";
    private const int MaximumFixtureBytes = 65536;
    private string ReceiptPath => receiptPath
        ?? throw new InvalidOperationException("A fixture receipt path is required.");

    public OwnerFixtureReceipt Create(string archiveDirectory)
    {
        if (ProofFiles.Exists(ownerDirectory))
            throw new InvalidOperationException("Existing owner state cannot be adopted.");
        var parent = Path.GetDirectoryName(ownerDirectory)
            ?? throw new InvalidOperationException("Owner parent is unavailable.");
        TemporaryPaths.RequirePlainDirectory(parent);
        using var receipt = new FileStream(ReceiptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var (staging, evidence) = Stage(parent, archiveDirectory);
        publisher.Publish(staging, ownerDirectory);
        receipt.Write(JsonSerializer.SerializeToUtf8Bytes(evidence, ProofFiles.Json));
        receipt.Flush(flushToDisk: true);
        return evidence;
    }

    private (string Staging, OwnerFixtureReceipt Evidence) Stage(string parent, string archiveDirectory)
    {
        var id = Guid.NewGuid();
        var staging = Path.Combine(parent, $".hermes-installer-fixture-{id:N}");
        publisher.CreateStage(staging);
        var config = Config(archiveDirectory);
        var settings = Encoding.UTF8.GetBytes("SYNTHETIC retained preferences");
        var evidence = new OwnerFixtureReceipt(
            1, FixtureKind, id, job, Hash(config), Hash(settings));
        WriteNew(Path.Combine(staging, "config.yaml"), config);
        WriteNew(Path.Combine(staging, "synthetic-settings.txt"), settings);
        WriteNew(Path.Combine(staging, MarkerName),
            JsonSerializer.SerializeToUtf8Bytes(evidence, ProofFiles.Json));
        return (staging, evidence);
    }

    public OwnerFixtureReceipt? Admit()
    {
        if (receiptPath is not null)
            return Verify();
        if (ProofFiles.Exists(ownerDirectory))
            throw new InvalidOperationException("Unproven owner state is present.");
        return null;
    }

    public OwnerFixtureReceipt Verify()
    {
        TemporaryPaths.RequirePlainDirectory(ownerDirectory);
        var evidence = JsonSerializer.Deserialize<OwnerFixtureReceipt>(
            ReadRegular(ReceiptPath, 8192), ProofFiles.Json)
            ?? throw new InvalidDataException("Fixture receipt is missing.");
        RequireEvidence(evidence);
        RequireInventory();
        var marker = ReadRegular(Path.Combine(ownerDirectory, MarkerName), 8192);
        if (!marker.SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(evidence, ProofFiles.Json)) ||
            Hash(ReadRegular(Path.Combine(ownerDirectory, "config.yaml"))) != evidence.ConfigSha256 ||
            Hash(ReadRegular(Path.Combine(ownerDirectory, "synthetic-settings.txt"))) != evidence.SettingsSha256)
            throw new InvalidDataException("Owner fixture identity or bytes changed.");
        return evidence;
    }

    private void RequireEvidence(OwnerFixtureReceipt evidence)
    {
        if (evidence is not { Schema: 1, Kind: FixtureKind } ||
            evidence.FixtureId == Guid.Empty || evidence.Job != job ||
            !IsHash(evidence.ConfigSha256) || !IsHash(evidence.SettingsSha256))
            throw new InvalidDataException("Fixture receipt does not belong to this job.");
    }

    private void RequireInventory()
    {
        string[] expected = [MarkerName, "config.yaml", "synthetic-settings.txt"];
        var actual = Directory.EnumerateFileSystemEntries(ownerDirectory).Take(4)
            .Select(path => Path.GetFileName(path)
                ?? throw new InvalidDataException("Fixture entry name is unavailable."))
            .Order(StringComparer.Ordinal);
        if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal)))
            throw new InvalidDataException("Owner fixture contains unexpected state.");
    }

    private static byte[] Config(string archiveDirectory) => Encoding.UTF8.GetBytes(
        $"archive_dir: {JsonSerializer.Serialize(InstalledSmokeSettings.Absolute(archiveDirectory))}\n" +
        "accounts: []\nwatch_folders: []\n");

    private static byte[] ReadRegular(string path, int maximum = MaximumFixtureBytes)
    {
        RequireRegular(File.GetAttributes(path));
        return ProofFiles.Read(path, maximum);
    }

    internal static void RequireRegular(FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException("Fixture entry is not a plain file.");
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static bool IsHash(string value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}

internal static class OwnerFixture
{
    public static OwnerFixtureReceipt Create(string receiptPath, string archiveDirectory)
    {
        CiAdmission.Read(cleanOwner: true).Require();
        TemporaryPaths.RequireNewFile(receiptPath);
        TemporaryPaths.Require(archiveDirectory, directory: true);
        return Current(receiptPath).Create(archiveDirectory);
    }

    public static OwnerFixtureReceipt? RequireAdmission(string? receiptPath)
    {
        if (receiptPath is { } path)
            TemporaryPaths.Require(path);
        return Current(receiptPath).Admit();
    }

    public static void RequireExpected(string? receiptPath, OwnerFixtureReceipt? expected)
    {
        if (RequireAdmission(receiptPath) != expected)
            throw new InvalidDataException("Owner fixture admission changed during native proof.");
    }

    private static OwnerFixtureStore Current(string? receiptPath)
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HERMES_CONFIG_DIR")))
            throw new InvalidOperationException("Inherited owner configuration overrides are forbidden.");
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (Environment.GetEnvironmentVariable("APPDATA") is not { } appData ||
            !InstalledSource.SamePath(roaming, appData))
            throw new InvalidOperationException("The genuine default owner profile is unavailable.");
        return new(Path.Combine(roaming, "hermes"), receiptPath,
            FixtureJob.Current(), new WindowsOwnerFixturePublisher());
    }

    public static void Attach(
        JsonObject report, OwnerFixtureReceipt? receipt, bool unchanged)
    {
        report["ownerStateVerified"] = unchanged;
        report["ownerFixtureVerified"] = receipt is not null && unchanged;
        report["ownerFixtureId"] = receipt?.FixtureId.ToString("N");
        if (receipt is { } evidence)
            report["ownerFixtureFiles"] = JsonSerializer.SerializeToNode(
                new InstalledFileProof[]
                {
                    new("config.yaml", evidence.ConfigSha256),
                    new("synthetic-settings.txt", evidence.SettingsSha256)
                }, ProofFiles.Json);
    }
}
