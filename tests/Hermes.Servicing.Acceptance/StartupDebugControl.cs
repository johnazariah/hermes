using System.Collections;
using System.Text;

namespace Hermes.Servicing.Acceptance;

internal sealed class StartupDebugParent(
    MsiCallContextObservation context,
    AcceptanceParentSecuritySnapshot security,
    AcceptanceParentDesktopConnection desktop,
    string environment)
{
    private readonly MsiCallContextObservation _context = context;
    private readonly AcceptanceParentSecuritySnapshot _security = security;
    private readonly AcceptanceParentDesktopConnection _desktop = desktop;
    private readonly string _environment = environment;

    internal static MsiCallContextObservation ReadContext() =>
        MsiCallContextObservation.Read(WindowsMsiCallContextTokens.Instance);

    internal static bool IsHighSource(MsiCallContextObservation value) => value is
    {
        Process: MsiContextRead.Observed, Elevated: 1, ElevationType: 1,
        Integrity: 12288, Admin: 1, Thread: MsiContextRead.Absent,
        Linked: MsiContextRead.Absent, SameOwner: -1, LinkedElevated: -1
    };

    internal static StartupDebugParent Read()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        var context = ReadContext();
        if (!IsHighSource(context))
            throw RestrictedOwnerToken.Failure(5);
        return new(context, api.ReadParentSecurity(), api.ReadParentDesktop(),
            EnvironmentFingerprint());
    }

    internal bool SameFinalState(StartupDebugParent other) =>
        _context == other._context &&
        _security.SameFinalState(other._security) &&
        _desktop.SameConnection(other._desktop) &&
        _environment == other._environment;

    private static string EnvironmentFingerprint()
    {
        var values = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .OrderBy(entry => entry.Key.ToString(), StringComparer.Ordinal)
            .Select(entry => $"{entry.Key}\0{entry.Value}\0");
        var bytes = Encoding.UTF8.GetBytes(
            Environment.CurrentDirectory + "\0" + string.Concat(values));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}

// These are advisory control rows, not native-writer or installer evidence.
// Neither a caller-selected PID nor a caller-selected command is accepted.
internal sealed class FileStartupDebugControl(
    string root, StreamWriter writer) : IStartupDebugControl, IDisposable
{
    private bool _published;
    private bool _finished;

    internal static FileStartupDebugControl Open()
    {
        var root = WindowsRestrictedOwnerTokens.StartupTraceRoot();
        var stream = new FileStream(Path.Combine(root, "helper.rows"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        return new(root, new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true });
    }

    public void AwaitArming(
        StartupDebugIdentity identity, CancellationToken cancellationToken)
    {
        if (_published || _finished)
            throw RestrictedOwnerToken.Failure(13);
        identity.Require();
        _published = true;
        writer.WriteLine(Row(1, identity, new(0, false, 0, 0), false));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(45))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadArm(identity))
                return;
            Thread.Sleep(25);
        }
        throw RestrictedOwnerToken.Failure(1460);
    }

    private bool TryReadArm(StartupDebugIdentity identity)
    {
        var path = Path.Combine(root, "armed.row");
        if (!File.Exists(path))
            return false;
        WindowsRestrictedOwnerTokens.RequireStartupFile(path);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 128)
            throw RestrictedOwnerToken.Failure(13);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var expected = FormattableString.Invariant(
            $"HERMES_STARTUP_ARM v=1 pid={identity.Pid} born={identity.Born}\n");
        if (!string.Equals(Encoding.ASCII.GetString(bytes), expected, StringComparison.Ordinal))
            throw RestrictedOwnerToken.Failure(13);
        return true;
    }

    internal void Finish(
        StartupDebugIdentity identity, StartupDebugOutcome outcome, bool parent)
    {
        if (_finished)
            throw RestrictedOwnerToken.Failure(13);
        _finished = true;
        writer.WriteLine(Row(2, identity, outcome, parent));
    }

    private static string Row(
        int stage, StartupDebugIdentity identity, StartupDebugOutcome outcome,
        bool parent) => FormattableString.Invariant(
            $"HERMES_STARTUP v=1 stage={stage} helper={Environment.ProcessId} pid={identity.Pid} born={identity.Born} image={Bit(identity.ImageMatched)} debug={Bit(identity.DebuggerPresent)} context={Bit(identity.Pid != 0)} flags=134742020 exit={outcome.Exit} parent={Bit(parent)} cleanup={Bit(outcome.Cleanup)} fault={outcome.Error} cleanupFault={outcome.CleanupError}");

    private static int Bit(bool value) => value ? 1 : 0;

    public void Dispose() => writer.Dispose();
}
