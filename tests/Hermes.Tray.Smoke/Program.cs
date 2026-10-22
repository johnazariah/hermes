using System.Text.Json;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal static class Program
{
    private static Task<int> Main(string[] arguments) =>
        SmokeEntry.RunAsync(arguments, NativeStartupGate.ForCurrentProcess, CreateFixture,
            args => InstalledSmokeHarness.RunAsync(
                InstalledSmokeSettings.Parse(args), CancellationToken.None),
            Console.Error.WriteLine);

    private static int CreateFixture(string receipt, string archive)
    {
        _ = OwnerFixture.Create(receipt, archive);
        Console.WriteLine("Synthetic owner fixture created.");
        return 0;
    }
}
