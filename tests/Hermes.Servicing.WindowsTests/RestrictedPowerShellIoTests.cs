using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class RestrictedPowerShellIoTests
{
    [Fact]
    public void Actual_struct_layout_and_fixed_flags_match_the_extended_Windows_ABI()
    {
        var layout = WindowsRestrictedOwnerTokens.PowerShellProbeIoLayout;
        Assert.Equal((8, 104, 112), layout);
        AcceptancePowerShellProbeIoPolicy.RequireLayout(layout);
        Assert.Equal(0x0008000cu, AcceptancePowerShellProbeIoPolicy.CreationFlags);
        Assert.Equal(0x100u, AcceptancePowerShellProbeIoPolicy.StartupFlags);
        Assert.Equal(1, AcceptancePowerShellProbeIoPolicy.InheritHandles);
        Assert.Equal(0u, AcceptancePowerShellProbeIoPolicy.CreationFlags & 0x08000000u);
    }

    [Theory]
    [InlineData(4, 104, 112)]
    [InlineData(8, 103, 112)]
    [InlineData(8, 104, 111)]
    [InlineData(8, 0, 0)]
    public void Incorrect_struct_layout_is_refused(int pointer, int startup, int extended)
    {
        Assert.Throws<InvalidDataException>(() =>
            AcceptancePowerShellProbeIoPolicy.RequireLayout((pointer, startup, extended)));
    }

    [Fact]
    public void Exactly_three_distinct_nonpseudo_handles_are_accepted()
    {
        AcceptancePowerShellProbeIoPolicy.RequireHandles([4, 8, 12]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Invalid_stdio_handle_lists_are_refused(int variant)
    {
        nint[] handles = variant switch
        {
            0 => [0, 8, 12], 1 => [4, 0, 12], 2 => [4, 8, 0],
            3 => [-1, 8, 12], 4 => [4, -2, 12], 5 => [4, 8, -6],
            6 => [4, 4, 12], 7 => [4, 8, 8], 8 => [4, 8],
            _ => [4, 8, 12, 16]
        };
        Assert.Throws<InvalidDataException>(() =>
            AcceptancePowerShellProbeIoPolicy.RequireHandles(handles));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 4096)]
    [InlineData(2, 0)]
    public void Bounded_pipe_read_results_are_accepted(int kind, int count)
    {
        AcceptancePowerShellProbeIoPolicy.RequireRead(
            new((AcceptancePowerShellProbeReadKind)kind, count));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 4097)]
    [InlineData(2, 1)]
    [InlineData(3, 0)]
    [InlineData(0, -1)]
    public void Contradictory_or_oversized_pipe_read_results_are_refused(int kind, int count)
    {
        Assert.Throws<InvalidDataException>(() =>
            AcceptancePowerShellProbeIoPolicy.RequireRead(
                new((AcceptancePowerShellProbeReadKind)kind, count)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4095)]
    [InlineData(4096)]
    [InlineData(4097)]
    [InlineData(12288)]
    public void Retention_is_capped_but_reading_and_discard_counting_continue(int bytes)
    {
        var source = new BulkOutput(bytes);
        var result = new AcceptancePowerShellProbeDrain(source).Run(CancellationToken.None);
        Assert.Null(result.Failure);
        Assert.Equal((long)bytes, result.Facts.Total);
        Assert.Equal(Math.Min(bytes, 4096), result.Facts.Retained);
        Assert.Equal((long)Math.Max(bytes - 4096, 0), result.Facts.Discarded);
        Assert.Equal(AcceptancePowerShellProbeDrainState.End, result.Facts.State);
        Assert.Equal(0, result.Facts.Error);
        Assert.Equal(1 + (bytes + 4095) / 4096, source.Calls);
    }

    [Fact]
    public void Original_read_failure_is_preserved_with_numeric_only_facts()
    {
        var fault = new Win32Exception(1314, "Synthetic private diagnostic.");
        var drain = new AcceptancePowerShellProbeDrain(new StubOutput(() => throw fault));
        var result = drain.Run(CancellationToken.None);
        Assert.Same(fault, result.Failure);
        Assert.Same(fault, Assert.Throws<Win32Exception>(result.ThrowIfFailed));
        Assert.Equal(AcceptancePowerShellProbeDrainState.Faulted, result.Facts.State);
        Assert.Equal(1314, result.Facts.Error);
        Assert.Equal(0L, result.Facts.Total);
    }

    [Fact]
    public void Cancellation_is_observed_between_nonblocking_reads()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new StubOutput(() =>
        {
            cancellation.Cancel();
            return new(AcceptancePowerShellProbeReadKind.Pending);
        });
        var result = new AcceptancePowerShellProbeDrain(source).Run(cancellation.Token);
        var error = Assert.IsType<OperationCanceledException>(result.Failure);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(AcceptancePowerShellProbeDrainState.Cancelled, result.Facts.State);
        Assert.Equal(1223, result.Facts.Error);
    }

    [Fact]
    public void Invalid_source_count_fails_before_retention_or_count_mutation()
    {
        var source = new StubOutput(() =>
            new(AcceptancePowerShellProbeReadKind.Data, 4097));
        var result = new AcceptancePowerShellProbeDrain(source).Run(CancellationToken.None);
        Assert.IsType<InvalidDataException>(result.Failure);
        Assert.Equal(AcceptancePowerShellProbeDrainState.Faulted, result.Facts.State);
        Assert.Equal(13, result.Facts.Error);
        Assert.Equal(0L, result.Facts.Total);
        Assert.Equal(0, result.Facts.Retained);
        Assert.Equal(0L, result.Facts.Discarded);
    }

    [Fact]
    public void A_drain_cannot_be_started_twice()
    {
        var source = new BulkOutput(0);
        var drain = new AcceptancePowerShellProbeDrain(source);
        Assert.Null(drain.Run(CancellationToken.None).Failure);
        var error = Assert.Throws<Win32Exception>(() => drain.Run(CancellationToken.None));
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public void Both_output_streams_are_drained_concurrently()
    {
        using var entered = new CountdownEvent(2);
        AcceptancePowerShellProbeRead ReadTogether()
        {
            entered.Signal();
            if (!entered.Wait(TimeSpan.FromSeconds(2)))
                throw new Win32Exception(1460, "Synthetic concurrent read timeout.");
            return new(AcceptancePowerShellProbeReadKind.End);
        }
        using var drains = new AcceptancePowerShellProbeDrains(
            new StubOutput(ReadTogether), new StubOutput(ReadTogether));
        drains.Start();
        drains.Dispose();
        Assert.Equal(0, entered.CurrentCount);
        Assert.True(drains.IsClosed);
        Assert.Equal(AcceptancePowerShellProbeDrainState.End, drains.Facts.Output.State);
        Assert.Equal(AcceptancePowerShellProbeDrainState.End, drains.Facts.Error.State);
    }

    [Fact]
    public void Missing_EOF_is_a_bounded_cleanup_failure_not_success()
    {
        using var drains = new AcceptancePowerShellProbeDrains(
            new StubOutput(Pending), new StubOutput(Pending));
        drains.Start();
        var elapsed = Stopwatch.StartNew();
        var error = Assert.Throws<AcceptanceChildCleanupException>(drains.Dispose);
        Assert.Contains(error.InnerExceptions,
            failure => failure is Win32Exception { NativeErrorCode: 1460 });
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(15));
        Assert.False(drains.IsClosed);
        Assert.Equal(AcceptancePowerShellProbeDrainState.Cancelled, drains.Facts.Output.State);
        Assert.Equal(AcceptancePowerShellProbeDrainState.Cancelled, drains.Facts.Error.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Failure_of_either_stream_is_not_hidden_by_the_other_stream(int stream)
    {
        var fault = new Win32Exception(5, "Synthetic output read denial.");
        using var drains = new AcceptancePowerShellProbeDrains(
            new StubOutput(() => stream == 0 ? throw fault : End()),
            new StubOutput(() => stream == 1 ? throw fault : End()));
        drains.Start();
        var error = Assert.Throws<AcceptanceChildCleanupException>(drains.Dispose);
        Assert.Same(fault, Assert.Single(error.InnerExceptions));
        Assert.False(drains.IsClosed);
        var facts = stream == 0 ? drains.Facts.Output : drains.Facts.Error;
        Assert.Equal(AcceptancePowerShellProbeDrainState.Faulted, facts.State);
        Assert.Equal(5, facts.Error);
    }

    [Fact]
    public void Closed_and_duplicate_controller_use_is_refused_without_new_drains()
    {
        using var drains = new AcceptancePowerShellProbeDrains(
            new StubOutput(End), new StubOutput(End));
        Assert.Equal(13, Assert.Throws<Win32Exception>(drains.Check).NativeErrorCode);
        drains.Start();
        Assert.Equal(13, Assert.Throws<Win32Exception>(drains.Start).NativeErrorCode);
        drains.Dispose();
        Assert.True(drains.IsClosed);
        Assert.Equal(6, Assert.Throws<Win32Exception>(drains.Start).NativeErrorCode);
        Assert.Equal(6, Assert.Throws<Win32Exception>(drains.Check).NativeErrorCode);
        drains.Dispose();
        Assert.True(drains.IsClosed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Acquisition_failure_and_each_cleanup_failure_are_both_preserved(int point)
    {
        var original = new InvalidOperationException("Synthetic acquisition fault.");
        var cleanup = new Win32Exception(6, "Synthetic close fault.");
        List<int> calls = [];
        var operations = Enumerable.Range(0, 4).Select(index => (Action)(() =>
        {
            calls.Add(index);
            if (index == point)
                throw cleanup;
        })).ToArray();
        var error = Assert.Throws<AggregateException>(() =>
            AcceptancePowerShellProbeIoPolicy.CleanupAfterFailure(original, operations));
        Assert.Equal(new[] { 0, 1, 2, 3 }, calls);
        Assert.Same(original, error.InnerExceptions[0]);
        var closed = Assert.IsType<AcceptanceChildCleanupException>(error.InnerExceptions[1]);
        Assert.Same(cleanup, Assert.Single(closed.InnerExceptions));
    }

    [Fact]
    public void Successful_cleanup_does_not_replace_the_original_failure()
    {
        var original = new InvalidOperationException("Synthetic acquisition fault.");
        var calls = 0;
        void Fail()
        {
            try { throw original; }
            catch (Exception error)
            {
                AcceptancePowerShellProbeIoPolicy.CleanupAfterFailure(
                    error, () => calls++);
                throw;
            }
        }
        Assert.Same(original, Assert.Throws<InvalidOperationException>(Fail));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(0, 0x0008000cu)]
    [InlineData(1, 0x08080004u)]
    [InlineData(2, 0x00080014u)]
    public void Console_comparison_changes_only_the_closed_console_flags(int console, uint expected)
    {
        var flags = AcceptancePowerShellProbeIoPolicy.CreationFlagsFor(
            (AcceptancePowerShellProbeConsole)console);
        Assert.Equal(expected, flags);
        Assert.Equal(0x00080004u, flags & ~(0x08000000u | 0x00000008u | 0x00000010u));
    }

    [Theory]
    [InlineData(0, 0x100u)]
    [InlineData(1, 0x100u)]
    [InlineData(2, 0x101u)]
    public void Only_the_new_private_console_selects_the_hidden_window_startup_flag(
        int console, uint expected)
    {
        var flags = AcceptancePowerShellProbeIoPolicy.StartupFlagsFor(
            (AcceptancePowerShellProbeConsole)console);
        Assert.Equal(expected, flags);
        Assert.Equal(0x100u, flags & ~0x00000001u);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void Unknown_console_policy_is_refused_without_a_default_arm(int value)
    {
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptancePowerShellProbeIoPolicy.CreationFlagsFor(
                (AcceptancePowerShellProbeConsole)value));
        Assert.Equal(13, error.NativeErrorCode);
        var startupError = Assert.Throws<Win32Exception>(() =>
            AcceptancePowerShellProbeIoPolicy.StartupFlagsFor(
                (AcceptancePowerShellProbeConsole)value));
        Assert.Equal(13, startupError.NativeErrorCode);
    }

    [Fact]
    public void Image_verification_reads_once_and_accepts_ordinal_case_difference()
    {
        var image = new ProbeImage(ProbeApplication.ToLowerInvariant());
        AcceptancePowerShellProbeIoPolicy.RequireImage(ProbeApplication, image);
        Assert.Equal(1, image.Reads);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pwsh.exe")]
    [InlineData(@"C:\Windows\System32\cmd.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\other.exe")]
    public void A_different_or_unqualified_actual_image_is_refused(string actual)
    {
        var image = new ProbeImage(actual);
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptancePowerShellProbeIoPolicy.RequireImage(ProbeApplication, image));
        Assert.Equal(13, error.NativeErrorCode);
        Assert.Equal(1, image.Reads);
    }

    [Fact]
    public void Image_query_failure_is_preserved_without_a_second_read()
    {
        var fault = new Win32Exception(5, "Synthetic image query denial.");
        var image = new ProbeImage(string.Empty, fault);
        var error = Assert.Throws<Win32Exception>(() =>
            AcceptancePowerShellProbeIoPolicy.RequireImage(ProbeApplication, image));
        Assert.Same(fault, error);
        Assert.Equal(1, image.Reads);
    }

    [Fact]
    public void Visible_diagnostics_are_closed_numeric_rows_without_stream_content()
    {
        var ended = new AcceptancePowerShellProbeDrainFacts(
            3, 3, 0, AcceptancePowerShellProbeDrainState.End, 0);
        var facts = new AcceptancePowerShellProbeIoFacts(
            AcceptancePowerShellProbeIoPolicy.CreationFlags, true, true, 8, true, true, true, ended, ended);
        Assert.Equal(new[]
        {
            "HERMES_RESTRICTED_POWERSHELL_LAUNCH v=1 arm=5 flags=524300 created=1 imageMatch=1 sourceBytes=8",
            "HERMES_RESTRICTED_POWERSHELL_IO v=2 arm=5 stdinEof=1 pipesClosed=1 drainsClosed=1",
            "HERMES_RESTRICTED_POWERSHELL_DRAIN v=1 arm=5 stream=0 bytes=3 kept=3 dropped=0 state=1 error=0",
            "HERMES_RESTRICTED_POWERSHELL_DRAIN v=1 arm=5 stream=1 bytes=3 kept=3 dropped=0 state=1 error=0"
        }, facts.Rows(5));
        var uncreated = facts with { ProcessCreated = false, ImageMatched = false, SourceBytes = 0 };
        Assert.Contains("created=0 imageMatch=0 sourceBytes=0", uncreated.Rows(5)[0]);
    }

    [Fact]
    public void Observation_capability_carries_final_existing_drain_facts_after_disposal()
    {
        using var drains = new AcceptancePowerShellProbeDrains(
            new BulkOutput(7), new BulkOutput(9));
        drains.Start();
        drains.Dispose();
        IAcceptancePowerShellProbeObservation observed = new StubObservation(
            new(AcceptancePowerShellProbeIoPolicy.CreationFlags, true, true, 8,
                true, true, drains.IsClosed, drains.Facts.Output, drains.Facts.Error));
        var facts = observed.PowerShellIoFacts;
        Assert.Equal((7L, 9L), (facts.Output.Total, facts.Error.Total));
        Assert.True(facts.DrainsClosed);
        Assert.Equal(4, facts.Rows(5).Length);
    }

    private const string ProbeApplication = @"C:\Program Files\PowerShell\7\pwsh.exe";

    private sealed record StubObservation(AcceptancePowerShellProbeIoFacts PowerShellIoFacts)
        : IAcceptancePowerShellProbeObservation;

    private sealed class ProbeImage(string image, Exception? failure = null)
        : IAcceptancePowerShellProbeImage
    {
        private int _reads;
        internal int Reads => _reads;

        public string Read()
        {
            _reads++;
            if (failure is { } error)
                throw error;
            return image;
        }
    }

    private static AcceptancePowerShellProbeRead Pending() =>
        new(AcceptancePowerShellProbeReadKind.Pending);

    private static AcceptancePowerShellProbeRead End() =>
        new(AcceptancePowerShellProbeReadKind.End);

    private sealed class StubOutput(Func<AcceptancePowerShellProbeRead> read)
        : IAcceptancePowerShellProbeOutput
    {
        public AcceptancePowerShellProbeRead Read(Span<byte> buffer) => read();
    }

    private sealed class BulkOutput(int bytes) : IAcceptancePowerShellProbeOutput
    {
        private int _remaining = bytes;
        private int _calls;
        internal int Calls => _calls;

        public AcceptancePowerShellProbeRead Read(Span<byte> buffer)
        {
            _calls++;
            if (_remaining == 0)
                return End();
            var count = Math.Min(_remaining, buffer.Length);
            buffer[..count].Fill(0x61);
            _remaining -= count;
            return new(AcceptancePowerShellProbeReadKind.Data, count);
        }
    }
}
