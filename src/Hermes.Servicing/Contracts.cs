using System.Collections.Immutable;

namespace Hermes.Servicing;

public static class HermesServicingProtocol
{
    public static readonly Guid UpgradeCode =
        new("7A2E1F3D-4B5C-6D7E-8F9A-0B1C2D3E4F5A");

    public const int AdmissionAbi = 1;
    public const string RunProperty = "HERMES_SERVICING_RUN";
    public const string AbiProperty = "HERMES_SERVICING_ABI";
    public const string RollbackProperty = "HERMES_ALLOW_ROLLBACK";
}

public enum RuntimeEntry { Tray, Service, Operator }

public enum RefusalReason
{
    Busy,
    Closed,
    UnownedInstallation,
    UnsupportedProtocol,
    WrongGeneration,
    InvalidState,
    Unavailable
}

public sealed record InstallationBinding(
    Guid UpgradeCode,
    string AppDirectory,
    int AdmissionAbi);

public sealed record ManagedImage(string RelativePath, Guid ModuleVersionId);

public sealed record ReadyInstallation(
    InstallationBinding Binding,
    Guid ProductCode,
    ImmutableArray<ManagedImage> Images);

// Null means a verified absent installation, not an admitted runtime.
public sealed record GateReadyState(ReadyInstallation? Installation);

public sealed record LoadedImage(string AssemblyPath, Guid ModuleVersionId);

public sealed record LoadedCode(
    string EntryAssemblyPath,
    Guid EntryModuleVersionId,
    ImmutableArray<LoadedImage> Images);

// Deliberately neither IDisposable nor a release capability.
public interface IProcessLease { }

// Selection is not child admission. The selected Service must acquire its own lease.
public interface IEnrolledServiceLease : IProcessLease
{
    ServiceSelection SelectService(string executablePath);
}

public abstract record ServiceSelection;
public sealed record ServiceSelected(string ExecutablePath) : ServiceSelection;
public sealed record ServiceRefused(RefusalReason Reason) : ServiceSelection;

public abstract record Admission;
public sealed record Admitted(IProcessLease Lease) : Admission;
// Positive outside-placement evidence; neither installed admission nor a lease.
public sealed record OutsideKnownInstallation : Admission;
public sealed record Refused(RefusalReason Reason) : Admission;

public interface IStartupAdmission
{
    Admission Enter(RuntimeEntry entry);
}

public interface IRuntimeReadLease : IDisposable
{
    GateReadyState ReadReady();
}

public abstract record GateOpen;
public sealed record GateOpened(IRuntimeReadLease Lease) : GateOpen;
public sealed record GateUnavailable(RefusalReason Reason) : GateOpen;

public interface IRuntimeGate
{
    GateOpen TryOpen();
}

public interface ILoadedCode
{
    LoadedCode Read();
}

public interface ILeaseLifetime
{
    IProcessLease Retain(IRuntimeReadLease lease);
}

public enum GuardPhase { Preflight, Transaction }
public enum MaintenanceKind { Install, Repair, Uninstall }

public sealed record GuardRequest(
    GuardPhase Phase,
    Guid ProductCode,
    Guid? UpgradingProductCode,
    MaintenanceKind Operation,
    string AppDirectory);

public enum GuardDecision { Permit, Refuse }

public interface IInstallerAdmission
{
    GuardDecision Validate(GuardRequest request);
}
