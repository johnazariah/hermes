using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.Versioning;

namespace Hermes.Installer.Control;

// Values are the frozen source action positions, not an action-completion claim.
internal enum MsiFailureAction
{
    Unknown = 0,
    FindRelatedProducts = 25,
    AppSearch = 50,
    LaunchConditions = 100,
    ValidateProductID = 700,
    CostInitialize = 800,
    FileCost = 900,
    CostFinalize = 1000,
    ExecuteAction = 1300,
    HermesPreflight = 1399,
    InstallValidate = 1400,
    InstallInitialize = 1500,
    HermesTransactionFence = 1501,
    RemoveExistingProducts = 1502,
    ProcessComponents = 1600,
    UnpublishFeatures = 1800,
    RemoveRegistryValues = 2600,
    RemoveShortcuts = 3200,
    RemoveFiles = 3500,
    RemoveFolders = 3600,
    CreateFolders = 3700,
    InstallAdminPackage = 3900,
    InstallFiles = 4000,
    CreateShortcuts = 4500,
    WriteRegistryValues = 5000,
    RegisterUser = 6000,
    RegisterProduct = 6100,
    PublishFeatures = 6300,
    PublishProduct = 6400,
    InstallFinalize = 6600
}

internal sealed record MsiFailureObservation(
    MsiFailureAction Action,
    int? ErrorId,
    bool RecordReadFailed);

internal interface IMsiFailureRecords
{
    string ReadAction(uint record);
    int? ReadErrorId(uint record);
}

// Access is serialized by CallAuthority. No native record handle is retained.
internal sealed class MsiFailureTrace
{
    internal const uint ActionStartMessage = 0x08000000;
    internal const uint ErrorMessage = 0x01000000;
    internal const uint Filters = (1u << 8) | (1u << 1);

    private static readonly ImmutableDictionary<string, MsiFailureAction> Actions =
        Enum.GetValues<MsiFailureAction>().ToImmutableDictionary(
            value => value.ToString(), value => value, StringComparer.Ordinal);

    private MsiFailureObservation _value = new(MsiFailureAction.Unknown, null, false);

    internal MsiFailureObservation Snapshot => _value;

    internal void Observe(uint message, uint record, IMsiFailureRecords records)
    {
        if (_value.ErrorId is not null)
            return;
        try
        {
            _value = (message & 0xff000000) switch
            {
                ActionStartMessage => _value with { Action = Decode(records.ReadAction(record)) },
                ErrorMessage => WithError(records.ReadErrorId(record)),
                _ => _value
            };
        }
        catch (Win32Exception)
        {
            _value = _value with { RecordReadFailed = true };
        }
    }

    private MsiFailureObservation WithError(int? error) => error switch
    {
        null => _value,
        > 0 and <= ushort.MaxValue => _value with { ErrorId = error },
        _ => _value with { RecordReadFailed = true }
    };

    private static MsiFailureAction Decode(string name) =>
        name.Length <= 32
            ? Actions.GetValueOrDefault(name, MsiFailureAction.Unknown)
            : MsiFailureAction.Unknown;
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsMsiFailureRecords : IMsiFailureRecords
{
    internal static WindowsMsiFailureRecords Instance { get; } = new();

    public string ReadAction(uint record) => MsiNative.RecordField(record, 1);

    public int? ReadErrorId(uint record) => MsiNative.RecordNullableInteger(record, 1);
}
