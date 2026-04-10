using System.Drawing;
using System.Net.Http;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

Application.EnableVisualStyles();
Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

var form = new Form
{
    Text = "Hermes — Document Intelligence",
    Width = 1280,
    Height = 800,
    StartPosition = FormStartPosition.CenterScreen,
    Icon = SystemIcons.Application
};

var webView = new WebView2 { Dock = DockStyle.Fill };
form.Controls.Add(webView);

// Tray icon — minimize to tray, restore on double-click
var trayIcon = new NotifyIcon
{
    Text = "Hermes — Document Intelligence",
    Icon = SystemIcons.Application,
    Visible = true,
    ContextMenuStrip = new ContextMenuStrip()
};

trayIcon.ContextMenuStrip.Items.Add("Show", null, (_, _) => { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); });
trayIcon.ContextMenuStrip.Items.Add("Sync Now", null, async (_, _) =>
{
    using var http = new HttpClient();
    try { await http.PostAsync("http://localhost:21741/api/sync", null); } catch { }
});
trayIcon.ContextMenuStrip.Items.Add("-");
trayIcon.ContextMenuStrip.Items.Add("Quit", null, (_, _) => { trayIcon.Visible = false; Application.Exit(); });

trayIcon.DoubleClick += (_, _) => { form.Show(); form.WindowState = FormWindowState.Normal; form.Activate(); };

// Minimize to tray instead of closing
form.FormClosing += (_, e) =>
{
    if (e.CloseReason == CloseReason.UserClosing)
    {
        e.Cancel = true;
        form.Hide();
    }
};

form.Shown += async (_, _) =>
{
    await webView.EnsureCoreWebView2Async();
    webView.CoreWebView2.Navigate("http://localhost:21741");
};

Application.Run(form);

trayIcon.Visible = false;
trayIcon.Dispose();
