using System;
using System.IO;
using System.Threading;
using Avalonia;
using Hermes.App;

namespace Hermes.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var lockPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "hermes", "hermes.lock");

        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);

        FileStream? lockFile = null;
        try
        {
            lockFile = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            // Another instance holds the lock — exit silently
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            lockFile?.Dispose();
            try { File.Delete(lockPath); } catch { }
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
