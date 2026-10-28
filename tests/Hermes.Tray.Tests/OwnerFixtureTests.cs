using System.Text.Json;
using Hermes.Tray.Smoke;
using Xunit;

namespace Hermes.Tray.Tests;

public sealed class OwnerFixtureTests
{
    [Fact]
    public void Creation_publishes_only_a_new_attested_fixture()
    {
        using var rig = new OwnerFixtureRig();
        var receipt = rig.Store.Create(rig.Archive);
        Assert.Equal(receipt, rig.Store.Admit());
        Assert.Equal(receipt, rig.Store.Verify());
        Assert.Equal(3, Directory.GetFileSystemEntries(rig.Owner).Length);
        Assert.Contains("accounts: []", File.ReadAllText(Path.Combine(rig.Owner, "config.yaml")));
    }

    [Fact]
    public void Creation_refuses_preexisting_owner_state_without_modifying_it()
    {
        using var rig = new OwnerFixtureRig();
        Directory.CreateDirectory(rig.Owner);
        var config = Path.Combine(rig.Owner, "config.yaml");
        File.WriteAllText(config, "synthetic existing owner sentinel");
        Assert.Throws<InvalidOperationException>(() => rig.Store.Create(rig.Archive));
        Assert.Equal("synthetic existing owner sentinel", File.ReadAllText(config));
        Assert.False(File.Exists(rig.Receipt));
    }

    [Fact]
    public void Atomic_publication_does_not_adopt_a_directory_created_during_preparation()
    {
        using var rig = new OwnerFixtureRig(new ConflictingOwnerFixturePublisher());
        Assert.Throws<IOException>(() => rig.Store.Create(rig.Archive));
        Assert.Equal("synthetic concurrent owner", File.ReadAllText(Path.Combine(rig.Owner, "sentinel")));
        Assert.False(File.Exists(Path.Combine(rig.Owner, "config.yaml")));
    }

    [Fact]
    public void No_receipt_accepts_only_absent_owner_state()
    {
        using var rig = new OwnerFixtureRig();
        Assert.Null(rig.WithReceipt(null).Admit());
    }

    [Fact]
    public void No_receipt_cannot_adopt_even_a_valid_existing_fixture()
    {
        using var rig = new OwnerFixtureRig();
        _ = rig.Store.Create(rig.Archive);
        Assert.Throws<InvalidOperationException>(() => rig.WithReceipt(null).Admit());
    }

    [Theory]
    [InlineData("config.yaml")]
    [InlineData("synthetic-settings.txt")]
    public void Changed_fixture_bytes_fail_admission(string name)
    {
        using var rig = new OwnerFixtureRig();
        _ = rig.Store.Create(rig.Archive);
        File.AppendAllText(Path.Combine(rig.Owner, name), "\nsynthetic changed bytes");
        Assert.Throws<InvalidDataException>(() => rig.Store.Verify());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unexpected_files_or_directories_are_not_adopted(bool directory)
    {
        using var rig = new OwnerFixtureRig();
        _ = rig.Store.Create(rig.Archive);
        var unexpected = Path.Combine(rig.Owner, "unexpected");
        if (directory)
            Directory.CreateDirectory(unexpected);
        else
            File.WriteAllText(unexpected, "synthetic unexpected state");
        Assert.Throws<InvalidDataException>(() => rig.Store.Verify());
    }

    [Fact]
    public void Missing_marker_invalidates_an_otherwise_unchanged_fixture()
    {
        using var rig = new OwnerFixtureRig();
        _ = rig.Store.Create(rig.Archive);
        File.Delete(Path.Combine(rig.Owner, OwnerFixtureStore.MarkerName));
        Assert.Throws<InvalidDataException>(() => rig.Store.Verify());
    }

    public enum WrongJob { Run, Attempt, Job }

    [Theory]
    [InlineData(WrongJob.Run)]
    [InlineData(WrongJob.Attempt)]
    [InlineData(WrongJob.Job)]
    public void Receipt_is_bound_to_the_exact_github_job(WrongJob wrong)
    {
        using var rig = new OwnerFixtureRig();
        _ = rig.Store.Create(rig.Archive);
        var job = wrong switch
        {
            WrongJob.Run => rig.Job with { RunId = "other-run" },
            WrongJob.Attempt => rig.Job with { Attempt = "other-attempt" },
            _ => rig.Job with { Job = "other-job" }
        };
        Assert.Throws<InvalidDataException>(() => rig.WithJob(job).Verify());
    }

    [Fact]
    public void Malformed_receipt_cannot_bless_existing_owner_state()
    {
        using var rig = new OwnerFixtureRig();
        _ = rig.Store.Create(rig.Archive);
        File.WriteAllText(rig.Receipt, "{");
        Assert.Throws<JsonException>(() => rig.Store.Verify());
    }

    [Fact]
    public void Receipt_and_in_directory_marker_must_match_exactly()
    {
        using var rig = new OwnerFixtureRig();
        var receipt = rig.Store.Create(rig.Archive);
        ProofFiles.Write(rig.Receipt, receipt with { FixtureId = Guid.NewGuid() });
        Assert.Throws<InvalidDataException>(() => rig.Store.Verify());
    }

    [Theory]
    [InlineData(FileAttributes.ReparsePoint)]
    [InlineData(FileAttributes.Directory)]
    public void Redirected_or_non_file_entries_are_rejected(FileAttributes attributes) =>
        Assert.Throws<InvalidDataException>(() => OwnerFixtureStore.RequireRegular(attributes));
}

internal sealed class OwnerFixtureRig : IDisposable
{
    private readonly Layout _layout = new();
    private readonly IOwnerFixturePublisher _publisher;
    public FixtureJob Job { get; } = new("synthetic-run", "1", "installer");
    public string Owner { get; }
    public string Receipt { get; }
    public string Archive { get; }
    public OwnerFixtureStore Store { get; }

    public OwnerFixtureRig(IOwnerFixturePublisher? publisher = null)
    {
        _publisher = publisher ?? new WindowsOwnerFixturePublisher();
        var profile = Path.Combine(_layout.Root, "profile");
        Directory.CreateDirectory(profile);
        Owner = Path.Combine(profile, "hermes");
        Receipt = Path.Combine(_layout.Root, "owner-fixture.json");
        Archive = Path.Combine(_layout.Root, "synthetic-archive");
        Directory.CreateDirectory(Archive);
        Store = new(Owner, Receipt, Job, _publisher);
    }

    public OwnerFixtureStore WithReceipt(string? receipt) => new(Owner, receipt, Job, _publisher);
    public OwnerFixtureStore WithJob(FixtureJob job) => new(Owner, Receipt, job, _publisher);
    public void Dispose() => _layout.Dispose();
}

internal sealed class ConflictingOwnerFixturePublisher : IOwnerFixturePublisher
{
    private readonly WindowsOwnerFixturePublisher _publisher = new();
    public void CreateStage(string path) => _publisher.CreateStage(path);

    public void Publish(string staging, string destination)
    {
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "sentinel"), "synthetic concurrent owner");
        _publisher.Publish(staging, destination);
    }
}
