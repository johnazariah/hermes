using System.Buffers.Binary;
using System.Text;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class CrashEventValuesTests
{
    private const ulong Address = 0x10000;
    private const ulong Birth = (1UL << 53) + 1;
    private const string Image = @"C:\owned\child.exe";
    private static nint OwnedEvent => (nint)77;
    private static CrashEventSubject Subject => new(uint.MaxValue, Birth, Image);

    private enum Call
    {
        CreateIdentity, ProbeIdentity, AllocateIdentity, RenderIdentity,
        SnapshotIdentity, FreeIdentity, CloseIdentity,
        CreateDetails, ProbeDetails, AllocateDetails, RenderDetails,
        SnapshotDetails, FreeDetails, CloseDetails
    }

    [Fact]
    public void Paths_are_fixed_ordered_and_selective()
    {
        Assert.Equal<string>(
            ["Event/EventData/Data[@Name=\"ProcessId\"]",
                "Event/EventData/Data[@Name=\"ProcessCreationTime\"]",
                "Event/EventData/Data[@Name=\"AppPath\"]"],
            CrashEventValues.ValuePaths(CrashEventSelection.Identity));
        Assert.Equal<string>(
            ["Event/EventData/Data[@Name=\"ExceptionCode\"]",
                "Event/EventData/Data[@Name=\"ModuleName\"]",
                "Event/EventData/Data[@Name=\"ModulePath\"]"],
            CrashEventValues.ValuePaths(CrashEventSelection.Details));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CrashEventValues.ValuePaths((CrashEventSelection)(-1)));
    }

    [Fact]
    public void Matched_values_are_snapshotted_before_cleanup_in_exact_order()
    {
        var api = new FakeApi();
        var match = Assert.IsType<CrashEventMatched>(Read(api));
        Assert.Equal(new CrashEventDetails(
            "80131623", "module.dll", @"C:\owned\module.dll"), match.Details);
        Assert.Equal<Call>(
            [Call.CreateIdentity, Call.ProbeIdentity, Call.AllocateIdentity,
                Call.RenderIdentity, Call.SnapshotIdentity, Call.FreeIdentity,
                Call.CloseIdentity, Call.CreateDetails, Call.ProbeDetails,
                Call.AllocateDetails, Call.RenderDetails, Call.SnapshotDetails,
                Call.FreeDetails, Call.CloseDetails], api.Calls);
        Assert.Equal(nameof(CrashEventMatched), match.ToString());
        Assert.Equal(nameof(CrashEventDetails), match.Details.ToString());
        Assert.Equal(nameof(CrashEventSubject), Subject.ToString());
        api.AssertSettled();
    }

    public static IEnumerable<object[]> Births() =>
        new[] { (1UL << 53) - 1, 1UL << 53, (1UL << 53) + 1, ulong.MaxValue }
            .Select(value => new object[] { value });

    [Theory]
    [MemberData(nameof(Births))]
    public void UInt64_births_match_losslessly_and_neighbors_do_not(ulong birth)
    {
        var expected = Subject with { CreationFileTime = birth };
        var matched = new FakeApi(Identity(birth));
        Assert.IsType<CrashEventMatched>(Read(matched, expected));
        matched.AssertSettled();
        AssertBirthMismatch(birth, birth - 1);
        if (birth != ulong.MaxValue)
            AssertBirthMismatch(birth, birth + 1);
    }

    private static void AssertBirthMismatch(ulong expected, ulong actual)
    {
        var api = new FakeApi(Identity(actual));
        var mismatch = Assert.IsType<CrashEventMismatch>(
            Read(api, Subject with { CreationFileTime = expected }));
        Assert.Equal(CrashEventReason.CreationTimeMismatch, mismatch.Reason);
        AssertNoDetails(api);
    }

    [Fact]
    public void Wrong_uint32_pid_never_reads_details()
    {
        var api = new FakeApi(Identity(pid: uint.MaxValue - 1));
        var mismatch = Assert.IsType<CrashEventMismatch>(Read(api));
        Assert.Equal(CrashEventReason.ProcessIdMismatch, mismatch.Reason);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData(@"C:\owned\CHILD.exe")]
    [InlineData(@"c:\owned\child.exe")]
    [InlineData(@"C:\other\child.exe")]
    public void Path_comparison_is_exact_without_case_or_alias_folding(string path)
    {
        var api = new FakeApi(Identity(path: path));
        var mismatch = Assert.IsType<CrashEventMismatch>(Read(api));
        Assert.Equal(CrashEventReason.ImagePathMismatch, mismatch.Reason);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"child.exe")]
    [InlineData(@"C:child.exe")]
    [InlineData(@"C:\owned\..\child.exe")]
    [InlineData(@"C:\owned\.\child.exe")]
    [InlineData(@"C:\owned\\child.exe")]
    [InlineData(@"C:/owned/child.exe")]
    [InlineData(@"\\server\owned\child.exe")]
    [InlineData(@"\\?\C:\owned\child.exe")]
    [InlineData(@"C:\owned\child.exe.")]
    [InlineData(@"C:\owned\child.exe ")]
    [InlineData("C:\\owned\\bad\u0001.exe")]
    public void Invalid_expected_paths_are_refused_before_any_native_capability(string path)
    {
        var api = new FakeApi();
        AssertFault(Read(api, Subject with { ActualImagePath = path }),
            CrashEventFailureKind.Refused, CrashEventReason.InvalidSubject);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdbff)]
    [InlineData(0xdc00)]
    [InlineData(0xdfff)]
    public void Runtime_unpaired_surrogates_are_refused_before_any_native_capability(
        int codeUnit)
    {
        const string prefix = @"C:\owned\bad";
        // Construct after attribute decoding; metadata strings cannot reliably
        // preserve an unpaired surrogate through their UTF-8 representation.
        var path = prefix + new string((char)codeUnit, 1) + ".exe";
        Assert.Equal(codeUnit, (int)path[prefix.Length]);
        Assert.Equal(1, path.Count(char.IsSurrogate));
        var api = new FakeApi();
        AssertFault(Read(api, Subject with { ActualImagePath = path }),
            CrashEventFailureKind.Refused, CrashEventReason.InvalidSubject);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Replacement_characters_are_valid_unicode_not_surrogate_failures(int count)
    {
        var replacement = new string((char)0xfffd, count);
        Assert.All(replacement, character => Assert.Equal(0xfffd, (int)character));
        var path = @"C:\owned\bad" + replacement + ".exe";
        var expected = Subject with { ActualImagePath = path };
        var matching = new FakeApi(Identity(path: path));
        Assert.IsType<CrashEventMatched>(Read(matching, expected));
        matching.AssertSettled();
        var different = new FakeApi();
        var mismatch = Assert.IsType<CrashEventMismatch>(Read(different, expected));
        Assert.Equal(CrashEventReason.ImagePathMismatch, mismatch.Reason);
        AssertNoDetails(different);
    }

    [Theory]
    [InlineData(@"C:\owned\..\child.exe")]
    [InlineData(@"C:\owned\child.exe.")]
    [InlineData(@"relative.exe")]
    public void Malformed_event_paths_do_not_grant_identity(string path)
    {
        var api = new FakeApi(Identity(path: path));
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.MalformedPath);
        AssertNoDetails(api);
    }

    [Fact]
    public void Missing_expected_pid_or_birth_and_invalid_handles_make_no_calls()
    {
        var api = new FakeApi();
        AssertFault(Read(api, Subject with { ProcessId = 0 }),
            CrashEventFailureKind.Refused, CrashEventReason.InvalidSubject);
        AssertFault(Read(api, Subject with { CreationFileTime = 0 }),
            CrashEventFailureKind.Refused, CrashEventReason.InvalidSubject);
        AssertFault(CrashEventValues.Read(api, 0, Subject),
            CrashEventFailureKind.Refused, CrashEventReason.InvalidEventHandle);
        AssertFault(CrashEventValues.Read(api, -1, Subject),
            CrashEventFailureKind.Refused, CrashEventReason.InvalidEventHandle);
        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(0, 7u)]
    [InlineData(0, 8u)]
    [InlineData(0, 12u)]
    [InlineData(0, 1u)]
    [InlineData(0, 148u)]
    [InlineData(0, 276u)]
    [InlineData(1, 0u)]
    [InlineData(1, 9u)]
    [InlineData(1, 10u)]
    [InlineData(1, 12u)]
    [InlineData(1, 149u)]
    [InlineData(2, 0u)]
    [InlineData(2, 20u)]
    [InlineData(2, 129u)]
    public void Invalid_identity_types_never_read_details(int index, uint type)
    {
        var bytes = Identity();
        SetType(bytes, index, type);
        var api = new FakeApi(bytes);
        AssertFault(Read(api), CrashEventFailureKind.Refused,
            type == 0 ? CrashEventReason.MissingValue : CrashEventReason.VariantType);
        AssertNoDetails(api);
    }

    [Fact]
    public void Undefined_scalar_counts_and_uint32_union_padding_are_not_interpreted()
    {
        var identity = Identity();
        var details = Details();
        foreach (var bytes in new[] { identity, details })
            foreach (var index in Enumerable.Range(0, 3))
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 16 + 8),
                    uint.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(identity, 0xdeadbeefffffffffUL);
        var api = new FakeApi(identity, details);
        Assert.IsType<CrashEventMatched>(Read(api));
        api.AssertSettled();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(47u)]
    [InlineData(65537u)]
    [InlineData(uint.MaxValue)]
    public void Probe_sizes_are_bounded_before_allocation(uint size)
    {
        var api = new FakeApi { ProbeSize = size };
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.BufferSize);
        Assert.DoesNotContain(Call.AllocateIdentity, api.Calls);
        AssertNoDetails(api);
    }

    [Fact]
    public void Maximum_native_allocation_is_allowed_without_reading_unused_bytes()
    {
        var api = new FakeApi { ProbeSize = CrashEventValues.MaximumBufferBytes };
        Assert.IsType<CrashEventMatched>(Read(api));
        api.AssertSettled();
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(47u)]
    [InlineData(65537u)]
    [InlineData(uint.MaxValue)]
    public void Returned_used_bytes_are_checked_before_snapshot(uint used)
    {
        var api = new FakeApi { UsedBytes = used };
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.BufferSize);
        Assert.DoesNotContain(Call.SnapshotIdentity, api.Calls);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void Exact_api_property_count_is_required_before_snapshot(uint count)
    {
        var api = new FakeApi { PropertyCount = count };
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.PropertyCount);
        Assert.DoesNotContain(Call.SnapshotIdentity, api.Calls);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void Allocation_and_snapshot_lengths_cannot_override_owned_bounds(int adjustment)
    {
        var allocation = new FakeApi { CapacityAdjustment = adjustment };
        AssertFault(Read(allocation), CrashEventFailureKind.Refused, CrashEventReason.BufferSize);
        Assert.DoesNotContain(Call.RenderIdentity, allocation.Calls);
        AssertNoDetails(allocation);
        var snapshot = new FakeApi { SnapshotAdjustment = adjustment };
        AssertFault(Read(snapshot), CrashEventFailureKind.Refused, CrashEventReason.BufferSize);
        AssertNoDetails(snapshot);
    }

    [Theory]
    [InlineData(0UL, (int)CrashEventReason.PointerRange)]
    [InlineData(1UL, (int)CrashEventReason.BufferAlignment)]
    [InlineData(65537UL, (int)CrashEventReason.BufferAlignment)]
    [InlineData(18446744073709551608UL, (int)CrashEventReason.PointerRange)]
    public void Allocation_addresses_are_checked_before_render(ulong address, int reason)
    {
        var api = new FakeApi { AllocationAddress = address };
        AssertFault(Read(api), CrashEventFailureKind.Refused, (CrashEventReason)reason);
        Assert.DoesNotContain(Call.RenderIdentity, api.Calls);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(Address + 16)]
    [InlineData(Address + 47)]
    [InlineData(Address + 49)]
    [InlineData(ulong.MaxValue)]
    public void String_pointers_cannot_target_null_headers_odd_or_foreign_regions(ulong pointer)
    {
        var bytes = Identity();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), pointer);
        var api = new FakeApi(bytes);
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.PointerRange);
        AssertNoDetails(api);
    }

    [Fact]
    public void String_pointer_at_end_of_used_buffer_is_not_dereferenced()
    {
        var bytes = Identity();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32),
            Address + (ulong)bytes.Length);
        var api = new FakeApi(bytes);
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.PointerRange);
        AssertNoDetails(api);
    }

    [Theory]
    [InlineData(0xd800)]
    [InlineData(0xdc00)]
    public void Invalid_utf16_is_refused_without_replacement(int codeUnit)
    {
        var bytes = Identity();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), (ushort)codeUnit);
        var api = new FakeApi(bytes);
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.InvalidUtf16);
        AssertNoDetails(api);
    }

    [Fact]
    public void Unicode_surrogate_pairs_are_preserved_exactly()
    {
        const string path = "C:\\owned\\\U0001f680.exe";
        var api = new FakeApi(Identity(path: path));
        Assert.IsType<CrashEventMatched>(Read(api, Subject with { ActualImagePath = path }));
        api.AssertSettled();
    }

    [Fact]
    public void String_limit_is_inclusive_and_missing_terminators_are_refused()
    {
        var path = @"C:\" + new string('a', CrashEventValues.MaximumCharacters - 3);
        var admitted = new FakeApi(Identity(path: path));
        Assert.IsType<CrashEventMatched>(
            Read(admitted, Subject with { ActualImagePath = path }));
        admitted.AssertSettled();
        var oversized = new FakeApi(Identity(path: path + "a"));
        AssertFault(Read(oversized), CrashEventFailureKind.Refused, CrashEventReason.StringLimit);
        AssertNoDetails(oversized);
        var bytes = Identity();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(bytes.Length - 2), 1);
        var unterminated = new FakeApi(bytes);
        AssertFault(Read(unterminated),
            CrashEventFailureKind.Refused, CrashEventReason.StringLimit);
        AssertNoDetails(unterminated);
    }

    [Theory]
    [InlineData(0, 0u)]
    [InlineData(0, 20u)]
    [InlineData(1, 129u)]
    [InlineData(2, 21u)]
    public void Detail_fields_keep_their_documented_string_types(int index, uint type)
    {
        var details = Details();
        SetType(details, index, type);
        var api = new FakeApi(details: details);
        AssertFault(Read(api), CrashEventFailureKind.Refused,
            type == 0 ? CrashEventReason.MissingValue : CrashEventReason.VariantType);
        api.AssertSettled();
    }

    [Theory]
    [InlineData("", "module.dll", "module-path")]
    [InlineData("0x80131623", "module.dll", "module-path")]
    [InlineData("8013162g", "module.dll", "module-path")]
    [InlineData("80131623", "", "module-path")]
    [InlineData("80131623", "module.dll", "")]
    public void Malformed_details_are_not_accepted(string code, string module, string path)
    {
        var api = new FakeApi(details: Details(code, module, path));
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.InvalidDetail);
        api.AssertSettled();
    }

    [Fact]
    public void Malformed_detail_unicode_is_not_accepted()
    {
        var bytes = Details();
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(48), 0xd800);
        var api = new FakeApi(details: bytes);
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.InvalidUtf16);
        api.AssertSettled();
    }

    public static IEnumerable<object[]> NativeFailures() =>
        new[]
        {
            (Call.CreateIdentity, CrashEventReason.ContextCreate),
            (Call.ProbeIdentity, CrashEventReason.Render),
            (Call.RenderIdentity, CrashEventReason.Render),
            (Call.FreeIdentity, CrashEventReason.BufferRelease),
            (Call.CloseIdentity, CrashEventReason.ContextClose),
            (Call.CreateDetails, CrashEventReason.ContextCreate),
            (Call.ProbeDetails, CrashEventReason.Render),
            (Call.RenderDetails, CrashEventReason.Render),
            (Call.FreeDetails, CrashEventReason.BufferRelease),
            (Call.CloseDetails, CrashEventReason.ContextClose)
        }.Select(value => new object[] { (int)value.Item1, (int)value.Item2 });

    [Theory]
    [MemberData(nameof(NativeFailures))]
    public void Every_native_or_cleanup_failure_is_non_success(int failed, int reason)
    {
        var call = (Call)failed;
        var api = new FakeApi { Failures = [call] };
        var kind = (CrashEventReason)reason is
            CrashEventReason.ContextClose or CrashEventReason.BufferRelease
            ? CrashEventFailureKind.Refused : CrashEventFailureKind.Unavailable;
        AssertFault(Read(api), kind, (CrashEventReason)reason);
        if (call <= Call.CloseIdentity)
            AssertNoDetails(api);
        api.AssertSettled();
    }

    [Fact]
    public void All_known_cleanup_failures_are_retained_and_details_are_not_read()
    {
        var api = new FakeApi { Failures = [Call.FreeIdentity, Call.CloseIdentity] };
        var failure = Assert.IsType<CrashEventFailure>(Read(api));
        Assert.Equal<CrashEventReason>(
            [CrashEventReason.BufferRelease, CrashEventReason.ContextClose],
            failure.Faults.Select(fault => fault.Reason));
        AssertNoDetails(api);
    }

    public static IEnumerable<object[]> UnexpectedCalls() =>
        Enum.GetValues<Call>().Select(call => new object[] { (int)call });

    [Theory]
    [MemberData(nameof(UnexpectedCalls))]
    public void Unexpected_exceptions_propagate_after_all_owned_cleanup(int failed)
    {
        var api = new FakeApi { UnexpectedAt = (Call)failed };
        Assert.Throws<ArithmeticException>(() => Read(api));
        api.AssertSettled();
    }

    [Fact]
    public void Cleanup_cannot_replace_an_unexpected_error_with_a_typed_failure()
    {
        var api = new FakeApi
        {
            UnexpectedAt = Call.RenderDetails,
            Failures = [Call.FreeDetails, Call.CloseDetails]
        };
        var error = Assert.Throws<AggregateException>(() => Read(api));
        Assert.Contains(error.InnerExceptions, item => item is ArithmeticException);
        Assert.Equal(2, error.InnerExceptions.OfType<CrashEventValuesException>().Count());
        api.AssertSettled();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Every_abi_invariant_is_checked_before_context_creation(int defect)
    {
        var abi = CrashEventAbi.X64;
        var api = new FakeApi { Abi = CorruptAbi(abi, defect) };
        AssertFault(Read(api), CrashEventFailureKind.Refused, CrashEventReason.UnsupportedAbi);
        Assert.Empty(api.Calls);
    }

    private static CrashEventAbi CorruptAbi(CrashEventAbi abi, int defect) => defect switch
    {
        0 => abi with { PointerBytes = 4 },
        1 => abi with { UnionBytes = 4 },
        2 => abi with { CountOffset = 4 },
        3 => abi with { TypeOffset = 8 },
        4 => abi with { VariantBytes = 12 },
        5 => abi with { LittleEndian = false },
        6 => abi with { UnionAtZero = false },
        _ => throw new ArgumentOutOfRangeException(nameof(defect))
    };

    private static CrashEventRead Read(FakeApi api, CrashEventSubject? expected = null) =>
        CrashEventValues.Read(api, OwnedEvent, expected ?? Subject);

    private static void AssertFault(
        CrashEventRead result, CrashEventFailureKind kind, CrashEventReason reason)
    {
        var failure = Assert.IsType<CrashEventFailure>(result);
        Assert.Equal(new CrashEventFault(kind, reason), Assert.Single(failure.Faults));
    }

    private static void AssertNoDetails(FakeApi api)
    {
        Assert.DoesNotContain(Call.CreateDetails, api.Calls);
        Assert.DoesNotContain(Call.ProbeDetails, api.Calls);
        Assert.DoesNotContain(Call.RenderDetails, api.Calls);
        Assert.DoesNotContain(Call.SnapshotDetails, api.Calls);
        api.AssertSettled();
    }

    private static byte[] Identity(
        ulong birth = Birth, string path = Image, uint pid = uint.MaxValue)
    {
        var bytes = BufferFor([path]);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, pid);
        SetType(bytes, 0, 20);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), birth);
        SetType(bytes, 1, 21);
        _ = PutString(bytes, 2, path, 48);
        return bytes;
    }

    private static byte[] Details(
        string code = "80131623", string module = "module.dll",
        string path = @"C:\owned\module.dll")
    {
        var texts = new[] { code, module, path };
        var bytes = BufferFor(texts);
        var offset = 48;
        foreach (var (text, index) in texts.Select((text, index) => (text, index)))
            offset = PutString(bytes, index, text, offset);
        return bytes;
    }

    private static byte[] BufferFor(IEnumerable<string> texts) =>
        new byte[48 + texts.Sum(text => Encoding.Unicode.GetByteCount(text) + 2)];

    private static int PutString(byte[] bytes, int index, string text, int offset)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(index * 16),
            Address + (ulong)offset);
        SetType(bytes, index, 1);
        var encoded = Encoding.Unicode.GetBytes(text);
        encoded.CopyTo(bytes, offset);
        return offset + encoded.Length + 2;
    }

    private static void SetType(byte[] bytes, int index, uint type) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 16 + 12), type);

    private static Call Step(CrashEventSelection selection, Call identity, Call details) =>
        selection == CrashEventSelection.Identity ? identity : details;

    private sealed class FakeApi(byte[]? identity = null, byte[]? details = null)
        : ICrashEventApi
    {
        private readonly byte[] _identity = identity ?? Identity();
        private readonly byte[] _details = details ?? Details();
        private readonly List<Call> _calls = [];
        private readonly List<nint> _created = [];
        private readonly List<nint> _closeAttempts = [];
        private readonly List<FakeAllocation> _allocations = [];
        private CrashEventSelection _selection;
        private int _contexts;

        public CrashEventAbi Abi { get; init; } = CrashEventAbi.X64;
        internal IReadOnlyList<Call> Calls => _calls;
        internal IReadOnlyList<Call> Failures { get; init; } = [];
        internal Call? UnexpectedAt { get; init; }
        internal uint? ProbeSize { get; init; }
        internal uint? UsedBytes { get; init; }
        internal uint PropertyCount { get; init; } = 3;
        internal int CapacityAdjustment { get; init; }
        internal int SnapshotAdjustment { get; init; }
        internal ulong AllocationAddress { get; init; } = Address;

        public nint CreateContext(IReadOnlyList<string> valuePaths)
        {
            _selection = _contexts++ == 0
                ? CrashEventSelection.Identity : CrashEventSelection.Details;
            Assert.Equal<string>(CrashEventValues.ValuePaths(_selection), valuePaths);
            if (Hit(Step(_selection, Call.CreateIdentity, Call.CreateDetails)))
                return 0;
            var handle = (nint)_contexts;
            _created.Add(handle);
            return handle;
        }

        public CrashEventRenderCall Render(
            nint context, nint borrowedEvent, ICrashEventAllocation? allocation)
        {
            Assert.Equal(OwnedEvent, borrowedEvent);
            Assert.Contains(context, _created);
            var selection = context == 1
                ? CrashEventSelection.Identity : CrashEventSelection.Details;
            var bytes = selection == CrashEventSelection.Identity ? _identity : _details;
            if (allocation is null)
                return Probe(selection, bytes.Length);
            if (Hit(Step(selection, Call.RenderIdentity, Call.RenderDetails)))
                return new(false, 5, 0, 0);
            return new(true, 0, UsedBytes ?? (uint)bytes.Length, PropertyCount);
        }

        private CrashEventRenderCall Probe(CrashEventSelection selection, int length)
        {
            if (Hit(Step(selection, Call.ProbeIdentity, Call.ProbeDetails)))
                return new(false, 5, 0, 0);
            // Failed-probe property count is deliberately not a usable count.
            return new(false, 122, ProbeSize ?? (uint)length, uint.MaxValue);
        }

        public ICrashEventAllocation Allocate(int capacity)
        {
            _ = Hit(Step(_selection, Call.AllocateIdentity, Call.AllocateDetails));
            var bytes = _selection == CrashEventSelection.Identity ? _identity : _details;
            var allocation = new FakeAllocation(
                this, _selection, bytes, capacity + CapacityAdjustment);
            _allocations.Add(allocation);
            return allocation;
        }

        public bool Close(nint context)
        {
            Assert.NotEqual(OwnedEvent, context);
            Assert.Contains(context, _created);
            _closeAttempts.Add(context);
            return !Hit(context == 1 ? Call.CloseIdentity : Call.CloseDetails);
        }

        private bool Hit(Call call)
        {
            _calls.Add(call);
            if (UnexpectedAt == call)
                throw new ArithmeticException("Synthetic capability failure.");
            return Failures.Contains(call);
        }

        internal void AssertSettled()
        {
            Assert.All(_allocations, allocation => Assert.True(allocation.Disposed));
            Assert.Equal<nint>(_created.Order(), _closeAttempts.Order());
            Assert.DoesNotContain(OwnedEvent, _closeAttempts);
        }

        private sealed class FakeAllocation : ICrashEventAllocation
        {
            private readonly FakeApi _owner;
            private readonly CrashEventSelection _selection;
            private readonly byte[] _bytes;

            internal FakeAllocation(
                FakeApi owner, CrashEventSelection selection, byte[] bytes, int capacity)
            {
                _owner = owner;
                _selection = selection;
                Capacity = capacity;
                _bytes = new byte[Math.Max(capacity, bytes.Length) + 1];
                bytes.CopyTo(_bytes, 0);
            }

            public ulong Address => _owner.AllocationAddress;
            public int Capacity { get; }
            internal bool Disposed { get; private set; }

            public ReadOnlyMemory<byte> Snapshot(int usedBytes)
            {
                Assert.False(Disposed);
                if (_owner.Hit(Step(_selection, Call.SnapshotIdentity, Call.SnapshotDetails)))
                    throw CrashEventValues.Refuse(CrashEventReason.BufferSize);
                return _bytes.AsMemory(0, usedBytes + _owner.SnapshotAdjustment);
            }

            public void Dispose()
            {
                Assert.False(Disposed);
                Disposed = true;
                try
                {
                    if (_owner.Hit(Step(_selection, Call.FreeIdentity, Call.FreeDetails)))
                        throw CrashEventValues.Refuse(CrashEventReason.BufferRelease);
                }
                finally { Array.Clear(_bytes); }
            }
        }
    }
}
