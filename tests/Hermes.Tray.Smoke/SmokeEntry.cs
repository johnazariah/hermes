using Hermes.Servicing;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal static class SmokeEntry
{
    internal static Task<int> RunAsync(
        string[] arguments,
        Func<IStartupAdmission> createAdmission,
        Func<string, string, int> createFixture,
        Func<string[], Task<int>> smoke,
        Action<string> diagnostic) =>
        NativeStartupGate.RunAsync(createAdmission,
            _ => DispatchAsync(arguments, createFixture, smoke, diagnostic), diagnostic);

    private static async Task<int> DispatchAsync(
        string[] arguments,
        Func<string, string, int> createFixture,
        Func<string[], Task<int>> smoke,
        Action<string> diagnostic)
    {
        try
        {
            if (arguments is ["--create-owner-fixture", var receipt, "--archive-directory", var archive])
                return createFixture(receipt, archive);
            return await smoke(arguments);
        }
        catch (Exception error)
        {
            diagnostic($"Installed native smoke failed: {ProofFiles.Failure(error)}");
            return 1;
        }
    }
}
