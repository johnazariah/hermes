using System.ComponentModel;
using System.Security.AccessControl;
using Hermes.Servicing.Acceptance;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Hermes.Servicing.WindowsTests;

public sealed partial class RestrictedOwnerChildTests
{
    private static readonly AcceptanceChildObject[] ChildAccessTargets =
    [
        AcceptanceChildObject.Process,
        AcceptanceChildObject.Thread,
        AcceptanceChildObject.Token
    ];

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
        Assert.Equal(AcceptanceChildObjectAccess.Unknown(
            AcceptanceChildObject.Token), result.Access.Token);
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
        RequireChildInventory(result.Token, AcceptanceChildObject.Token);
        RequireInventoryCallOrder(fake);
        RequireInventoryQuerySizes(fake);
    }

    private static void RequireInventoryCallOrder(FakeChildAccessScope fake)
    {
        Assert.Equal(new[]
        {
            AuthzPoint.Resource, AuthzPoint.Context,
            AuthzPoint.Query, AuthzPoint.Query, AuthzPoint.Check,
            AuthzPoint.Query, AuthzPoint.Query,
            AuthzPoint.Query, AuthzPoint.Query, AuthzPoint.Check,
            AuthzPoint.Query, AuthzPoint.Query,
            AuthzPoint.Query, AuthzPoint.Query, AuthzPoint.Check,
            AuthzPoint.Query, AuthzPoint.Query,
            AuthzPoint.CloseContext, AuthzPoint.CloseResource
        }, fake.Calls);
        Assert.Equal(ChildAccessTargets.SelectMany(target => new[]
        {
            (target, 7u), (target, 7u), (target, 16u), (target, 16u)
        }), fake.QueryCalls);
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
        foreach (var target in ChildAccessTargets)
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
        Assert.Equal(expected, result.Token.Authz);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Process.Label.State);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Thread.Label.State);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Token.Label.State);
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
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(expected), result.Token.Authz);
        Assert.Equal(second ? 2 : 1, fake.Sizes(AcceptanceChildObject.Process, 7).Count);
        Assert.Equal(second ? 2 : 1, fake.Sizes(AcceptanceChildObject.Token, 7).Count);
        Assert.DoesNotContain(AuthzPoint.Check, fake.Calls);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Process.Label.State);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Token.Label.State);
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
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(50), result.Token.Authz);
        Assert.DoesNotContain(AuthzPoint.Check, fake.Calls);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Thread.Label.State);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Token.Label.State);
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

    [Fact]
    public void Actual_primary_token_inventory_requests_only_query_and_read_control()
    {
        Assert.Equal(0x00020008u, AcceptanceChildAccess.PrimaryTokenAccess);
        Assert.Equal(new[] { 0, 1, 2, 3 },
            Enum.GetValues<AcceptanceChildObject>().Select(value => (int)value));
        Assert.Equal(1, (int)AcceptanceChildSubject.ActualTokenVerified);
    }

    [Fact]
    public void Unprovided_token_inventory_is_unknown_even_when_other_objects_were_observed()
    {
        var observed = AcceptanceChildAccess.Read(new FakeChildAccessScope());
        var partial = new AcceptanceChildAccessResult(
            observed.Process, observed.Thread, observed.ResourcesClosed);
        RequireChildInventory(partial.Process, AcceptanceChildObject.Process);
        RequireChildInventory(partial.Thread, AcceptanceChildObject.Thread);
        Assert.Equal(AcceptanceChildObjectAccess.Unknown(
            AcceptanceChildObject.Token), partial.Token);
    }

    [Theory]
    [InlineData(1, 48u, 0, false, 13)]
    [InlineData(0, 19u, 122, false, 13)]
    [InlineData(0, 65537u, 122, false, 13)]
    [InlineData(1, 1u, 0, true, 13)]
    [InlineData(0, uint.MaxValue, 5, true, 5)]
    public void Actual_token_DACL_query_failure_keeps_other_objects_and_token_label_independent(
        int succeeded, uint bytes, int error, bool second, int expected)
    {
        var reply = new AcceptanceLabelQueryReply(succeeded, bytes, error);
        var fake = new FakeChildAccessScope();
        fake.SetQuery(AcceptanceChildObject.Token, 7, new FakeMandatoryLabelQuery(
            SyntheticDesktopDescriptor(),
            measure: second ? null : reply, read: second ? reply : null));
        var result = AcceptanceChildAccess.Read(fake);
        RequireChildInventory(result.Process, AcceptanceChildObject.Process);
        RequireChildInventory(result.Thread, AcceptanceChildObject.Thread);
        Assert.Equal(AcceptanceAuthzObservation.Unobserved(expected), result.Token.Authz);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Token.Label.State);
        Assert.Equal(second ? 2 : 1, fake.Sizes(AcceptanceChildObject.Token, 7).Count);
        Assert.Equal(second ? 12 : 11, fake.QueryCalls.Count);
        Assert.Equal(2, fake.Calls.Count(point => point == AuthzPoint.Check));
        Assert.True(result.ResourcesClosed && fake.IsClosed);
    }

    [Theory]
    [InlineData(0, 0u, 50, false, 3, 50)]
    [InlineData(0, uint.MaxValue, 120, false, 3, 120)]
    [InlineData(0, 0u, 5, false, 4, 5)]
    [InlineData(0, 0u, 1314, false, 4, 1314)]
    [InlineData(0, 65537u, 122, false, 5, 13)]
    [InlineData(1, 48u, 0, false, 5, 13)]
    [InlineData(0, uint.MaxValue, 5, true, 4, 5)]
    [InlineData(1, 1u, 0, true, 5, 13)]
    public void Actual_token_LABEL_query_classifies_transport_without_inventing_a_label(
        int succeeded, uint bytes, int error, bool second, int state, int expected)
    {
        var reply = new AcceptanceLabelQueryReply(succeeded, bytes, error);
        var fake = new FakeChildAccessScope();
        fake.SetQuery(AcceptanceChildObject.Token, 16, new FakeMandatoryLabelQuery(
            SyntheticMandatoryLabel(),
            measure: second ? null : reply, read: second ? reply : null));
        var result = AcceptanceChildAccess.Read(fake);
        RequireChildInventory(result.Process, AcceptanceChildObject.Process);
        RequireChildInventory(result.Thread, AcceptanceChildObject.Thread);
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            (AcceptanceMandatoryLabelState)state, -1, -1, -1, expected), result.Token.Label);
        Assert.Equal(AcceptanceAuthzState.Granted, result.Token.Authz.State);
        Assert.Equal(second ? new[] { 0, 48 } : new[] { 0 },
            fake.Sizes(AcceptanceChildObject.Token, 16));
        Assert.Equal(second ? 12 : 11, fake.QueryCalls.Count);
        Assert.Equal(3, fake.Calls.Count(point => point == AuthzPoint.Check));
        Assert.True(result.ResourcesClosed && fake.IsClosed);
    }

    [Theory]
    [InlineData(0, 50, 0u, uint.MaxValue, 3, 50)]
    [InlineData(1, 1314, 0u, 0x00020008u, 1, 0)]
    [InlineData(1, 1314, 5u, 0u, 2, 0)]
    [InlineData(1, 1314, 0u, 0x80000000u, 3, 50)]
    [InlineData(1, 1314, 87u, uint.MaxValue, 3, 87)]
    public void Actual_token_Authz_reply_is_not_replaced_by_other_object_results(
        int succeeded, int error, uint replyError, uint mask, int state, int expected)
    {
        var fake = new FakeChildAccessScope(
            tokenReply: new AcceptanceAuthzReply(succeeded, error, replyError, mask));
        var result = AcceptanceChildAccess.Read(fake);
        RequireChildInventory(result.Process, AcceptanceChildObject.Process);
        RequireChildInventory(result.Thread, AcceptanceChildObject.Thread);
        Assert.Equal(new AcceptanceAuthzObservation(
            (AcceptanceAuthzState)state, expected,
            succeeded is 0 ? -1L : replyError, state is 1 ? mask : 0), result.Token.Authz);
        Assert.Equal(AcceptanceMandatoryLabelState.Present, result.Token.Label.State);
        Assert.Equal(3, fake.Calls.Count(point => point == AuthzPoint.Check));
        RequireInventoryQuerySizes(fake);
        Assert.True(result.ResourcesClosed && fake.IsClosed);
    }

    [Fact]
    public void Actual_token_object_label_is_not_inferred_from_subject_integrity()
    {
        var fake = new FakeChildAccessScope();
        fake.SetQuery(AcceptanceChildObject.Token, 16,
            new FakeMandatoryLabelQuery(SyntheticMandatoryLabel(12288, 3, 16)));
        var result = AcceptanceChildAccess.Read(fake);
        RequireChildInventory(result.Process, AcceptanceChildObject.Process);
        RequireChildInventory(result.Thread, AcceptanceChildObject.Thread);
        Assert.Equal(AcceptanceChildSubject.ActualTokenVerified, result.Token.Subject);
        Assert.Equal(AcceptanceAuthzState.Granted, result.Token.Authz.State);
        Assert.Equal(new AcceptanceMandatoryLabelObservation(
            AcceptanceMandatoryLabelState.Present, 12288, 3, 16, 0), result.Token.Label);
        RequireInventoryQuerySizes(fake);
        Assert.True(result.ResourcesClosed && fake.IsClosed);
    }

    [Fact]
    public void Actual_token_queries_accept_the_shared_maximum_buffer_without_extra_calls()
    {
        var dacl = new byte[AcceptanceDesktopDacl.DescriptorLimit];
        var label = new byte[AcceptanceDesktopDacl.DescriptorLimit];
        SyntheticDesktopDescriptor().CopyTo(dacl, 0);
        SyntheticMandatoryLabel().CopyTo(label, 0);
        var fake = new FakeChildAccessScope();
        fake.SetQuery(AcceptanceChildObject.Token, 7, new FakeMandatoryLabelQuery(dacl));
        fake.SetQuery(AcceptanceChildObject.Token, 16, new FakeMandatoryLabelQuery(label));
        var result = AcceptanceChildAccess.Read(fake);
        RequireChildInventory(result.Process, AcceptanceChildObject.Process);
        RequireChildInventory(result.Thread, AcceptanceChildObject.Thread);
        RequireChildInventory(result.Token, AcceptanceChildObject.Token);
        Assert.Equal(new[] { 0, 65536 }, fake.Sizes(AcceptanceChildObject.Token, 7));
        Assert.Equal(new[] { 0, 65536 }, fake.Sizes(AcceptanceChildObject.Token, 16));
        Assert.Equal(12, fake.QueryCalls.Count);
        Assert.True(result.ResourcesClosed && fake.IsClosed);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Every_PowerShell_arm_emits_token_inventory_before_the_failed_exit_assertion(int arm)
    {
        var captured = new ChildAccessOutput();
        var tests = new RestrictedOwnerChildTests(captured);
        var fake = new FakeChildren
        {
            Access = AcceptanceChildAccess.Read(new FakeChildAccessScope()),
            ExitCode = 0xc0000142u
        };
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        var proof = tests.MeasurePowerShellArm(token, fake, arm);
        RequireTokenInventoryRows(captured.Rows, arm);
        Assert.Throws<EqualException>(() => RequirePowerShellArm(proof));
        AssertCompleteCleanup(fake);
    }

    private static void RequireTokenInventoryRows(IReadOnlyList<string> rows, int arm)
    {
        var actual = rows.Where(row => row.StartsWith(
            "HERMES_RESTRICTED_CHILD_AUTHZ ", StringComparison.Ordinal));
        var expected = ChildAccessTargets.Select(target =>
            $"HERMES_RESTRICTED_CHILD_AUTHZ v=1 arm={arm} object={(int)target} subject=1 descriptorScope=7 desired=33554432 micObserved=0 status=1 error=0 replyError=0 grantedMask=1179649 labelScope=16 labelState=1 rid=8192 policy=1 aceFlags=0 labelError=0 resourcesClosed=1");
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Actual_primary_read_control_open_failure_is_numeric_and_never_resumes()
    {
        var captured = new ChildAccessOutput();
        var tests = new RestrictedOwnerChildTests(captured);
        var fake = new FakeChildren();
        var fault = new Win32Exception(5);
        fake.Fail(ChildPoint.Facts, fault);
        using var token = RestrictedOwnerToken.Create(fake.Tokens);
        Assert.Same(fault, Assert.Throws<Win32Exception>(
            () => tests.MeasurePowerShellArm(token, fake, 5)));
        Assert.Contains(
            "HERMES_RESTRICTED_DESKTOP_AB v=1 arm=5 completed=0 error=5 failed=0 cleanupNotCompleted=0",
            captured.Rows);
        Assert.Equal(1, fake.Calls.Count(point => point == ChildPoint.Facts));
        Assert.DoesNotContain(ChildPoint.Inventory, fake.Calls);
        Assert.Equal(0, fake.Resumes);
        AssertCompleteCleanup(fake);
    }

    private sealed class ChildAccessOutput : ITestOutputHelper
    {
        private readonly List<string> _rows = [];
        internal IReadOnlyList<string> Rows => _rows;

        public void WriteLine(string message) => _rows.Add(message);

        public void WriteLine(string format, params object[] args) =>
            WriteLine(string.Format(
                System.Globalization.CultureInfo.InvariantCulture, format, args));
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
        private readonly List<(AcceptanceChildObject Target, uint Scope)> _queryCalls = [];
        private readonly Dictionary<AuthzPoint, Exception> _faults = [];
        private readonly AcceptanceAuthzReply _tokenReply;
        private bool _resource;
        private bool _context;
        private bool _disposed;
        private bool _completed;

        internal FakeChildAccessScope(
            byte[]? descriptor = null, AcceptanceLabelQueryReply? measure = null,
            AcceptanceLabelQueryReply? read = null, AcceptanceAuthzReply? tokenReply = null)
        {
            _tokenReply = tokenReply ?? new AcceptanceAuthzReply(1, 1314, 0, 0x00120001);
            uint[] scopes = [7, 16];
            _queries = ChildAccessTargets.SelectMany(target => scopes.Select(scope =>
                (Target: target, Scope: scope))).ToDictionary(pair => pair,
                pair => new FakeMandatoryLabelQuery(
                    pair.Scope == 7 ? descriptor ?? SyntheticDesktopDescriptor() : SyntheticMandatoryLabel(),
                    measure: pair.Scope == 7 ? measure : null,
                    read: pair.Scope == 7 ? read : null));
        }

        public bool IsClosed => _disposed && _completed;
        internal IReadOnlyList<AuthzPoint> Calls => _calls;
        internal IReadOnlyList<(AcceptanceChildObject Target, uint Scope)> QueryCalls => _queryCalls;
        internal IReadOnlyList<int> Sizes(AcceptanceChildObject target, uint scope) =>
            _queries[(target, scope)].Sizes;

        internal void SetQuery(
            AcceptanceChildObject target, uint information, FakeMandatoryLabelQuery query) =>
            _queries[(target, information)] = query;

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
            _queryCalls.Add((target, information));
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
            return _queryCalls[^1].Target is AcceptanceChildObject.Token
                ? _tokenReply : new(1, 1314, 0, 0x00120001);
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
