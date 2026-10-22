using System.Collections;
using System.Collections.Immutable;
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

internal enum StartupDebugStage
{
    Ready = 1,
    Resumed = 2,
    Final = 3
}

// Closed wire grammar, ASCII with exactly one LF and no BOM:
//
// created.row:
// HERMES_STARTUP_CREATED v=2 phase=created helper=H pid=P born=B\n
//
// helper.rows, in this exact field order:
// HERMES_STARTUP v=2 stage=S helper=H pid=P born=B image=I debug=D context=C flags=134742020 resumed=R exit=E parent=A cleanup=K fault=F cleanupFault=Q\n
//
// H is the actual helper PID; P/B are the held child's PID/creation FILETIME.
// Integers use invariant decimal, without padding or a sign. Boolean fields
// are 0/1; F/Q are 0..65535. Only the exact CREATED row above is accepted.
// armed.row, v1, CRLF, additional fields, and trailing bytes are not accepted.
//
// Success: stage 1/R=0, stage 2/R=1, stage 3/R=1.
// Stages 1/2 have E/A/K/F/Q=0. Stage 3 retains the existing observed exit,
// unchanged-parent, checked-child-cleanup, original-error and cleanup-error facts.
// Failure before checked Resume: omit stage 2; stage 3 has R=0.
// If stage-2 publication fails AFTER checked Resume, stage 3 has R=1, but
// cannot replace the missing complete stage-2 receipt or authorise continuation.
// Failure before READY may produce only stage 3. Incomplete/partial rows never
// constitute a successful three-row exchange.
//
// These are advisory helper facts only: no loader flags, ARMED/trace-ready,
// debugger continuation, native acceptance, or installed acceptance is claimed.
internal static class StartupDebugProtocol
{
    internal const int Version = 2;
    internal const uint CreationFlags = 134742020;
    internal const int MaximumCreatedBytes = 128;
    internal const string CreatedFileName = "created.row";

    internal static bool IsCreated(
        ReadOnlySpan<byte> bytes, int helperPid, StartupDebugIdentity identity)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumCreatedBytes || helperPid <= 0 ||
            identity is not
                { Pid: > 0, Born: > 0, ImageMatched: true, DebuggerPresent: true })
            return false;
        var expected = FormattableString.Invariant(
            $"HERMES_STARTUP_CREATED v={Version} phase=created helper={helperPid} pid={identity.Pid} born={identity.Born}\n");
        return bytes.SequenceEqual(Encoding.ASCII.GetBytes(expected));
    }

    internal static ImmutableArray<byte> ReadCreated(Stream stream)
    {
        var length = stream.Length;
        if (length is < 1 or > MaximumCreatedBytes)
            throw RestrictedOwnerToken.Failure(13);
        var bytes = new byte[checked((int)length)];
        stream.ReadExactly(bytes);
        return [.. bytes];
    }
}

internal sealed record StartupDebugControlRow(
    StartupDebugStage Stage, StartupDebugIdentity Identity, bool Resumed,
    StartupDebugOutcome Outcome, bool Parent)
{
    internal string ToWire(int helperPid)
    {
        Require(helperPid);
        return FormattableString.Invariant(
            $"HERMES_STARTUP v={StartupDebugProtocol.Version} stage={(int)Stage} helper={helperPid} pid={Identity.Pid} born={Identity.Born} image={Bit(Identity.ImageMatched)} debug={Bit(Identity.DebuggerPresent)} context={Bit(Identity.Pid != 0)} flags={StartupDebugProtocol.CreationFlags} resumed={Bit(Resumed)} exit={Outcome.Exit} parent={Bit(Parent)} cleanup={Bit(Outcome.Cleanup)} fault={Outcome.Error} cleanupFault={Outcome.CleanupError}\n");
    }

    private void Require(int helperPid)
    {
        var pending = Outcome == StartupDebugOutcome.Pending && !Parent;
        var phase = (Stage, Resumed) switch
        {
            (StartupDebugStage.Ready, false) or
                (StartupDebugStage.Resumed, true) => pending,
            (StartupDebugStage.Final, _) => true,
            _ => false
        };
        if (!phase || helperPid <= 0 || Identity.Born < 0 ||
            Outcome.Error is < 0 or > 65535 ||
            Outcome.CleanupError is < 0 or > 65535)
            throw RestrictedOwnerToken.Failure(13);
        if (Stage != StartupDebugStage.Final)
            Identity.Require();
    }

    private static int Bit(bool value) => value ? 1 : 0;
}

