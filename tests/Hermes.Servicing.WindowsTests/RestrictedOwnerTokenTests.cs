using System.ComponentModel;
using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing.Acceptance;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Hermes.Servicing.WindowsTests;

[SupportedOSPlatform("windows")]
public sealed class RestrictedOwnerTokenTests(ITestOutputHelper output)
{
    [Fact]
    public void Factory_transfers_only_the_verified_filtered_token()
    {
        var fake = new FakeTokens();
        var token = RestrictedOwnerToken.Create(fake);
        try
        {
            Assert.Equal(5u, (uint)fake.Flags);
            Assert.Equal(1, fake.IntegritySets);
            Assert.All(fake.Sources, source => Assert.True(source.IsClosed));
            Assert.False(token.IsClosed);
            Assert.Equal(RestrictedOwnerTokenFacts.Required, token.Verify());
        }
        finally
        {
            token.Dispose();
        }
        Assert.True(token.IsClosed);
        Assert.Equal(1, fake.Candidate.Closes);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(1, -1)]
    [InlineData(2, 1)]
    [InlineData(2, -1)]
    [InlineData(3, 12288)]
    [InlineData(3, 4096)]
    [InlineData(3, -1)]
    [InlineData(4, 1)]
    [InlineData(4, -1)]
    [InlineData(5, 0)]
    [InlineData(5, -1)]
    [InlineData(6, 0)]
    [InlineData(6, -1)]
    [InlineData(7, 0)]
    [InlineData(7, -1)]
    public void Wrong_or_unknown_token_predicates_fail_closed(int predicate, int value)
    {
        var fake = new FakeTokens(facts: Changed(predicate, value));
        var error = Assert.Throws<RestrictedOwnerTokenVerificationException>(
            () => RestrictedOwnerToken.Create(fake));
        Assert.Equal(13, error.NativeErrorCode);
        Assert.Equal(1 << (predicate - 1), error.FailedPredicates);
        Assert.True(fake.Candidate.IsClosed);
        Assert.All(fake.Sources, source => Assert.True(source.IsClosed));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public void Only_observed_thread_absence_is_accepted(int point, int state)
    {
        var fake = new FakeTokens(threadPoint: point, threadState: (MsiContextRead)state);
        var error = Assert.Throws<Win32Exception>(() => RestrictedOwnerToken.Create(fake));
        Assert.Equal(13, error.NativeErrorCode);
        Assert.Equal(point == 1, fake.Candidate.IsClosed);
        Assert.All(fake.Sources, source => Assert.True(source.IsClosed));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void Native_failures_propagate_without_fallback(int point)
    {
        var fault = new Win32Exception(123, "Synthetic numeric failure.");
        var fake = new FakeTokens(failurePoint: point, fault: fault);
        var error = Assert.Throws<Win32Exception>(() => RestrictedOwnerToken.Create(fake));
        Assert.Same(fault, error);
        Assert.Equal(123, error.NativeErrorCode);
        Assert.Equal(point >= 5, fake.Candidate.IsClosed);
        Assert.All(fake.Sources, source => Assert.True(source.IsClosed));
    }

    [Fact]
    public void Unexpected_program_faults_are_not_reclassified_or_swallowed()
    {
        var fault = new InvalidOperationException("Synthetic program fault.");
        var fake = new FakeTokens(failurePoint: 6, fault: fault);
        var error = Assert.Throws<InvalidOperationException>(
            () => RestrictedOwnerToken.Create(fake));
        Assert.Same(fault, error);
        Assert.True(fake.Candidate.IsClosed);
        Assert.All(fake.Sources, source => Assert.True(source.IsClosed));
    }

    [Fact]
    public void Pre_use_verification_requeries_facts_and_thread_absence()
    {
        var fake = new FakeTokens();
        using var token = RestrictedOwnerToken.Create(fake);
        Assert.Equal(RestrictedOwnerTokenFacts.Required, token.Verify());
        fake.ChangeFacts(Changed(6, 0));
        Assert.Throws<RestrictedOwnerTokenVerificationException>(() => token.Verify());
        fake.ChangeFacts(RestrictedOwnerTokenFacts.Required);
        fake.RejectNextThreadRead(MsiContextRead.Failed);
        var error = Assert.Throws<Win32Exception>(() => token.Verify());
        Assert.Equal(13, error.NativeErrorCode);
        Assert.False(token.IsClosed);
        Assert.All(fake.Sources, source => Assert.True(source.IsClosed));
    }

    [Fact]
    public void Disposal_is_idempotent_and_closed_use_never_reaches_the_capability()
    {
        var fake = new FakeTokens();
        var token = RestrictedOwnerToken.Create(fake);
        token.Dispose();
        token.Dispose();
        var calls = fake.Calls;
        var error = Assert.Throws<Win32Exception>(() => token.Verify());
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Equal(calls, fake.Calls);
        Assert.Equal(1, fake.Candidate.Closes);
        Assert.True(token.IsClosed);
    }

    [Fact]
    public void Actual_high_default_unlinked_owner_filters_without_parent_changes()
    {
        try
        {
            RequireGenuineProof();
        }
        catch (Win32Exception error)
        {
            output.WriteLine(
                $"HERMES_RESTRICTED_TOKEN_TEST v=1 observed=0 error={error.NativeErrorCode}");
            throw new XunitException(
                $"Restricted token numeric stop error={error.NativeErrorCode}.");
        }
    }

    private void RequireGenuineProof()
    {
        var api = WindowsRestrictedOwnerTokens.Instance;
        api.RequireDisposableJob();
        var before = (Context: ReadParent(), Stamp: api.ReadCurrentStamp());
        var source = IsActualSource(before.Context) ? 1 : 0;
        var proof = source == 1 ? MeasureNative() : new NativeProof(0, 0, 0, 0, 0, 13);
        var parent = before.Context == ReadParent() &&
            before.Stamp == api.ReadCurrentStamp() ? 1 : 0;
        var line = FormattableString.Invariant(
            $"HERMES_RESTRICTED_TOKEN_TEST v=1 source={source} observed={proof.Observed} failed={proof.Failed} filtered={proof.Filtered} parent={parent} disposed={proof.Disposed} closed={proof.ClosedUseRefused} error={proof.Error}");
        output.WriteLine(line);
        Assert.True(source == 1 && proof.Observed == 1 && proof.Failed == 0 &&
            proof.Filtered == 1 && parent == 1 && proof.Disposed == 1 &&
            proof.ClosedUseRefused == 1 && proof.Error == 0, line);
    }

    private static NativeProof MeasureNative()
    {
        RestrictedOwnerToken? token = null;
        var result = new NativeProof(0, 0, 0, 0, 0, 0);
        try
        {
            token = RestrictedOwnerToken.Create(WindowsRestrictedOwnerTokens.Instance);
            _ = token.Verify();
            result = new(1, 0, 1, 0, 0, 0);
        }
        catch (Win32Exception error)
        {
            result = NativeFailure(error);
        }
        finally
        {
            token?.Dispose();
        }
        return result with { Disposed = token is { IsClosed: true } ? 1 : 0,
            ClosedUseRefused = ClosedUseRefused(token) };
    }

    private static NativeProof NativeFailure(Win32Exception error) => error switch
    {
        RestrictedOwnerTokenVerificationException refused =>
            new(1, refused.FailedPredicates, 0, 0, 0, error.NativeErrorCode),
        _ => new(0, 0, 0, 0, 0, error.NativeErrorCode)
    };

    private static int ClosedUseRefused(RestrictedOwnerToken? token)
    {
        if (token is not { } owned)
            return 0;
        try
        {
            _ = owned.Verify();
            return 0;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 6)
        {
            return 1;
        }
    }

    private static MsiCallContextObservation ReadParent() =>
        MsiCallContextObservation.Read(WindowsMsiCallContextTokens.Instance);

    private static bool IsActualSource(MsiCallContextObservation value) => value is
    {
        Process: MsiContextRead.Observed,
        Elevated: 1,
        ElevationType: 1,
        Integrity: 12288,
        Admin: 1,
        Thread: MsiContextRead.Absent,
        Linked: MsiContextRead.Absent,
        SameOwner: -1,
        LinkedElevated: -1
    };

    private static RestrictedOwnerTokenFacts Changed(int predicate, int value) =>
        predicate switch
        {
            1 => RestrictedOwnerTokenFacts.Required with { TokenType = value },
            2 => RestrictedOwnerTokenFacts.Required with { Elevated = value },
            3 => RestrictedOwnerTokenFacts.Required with { Integrity = value },
            4 => RestrictedOwnerTokenFacts.Required with { AdminEnabled = value },
            5 => RestrictedOwnerTokenFacts.Required with { AdminDenyOnly = value },
            6 => RestrictedOwnerTokenFacts.Required with { SameOwner = value },
            7 => RestrictedOwnerTokenFacts.Required with { SameSession = value },
            _ => throw new ArgumentOutOfRangeException(nameof(predicate))
        };

    private sealed record NativeProof(
        int Observed, int Failed, int Filtered, int Disposed,
        int ClosedUseRefused, int Error);

    private abstract class FakeToken : IDisposable
    {
        private int _closes;
        public bool IsClosed => _closes != 0;
        internal int Closes => _closes;

        public void Dispose()
        {
            if (!IsClosed)
                _closes++;
        }
    }

    private sealed class FakeSourceToken : FakeToken, IAcceptanceSourceToken { }
    private sealed class FakeFilteredToken : FakeToken, IAcceptanceRestrictedToken { }

    private sealed class FakeTokens(
        int failurePoint = 0,
        Exception? fault = null,
        RestrictedOwnerTokenFacts? facts = null,
        int threadPoint = -1,
        MsiContextRead threadState = MsiContextRead.Absent) : IRestrictedOwnerTokens
    {
        private readonly Exception _fault = fault ??
            new Win32Exception(123, "Synthetic numeric failure.");
        private readonly List<FakeSourceToken> _sources = [];
        private RestrictedOwnerTokenFacts _facts = facts ?? RestrictedOwnerTokenFacts.Required;
        private int _threadReads;
        private int _rejectAt = threadPoint;
        private MsiContextRead _rejectedThread = threadState;
        private int _calls;
        private int _integritySets;
        private AcceptanceTokenFilter _flags;

        internal FakeFilteredToken Candidate { get; } = new();
        internal IReadOnlyList<FakeSourceToken> Sources => _sources;
        internal int Calls => _calls;
        internal int IntegritySets => _integritySets;
        internal AcceptanceTokenFilter Flags => _flags;

        public void RequireDisposableJob() => At(1);

        public MsiContextRead ReadThreadToken()
        {
            var point = _threadReads++;
            At(point == 0 ? 2 : 7);
            return point == _rejectAt ? _rejectedThread : MsiContextRead.Absent;
        }

        public IAcceptanceSourceToken OpenCurrentPrimary()
        {
            At(3);
            var source = new FakeSourceToken();
            _sources.Add(source);
            return source;
        }

        public IAcceptanceRestrictedToken Filter(
            IAcceptanceSourceToken source, AcceptanceTokenFilter flags)
        {
            At(4);
            Assert.Same(_sources[^1], source);
            Assert.False(source.IsClosed);
            _flags = flags;
            return Candidate;
        }

        public void SetMediumIntegrity(IAcceptanceRestrictedToken token)
        {
            At(5);
            Assert.Same(Candidate, token);
            _integritySets++;
        }

        public RestrictedOwnerTokenFacts ReadFacts(
            IAcceptanceRestrictedToken token, IAcceptanceSourceToken source)
        {
            At(6);
            Assert.Same(Candidate, token);
            Assert.Same(_sources[^1], source);
            Assert.False(token.IsClosed);
            Assert.False(source.IsClosed);
            return _facts;
        }

        internal void ChangeFacts(RestrictedOwnerTokenFacts value) => _facts = value;

        internal void RejectNextThreadRead(MsiContextRead value)
        {
            _rejectAt = _threadReads;
            _rejectedThread = value;
        }

        private void At(int point)
        {
            _calls++;
            if (failurePoint == point)
                throw _fault;
        }
    }
}
