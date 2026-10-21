using System.ComponentModel;
using System.Security.AccessControl;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.WindowsTests;

public sealed partial class RestrictedOwnerChildTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Inventory_failure_never_resumes_and_preserves_cleanup_failures(bool cleanupFails)
    {
        var fake = new FakeChildren();
        var original = new InvalidOperationException("Synthetic inventory fault.");
        fake.Fail(ChildPoint.Inventory, original);
        if (cleanupFails)
            fake.Fail(ChildPoint.CloseJob, new Win32Exception(123));
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var error = Record.Exception(() => RestrictedOwnerChild.RunInert(token, fake));
        RequireInventoryFailure(error, original, cleanupFails);
        Assert.Equal(0, fake.Resumes);
        Assert.Equal(CleanupPoints, CleanupCalls(fake));
        Assert.Equal(!cleanupFails, fake.Child.IsClosed);
        Assert.Equal(1, fake.Calls.Count(point => point == ChildPoint.Inventory));
    }

    private static void RequireInventoryFailure(
        Exception? error, Exception original, bool cleanupFails)
    {
        if (!cleanupFails)
        {
            Assert.Same(original, error);
            return;
        }
        var aggregate = Assert.IsType<AggregateException>(error);
        Assert.Same(original, aggregate.InnerExceptions[0]);
        var cleanup = Assert.IsType<AcceptanceChildCleanupException>(
            aggregate.InnerExceptions[1]);
        Assert.Equal(123, Assert.IsType<Win32Exception>(
            Assert.Single(cleanup.InnerExceptions)).NativeErrorCode);
    }

    [Fact]
    public void Cancellation_at_inventory_is_observed_before_resume()
    {
        var fake = new FakeChildren();
        using var cancellation = new CancellationTokenSource();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        fake.Observe(point =>
        {
            if (point == ChildPoint.Inventory)
                cancellation.Cancel();
        });
        var error = Assert.Throws<OperationCanceledException>(() =>
            RestrictedOwnerChild.RunInert(token, fake, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    [Fact]
    public void Unprovided_inventory_is_unknown_and_closed_inventory_use_is_refused()
    {
        var fake = new FakeChildren();
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var result = RestrictedOwnerChild.RunInert(token, fake);
        Assert.Same(AcceptanceChildAccessResult.Unknown, result.Access);
        Assert.Equal(AcceptanceChildSubject.Unknown, result.Access.Process.Subject);
        Assert.Equal(AcceptanceAuthzState.Unknown, result.Access.Process.Authz.State);
        Assert.Equal(-1L, result.Access.Process.Authz.ReplyError);
        Assert.Equal(0u, result.Access.Process.Authz.GrantedMask);
        Assert.Equal(AcceptanceMandatoryLabelObservation.Unknown, result.Access.Thread.Label);
        Assert.False(result.Access.ResourcesClosed);
        var count = fake.Calls.Count;
        var error = Assert.Throws<Win32Exception>(() => fake.Child.ReadObjectAccess());
        Assert.Equal(6, error.NativeErrorCode);
        Assert.Equal(count, fake.Calls.Count);
    }

    [Fact]
    public void Child_inventory_native_boundary_uses_no_audit_maximum_allowed_and_separate_labels()
    {
        var fake = new FakeChildAccessScope();
        var result = AcceptanceChildAccess.Read(fake);
        Assert.True(result.ResourcesClosed && fake.IsClosed);
        RequireChildInventory(result.Process, AcceptanceChildObject.Process);
        RequireChildInventory(result.Thread, AcceptanceChildObject.Thread);
        Assert.Equal(new[]
        {
            AuthzPoint.Resource, AuthzPoint.Context,
            AuthzPoint.Query, AuthzPoint.Query, AuthzPoint.Check,
            AuthzPoint.Query, AuthzPoint.Query,
            AuthzPoint.Query, AuthzPoint.Query, AuthzPoint.Check,
            AuthzPoint.Query, AuthzPoint.Query,
            AuthzPoint.CloseContext, AuthzPoint.CloseResource
        }, fake.Calls);
        RequireInventoryQuerySizes(fake);
    }

    private static void RequireChildInventory(
        AcceptanceChildObjectAccess value, AcceptanceChildObject target)
    {
        Assert.Equal(target, value.Object);
        Assert.Equal(AcceptanceChildSubject.ActualTokenVerified, value.Subject);
        Assert.Equal(new AcceptanceAuthzObservation(
            AcceptanceAuthzState.Granted, 0, 0, 0x00120001), value.Authz);
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.Present, 8192, 1, 0, 0), value.Label);
    }

    private static void RequireInventoryQuerySizes(FakeChildAccessScope fake)
    {
        foreach (var target in new[] { AcceptanceChildObject.Process, AcceptanceChildObject.Thread })
        {
            Assert.Equal(new[] { 0, SyntheticDesktopDescriptor().Length }, fake.Sizes(target, 7));
            Assert.Equal(new[] { 0, 48 }, fake.Sizes(target, 16));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Authz_initialization_failure_is_unobserved_not_denied_and_labels_still_run(int point)
    {
        var fake = new FakeChildAccessScope();
        fake.Fail((AuthzPoint)point, new Win32Exception(5));
        var result = AcceptanceChildAccess.Read(fake);
        var expected = AcceptanceAuthzObservation.Unobserved(5);
        Assert.Equal(expected, result.Process.Authz);
        Assert.Equal(expected, result.Thread.Authz);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Process.Label.State);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Thread.Label.State);
        Assert.DoesNotContain(AuthzPoint.Check, fake.Calls);
        Assert.True(result.ResourcesClosed && fake.IsClosed);
        RequireInventoryQuerySizes(fake);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(50)]
    [InlineData(87)]
    [InlineData(1314)]
    public void Failed_Authz_API_ignores_poisoned_reply_outputs(int error)
    {
        var result = AcceptanceChildAccess.Decide(new(0, error, 0, uint.MaxValue));
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(error), result);
        Assert.Equal(-1L, result.ReplyError);
        Assert.Equal(0u, result.GrantedMask);
        Assert.NotEqual(AcceptanceAuthzState.Denied, result.State);
    }

    [Theory]
    [InlineData(0u, 0x00120001u, 1, 0)]
    [InlineData(5u, 0u, 2, 0)]
    [InlineData(0u, 0u, 3, 13)]
    [InlineData(5u, 1u, 3, 13)]
    [InlineData(1314u, 0u, 3, 1314)]
    [InlineData(87u, uint.MaxValue, 3, 87)]
    [InlineData(0u, 0x80000000u, 3, 50)]
    public void Authz_reply_error_is_distinct_from_API_failure(
        uint replyError, uint granted, int state, int error)
    {
        var result = AcceptanceChildAccess.Decide(new(1, 1314, replyError, granted));
        Assert.Equal((AcceptanceAuthzState)state, result.State);
        Assert.Equal(error, result.Error);
        Assert.Equal((long)replyError, result.ReplyError);
        Assert.Equal(state == 1 ? granted : 0u, result.GrantedMask);
    }

    [Theory]
    [InlineData(1, 48u, 0, false, 13)]
    [InlineData(0, 19u, 122, false, 13)]
    [InlineData(0, 65537u, 122, false, 13)]
    [InlineData(1, 1u, 0, true, 13)]
    [InlineData(0, uint.MaxValue, 5, true, 5)]
    public void Kernel_descriptor_transport_is_bounded_and_does_not_turn_failure_into_denial(
        int succeeded, uint bytes, int error, bool second, int expected)
    {
        var reply = new AcceptanceLabelQueryReply(succeeded, bytes, error);
        var fake = new FakeChildAccessScope(
            measure: second ? null : reply, read: second ? reply : null);
        var result = AcceptanceChildAccess.Read(fake);
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(expected), result.Process.Authz);
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(expected), result.Thread.Authz);
        Assert.Equal(second ? 2 : 1, fake.Sizes(AcceptanceChildObject.Process, 7).Count);
        Assert.DoesNotContain(AuthzPoint.Check, fake.Calls);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Process.Label.State);
        Assert.True(result.ResourcesClosed);
    }

    [Theory]
    [InlineData(0x80000000u)]
    [InlineData(0x40000000u)]
    [InlineData(0x20000000u)]
    [InlineData(0x10000000u)]
    public void Generic_kernel_ACE_masks_are_unsupported_without_guessing_a_mapping(uint mask)
    {
        var descriptor = new RawSecurityDescriptor($"O:SYG:SYD:(A;;0x{mask:X8};;;SY)");
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        var fake = new FakeChildAccessScope(bytes);
        var result = AcceptanceChildAccess.Read(fake);
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(50), result.Process.Authz);
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(50), result.Thread.Authz);
        Assert.DoesNotContain(AuthzPoint.Check, fake.Calls);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Thread.Label.State);
        Assert.True(result.ResourcesClosed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Unexpected_inventory_exceptions_propagate_and_preserve_owned_cleanup(int point)
    {
        var fake = new FakeChildAccessScope();
        var original = new InvalidOperationException("Synthetic native-boundary fault.");
        var cleanup = new Win32Exception(123);
        fake.Fail((AuthzPoint)point, original);
        fake.Fail(AuthzPoint.CloseResource, cleanup);
        var error = Record.Exception(() => AcceptanceChildAccess.Read(fake));
        RequireScopeFailure(error, original, cleanup, point);
        Assert.Equal(1, fake.Calls.Count(value => value == (AuthzPoint)point));
        Assert.Equal(point >= 3, fake.Calls.Contains(AuthzPoint.CloseContext));
        Assert.Equal(point >= 2, fake.Calls.Contains(AuthzPoint.CloseResource));
    }

    private static void RequireScopeFailure(
        Exception? error, Exception original, Exception cleanup, int point)
    {
        if (point == 1)
        {
            Assert.Same(original, error);
            return;
        }
        var aggregate = Assert.IsType<AggregateException>(error);
        Assert.Same(original, aggregate.InnerExceptions[0]);
        var incomplete = Assert.IsType<AcceptanceChildCleanupException>(
            aggregate.InnerExceptions[1]);
        Assert.Same(cleanup, Assert.Single(incomplete.InnerExceptions));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_Authz_cleanup_is_checked_and_other_owned_cleanup_is_attempted(int mask)
    {
        var fake = new FakeChildAccessScope();
        if ((mask & 1) != 0)
            fake.Fail(AuthzPoint.CloseContext, new Win32Exception(123));
        if ((mask & 2) != 0)
            fake.Fail(AuthzPoint.CloseResource, new Win32Exception(124));
        var error = Assert.Throws<AcceptanceChildCleanupException>(
            () => AcceptanceChildAccess.Read(fake));
        Assert.Equal((mask & 1) + ((mask >> 1) & 1), error.InnerExceptions.Count);
        Assert.Equal(new[] { AuthzPoint.CloseContext, AuthzPoint.CloseResource },
            fake.Calls.TakeLast(2));
        Assert.False(fake.IsClosed);
    }

    private enum AuthzPoint
    {
        Resource = 1,
        Context,
        Query,
        Check,
        CloseContext,
        CloseResource
    }

    private sealed class FakeChildAccessScope : IAcceptanceChildAccessScope
    {
        private readonly Dictionary<
            (AcceptanceChildObject Target, uint Scope), FakeMandatoryLabelQuery> _queries;
        private readonly List<AuthzPoint> _calls = [];
        private readonly Dictionary<AuthzPoint, Exception> _faults = [];
        private bool _resource;
        private bool _context;
        private bool _disposed;
        private bool _completed;

        internal FakeChildAccessScope(
            byte[]? descriptor = null, AcceptanceLabelQueryReply? measure = null,
            AcceptanceLabelQueryReply? read = null)
        {
            AcceptanceChildObject[] targets = [AcceptanceChildObject.Process, AcceptanceChildObject.Thread];
            uint[] scopes = [7, 16];
            _queries = targets.SelectMany(target => scopes.Select(scope =>
                (Target: target, Scope: scope))).ToDictionary(pair => pair,
                pair => new FakeMandatoryLabelQuery(
                    pair.Scope == 7 ? descriptor ?? SyntheticDesktopDescriptor() : SyntheticMandatoryLabel(),
                    measure: pair.Scope == 7 ? measure : null,
                    read: pair.Scope == 7 ? read : null));
        }

        public bool IsClosed => _disposed && _completed;
        internal IReadOnlyList<AuthzPoint> Calls => _calls;
        internal IReadOnlyList<int> Sizes(AcceptanceChildObject target, uint scope) =>
            _queries[(target, scope)].Sizes;

        internal void Fail(AuthzPoint point, Exception error) => _faults.Add(point, error);

        public void InitializeResourceManager(uint flags)
        {
            Assert.Equal(1u, flags);
            At(AuthzPoint.Resource);
            _resource = true;
        }

        public void InitializeContext(uint flags)
        {
            Assert.True(_resource);
            Assert.Equal(0u, flags);
            At(AuthzPoint.Context);
            _context = true;
        }

        public AcceptanceLabelQueryReply Query(
            AcceptanceChildObject target, uint information, Span<byte> buffer)
        {
            At(AuthzPoint.Query);
            return _queries[(target, information)].Query(buffer);
        }

        public AcceptanceAuthzReply Check(uint flags, uint desired, byte[] descriptor)
        {
            Assert.True(_context);
            Assert.Equal(0u, flags);
            Assert.Equal(0x02000000u, desired);
            Assert.Equal(AcceptanceDesktopDacl.NormalizeDescriptor(
                SyntheticDesktopDescriptor(), true, true), descriptor);
            At(AuthzPoint.Check);
            return new(1, 1314, 0, 0x00120001);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            AcceptanceChildCleanup.Complete(
                () => { if (_context) At(AuthzPoint.CloseContext); },
                () => { if (_resource) At(AuthzPoint.CloseResource); });
            _completed = true;
        }

        private void At(AuthzPoint point)
        {
            _calls.Add(point);
            if (_faults.GetValueOrDefault(point) is { } error)
                throw error;
        }
    }
}
