using Hermes.Servicing;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class ServiceSelectionTests
{
    [Theory]
    [InlineData(RefusalReason.UnownedInstallation)]
    [InlineData(RefusalReason.WrongGeneration)]
    [InlineData(RefusalReason.InvalidState)]
    public void Rejected_selection_prevents_owner_writes_and_process_start(RefusalReason reason)
    {
        var starts = 0;
        var writes = 0;
        var options = new TrayOptions(@"C:\synthetic\owner", 31991, @"C:\elsewhere\Service.exe");
        var startup = new NativeStartupContext(new Admitted(
            new SelectingLease(_ => new ServiceRefused(reason))));
        var launcher = new NativeProcessLauncher(new MemoryLog(),
            (_, _) => { starts++; return new FakeProcess(123, true); });
        Assert.Throws<InvalidOperationException>(() =>
        {
            var admitted = options.ForStartup(startup);
            writes++;
            launcher.Start(admitted);
        });
        Assert.Throws<InvalidOperationException>(() => launcher.Start(options with { Startup = startup }));
        Assert.Equal(0, writes);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void Missing_admission_or_opaque_token_never_reaches_native_start()
    {
        var starts = 0;
        var options = new TrayOptions(@"C:\synthetic\owner", 31991, @"C:\synthetic\Service.exe");
        var launcher = new NativeProcessLauncher(new MemoryLog(),
            (_, _) => { starts++; return new FakeProcess(123, true); });
        Assert.Throws<InvalidOperationException>(() => launcher.Start(options));
        var opaque = new NativeStartupContext(new Admitted(new OpaqueToken()));
        Assert.Throws<InvalidOperationException>(() => options.ForStartup(opaque));
        Assert.Throws<InvalidOperationException>(() => launcher.Start(options with { Startup = opaque }));
        Assert.Equal(0, starts);
    }

    [Fact]
    public void Unknown_child_result_is_not_source_override_permission()
    {
        var startup = new NativeStartupContext(new Admitted(new SelectingLease(_ => new UnknownSelection())));
        var options = new TrayOptions(@"C:\synthetic\owner", 31991, @"C:\synthetic\Service.exe");
        Assert.Throws<InvalidOperationException>(() => options.ForStartup(startup));
    }

    [Fact]
    public void Selected_path_is_used_and_selection_is_rechecked_immediately_before_start()
    {
        const string enrolled = @"C:\synthetic\Hermes\App\service\Hermes.Service.exe";
        var selections = new List<string>();
        var lease = new SelectingLease(path =>
        {
            selections.Add(path);
            return new ServiceSelected(enrolled);
        });
        var startup = new NativeStartupContext(new Admitted(lease));
        var options = new TrayOptions(@"C:\synthetic\owner", 31991, enrolled).ForStartup(startup);
        var starts = new List<string>();
        var launcher = new NativeProcessLauncher(new MemoryLog(),
            (info, _) => { starts.Add(info.FileName); return new FakeProcess(123, true); });
        Assert.Equal(123, launcher.Start(options).Id);
        Assert.Same(startup, options.Startup);
        Assert.Equal([enrolled, enrolled], selections);
        Assert.Equal([enrolled], starts);
    }

    [Fact]
    public void A_failed_recheck_cannot_launch_a_previously_selected_child()
    {
        var checks = 0;
        var starts = 0;
        var startup = new NativeStartupContext(new Admitted(new SelectingLease(path =>
            ++checks == 1 ? new ServiceSelected(path) : new ServiceRefused(RefusalReason.WrongGeneration))));
        var options = new TrayOptions(@"C:\synthetic\owner", 31991, @"C:\synthetic\Service.exe")
            .ForStartup(startup);
        var launcher = new NativeProcessLauncher(new MemoryLog(),
            (_, _) => { starts++; return new FakeProcess(123, true); });
        Assert.Throws<InvalidOperationException>(() => launcher.Start(options));
        Assert.Equal(2, checks);
        Assert.Equal(0, starts);
    }

    [Fact]
    public void Source_override_survives_only_with_positive_outside_context()
    {
        using var layout = new Layout();
        var service = layout.Touch("development", ServiceWire.Executable);
        var startup = new NativeStartupContext(new OutsideKnownInstallation());
        var options = TrayOptions.Parse(
            ["--service-path", service, "--config-dir", Path.Combine(layout.Root, "owner")],
            _ => null, layout.Tray).ForStartup(startup);
        var starts = new List<string>();
        var launcher = new NativeProcessLauncher(new MemoryLog(),
            (info, _) => { starts.Add(info.FileName); return new FakeProcess(123, true); });
        Assert.Equal(123, launcher.Start(options).Id);
        Assert.Equal([service], starts);
        Assert.False(Directory.Exists(options.ConfigDirectory));
    }

    private sealed record OpaqueToken : IProcessLease;
    private sealed record UnknownSelection : ServiceSelection;
}
