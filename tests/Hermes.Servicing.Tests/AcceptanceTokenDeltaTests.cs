using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class AcceptanceTokenDeltaTests
{
    public static IEnumerable<object[]> FailurePairs =>
        from operation in Enumerable.Range(1, 3)
        from read in Enumerable.Range(1, operation)
        select new object[] { operation, read };

    [Fact]
    public void Exact_stages_read_the_held_subject_and_do_not_accept_or_rewrite_values()
    {
        var fake = new Construction();
        var delta = new AcceptanceTokenDelta();
        fake.Run(delta);
        Assert.Equal(new[] { "read1", "filter", "read2", "set", "read3", "verify" }, fake.Calls);
        Assert.Equal(new[]
        {
            "HERMES_RESTRICTED_TOKEN_DELTA v=1 stage=1 integrity=12288 restrictedCount=0 available=1 error=0",
            "HERMES_RESTRICTED_TOKEN_DELTA v=1 stage=2 integrity=4096 restrictedCount=2 available=1 error=0",
            "HERMES_RESTRICTED_TOKEN_DELTA v=1 stage=3 integrity=8192 restrictedCount=2 available=1 error=0"
        }, delta.Samples.Select(sample => sample.Row));
        Assert.Equal(new AcceptanceTokenDeltaFacts(12288, 0), fake.Source.Facts);
        Assert.True(fake.Source.IsClosed && fake.Candidate.IsClosed);
        delta.Complete(null, new Sink(row => Assert.True(row.Length <= 160)));
    }

    [Theory]
    [InlineData(1, 50)]
    [InlineData(2, 50)]
    [InlineData(3, 50)]
    [InlineData(1, 13)]
    [InlineData(2, 13)]
    [InlineData(3, 13)]
    public void Unsupported_and_invalid_reads_are_unavailable_and_do_not_skip_operations(
        int point, int code)
    {
        var fault = new Win32Exception(code, "Synthetic read failure.");
        var fake = new Construction(failedRead: point, observation: fault);
        var delta = new AcceptanceTokenDelta();
        fake.Run(delta);
        Assert.Equal(new[] { "read1", "filter", "read2", "set", "read3", "verify" }, fake.Calls);
        var sample = delta.Samples[point - 1];
        Assert.Null(sample.Facts);
        Assert.Contains($"integrity=-1 restrictedCount=-1 available=0 error={code}", sample.Row);
        Assert.True(fake.Source.IsClosed && fake.Candidate.IsClosed);
        Assert.Same(fault, Record.Exception(() => delta.Complete(null, new Sink(_ => { }))));
    }

    [Theory]
    [MemberData(nameof(FailurePairs))]
    public void Original_graph_is_primary_and_only_reached_stages_are_reported(
        int operation, int read)
    {
        var leaf = new InvalidOperationException("Synthetic original leaf.");
        var original = new AggregateException("Synthetic original graph.", leaf, leaf);
        var observation = new InvalidOperationException("Synthetic observation failure.");
        var fake = new Construction(read, observation, operation, original);
        var delta = new AcceptanceTokenDelta();
        Assert.Same(original, Record.Exception(() => fake.Run(delta)));
        var failure = Assert.Throws<AggregateException>(
            () => delta.Complete(original, new Sink(_ => { })));
        Assert.Equal(new Exception[] { original, observation }, failure.InnerExceptions);
        Assert.Equal(new Exception[] { leaf, leaf }, original.InnerExceptions);
        Assert.Equal(operation, delta.Samples.Count);
        Assert.True(fake.Source.IsClosed);
        Assert.Equal(operation >= 2, fake.Candidate.IsClosed);
    }

    [Fact]
    public void A_failure_already_in_the_original_graph_is_not_duplicated_or_flattened()
    {
        var observation = new InvalidOperationException("Synthetic shared leaf.");
        var original = new AggregateException("Synthetic envelope.", observation, observation);
        var fake = new Construction(1, observation, 3, original);
        var delta = new AcceptanceTokenDelta();
        Assert.Same(original, Record.Exception(() => fake.Run(delta)));
        Assert.Same(original, Record.Exception(
            () => delta.Complete(original, new Sink(_ => { }))));
        Assert.Equal(new Exception[] { observation, observation }, original.InnerExceptions);
    }

    [Fact]
    public void Reporting_attempts_every_row_after_cleanup_and_preserves_all_failure_roots()
    {
        var original = new Win32Exception(123, "Synthetic original failure.");
        var observation = new InvalidOperationException("Synthetic observation failure.");
        var reporting = new InvalidOperationException("Synthetic reporting failure.");
        var fake = new Construction(2, observation, 3, original);
        var delta = new AcceptanceTokenDelta();
        Assert.Same(original, Record.Exception(() => fake.Run(delta)));
        var writes = 0;
        var sink = new Sink(_ =>
        {
            Assert.True(fake.Source.IsClosed && fake.Candidate.IsClosed);
            writes++;
            throw reporting;
        });
        var error = Assert.Throws<AggregateException>(() => delta.Complete(original, sink));
        Assert.Equal(new Exception[] { original, observation, reporting }, error.InnerExceptions);
        Assert.Equal(3, writes);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(8192, -1)]
    [InlineData(8192, 8192)]
    public void Invalid_numeric_results_cannot_claim_read_success(int integrity, int count)
    {
        var delta = new AcceptanceTokenDelta();
        _ = delta.Filter(0, value => value,
            _ => new(integrity, count), _ => new(8192, 0));
        Assert.Null(delta.Samples[0].Facts);
        Assert.Contains("available=0 error=13", delta.Samples[0].Row);
        Assert.Throws<Win32Exception>(() => delta.Complete(null, new Sink(_ => { })));
    }

    [Theory]
    [InlineData("ar-SA", 0, 0, "integrity=0 restrictedCount=0", 93)]
    [InlineData("ar-SA", 0, 8191, "integrity=0 restrictedCount=8191", 96)]
    [InlineData("ar-SA", int.MaxValue, 0, "integrity=2147483647 restrictedCount=0", 102)]
    [InlineData("ar-SA", int.MaxValue, 8191, "integrity=2147483647 restrictedCount=8191", 105)]
    [InlineData("fr-FR", 0, 0, "integrity=0 restrictedCount=0", 93)]
    [InlineData("fr-FR", 0, 8191, "integrity=0 restrictedCount=8191", 96)]
    [InlineData("fr-FR", int.MaxValue, 0, "integrity=2147483647 restrictedCount=0", 102)]
    [InlineData("fr-FR", int.MaxValue, 8191, "integrity=2147483647 restrictedCount=8191", 105)]
    public void Accepted_numeric_boundaries_emit_invariant_ascii_rows(
        string cultureName, int integrity, int count, string numbers, int framedBytes) =>
        InCulture(cultureName, () =>
        {
            var delta = new AcceptanceTokenDelta();
            var filtered = delta.Filter(0, value => value,
                _ => new(integrity, count), _ => new(integrity, count));
            delta.SetMedium(filtered, _ => { }, _ => new(integrity, count));
            var rows = new List<string>();
            delta.Complete(null, new Sink(rows.Add));
            Assert.Equal(new[]
            {
                $"HERMES_RESTRICTED_TOKEN_DELTA v=1 stage=1 {numbers} available=1 error=0",
                $"HERMES_RESTRICTED_TOKEN_DELTA v=1 stage=2 {numbers} available=1 error=0",
                $"HERMES_RESTRICTED_TOKEN_DELTA v=1 stage=3 {numbers} available=1 error=0"
            }, rows);
            Assert.Equal(delta.Samples.Select(sample => sample.Row), rows);
            Assert.All(rows, row => AssertCanonicalRow(row, framedBytes));
        });

    [Theory]
    [InlineData("ar-SA", 1)]
    [InlineData("ar-SA", 2)]
    [InlineData("ar-SA", 3)]
    [InlineData("fr-FR", 1)]
    [InlineData("fr-FR", 2)]
    [InlineData("fr-FR", 3)]
    public void Maximum_positive_native_error_emits_unknowns_and_preserves_failure(
        string cultureName, int point) =>
        InCulture(cultureName, () =>
        {
            var fault = new Win32Exception(int.MaxValue, "Synthetic read failure.");
            var fake = new Construction(failedRead: point, observation: fault);
            var delta = new AcceptanceTokenDelta();
            fake.Run(delta);
            var sample = delta.Samples[point - 1];
            Assert.Null(sample.Facts);
            Assert.Same(fault, sample.Failure);
            Assert.Equal($"HERMES_RESTRICTED_TOKEN_DELTA v=1 stage={point} integrity=-1 restrictedCount=-1 available=0 error=2147483647",
                sample.Row);
            var rows = new List<string>();
            Assert.Same(fault, Record.Exception(
                () => delta.Complete(null, new Sink(rows.Add))));
            Assert.Equal(delta.Samples.Select(value => value.Row), rows);
            Assert.Equal(104, rows.Max(row => Encoding.UTF8.GetByteCount(row + "\r\n")));
            AssertCanonicalRow(rows[point - 1], 104);
        });

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(-1)]
    [InlineData(0)]
    public void Nonpositive_native_errors_use_the_actual_projection_fallback(int code) =>
        Assert.Equal(13, AcceptanceSourceControlOrchestration.Code(
            new Win32Exception(code, "Synthetic nonpositive error.")));

    [Theory]
    [InlineData(0, 122, 3u, 13)]
    [InlineData(0, 122, 65537u, 13)]
    [InlineData(1, 0, 4u, 13)]
    [InlineData(0, 50, 0u, 50)]
    [InlineData(0, 0, 0u, 13)]
    public void Invalid_query_envelopes_are_refused(int result, int error, uint length, int code)
    {
        var failure = Assert.Throws<Win32Exception>(
            () => AcceptanceTokenDeltaBuffer.RequiredSize(result, error, length, 4));
        Assert.Equal(code, failure.NativeErrorCode);
    }

    [Fact]
    public void Minimum_empty_groups_and_maximum_query_sizes_remain_supported()
    {
        using var data = new Buffer(4);
        Assert.Equal(4, AcceptanceTokenDeltaBuffer.RequiredSize(0, 122, 4, 4));
        Assert.Equal(65536, AcceptanceTokenDeltaBuffer.RequiredSize(0, 122, 65536, 4));
        Assert.Equal(0, ReadCount(data, 4));
    }

    [Fact]
    public void Effective_count_counts_entries_not_distinct_sids_or_group_attributes()
    {
        using var data = Groups();
        Assert.Equal(2, ReadCount(data, 80));
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(-1, 80)]
    [InlineData(1, 4)]
    [InlineData(int.MaxValue, 80)]
    public void Invalid_counted_group_buffers_are_refused(int count, int length)
    {
        using var data = new Buffer(80);
        Marshal.WriteInt32(data.Pointer, count);
        Assert.Equal(13, Assert.Throws<Win32Exception>(
            () => ReadCount(data, length)).NativeErrorCode);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Counted_sids_must_be_valid_and_wholly_inside_the_held_buffer(int variant)
    {
        using var data = Groups();
        var sid = Marshal.ReadIntPtr(data.Pointer, nint.Size);
        var address = variant switch
        {
            0 => data.Pointer - 1,
            1 => data.Pointer + 80,
            _ => sid
        };
        Marshal.WriteIntPtr(data.Pointer, nint.Size, address);
        if (variant == 2)
            Marshal.WriteByte(sid, 0, 0);
        if (variant is 3 or 4)
            Marshal.WriteByte(sid, 1, variant == 3 ? (byte)16 : (byte)15);
        Assert.Equal(13, Assert.Throws<Win32Exception>(() => ReadCount(data, 80)).NativeErrorCode);
    }

    private static void InCulture(string cultureName, Action assertion)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            assertion();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static void AssertCanonicalRow(string row, int framedBytes)
    {
        Assert.Matches(
            @"\AHERMES_RESTRICTED_TOKEN_DELTA v=1 stage=[1-3] " +
            @"integrity=(-1|0|[1-9][0-9]*) restrictedCount=(-1|0|[1-9][0-9]*) " +
            @"available=[01] error=(0|[1-9][0-9]*)\z", row);
        var bytes = Encoding.UTF8.GetByteCount(row + "\r\n");
        Assert.Equal(framedBytes, bytes);
        Assert.Equal(framedBytes - 2, row.Length);
        Assert.InRange(bytes, 0, 160);
    }

    private static int ReadCount(Buffer data, int length) =>
        AcceptanceTokenDeltaBuffer.ReadGroups(data.Pointer, length, 2 * nint.Size,
            offset => AcceptanceTokenDeltaBuffer.RequireSid(data.Pointer, length,
                Marshal.ReadIntPtr(data.Pointer, offset))).Length;

    private static Buffer Groups()
    {
        var data = new Buffer(80);
        var stride = 2 * nint.Size;
        var sid = data.Pointer + nint.Size + 2 * stride;
        Marshal.Copy(new byte[] { 1, 1, 0, 0, 0, 0, 0, 5, 32, 0, 0, 0 }, 0, sid, 12);
        Marshal.WriteInt32(data.Pointer, 2);
        Marshal.WriteIntPtr(data.Pointer, nint.Size, sid);
        Marshal.WriteIntPtr(data.Pointer, nint.Size + stride, sid);
        Marshal.WriteInt32(data.Pointer, nint.Size + stride + nint.Size, 0x10);
        return data;
    }

    private sealed class Buffer : IDisposable
    {
        internal nint Pointer { get; }

        internal Buffer(int length)
        {
            Pointer = Marshal.AllocHGlobal(length);
            Marshal.Copy(new byte[length], 0, Pointer, length);
        }

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    private sealed record Sink(Action<string> Write) : IAcceptanceSourceControlOutput
    {
        public void WriteLine(string row) => Write(row);
    }

    private sealed class Subject(int integrity, int count) : IDisposable
    {
        private AcceptanceTokenDeltaFacts _facts = new(integrity, count);
        private bool _closed;
        internal AcceptanceTokenDeltaFacts Facts => _facts;
        internal bool IsClosed => _closed;
        internal void Medium() => _facts = _facts with { Integrity = 8192 };
        public void Dispose() => _closed = true;
    }

    private sealed class Construction(
        int failedRead = 0, Exception? observation = null,
        int failedOperation = 0, Exception? original = null)
    {
        private readonly List<string> _calls = [];
        private int _sets;
        internal IReadOnlyList<string> Calls => _calls;
        internal Subject Source { get; } = new(12288, 0);
        internal Subject Candidate { get; } = new(4096, 2);

        internal void Run(AcceptanceTokenDelta delta)
        {
            using var source = Source;
            using var filtered = delta.Filter(source, Filter,
                held => Read(held, 1), held => Read(held, 2));
            delta.SetMedium(filtered, SetMedium, held => Read(held, 3));
            _calls.Add("verify");
            Fail(3);
        }

        private Subject Filter(Subject source)
        {
            Assert.Same(Source, source);
            Assert.False(source.IsClosed);
            _calls.Add("filter");
            Fail(1);
            return Candidate;
        }

        private void SetMedium(Subject filtered)
        {
            Assert.Same(Candidate, filtered);
            Assert.False(filtered.IsClosed);
            _calls.Add("set");
            Fail(2);
            filtered.Medium();
            _sets++;
        }

        private AcceptanceTokenDeltaFacts Read(Subject held, int stage)
        {
            Assert.Same(stage == 1 ? Source : Candidate, held);
            Assert.False(held.IsClosed);
            Assert.False(Source.IsClosed);
            Assert.Equal(stage == 3 ? 1 : 0, _sets);
            _calls.Add($"read{stage}");
            if (failedRead == stage)
                throw observation ?? new Win32Exception(50, "Synthetic read failure.");
            return held.Facts;
        }

        private void Fail(int point)
        {
            if (failedOperation == point)
                throw original ?? new Win32Exception(123, "Synthetic operation failure.");
        }
    }
}
