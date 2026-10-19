namespace Hermes.Tray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        try
        {
            var options = TrayOptions.Parse(arguments, Environment.GetEnvironmentVariable, AppContext.BaseDirectory);
            using var lease = InstanceLease.Acquire(options);
            ApplicationConfiguration.Initialize();
            using var context = new TrayApplicationContext(options);
            Application.Run(context);
            return Environment.ExitCode;
        }
        catch (Exception error)
        {
            var detail = error is ArgumentException or InvalidOperationException
                ? error.Message : SafeFailure.Describe(error);
            MessageBox.Show(detail, "Hermes could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
