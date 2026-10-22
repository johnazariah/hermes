namespace Hermes.Tray;

using Hermes.Servicing;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments) =>
        Run(arguments, NativeStartupGate.ForCurrentProcess, RunNormal,
            Smoke.InstalledDiagnostic.Run, Console.Error.WriteLine);

    internal static int Run(
        string[] arguments,
        Func<IStartupAdmission> createAdmission,
        Func<string[], NativeStartupContext, int> normal,
        Func<string, NativeStartupContext, int> diagnostic,
        Action<string> refusal) =>
        NativeStartupGate.Run(createAdmission, startup => arguments switch
        {
            ["--installed-smoke", var workspace] => diagnostic(workspace, startup),
            _ => normal(arguments, startup)
        }, refusal);

    private static int RunNormal(string[] arguments, NativeStartupContext startup)
    {
        try
        {
            var options = TrayOptions.Parse(
                arguments, Environment.GetEnvironmentVariable, AppContext.BaseDirectory).ForStartup(startup);
            using var lease = InstanceLease.Acquire(options);
            ApplicationConfiguration.Initialize();
            using var context = new TrayApplicationContext(options);
            Application.Run(context);
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            return ReportFailure(error);
        }
    }

    private static int ReportFailure(Exception error)
    {
        var detail = error is ArgumentException or InvalidOperationException
            ? error.Message : SafeFailure.Describe(error);
        MessageBox.Show(detail, "Hermes could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return 1;
    }
}
