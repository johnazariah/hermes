using System.Collections.Immutable;
using Hermes.Setup;
using Xunit;

namespace Hermes.Setup.Tests;

public sealed class PackageAuthoringSnapshotTests
{
    [Fact]
    public void ProvenAbsentOptionalTableIsEmpty()
    {
        var snapshot = Snapshot();
        Assert.False(snapshot.HasTable("Shortcut"));
        Assert.Empty(snapshot.OptionalTable("Shortcut"));
    }

    [Fact]
    public void PresentButUnreadTableIsNotAbsence()
    {
        var snapshot = Snapshot() with
        {
            TableNames = ImmutableHashSet.Create(StringComparer.Ordinal, "Shortcut")
        };
        var error = Assert.Throws<SetupIoException>(() =>
            snapshot.OptionalTable("Shortcut"));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
    }

    [Fact]
    public void MissingRequiredTableFailsExplicitly()
    {
        var error = Assert.Throws<SetupIoException>(() =>
            Snapshot().RequireTable("InstallExecuteSequence"));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
    }

    [Fact]
    public void SuccessfullyReadEmptyTableRemainsKnownPresent()
    {
        var snapshot = WithTable("RemoveRegistry", []);
        Assert.True(snapshot.HasTable("RemoveRegistry"));
        Assert.Empty(snapshot.RequireTable("RemoveRegistry"));
        Assert.Empty(snapshot.OptionalTable("RemoveRegistry"));
    }

    [Fact]
    public void SuccessfullyReadRowsAreReturnedWithoutInventedInterpretation()
    {
        ImmutableArray<ImmutableArray<string>> rows =
            [["HermesTransactionFence", "", "1501"]];
        var snapshot = WithTable("InstallExecuteSequence", rows);
        Assert.Equal(rows, snapshot.RequireTable("InstallExecuteSequence"));
    }

    [Fact]
    public void MissingGuardBinaryFailsExplicitly()
    {
        var error = Assert.Throws<SetupIoException>(() =>
            Snapshot().RequireBinary("HermesInstallerGuard"));
        Assert.Equal(SetupCode.MediaInvalid, error.Code);
    }

    [Fact]
    public void EmptyGuardBinaryIsNotEvidence()
    {
        var snapshot = Snapshot() with
        {
            Binaries = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                .Add("HermesInstallerGuard", [])
        };
        Assert.Throws<SetupIoException>(() =>
            snapshot.RequireBinary("HermesInstallerGuard"));
    }

    [Fact]
    public void GuardBinaryBytesAreReturnedExactly()
    {
        ImmutableArray<byte> bytes = [0x4d, 0x5a, 0x01];
        var snapshot = Snapshot() with
        {
            Binaries = ImmutableDictionary<string, ImmutableArray<byte>>.Empty
                .Add("HermesInstallerGuard", bytes)
        };
        Assert.Equal(bytes, snapshot.RequireBinary("HermesInstallerGuard"));
    }

    [Fact]
    public void ExtendedCustomActionFlagsAreNotOmittedFromTheQuery()
    {
        ImmutableArray<string> requested = ["Action", "Type", "Source", "Target"];
        ImmutableArray<ImmutableArray<string>> definitions =
            [["CustomAction", "5", "ExtendedType", "5378"]];
        Assert.Equal<string>(requested.Add("ExtendedType"),
            PackageAuthoringQueries.ActualColumns(
                "CustomAction", requested, definitions));
    }

    [Fact]
    public void StandardCustomActionSchemaDoesNotInventAnExtendedColumn()
    {
        ImmutableArray<string> requested = ["Action", "Type", "Source", "Target"];
        Assert.Equal<string>(requested, PackageAuthoringQueries.ActualColumns(
            "CustomAction", requested, []));
    }

    [Fact]
    public void OtherTablesDoNotAcquireCustomActionColumns()
    {
        ImmutableArray<string> requested = ["Action", "Condition", "Sequence"];
        ImmutableArray<ImmutableArray<string>> definitions =
            [["CustomAction", "5", "ExtendedType", "5378"]];
        Assert.Equal(requested, PackageAuthoringQueries.ActualColumns(
            "InstallExecuteSequence", requested, definitions));
    }

    private static PackageAuthoringSnapshot WithTable(
        string name, ImmutableArray<ImmutableArray<string>> rows) =>
        Snapshot() with
        {
            TableNames = ImmutableHashSet.Create(StringComparer.Ordinal, name),
            Tables = ImmutableDictionary<
                string, ImmutableArray<ImmutableArray<string>>>.Empty.Add(name, rows)
        };

    private static PackageAuthoringSnapshot Snapshot() =>
        new(
            new("", "", 0,
                ImmutableDictionary<string,
                    ImmutableArray<ImmutableArray<string>>>.Empty,
                ImmutableDictionary<string, uint>.Empty, []),
            ImmutableHashSet.Create<string>(StringComparer.Ordinal),
            [],
            ImmutableDictionary<string,
                ImmutableArray<ImmutableArray<string>>>.Empty,
            ImmutableDictionary<string, ImmutableArray<byte>>.Empty);
}
