using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal sealed record NativeAuthenticationProof(
    bool Paired, string CurrentInstance, ImmutableArray<string> Capabilities,
    int AnonymousRead, int AnonymousMutation, int SettingsRead,
    int PreferencesWrite, bool PreferencesPersisted, int SyncMutation, bool SyncAccepted);

internal static class NativeAuthentication
{
    private const string Receipt = "Synthetic native protected write receipt";
    private const string PreferencesSaved = "Preferences saved.";
    private const string SyncAccepted = "Sync request accepted. Progress appears in Pipeline.";

    public static async Task<NativeAuthenticationProof> RunAsync(
        TrayOptions options, MainWindow window, SmokeOwnershipOracle oracle,
        CancellationToken cancellationToken)
    {
        var config = RequireFixture(options);
        var denial = await ProveAnonymousAsync(options, oracle, cancellationToken);
        using var responses = new NativeResponseProbe(window);
        await oracle.ProveAsync("access:settings-navigation",
            token => window.NavigateAsync("/settings", token), cancellationToken);
        await ProveSettingsAsync(window, oracle, config, cancellationToken);
        await ProvePreferencesAsync(options, window, oracle, cancellationToken);
        await ProveSyncAsync(window, oracle, cancellationToken);
        var access = window.AccessEvidence;
        if (!access.Paired || access.CurrentInstance is not { } instance ||
            responses.SettingsRead != 200 || responses.PreferencesWrite != 200 || responses.SyncMutation != 202)
            throw new InvalidOperationException("Installed protected native evidence is incomplete.");
        return new(true, instance, access.Capabilities, denial.Read, denial.Mutation,
            responses.SettingsRead, responses.PreferencesWrite, true, responses.SyncMutation, true);
    }

    private static string RequireFixture(TrayOptions options)
    {
        if (!options.Synthetic || File.Exists(Path.Combine(options.ConfigDirectory, "preferences.txt")))
            throw new InvalidOperationException("A fresh synthetic native fixture is required.");
        var bytes = ProofFiles.Read(Path.Combine(options.ConfigDirectory, "config.yaml"), 65536);
        var config = System.Text.Encoding.UTF8.GetString(bytes);
        if (!config.Contains("preferences: Synthetic native smoke preferences", StringComparison.Ordinal))
            throw new InvalidOperationException("The expected synthetic configuration is unavailable.");
        return config;
    }

