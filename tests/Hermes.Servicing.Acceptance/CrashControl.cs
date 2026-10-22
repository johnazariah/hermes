using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hermes.Servicing.Acceptance.Model;
using Microsoft.Win32.SafeHandles;

namespace Hermes.Servicing.Acceptance;

internal enum CrashFault
{
    None, Guard, Start, Ready, Binding, Arm, Exit, Callback,
    Stdout, Stderr, Cancelled, Deadline
}

// Failure-location observations only; none of these values grants acceptance.
internal enum CrashExitCheck
{
    None = 0, Lifetime = 1, Wait = 2, Drains = 3, Checkpoint = 4,
    Handle = 5, ProcessId = 6, ProcessTimes = 7, ProcessExitCode = 8,
    ProcessIdMatch = 9, ImageQuery = 10, ImageLength = 11,
    Pins = 12, PublicationPath = 13, ImageHash = 14, SourceHash = 15,
    SourceMvid = 16, IdentityProcess = 17, IdentityPath = 18,
    IdentityImage = 19, IdentitySource = 20, ExitTimeOrder = 21,
    ExitTimePresent = 22, ExitCodeMatch = 23, Status = 24, IdentityRead = 25
}

internal sealed class CrashExitException(
    CrashExitCheck check, Exception? cause = null)
    : IOException("Crash-control exit check refused.", cause)
{
    internal CrashExitCheck Check { get; } = check;
    internal uint NativeError { get; } = cause is Win32Exception native
        ? unchecked((uint)native.NativeErrorCode) : 0;
}

internal static class CrashExitChecks
{
    internal static T Read<T>(CrashExitCheck check, Func<T> read)
    {
        try { return read(); }
        catch (Exception error) when (
            error is not (CrashExitException or OperationCanceledException) &&
            CrashControl.Operational(error))
        {
            throw new CrashExitException(check, error);
        }
    }

    internal static void Require(CrashExitCheck check, bool accepted)
    {
        if (!accepted)
            throw new CrashExitException(check);
    }
}

[Flags]
internal enum CrashCleanup
{
    None = 0, Input = 1, Kill = 2, Reap = 4,
    Output = 8, Error = 16, Dispose = 32, Deadline = 64,
    Ownership = 128
}

[Flags]
internal enum CrashWerFlags : uint
{
    NoHeap = 1,
    Queue = 2,
    NoUi = 32,
    NoHeapOnQueue = 64,
    DisableSnapshotCrash = 128
}

internal static class CrashControl
{
    internal const string ChildCommand = "startup-crash-control";
    internal const string ProbeCommand = "startup-crash-probe";
    internal const string Ready = "HERMES_CRASH_READY v=1\n";
    internal const string Arm = "HERMES_CRASH_ARM v=1\n";
    internal const string Message = "Hermes acceptance synthetic crash control.";
    internal const int InputRefused = 64;
    internal const int WerRefused = 65;
    internal const int CrashReturned = 66;
    internal const int GuardRefused = 67;
    internal const int TransportRefused = 68;
    internal const int MaximumStderrBytes = 65536;
    internal const int MaximumReportBytes = 4096;
    internal static string EmptyHash { get; } = new('0', 64);
    internal const CrashWerFlags WerPolicy = CrashWerFlags.NoHeap |
        CrashWerFlags.Queue | CrashWerFlags.NoUi |
        CrashWerFlags.NoHeapOnQueue | CrashWerFlags.DisableSnapshotCrash;

    internal static bool Operational(Exception error) => error is
        IOException or InvalidDataException or Win32Exception or
        InvalidOperationException or UnauthorizedAccessException or ArgumentException or
        TimeoutException or OperationCanceledException or NotSupportedException;

    internal static bool IsHash(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    // Deliberately conservative: small normal/refusal exit codes are not crashes.
    // The observed code is always retained, including an unsupported crash code.
    // Includes COR_E_FAILFAST for the fixed .NET 9 Environment.FailFast child.
    internal static bool IsCrashStatus(uint status) =>
        status is 0x80131506 or 0x80131623 or 0xc0000409 or 0xc0000602 or 0xe0434352;
}

internal enum CrashReadKind { Pending, Data, End }
internal readonly record struct CrashRead(CrashReadKind Kind, int Count = 0);
internal sealed record CrashPipe(Func<Memory<byte>, CrashRead> Read);

internal static class CrashWire
{
    // Borrowed, never disposed here. Windows Process redirects through FileStream.
    // No StreamReader/StreamWriter operation may consume these same base streams.
    internal static SafeFileHandle BorrowHandle(Stream stream) => stream switch
    {
        FileStream file when file.SafeFileHandle is
            { IsClosed: false, IsInvalid: false } handle => handle,
        _ => throw new InvalidDataException("Crash-control pipe handle refused.")
    };

