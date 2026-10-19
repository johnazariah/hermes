using System.Security.Cryptography;
using System.Text;

namespace Hermes.Tray;

// Acquired and released by the STA entry thread, never by an async continuation.
internal sealed class InstanceLease : IDisposable
{
    private readonly Mutex _configuration;
    private readonly Mutex _port;
    private bool _disposed;

    private InstanceLease(Mutex configuration, Mutex port) =>
        (_configuration, _port) = (configuration, port);

    public static InstanceLease Acquire(TrayOptions options)
    {
        var configuration = Take($"config:{options.ConfigDirectory.ToUpperInvariant()}");
        try
        {
            return new(configuration, Take($"port:{options.Port}"));
        }
        catch
        {
            configuration.ReleaseMutex();
            configuration.Dispose();
            throw;
        }
    }

    private static Mutex Take(string key)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var mutex = new Mutex(false, $@"Global\Hermes.Tray.{hash}");
        if (TryAcquire(mutex))
            return mutex;
        mutex.Dispose();
        throw new InvalidOperationException("Another Hermes Tray owns this configuration directory or port.");
    }

    private static bool TryAcquire(Mutex mutex)
    {
        try { return mutex.WaitOne(0); }
        catch (AbandonedMutexException) { return true; }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _port.ReleaseMutex();
        _configuration.ReleaseMutex();
        _port.Dispose();
        _configuration.Dispose();
    }
}
