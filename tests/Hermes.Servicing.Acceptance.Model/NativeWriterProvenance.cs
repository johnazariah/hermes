namespace Hermes.Servicing.Acceptance.Model;

public sealed record NativeReporter(
    Guid ProviderId,
    uint ProcessId,
    long EventFileTime);

public sealed record NativeWriterBinding(
    long Cursor,
    NativeReporter Reporter,
    ProcessKey Process,
    long ObservedFileTime);

public static class NativeWriterProof
{
    public const string ProviderName = "Hermes.Servicing.Evidence.Native";
    public const string SchemaProfile = "manifest-application-msi-english-v1";
    public static Guid ProviderId { get; } =
        new("c760c1ab-37cb-4c3a-a245-3ea632fc9c5d");

    public static NativeWriterBinding Bind(long cursor, NativeReporter? reporter,
        ProcessKey claimed, ProcessKey observed, long observedFileTime)
    {
        var native = reporter ?? throw new EvidenceGap("MissingNativeReporter");
        Need(cursor > 0, "InvalidNativeBindingCursor");
        Need(native.ProviderId == ProviderId && native.ProcessId > 0 &&
            native.EventFileTime > 0, "UnsupportedNativeReporter");
        Need(claimed.ProcessId == native.ProcessId &&
            observed.ProcessId == native.ProcessId, "NativeReporterPidMismatch");
        Need(observed.CreationFileTime > 0 && claimed == observed,
            "DiagnosticWriterInstanceMismatch");
        Need(native.EventFileTime >= observed.CreationFileTime,
            "ReporterPredatesProcessCreation");
        Need(observedFileTime >= native.EventFileTime &&
            observedFileTime >= observed.CreationFileTime,
            "InvalidNativeObservationTime");
        return new(cursor, native, observed, observedFileTime);
    }

    public static NativeWriterBinding Require(LogFact fact)
    {
        var diagnostic = fact.Diagnostic ?? throw new EvidenceGap("MissingDiagnostic");
        var binding = fact.NativeWriter ?? throw new EvidenceGap("MissingNativeWriterBinding");
        Need(binding.Cursor == fact.Cursor, "NativeBindingCursorMismatch");
        return Bind(fact.Cursor, binding.Reporter, diagnostic.Writer,
            binding.Process, binding.ObservedFileTime);
    }

    private static void Need(bool condition, string code)
    {
        if (!condition)
            throw new EvidenceGap(code);
    }
}

public sealed class NativeWriterTracker
{
    private readonly Dictionary<uint, ProcessKey> _instances = [];

    public NativeWriterBinding Observe(long cursor, NativeReporter? reporter,
        ProcessKey claimed, ProcessKey observed, long observedFileTime)
    {
        var binding = NativeWriterProof.Bind(
            cursor, reporter, claimed, observed, observedFileTime);
        if (_instances.GetValueOrDefault(observed.ProcessId) is { } previous &&
            previous != observed)
            throw new EvidenceGap("WriterPidReusedWithinSubscription");
        _instances[observed.ProcessId] = observed;
        return binding;
    }
}