    internal static async Task<int> ReadAsync(
        CrashPipe pipe, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = pipe.Read(destination);
            if (read is { Kind: CrashReadKind.End, Count: 0 })
                return 0;
            if (read.Kind == CrashReadKind.Data &&
                read.Count > 0 && read.Count <= destination.Length)
                return read.Count;
            if (read is not { Kind: CrashReadKind.Pending, Count: 0 })
                throw new InvalidDataException("Invalid crash-control pipe result.");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    internal static async Task<bool> ReadFixedAsync(
        CrashPipe pipe, string expected, Action ready,
        CancellationToken cancellationToken)
    {
        var wanted = Encoding.ASCII.GetBytes(expected);
        var buffer = new byte[wanted.Length + 1];
        var length = 0;
        while (true)
        {
            var count = await ReadAsync(pipe, buffer.AsMemory(length), cancellationToken);
            if (count == 0)
                return length == wanted.Length;
            length += count;
            if (length > wanted.Length ||
                !buffer.AsSpan(0, length).SequenceEqual(wanted.AsSpan(0, length)))
                return false;
            if (length == wanted.Length)
                ready();
        }
    }

    internal static async Task ReadOutputAsync(
        CrashPipe pipe, Action ready, CancellationToken cancellationToken)
    {
        if (!await ReadFixedAsync(pipe, CrashControl.Ready, ready, cancellationToken))
            throw new InvalidDataException("Crash-control readiness refused.");
    }

    internal static async Task DrainErrorAsync(
        CrashPipe pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[256];
        var total = 0;
        try
        {
            while (await ReadAsync(pipe, buffer, cancellationToken) is > 0 and var count)
            {
                total = checked(total + count);
                if (total > CrashControl.MaximumStderrBytes)
                    throw new InvalidDataException("Crash-control stderr limit.");
            }
        }
        finally { Array.Clear(buffer); }
    }

    internal static async Task WriteArmAsync(
        Stream input, CancellationToken cancellationToken)
    {
        await input.WriteAsync(Encoding.ASCII.GetBytes(CrashControl.Arm), cancellationToken);
        await input.FlushAsync(cancellationToken);
    }

    internal static async Task WriteReadyAsync(
        Stream output, CancellationToken cancellationToken)
    {
        await output.WriteAsync(Encoding.ASCII.GetBytes(CrashControl.Ready), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }
}

internal sealed record CrashChildEffects(
    CrashPipe Input,
    Func<CancellationToken, Task> WriteReady,
    Func<uint, int> SetFlags,
    Func<(int HResult, uint Flags)> GetFlags,
    Action FailFast);

internal static class CrashChild
{
    internal static async Task<int> RunAsync(
        CrashChildEffects effects, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await effects.WriteReady(cancellationToken);
            if (!await CrashWire.ReadFixedAsync(
                effects.Input, CrashControl.Arm, static () => { }, cancellationToken))
                return CrashControl.InputRefused;
            cancellationToken.ThrowIfCancellationRequested();
            return ConfigureAndCrash(effects);
        }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            return CrashControl.TransportRefused;
        }
    }

    private static int ConfigureAndCrash(CrashChildEffects effects)
    {
        try
        {
            if (effects.SetFlags((uint)CrashControl.WerPolicy) != 0)
                return CrashControl.WerRefused;
            var read = effects.GetFlags();
            if (read.HResult != 0 || read.Flags != (uint)CrashControl.WerPolicy)
                return CrashControl.WerRefused;
        }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            return CrashControl.WerRefused;
        }
        effects.FailFast();
        return CrashControl.CrashReturned;
    }
}

