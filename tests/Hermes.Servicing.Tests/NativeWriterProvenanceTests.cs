using Hermes.Servicing.Acceptance.Model;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class NativeWriterProvenanceTests
{
    private static readonly ProcessKey Writer = new(41, 1000);
    private static NativeReporter Reporter =>
        new(NativeWriterProof.ProviderId, Writer.ProcessId, 2000);

    [Fact]
    public void Native_reporter_claim_and_fresh_OS_identity_can_bind()
    {
        var binding = NativeWriterProof.Bind(12, Reporter, Writer, Writer, 3000);
        Assert.Equal(12, binding.Cursor);
        Assert.Equal(Reporter, binding.Reporter);
        Assert.Equal(Writer, binding.Process);
        Assert.Equal(3000, binding.ObservedFileTime);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("zero-pid")]
    [InlineData("wrong-provider")]
    [InlineData("wrong-native-pid")]
    [InlineData("unknown-creation")]
    [InlineData("wrong-creation")]
    [InlineData("before-creation")]
    [InlineData("future-event")]
    [InlineData("zero-cursor")]
    public void Missing_or_contradictory_provenance_cannot_bind(string mutation)
    {
        var reporter = mutation switch
        {
            "missing" => null,
            "zero-pid" => Reporter with { ProcessId = 0 },
            "wrong-provider" => Reporter with { ProviderId = Guid.Empty },
            "wrong-native-pid" => Reporter with { ProcessId = 99 },
            "before-creation" => Reporter with { EventFileTime = 999 },
            "future-event" => Reporter with { EventFileTime = 3001 },
            _ => Reporter
        };
        var observed = mutation switch
        {
            "unknown-creation" => Writer with { CreationFileTime = 0 },
            "wrong-creation" => Writer with { CreationFileTime = 1001 },
            _ => Writer
        };
        Assert.Throws<EvidenceGap>(() => NativeWriterProof.Bind(
            mutation == "zero-cursor" ? 0 : 12, reporter, Writer, observed, 3000));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Previously_seen_writer_does_not_bypass_native_binding(bool missing)
    {
        var tracker = new NativeWriterTracker();
        _ = tracker.Observe(12, Reporter, Writer, Writer, 3000);
        var reporter = missing ? null : Reporter with { ProcessId = 99 };
        Assert.Throws<EvidenceGap>(() =>
            tracker.Observe(13, reporter, Writer, Writer, 3000));
    }

    [Fact]
    public void Repeated_legitimate_writer_is_reverified_and_can_bind()
    {
        var tracker = new NativeWriterTracker();
        _ = tracker.Observe(12, Reporter, Writer, Writer, 3000);
        Assert.Equal(13, tracker.Observe(13, Reporter, Writer, Writer, 3000).Cursor);
    }

    [Fact]
    public void Reused_PID_cannot_be_substituted_even_with_a_matching_new_claim()
    {
        var tracker = new NativeWriterTracker();
        _ = tracker.Observe(12, Reporter, Writer, Writer, 3000);
        var replacement = Writer with { CreationFileTime = 4000 };
        Assert.Throws<EvidenceGap>(() => tracker.Observe(13,
            Reporter with { EventFileTime = 5000 }, replacement, replacement, 6000));
    }

    [Fact]
    public void Old_event_cannot_be_bound_to_a_reused_PID()
    {
        var replacement = Writer with { CreationFileTime = 4000 };
        Assert.Throws<EvidenceGap>(() =>
            NativeWriterProof.Bind(12, Reporter, replacement, replacement, 6000));
    }
}
