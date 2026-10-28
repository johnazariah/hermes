namespace Hermes.Servicing.Acceptance;

internal enum AcceptanceDesktopSelection
{
    Automatic,
    ExistingParent
}

internal interface IAcceptanceParentDesktopNames
{
    string ReadStartupName();
}

internal static class AcceptanceChildDesktop
{
    internal const int NameCapacity = 512;

    internal static string? Resolve(
        AcceptanceDesktopSelection selection, IAcceptanceParentDesktopNames names) =>
        selection switch
        {
            AcceptanceDesktopSelection.Automatic => null,
            AcceptanceDesktopSelection.ExistingParent =>
                RequireName(names.ReadStartupName()),
            _ => throw RestrictedOwnerToken.Failure(13)
        };

    internal static string RequireName(string value)
    {
        if (value.Length is < 3 or > (2 * NameCapacity - 1))
            throw RestrictedOwnerToken.Failure(13);
        return value.Split('\\') switch
        {
            [var station, var desktop] when
                IsComponent(station) && IsComponent(desktop) => value,
            _ => throw RestrictedOwnerToken.Failure(13)
        };
    }

    private static bool IsComponent(string value) =>
        value.Length is > 0 and < NameCapacity &&
        !value.Any(character => char.IsControl(character) ||
            character is '\\' or '/');

    internal static string DecodeName(ReadOnlySpan<char> buffer, uint bytes)
    {
        if (buffer.Length is 0 or > NameCapacity ||
            bytes < 2 * sizeof(char) || bytes % sizeof(char) != 0 ||
            bytes > (uint)(buffer.Length * sizeof(char)))
            throw RestrictedOwnerToken.Failure(13);
        var text = buffer[..checked((int)(bytes / sizeof(char)))];
        if (text[^1] != '\0' || text[..^1].IndexOf('\0') >= 0)
            throw RestrictedOwnerToken.Failure(13);
        return new string(text[..^1]);
    }
}

// Borrowed handles and OS names remain private metadata, never diagnostics.
// This type intentionally has no generated ToString or value deconstruction.
internal sealed class AcceptanceParentDesktopConnection(
    nint station, nint desktop, string startupName)
{
    private readonly nint _station = station;
    private readonly nint _desktop = desktop;

    internal string StartupName { get; } =
        AcceptanceChildDesktop.RequireName(startupName);

    internal bool SameConnection(AcceptanceParentDesktopConnection other) =>
        _station == other._station && _desktop == other._desktop &&
        string.Equals(StartupName, other.StartupName, StringComparison.Ordinal);
}

internal sealed unsafe partial class WindowsRestrictedOwnerTokens
    : IAcceptanceParentDesktopNames
{
    // Observation-only factory. The ordinary factory retains NULL lpDesktop.
    // No external name, command, credential, or policy input is accepted.
    internal IAcceptanceChildFactory ExistingParentDesktopForObservation { get; } =
        new ExistingParentDesktopChildren();

    string IAcceptanceParentDesktopNames.ReadStartupName() =>
        ReadParentDesktop().StartupName;

    internal AcceptanceParentDesktopConnection ReadParentDesktop()
    {
        RequireDisposableJob();
        var station = GetProcessWindowStation();
        if (station == 0)
            throw NativeFailure();
        var desktop = GetThreadDesktop(GetCurrentThreadId());
        if (desktop == 0)
            throw NativeFailure();
        return new(station, desktop,
            $"{ReadObjectName(station)}\\{ReadObjectName(desktop)}");
    }

    private static string ReadObjectName(nint borrowed)
    {
        Span<char> buffer = stackalloc char[AcceptanceChildDesktop.NameCapacity];
        buffer.Clear();
        uint bytes = 0;
        fixed (char* address = buffer)
        {
            if (GetUserObjectInformationW(borrowed, 2, address,
                (uint)(buffer.Length * sizeof(char)), &bytes) == 0)
                throw NativeFailure();
        }
        return AcceptanceChildDesktop.DecodeName(buffer, bytes);
    }

    private sealed class ExistingParentDesktopChildren : IAcceptanceChildFactory
    {
        public IAcceptanceOwnedChild Open()
        {
            Instance.RequireDisposableJob();
            return new OwnedInertChild(AcceptanceDesktopSelection.ExistingParent);
        }
    }

    // These getters do not transfer ownership. Never close their results.
    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetProcessWindowStation();

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint GetThreadDesktop(uint thread);

    [LibraryImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetUserObjectInformationW(
        nint borrowed, int information, char* buffer, uint length, uint* needed);
}
