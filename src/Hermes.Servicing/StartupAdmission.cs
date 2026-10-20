using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Hermes.Servicing;

public sealed class StartupAdmission(
    IRuntimeGate gate,
    ILoadedCode loaded,
    ILeaseLifetime lifetime) : IStartupAdmission
{
    private readonly string? _expectedRoot;
    private readonly IPhysicalPaths? _physical;

    internal StartupAdmission(
        IRuntimeGate gate, ILoadedCode loaded, ILeaseLifetime lifetime,
        string expectedRoot, IPhysicalPaths physical)
        : this(gate, loaded, lifetime) =>
        (_expectedRoot, _physical) = (expectedRoot, physical);

    public Admission Enter(RuntimeEntry entry) => gate.TryOpen() switch
    {
        GateOpened opened => Admit(opened.Lease, entry),
        GateUnavailable unavailable => new Refused(unavailable.Reason),
        _ => new Refused(RefusalReason.InvalidState)
    };

    private Admission Admit(IRuntimeReadLease lease, RuntimeEntry entry)
    {
        var retained = false;
        try
        {
            var installation = RequireInstallation(lease);
            LoadedValidation.Require(installation, loaded.Read(), entry);
            var token = lifetime.Retain(lease)
                ?? throw new AdmissionException(RefusalReason.InvalidState);
            retained = true;
            return new Admitted(ChildCapability(token, installation));
        }
        catch (AdmissionException error) { return new Refused(error.Reason); }
        catch (Exception error) when (IsStateFailure(error))
        { return new Refused(RefusalReason.InvalidState); }
        finally { if (!retained) lease.Dispose(); }
    }

    private ReadyInstallation RequireInstallation(IRuntimeReadLease lease)
    {
        var installation = lease.ReadReady().Installation
            ?? throw new AdmissionException(RefusalReason.UnownedInstallation);
        ReadyValidation.Require(installation);
        if (_expectedRoot is { } root)
            RuntimePaths.RequireFixedRoot(installation, root);
        return installation;
    }

    private IProcessLease ChildCapability(IProcessLease token, ReadyInstallation installation) =>
        _physical is { } physical
            ? new EnrolledServiceLease(token, installation, physical, new PortableExecutableMetadata())
            : token;

    internal static bool IsStateFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or InvalidOperationException or NotSupportedException;
}

public static class WindowsStartupAdmission
{
    [SupportedOSPlatform("windows")]
    public static IStartupAdmission ForCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows servicing admission is Windows-only.");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(profile))
            throw new InvalidOperationException("The user profile could not be resolved.");
        var path = Path.Combine(profile, "Hermes", "Servicing",
            HermesServicingProtocol.UpgradeCode.ToString("D"), "admission.bin");
        return Create(Path.Combine(profile, "Hermes", "App"),
            new WindowsPhysicalPaths(), new WindowsRuntimeGate(path),
            new CurrentLoadedCode(), ProcessLeaseRoot.Instance);
    }

    internal static IStartupAdmission Create(
        string appDirectory, IPhysicalPaths physical, IRuntimeGate gate,
        ILoadedCode loaded, ILeaseLifetime lifetime) =>
        new FixedRootStartupAdmission(
            RuntimePaths.Canonical(appDirectory), physical, gate, loaded, lifetime);
}

internal sealed class CurrentLoadedCode : ILoadedCode
{
    public LoadedCode Read()
    {
        var entry = Assembly.GetEntryAssembly()
            ?? throw new AdmissionException(RefusalReason.InvalidState);
        var images = AppDomain.CurrentDomain.GetAssemblies()
            .Where(IsHermesAssembly)
            .Select(assembly => new LoadedImage(
                assembly.Location, assembly.ManifestModule.ModuleVersionId))
            .ToImmutableArray();
        return new(entry.Location, entry.ManifestModule.ModuleVersionId, images);
    }

    private static bool IsHermesAssembly(Assembly assembly) =>
        assembly.GetName().Name is { } name &&
        name.StartsWith("Hermes.", StringComparison.Ordinal);
}

