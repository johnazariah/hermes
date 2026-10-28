using System.ComponentModel;
using System.Globalization;
using System.Security;

namespace Hermes.Installer.Control;

internal enum MsiContextRead
{
    NotObserved = 0,
    Observed = 1,
    Failed = 2,
    Absent = 3
}

// Caller-side measurements only. These are not installer privilege state,
// native-writer provenance, owner admission, private fences, or completion proof.
// Observed means present for Thread/Linked. Failed is never absence.
internal sealed record MsiCallContextObservation(
    MsiContextRead Process,
    int Elevated,
    int ElevationType,
    int Integrity,
    int Admin,
    MsiContextRead Thread,
    MsiContextRead Linked,
    int SameOwner,
    int LinkedElevated)
{
    internal static MsiCallContextObservation Missing { get; } =
        new(MsiContextRead.NotObserved, -1, -1, -1, -1,
            MsiContextRead.NotObserved, MsiContextRead.NotObserved, -1, -1);

    private static MsiCallContextObservation Failed { get; } =
        new(MsiContextRead.Failed, -1, -1, -1, -1,
            MsiContextRead.Failed, MsiContextRead.Failed, -1, -1);

    internal static MsiCallContextObservation Read(IMsiCallContextTokens tokens)
    {
        var value = ReadProcess(tokens) with { Thread = ReadThread(tokens) };
        value.RequireValid();
        return value;
    }

    private static MsiCallContextObservation ReadProcess(IMsiCallContextTokens tokens)
    {
        try
        {
            using var process = tokens.OpenProcess();
            return ReadLinked(process, ReadPrimary(process));
        }
        catch (Exception error) when (Expected(error))
        {
            return Failed;
        }
    }

    private static MsiCallContextObservation ReadPrimary(IMsiCallContextToken process)
    {
        try
        {
            var value = process.ReadPrimary();
            return Failed with
            {
                Process = MsiContextRead.Observed,
                Elevated = value.Elevated,
                ElevationType = value.ElevationType,
                Integrity = value.Integrity,
                Admin = value.Admin
            };
        }
        catch (Exception error) when (Expected(error)) { return Failed; }
    }

    private static MsiCallContextObservation ReadLinked(
        IMsiCallContextToken process, MsiCallContextObservation primary)
    {
        try
        {
            var value = process.ReadLinked();
            return primary with
            {
                Linked = value.State,
                SameOwner = value.SameOwner,
                LinkedElevated = value.Elevated
            };
        }
        catch (Exception error) when (Expected(error)) { return primary; }
    }

    private static MsiContextRead ReadThread(IMsiCallContextTokens tokens)
    {
        try { return tokens.ReadThreadToken(); }
        catch (Exception error) when (Expected(error)) { return MsiContextRead.Failed; }
    }

    private static bool Expected(Exception error) =>
        error is Win32Exception or UnauthorizedAccessException or SecurityException;

    internal void RequireValid()
    {
        var primary = Process switch
        {
            MsiContextRead.Observed => Elevated is 0 or 1 && ElevationType is >= 1 and <= 3 &&
                Integrity is >= 0 and <= 65535 && Admin is 0 or 1,
            MsiContextRead.NotObserved or MsiContextRead.Failed =>
                Elevated == -1 && ElevationType == -1 && Integrity == -1 && Admin == -1,
            _ => false
        };
        var linked = Linked switch
        {
            MsiContextRead.Observed => SameOwner is 0 or 1 && LinkedElevated is 0 or 1,
            MsiContextRead.NotObserved or MsiContextRead.Failed or MsiContextRead.Absent =>
                SameOwner == -1 && LinkedElevated == -1,
            _ => false
        };
        if (!primary || !linked || !ValidPresence())
            throw new ArgumentOutOfRangeException(nameof(Process));
    }

    private bool ValidPresence() =>
        (int)Thread is >= 0 and <= 3 &&
        (Process == MsiContextRead.NotObserved
            ? Thread == MsiContextRead.NotObserved && Linked == MsiContextRead.NotObserved
            : Thread != MsiContextRead.NotObserved && Linked != MsiContextRead.NotObserved);

    internal string ToDiagnosticSuffix()
    {
        RequireValid();
        return string.Create(CultureInfo.InvariantCulture,
            $" context=1 process={(int)Process} elevated={Elevated} elevationType={ElevationType} integrity={Integrity} admin={Admin} thread={(int)Thread} linked={(int)Linked} sameOwner={SameOwner} linkedElevated={LinkedElevated}");
    }
}

// Deliberately limited to this one caller-context observation. No token handles,
// SIDs, names, privileges, or exception text enter its immutable output.
internal interface IMsiCallContextTokens
{
    IMsiCallContextToken OpenProcess();
    MsiContextRead ReadThreadToken();
}

internal interface IMsiCallContextToken : IDisposable
{
    (int Elevated, int ElevationType, int Integrity, int Admin) ReadPrimary();
    (MsiContextRead State, int SameOwner, int Elevated) ReadLinked();
}
