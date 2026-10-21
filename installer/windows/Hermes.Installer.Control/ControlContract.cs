using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Hermes.Servicing;

namespace Hermes.Installer.Control;

internal interface IClosedGateOwnership
{
    string GatePath { get; }
    bool IsHeldAndClosed { get; }
}

internal sealed record ProcessInstance(
    [property: JsonRequired] uint ProcessId,
    [property: JsonRequired] long CreationFileTime);

internal sealed record ControlEndpoint(
    [property: JsonRequired] int Abi,
    [property: JsonRequired] Guid RunId,
    [property: JsonRequired] ProcessInstance Coordinator,
    [property: JsonRequired] string UserSid,
    [property: JsonRequired] string PipeName);

internal sealed record ControlPlan(
    Guid ProductCode,
    MaintenanceKind Operation,
    string AppDirectory,
    ImmutableArray<Guid> NestedRemovalProducts,
    string? PackagePath,
    bool AllowRollback,
    bool Quiet);

internal sealed record GuardExchange(
    [property: JsonRequired] Guid RunId,
    [property: JsonRequired] int Abi,
    [property: JsonRequired] Guid Challenge,
    [property: JsonRequired] Guid? PropertyRun,
    [property: JsonRequired] GuardRequest Request);

internal enum ControlStep { Prepared, Permit, Refuse }

internal sealed record ControlReply(
    [property: JsonRequired] ControlStep Step,
    [property: JsonRequired] Guid RunId,
    [property: JsonRequired] Guid Challenge);

internal sealed record FenceReceipt(
    [property: JsonRequired] Guid RunId,
    [property: JsonRequired] Guid Challenge,
    [property: JsonRequired] GuardPhase Phase,
    [property: JsonRequired] Guid ProductCode,
    [property: JsonRequired] bool OwnSession,
    [property: JsonRequired] ProcessInstance CaHost);

internal sealed record InstallerCallReceipt(
    Guid RunId,
    bool ApiEntered,
    bool ApiReturned,
    uint? MsiExitCode,
    bool ControlFaulted,
    ImmutableArray<FenceReceipt> Fences)
{
    public int SchemaVersion => 1;
    public int ObservationFaults { get; init; }

    // Ephemeral diagnostics only: not part of the serialized control receipt.
    internal MsiFailureObservation? MsiFailure { get; init; }
    internal NativePreflightObservation NativePreflight { get; init; } =
        NativePreflightObservation.Missing;

    internal bool MayEvaluateRecovery =>
        ApiEntered && ApiReturned && MsiExitCode == 0 && !ControlFaulted &&
        Fences.Any(fence => fence.OwnSession && fence.Phase == GuardPhase.Preflight) &&
        Fences.Any(fence => fence.OwnSession && fence.Phase == GuardPhase.Transaction);
}

internal sealed record FenceSignal(Guid RunId, GuardPhase Phase, Guid Challenge)
{
    private const string Marker = "Hermes.A2.Fence";

    internal string Encode() => $"{Marker}/1/{RunId:N}/{Phase}/{Challenge:N}";

    internal static FenceSignal? Decode(string text)
    {
        var parts = text.Split('/');
        if (parts.Length != 5 || parts[0] != Marker || parts[1] != "1")
            return null;
        try
        {
            var phase = parts[3] switch
            {
                nameof(GuardPhase.Preflight) => GuardPhase.Preflight,
                nameof(GuardPhase.Transaction) => GuardPhase.Transaction,
                _ => throw new FormatException()
            };
            return new(Guid.ParseExact(parts[2], "N"), phase, Guid.ParseExact(parts[4], "N"));
        }
        catch (FormatException) { return null; }
    }
}

internal static class ControlNames
{
    internal const int MaximumPacket = 8192;
    internal static string Pipe(Guid run) => $"Hermes.Servicing.{run:N}";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ControlEndpoint))]
[JsonSerializable(typeof(GuardExchange))]
[JsonSerializable(typeof(ControlReply))]
[JsonSerializable(typeof(InstallerCallReceipt))]
internal partial class ControlJson : JsonSerializerContext;