    private static async Task<(int Read, int Mutation)> ProveAnonymousAsync(
        TrayOptions options, SmokeOwnershipOracle oracle, CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = false, UseCookies = false, AllowAutoRedirect = false
        });
        using var read = await oracle.ProveAsync("access:anonymous-settings",
            token => http.GetAsync(new Uri(options.BaseAddress, "/api/settings"), token), cancellationToken);
        if (read.StatusCode != HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Anonymous protected configuration was not denied.");
        using var mutation = await oracle.ProveAsync("access:anonymous-sync",
            token => http.PostAsync(new Uri(options.BaseAddress, "/api/sync"), null, token), cancellationToken);
        if (mutation.StatusCode != HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("Anonymous protected mutation was not denied.");
        return ((int)read.StatusCode, (int)mutation.StatusCode);
    }

    private static async Task ProveSettingsAsync(
        MainWindow window, SmokeOwnershipOracle oracle, string expectedConfig,
        CancellationToken cancellationToken)
    {
        await WaitAsync(window, oracle, "access:paired", """
            (() => document.querySelector('#root[data-hermes-app="canonical-react"]') !== null &&
              document.querySelector('section[aria-label="Hermes access"] strong')?.textContent ===
                'Access: authenticated')()
            """, cancellationToken);
        await ClickAsync(window, oracle, "access:configuration-section", """
            (() => {
              const button = [...document.querySelectorAll('main button')].find(
                b => b.querySelector('h2')?.textContent?.trim() === 'Configuration (YAML)');
              if (!button) return false;
              button.click(); return true;
            })()
            """, cancellationToken);
        await WaitAsync(window, oracle, "access:configuration-result",
            "(() => { const t = document.querySelector('main textarea[rows=\"20\"]'); " +
            "return t !== null && !t.disabled && t.value === " +
            JsonSerializer.Serialize(expectedConfig) + "; })()", cancellationToken);
    }

    private static async Task ProvePreferencesAsync(
        TrayOptions options, MainWindow window, SmokeOwnershipOracle oracle,
        CancellationToken cancellationToken)
    {
        await ClickAsync(window, oracle, "access:preferences-edit", PreferenceEditScript(), cancellationToken);
        await WaitAsync(window, oracle, "access:preferences-enabled", PreferenceButtonScript(false),
            cancellationToken);
        await ClickAsync(window, oracle, "access:preferences-save", PreferenceButtonScript(true),
            cancellationToken);
        await WaitAsync(window, oracle, "access:preferences-result", FeedbackScript(PreferencesSaved),
            cancellationToken);
        var saved = await oracle.ProveAsync("access:preferences-persisted",
            token => File.ReadAllTextAsync(Path.Combine(options.ConfigDirectory, "preferences.txt"), token),
            cancellationToken);
        if (saved != Receipt)
            throw new InvalidOperationException("The protected Settings write was not persisted.");
    }

    private static string PreferenceEditScript() => """
        (() => {
          const t = document.querySelector('main textarea[rows="5"]');
          const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, 'value')?.set;
          if (!t || t.disabled || !setter) return false;
          setter.call(t,
        """ + JsonSerializer.Serialize(Receipt) + """
          );
          t.dispatchEvent(new Event('input', { bubbles: true }));
          return true;
        })()
        """;

    private static string PreferenceButtonScript(bool click) => """
        (() => {
          const heading = [...document.querySelectorAll('main h2')].find(
            h => h.textContent?.trim() === 'Your Preferences');
          const section = heading?.closest('button')?.parentElement?.parentElement;
          const button = [...(section?.querySelectorAll('button') ?? [])].find(
            b => b.textContent?.trim() === 'Save');
          if (!button || button.disabled) return false;
        """ + (click ? "button.click();" : "") + """
          return true;
        })()
        """;

    private static async Task ProveSyncAsync(
        MainWindow window, SmokeOwnershipOracle oracle, CancellationToken cancellationToken)
    {
        await ClickAsync(window, oracle, "access:settings-sync", """
            (() => {
              const button = [...document.querySelectorAll('main button')].find(
                b => b.textContent?.trim() === 'Sync Now');
              if (!button || button.disabled) return false;
              button.click(); return true;
            })()
            """, cancellationToken);
        await WaitAsync(window, oracle, "access:sync-result", FeedbackScript(SyncAccepted),
            cancellationToken);
    }

    private static string FeedbackScript(string expected) =>
        "(() => [...document.querySelectorAll('main [role=\"status\"]')].some(" +
        "node => node.textContent === " + JsonSerializer.Serialize(expected) + "))()";

    private static async Task ClickAsync(
        MainWindow window, SmokeOwnershipOracle oracle, string step, string script,
        CancellationToken cancellationToken)
    {
        var result = await oracle.ProveAsync(step,
            token => window.ExecuteScriptAsync(script, token), cancellationToken);
        if (result != "true")
            throw new InvalidOperationException("The canonical Settings control could not be activated.");
    }

    private static async Task WaitAsync(
        MainWindow window, SmokeOwnershipOracle oracle, string step, string script,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = await oracle.ProveAsync(step,
                token => window.ExecuteScriptAsync(script, token), cancellationToken);
            if (result == "true")
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }
}

internal sealed class NativeResponseProbe : IDisposable
{
    private readonly MainWindow _window;
    private int _settings;
    private int _preferences;
    private int _sync;

    public NativeResponseProbe(MainWindow window)
    {
        _window = window;
        window.ProtectedResponse += Record;
    }

    public int SettingsRead => _settings;
    public int PreferencesWrite => _preferences;
    public int SyncMutation => _sync;

    private void Record(NativeHttpResponse response)
    {
        switch (response.Method, response.Path)
        {
            case ("GET", "/api/settings"): _settings = response.StatusCode; break;
            case ("PUT", "/api/preferences"): _preferences = response.StatusCode; break;
            case ("POST", "/api/sync"): _sync = response.StatusCode; break;
        }
    }

    public void Dispose() => _window.ProtectedResponse -= Record;
}