// This capability has no executable, debugger command, PID lookup, file-name
// selector, or process-opening operation. [] means the fixed CREATED file is
// absent; an existing empty/malformed file is an error, not absence.
internal interface IStartupDebugControlStorage : IDisposable
{
    int HelperPid { get; }
    TimeSpan Elapsed { get; }
    void Publish(StartupDebugControlRow row);
    ImmutableArray<byte> ReadCreated();
    void Pause(CancellationToken cancellationToken);
}

internal sealed class FileStartupDebugControl(
    IStartupDebugControlStorage storage) : IStartupDebugControl, IDisposable
{
    private enum Phase { Fresh, Ready, Created, Resumed, Finished, Disposed }

    private Phase _phase;
    private StartupDebugIdentity _identity = StartupDebugIdentity.Empty;

    internal static FileStartupDebugControl Open() =>
        new(FileStartupDebugControlStorage.Open());

    public void AwaitCreated(
        StartupDebugIdentity identity, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PublishReady(identity);
        WaitCreated(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _phase = Phase.Created;
    }

    private void PublishReady(StartupDebugIdentity identity)
    {
        if (_phase != Phase.Fresh)
            throw RestrictedOwnerToken.Failure(13);
        identity.Require();
        _identity = identity;
        _phase = Phase.Ready;
        storage.Publish(new(StartupDebugStage.Ready, identity, false,
            StartupDebugOutcome.Pending, false));
    }

    private void WaitCreated(CancellationToken cancellationToken)
    {
        var started = storage.Elapsed;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = storage.Elapsed - started;
            if (elapsed < TimeSpan.Zero)
                throw RestrictedOwnerToken.Failure(13);
            if (elapsed >= TimeSpan.FromSeconds(45))
                throw RestrictedOwnerToken.Failure(1460);
            if (TryReadCreated())
                return;
            storage.Pause(cancellationToken);
        }
    }

    private bool TryReadCreated()
    {
        var bytes = storage.ReadCreated();
        return bytes switch
        {
            { IsDefault: true } => throw RestrictedOwnerToken.Failure(13),
            { IsEmpty: true } => false,
            _ when StartupDebugProtocol.IsCreated(
                bytes.AsSpan(), storage.HelperPid, _identity) => true,
            _ => throw RestrictedOwnerToken.Failure(13)
        };
    }

    public void Resumed(StartupDebugIdentity identity)
    {
        if (_phase != Phase.Created || identity != _identity)
            throw RestrictedOwnerToken.Failure(13);
        // Called only after the existing owned child's checked Resume returns.
        // Set before I/O: publication failure must not erase that actual fact.
        _phase = Phase.Resumed;
        storage.Publish(new(StartupDebugStage.Resumed, identity, true,
            StartupDebugOutcome.Pending, false));
    }

    internal void Finish(
        StartupDebugIdentity identity, StartupDebugOutcome outcome, bool parent)
    {
        if (_phase is Phase.Finished or Phase.Disposed ||
            (_phase != Phase.Fresh && identity != _identity))
            throw RestrictedOwnerToken.Failure(13);
        var resumed = _phase == Phase.Resumed;
        _phase = Phase.Finished;
        storage.Publish(new(StartupDebugStage.Final, identity, resumed, outcome, parent));
    }

    public void Dispose()
    {
        if (_phase == Phase.Disposed)
            return;
        _phase = Phase.Disposed;
        storage.Dispose();
    }
}

internal sealed class FileStartupDebugControlStorage(
    string root, StreamWriter writer, int helperPid) : IStartupDebugControlStorage
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    public int HelperPid => helperPid;
    public TimeSpan Elapsed => _clock.Elapsed;

    internal static FileStartupDebugControlStorage Open()
    {
        var root = WindowsRestrictedOwnerTokens.StartupTraceRoot();
        var stream = new FileStream(Path.Combine(root, "helper.rows"),
            FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try
        {
            return new(root, new StreamWriter(stream, Encoding.ASCII)
                { AutoFlush = true, NewLine = "\n" }, Environment.ProcessId);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Publish(StartupDebugControlRow row) =>
        writer.Write(row.ToWire(HelperPid));

    public ImmutableArray<byte> ReadCreated()
    {
        var path = Path.Combine(root, StartupDebugProtocol.CreatedFileName);
        if (!File.Exists(path))
            return [];
        WindowsRestrictedOwnerTokens.RequireStartupFile(path);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return StartupDebugProtocol.ReadCreated(stream);
    }

    public void Pause(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Thread.Sleep(25);
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Dispose() => writer.Dispose();
}
