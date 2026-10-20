using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Hermes.Servicing;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

public sealed class KernelGateTests
{
    public enum Win32PathExpectation { CanonicalAlias, PathNotFound }

    [Fact]
    public void Runtime_reader_blocks_exclusive_open_without_changing_bytes()
    {
        using var fixture = new Fixture();
        var before = File.ReadAllBytes(fixture.GatePath);
        var opened = Assert.IsType<GateOpened>(new WindowsRuntimeGate(fixture.GatePath).TryOpen());
        using var reader = opened.Lease;
        var error = Assert.Throws<IOException>(() => ExclusiveGate.Open(fixture.GatePath));
        Assert.Contains(error.HResult & 0xffff, new[] { 32, 33 });
        Assert.Equal(before, File.ReadAllBytes(fixture.GatePath));
    }

    [Fact]
    public void Multiple_runtime_readers_coexist_but_servicing_cannot_enter()
    {
        using var fixture = new Fixture();
        var gate = new WindowsRuntimeGate(fixture.GatePath);
        using var first = Assert.IsType<GateOpened>(gate.TryOpen()).Lease;
        using var second = Assert.IsType<GateOpened>(gate.TryOpen()).Lease;
        Assert.NotNull(first.ReadReady().Installation);
        Assert.NotNull(second.ReadReady().Installation);
        Assert.Throws<IOException>(() => ExclusiveGate.Open(fixture.GatePath));
    }

    [Fact]
    public void Exclusive_handle_blocks_startup_and_closed_survives_its_release()
    {
        using var fixture = new Fixture();
        var gate = new WindowsRuntimeGate(fixture.GatePath);
        using (var exclusive = ExclusiveGate.Open(fixture.GatePath))
        {
            exclusive.CloseAdmission();
            Assert.Equal(new GateUnavailable(RefusalReason.Busy), gate.TryOpen());
        }
        using var reader = Assert.IsType<GateOpened>(gate.TryOpen()).Lease;
        var error = Assert.Throws<AdmissionException>(() => reader.ReadReady());
        Assert.Equal(RefusalReason.Closed, error.Reason);
    }

    [Fact]
    public void Incomplete_ready_body_remains_closed()
    {
        using var fixture = new Fixture();
        using (var exclusive = ExclusiveGate.Open(fixture.GatePath))
            exclusive.CloseAdmission();
        using (var stream = new FileStream(fixture.GatePath, FileMode.Open,
            FileAccess.Write, FileShare.None))
        {
            stream.Position = GateCodec.HeaderSize;
            stream.Write("incomplete"u8);
            stream.Flush(flushToDisk: true);
        }
        using var reader = Assert.IsType<GateOpened>(
            new WindowsRuntimeGate(fixture.GatePath).TryOpen()).Lease;
        Assert.Equal(RefusalReason.Closed,
            Assert.Throws<AdmissionException>(() => reader.ReadReady()).Reason);
    }

    [Fact]
    public void Ready_absent_is_explicit_and_unknown_files_are_untouched()
    {
        using var fixture = new Fixture();
        var before = SHA256.HashData(File.ReadAllBytes(fixture.UnknownPath));
        using (var exclusive = ExclusiveGate.Open(fixture.GatePath))
            exclusive.PublishReady(new GateReadyState(null));
        using var reader = Assert.IsType<GateOpened>(
            new WindowsRuntimeGate(fixture.GatePath).TryOpen()).Lease;
        Assert.Null(reader.ReadReady().Installation);
        Assert.Equal(before, SHA256.HashData(File.ReadAllBytes(fixture.UnknownPath)));
    }

    [Theory]
    [InlineData("service/Hermes.Service.dll.", Win32PathExpectation.CanonicalAlias)]
    [InlineData("service/Hermes.Service.dll ", Win32PathExpectation.CanonicalAlias)]
    [InlineData("service./Hermes.Service.dll", Win32PathExpectation.CanonicalAlias)]
    [InlineData("service /Hermes.Service.dll", Win32PathExpectation.PathNotFound)]
    public void Noncanonical_win32_paths_are_rejected_as_contradictory_inventory(
        string path, Win32PathExpectation expectation)
    {
        using var fixture = new Fixture();
        RequireWin32Resolution(fixture, path, expectation);
        var state = new GateReadyState(new ReadyInstallation(
            new(HermesServicingProtocol.UpgradeCode, fixture.AppPath,
                HermesServicingProtocol.AdmissionAbi),
            new Guid("44444444-4444-4444-4444-444444444444"),
            [new("service/Hermes.Service.dll",
                new Guid("11111111-1111-1111-1111-111111111111")),
             new(path, new Guid("22222222-2222-2222-2222-222222222222"))]));
        AssertInvalidPublication(fixture, state);
    }

    private static void RequireWin32Resolution(Fixture fixture, string path,
        Win32PathExpectation expectation)
    {
        const string content = "Synthetic image bytes, not a loaded assembly.";
        Directory.CreateDirectory(Path.Combine(fixture.AppPath, "service"));
        File.WriteAllText(Path.Combine(fixture.AppPath, "service", "Hermes.Service.dll"), content);
        using var handle = OpenWin32File(Path.Combine(fixture.AppPath, path.Replace('/', '\\')),
            0x80000000, FileShare.Read, 0, FileMode.Open, 0, 0);
        var error = Marshal.GetLastWin32Error();
        if (expectation is Win32PathExpectation.PathNotFound)
        {
            Assert.True(handle.IsInvalid, "CreateFileW unexpectedly opened the missing path.");
            Assert.Equal(3, error); // ERROR_PATH_NOT_FOUND
            return;
        }
        AssertWin32Alias(handle, error, content);
    }

    private static void AssertWin32Alias(SafeFileHandle handle, int error, string content)
    {
        Assert.False(handle.IsInvalid, $"CreateFileW failed: {error}");
        using var stream = new FileStream(handle, FileAccess.Read);
        using var reader = new StreamReader(stream);
        Assert.Equal(content, reader.ReadToEnd());
    }

    private static void AssertInvalidPublication(Fixture fixture, GateReadyState state)
    {
        var before = File.ReadAllBytes(fixture.GatePath);
        using (var exclusive = ExclusiveGate.Open(fixture.GatePath))
            Assert.Equal(RefusalReason.InvalidState,
                Assert.Throws<AdmissionException>(() => exclusive.PublishReady(state)).Reason);
        Assert.Equal(before, File.ReadAllBytes(fixture.GatePath));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", ExactSpelling = true,
        CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle OpenWin32File(
        string name, uint desiredAccess, FileShare shareMode, nint securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, nint templateFile);

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory =
            Path.Combine(Path.GetTempPath(), $"hermes-gate-unit-{Guid.NewGuid():N}");

        internal string GatePath => Path.Combine(_directory, "admission.bin");
        internal string UnknownPath => Path.Combine(_directory, "user-owned.txt");
        internal string AppPath => Path.Combine(_directory, "App");

        internal Fixture()
        {
            Directory.CreateDirectory(_directory);
            File.WriteAllText(UnknownPath, "Synthetic user-owned content.");
            using var writer = ExclusiveGate.Open(GatePath, create: true);
            writer.PublishReady(new GateReadyState(new ReadyInstallation(
                new(HermesServicingProtocol.UpgradeCode,
                    Path.Combine(_directory, "App"), HermesServicingProtocol.AdmissionAbi),
                Guid.NewGuid(), [new("service/Hermes.Service.dll", Guid.NewGuid())])));
        }

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }
}
