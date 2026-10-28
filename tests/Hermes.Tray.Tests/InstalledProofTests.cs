using Hermes.Tray;
using Hermes.Tray.Smoke;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class InstalledProofTests
{
    public enum AdmissionFailure { Windows, Interactive, Actions, Hosted, Repository, Owner }

    [Theory]
    [InlineData(AdmissionFailure.Windows)]
    [InlineData(AdmissionFailure.Interactive)]
    [InlineData(AdmissionFailure.Actions)]
    [InlineData(AdmissionFailure.Hosted)]
    [InlineData(AdmissionFailure.Repository)]
    [InlineData(AdmissionFailure.Owner)]
    public void Diagnostic_admission_rejects_each_missing_prerequisite(AdmissionFailure failure)
    {
        var admission = new CiAdmission(
            failure != AdmissionFailure.Windows, failure != AdmissionFailure.Interactive,
            failure != AdmissionFailure.Actions, failure != AdmissionFailure.Hosted,
            failure != AdmissionFailure.Repository, failure != AdmissionFailure.Owner);
        Assert.Throws<InvalidOperationException>(admission.Require);
    }

    [Fact]
    public void Diagnostic_admission_accepts_only_the_complete_prerequisite_set() =>
        new CiAdmission(true, true, true, true, true, true).Require();

    [Fact]
    public void Installed_cli_does_not_accept_the_old_service_only_contract() =>
        Assert.Throws<ArgumentException>(() => InstalledSmokeSettings.Parse(
            ["--service-path", @"C:\installed\service\Hermes.Service.exe"]));

    [Theory]
    [InlineData("--ready-file")]
    [InlineData("--continue-file")]
    public void Hold_files_must_be_paired(string flag) =>
        Assert.Throws<ArgumentException>(() => InstalledSmokeSettings.Parse(
            [.. Arguments(), flag, @"C:\runner\hold.json"]));

    [Theory]
    [InlineData("9")]
    [InlineData("301")]
    public void Installed_timeout_is_bounded(string seconds) =>
        Assert.Throws<ArgumentException>(() => InstalledSmokeSettings.Parse(
            [.. Arguments(), "--timeout-seconds", seconds]));

    [Fact]
    public void Installed_cli_preserves_the_explicit_installed_apphost_and_hold_paths()
    {
        var result = InstalledSmokeSettings.Parse(
            [.. Arguments(), "--ready-file", @"C:\runner\ready.json",
             "--continue-file", @"C:\runner\continue.json"]);
        Assert.Equal(@"C:\installed\tray\Hermes.Tray.exe", result.TrayPath);
        Assert.Equal(@"C:\runner\ready.json", result.ReadyFile);
        Assert.Equal(@"C:\runner\continue.json", result.ContinueFile);
        Assert.Equal(150, result.TimeoutSeconds);
    }

    public enum IdentityFailure { WrongPath, Unknown, ReusedDuring, WrongPid, Unhealthy }

    [Theory]
    [InlineData(IdentityFailure.WrongPath)]
    [InlineData(IdentityFailure.Unknown)]
    [InlineData(IdentityFailure.ReusedDuring)]
    [InlineData(IdentityFailure.WrongPid)]
    [InlineData(IdentityFailure.Unhealthy)]
    public async Task Os_identity_failures_cannot_be_adopted_as_the_owned_child(IdentityFailure failure)
    {
        await using var rig = new Rig(new ManualClock());
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        var original = new ProcessStamp(pid, 100, @"C:\installed\service\Hermes.Service.exe");
        var source = new ReadingIdentitySource(call => BrokenStamp(original, failure, call));
        var verifier = new StableProcessVerifier(rig.Service, original.ImagePath, source);
        if (failure == IdentityFailure.Unhealthy)
            rig.Health.Healthy = false;
        await Assert.ThrowsAnyAsync<Exception>(() =>
            verifier.VerifyOwnershipAsync(new(pid, 0), CancellationToken.None));
        Assert.Equal(0, rig.RequireCurrent().ShutdownRequests);
        Assert.Equal(0, rig.RequireCurrent().Kills);
    }

    [Fact]
    public async Task Stable_os_start_identity_composes_with_the_existing_supervisor()
    {
        await using var rig = new Rig(new ManualClock());
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        var stamp = new ProcessStamp(pid, 100, @"C:\installed\service\Hermes.Service.exe");
        var source = new ReadingIdentitySource(_ => stamp);
        var verifier = new StableProcessVerifier(rig.Service, stamp.ImagePath, source);
        Assert.Equal(new(pid, 0),
            await verifier.VerifyOwnershipAsync(new(pid, 0), CancellationToken.None));
        Assert.Equal(stamp, verifier.Bound);
        Assert.Equal(2, source.Reads);
        await rig.StopAsync();
        Assert.Equal(new StopOutcome(true, false, true, 0), rig.Service.LastStop);
    }

    public enum Occupancy { Foreign, Installed, Unknown }

    [Theory]
    [InlineData(Occupancy.Foreign)]
    [InlineData(Occupancy.Installed)]
    [InlineData(Occupancy.Unknown)]
    public void Preflight_distinguishes_foreign_same_name_from_owned_or_unknown(Occupancy occupancy)
    {
        var source = new ReadingIdentitySource(_ => occupancy switch
        {
            Occupancy.Unknown => throw new UnauthorizedAccessException("synthetic-private-detail"),
            Occupancy.Installed => new(1001, 100, @"C:\installed\service\Hermes.Service.exe"),
            _ => new(1001, 100, @"C:\foreign\Hermes.Service.exe")
        });
        void Inspect() => InstalledSource.RefuseRunning(
            @"C:\installed\tray\Hermes.Tray.exe", new CandidateCatalog(), source);
        switch (occupancy)
        {
            case Occupancy.Foreign: Inspect(); break;
            case Occupancy.Installed: Assert.Throws<InvalidOperationException>(Inspect); break;
            case Occupancy.Unknown: Assert.Throws<UnauthorizedAccessException>(Inspect); break;
        }
    }

    [Fact]
    public async Task Evidence_bound_fails_instead_of_truncating_a_success_report()
    {
        var oracle = new SmokeOwnershipOracle(new OracleVerifier());
        oracle.Bind(new(ServicePhase.Running, EventCode.Healthy, 1001));
        foreach (var index in Enumerable.Range(0, SmokeOwnershipOracle.MaximumChecks))
            await oracle.ProveAsync($"proof:{index}", _ => Task.FromResult(true), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            oracle.ProveAsync("overflow", _ => Task.FromResult(true), CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() =>
            oracle.Bind(new(ServicePhase.Running, EventCode.Healthy, 1001)));
    }

    [Fact]
    public void Missing_installed_tray_dll_cannot_produce_source_proof()
    {
        using var layout = new Layout();
        var tray = layout.Touch("tray", "Hermes.Tray.exe");
        layout.Touch("service", "Hermes.Service.exe");
        layout.Touch("service", "Hermes.Service.dll");
        Assert.Throws<FileNotFoundException>(() => InstalledSource.Capture(tray));
    }

    [Fact]
    public void Changed_installed_tray_dll_changes_the_pinned_source_proof()
    {
        using var layout = new Layout();
        var tray = layout.Touch("tray", "Hermes.Tray.exe");
        var assembly = layout.Touch("tray", "Hermes.Tray.dll");
        layout.Touch("service", "Hermes.Service.exe");
        layout.Touch("service", "Hermes.Service.dll");
        var before = InstalledSource.Capture(tray);
        File.WriteAllText(assembly, "synthetic changed assembly bytes");
        var after = InstalledSource.Capture(tray);
        Assert.False(before.SequenceEqual(after));
        Assert.Equal(4, after.Length);
        Assert.NotEqual(before.Single(file => file.RelativePath == "tray/Hermes.Tray.dll").Sha256,
            after.Single(file => file.RelativePath == "tray/Hermes.Tray.dll").Sha256);
    }

    private static string[] Arguments() =>
        ["--installed-tray-path", @"C:\installed\tray\Hermes.Tray.exe",
         "--port", "31873", "--report", @"C:\runner\installed.json"];

    private static ProcessStamp BrokenStamp(ProcessStamp original, IdentityFailure failure, int call) =>
        failure switch
        {
            IdentityFailure.WrongPath => original with { ImagePath = @"C:\foreign\Hermes.Service.exe" },
            IdentityFailure.Unknown => throw new UnauthorizedAccessException("synthetic-private-detail"),
            IdentityFailure.ReusedDuring when call > 1 => original with { StartUtcTicks = 101 },
            IdentityFailure.WrongPid => original with { ProcessId = original.ProcessId + 1 },
            _ => original
        };
}

internal sealed class ReadingIdentitySource(Func<int, ProcessStamp> read) : IProcessIdentitySource
{
    public int Reads { get; private set; }
    public ProcessStamp Read(int processId) => read(++Reads);
}

internal sealed class CandidateCatalog : IProcessCatalog
{
    public IReadOnlyList<int> Candidates(string executableName) => [1001];
}
