using System.Collections.Immutable;
using Hermes.Setup;

namespace Hermes.Setup.Tests;

internal static class SignatureFixture
{
    internal const string Name = "Signature";

    // Independent expected native observations, not derived from production schema.
    internal static ImmutableArray<ImmutableArray<string>> Definitions { get; } =
    [
        [Name, "1", "Signature", "11592"],
        [Name, "2", "FileName", "3583"],
        [Name, "3", "MinVersion", "7444"],
        [Name, "4", "MaxVersion", "7444"],
        [Name, "5", "MinSize", "4356"],
        [Name, "6", "MaxSize", "4356"],
        [Name, "7", "MinDate", "4356"],
        [Name, "8", "MaxDate", "4356"],
        [Name, "9", "Languages", "7679"]
    ];

    internal static ImmutableArray<string> Row { get; } =
        ["ExistingInstallDirectory", "foreign.exe", "", "", "", "", "", "", ""];

    internal static AuthoringFixture WithEmpty(AuthoringFixture fixture) =>
        fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                TableNames = fixture.Snapshot.TableNames.Add(Name),
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions.AddRange(Definitions),
                Tables = fixture.Snapshot.Tables.SetItem(Name, [])
            }
        };

    internal static AuthoringFixture WithDefinitions(
        AuthoringFixture fixture, ImmutableArray<ImmutableArray<string>> definitions) =>
        fixture with
        {
            Snapshot = fixture.Snapshot with
            {
                ColumnDefinitions = fixture.Snapshot.ColumnDefinitions
                    .Where(row => row[0] != Name).Concat(definitions).ToImmutableArray()
            }
        };
}
