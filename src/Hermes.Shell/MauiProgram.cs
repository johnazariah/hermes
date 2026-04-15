using Microsoft.Extensions.Logging;
using Hermes.Shell.Services;
using Hermes.UI.Services;

namespace Hermes.Shell;

public static class MauiProgram
{
    internal static IServiceProvider? Services { get; private set; }

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<ServiceManager>();
        builder.Services.AddScoped<IHermesClient>(sp =>
        {
            var mgr = sp.GetRequiredService<ServiceManager>();
            var http = new HttpClient { BaseAddress = new Uri(mgr.ServiceUrl) };
            return new HttpHermesClient(http);
        });

        var app = builder.Build();
        Services = app.Services;

        // Start the Hermes service process
        var serviceManager = app.Services.GetRequiredService<ServiceManager>();
        _ = serviceManager.StartAsync();

        return app;
    }
}
