using System.Globalization;
using System.Text.Json;
using Hermes.Installer.Control;
using Hermes.Servicing;
using Hermes.Setup;

namespace Hermes.Servicing.Acceptance;

internal static class SetupOutput
{
    private const string EvidencePrefix = "HERMES_SETUP_EVIDENCE";
    private const string MsiPrefix = "HERMES_SETUP_MSI";
    private const string NativePrefix = "HERMES_NATIVE_PREFLIGHT";
    private const string MsiFields = "v action error readFailed preflight fence";
    private const string ContextFields =
        "context process elevated elevationType integrity admin thread linked sameOwner linkedElevated";

    internal static SetupChildResult Parse(
        string stdout, string stderr, int exitCode, SetupVerb operation)
    {
        Require(exitCode is 0 or 1603 or 1618 or 3010, SetupChildFault.UnexpectedExit);
        var newline = stdout.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var report = Records(stdout, newline, SetupChildFault.InvalidReport) switch
        {
            [var line] => Report(line, operation),
            _ => throw new SetupChildException(SetupChildFault.InvalidReport)
        };
        var diagnostics = Records(stderr, newline, SetupChildFault.InvalidDiagnostics);
        var code = Code(diagnostics[0]);
        Require(Status(report, code, exitCode), SetupChildFault.ExitMismatch);
        Diagnostics(report, code, diagnostics[1..]);
        return new(exitCode, report);
    }

    private static string[] Records(string text, string newline, SetupChildFault fault)
    {
        Require(text.Length > 0 && text.EndsWith('\n'), SetupChildFault.PrematureEof);
        var records = text.Split(newline, StringSplitOptions.None);
        Require(records[^1] == "" && records[..^1].All(line =>
            line.Length > 0 && !line.Contains('\r') && !line.Contains('\n')), fault);
        return records[..^1];
    }

