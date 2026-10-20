using System.Collections.Immutable;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal sealed class WindowsRegistrationInventory : IRegistrationInventory
{
    private const string InstallerKey = @"Software\Hermes\Installer";
    private const string ComponentsKey = InstallerKey + @"\Components";
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SessionManagerKey = @"SYSTEM\CurrentControlSet\Control\Session Manager";

    public RegistrationInventory Read(IReadOnlyList<Guid> componentCodes)
    {
        using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var product = user.OpenSubKey(InstallerKey, writable: false);
        using var components = user.OpenSubKey(ComponentsKey, writable: false);
        using var run = user.OpenSubKey(RunKey, writable: false);
        using var pending = machine.OpenSubKey(SessionManagerKey, writable: false);
        var shortcut = MenuLink();
        var msi = new MsiRegistrationReader(new WindowsMsiRegistrationQueries()).Read(componentCodes);
        return new(msi,
            ReadValues(product), ReadValues(components), ReadValue(run, "Hermes"),
            new WindowsShellLinkQuery().Read(shortcut),
            PendingFileClassifier.Read(
                [WindowsPendingValueQuery.Read(pending, "PendingFileRenameOperations"),
                 WindowsPendingValueQuery.Read(pending, "PendingFileRenameOperations2")],
                [WindowsSetupScope.ForCurrentUser().AppDirectory, shortcut],
                new WindowsPathIdentityQuery()));
    }

    private static ImmutableDictionary<string, RegistryAtom> ReadValues(RegistryKey? key) =>
        key is null
            ? ImmutableDictionary<string, RegistryAtom>.Empty
            : key.GetValueNames().ToImmutableDictionary(name => name,
                name => ReadValue(key, name), StringComparer.OrdinalIgnoreCase);

    private static RegistryAtom ReadValue(RegistryKey? key, string name)
    {
        if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase))
            return new(RegistryAtomKind.Missing, null, null, []);
        var kind = key.GetValueKind(name);
        var value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return (kind, value) switch
        {
            (RegistryValueKind.String, string text) =>
                new(RegistryAtomKind.Text, text, null, []),
            (RegistryValueKind.DWord, int integer) =>
                new(RegistryAtomKind.Integer, null, integer, []),
            (RegistryValueKind.MultiString, string[] strings) =>
                new(RegistryAtomKind.Strings, null, null, strings.ToImmutableArray()),
            _ => new(RegistryAtomKind.Unsupported, null, null, [])
        };
    }

    private static string MenuLink()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        if (string.IsNullOrWhiteSpace(programs) || !Path.IsPathFullyQualified(programs))
            throw new InvalidOperationException("The OS Programs known folder is unavailable.");
        return Path.Combine(programs, "Hermes", "Hermes.lnk");
    }
}