// Structural Windows qualification, independent of the executing host.
// Equivalent to !PathInternal.IsPartiallyQualified in .NET 9; this does not
// validate, canonicalize, normalize or resolve a path.
// https://github.com/dotnet/runtime/blob/v9.0.0/src/libraries/Common/src/System/IO/PathInternal.Windows.cs
internal static class CrashWindowsPath
{
    internal static bool IsFullyQualified(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.AsSpan() switch
        {
            ['\\' or '/', '?' or '\\' or '/', ..] => true,
            [>= 'A' and <= 'Z' or >= 'a' and <= 'z', ':', '\\' or '/', ..] => true,
            _ => false
        };
    }
}

internal sealed record CrashIdentity(
    ProcessKey Process, string ImagePath, string ImageSha256,
    string SourceSha256, long ExitFileTime, uint ExitCode)
{
    internal bool SameInstance(CrashIdentity other) =>
        Process == other.Process &&
        string.Equals(ImagePath, other.ImagePath, StringComparison.OrdinalIgnoreCase) &&
        ImageSha256 == other.ImageSha256 && SourceSha256 == other.SourceSha256;

    internal void RequireLive()
    {
        if (Process is not { ProcessId: > 0, CreationFileTime: > 0 } ||
            !CrashWindowsPath.IsFullyQualified(ImagePath) ||
            !CrashControl.IsHash(ImageSha256) || !CrashControl.IsHash(SourceSha256) ||
            ExitFileTime != 0 || ExitCode != 259)
            throw new InvalidDataException("Crash-control live identity refused.");
    }

    internal void RequireSameInstance(CrashIdentity original)
    {
        CrashExitChecks.Require(CrashExitCheck.IdentityProcess, Process == original.Process);
        CrashExitChecks.Require(CrashExitCheck.IdentityPath,
            string.Equals(ImagePath, original.ImagePath, StringComparison.OrdinalIgnoreCase));
        CrashExitChecks.Require(CrashExitCheck.IdentityImage, ImageSha256 == original.ImageSha256);
        CrashExitChecks.Require(CrashExitCheck.IdentitySource, SourceSha256 == original.SourceSha256);
    }

    internal void RequireExit(CrashIdentity original, uint actualExit)
    {
        RequireSameInstance(original);
        CrashExitChecks.Require(CrashExitCheck.ExitTimePresent, ExitFileTime != 0);
        CrashExitChecks.Require(CrashExitCheck.ExitTimeOrder, ExitFileTime >= Process.CreationFileTime);
        CrashExitChecks.Require(CrashExitCheck.ExitCodeMatch, ExitCode == actualExit);
    }
}

internal sealed record CrashProcessSnapshot(
    SafeProcessHandle Handle, ProcessKey Process, long ExitFileTime, uint ExitCode);

internal sealed record CrashImagePins(string ImageSha256, string SourceSha256);

internal sealed record CrashRetainedIdentityEffects(
    Func<CrashProcessSnapshot> ReadProcess,
    Func<SafeProcessHandle, string> ReadLoadedImage,
    Func<string, CrashImagePins> RecheckPins,
    Func<CancellationToken, Task<uint>> Wait);

// Image evidence is established while live, never reconstructed after termination.
// Capabilities borrow the same owned process and publication until coordinator cleanup.
internal sealed class CrashRetainedIdentity(CrashRetainedIdentityEffects effects) : IDisposable
{
    private sealed record LiveProof(SafeProcessHandle Handle, CrashIdentity Identity);

    private LiveProof? _proof;
    private Task<uint>? _wait;
    private bool _disposed;

