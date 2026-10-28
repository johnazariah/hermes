using Hermes.Tray.Access;

namespace Hermes.Tray;

internal sealed record OwnedAccessState(
    IAccessProcessHandle Process, long RestartGeneration, int ReadyHealthProcessId);

internal interface IOwnedAccessSource
{
    OwnedAccessState? ReadAccess();
}

internal interface INativeAccessSurface
{
    Uri? Source { get; }
    void VerifyAccess();
    void PostReady(string json);
    void RenderAccess(NativeAccessView view);
    void ClearAccessDocument();
}

internal sealed class NativeAccessEnvironment(
    IOwnedAccessSource ownership, BrowserNavigation navigation, INativeAccessSurface surface)
    : IAccessEnvironment
{
    private Document? _document;

    public Uri? NavigationSource => _document?.Source;
    public bool Completed => _document is { Completed: true };

    public void Starting(ulong generation, Uri? source)
    {
        surface.VerifyAccess();
        _document = source is { IsAbsoluteUri: true } ? new(generation, source, false) : null;
    }

    public void Complete(ulong generation, bool success)
    {
        surface.VerifyAccess();
        if (_document is { } document && document.Generation == generation)
            _document = document with
            {
                Completed = success && surface.Source?.AbsoluteUri == document.Source.AbsoluteUri
            };
    }

    public void Clear()
    {
        surface.VerifyAccess();
        _document = null;
    }

    public AccessEnvironmentSnapshot? Read()
    {
        surface.VerifyAccess();
        if (_document is not { } document ||
            surface.Source?.AbsoluteUri != document.Source.AbsoluteUri ||
            ownership.ReadAccess() is not { } owned ||
            !owned.Process.IsAlive || owned.ReadyHealthProcessId != owned.Process.Identity.ProcessId)
            return null;
        return new(owned.Process, owned.RestartGeneration, document.Generation,
            document.Source, document.Completed, navigation.IsCanonicalDocument(document.Source)
                ? AccessDocument.CanonicalReactApplication : AccessDocument.Other);
    }

    private sealed record Document(ulong Generation, Uri Source, bool Completed);
}
