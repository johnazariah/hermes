using System.ComponentModel;
using System.Text.Json;
using Hermes.Tray;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class BrowserNavigationTests
{
    private const string ExternalAddress = "https://browser-launch.example.invalid/";
    private const string FileAddress = "file:///C:/HermesBrowserFixture/example.txt";
    private const string ScriptAddress = "javascript:void(0)";
    private const string DataAddress = "data:text/plain,browser-fixture";
    private const string MalformedAddress = "synthetic relative address";
    private const string UserInfoAddress = "https://fixture-user@userinfo.browser-launch.example.invalid/";
    private const string SyntheticAddress = "https://synthetic.browser-launch.example.invalid/";
    private const string FtpAddress = "ftp://ftp.browser-launch.example.invalid/";
    private const string LaunchDetail = "synthetic-launch-detail";

    public enum Callback { Navigation, NewWindow }
    public enum LaunchFailure { Win32, FileNotFound, Io, Unauthorized, InvalidOperation }

    [Theory]
    [InlineData(Callback.Navigation, LaunchFailure.Win32)]
    [InlineData(Callback.NewWindow, LaunchFailure.Win32)]
    [InlineData(Callback.Navigation, LaunchFailure.FileNotFound)]
    [InlineData(Callback.NewWindow, LaunchFailure.FileNotFound)]
    [InlineData(Callback.Navigation, LaunchFailure.Io)]
    [InlineData(Callback.NewWindow, LaunchFailure.Io)]
    [InlineData(Callback.Navigation, LaunchFailure.Unauthorized)]
    [InlineData(Callback.NewWindow, LaunchFailure.Unauthorized)]
    [InlineData(Callback.Navigation, LaunchFailure.InvalidOperation)]
    [InlineData(Callback.NewWindow, LaunchFailure.InvalidOperation)]
    public void Browser_failure_cannot_escape_either_shared_callback_body(Callback callback, LaunchFailure failure)
    {
        var error = Failure(failure);
        var launcher = new RecordingExternalLauncher(error);
        var log = new MemoryLog();
        var notification = new RecordingBrowserNotification();
        var navigation = Navigation(launcher, log, notification);
        Assert.True(Invoke(callback, navigation, ExternalAddress));
        Assert.Equal(new Uri(ExternalAddress), Assert.Single(launcher.Targets));
        Assert.Equal(1, notification.Count);
        var entry = Assert.Single(log.Entries);
        Assert.Equal(EventCode.BrowserFailed, entry.Code);
        Assert.Equal(SafeFailure.Describe(error), entry.Detail);
        Assert.DoesNotContain(ExternalAddress, JsonSerializer.Serialize(log.Entries));
        Assert.DoesNotContain(LaunchDetail, entry.Detail ?? "");
    }

    [Theory]
    [InlineData(Callback.Navigation)]
    [InlineData(Callback.NewWindow)]
    public void Healthy_external_launch_runs_once_without_error_or_fallback(Callback callback)
    {
        var launcher = new RecordingExternalLauncher();
        var log = new MemoryLog();
        var notification = new RecordingBrowserNotification();
        Assert.True(Invoke(callback, Navigation(launcher, log, notification), ExternalAddress));
        Assert.Equal(new Uri(ExternalAddress), Assert.Single(launcher.Targets));
        Assert.Equal(0, notification.Count);
        Assert.Empty(log.Entries);
    }

    [Theory]
    [InlineData(FileAddress, false)]
    [InlineData(ScriptAddress, false)]
    [InlineData(DataAddress, false)]
    [InlineData(MalformedAddress, false)]
    [InlineData(UserInfoAddress, false)]
    [InlineData(SyntheticAddress, true)]
    [InlineData(FtpAddress, false)]
    public void Both_callbacks_preserve_protocol_userinfo_and_synthetic_restrictions(string address, bool synthetic)
    {
        var launcher = new RecordingExternalLauncher();
        var log = new MemoryLog();
        var notification = new RecordingBrowserNotification();
        var navigation = Navigation(launcher, log, notification, synthetic);
        Assert.True(Invoke(Callback.Navigation, navigation, address));
        Assert.True(Invoke(Callback.NewWindow, navigation, address));
        Assert.Empty(launcher.Targets);
        Assert.Empty(log.Entries);
        Assert.Equal(0, notification.Count);
    }

    [Fact]
    public void Canonical_navigation_remains_internal_and_new_windows_remain_handled()
    {
        var launcher = new RecordingExternalLauncher();
        var log = new MemoryLog();
        var notification = new RecordingBrowserNotification();
        var navigation = Navigation(launcher, log, notification);
        var canonical = Options().BaseAddress.AbsoluteUri;
        Assert.False(Invoke(Callback.Navigation, navigation, canonical));
        Assert.Empty(launcher.Targets);
        Assert.True(Invoke(Callback.NewWindow, navigation, canonical));
        Assert.Equal(new Uri(canonical), Assert.Single(launcher.Targets));
        Assert.Empty(log.Entries);
        Assert.Equal(0, notification.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reporting_failures_do_not_rethrow_the_launch_failure_into_the_ui_callback(bool brokenLog)
    {
        var memory = new MemoryLog();
        IServiceLog log = brokenLog ? new FailingBrowserLog(memory) : memory;
        var notification = new RecordingBrowserNotification(
            brokenLog ? null : new InvalidOperationException("synthetic-notification-detail"));
        var launcher = new RecordingExternalLauncher(new Win32Exception(2, LaunchDetail));
        Assert.True(Invoke(Callback.Navigation, Navigation(launcher, log, notification), ExternalAddress));
        Assert.Equal(new Uri(ExternalAddress), Assert.Single(launcher.Targets));
        Assert.Equal(1, notification.Count);
        Assert.NotEmpty(memory.Entries);
        Assert.All(memory.Entries, entry => Assert.Equal(EventCode.BrowserFailed, entry.Code));
        var serialized = JsonSerializer.Serialize(memory.Entries);
        Assert.DoesNotContain("synthetic-", serialized);
        Assert.DoesNotContain(ExternalAddress, serialized);
    }

    private static TrayOptions Options(bool synthetic = false) =>
        new(@"C:\browser-fixture\config", 31991, @"C:\browser-fixture\service.exe", synthetic);

    private static BrowserNavigation Navigation(
        IExternalLauncher launcher, IServiceLog log,
        IBrowserFailureNotification notification, bool synthetic = false)
    {
        var options = Options(synthetic);
        return new(options, new GuardedExternalOpener(options, launcher, log, notification));
    }

    // Executes the shared bodies used by MainWindow's two WebView2 event adapters.
    // This does not construct WebView2 or constitute native event-delivery proof.
    private static bool Invoke(Callback callback, BrowserNavigation navigation, string address)
    {
        var flag = false;
        switch (callback)
        {
            case Callback.Navigation: navigation.NavigationStarting(address, () => flag = true); break;
            case Callback.NewWindow: navigation.NewWindowRequested(address, () => flag = true); break;
        }
        return flag;
    }

    private static Exception Failure(LaunchFailure failure) => failure switch
    {
        LaunchFailure.Win32 => new Win32Exception(2, LaunchDetail),
        LaunchFailure.FileNotFound => new FileNotFoundException(LaunchDetail),
        LaunchFailure.Io => new IOException(LaunchDetail),
        LaunchFailure.Unauthorized => new UnauthorizedAccessException(LaunchDetail),
        _ => new InvalidOperationException(LaunchDetail)
    };
}

internal sealed class RecordingExternalLauncher(Exception? failure = null) : IExternalLauncher
{
    private readonly List<Uri> _targets = [];
    public IReadOnlyList<Uri> Targets => _targets;

    public void Open(Uri target)
    {
        _targets.Add(target);
        if (failure is { } error)
            throw error;
    }
}

internal sealed class RecordingBrowserNotification(Exception? failure = null) : IBrowserFailureNotification
{
    private int _count;
    public int Count => _count;

    public void BrowserFailed()
    {
        _count++;
        if (failure is { } error)
            throw error;
    }
}

internal sealed class FailingBrowserLog(MemoryLog memory) : IServiceLog
{
    public void Write(LogEntry entry)
    {
        memory.Write(entry);
        throw new IOException("synthetic-log-detail");
    }
}
