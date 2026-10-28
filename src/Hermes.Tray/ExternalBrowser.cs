using System.Diagnostics;

namespace Hermes.Tray;

internal interface IExternalLauncher { void Open(Uri target); }
internal interface IExternalOpener { void Open(string address); }
internal interface IBrowserFailureNotification { void BrowserFailed(); }

internal sealed class ShellExternalLauncher : IExternalLauncher
{
    public void Open(Uri target)
    {
        using var process = Process.Start(
            new ProcessStartInfo(target.AbsoluteUri) { UseShellExecute = true });
    }
}

internal static class ExternalAddress
{
    public static Uri? Parse(string address)
    {
        try { return new(address, UriKind.Absolute); }
        catch (Exception error) when (error is UriFormatException or ArgumentException) { return null; }
    }
}

internal sealed class GuardedExternalOpener(
    TrayOptions options, IExternalLauncher launcher,
    IServiceLog log, IBrowserFailureNotification notification) : IExternalOpener
{
    public void Open(string address)
    {
        if (options.Synthetic || ExternalAddress.Parse(address) is not { } target ||
            target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo))
            return;
        try { launcher.Open(target); }
        catch (Exception error) { ReportFailure(error); }
    }

    private void ReportFailure(Exception error)
    {
        WriteFailure(error);
        try { notification.BrowserFailed(); }
        catch (Exception notificationError) { WriteFailure(notificationError); }
    }

    private void WriteFailure(Exception error)
    {
        try { log.Write(new(EventCode.BrowserFailed, Detail: SafeFailure.Describe(error))); }
        catch (Exception logError)
        {
            Trace.TraceError("Hermes browser log failed: {0}", SafeFailure.Describe(logError));
        }
    }
}

// These are the shared callback bodies used by the WebView2 event adapters.
internal sealed class BrowserNavigation(TrayOptions options, IExternalOpener external)
{
    public bool IsCanonical(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" &&
        uri.Port == options.Port && string.IsNullOrEmpty(uri.UserInfo);

    public bool IsCanonicalDocument(Uri uri) =>
        IsCanonical(uri) && uri.AbsolutePath is
            "/" or "/documents" or "/search" or "/settings" or "/chat" or "/pipeline" or "/onboarding";

    public void NavigationStarting(string address, Action cancel)
    {
        if (ExternalAddress.Parse(address) is { } target && IsCanonical(target))
            return;
        cancel();
        external.Open(address);
    }

    public void NewWindowRequested(string address, Action handled)
    {
        handled();
        external.Open(address);
    }
}
