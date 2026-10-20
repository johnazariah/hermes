using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Hermes.Servicing;

internal interface IManagedImageMetadata
{
    Guid ReadModuleVersionId(string path);
}

internal sealed class PortableExecutableMetadata : IManagedImageMetadata
{
    public Guid ReadModuleVersionId(string path)
    {
        using var stream = File.OpenRead(path);
        using var image = new PEReader(stream);
        var metadata = image.GetMetadataReader();
        return metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
    }
}

internal sealed class EnrolledServiceLease(
    IProcessLease retained,
    ReadyInstallation installation,
    IPhysicalPaths physical,
    IManagedImageMetadata metadata) : IEnrolledServiceLease
{
    private const string ServiceDirectory = "service";
    private const string ServiceEntry = "service/Hermes.Service.dll";
    private const string ServicingEntry = "service/Hermes.Servicing.dll";

    public ServiceSelection SelectService(string executablePath)
    {
        try
        {
            ReadyValidation.Require(installation);
            var executable = RequireExecutable(executablePath);
            RequireImages();
            return new ServiceSelected(executable);
        }
        catch (AdmissionException error) { return new ServiceRefused(error.Reason); }
        catch (Exception error) when (
            StartupAdmission.IsStateFailure(error) || error is BadImageFormatException)
        { return new ServiceRefused(RefusalReason.InvalidState); }
        finally { GC.KeepAlive(retained); }
    }

    private string RequireExecutable(string supplied)
    {
        var root = RuntimePaths.Canonical(installation.Binding.AppDirectory);
        var directory = Path.Combine(root, ServiceDirectory);
        var executable = Path.Combine(directory, "Hermes.Service.exe");
        RequireSame(executable, supplied);
        RequireSame(root, physical.Directory(root));
        RequireSame(directory, physical.Directory(directory));
        RequireSame(executable, physical.Image(executable));
        return executable;
    }

    private void RequireImages()
    {
        var images = installation.Images
            .Where(image => image.RelativePath.StartsWith(
                ServiceDirectory + "/", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (!images.Any(image => SameRelative(image.RelativePath, ServiceEntry)))
            throw new AdmissionException(RefusalReason.UnownedInstallation);
        var libraryMvid = typeof(StartupAdmission).Assembly.ManifestModule.ModuleVersionId;
        if (!images.Any(image => SameRelative(image.RelativePath, ServicingEntry) &&
            image.ModuleVersionId == libraryMvid))
            throw new AdmissionException(RefusalReason.WrongGeneration);
        foreach (var image in images)
            RequireImage(image);
    }

    private void RequireImage(ManagedImage image)
    {
        var path = RuntimePaths.Canonical(
            Path.Combine(installation.Binding.AppDirectory, image.RelativePath));
        RequireSame(path, physical.Image(path));
        if (metadata.ReadModuleVersionId(path) != image.ModuleVersionId)
            throw new AdmissionException(RefusalReason.WrongGeneration);
    }

    private static void RequireSame(string expected, string actual)
    {
        if (!string.Equals(RuntimePaths.Canonical(expected), RuntimePaths.Canonical(actual),
            StringComparison.OrdinalIgnoreCase))
            throw new AdmissionException(RefusalReason.UnownedInstallation);
    }

    private static bool SameRelative(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
