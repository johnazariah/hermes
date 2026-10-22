using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

internal sealed record ServicingDiagnostic(
    int SchemaVersion,
    string Kind,
    Guid Invocation,
    Guid RunId,
    ProcessInstance Coordinator,
    ProcessInstance Writer,
    Guid ProductCode,
    Guid? UpgradingProductCode,
    string? Phase,
    string? Operation,
    int? Status,
    string? Action,
    int Failures);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ServicingDiagnostic))]
internal partial class DiagnosticJson : JsonSerializerContext;

[SupportedOSPlatform("windows")]
internal static unsafe partial class DiagnosticLog
{
    internal const string Source = "Hermes.Servicing.Evidence.Native";
    internal const ushort EventId = 4096;
    private static int _failures;

    internal static int Failures => Volatile.Read(ref _failures);

    internal static void NoteFailure() => Interlocked.Increment(ref _failures);

    internal static bool Write(ServicingDiagnostic value)
    {
        try
        {
            var text = JsonSerializer.Serialize(value, DiagnosticJson.Default.ServicingDiagnostic);
            if (text.Length > 8192)
                throw new InvalidDataException("Diagnostic record exceeded its bound.");
            return WriteText(text);
        }
        catch
        {
            NoteFailure();
            return false;
        }
    }

    private static bool WriteText(string text)
    {
        var provider = new Guid("c760c1ab-37cb-4c3a-a245-3ea632fc9c5d");
        ulong registration = 0;
        RequireSuccess(EventRegister(&provider, 0, 0, &registration));
        try { return WriteRegistered(registration, text); }
        finally
        {
            if (EventUnregister(registration) != 0)
                NoteFailure();
        }
    }

    private static bool WriteRegistered(ulong registration, string text)
    {
        if (EventProviderEnabled(registration, 4, 0) == 0)
            throw new InvalidOperationException("Diagnostic provider is not enabled.");
        // Application is the imported Windows channel with channel value 9.
        var descriptor = new EventDescriptor(EventId, 0, 9, 4, 0, 0, 0);
        fixed (char* characters = text)
        {
            var data = new EventDataDescriptor((ulong)characters,
                checked((uint)((text.Length + 1) * sizeof(char))), 0);
            RequireSuccess(EventWrite(registration, &descriptor, 1, &data));
        }
        return true;
    }

    private static void RequireSuccess(uint status)
    {
        if (status != 0)
            throw new InvalidOperationException("Native diagnostic transport failed.");
    }

    internal static void Call(ControlEndpoint endpoint, Guid product,
        string kind, int? status = null, string? action = null)
    {
        try
        {
            Write(new(1, kind, Guid.Empty, endpoint.RunId,
                endpoint.Coordinator, WindowsPeers.Current(), product,
                null, null, null, status, action, Failures));
        }
        catch { NoteFailure(); }
    }

    internal static void ObserverFailure(
        ControlEndpoint endpoint, Guid product, string point)
    {
        NoteFailure();
        Call(endpoint, product, "ObserverFault", action: point);
    }

    internal static bool Checkpoint(Guid subscription)
    {
        try
        {
            var current = WindowsPeers.Current();
            return Write(new(1, "ObserverReady", subscription, Guid.Empty,
                current, current, Guid.Empty, null, null, null,
                null, null, Failures));
        }
        catch
        {
            NoteFailure();
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventDescriptor(
        ushort Id, byte Version, byte Channel, byte Level,
        byte Opcode, ushort Task, ulong Keyword);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct EventDataDescriptor(
        ulong Pointer, uint Size, uint Reserved);

    [LibraryImport("advapi32.dll")]
    private static partial uint EventRegister(
        Guid* provider, nint callback, nint context, ulong* registration);

    [LibraryImport("advapi32.dll")]
    private static partial byte EventProviderEnabled(
        ulong registration, byte level, ulong keyword);

    [LibraryImport("advapi32.dll")]
    private static partial uint EventWrite(ulong registration,
        EventDescriptor* descriptor, uint count, EventDataDescriptor* data);

    [LibraryImport("advapi32.dll")]
    private static partial uint EventUnregister(ulong registration);
}

[SupportedOSPlatform("windows")]
internal sealed class GuardDiagnostic(ServicingDiagnostic entered)
{
    internal static GuardDiagnostic? Start(
        SessionSnapshot snapshot, ControlEndpoint endpoint)
    {
        try
        {
            var request = snapshot.Request;
            var value = new ServicingDiagnostic(1, "GuardEnter", Guid.NewGuid(),
                endpoint.RunId, endpoint.Coordinator, WindowsPeers.Current(),
                request.ProductCode, request.UpgradingProductCode,
                request.Phase.ToString(), request.Operation.ToString(),
                null, null, DiagnosticLog.Failures);
            DiagnosticLog.Write(value);
            return new(value);
        }
        catch
        {
            DiagnosticLog.NoteFailure();
            return null;
        }
    }

    internal void Complete(uint status) =>
        DiagnosticLog.Write(entered with
        {
            Kind = "GuardDecision",
            Status = checked((int)status),
            Failures = DiagnosticLog.Failures
        });
}
[SupportedOSPlatform("windows")]
internal sealed class WindowsObservationFailureSink(
    ControlEndpoint endpoint, Guid product) : IObservationFailureSink
{
    public void Record(string point) =>
        DiagnosticLog.ObserverFailure(endpoint, product, point);
}
