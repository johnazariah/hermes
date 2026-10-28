using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OwnedAccessSourceTests
{
    [Fact]
    public async Task Supervisor_publishes_its_launched_handle_only_after_matching_health()
    {
        await using var rig = new Rig();
        Assert.Null(rig.Service.ReadAccess());
        rig.Start();
        var pid = await rig.Service.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        var owned = Assert.IsType<OwnedAccessState>(rig.Service.ReadAccess());
        Assert.Same(rig.RequireCurrent(), owned.Process);
        Assert.Equal(pid, owned.Process.Identity.ProcessId);
        Assert.Equal(pid, owned.ReadyHealthProcessId);
        Assert.Equal(rig.Service.Snapshot.Restart, owned.RestartGeneration);
        await rig.StopAsync();
        Assert.Null(rig.Service.ReadAccess());
    }

    [Fact]
    public async Task Actual_child_death_invalidates_capture_before_supervisor_poll()
    {
        await using var rig = new Rig();
        rig.Start();
        await rig.Service.Ready.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(rig.Service.ReadAccess());
        rig.RequireCurrent().Exit(37);
        Assert.Null(rig.Service.ReadAccess());
    }

    [Fact]
    public async Task Foreign_health_never_creates_an_access_capture()
    {
        await using var rig = new Rig();
        rig.Health.Foreign = new(true, true, 9876);
        rig.Start();
        await Assert.ThrowsAsync<LifecycleFault>(() => rig.Service.Ready);
        await rig.Worker;
        Assert.Null(rig.Service.ReadAccess());
        Assert.Empty(rig.Launcher.Processes);
    }
}
