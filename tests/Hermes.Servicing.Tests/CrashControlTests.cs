using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Hermes.Servicing.Acceptance;
using Hermes.Servicing.Acceptance.Model;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class CrashControlTests
{
    private const uint CrashExit = 0x80131623;
    private static readonly CrashLimits Limits =
        new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));
    private static string Hash => new('a', 64);
    private static CrashIdentity Live => new(new ProcessKey(42, 100),
        @"C:\owned\Hermes.Servicing.Acceptance.exe",
        Hash, new string('b', 64), 0, 259);

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("c:/", true)]
    [InlineData(@"Z:\owned\child.exe", true)]
    [InlineData("z:/owned/child.exe", true)]
    [InlineData(@"\\server\share\child.exe", true)]
    [InlineData("//server/share/child.exe", true)]
    [InlineData(@"\/server\share", true)]
    [InlineData(@"/\server/share", true)]
    [InlineData(@"\\", true)]
    [InlineData("//", true)]
    [InlineData(@"\/", true)]
    [InlineData(@"/\", true)]
    [InlineData(@"\\server", true)]
    [InlineData(@"\\?\C:\owned\child.exe", true)]
    [InlineData(@"\\.\C:\owned\child.exe", true)]
    [InlineData(@"\??\C:\owned\child.exe", true)]
    [InlineData("//?/C:/owned/child.exe", true)]
    [InlineData("//./C:/owned/child.exe", true)]
    [InlineData("/??/C:/owned/child.exe", true)]
    [InlineData(@"\?", true)]
    [InlineData("/?", true)]
    [InlineData(@"\?relative", true)]
    [InlineData("/?relative", true)]
    [InlineData("", false)]
    [InlineData("C", false)]
    [InlineData(@"C:", false)]
    [InlineData(@"C:child.exe", false)]
    [InlineData(@"C: child.exe", false)]
    [InlineData(@"\child.exe", false)]
    [InlineData("/child.exe", false)]
    [InlineData(@"\", false)]
    [InlineData("/", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData(@"..\child.exe", false)]
    [InlineData("child.exe", false)]
    [InlineData(@"1:\child.exe", false)]
    [InlineData(@"=:\child.exe", false)]
    [InlineData(@"@:\child.exe", false)]
    [InlineData(@"[:\child.exe", false)]
    [InlineData(@"é:\child.exe", false)]
    [InlineData(@" C:\child.exe", false)]
    [InlineData("http://example", false)]
    public void Qualification_has_Windows_semantics_on_every_host(string path, bool qualified)
    {
        Assert.Equal(qualified, CrashWindowsPath.IsFullyQualified(path));
        var identity = Live with { ImagePath = path };
        if (qualified)
            identity.RequireLive();
        else
            Assert.Throws<InvalidDataException>(identity.RequireLive);
        if (OperatingSystem.IsWindows())
            Assert.Equal(Path.IsPathFullyQualified(path), CrashWindowsPath.IsFullyQualified(path));
    }

    [Fact]
    public void Qualification_is_structural_not_path_validity_or_canonicalization()
    {
        string[] paths = [@"C:\a\..\*.dll", "C:/bad\0name", @"\\", @"\?"];
        Assert.All(paths, path =>
        {
            Assert.True(CrashWindowsPath.IsFullyQualified(path));
            (Live with { ImagePath = path }).RequireLive();
            if (OperatingSystem.IsWindows())
                Assert.Equal(Path.IsPathFullyQualified(path),
                    CrashWindowsPath.IsFullyQualified(path));
        });
    }

    [Fact]
    public async Task Live_image_proof_survives_exit_without_a_postmortem_image_query()
    {
        var child = new Child();
        var source = new LiveImageSource(child);
        CrashObservation? retained = null;
        var result = await RunWithProof(child, source, (scope, _) =>
        {
            retained = scope;
            Assert.False(source.HandleClosed);
            Assert.Equal(CrashExit, scope.Recheck().ExitCode);
            return Task.CompletedTask;
        });
        Assert.Equal(0, result.ExitCode);
        Assert.Equal((2, 0, 5, 5),
            (source.ImageQueries, source.TerminalQueries, source.PinReads, source.SnapshotReads));
        Assert.True(source.HandleClosed);
        Assert.NotNull(retained);
        Assert.Throws<ObjectDisposedException>(() => retained.Recheck());
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Live_image_query_error_31_still_refuses_before_arm()
    {
        var child = new Child();
        var source = new LiveImageSource(child)
        {
            Image = _ => throw new System.ComponentModel.Win32Exception(31)
        };
        var result = await RunWithProof(child, source);
        Assert.Equal(CrashFault.Binding, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, child.Writes);
        Assert.Equal((1, 0, 0), (source.ImageQueries, source.TerminalQueries, source.PinReads));
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Successful_wait_without_live_image_proof_cannot_mint_terminal_identity()
    {
        var child = new Child();
        var source = new LiveImageSource(child);
        var owned = source.Wrap(child.Open());
        try
        {
            owned.Stop();
            await owned.Wait(CancellationToken.None);
            var failure = Assert.Throws<CrashExitException>(() => owned.ReadIdentity());
            Assert.Equal(CrashExitCheck.Lifetime, failure.Check);
            Assert.Equal((0, 0), (source.ImageQueries, source.PinReads));
        }
        finally { owned.Dispose(); }
    }

    [Fact]
    public async Task Terminal_snapshot_requires_completion_of_this_readers_wait()
    {
        var child = new Child();
        var source = new LiveImageSource(child);
        var owned = source.Wrap(child.Open());
        try
        {
            _ = owned.ReadIdentity();
            owned.Stop();
            Assert.Throws<InvalidDataException>(() => owned.ReadIdentity());
            await owned.Wait(CancellationToken.None);
            Assert.Equal(CrashExit, owned.ReadIdentity().ExitCode);
            Assert.Equal((1, 0, 2), (source.ImageQueries, source.TerminalQueries, source.PinReads));
        }
        finally { owned.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Faulted_or_cancelled_wait_cannot_authorize_terminal_identity(bool cancelled)
    {
        var child = new Child();
        var source = new LiveImageSource(child);
        var failed = cancelled
            ? Task.FromCanceled<uint>(new CancellationToken(true))
            : Task.FromException<uint>(new IOException());
        var owned = source.Wrap(child.Open() with { Wait = _ => failed });
        try
        {
            _ = owned.ReadIdentity();
            owned.Stop();
            Assert.Same(failed, owned.Wait(CancellationToken.None));
            await Assert.ThrowsAnyAsync<Exception>(() => failed);
            Assert.Throws<InvalidDataException>(() => owned.ReadIdentity());
            Assert.Equal((1, 0), (source.ImageQueries, source.TerminalQueries));
        }
        finally { owned.Dispose(); }
    }

    [Fact]
    public async Task Changed_loaded_image_before_arm_cannot_replace_live_proof()
    {
        var child = new Child();
        var source = new LiveImageSource(child)
        {
            Image = read => read == 1 ? Live.ImagePath : Live.ImagePath + ".other"
        };
        var result = await RunWithProof(child, source);
        Assert.Equal(CrashFault.Binding, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, child.Writes);
        Assert.False(result.Held);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Changed_image_or_source_pins_never_replace_proof(
        bool afterExit, bool changeImage)
    {
        var child = new Child();
        var reads = 0;
        var source = new LiveImageSource(child)
        {
            Pins = () => (afterExit ? child.Exited : ++reads > 1)
                ? ChangedPins(changeImage) : new(Live.ImageSha256, Live.SourceSha256)
        };
        var result = await RunWithProof(child, source);
        Assert.Equal(afterExit ? CrashFault.Exit : CrashFault.Binding, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(afterExit ? 1 : 0, child.Writes);
        Assert.False(result.Held);
        Assert.Equal((2, 0), (source.ImageQueries, source.TerminalQueries));
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Closed_publication_pins_refuse_terminal_identity()
    {
        var child = new Child();
        var source = new LiveImageSource(child) { PinsOpen = () => !child.Exited };
        var result = await RunWithProof(child, source);
        Assert.Equal(CrashFault.Exit, result.Error);
        Assert.Equal(CrashExitCheck.Pins, result.ExitCheck);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Held);
        Assert.True(result.Cleanup);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_different_handle_cannot_reuse_the_same_process_key(bool afterExit)
    {
        using var other = new SafeProcessHandle(new nint(43), ownsHandle: false);
        var child = new Child();
        var reads = 0;
        var source = new LiveImageSource(child)
        {
            HandleOverride = handle => (afterExit ? child.Exited : ++reads == 2) ? other : handle
        };
        var result = await RunWithProof(child, source);
        Assert.Equal(afterExit ? CrashFault.Exit : CrashFault.Binding, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(afterExit ? 1 : 0, child.Writes);
        Assert.False(result.Held);
        Assert.True(source.HandleClosed);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData(43u, 100L)]
    [InlineData(42u, 101L)]
    public async Task Terminal_pid_or_birth_change_cannot_reuse_live_image_proof(uint pid, long born)
    {
        var child = new Child
        {
            ExitedIdentity = () => Live with
                { Process = new(pid, born), ExitFileTime = 200, ExitCode = CrashExit }
        };
        var source = new LiveImageSource(child);
        var result = await RunWithProof(child, source);
        Assert.Equal(CrashFault.Exit, result.Error);
        Assert.Equal(CrashExitCheck.IdentityProcess, result.ExitCheck);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Held);
        Assert.Equal((2, 0), (source.ImageQueries, source.TerminalQueries));
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData((int)CrashExitCheck.ExitTimeOrder)]
    [InlineData((int)CrashExitCheck.ExitTimePresent)]
    [InlineData((int)CrashExitCheck.ExitCodeMatch)]
    public async Task Terminal_time_and_code_must_be_fresh_and_match_actual_wait(int check)
    {
        var child = new Child { ExitedIdentity = () => InvalidExit((CrashExitCheck)check) };
        var source = new LiveImageSource(child);
        var result = await RunWithProof(child, source);
        Assert.Equal(CrashFault.Exit, result.Error);
        Assert.Equal((CrashExitCheck)check, result.ExitCheck);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Held);
        Assert.Equal((2, 0), (source.ImageQueries, source.TerminalQueries));
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Closing_retained_handle_during_callback_refuses_postcheck()
    {
        var child = new Child();
        var source = new LiveImageSource(child);
        var result = await RunWithProof(child, source, (scope, cancellationToken) =>
        {
            _ = scope.Recheck();
            source.CloseHandle();
            return Task.CompletedTask;
        });
        Assert.Equal(CrashFault.Callback, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Held);
        Assert.True(result.Cleanup);
        Assert.Equal((2, 0), (source.ImageQueries, source.TerminalQueries));
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Source_pins_are_rechecked_after_actual_callback()
    {
        var changed = false;
        var child = new Child();
        var source = new LiveImageSource(child)
        {
            Pins = () => changed ? ChangedPins(false) : new(Live.ImageSha256, Live.SourceSha256)
        };
        var result = await RunWithProof(child, source, (scope, cancellationToken) =>
        {
            _ = scope.Recheck();
            changed = true;
            return Task.CompletedTask;
        });
        Assert.Equal(CrashFault.Callback, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Held);
        Assert.True(result.Cleanup);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData((int)CrashCleanup.Input)]
    [InlineData((int)CrashCleanup.Kill)]
    [InlineData((int)CrashCleanup.Output)]
    [InlineData((int)CrashCleanup.Error)]
    [InlineData((int)CrashCleanup.Dispose)]
    public async Task Live_image_policy_does_not_bypass_cleanup_failures(int value)
    {
        var failure = (CrashCleanup)value;
        var child = new Child { CleanupFailure = failure };
        var source = new LiveImageSource(child);
        var result = await RunWithProof(child, source);
        Assert.True(result.CleanupError.HasFlag(failure));
        Assert.False(result.Cleanup);
        Assert.Equal(1, result.ExitCode);
        Assert.True(source.HandleClosed);
        Assert.Equal(0, source.TerminalQueries);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData("bad", false, (int)CrashFault.Stdout, 0)]
    [InlineData(CrashControl.Ready, true, (int)CrashFault.Arm, 2)]
    public async Task Live_image_policy_preserves_readiness_and_arm_refusals(
        string ready, bool writeFailure, int fault, int queries)
    {
        var child = new Child(ready) { WriteFailure = writeFailure };
        var source = new LiveImageSource(child);
        var result = await RunWithProof(child, source);
        Assert.Equal((CrashFault)fault, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Armed);
        Assert.Equal((queries, 0), (source.ImageQueries, source.TerminalQueries));
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Live_image_policy_does_not_bypass_stderr_drain_failure()
    {
        var child = new Child { ErrorBytes = CrashControl.MaximumStderrBytes + 1 };
        var source = new LiveImageSource(child);
        var result = await RunWithProof(child, source);
        Assert.Equal(CrashFault.Stderr, result.Error);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(0, child.Writes);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Live_image_policy_keeps_success_report_at_28_fields_and_native_acceptance_zero()
    {
        var child = new Child();
        var result = await RunWithProof(child, new LiveImageSource(child));
        using var document = JsonDocument.Parse(CrashReport.Encode(result));
        var report = document.RootElement;
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(28, report.EnumerateObject().Count());
        Assert.Equal(0, report.GetProperty("status").GetInt32());
        Assert.Equal(2148734499u, report.GetProperty("exit").GetUInt32());
        Assert.Equal(0, report.GetProperty("originalGatePassed").GetInt32());
        Assert.Equal(0, report.GetProperty("nativeAcceptance").GetInt32());
        Assert.Equal(Live.ImageSha256, report.GetProperty("imageSha256").GetString());
        Assert.Equal(Live.SourceSha256, report.GetProperty("sourceSha256").GetString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("HERMES_CRASH_ARM v=1\r\n")]
    [InlineData("HERMES_CRASH_ARM v=1\nx")]
    [InlineData("HERMES_CRASH_ARM v=2\n")]
    [InlineData("\uFEFFHERMES_CRASH_ARM v=1\n")]
    public async Task Invalid_arm_never_configures_or_crashes(string input)
    {
        var calls = new List<string>();
        var result = await CrashChild.RunAsync(ChildEffects(input, calls), CancellationToken.None);
        Assert.Equal(CrashControl.InputRefused, result);
        Assert.Equal<string>(["ready"], calls);
    }

    [Fact]
    public async Task Valid_arm_requires_EOF_and_exact_process_WER_readback()
    {
        var calls = new List<string>();
        var result = await CrashChild.RunAsync(
            ChildEffects(CrashControl.Arm, calls), CancellationToken.None);
        Assert.Equal(CrashControl.CrashReturned, result);
        Assert.Equal<string>(["ready", "set:227", "get", "crash"], calls);
        Assert.Equal(227u, (uint)CrashControl.WerPolicy);
    }

    [Theory]
    [InlineData(-1, 0, 227u)]
    [InlineData(0, -1, 227u)]
    [InlineData(0, 0, 0u)]
    [InlineData(0, 0, 235u)]
    [InlineData(0, 0, 243u)]
    [InlineData(0, 0, 739u)]
    public async Task WER_failure_or_forbidden_extra_flags_refuses(
        int set, int get, uint flags)
    {
        var calls = new List<string>();
        var effects = ChildEffects(CrashControl.Arm, calls) with
        {
            SetFlags = _ => set,
            GetFlags = () => (get, flags)
        };
        Assert.Equal(CrashControl.WerRefused,
            await CrashChild.RunAsync(effects, CancellationToken.None));
        Assert.DoesNotContain("crash", calls);
    }

    [Fact]
    public async Task Child_transport_cancellation_and_configuration_faults_are_refusals()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var calls = new List<string>();
        Assert.Equal(CrashControl.TransportRefused, await CrashChild.RunAsync(
            ChildEffects(CrashControl.Arm, calls), cancelled.Token));
        Assert.Empty(calls);
        var effects = ChildEffects(CrashControl.Arm, calls) with
        {
            SetFlags = _ => throw new IOException()
        };
        Assert.Equal(CrashControl.WerRefused,
            await CrashChild.RunAsync(effects, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_line_without_EOF_does_not_arm_child()
    {
        var calls = new List<string>();
        var source = new Pipe(CrashControl.Arm) { End = () => false };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var effects = ChildEffects("", calls) with { Input = source.Capability };
        Assert.Equal(CrashControl.TransportRefused,
            await CrashChild.RunAsync(effects, deadline.Token));
        Assert.Equal<string>(["ready"], calls);
    }

    [Fact]
    public async Task Wire_writes_are_exact_ASCII_LF_without_BOM()
    {
        using var arm = new MemoryStream();
        using var ready = new MemoryStream();
        await CrashWire.WriteArmAsync(arm, CancellationToken.None);
        await CrashWire.WriteReadyAsync(ready, CancellationToken.None);
        Assert.Equal(Encoding.ASCII.GetBytes(CrashControl.Arm), arm.ToArray());
        Assert.Equal(Encoding.ASCII.GetBytes(CrashControl.Ready), ready.ToArray());
    }

    [Theory]
    [InlineData((int)CrashReadKind.Data, 0)]
    [InlineData((int)CrashReadKind.Data, 2)]
    [InlineData((int)CrashReadKind.End, 1)]
    [InlineData((int)CrashReadKind.Pending, 1)]
    [InlineData(99, 0)]
    public async Task Invalid_pipe_receipts_fail_closed(int kind, int count)
    {
        var pipe = new CrashPipe(_ => new((CrashReadKind)kind, count));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CrashWire.ReadAsync(pipe, new byte[1], CancellationToken.None));
    }

    [Fact]
    public async Task Owned_handle_survives_exit_and_callback_but_not_scope_end()
    {
        var child = new Child();
        CrashObservation? retained = null;
        var result = await Run(child, (scope, _) =>
        {
            retained = scope;
            Assert.False(child.Disposed);
            Assert.True(child.Exited);
            Assert.Equal(Live.Process, scope.Identity.Process);
            Assert.Equal(CrashExit, scope.Recheck().ExitCode);
            child.Events.Add("callback");
            return Task.CompletedTask;
        });
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Held);
        Assert.True(child.Events.IndexOf("callback") < child.Events.IndexOf("dispose"));
        Assert.NotNull(retained);
        Assert.Throws<ObjectDisposedException>(() => retained.Recheck());
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Arming_has_two_live_identity_reads_then_one_write_and_EOF()
    {
        var child = new Child();
        var result = await Run(child);
        Assert.Equal<string>(["identity", "identity", "write", "eof"],
            child.Events.Take(4));
        Assert.Equal(1, child.Writes);
        Assert.Equal(1, child.InputCloses);
        Assert.True(result.Armed);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Unadopted_start_failure_does_not_claim_clean_ownership()
    {
        var result = await OwnedCrashControl.RunAsync(
            (_, _) => throw new IOException(), NoObservation, Limits, CancellationToken.None);
        Assert.Equal(CrashFault.Start, result.Error);
        Assert.False(result.Started);
        Assert.False(result.Armed);
        Assert.False(result.Cleanup);
        Assert.Equal(CrashCleanup.Ownership, result.CleanupError);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("")]
    [InlineData("HERMES_CRASH_READY v=1\r\n")]
    public async Task Ready_refusal_precedes_identity_and_arm(string ready)
    {
        var child = new Child(ready);
        var result = await Run(child);
        Assert.Equal(CrashFault.Stdout, result.Error);
        Assert.DoesNotContain("identity", child.Events);
        Assert.Equal(0, child.Writes);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public async Task Invalid_live_binding_never_arms(int fault)
    {
        var child = new Child
        {
            Identity = _ => fault switch
            {
                0 => Live with { Process = new(0, 100) },
                1 => Live with { Process = new(42, 0) },
                2 => Live with { ImageSha256 = "bad" },
                3 => Live with { ExitFileTime = 101, ExitCode = CrashExit },
                _ => Live with { ExitCode = 0 }
            }
        };
        var result = await Run(child);
        Assert.Equal(CrashFault.Binding, result.Error);
        Assert.Equal(0, child.Writes);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Identity_change_between_live_reads_prevents_arm()
    {
        var child = new Child
        {
            Identity = reads => Live with { Process = new(42, 100 + reads) }
        };
        var result = await Run(child);
        Assert.Equal(CrashFault.Binding, result.Error);
        Assert.False(result.ArmAttempted);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Write_failure_still_closes_input_once_and_reaps()
    {
        var child = new Child { WriteFailure = true };
        var result = await Run(child);
        Assert.Equal(CrashFault.Arm, result.Error);
        Assert.True(result.ArmAttempted);
        Assert.False(result.Armed);
        Assert.Equal(1, child.InputCloses);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData(0x80131506u)]
    [InlineData(0x80131623u)]
    [InlineData(0xc0000409u)]
    [InlineData(0xc0000602u)]
    [InlineData(0xe0434352u)]
    public async Task Exact_known_crash_statuses_succeed_with_all_gates_satisfied(uint exit)
    {
        Assert.True(CrashControl.IsCrashStatus(exit));
        var child = new Child { ActualExit = exit };
        var result = await Run(child);
        Assert.Equal(exit, result.Exit);
        Assert.Equal(CrashFault.None, result.Error);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Held);
        Assert.True(result.Cleanup);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(37u)]
    [InlineData(64u)]
    [InlineData(65u)]
    [InlineData(66u)]
    [InlineData(67u)]
    [InlineData(68u)]
    [InlineData(259u)]
    [InlineData(0x80131622u)]
    [InlineData(0x80131624u)]
    [InlineData(0xc0000142u)]
    [InlineData(0xffffffffu)]
    public async Task Normal_refusal_and_unsupported_exits_are_not_crash_success(uint exit)
    {
        Assert.False(CrashControl.IsCrashStatus(exit));
        var child = new Child { ActualExit = exit };
        var result = await Run(child);
        Assert.Equal(exit, result.Exit);
        Assert.Equal(CrashFault.Exit, result.Error);
        Assert.Equal(CrashExitCheck.Status, result.ExitCheck);
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Held);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Wait_result_must_match_retained_handle_exit_identity()
    {
        var child = new Child { ExitIdentityMismatch = true };
        var called = false;
        var result = await Run(child, (_, _) =>
        {
            called = true;
            return Task.CompletedTask;
        });
        Assert.Equal(CrashFault.Exit, result.Error);
        Assert.Equal(CrashExitCheck.ExitCodeMatch, result.ExitCheck);
        Assert.False(called);
        Assert.False(result.Held);
        AssertSettled(child, result);
    }

    [Fact]
    public void Exit_annotation_preserves_values_cancellation_and_unexpected_failures()
    {
        Assert.Equal(42, CrashExitChecks.Read(CrashExitCheck.IdentityRead, () => 42));
        var cancelled = new OperationCanceledException();
        Assert.Same(cancelled, Assert.Throws<OperationCanceledException>(() =>
            CrashExitChecks.Read<int>(CrashExitCheck.IdentityRead, () => throw cancelled)));
        var unexpected = new ApplicationException();
        Assert.Same(unexpected, Assert.Throws<ApplicationException>(() =>
            CrashExitChecks.Read<int>(CrashExitCheck.IdentityRead, () => throw unexpected)));
        var annotated = new CrashExitException(CrashExitCheck.ImageQuery,
            new System.ComponentModel.Win32Exception(5));
        Assert.Same(annotated, Assert.Throws<CrashExitException>(() =>
            CrashExitChecks.Read<int>(CrashExitCheck.IdentityRead, () => throw annotated)));
    }

    [Theory]
    [InlineData((int)CrashExitCheck.Handle)]
    [InlineData((int)CrashExitCheck.ProcessId)]
    [InlineData((int)CrashExitCheck.ProcessTimes)]
    [InlineData((int)CrashExitCheck.ProcessExitCode)]
    [InlineData((int)CrashExitCheck.ProcessIdMatch)]
    [InlineData((int)CrashExitCheck.ImageQuery)]
    [InlineData((int)CrashExitCheck.ImageLength)]
    [InlineData((int)CrashExitCheck.Pins)]
    [InlineData((int)CrashExitCheck.PublicationPath)]
    [InlineData((int)CrashExitCheck.ImageHash)]
    [InlineData((int)CrashExitCheck.SourceHash)]
    [InlineData((int)CrashExitCheck.SourceMvid)]
    [InlineData((int)CrashExitCheck.IdentityRead)]
    public async Task Annotated_exit_reads_reproduce_failure_without_leaking_details(int check)
    {
        var child = new Child
        {
            ExitedIdentity = () => CrashExitChecks.Read<CrashIdentity>(
                (CrashExitCheck)check,
                () => throw new System.ComponentModel.Win32Exception(5, "private/path"))
        };
        var result = await Run(child);
        Assert.Equal((1, 2148734499u, CrashFault.Exit, CrashCleanup.None, 0),
            (result.ExitCode, result.Exit, result.Error, result.CleanupError, result.ReportError));
        Assert.Equal((CrashExitCheck)check, result.ExitCheck);
        Assert.Equal(5u, result.ExitNativeError);
        Assert.False(result.Held);
        Assert.True(result.Cleanup);
        var summary = CrashReport.Summary(result);
        Assert.EndsWith(FormattableString.Invariant(
            $"HERMES_CRASH_EXIT v=1 check={check} nativeError=5 nativeAcceptance=0\n"), summary);
        Assert.DoesNotContain("private/path", summary);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData((int)CrashExitCheck.IdentityProcess)]
    [InlineData((int)CrashExitCheck.IdentityPath)]
    [InlineData((int)CrashExitCheck.IdentityImage)]
    [InlineData((int)CrashExitCheck.IdentitySource)]
    [InlineData((int)CrashExitCheck.ExitTimeOrder)]
    [InlineData((int)CrashExitCheck.ExitTimePresent)]
    [InlineData((int)CrashExitCheck.ExitCodeMatch)]
    public async Task Each_retained_identity_predicate_remains_required(int check)
    {
        var child = new Child { ExitedIdentity = () => InvalidExit((CrashExitCheck)check) };
        var result = await Run(child);
        Assert.Equal((1, 2148734499u, CrashFault.Exit, CrashCleanup.None, 0),
            (result.ExitCode, result.Exit, result.Error, result.CleanupError, result.ReportError));
        Assert.Equal((CrashExitCheck)check, result.ExitCheck);
        Assert.Equal(0u, result.ExitNativeError);
        Assert.False(result.Held);
        Assert.True(result.Cleanup);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Exit_observation_is_summary_only_and_report_stays_strict()
    {
        var child = new Child { ExitedIdentity = () => throw new IOException("private/path") };
        var result = await Run(child);
        Assert.Equal(CrashExitCheck.IdentityRead, result.ExitCheck);
        using var document = JsonDocument.Parse(CrashReport.Encode(result));
        var report = document.RootElement;
        Assert.Equal(28, report.EnumerateObject().Count());
        Assert.Equal(1, report.GetProperty("status").GetInt32());
        Assert.Equal((int)CrashFault.Exit, report.GetProperty("error").GetInt32());
        Assert.Equal(0, report.GetProperty("held").GetInt32());
        Assert.Equal(0, report.GetProperty("nativeAcceptance").GetInt32());
        Assert.Equal(0, report.GetProperty("originalGatePassed").GetInt32());
        var commands = new Commands(result);
        Assert.Equal(1, await CrashCommands.DispatchAsync(
            [CrashControl.ProbeCommand], commands.Effects, CancellationToken.None));
        Assert.Equal(CrashReport.Summary(result), Assert.Single(commands.Summaries));
        Assert.InRange(commands.Summaries[0].Length, 1, 256);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(26)]
    [InlineData(int.MaxValue)]
    public void Undefined_exit_observations_are_not_emitted(int check)
    {
        var result = new CrashResult
            { Error = CrashFault.Exit, Exit = CrashExit, ExitCheck = (CrashExitCheck)check };
        Assert.Equal(
            "HERMES_CRASH_CONTROL v=1 status=1 exit=2148734499 error=6 cleanupError=0 reportError=0 nativeAcceptance=0\n",
            CrashReport.Summary(result));
    }

    [Fact]
    public void Exit_observation_numeric_contract_is_contiguous_and_bounded()
    {
        Assert.Equal(Enumerable.Range(0, 26),
            Enum.GetValues<CrashExitCheck>().Select(value => (int)value));
    }

    [Fact]
    public async Task Callback_fault_does_not_close_handle_early_or_skip_cleanup()
    {
        var child = new Child();
        var result = await Run(child, (scope, cancellationToken) =>
        {
            _ = scope.Recheck();
            Assert.False(child.Disposed);
            return Task.FromException(new IOException());
        });
        Assert.Equal(CrashFault.Callback, result.Error);
        Assert.False(result.Held);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Callback_postcheck_detects_retained_identity_change()
    {
        var child = new Child();
        var result = await Run(child, (_, _) =>
        {
            child.Changed = true;
            return Task.CompletedTask;
        });
        Assert.Equal(CrashFault.Callback, result.Error);
        Assert.False(result.Held);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Cancellation_joins_the_actual_wait_and_drain_tasks()
    {
        var child = new Child { ExitOnEof = false };
        using var cancel = new CancellationTokenSource();
        child.OnWrite = cancel.Cancel;
        var result = await OwnedCrashControl.RunAsync(
            child.Start, NoObservation, Limits, cancel.Token);
        Assert.True(result.Cancelled);
        Assert.Equal(CrashFault.Cancelled, result.Error);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Deadline_before_readiness_stops_only_owned_child_and_joins()
    {
        var child = new Child("") { ReadyEnds = false };
        var result = await OwnedCrashControl.RunAsync(child.Start, NoObservation,
            new(TimeSpan.FromMilliseconds(30), TimeSpan.FromSeconds(1)),
            CancellationToken.None);
        Assert.True(result.Deadline);
        Assert.Equal(CrashFault.Deadline, result.Error);
        Assert.Equal(0, child.Writes);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Stderr_cap_failure_wins_over_cancellation_it_causes()
    {
        var child = new Child
        {
            ErrorBytes = CrashControl.MaximumStderrBytes + 1
        };
        var result = await Run(child);
        Assert.Equal(CrashFault.Stderr, result.Error);
        Assert.Equal(1, result.ExitCode);
        AssertSettled(child, result);
    }

    [Theory]
    [InlineData((int)CrashCleanup.Input)]
    [InlineData((int)CrashCleanup.Kill)]
    [InlineData((int)CrashCleanup.Output)]
    [InlineData((int)CrashCleanup.Error)]
    [InlineData((int)CrashCleanup.Dispose)]
    public async Task Cleanup_faults_are_explicit_and_later_cleanup_still_runs(
        int value)
    {
        var failure = (CrashCleanup)value;
        var child = new Child { CleanupFailure = failure };
        var result = await Run(child);
        Assert.True(result.CleanupError.HasFlag(failure));
        Assert.False(result.Cleanup);
        Assert.Equal(1, result.ExitCode);
        Assert.True(child.Disposed);
        Assert.True(child.WaitTask is { IsCompleted: true });
        Assert.True(result.DrainsSettled);
        Assert.Contains("close-output", child.Events);
        Assert.Contains("close-error", child.Events);
    }

    [Fact]
    public async Task Reap_timeout_cancels_and_joins_actual_wait_not_a_wrapper_only()
    {
        var child = new Child { WriteFailure = true, StopExits = false };
        var result = await OwnedCrashControl.RunAsync(child.Start, NoObservation,
            new(TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(30)),
            CancellationToken.None);
        Assert.Equal(CrashFault.Arm, result.Error);
        Assert.True(result.CleanupError.HasFlag(CrashCleanup.Reap));
        Assert.True(result.CleanupError.HasFlag(CrashCleanup.Deadline));
        Assert.True(child.WaitTask is { IsCanceled: true });
        Assert.True(result.WaitSettled);
        Assert.True(result.DrainsSettled);
        Assert.True(child.Disposed);
        Assert.False(child.Exited);
        Assert.False(result.Cleanup);
    }

    [Fact]
    public async Task Original_binding_fault_survives_cleanup_failure()
    {
        var child = new Child
        {
            Identity = _ => throw new IOException(),
            CleanupFailure = CrashCleanup.Dispose
        };
        var result = await Run(child);
        Assert.Equal(CrashFault.Binding, result.Error);
        Assert.Equal(CrashCleanup.Dispose, result.CleanupError);
        Assert.Equal(0, child.Writes);
        AssertSettled(child, result);
    }

    [Fact]
    public void Launch_is_fixed_and_discards_inherited_credentials_and_profile()
    {
        var image = Path.Combine(Path.GetTempPath(), "Hermes.Servicing.Acceptance.exe");
        var start = new ProcessStartInfo(image);
        start.ArgumentList.Add("arbitrary");
        start.Environment["GITHUB_TOKEN"] = "not-forwarded";
        start.Environment["USERPROFILE"] = "not-forwarded";
        CrashLaunch.Restrict(start, "/windows", "/temporary");
        Assert.Equal<string>([CrashControl.ChildCommand], start.ArgumentList);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput &&
            start.RedirectStandardError);
        Assert.Equal(Path.GetDirectoryName(image), start.WorkingDirectory);
        Assert.Equal(12, start.Environment.Count);
        Assert.False(start.Environment.ContainsKey("GITHUB_TOKEN"));
        Assert.False(start.Environment.ContainsKey("USERPROFILE"));
        Assert.Equal("/temporary", start.Environment["TEMP"]);
        Assert.NotNull(start.StandardInputEncoding);
        Assert.Empty(start.StandardInputEncoding.GetPreamble());
    }

    [Fact]
    public async Task Observed_dotnet9_failfast_status_is_bound_to_success_report()
    {
        var result = await Run(new Child { ActualExit = 2148734499u });
        using var document = JsonDocument.Parse(CrashReport.Encode(result));
        var report = document.RootElement;
        Assert.Equal(0x80131623u, result.Exit);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, report.GetProperty("status").GetInt32());
        Assert.Equal(2148734499u, report.GetProperty("exit").GetUInt32());
        Assert.Equal(Live.ImageSha256, report.GetProperty("imageSha256").GetString());
        Assert.Equal(Live.SourceSha256, report.GetProperty("sourceSha256").GetString());
        Assert.Equal(0, report.GetProperty("nativeAcceptance").GetInt32());
        Assert.Equal(0, report.GetProperty("originalGatePassed").GetInt32());
        Assert.Equal(
            "HERMES_CRASH_CONTROL v=1 status=0 exit=2148734499 error=0 cleanupError=0 reportError=0 nativeAcceptance=0\n",
            CrashReport.Summary(result));
    }

    [Fact]
    public async Task Report_is_bounded_fixed_numeric_and_hash_only()
    {
        var result = await Run(new Child());
        var bytes = CrashReport.Encode(result);
        using var json = JsonDocument.Parse(bytes);
        var properties = json.RootElement.EnumerateObject().ToArray();
        Assert.InRange(bytes.Length, 1, 4096);
        Assert.Equal(28, properties.Length);
        Assert.All(properties, property => Assert.True(
            property.Value.ValueKind == JsonValueKind.Number ||
            property.Name is "imageSha256" or "sourceSha256"));
        Assert.Equal(0, json.RootElement.GetProperty("nativeAcceptance").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("originalGatePassed").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("outerSupervisionRequired").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("synchronousCallsInterruptible").GetInt32());
        Assert.Equal(CrashExit, json.RootElement.GetProperty("exit").GetUInt32());
        Assert.DoesNotContain(Live.ImagePath, Encoding.UTF8.GetString(bytes));
        Assert.Throws<InvalidDataException>(() =>
            CrashReport.Encode(result with { ImageSha256 = "not-a-hash" }));
    }

    [Theory]
    [InlineData("startup-crash-control", 66)]
    [InlineData("startup-crash-probe", 0)]
    public async Task Only_exact_routes_run_after_guard(string command, int expected)
    {
        var fixture = new Commands(await Run(new Child()));
        Assert.Equal(expected, await CrashCommands.DispatchAsync(
            [command], fixture.Effects, CancellationToken.None));
        Assert.Equal("guard", fixture.Events[0]);
        Assert.Equal(command == CrashControl.ChildCommand ? "child" : "probe",
            fixture.Events[1]);
        Assert.Equal(2, await CrashCommands.DispatchAsync(
            [command, "--pid", "42"], fixture.Effects, CancellationToken.None));
    }

    [Theory]
    [InlineData(CrashControl.ChildCommand, 67)]
    [InlineData(CrashControl.ProbeCommand, 1)]
    public async Task Guard_refusal_has_no_child_probe_or_report_actions(
        string command, int expected)
    {
        var fixture = new Commands(new()) { GuardFailure = true };
        Assert.Equal(expected, await CrashCommands.DispatchAsync(
            [command], fixture.Effects, CancellationToken.None));
        Assert.DoesNotContain("child", fixture.Events);
        Assert.DoesNotContain("probe", fixture.Events);
        Assert.DoesNotContain("publish", fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Probe_summary_status_and_exit_match_report_or_publication_failure(
        bool publishFailure)
    {
        var result = await Run(new Child());
        var fixture = new Commands(result) { PublishFailure = publishFailure };
        var exit = await CrashCommands.DispatchAsync(
            [CrashControl.ProbeCommand], fixture.Effects, CancellationToken.None);
        Assert.Equal(publishFailure ? 1 : 0, exit);
        Assert.Single(fixture.Summaries);
        Assert.Contains($"status={exit} ", fixture.Summaries[0]);
        Assert.Contains($"exit={CrashExit} ", fixture.Summaries[0]);
        Assert.Contains($"reportError={(publishFailure ? 1 : 0)} ", fixture.Summaries[0]);
        Assert.EndsWith("nativeAcceptance=0\n", fixture.Summaries[0]);
        Assert.InRange(fixture.Summaries[0].Length, 1, 256);
    }

    [Fact]
    public void Portable_implementations_are_in_the_existing_model_assembly()
    {
        var assembly = typeof(ProcessKey).Assembly;
        Assert.Equal("Hermes.Servicing.Acceptance.Model", assembly.GetName().Name);
        Assert.Same(assembly, typeof(CrashCommands).Assembly);
        Assert.Same(assembly, typeof(CrashWire).Assembly);
        Assert.Same(assembly, typeof(CrashExitChecks).Assembly);
        Assert.Same(assembly, typeof(CrashRetainedIdentity).Assembly);
        Assert.Same(assembly, typeof(CrashProcessSnapshot).Assembly);
        Assert.Same(assembly, typeof(OwnedCrashControl).Assembly);
        Assert.NotSame(typeof(CrashControlTests).Assembly, assembly);
    }

    [Fact]
    public void FileStream_handle_is_borrowed_without_ownership_transfer()
    {
        var path = Path.GetTempFileName();
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.DeleteOnClose);
        var handle = CrashWire.BorrowHandle(file);
        Assert.Same(file.SafeFileHandle, handle);
        Assert.False(handle.IsClosed);
        file.WriteByte(1);
        Assert.Equal(1, file.Length);
    }

    [Fact]
    public void Opaque_or_closed_streams_do_not_fabricate_pipe_handles()
    {
        using var memory = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => CrashWire.BorrowHandle(memory));
        var path = Path.GetTempFileName();
        var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.DeleteOnClose);
        file.Dispose();
        var error = Assert.Throws<InvalidDataException>(() => CrashWire.BorrowHandle(file));
        Assert.Equal("Crash-control pipe handle refused.", error.Message);
    }

    [Fact]
    public async Task Failure_after_creation_adoption_still_reaps_the_owned_child()
    {
        var child = new Child();
        var result = await OwnedCrashControl.RunAsync((adopt, token) =>
        {
            child.Start(adopt, token);
            throw new IOException();
        }, NoObservation, Limits, CancellationToken.None);
        Assert.True(result.Started);
        Assert.Equal(CrashFault.Start, result.Error);
        Assert.Equal(0, child.Writes);
        Assert.True(result.Cleanup);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Unadopted_cleanup_failure_keeps_start_and_cleanup_facts_separate()
    {
        var faults = CrashCleanup.Reap | CrashCleanup.Dispose;
        var initiating = new IOException("fixed synthetic startup fault");
        var result = await OwnedCrashControl.RunAsync(
            (_, _) => throw new CrashStartException(faults, initiating),
            NoObservation, Limits, CancellationToken.None);
        Assert.Equal(CrashFault.Start, result.Error);
        Assert.Equal(faults | CrashCleanup.Ownership, result.CleanupError);
        Assert.False(result.Cleanup);
        Assert.False(result.Exited);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public async Task Synchronous_start_return_is_checked_without_a_late_background_start()
    {
        var child = new Child();
        using var cancel = new CancellationTokenSource();
        var returned = false;
        var result = await OwnedCrashControl.RunAsync((adopt, token) =>
        {
            child.Start(adopt, token);
            cancel.Cancel();
            returned = true;
        }, NoObservation, Limits, cancel.Token);
        Assert.True(returned);
        Assert.True(result.Cancelled);
        Assert.Equal(0, child.Writes);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Cancelled_write_is_joined_before_its_input_handle_is_closed()
    {
        using var cancel = new CancellationTokenSource();
        var child = new Child
        {
            WriteWaitsForStop = true,
            OnWrite = cancel.Cancel
        };
        var result = await OwnedCrashControl.RunAsync(
            child.Start, NoObservation, Limits, cancel.Token);
        Assert.True(result.Cancelled);
        Assert.True(result.WriteSettled);
        Assert.True(child.WriteTask is { IsCompletedSuccessfully: true });
        Assert.True(child.Events.IndexOf("stop") < child.Events.IndexOf("eof"));
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Callback_scope_survives_cancellation_until_actual_callback_settles()
    {
        using var cancel = new CancellationTokenSource();
        var release = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new Child { OnStop = () => release.TrySetResult(true) };
        var result = await OwnedCrashControl.RunAsync(child.Start, async (scope, cancellationToken) =>
        {
            cancel.Cancel();
            await release.Task;
            Assert.False(child.Disposed);
            _ = scope.Recheck();
        }, Limits, cancel.Token);
        Assert.True(result.Cancelled);
        Assert.True(result.CallbackSettled);
        Assert.False(result.Held);
        AssertSettled(child, result);
    }

    [Fact]
    public async Task Unexpected_callback_failure_is_rethrown_only_after_cleanup()
    {
        var child = new Child();
        var failure = new ApplicationException("fixed synthetic callback fault");
        var actual = await Assert.ThrowsAsync<ApplicationException>(() =>
            Run(child, (_, _) => Task.FromException(failure)));
        Assert.Same(failure, actual);
        Assert.True(child.Disposed);
        Assert.True(child.WaitTask is { IsCompleted: true });
        Assert.True(child.WriteTask is { IsCompleted: true });
        Assert.Contains("close-output", child.Events);
        Assert.Contains("close-error", child.Events);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    public async Task Fragmented_exact_arm_still_requires_the_complete_frame_and_EOF(int chunk)
    {
        var ready = 0;
        var pipe = new Pipe(CrashControl.Arm) { Chunk = chunk };
        Assert.True(await CrashWire.ReadFixedAsync(pipe.Capability, CrashControl.Arm,
            () => ready++, CancellationToken.None));
        Assert.Equal(1, ready);
    }

    [Fact]
    public async Task WER_readback_exception_does_not_reach_failfast()
    {
        var calls = new List<string>();
        var effects = ChildEffects(CrashControl.Arm, calls) with
        {
            GetFlags = () => throw new IOException()
        };
        Assert.Equal(CrashControl.WerRefused,
            await CrashChild.RunAsync(effects, CancellationToken.None));
        Assert.DoesNotContain("crash", calls);
    }

    private static CrashImagePins ChangedPins(bool image) => new(
        image ? new string('c', 64) : Live.ImageSha256,
        image ? Live.SourceSha256 : new string('c', 64));

    private static CrashIdentity InvalidExit(CrashExitCheck check)
    {
        var identity = Live with { ExitFileTime = 200, ExitCode = CrashExit };
        return check switch
        {
            CrashExitCheck.IdentityProcess => identity with { Process = new(43, 100) },
            CrashExitCheck.IdentityPath => identity with { ImagePath = Live.ImagePath + ".other" },
            CrashExitCheck.IdentityImage => identity with { ImageSha256 = new string('c', 64) },
            CrashExitCheck.IdentitySource => identity with { SourceSha256 = new string('c', 64) },
            CrashExitCheck.ExitTimeOrder => identity with { ExitFileTime = 99 },
            CrashExitCheck.ExitTimePresent => identity with { ExitFileTime = 0 },
            CrashExitCheck.ExitCodeMatch => identity with { ExitCode = 37 },
            _ => throw new ArgumentOutOfRangeException(nameof(check))
        };
    }

    private static CrashChildEffects ChildEffects(string input, List<string> calls) => new(
        new Pipe(input).Capability,
        _ => { calls.Add("ready"); return Task.CompletedTask; },
        flags => { calls.Add($"set:{flags}"); return 0; },
        () => { calls.Add("get"); return (0, (uint)CrashControl.WerPolicy); },
        () => calls.Add("crash"));

    private static Task NoObservation(
        CrashObservation observation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = observation.Recheck();
        return Task.CompletedTask;
    }

    private static Task<CrashResult> Run(Child child,
        Func<CrashObservation, CancellationToken, Task>? observe = null) =>
        OwnedCrashControl.RunAsync(
            child.Start, observe ?? NoObservation, Limits, CancellationToken.None);

    private static Task<CrashResult> RunWithProof(
        Child child, LiveImageSource source,
        Func<CrashObservation, CancellationToken, Task>? observe = null) =>
        OwnedCrashControl.RunAsync(
            (adopt, token) => child.Start(owned => adopt(source.Wrap(owned)), token),
            observe ?? NoObservation, Limits, CancellationToken.None);

    private static void AssertSettled(Child child, CrashResult result)
    {
        Assert.True(child.Disposed);
        Assert.True(child.WaitTask is { IsCompleted: true });
        Assert.True(result.WaitSettled);
        Assert.True(result.DrainsSettled);
        Assert.True(result.WriteSettled);
        Assert.True(result.CallbackSettled);
        Assert.Equal(1, child.InputCloses);
    }

    private sealed class LiveImageSource(Child child)
    {
        // Non-owning sentinels: no OS process handle is opened or released by this fake.
        private readonly SafeProcessHandle _handle = new(new nint(42), ownsHandle: false);
        internal Func<int, string> Image { get; init; } = static _ => Live.ImagePath;
        internal Func<CrashImagePins> Pins { get; init; } =
            static () => new(Live.ImageSha256, Live.SourceSha256);
        internal Func<bool> PinsOpen { get; init; } = static () => true;
        internal Func<SafeProcessHandle, SafeProcessHandle>? HandleOverride { get; init; }
        internal int ImageQueries { get; private set; }
        internal int TerminalQueries { get; private set; }
        internal int PinReads { get; private set; }
        internal int SnapshotReads { get; private set; }
        internal bool HandleClosed => _handle.IsClosed;

        internal CrashOwnedChild Wrap(CrashOwnedChild owned)
        {
            var identity = new CrashRetainedIdentity(new(
                () => ReadSnapshot(owned.ReadIdentity), QueryImage, RecheckPins, owned.Wait));
            return owned with
            {
                ReadIdentity = identity.Read, Wait = identity.WaitAsync,
                Dispose = () => Dispose(identity, owned.Dispose)
            };
        }

        internal void CloseHandle() => _handle.Dispose();

        private CrashProcessSnapshot ReadSnapshot(Func<CrashIdentity> read)
        {
            SnapshotReads++;
            var current = read();
            var handle = HandleOverride is { } select ? select(_handle) : _handle;
            return new(handle, current.Process, current.ExitFileTime, current.ExitCode);
        }

        private string QueryImage(SafeProcessHandle handle)
        {
            ImageQueries++;
            CrashExitChecks.Require(CrashExitCheck.Handle, ReferenceEquals(handle, _handle));
            if (child.Exited)
            {
                TerminalQueries++;
                throw new CrashExitException(CrashExitCheck.ImageQuery,
                    new System.ComponentModel.Win32Exception(31));
            }
            return CrashExitChecks.Read(CrashExitCheck.ImageQuery, () => Image(ImageQueries));
        }

        private CrashImagePins RecheckPins(string path)
        {
            PinReads++;
            CrashExitChecks.Require(CrashExitCheck.Pins, PinsOpen());
            CrashExitChecks.Require(CrashExitCheck.PublicationPath,
                string.Equals(path, Live.ImagePath, StringComparison.OrdinalIgnoreCase));
            return Pins();
        }

        private void Dispose(CrashRetainedIdentity identity, Action dispose)
        {
            identity.Dispose();
            try { dispose(); }
            finally { _handle.Dispose(); }
        }
    }

    private sealed class Pipe(string text)
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(text);
        private int _offset;
        internal Func<bool> End { get; init; } = static () => true;
        internal int Chunk { get; init; } = int.MaxValue;
        internal CrashPipe Capability => new(Read);

        private CrashRead Read(Memory<byte> destination)
        {
            if (_offset == _bytes.Length)
                return new(End() ? CrashReadKind.End : CrashReadKind.Pending);
            var count = Math.Min(Chunk,
                Math.Min(destination.Length, _bytes.Length - _offset));
            _bytes.AsMemory(_offset, count).CopyTo(destination);
            _offset += count;
            return new(CrashReadKind.Data, count);
        }
    }

    private sealed class Child(string ready = CrashControl.Ready)
    {
        private readonly TaskCompletionSource<uint> _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _reads;
        private int _errorRead;
        private TaskCompletionSource<bool>? _writeRelease;
        internal List<string> Events { get; } = [];
        internal Func<int, CrashIdentity>? Identity { get; init; }
        internal Func<CrashIdentity>? ExitedIdentity { get; init; }
        internal Action OnWrite { get; set; } = static () => { };
        internal Action OnStop { get; init; } = static () => { };
        internal bool WriteFailure { get; init; }
        internal bool WriteWaitsForStop { get; init; }
        internal bool ExitOnEof { get; init; } = true;
        internal bool StopExits { get; init; } = true;
        internal bool ReadyEnds { get; init; } = true;
        internal bool ExitIdentityMismatch { get; init; }
        internal int ErrorBytes { get; init; }
        internal uint ActualExit { get; init; } = CrashExit;
        internal CrashCleanup CleanupFailure { get; init; }
        internal bool Exited { get; private set; }
        internal bool Disposed { get; private set; }
        internal bool Changed { get; set; }
        internal int Writes { get; private set; }
        internal int InputCloses { get; private set; }
        internal Task<uint>? WaitTask { get; private set; }
        internal Task? WriteTask { get; private set; }

        internal void Start(Action<CrashOwnedChild> adopt, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            adopt(Open());
        }

        internal CrashOwnedChild Open()
        {
            var output = new Pipe(ready)
            {
                End = () => ready != CrashControl.Ready ? ReadyEnds : Exited
            };
            return new(output.Capability, new(ReadError), WriteAsync, CloseInput,
                () => Cleanup("close-output", CrashCleanup.Output),
                () => Cleanup("close-error", CrashCleanup.Error),
                ReadIdentity, WaitAsync, Stop, Dispose);
        }

        private Task WriteAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("write");
            Writes++;
            OnWrite();
            _writeRelease = WriteWaitsForStop
                ? new(TaskCreationOptions.RunContinuationsAsynchronously) : null;
            return WriteTask = WriteFailure ? Task.FromException(new IOException()) :
                _writeRelease is { } release ? release.Task : Task.CompletedTask;
        }

        private void CloseInput()
        {
            Assert.True(WriteTask?.IsCompleted != false);
            Events.Add("eof");
            InputCloses++;
            if (ExitOnEof && !WriteFailure)
                Exit(ActualExit);
            Cleanup("input-close", CrashCleanup.Input);
        }

        private CrashIdentity ReadIdentity()
        {
            Events.Add("identity");
            if (Exited && ExitedIdentity is { } read)
                return read();
            if (Identity is { } custom && !Exited)
                return custom(_reads++);
            var result = Changed ? Live with { SourceSha256 = new string('c', 64) } : Live;
            return Exited ? result with
            {
                ExitFileTime = 200,
                ExitCode = ExitIdentityMismatch ? ActualExit + 1 : ActualExit
            } : result;
        }

        private Task<uint> WaitAsync(CancellationToken cancellationToken) =>
            WaitTask = WaitCoreAsync(cancellationToken);

        private async Task<uint> WaitCoreAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                () => _exit.TrySetCanceled(cancellationToken));
            return await _exit.Task;
        }

        private CrashRead ReadError(Memory<byte> destination)
        {
            if (_errorRead < ErrorBytes)
            {
                var count = Math.Min(destination.Length, ErrorBytes - _errorRead);
                destination.Span[..count].Fill(65);
                _errorRead += count;
                return new(CrashReadKind.Data, count);
            }
            return new(Exited ? CrashReadKind.End : CrashReadKind.Pending);
        }

        private void Stop()
        {
            Events.Add("stop");
            if (StopExits && !Exited)
                Exit(ActualExit);
            _writeRelease?.TrySetResult(true);
            OnStop();
            Cleanup("kill", CrashCleanup.Kill);
        }

        private void Exit(uint code)
        {
            Exited = true;
            _exit.TrySetResult(code);
        }

        private void Dispose()
        {
            Disposed = true;
            Cleanup("dispose", CrashCleanup.Dispose);
        }

        private void Cleanup(string name, CrashCleanup point)
        {
            Events.Add(name);
            if (CleanupFailure.HasFlag(point))
                throw new IOException();
        }
    }

    private sealed class Commands(CrashResult result)
    {
        internal bool GuardFailure { get; init; }
        internal bool PublishFailure { get; init; }
        internal List<string> Events { get; } = [];
        internal List<string> Summaries { get; } = [];
        internal CrashCommandEffects Effects => new(
            Guard, Child, Probe, Publish, Summaries.Add);

        private void Guard()
        {
            Events.Add("guard");
            if (GuardFailure)
                throw new InvalidOperationException();
        }

        private Task<int> Child(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("child");
            return Task.FromResult(CrashControl.CrashReturned);
        }

        private Task<CrashResult> Probe(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("probe");
            return Task.FromResult(result);
        }

        private void Publish(byte[] bytes)
        {
            Events.Add("publish");
            Assert.InRange(bytes.Length, 1, 4096);
            if (PublishFailure)
                throw new IOException();
            using var json = JsonDocument.Parse(bytes);
            Assert.Equal(result.ExitCode, json.RootElement.GetProperty("status").GetInt32());
        }
    }
}
