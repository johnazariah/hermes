using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hermes.Tray;

namespace Hermes.Tray.Smoke;

internal sealed record AssetProof(string Path, string Sha256);
internal sealed record RouteProof(string Path, string Heading, bool SpaNavigation, bool DeepLink);
internal sealed record ReactProofResult(
    string Runtime, ImmutableArray<AssetProof> Assets, ImmutableArray<RouteProof> Routes,
    NativeAuthenticationProof Authentication);
internal sealed record RouteTarget(string Path, string Heading);
internal sealed record AssetManifest(ImmutableArray<string> Entrypoints);
internal sealed record DomState(string Path, bool React, string Heading, ImmutableArray<string> Scripts, ImmutableArray<string> Links);

internal static class ReactProof
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ImmutableArray<RouteTarget> Targets =
    [
        new("/documents", "Documents"), new("/search", "Search"), new("/settings", "Settings"),
        new("/chat", "Chat"), new("/pipeline", "Pipeline")
    ];

    private const string InspectScript = """
        (() => ({
          path: location.pathname,
          react: document.querySelector('#root[data-hermes-app="canonical-react"]') !== null,
          heading: document.querySelector('main h1')?.textContent?.trim() ?? '',
          scripts: [...document.querySelectorAll('script[type="module"][src]')].map(s => new URL(s.src).href).sort(),
          links: [...document.querySelectorAll('nav a[href]')].map(a => new URL(a.href).pathname)
        }))()
        """;

    public static async Task<ReactProofResult> RunAsync(
        TrayOptions options, MainWindow window, SmokeOwnershipOracle oracle,
        CancellationToken cancellationToken)
    {
        using var http = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false });
        var assets = await ProveAssetsAsync(options, http, oracle, cancellationToken);
        await oracle.ProveAsync("webview:open", window.OpenAsync, cancellationToken);
        var root = await AwaitDomAsync(options, window, oracle, new("/", ""), assets, cancellationToken);
        if (!Targets.All(target => root.Links.Contains(target.Path)))
            throw new InvalidOperationException("Canonical React navigation links are missing.");
        var routes = ImmutableArray.CreateBuilder<RouteProof>();
        routes.Add(new("/", root.Heading, false, true));
        foreach (var target in Targets)
            routes.Add(await ProveRouteAsync(options, window, oracle, target, assets, cancellationToken));
        var authentication = await NativeAuthentication.RunAsync(options, window, oracle, cancellationToken);
        return new(window.RuntimeVersion, assets, routes.ToImmutable(), authentication);
    }

    private static async Task<ImmutableArray<AssetProof>> ProveAssetsAsync(
        TrayOptions options, HttpClient http, SmokeOwnershipOracle oracle,
        CancellationToken cancellationToken)
    {
        var webRoot = Path.Combine(Path.GetDirectoryName(options.ServicePath) ?? "", "wwwroot");
        var manifest = await MatchingBytesAsync(options, http, oracle, webRoot, "/asset-manifest.json", cancellationToken);
        var parsed = JsonSerializer.Deserialize<AssetManifest>(manifest, Json)
            ?? throw new InvalidOperationException("Asset manifest is missing.");
        var scripts = parsed.Entrypoints.Where(path => path.EndsWith(".js", StringComparison.Ordinal)).ToImmutableArray();
        if (scripts.Length is 0 or > 32 || scripts.Distinct().Count() != scripts.Length)
            throw new InvalidOperationException("Asset manifest must contain distinct JavaScript entrypoints.");
        var proofs = await Task.WhenAll(scripts.Select(path =>
            ProveAssetAsync(options, http, oracle, webRoot, path, cancellationToken)));
        return proofs.OrderBy(proof => proof.Path, StringComparer.Ordinal).ToImmutableArray();
    }

    private static async Task<AssetProof> ProveAssetAsync(
        TrayOptions options, HttpClient http, SmokeOwnershipOracle oracle,
        string webRoot, string path, CancellationToken cancellationToken)
    {
        if (path.Length > 256 || !Regex.IsMatch(path, @"^/assets/[A-Za-z0-9_./-]+\.js$") ||
            path.Contains("..", StringComparison.Ordinal))
            throw new InvalidOperationException("Manifest entrypoint is not a canonical local JavaScript asset.");
        var bytes = await MatchingBytesAsync(options, http, oracle, webRoot, path, cancellationToken);
        return new(path, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static async Task<byte[]> MatchingBytesAsync(
        TrayOptions options, HttpClient http, SmokeOwnershipOracle oracle,
        string webRoot, string path, CancellationToken cancellationToken)
    {
        var published = await File.ReadAllBytesAsync(Path.Combine(webRoot, path.TrimStart('/')), cancellationToken);
        var served = await oracle.ProveAsync($"http:{path}",
            token => http.GetByteArrayAsync(new Uri(options.BaseAddress, path), token), cancellationToken);
        if (!SHA256.HashData(published).SequenceEqual(SHA256.HashData(served)))
            throw new InvalidOperationException("HTTP asset bytes differ from the isolated published Service assets.");
        return published;
    }

    private static async Task<RouteProof> ProveRouteAsync(
        TrayOptions options, MainWindow window, SmokeOwnershipOracle oracle, RouteTarget target,
        ImmutableArray<AssetProof> assets, CancellationToken cancellationToken)
    {
        var click = await oracle.ProveAsync($"webview:click:{target.Path}", token => window.ExecuteScriptAsync(
            $"(() => {{ const a = document.querySelector('nav a[href=\"{target.Path}\"]'); " +
            "if (!a) return false; a.click(); return true; })()", token), cancellationToken);
        if (click != "true")
            throw new InvalidOperationException("Canonical React navigation link could not be activated.");
        await AwaitDomAsync(options, window, oracle, target, assets, cancellationToken);
        await oracle.ProveAsync($"webview:deep-link:{target.Path}",
            token => window.NavigateAsync(target.Path, token), cancellationToken);
        var deep = await AwaitDomAsync(options, window, oracle, target, assets, cancellationToken);
        return new(target.Path, deep.Heading, true, true);
    }

    private static async Task<DomState> AwaitDomAsync(
        TrayOptions options, MainWindow window, SmokeOwnershipOracle oracle, RouteTarget target,
        ImmutableArray<AssetProof> assets, CancellationToken cancellationToken)
    {
        var expected = assets.Select(asset => new Uri(options.BaseAddress, asset.Path).AbsoluteUri)
            .Order(StringComparer.Ordinal).ToImmutableArray();
        while (true)
        {
            var text = await oracle.ProveAsync($"webview:inspect:{target.Path}",
                token => window.ExecuteScriptAsync(InspectScript, token), cancellationToken);
            var state = JsonSerializer.Deserialize<DomState>(text, Json);
            if (state is { React: true, Heading.Length: > 0 } && state.Path == target.Path &&
                !state.Scripts.IsDefault && state.Scripts.SequenceEqual(expected) &&
                (target.Heading.Length == 0 || string.Equals(state.Heading, target.Heading, StringComparison.OrdinalIgnoreCase)))
                return state;
            await Task.Delay(100, cancellationToken);
        }
    }
}
