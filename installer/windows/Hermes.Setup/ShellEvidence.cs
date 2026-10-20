namespace Hermes.Setup;

internal sealed record ShortcutFields(string Target, string Arguments, string WorkingDirectory);
internal sealed record RunCommand(string Executable, string Arguments);
internal enum ShortcutComparison
{
    Matched, Missing, Unreadable, WrongTarget, WrongArguments, WrongWorkingDirectory, Uncertain
}

internal interface IShellLinkQuery
{
    ReadObservation<ShortcutFields> Read(string knownPath);
}

internal static class ShortcutClassifier
{
    internal static ShortcutComparison Compare(
        ReadObservation<ShortcutFields> actual, ShortcutFields expected, IPathIdentityQuery paths)
    {
        if (actual.State == ObservationState.Missing)
            return ShortcutComparison.Missing;
        if (actual is not { State: ObservationState.Known, Value: { } fields })
            return ShortcutComparison.Unreadable;
        try { return CompareFields(fields, expected, paths); }
        catch (FormatException) { return ShortcutComparison.Uncertain; }
    }

    private static ShortcutComparison CompareFields(
        ShortcutFields actual, ShortcutFields expected, IPathIdentityQuery paths) =>
        (WindowsEvidencePath.Same(actual.Target, expected.Target),
            actual.Arguments == expected.Arguments,
            WindowsEvidencePath.Same(actual.WorkingDirectory, expected.WorkingDirectory)) switch
        {
            (false, _, _) => ShortcutComparison.WrongTarget,
            (_, false, _) => ShortcutComparison.WrongArguments,
            (_, _, false) => ShortcutComparison.WrongWorkingDirectory,
            _ => Stable(actual.Target, paths) && Stable(actual.WorkingDirectory, paths)
                ? ShortcutComparison.Matched : ShortcutComparison.Uncertain
        };

    private static bool Stable(string path, IPathIdentityQuery paths) =>
        paths.Read(WindowsEvidencePath.Normalize(path)) is
            { State: ObservationState.Known, Value: { } identity } &&
        WindowsEvidencePath.Same(path, identity.Path);
}

internal static class RunCommandReader
{
    internal static ReadObservation<RunCommand> Read(RegistryAtom atom)
    {
        if (atom.Kind == RegistryAtomKind.Missing)
            return ReadObservation<RunCommand>.Missing();
        if (atom is not { Kind: RegistryAtomKind.Text, Text: { } text } ||
            text.Length < 3 || text[0] != '"')
            return ReadObservation<RunCommand>.Unknown(ObservationFault.Ambiguous);
        try { return Quoted(text); }
        catch (FormatException)
        { return ReadObservation<RunCommand>.Unknown(ObservationFault.Malformed); }
    }

    private static ReadObservation<RunCommand> Quoted(string text)
    {
        var end = text.IndexOf('"', 1);
        if (end < 2 || (end + 1 < text.Length && text[end + 1] is not (' ' or '\t')))
            return ReadObservation<RunCommand>.Unknown(ObservationFault.Malformed);
        var executable = WindowsEvidencePath.Normalize(text[1..end]);
        var arguments = text[(end + 1)..].TrimStart(' ', '\t');
        return ReadObservation<RunCommand>.Known(new(executable, arguments));
    }
}
