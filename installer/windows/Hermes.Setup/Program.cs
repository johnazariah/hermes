using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.Versioning;
using Hermes.Servicing;

namespace Hermes.Setup;

internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        try
        {
            return await SetupEntryPoint.RunAsync(arguments, OperatingSystem.IsWindows(),
                new WindowsSetupFactory(), Console.Out, Console.Error, CancellationToken.None);
        }
        catch (AdmissionException)
        {
            return await SetupEntryPoint.NativeRefusalAsync(
                arguments, Console.Out, Console.Error);
        }
    }
}

internal sealed class WindowsSetupFactory : ISetupApplicationFactory
{
    public Effect<SetupApplication> Create()
    {
        if (!OperatingSystem.IsWindows())
            return Effect<SetupApplication>.Fail(SetupCode.Unavailable);
        var trust = CompiledGuardTrust.Read(typeof(Program).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Select(attribute => new KeyValuePair<string, string?>(
                attribute.Key, attribute.Value)));
        return trust is Effect<ApprovedGuardProfile>.Success(var approved)
            ? Effect<SetupApplication>.Ok(CreateWindows(approved))
            : Effect<SetupApplication>.Fail(trust.FailureCode);
    }

    [SupportedOSPlatform("windows")]
    private static SetupApplication CreateWindows(ApprovedGuardProfile approved)
    {
        var scope = WindowsSetupScope.ForCurrentUser();
        var programs = ProgramsDirectory();
        var disk = new SetupDisk(new PhysicalSetupFileSystem(),
            new WindowsSetupPathSafety(), new WindowsAtomicFileCommit());
        var database = new WindowsPackageDatabase();
        var capabilities = new SetupCapabilities(scope, programs, CoordinatorImages(),
            disk, database, database, new WindowsRegistrationInventory(),
            new WindowsPathIdentityQuery(),
            new FileAdmissionSource(disk, scope, new SharedSetupGateCodec()),
            new WindowsGuardedEngine());
        return SetupComposition.Create(capabilities, approved);
    }

    private static ImmutableArray<string> CoordinatorImages()
    {
        var executable = Environment.ProcessPath
            ?? throw new SetupIoException(SetupCode.Unavailable);
        var assembly = typeof(Program).Assembly.Location;
        SetupIo.Require(!string.IsNullOrWhiteSpace(assembly) &&
            Path.IsPathFullyQualified(executable) && Path.IsPathFullyQualified(assembly),
            SetupCode.Unavailable);
        return [executable, assembly];
    }

    [SupportedOSPlatform("windows")]
    private static string ProgramsDirectory()
    {
        var programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        SetupIo.Require(!string.IsNullOrWhiteSpace(programs) &&
            Path.IsPathFullyQualified(programs), SetupCode.Unavailable);
        return programs;
    }
}