internal sealed class ProcessLeaseRoot : ILeaseLifetime
{
    internal static ProcessLeaseRoot Instance { get; } = new();
    private readonly ConcurrentBag<IRuntimeReadLease> _leases = [];
    private ProcessLeaseRoot() { }

    public IProcessLease Retain(IRuntimeReadLease lease)
    {
        var token = new ProcessToken();
        _leases.Add(lease);
        return token;
    }

    private sealed record ProcessToken : IProcessLease;
}

internal sealed class AdmissionException(RefusalReason reason)
    : Exception(reason.ToString())
{
    internal RefusalReason Reason { get; } = reason;
}

internal static class ReadyValidation
{
    internal static void Require(ReadyInstallation installation)
    {
        if (installation.Binding is not { } binding)
            throw new AdmissionException(RefusalReason.InvalidState);
        if (binding.UpgradeCode != HermesServicingProtocol.UpgradeCode ||
            binding.AdmissionAbi != HermesServicingProtocol.AdmissionAbi)
            throw new AdmissionException(RefusalReason.UnsupportedProtocol);
        if (binding.AppDirectory is not { } root || !ValidRoot(root) ||
            installation.ProductCode == Guid.Empty || installation.Images.IsDefaultOrEmpty ||
            !installation.Images.All(ValidImage) ||
            installation.Images.Select(image => image.RelativePath)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != installation.Images.Length)
            throw new AdmissionException(RefusalReason.InvalidState);
    }

    private static bool ValidImage(ManagedImage image) =>
        image is { RelativePath: { } path, ModuleVersionId: var mvid } &&
        mvid != Guid.Empty && !string.IsNullOrWhiteSpace(path) &&
        path.Split('/').All(ValidSegment);

    internal static bool ValidRoot(string path)
    {
        if (!Path.IsPathFullyQualified(path) ||
            Path.GetPathRoot(path) is not { } prefix || !ValidRootPrefix(prefix))
            return false;
        var relative = Path.TrimEndingDirectorySeparator(path)[prefix.Length..];
        return relative.Length == 0 ||
            relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .All(ValidSegment);
    }

    private static bool ValidRootPrefix(string prefix) => prefix.Replace('\\', '/') switch
    {
        "/" => !OperatingSystem.IsWindows(),
        [var drive, ':', '/'] => char.IsAsciiLetter(drive),
        var unc when unc.StartsWith("//", StringComparison.Ordinal) =>
            unc[2..].TrimEnd('/').Split('/') is [var server, var share] &&
            ValidSegment(server) && ValidSegment(share),
        _ => false
    };

    private static bool ValidSegment(string segment) =>
        segment.Length > 0 && segment[^1] is not ('.' or ' ') &&
        segment.All(character => character >= ' ' &&
            character is not ('<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*')) &&
        !IsDeviceName(segment);

    private static bool IsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            name.Length == 4 && (name[..3] is "COM" or "LPT") &&
            (name[3] is >= '1' and <= '9' or '¹' or '²' or '³');
    }
}

internal static class LoadedValidation
{
    internal static void Require(
        ReadyInstallation installation, LoadedCode loaded, RuntimeEntry entry)
    {
        var relative = entry switch
        {
            RuntimeEntry.Tray => "tray/Hermes.Tray.dll",
            RuntimeEntry.Service or RuntimeEntry.Operator => "service/Hermes.Service.dll",
            _ => throw new AdmissionException(RefusalReason.UnsupportedProtocol)
        };
        var expectedEntry = Path.Combine(installation.Binding.AppDirectory, relative);
        if (!SamePath(expectedEntry, loaded.EntryAssemblyPath) ||
            !Matches(installation, new(loaded.EntryAssemblyPath, loaded.EntryModuleVersionId)) ||
            loaded.Images.IsDefaultOrEmpty || !loaded.Images.All(image => Matches(installation, image)))
            throw new AdmissionException(RefusalReason.WrongGeneration);
    }

    private static bool Matches(ReadyInstallation installation, LoadedImage loaded) =>
        loaded is { AssemblyPath: { } path, ModuleVersionId: var mvid } &&
        installation.Images.Any(image =>
            image.ModuleVersionId == mvid &&
            SamePath(Path.Combine(installation.Binding.AppDirectory, image.RelativePath), path));

