using System.Runtime.Versioning;

namespace Hermes.Servicing;

internal interface ICurrentUserStartup
{
    string LocalApplicationData();
    IPhysicalPaths Physical();
    IRuntimeGate Gate(string path, StartupRefusalEvidenceCapture? evidence);
    ILoadedCode Loaded();
    ILeaseLifetime Lifetime();
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsCurrentUserStartup : ICurrentUserStartup
{
    public string LocalApplicationData() =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public IPhysicalPaths Physical() => new WindowsPhysicalPaths();

    public IRuntimeGate Gate(string path, StartupRefusalEvidenceCapture? evidence) =>
        new WindowsRuntimeGate(path, evidence);

    public ILoadedCode Loaded() => new CurrentLoadedCode();

    public ILeaseLifetime Lifetime() => ProcessLeaseRoot.Instance;
}
