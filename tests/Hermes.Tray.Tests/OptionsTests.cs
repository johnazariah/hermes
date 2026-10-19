using Hermes.Tray;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void Published_sibling_layout_wins_over_legacy_child_layout()
    {
        using var layout = new Layout();
        var sibling = layout.Touch("service", ServiceWire.Executable);
        layout.Touch("tray", "service", ServiceWire.Executable);
        var result = TrayOptions.Parse([], _ => null, layout.Tray);
        Assert.Equal(sibling, result.ServicePath);
        Assert.Equal(21741, result.Port);
        Assert.Equal("127.0.0.1", result.BaseAddress.Host);
    }

    [Fact]
    public void Explicit_options_override_environment_without_reading_configuration()
    {
        using var layout = new Layout();
        var executable = layout.Touch("service", ServiceWire.Executable);
        var config = Path.Combine(layout.Root, "explicit");
        var result = TrayOptions.Parse(
            ["--service-path", executable, "--port", "31991", "--config-dir", config],
            name => name == ServiceWire.PortEnvironment ? "31992" : Path.Combine(layout.Root, "environment"),
            layout.Tray);
        Assert.Equal(31991, result.Port);
        Assert.Equal(config, result.ConfigDirectory);
        Assert.False(Directory.Exists(config));
    }

    [Fact]
    public void Environment_port_and_configuration_are_preserved()
    {
        using var layout = new Layout();
        layout.Touch("service", ServiceWire.Executable);
        var config = Path.Combine(layout.Root, "environment");
        var result = TrayOptions.Parse([], name => name switch
        {
            ServiceWire.PortEnvironment => "31992",
            ServiceWire.ConfigEnvironment => config,
            _ => null
        }, layout.Tray);
        Assert.Equal(31992, result.Port);
        Assert.Equal(config, result.ConfigDirectory);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("https://example.invalid")]
    [InlineData("")]
    public void Invalid_ports_fail_clearly(string value) =>
        Assert.Throws<ArgumentException>(() => TrayOptions.ParsePort(value));

    [Fact]
    public void Unknown_arguments_and_missing_explicit_service_fail()
    {
        using var layout = new Layout();
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse(["--url", "http://example.invalid"], _ => null, layout.Tray));
        Assert.Throws<ArgumentException>(() => TrayOptions.Parse(
            ["--service-path", Path.Combine(layout.Root, "missing.exe")], _ => null, layout.Tray));
    }

    [Fact]
    public void Legacy_child_layout_is_supported_only_when_the_executable_exists()
    {
        using var layout = new Layout();
        var executable = layout.Touch("tray", "service", ServiceWire.Executable);
        Assert.Equal(executable, TrayOptions.Parse([], _ => null, layout.Tray).ServicePath);
    }

    [Fact]
    public void Source_fallback_uses_matching_prebuilt_apphost_not_dotnet_run()
    {
        using var layout = new Layout();
        layout.Touch("src", "Hermes.Tray", "Hermes.Tray.csproj");
        var sourceBase = Path.Combine(layout.Root, "src", "Hermes.Tray", "bin", "Debug", "net9.0-windows");
        Directory.CreateDirectory(sourceBase);
        var executable = layout.Touch("src", "Hermes.Service", "bin", "Debug", "net9.0", ServiceWire.Executable);
        Assert.Equal(executable, TrayOptions.Parse([], _ => null, sourceBase).ServicePath);
    }
}

internal sealed class Layout : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), $"hermes-tray-test-{Guid.NewGuid():N}");
    public string Tray => Path.Combine(Root, "tray");
    public Layout() => Directory.CreateDirectory(Tray);

    public string Touch(params string[] components)
    {
        var path = Path.Combine([Root, .. components]);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Root);
        File.WriteAllText(path, "");
        return path;
    }

    public void Dispose() => Directory.Delete(Root, recursive: true);
}
