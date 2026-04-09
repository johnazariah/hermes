using System.Diagnostics;
using System.Drawing;
using System.Net.Http;
using System.Windows.Forms;

Application.EnableVisualStyles();

var trayIcon = new NotifyIcon
{
    Text = "Hermes — Document Intelligence",
    Icon = SystemIcons.Application,
    Visible = true,
    ContextMenuStrip = new ContextMenuStrip()
};

trayIcon.ContextMenuStrip.Items.Add("Open Hermes", null, (_, _) => OpenBrowser());
trayIcon.ContextMenuStrip.Items.Add("Sync Now", null, async (_, _) =>
{
    using var http = new HttpClient();
    try { await http.PostAsync("http://localhost:21741/api/sync", null); } catch { }
});
trayIcon.ContextMenuStrip.Items.Add("-");
trayIcon.ContextMenuStrip.Items.Add("Quit", null, (_, _) => Application.Exit());

trayIcon.DoubleClick += (_, _) => OpenBrowser();

Application.Run();

trayIcon.Visible = false;
trayIcon.Dispose();

static void OpenBrowser()
{
    Process.Start(new ProcessStartInfo("http://localhost:21741") { UseShellExecute = true });
}
