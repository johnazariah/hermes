using System.Diagnostics;
using System.Runtime.Versioning;
using Hermes.Servicing.Acceptance;
using Hermes.Servicing.Acceptance.Model;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
[Collection(nameof(RestrictedOwnerNativeCollection))]
public sealed class ReadOnlyWriterObservationTests
{
    private static readonly Stamp WriterStamp =
        new(41, 1000, @"C:\fixture\writer.exe", new string('a', 64));

    private static ProcessKey Writer => new(41, 1000);

    private static NativeReporter Reporter =>
        new(NativeWriterProof.ProviderId, 41, 2000);

    [Fact]
    public void Read_only_noninherited_pin_preserves_the_complete_identity_and_binding()
    {
        var query = new FakeQuery(new());
        var observation = Observe(query);
        Assert.Equal(new OpenRequest(0x00100410, 0, 41), query.Request);
        Assert.Equal(WriterStamp, observation.Identity);
        Assert.Equal(12, observation.Binding.Cursor);
        Assert.Equal(Reporter, observation.Binding.Reporter);
        Assert.Equal(Writer, observation.Binding.Process);
        Assert.True(observation.Binding.ObservedFileTime >= Reporter.EventFileTime);
        Assert.Equal(new[] { Step.Open, Step.Identity, Step.Wait }, query.Calls);
        Assert.True(query.Handle.IsClosed);
    }

    [Theory]
    [InlineData(0, false, 5, 5)]
    [InlineData(-1, false, 5, 5)]
    [InlineData(123, true, 5, 5)]
    [InlineData(0, false, 0, 13)]
    [InlineData(0, false, -1, 13)]
    public void Invalid_or_closed_open_never_reads_identity_and_disposes_the_handle(
        int rawHandle, bool closed, int error, int expectedError)
    {
        var query = new FakeQuery(new()
        {
            HandleValue = rawHandle,
            ClosedOnOpen = closed,
            Error = error
        });
        RequireReadFailure(() => Observe(query), expectedError);
        Assert.Equal(new[] { Step.Open }, query.Calls);
        Assert.True(query.Handle.IsClosed);
    }

    [Theory]
    [InlineData(uint.MaxValue, 5, 5)]
    [InlineData(uint.MaxValue, 0, 13)]
    [InlineData(0x80u, 5, 13)]
    [InlineData(1u, 5, 13)]
    public void Failed_or_unexpected_wait_cannot_become_live_evidence(
        uint status, int error, int expectedError)
    {
        var query = new FakeQuery(new() { WaitStatus = status, Error = error });
        RequireReadFailure(() => Observe(query), expectedError);
        Assert.Equal(new[] { Step.Open, Step.Identity, Step.Wait }, query.Calls);
        Assert.True(query.Handle.IsClosed);
    }

