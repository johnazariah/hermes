using System.Text.Json;

namespace Hermes.Servicing;

public enum SetupVerb { Install, Repair, Uninstall, Recover }

public enum SetupOutcome
{
    Completed,
    Refused,
    RolledBack,
    RebootRequired,
    RecoveryRequired
}

public enum SetupExitCode
{
    Success = 0,
    InvalidArguments = 2,
    ServicingFailed = 1603,
    Busy = 1618,
    RebootRequired = 3010
}

public abstract record SetupCommand(bool Quiet);

public sealed record InstallCommand(
    string PackagePath,
    bool AllowRollback,
    bool Quiet) : SetupCommand(Quiet);

public sealed record MaintenanceCommand(
    SetupVerb Operation,
    bool Quiet) : SetupCommand(Quiet);

public abstract record CommandParsing;
public sealed record ParsedCommand(SetupCommand Command) : CommandParsing;
public sealed record InvalidCommand(string Message) : CommandParsing;

public sealed record SetupReport(
    SetupVerb Operation,
    SetupOutcome Outcome,
    bool Ready,
    int? MsiExitCode)
{
    public int SchemaVersion => 1;
    public string ToJson() => JsonSerializer.Serialize(this, ServicingJson.Default.SetupReport);
}

public static class SetupCommandLine
{
    private const string QuietFlag = "--quiet";
    private const string RollbackFlag = "--allow-rollback";
    private const string Usage =
        "Expected install --package ABSOLUTE_MSI [--allow-rollback] [--quiet], " +
        "or repair|uninstall|recover [--quiet].";

    public static CommandParsing Parse(IReadOnlyList<string> arguments) =>
        arguments.ToArray() switch
        {
            ["install", "--package", var package, .. var flags] => Install(package, flags),
            ["repair", .. var flags] => Maintenance(SetupVerb.Repair, flags),
            ["uninstall", .. var flags] => Maintenance(SetupVerb.Uninstall, flags),
            ["recover", .. var flags] => Maintenance(SetupVerb.Recover, flags),
            _ => new InvalidCommand(Usage)
        };

    private static CommandParsing Install(string package, string[] flags)
    {
        if (!ValidFlags(flags, allowRollback: true))
            return new InvalidCommand(Usage);
        try
        {
            return Path.IsPathFullyQualified(package) &&
                string.Equals(Path.GetExtension(package), ".msi", StringComparison.OrdinalIgnoreCase)
                ? new ParsedCommand(new InstallCommand(Path.GetFullPath(package),
                    flags.Contains(RollbackFlag), flags.Contains(QuietFlag)))
                : new InvalidCommand(Usage);
        }
        catch (ArgumentException) { return new InvalidCommand(Usage); }
    }

    private static CommandParsing Maintenance(SetupVerb operation, string[] flags) =>
        ValidFlags(flags, allowRollback: false)
            ? new ParsedCommand(new MaintenanceCommand(operation, flags.Contains(QuietFlag)))
            : new InvalidCommand(Usage);

    private static bool ValidFlags(string[] flags, bool allowRollback) =>
        flags.Distinct(StringComparer.Ordinal).Count() == flags.Length &&
        flags.All(flag => flag == QuietFlag || (allowRollback && flag == RollbackFlag));
}
