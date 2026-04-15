using Hermes.Shell.Services;

namespace Hermes.Shell;

public partial class App : Application
{
    public App()
    {
        InitializeComponent();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "Hermes" };
        window.Destroying += OnWindowDestroying;
        return window;
    }

    private static void OnWindowDestroying(object? sender, EventArgs e)
    {
        if (MauiProgram.Services?.GetService(typeof(ServiceManager)) is ServiceManager mgr)
            mgr.Stop();
    }
}