    private static SetupReport Report(string line, SetupVerb operation)
    {
        try
        {
            var report = EvidenceJson.Decode<SetupReport>(line);
            // Compare the actual wire record, including schemaVersion, not its getter.
            Require(line == report.ToJson() && report.Operation == operation &&
                Enum.IsDefined(report.Operation) && Enum.IsDefined(report.Outcome) &&
                report.MsiExitCode is null or >= 0, SetupChildFault.InvalidReport);
            return report;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        {
            throw new SetupChildException(SetupChildFault.InvalidReport, error);
        }
    }

    private static SetupCode Code(string line)
    {
        var code = Enum.GetValues<SetupCode>()
            .FirstOrDefault(value => Summary(value) == line);
        Require(Summary(code) == line);
        return code;
    }

    private static string Summary(SetupCode code) => $"Hermes Setup: {code}.";

    private static bool Status(SetupReport report, SetupCode code, int exit) => report switch
    {
        { Outcome: SetupOutcome.Completed, Ready: true, MsiExitCode: 0 } =>
            exit == 0 && code == SetupCode.None,
        { Outcome: SetupOutcome.RolledBack, Ready: true, MsiExitCode: not null }
            when report.MsiExitCode is not (0 or 1605 or 1618 or 3010 or 1641) =>
            exit == 1603 && code == SetupCode.MsiCallFailed,
        { Outcome: SetupOutcome.RebootRequired, Ready: false, MsiExitCode: not null }
            when report.MsiExitCode is not (1605 or 1618) =>
            exit == 3010 && code == SetupCode.RebootRequired,
        { Outcome: SetupOutcome.Refused, Ready: false, MsiExitCode: null } =>
            FailureStatus(code, exit) && code != SetupCode.TransactionFenceMissing,
        { Outcome: SetupOutcome.RecoveryRequired, Ready: false } =>
            FailureStatus(code, exit) && RecoveryStatus(report.MsiExitCode, code),
        _ => false
    };

    private static bool RecoveryStatus(int? msiExit, SetupCode code) =>
        (msiExit, code) switch
        {
            (null, SetupCode.TransactionFenceMissing) => false,
            (null, _) => true,
            // Incomplete API receipts precede native-code handling in the producer.
            (_, SetupCode.MsiDidNotComplete) => true,
            (1618, SetupCode.Busy) => true,
            (1605, SetupCode.MsiCallFailed) => true,
            (1605 or 1618 or 3010 or 1641, _) => false,
            (_, SetupCode.Busy) => false,
            _ => true
        };

    private static bool FailureStatus(SetupCode code, int exit) =>
        code is not (SetupCode.None or SetupCode.InvalidArguments or SetupCode.RebootRequired) &&
        exit == (code == SetupCode.Busy ? 1618 : 1603);

    private static void Diagnostics(SetupReport report, SetupCode code, string[] lines)
    {
        try { ValidateDiagnostics(report, code, lines); }
        catch (Exception error) when (
            error is FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new SetupChildException(SetupChildFault.InvalidDiagnostics, error);
        }
    }

    private static void ValidateDiagnostics(
        SetupReport report, SetupCode code, string[] lines)
    {
        if (lines.Length == 0)
            return;
        if (code == SetupCode.TransactionFenceMissing)
        {
            FenceDiagnostics(lines);
            return;
        }
        Require(lines.Length == 1 && report.Outcome == SetupOutcome.Refused &&
            code is SetupCode.InvalidEvidence or SetupCode.Unavailable or SetupCode.MediaInvalid);
        var evidence = Refusal(lines[0]);
        Require(code != SetupCode.MediaInvalid || evidence.IsCandidateMedia);
    }

    private static void FenceDiagnostics(string[] lines)
    {
        switch (lines)
        {
            case [var msi] when msi.StartsWith(MsiPrefix, StringComparison.Ordinal):
                _ = Msi(msi);
                return;
            case [var native] when native.StartsWith(NativePrefix, StringComparison.Ordinal):
                Native(native);
                return;
            case [var msi, var native]:
                Require(!Msi(msi).OwnPreflight);
                Native(native);
                return;
            default:
                throw new SetupChildException(SetupChildFault.InvalidDiagnostics);
        }
    }

    private static SetupRefusalEvidence Refusal(string line)
    {
        var n = Fields(line, EvidencePrefix, "v stage cause fault errorKind error");
        var result = new SetupRefusalEvidence(
            (SetupEvidenceStage)checked((int)n[1]),
            (SetupEvidenceCause)checked((int)n[2]),
            (ObservationFault)checked((int)n[3]),
            (StartupEvidenceErrorKind)checked((int)n[4]), n[5]);
        Require(line.Length <= SetupRefusalEvidence.MaximumDiagnosticLength &&
            line == result.ToDiagnosticLine() && ValidRefusal(result));
        return result;
    }

    private static bool ValidRefusal(SetupRefusalEvidence value) =>
        value.Stage != SetupEvidenceStage.Unspecified && Enum.IsDefined(value.Stage) &&
        Enum.IsDefined(value.Cause) && Enum.IsDefined(value.Fault) &&
        Enum.IsDefined(value.ErrorKind) &&
        value.ErrorCode is >= int.MinValue and <= uint.MaxValue &&
        ((int)value.Cause < 10 ||
            (value.Fault == ObservationFault.None &&
             value.ErrorKind == StartupEvidenceErrorKind.None && value.ErrorCode == 0 &&
             value.Stage == ((int)value.Cause <= 18
                 ? SetupEvidenceStage.Entry : SetupEvidenceStage.CandidatePackage)));

    private static MsiFailureEvidence Msi(string line)
    {
        var context = line.Contains(" context=", StringComparison.Ordinal);
        var n = Fields(line, MsiPrefix,
            context ? MsiFields + " " + ContextFields : MsiFields)
            .Select(value => checked((int)value)).ToArray();
        Require(n[0] == (context ? 2 : 1) && KnownAction(n[1]) &&
            n[2] is >= 0 and <= 65535 &&
            n.Skip(3).Take(3).All(value => value is 0 or 1) && n[5] == 0);
        var result = new MsiFailureEvidence(n[1], n[2], n[3] == 1, n[4] == 1, false)
        {
            CallContext = context ? Context(n[6..]) : null
        };
        Require(line.Length <= 320 && line == result.ToDiagnosticLine());
        return result;
    }

    private static MsiCallContextObservation Context(int[] n)
    {
        Require(n[0] == 1);
        return new((MsiContextRead)n[1], n[2], n[3], n[4], n[5],
            (MsiContextRead)n[6], (MsiContextRead)n[7], n[8], n[9]);
    }

    private static bool KnownAction(int action) => action is
        0 or 25 or 50 or 100 or 700 or 800 or 900 or 1000 or 1300 or 1399 or 1400 or
        1500 or 1501 or 1502 or 1600 or 1800 or 2600 or 3200 or 3500 or 3600 or
        3700 or 3900 or 4000 or 4500 or 5000 or 6000 or 6100 or 6300 or 6400 or 6600;

    private static void Native(string line)
    {
        var n = Fields(line, NativePrefix, "v source observed point reason")
            .Select(value => checked((int)value)).ToArray();
        var value = NativePreflightEvidence.FromValues(n[3], n[4]);
        Require(line.Length <= 112 && line == value.ToDiagnosticLine());
    }

    private static long[] Fields(string line, string prefix, string names)
    {
        var fields = line.Split(' ');
        var keys = names.Split(' ');
        Require(fields.Length == keys.Length + 1 && fields[0] == prefix);
        return keys.Zip(fields.Skip(1), Field).ToArray();
    }

    private static long Field(string name, string field)
    {
        var prefix = name + "=";
        Require(field.StartsWith(prefix, StringComparison.Ordinal));
        var text = field[prefix.Length..];
        var value = long.Parse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        Require(text == value.ToString(CultureInfo.InvariantCulture));
        return value;
    }

    internal static void Require(
        bool condition, SetupChildFault fault = SetupChildFault.InvalidDiagnostics)
    {
        if (!condition)
            throw new SetupChildException(fault);
    }
}