    internal Task<uint> WaitAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _wait ??= effects.Wait(cancellationToken);
    }

    internal CrashIdentity Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var snapshot = effects.ReadProcess();
        RequireHandle(snapshot.Handle);
        if (_proof is { } proof)
            RequireBinding(snapshot, proof);
        return _wait is { IsCompletedSuccessfully: true } wait
            ? ReadTerminal(snapshot, wait.Result) : ReadLive(snapshot);
    }

    private CrashIdentity ReadLive(CrashProcessSnapshot snapshot)
    {
        if (snapshot.Process is not { ProcessId: > 0, CreationFileTime: > 0 } ||
            snapshot.ExitFileTime != 0 || snapshot.ExitCode != 259)
            throw new InvalidDataException("Crash-control live process snapshot refused.");
        var path = effects.ReadLoadedImage(snapshot.Handle);
        var current = BindImage(snapshot, path);
        current.RequireLive();
        if (_proof is { } proof)
            current.RequireSameInstance(proof.Identity);
        _proof ??= new(snapshot.Handle, current);
        return current;
    }

    private CrashIdentity ReadTerminal(CrashProcessSnapshot snapshot, uint exit)
    {
        if (_proof is not { } proof)
            throw new CrashExitException(CrashExitCheck.Lifetime);
        var current = BindImage(snapshot, proof.Identity.ImagePath);
        current.RequireExit(proof.Identity, exit);
        return current;
    }

    private CrashIdentity BindImage(CrashProcessSnapshot snapshot, string path)
    {
        var pins = effects.RecheckPins(path);
        RequireHandle(snapshot.Handle);
        return new(snapshot.Process, path, pins.ImageSha256, pins.SourceSha256,
            snapshot.ExitFileTime, snapshot.ExitCode);
    }

    private static void RequireBinding(CrashProcessSnapshot snapshot, LiveProof proof)
    {
        CrashExitChecks.Require(CrashExitCheck.Handle,
            ReferenceEquals(snapshot.Handle, proof.Handle));
        CrashExitChecks.Require(CrashExitCheck.IdentityProcess,
            snapshot.Process == proof.Identity.Process);
    }

    private static void RequireHandle(SafeProcessHandle handle) =>
        CrashExitChecks.Require(CrashExitCheck.Handle,
            handle is { IsClosed: false, IsInvalid: false });

    public void Dispose() => _disposed = true;
}

internal sealed record CrashResult
{
    internal bool Started { get; init; }
    internal bool Ready { get; init; }
    internal bool Identity { get; init; }
    internal bool ArmAttempted { get; init; }
    internal bool Armed { get; init; }
    internal bool Exited { get; init; }
    internal bool Held { get; init; }
    internal bool Cleanup { get; init; }
    internal bool WaitSettled { get; init; }
    internal bool DrainsSettled { get; init; }
    internal bool WriteSettled { get; init; }
    internal bool CallbackSettled { get; init; }
    internal bool StdoutComplete { get; init; }
    internal bool StderrComplete { get; init; }
    internal bool Deadline { get; init; }
    internal bool Cancelled { get; init; }
    internal uint Exit { get; init; }
    internal CrashFault Error { get; init; }
    internal CrashExitCheck ExitCheck { get; init; }
    internal uint ExitNativeError { get; init; }
    internal CrashCleanup CleanupError { get; init; }
    internal int ReportError { get; init; }
    internal string ImageSha256 { get; init; } = CrashControl.EmptyHash;
    internal string SourceSha256 { get; init; } = CrashControl.EmptyHash;

    internal int ExitCode => this is
    {
        Started: true, Ready: true, Identity: true, ArmAttempted: true,
        Armed: true, Exited: true, Held: true, Cleanup: true,
        WaitSettled: true, DrainsSettled: true,
        WriteSettled: true, CallbackSettled: true,
        StdoutComplete: true, StderrComplete: true,
        Deadline: false, Cancelled: false, Error: CrashFault.None,
        CleanupError: CrashCleanup.None, ReportError: 0
    } && CrashControl.IsCrashStatus(Exit) ? 0 : 1;
}

internal static class CrashReport
{
    internal static byte[] Encode(CrashResult result)
    {
        if (!CrashControl.IsHash(result.ImageSha256) ||
            !CrashControl.IsHash(result.SourceSha256))
            throw new InvalidDataException("Crash-control report hash refused.");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Shape(result));
        if (bytes.Length > CrashControl.MaximumReportBytes)
            throw new InvalidDataException("Crash-control report limit.");
        return bytes;
    }

    private static object Shape(CrashResult value) => new
    {
        schemaVersion = 1, originalGatePassed = 0, nativeAcceptance = 0,
        status = value.ExitCode, started = Bit(value.Started), ready = Bit(value.Ready),
        identity = Bit(value.Identity), armAttempted = Bit(value.ArmAttempted),
        armed = Bit(value.Armed), exited = Bit(value.Exited), held = Bit(value.Held),
        cleanup = Bit(value.Cleanup), waitSettled = Bit(value.WaitSettled),
        drainsSettled = Bit(value.DrainsSettled),
        writeSettled = Bit(value.WriteSettled), callbackSettled = Bit(value.CallbackSettled),
        outerSupervisionRequired = 1, synchronousCallsInterruptible = 0,
        stdoutComplete = Bit(value.StdoutComplete),
        stderrComplete = Bit(value.StderrComplete), deadline = Bit(value.Deadline),
        cancelled = Bit(value.Cancelled), exit = value.Exit, error = (int)value.Error,
        cleanupError = (int)value.CleanupError, reportError = value.ReportError,
        imageSha256 = value.ImageSha256, sourceSha256 = value.SourceSha256
    };

    internal static string Summary(CrashResult result) =>
        FormattableString.Invariant(
            $"HERMES_CRASH_CONTROL v=1 status={result.ExitCode} exit={result.Exit} error={(int)result.Error} cleanupError={(int)result.CleanupError} reportError={result.ReportError} nativeAcceptance=0\n")
        + ExitSummary(result);

    private static string ExitSummary(CrashResult result) => result is
        { Error: CrashFault.Exit, ExitCheck: not CrashExitCheck.None } &&
        Enum.IsDefined(result.ExitCheck)
            ? FormattableString.Invariant(
                $"HERMES_CRASH_EXIT v=1 check={(int)result.ExitCheck} nativeError={result.ExitNativeError} nativeAcceptance=0\n")
            : "";

    private static int Bit(bool value) => value ? 1 : 0;
}