    [Fact]
    public void Exit_after_identity_retains_the_existing_observation_lost_failure()
    {
        var query = new FakeQuery(new() { WaitStatus = 0 });
        var failure = Assert.Throws<EvidenceGap>(() => Observe(query));
        Assert.Equal("DiagnosticWriterObservationLost", failure.Code);
        Assert.True(query.Handle.IsClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Read_denial_IO_failure_and_unexpected_wait_fault_propagate_and_close(int stage)
    {
        Exception fault = stage switch
        {
            0 => new UnauthorizedAccessException(),
            1 => new IOException(),
            _ => new InvalidOperationException()
        };
        var scenario = stage == 2
            ? new Scenario { WaitFailure = fault }
            : new Scenario { ReadFailure = fault };
        var query = new FakeQuery(scenario);
        Assert.Same(fault, Assert.ThrowsAny<Exception>(() => Observe(query)));
        Assert.True(query.Handle.IsClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Observed_or_claimed_PID_and_creation_mismatches_still_fail_and_close(int mutation)
    {
        var actual = mutation switch
        {
            0 => WriterStamp with { ProcessId = 42 },
            1 => WriterStamp with { StartUtcFileTime = 1001 },
            _ => WriterStamp
        };
        var claimed = mutation switch
        {
            2 => Writer with { ProcessId = 42 },
            3 => Writer with { CreationFileTime = 1001 },
            _ => Writer
        };
        var query = new FakeQuery(new() { Identity = actual });
        var failure = Assert.Throws<EvidenceGap>(() => Observe(query, claimed: claimed));
        Assert.Equal(mutation is 0 or 2
            ? "NativeReporterPidMismatch" : "DiagnosticWriterInstanceMismatch", failure.Code);
        Assert.True(query.Handle.IsClosed);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(uint.MaxValue)]
    public void Unsupported_reporter_PID_is_rejected_before_any_open(uint processId)
    {
        var query = new FakeQuery(new());
        using var unacquired = query.Handle;
        var reporter = Reporter with { ProcessId = processId };
        var failure = Assert.Throws<EvidenceGap>(() => Observe(query, reporter: reporter));
        Assert.Equal("UnsupportedNativeReporter", failure.Code);
        Assert.Empty(query.Calls);
        Assert.False(unacquired.IsClosed);
    }

    [Fact]
    public void A_matching_new_claim_cannot_replace_a_previously_observed_writer_PID()
    {
        var tracker = new NativeWriterTracker();
        var first = new FakeQuery(new());
        _ = Observe(first, tracker);
        var second = new FakeQuery(new()
        {
            Identity = WriterStamp with { StartUtcFileTime = 1001 }
        });
        var failure = Assert.Throws<EvidenceGap>(() => Observe(
            second, tracker, claimed: Writer with { CreationFileTime = 1001 }));
        Assert.Equal("WriterPidReusedWithinSubscription", failure.Code);
        Assert.True(first.Handle.IsClosed);
        Assert.True(second.Handle.IsClosed);
    }

    [Fact]
    public void Windows_query_preserves_live_loaded_image_creation_time_and_hash()
    {
        using var process = Process.GetCurrentProcess();
        var expected = Files.Identity(process);
        var claimed = new ProcessKey(
            checked((uint)expected.ProcessId), expected.StartUtcFileTime);
        var reporter = new NativeReporter(NativeWriterProof.ProviderId,
            claimed.ProcessId, DateTime.UtcNow.ToFileTimeUtc());
        var observation = ReadOnlyWriterObservation.Observe(
            new WindowsWriterProcessQuery(), new NativeWriterTracker(),
            12, reporter, claimed);
        Assert.Equal(expected, observation.Identity);
        Assert.Equal(claimed, observation.Binding.Process);
        Assert.Equal(reporter, observation.Binding.Reporter);
    }

    private static (Stamp Identity, NativeWriterBinding Binding) Observe(
        FakeQuery query, NativeWriterTracker? tracker = null,
        NativeReporter? reporter = null, ProcessKey? claimed = null) =>
        ReadOnlyWriterObservation.Observe(query,
            tracker ?? new NativeWriterTracker(), 12,
            reporter ?? Reporter, claimed ?? Writer);

    private static void RequireReadFailure(Action action, int error) =>
        Assert.Equal($"WriterProcessReadFailed:{error}",
            Assert.Throws<EvidenceGap>(action).Code);

    private enum Step { Open, Identity, Wait }

    private sealed record OpenRequest(
        uint Access, int InheritHandle, uint ProcessId);

    private sealed record Scenario
    {
        internal int HandleValue { get; init; } = 123;
        internal bool ClosedOnOpen { get; init; }
        internal Stamp Identity { get; init; } = WriterStamp;
        internal uint WaitStatus { get; init; } = 258;
        internal int Error { get; init; } = 5;
        internal Exception? ReadFailure { get; init; }
        internal Exception? WaitFailure { get; init; }
    }

    private sealed class FakeQuery(Scenario scenario) : IWriterProcessQuery
    {
        private readonly List<Step> _calls = [];
        private OpenRequest? _request;

        internal SafeProcessHandle Handle { get; } =
            new((nint)scenario.HandleValue, ownsHandle: false);

        internal IReadOnlyList<Step> Calls => _calls;
        internal OpenRequest? Request => _request;

        public (SafeProcessHandle Handle, int Error) Open(
            uint access, int inheritHandle, uint processId)
        {
            _calls.Add(Step.Open);
            _request = new(access, inheritHandle, processId);
            if (scenario.ClosedOnOpen)
                Handle.Dispose();
            return (Handle, scenario.Error);
        }

        public Stamp Identity(int processId)
        {
            _calls.Add(Step.Identity);
            Assert.Equal(41, processId);
            Assert.False(Handle.IsClosed);
            if (scenario.ReadFailure is { } failure)
                throw failure;
            return scenario.Identity;
        }

        public (uint Status, int Error) Wait(SafeProcessHandle handle)
        {
            _calls.Add(Step.Wait);
            Assert.Same(Handle, handle);
            Assert.False(handle.IsClosed);
            if (scenario.WaitFailure is { } failure)
                throw failure;
            return (scenario.WaitStatus, scenario.Error);
        }
    }
}