    private static bool SamePath(string left, string right) =>
        string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string path) =>
        Path.IsPathFullyQualified(path)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            : throw new AdmissionException(RefusalReason.InvalidState);
}

internal enum CodePlacement { Installed, Outside }

internal interface IPhysicalPaths
{
    string Directory(string path);
    string Image(string path);
}

internal static class RuntimePaths
{
    internal static string Canonical(string? path) =>
        path is { } value && ReadyValidation.ValidRoot(value)
            ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(value))
            : throw new AdmissionException(RefusalReason.InvalidState);

    internal static bool Contains(string root, string path) =>
        string.Equals(root, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(
            Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    internal static void RequireFixedRoot(ReadyInstallation installation, string root)
    {
        if (!string.Equals(Canonical(installation.Binding.AppDirectory),
            Canonical(root), StringComparison.OrdinalIgnoreCase))
            throw new AdmissionException(RefusalReason.UnownedInstallation);
    }

    internal static ImmutableArray<string> ImagePaths(LoadedCode code)
    {
        if (code is not { EntryModuleVersionId: var entryId, Images: var images } ||
            entryId == Guid.Empty || images.IsDefaultOrEmpty ||
            !images.All(image => image is { ModuleVersionId: var id } && id != Guid.Empty))
            throw new AdmissionException(RefusalReason.InvalidState);
        return images.Select(image => Canonical(image.AssemblyPath))
            .Prepend(Canonical(code.EntryAssemblyPath))
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
    }
}

internal sealed class FixedRootPlacement(string appDirectory, IPhysicalPaths physical)
{
    internal CodePlacement Classify(LoadedCode code)
    {
        var root = RuntimePaths.Canonical(appDirectory);
        var images = RuntimePaths.ImagePaths(code);
        var inside = images.Count(path => RuntimePaths.Contains(root, path));
        if (inside == images.Length)
            return CodePlacement.Installed;
        if (inside != 0)
            throw new AdmissionException(RefusalReason.WrongGeneration);
        var resolvedRoot = RuntimePaths.Canonical(physical.Directory(root));
        if (images.Select(physical.Image).Select(RuntimePaths.Canonical)
            .Any(path => RuntimePaths.Contains(resolvedRoot, path)))
            throw new AdmissionException(RefusalReason.WrongGeneration);
        return CodePlacement.Outside;
    }
}

internal sealed class FixedRootStartupAdmission(
    string appDirectory, IPhysicalPaths physical, IRuntimeGate gate,
    ILoadedCode loaded, ILeaseLifetime lifetime) : IStartupAdmission
{
    private readonly FixedRootPlacement _placement = new(appDirectory, physical);
    private readonly StartupAdmission _installed = new(gate, loaded, lifetime, appDirectory, physical);

    public Admission Enter(RuntimeEntry entry)
    {
        try
        {
            if (entry is not (RuntimeEntry.Tray or RuntimeEntry.Service or RuntimeEntry.Operator))
                return new Refused(RefusalReason.UnsupportedProtocol);
            return _placement.Classify(loaded.Read()) switch
            {
                CodePlacement.Installed => _installed.Enter(entry),
                CodePlacement.Outside => Outside(),
                _ => new Refused(RefusalReason.InvalidState)
            };
        }
        catch (AdmissionException error) { return new Refused(error.Reason); }
        catch (Exception error) when (StartupAdmission.IsStateFailure(error))
        { return new Refused(RefusalReason.InvalidState); }
    }

    private Admission Outside() => gate.TryOpen() switch
    {
        GateUnavailable { Reason: RefusalReason.UnownedInstallation } =>
            new OutsideKnownInstallation(),
        GateUnavailable unavailable => new Refused(unavailable.Reason),
        GateOpened { Lease: { } lease } => VerifyOutside(lease),
        _ => new Refused(RefusalReason.InvalidState)
    };

    private Admission VerifyOutside(IRuntimeReadLease lease)
    {
        using (lease)
        {
            if (lease.ReadReady().Installation is { } installation)
            {
                ReadyValidation.Require(installation);
                RuntimePaths.RequireFixedRoot(installation, appDirectory);
            }
            return new OutsideKnownInstallation();
        }
    }
}
