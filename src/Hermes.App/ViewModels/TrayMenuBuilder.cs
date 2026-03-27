using Avalonia.Controls;

namespace Hermes.App.ViewModels;

public static class TrayMenuBuilder
{
    public static NativeMenu Build(TrayIconViewModel vm)
    {
        var menu = new NativeMenu();

        // Status line (disabled — info only)
        var statusItem = new NativeMenuItem(vm.StatusText) { IsEnabled = false };
        menu.Items.Add(statusItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        // Open Hermes
        var openItem = new NativeMenuItem("Open Hermes");
        openItem.Click += (_, _) => vm.OpenShellWindow();
        menu.Items.Add(openItem);

        // Open Archive Folder
        var archiveItem = new NativeMenuItem("Open Archive Folder");
        archiveItem.Click += (_, _) => vm.OpenArchiveFolder();
        menu.Items.Add(archiveItem);

        menu.Items.Add(new NativeMenuItemSeparator());

        // Pause / Resume
        var pauseItem = new NativeMenuItem(vm.IsPaused ? "Resume" : "Pause");
        pauseItem.Click += (_, _) =>
        {
            vm.TogglePause();
            pauseItem.Header = vm.IsPaused ? "Resume" : "Pause";
        };
        menu.Items.Add(pauseItem);

        // Update available (shown dynamically)
        if (vm.UpdateAvailable is { IsUpdateAvailable: true } update)
        {
            menu.Items.Add(new NativeMenuItemSeparator());
            var updateItem = new NativeMenuItem($"Update Available — v{update.LatestVersion}");
            updateItem.Click += (_, _) => vm.OpenUpdatePage();
            menu.Items.Add(updateItem);
        }

        menu.Items.Add(new NativeMenuItemSeparator());

        // Quit
        var quitItem = new NativeMenuItem("Quit Hermes");
        quitItem.Click += (_, _) => vm.Quit();
        menu.Items.Add(quitItem);

        return menu;
    }
}