internal sealed record CrashCommandEffects(
    Action RequireContext,
    Func<CancellationToken, Task<int>> Child,
    Func<CancellationToken, Task<CrashResult>> Probe,
    Action<byte[]> Publish,
    Action<string> Summary);

internal static class CrashCommands
{
    internal static async Task<int> DispatchAsync(
        string[] arguments, CrashCommandEffects effects,
        CancellationToken cancellationToken)
    {
        if (arguments is not ([CrashControl.ChildCommand] or [CrashControl.ProbeCommand]))
            return 2;
        var child = arguments[0] == CrashControl.ChildCommand;
        if (!Admitted(effects))
            return child ? CrashControl.GuardRefused : RefuseGuard(effects);
        try
        {
            return child ? await effects.Child(cancellationToken) :
                await ProbeAsync(effects, cancellationToken);
        }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            return child ? CrashControl.TransportRefused : 1;
        }
    }

    private static bool Admitted(CrashCommandEffects effects)
    {
        try { effects.RequireContext(); return true; }
        catch (Exception error) when (CrashControl.Operational(error)) { return false; }
    }

    private static int RefuseGuard(CrashCommandEffects effects)
    {
        try { effects.Summary(CrashReport.Summary(new() { Error = CrashFault.Guard })); }
        catch (Exception error) when (CrashControl.Operational(error)) { return 1; }
        return 1;
    }

    private static async Task<int> ProbeAsync(
        CrashCommandEffects effects, CancellationToken cancellationToken)
    {
        var result = await effects.Probe(cancellationToken);
        try { effects.Publish(CrashReport.Encode(result)); }
        catch (Exception error) when (CrashControl.Operational(error))
        {
            result = result with { ReportError = 1 };
        }
        effects.Summary(CrashReport.Summary(result));
        return result.ExitCode;
    }
}

internal static class CrashLaunch
{
    internal static void Restrict(ProcessStartInfo start, string windows, string temporary)
    {
        start.Arguments = "";
        start.ArgumentList.Clear();
        start.ArgumentList.Add(CrashControl.ChildCommand);
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        start.WorkingDirectory = Path.GetDirectoryName(start.FileName)
            ?? throw new InvalidDataException("Crash-control apphost directory missing.");
        start.Environment.Clear();
        Populate(start, windows, temporary);
    }

    private static void Populate(ProcessStartInfo start, string windows, string temporary)
    {
        (string Key, string Value)[] values =
        [
            ("SystemRoot", windows), ("WINDIR", windows),
            ("PATH", Path.Combine(windows, "System32")),
            ("TEMP", temporary), ("TMP", temporary),
            ("DOTNET_EnableDiagnostics", "0"),
            ("GITHUB_ACTIONS", "true"), ("RUNNER_OS", "Windows"),
            ("RUNNER_ENVIRONMENT", "github-hosted"), ("GITHUB_JOB", "installer"),
            ("GITHUB_SERVER_URL", "https://github.com"),
            ("GITHUB_REPOSITORY", "johnazariah/hermes")
        ];
        foreach (var (key, value) in values)
            start.Environment.Add(key, value);
    }
}
