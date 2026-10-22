using System.Diagnostics;
using System.Text;
using Hermes.Servicing.Acceptance;
using Xunit;

namespace Hermes.Servicing.Tests;

public sealed class CrashEventProducerTests
{
    private enum Call { Guard, Context, Transport, Set, Get, Ready, End, Native, Managed }

    private static string Image =>
        Path.Combine(Path.GetTempPath(), "Hermes.Servicing.Acceptance.exe");

    private static StepJobContext Actual => new(
        true, "true", "Windows", "github-hosted", "installer",
        "https://github.com", "johnazariah/hermes");

    [Fact]
    public void Factory_checks_actual_parent_context_before_transport_construction()
    {
        var fixture = new LaunchFixture();
        var start = CrashEventLaunch.CreateStartInfo(fixture.Effects);
        Assert.Equal<Call>([Call.Guard, Call.Context, Call.Transport], fixture.Calls);
        Assert.Equal<string>([CrashEventChild.Command], start.ArgumentList);
        Assert.Equal("", start.Arguments);
        AssertTransport(start);
        var original = OriginalStart();
        Assert.Equal<string>([CrashControl.ChildCommand], original.ArgumentList);
        Assert.Equal(
            original.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            start.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void Context_capture_reads_only_the_six_admission_values()
    {
        var reads = new List<string>();
        var values = OriginalStart().Environment;
        var actual = CrashEventLaunch.ReadContext(true, key =>
        {
            reads.Add(key);
            return values[key];
        });
        Assert.Equal(Actual, actual);
        actual.Require();
        Assert.Equal<string>(
            ["GITHUB_ACTIONS", "RUNNER_OS", "RUNNER_ENVIRONMENT", "GITHUB_JOB",
                "GITHUB_SERVER_URL", "GITHUB_REPOSITORY"], reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Missing_or_changed_actual_admission_never_constructs_transport(bool missing)
    {
        foreach (var field in Enumerable.Range(0, 7))
        {
            var fixture = new LaunchFixture { Context = InvalidActual(field, missing) };
            Assert.Throws<InvalidOperationException>(() =>
                CrashEventLaunch.CreateStartInfo(fixture.Effects));
            Assert.Equal<Call>([Call.Guard, Call.Context], fixture.Calls);
        }
    }

    [Fact]
    public void Real_parent_guard_refusal_precedes_context_read_and_transport()
    {
        var fixture = new LaunchFixture { GuardFailure = true };
        Assert.Throws<IOException>(() =>
            CrashEventLaunch.CreateStartInfo(fixture.Effects));
        Assert.Equal<Call>([Call.Guard], fixture.Calls);
    }

    [Fact]
    public void Context_read_and_transport_failures_are_not_hidden()
    {
        var fixture = new LaunchFixture();
        Assert.Throws<IOException>(() => CrashEventLaunch.CreateStartInfo(
            fixture.Effects with { ReadContext = () => throw new IOException() }));
        Assert.Equal<Call>([Call.Guard], fixture.Calls);
        fixture.Calls.Clear();
        Assert.Throws<IOException>(() => CrashEventLaunch.CreateStartInfo(
            fixture.Effects with { CreateTransport = () => throw new IOException() }));
        Assert.Equal<Call>([Call.Guard, Call.Context], fixture.Calls);
    }

    [Fact]
    public async Task Exact_native_dispatch_forwards_token_and_refuses_a_returning_native_call()
    {
        using var cancellation = new CancellationTokenSource();
        var child = new ChildFixture();
        var effects = new CrashEventChildCommandEffects(
            () => child.Calls.Add(Call.Guard),
            token =>
            {
                Assert.Equal(cancellation.Token, token);
                return CrashEventChild.RunAsync(child.Effects, token);
            });
        var exit = await CrashEventChild.DispatchAsync(
            [CrashEventChild.Command], effects, cancellation.Token);
        Assert.Equal(CrashControl.CrashReturned, exit);
        Assert.False(CrashControl.IsCrashStatus(checked((uint)exit)));
        Assert.Equal<Call>(
            [Call.Guard, Call.Set, Call.Get, Call.Ready, Call.End, Call.Native], child.Calls);
        Assert.Equal((nint.Zero, nint.Zero, 0u), Assert.Single(child.Raises));
    }

    [Fact]
    public async Task Native_dispatch_rejects_every_nonexact_argv_before_guard()
    {
        var calls = new List<Call>();
        var effects = new CrashEventChildCommandEffects(
            () => calls.Add(Call.Guard),
            _ => { calls.Add(Call.Native); return Task.FromResult(0); });
        foreach (var arguments in RejectedArguments())
            Assert.Equal(2, await CrashEventChild.DispatchAsync(
                arguments, effects, CancellationToken.None));
        Assert.Empty(calls);
    }

    [Fact]
    public async Task Native_guard_and_child_operational_failures_have_distinct_refusals()
    {
        var child = new ChildFixture();
        var effects = new CrashEventChildCommandEffects(
            () => throw new IOException(),
            token => CrashEventChild.RunAsync(child.Effects, token));
        Assert.Equal(CrashControl.GuardRefused, await CrashEventChild.DispatchAsync(
            [CrashEventChild.Command], effects, CancellationToken.None));
        Assert.Empty(child.Calls);
        effects = effects with
        {
            RequireContext = static () => { },
            Child = _ => throw new IOException()
        };
        Assert.Equal(CrashControl.TransportRefused, await CrashEventChild.DispatchAsync(
            [CrashEventChild.Command], effects, CancellationToken.None));
        Assert.Empty(child.Calls);
    }

    [Fact]
    public async Task Original_dispatch_keeps_managed_protocol_and_rejects_native_route()
    {
        var child = new ChildFixture();
        var source = child.Effects;
        var managed = new CrashChildEffects(source.Input, source.WriteReady,
            source.SetFlags, source.GetFlags, () => child.Calls.Add(Call.Managed));
        var effects = new CrashCommandEffects(
            () => child.Calls.Add(Call.Guard),
            token => CrashChild.RunAsync(managed, token),
            _ => throw new ApplicationException(),
            _ => throw new ApplicationException(),
            _ => throw new ApplicationException());
        Assert.Equal(2, await CrashCommands.DispatchAsync(
            [CrashEventChild.Command], effects, CancellationToken.None));
        Assert.Empty(child.Calls);
        Assert.Equal(CrashControl.CrashReturned, await CrashCommands.DispatchAsync(
            [CrashControl.ChildCommand], effects, CancellationToken.None));
        Assert.Equal<Call>(
            [Call.Guard, Call.Ready, Call.End, Call.Set, Call.Get, Call.Managed], child.Calls);
        Assert.Empty(child.Raises);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(7)]
    [InlineData(int.MaxValue)]
    public async Task Fragmentation_keeps_WER_before_READY_and_one_native_attempt_after_EOF(
        int chunk)
    {
        var child = new ChildFixture { Chunk = chunk };
        Assert.Equal(CrashControl.CrashReturned,
            await CrashEventChild.RunAsync(child.Effects, CancellationToken.None));
        Assert.Equal<Call>(
            [Call.Set, Call.Get, Call.Ready, Call.End, Call.Native], child.Calls);
        Assert.Equal((nint.Zero, nint.Zero, 0u), Assert.Single(child.Raises));
    }

    [Theory]
    [InlineData(-1, 0, 227u)]
    [InlineData(0, -1, 227u)]
    [InlineData(0, 0, 0u)]
    [InlineData(0, 0, 235u)]
    [InlineData(0, 0, 243u)]
    [InlineData(0, 0, 739u)]
    public async Task WER_failure_or_extra_flags_refuses_before_READY(
        int set, int get, uint flags)
    {
        var child = new ChildFixture();
        var effects = child.Effects with
        {
            SetFlags = value => { Assert.Equal(227u, value); return set; },
            GetFlags = () => (get, flags)
        };
        Assert.Equal(CrashControl.WerRefused,
            await CrashEventChild.RunAsync(effects, CancellationToken.None));
        Assert.Empty(child.Calls);
        Assert.Empty(child.Raises);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WER_operational_exceptions_refuse_before_READY(bool setter)
    {
        var child = new ChildFixture();
        var effects = setter
            ? child.Effects with { SetFlags = _ => throw new IOException() }
            : child.Effects with { GetFlags = () => throw new IOException() };
        Assert.Equal(CrashControl.WerRefused,
            await CrashEventChild.RunAsync(effects, CancellationToken.None));
        Assert.DoesNotContain(Call.Ready, child.Calls);
        Assert.DoesNotContain(Call.End, child.Calls);
        Assert.Empty(child.Raises);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HERMES_CRASH_ARM v=1\r\n")]
    [InlineData("HERMES_CRASH_ARM v=2\n")]
    [InlineData("\uFEFFHERMES_CRASH_ARM v=1\n")]
    [InlineData(CrashControl.Arm + "x")]
    [InlineData(CrashControl.Arm + CrashControl.Arm)]
    public async Task Invalid_or_duplicate_arm_never_reaches_native_effect(string input)
    {
        var child = new ChildFixture(input);
        Assert.Equal(CrashControl.InputRefused,
            await CrashEventChild.RunAsync(child.Effects, CancellationToken.None));
        Assert.Equal(1, child.Calls.Count(call => call == Call.Set));
        Assert.Equal(1, child.Calls.Count(call => call == Call.Get));
        Assert.Equal(1, child.Calls.Count(call => call == Call.Ready));
        Assert.Empty(child.Raises);
    }

    [Fact]
    public async Task Cancellation_before_setup_has_no_effects()
    {
        var child = new ChildFixture();
        Assert.Equal(CrashControl.TransportRefused, await CrashEventChild.RunAsync(
            child.Effects, new CancellationToken(canceled: true)));
        Assert.Empty(child.Calls);
        Assert.Empty(child.Raises);
    }

    [Fact]
    public async Task Cancellation_during_READY_does_not_read_or_crash()
    {
        using var cancellation = new CancellationTokenSource();
        var child = new ChildFixture();
        var effects = child.Effects with
        {
            WriteReady = _ => { cancellation.Cancel(); return Task.CompletedTask; }
        };
        Assert.Equal(CrashControl.TransportRefused,
            await CrashEventChild.RunAsync(effects, cancellation.Token));
        Assert.Equal<Call>([Call.Set, Call.Get], child.Calls);
        Assert.Empty(child.Raises);
    }

    [Fact]
    public async Task Complete_arm_without_EOF_waits_and_cancels_without_native_effect()
    {
        using var cancellation = new CancellationTokenSource();
        var pending = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new ChildFixture();
        var effects = child.Effects with
        {
            Input = WithoutEof(child.Effects.Input, () => pending.TrySetResult(true))
        };
        var run = CrashEventChild.RunAsync(effects, cancellation.Token);
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(run.IsCompleted);
        Assert.Empty(child.Raises);
        cancellation.Cancel();
        Assert.Equal(CrashControl.TransportRefused, await run);
        Assert.Empty(child.Raises);
    }

    [Fact]
    public async Task Cancellation_at_EOF_is_checked_before_native_effect()
    {
        using var cancellation = new CancellationTokenSource();
        var child = new ChildFixture();
        var input = child.Effects.Input;
        var effects = child.Effects with
        {
            Input = new(memory =>
            {
                var read = input.Read(memory);
                if (read.Kind == CrashReadKind.End)
                    cancellation.Cancel();
                return read;
            })
        };
        Assert.Equal(CrashControl.TransportRefused,
            await CrashEventChild.RunAsync(effects, cancellation.Token));
        Assert.Contains(Call.End, child.Calls);
        Assert.Empty(child.Raises);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Operational_transport_or_native_faults_are_explicit_refusals(int phase)
    {
        var child = new ChildFixture();
        Assert.Equal(CrashControl.TransportRefused, await CrashEventChild.RunAsync(
            FailAt(child.Effects, phase), CancellationToken.None));
        Assert.Empty(child.Raises);
        Assert.DoesNotContain(Call.Managed, child.Calls);
    }

    [Fact]
    public async Task Unexpected_native_failure_is_not_swallowed_or_replaced()
    {
        var child = new ChildFixture();
        var failure = new ApplicationException();
        var effects = child.Effects with { RaiseFailFast = (_, _, _) => throw failure };
        Assert.Same(failure, await Assert.ThrowsAsync<ApplicationException>(() =>
            CrashEventChild.RunAsync(effects, CancellationToken.None)));
        Assert.Equal<Call>([Call.Set, Call.Get, Call.Ready, Call.End], child.Calls);
        Assert.Empty(child.Raises);
    }

    [Fact]
    public void Producer_protocol_and_factory_are_portable_model_code()
    {
        var assembly = typeof(CrashChild).Assembly;
        Assert.Same(assembly, typeof(CrashEventChild).Assembly);
        Assert.Same(assembly, typeof(CrashEventLaunch).Assembly);
        Assert.NotSame(typeof(CrashEventProducerTests).Assembly, assembly);
    }

    private static IEnumerable<string[]> RejectedArguments() =>
    [
        [],
        [CrashEventChild.Command, "extra"],
        [CrashEventChild.Command, "--pid", "42"],
        [CrashEventChild.Command, CrashEventChild.Command],
        [CrashEventChild.Command.ToUpperInvariant()],
        [CrashEventChild.Command + " "],
        [CrashControl.ChildCommand],
        [CrashControl.ProbeCommand],
        [CrashEventControl.Command]
    ];

    private static StepJobContext InvalidActual(int field, bool missing)
    {
        var value = missing ? null : "not-admitted";
        return field switch
        {
            0 => Actual with { Windows = false },
            1 => Actual with { Actions = value },
            2 => Actual with { RunnerOs = value },
            3 => Actual with { RunnerEnvironment = value },
            4 => Actual with { Job = value },
            5 => Actual with { Server = value },
            6 => Actual with { Repository = value },
            _ => throw new ArgumentOutOfRangeException(nameof(field))
        };
    }

    private static ProcessStartInfo OriginalStart()
    {
        var start = new ProcessStartInfo(Image);
        start.Environment["GITHUB_TOKEN"] = "not-forwarded";
        start.Environment["USERPROFILE"] = "not-forwarded";
        CrashLaunch.Restrict(start, "/windows", "/temporary");
        return start;
    }

    private static void AssertTransport(ProcessStartInfo start)
    {
        Assert.Equal(Image, start.FileName);
        Assert.Equal(Path.GetDirectoryName(Image), start.WorkingDirectory);
        Assert.False(start.UseShellExecute);
        Assert.True(start.CreateNoWindow);
        Assert.True(start.RedirectStandardInput && start.RedirectStandardOutput &&
            start.RedirectStandardError);
        Assert.Equal(12, start.Environment.Count);
        Assert.False(start.Environment.ContainsKey("GITHUB_TOKEN"));
        Assert.False(start.Environment.ContainsKey("USERPROFILE"));
        Assert.Equal("/temporary", start.Environment["TEMP"]);
        Assert.NotNull(start.StandardInputEncoding);
        Assert.Empty(start.StandardInputEncoding.GetPreamble());
    }

    private static CrashPipe WithoutEof(CrashPipe input, Action pending) => new(memory =>
    {
        var read = input.Read(memory);
        if (read.Kind != CrashReadKind.End)
            return read;
        pending();
        return new(CrashReadKind.Pending);
    });

    private static CrashEventChildEffects FailAt(CrashEventChildEffects effects, int phase) =>
        phase switch
        {
            0 => effects with { Input = new(_ => throw new IOException()) },
            1 => effects with { WriteReady = _ => Task.FromException(new IOException()) },
            2 => effects with { RaiseFailFast = (_, _, _) => throw new IOException() },
            _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };

    private sealed class LaunchFixture
    {
        internal List<Call> Calls { get; } = [];
        internal StepJobContext Context { get; init; } = Actual;
        internal bool GuardFailure { get; init; }
        internal CrashEventLaunchEffects Effects => new(RequireContext, Read, CreateTransport);

        private void RequireContext()
        {
            Calls.Add(Call.Guard);
            if (GuardFailure)
                throw new IOException();
        }

        private StepJobContext Read()
        {
            Calls.Add(Call.Context);
            return Context;
        }

        private ProcessStartInfo CreateTransport()
        {
            Calls.Add(Call.Transport);
            var start = OriginalStart();
            start.Arguments = "not-forwarded";
            start.ArgumentList.Add("not-forwarded");
            start.Environment["GITHUB_JOB"] = "not-forwarded";
            return start;
        }
    }

    private sealed class ChildFixture(string input = CrashControl.Arm)
    {
        private readonly byte[] _bytes = Encoding.UTF8.GetBytes(input);
        private int _offset;
        internal int Chunk { get; init; } = 1;
        internal List<Call> Calls { get; } = [];
        internal List<(nint Record, nint Context, uint Flags)> Raises { get; } = [];
        internal CrashEventChildEffects Effects => new(
            new(Read), WriteReady, SetFlags, GetFlags, Raise);

        private CrashRead Read(Memory<byte> destination)
        {
            if (_offset == _bytes.Length)
            {
                Calls.Add(Call.End);
                return new(CrashReadKind.End);
            }
            var count = Math.Min(Chunk, Math.Min(destination.Length, _bytes.Length - _offset));
            _bytes.AsMemory(_offset, count).CopyTo(destination);
            _offset += count;
            return new(CrashReadKind.Data, count);
        }

        private Task WriteReady(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add(Call.Ready);
            return Task.CompletedTask;
        }

        private int SetFlags(uint flags)
        {
            Assert.Equal(227u, flags);
            Calls.Add(Call.Set);
            return 0;
        }

        private (int HResult, uint Flags) GetFlags()
        {
            Calls.Add(Call.Get);
            return (0, 227u);
        }

        private void Raise(nint record, nint context, uint flags)
        {
            Calls.Add(Call.Native);
            Raises.Add((record, context, flags));
        }
    }
}
