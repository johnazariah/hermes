using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using Hermes.Servicing;
using Hermes.Servicing.Acceptance;
using Xunit;
using Xunit.Abstractions;

namespace Hermes.Servicing.WindowsTests;

// Hosted High adapter proof only: no Medium-owner, MSI, or installed-product claim.
[SupportedOSPlatform("windows")]
[Collection(nameof(RestrictedOwnerNativeCollection))]
[Trait("NativeProof", "SetupOwnedChildAdapter")]
public sealed class SetupOwnedChildAdapterTests(ITestOutputHelper output)
{
    private static SetupReport Report { get; } =
        new(SetupVerb.Install, SetupOutcome.Completed, true, 0);
    private static SetupChildBudget Budget { get; } =
        new(TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(10), TimeProvider.System);
    private static string CommandImage => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");

    private enum Frame { Clean, StdoutNoise, StderrNoise, Hold }

    [Fact]
    public Task Canonical_completion_and_summary_use_the_real_owned_adapter() =>
        WithFixtureAsync(Frame.Clean, async (fixture, token) =>
        {
            var result = await MsiCases.MainAsync(
                fixture.Child, SetupVerb.Install, Budget, fixture.Drains, token);
            Assert.Equal(Report, result.Report);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(0, await fixture.Child.WaitAsync(token));
            await fixture.RequireConsumedAsync();
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Exit_zero_does_not_hide_trailing_output_on_either_pipe(bool stderr) =>
        WithFixtureAsync(stderr ? Frame.StderrNoise : Frame.StdoutNoise,
            async (fixture, token) =>
            {
                var error = await Assert.ThrowsAsync<SetupChildException>(() =>
                    MsiCases.MainAsync(
                        fixture.Child, SetupVerb.Install, Budget, fixture.Drains, token));
                Assert.Equal(stderr
                    ? SetupChildFault.InvalidDiagnostics : SetupChildFault.InvalidReport,
                    error.Fault);
                Assert.Equal(0, await fixture.Child.WaitAsync(token));
                await fixture.RequireConsumedAsync();
            });

    [Fact]
    public Task Deadline_reaps_the_owned_child_and_closes_and_settles_actual_drains() =>
        WithFixtureAsync(Frame.Hold, async (fixture, token) =>
        {
            var budget = Budget with { Run = TimeSpan.FromSeconds(2) };
            var error = await Assert.ThrowsAsync<SetupChildException>(() =>
                MsiCases.MainAsync(
                    fixture.Child, SetupVerb.Install, budget, fixture.Drains, token));
            // CleanupFailed is a test failure, not an accepted timeout result.
            Assert.Equal(SetupChildFault.Timeout, error.Fault);
            Assert.IsAssignableFrom<OperationCanceledException>(error.InnerException);
            Assert.NotEqual(0, await fixture.Child.WaitAsync(token));
            await fixture.RequireConsumedAsync();
        });

    private async Task WithFixtureAsync(
        Frame frame, Func<Fixture, CancellationToken, Task> proof)
    {
        var fixture = Fixture.Open(frame);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        Exception? failure = null;
        try
        {
            await fixture.ReleaseAsync(deadline.Token);
            await proof(fixture, deadline.Token);
        }
        catch (Exception error) { failure = error; }
        await fixture.FinishAsync(failure);
        output.WriteLine(
            $"HERMES_SETUP_ADAPTER v=1 scope=hosted-high-adapter-only case={(int)frame} completed=1 cleanup=1");
    }

    private sealed class Fixture(
        string root, FileStream script, OwnedChild child, StartupDebugParent parent)
    {
        private Stream? _stdout;
        private Stream? _stderr;
        internal OwnedChild Child => child;
        internal TaskCompletionSource<MsiCases.SetupDrains> Drains { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal static Fixture Open(Frame frame)
        {
            // Includes Windows, public hosted installer-job, and actual High-source checks.
            var parent = StartupDebugParent.Read();
            var root = CreateRoot();
            try { return StartPinned(root, frame, parent); }
            catch (Exception failure)
            {
                try { RemoveRoot(root); }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
                throw;
            }
        }

        private static Fixture StartPinned(
            string root, Frame frame, StartupDebugParent parent)
        {
            var script = PinScript(root, frame);
            try
            {
                var path = Path.Combine(root, "owned.cmd");
                Assert.Equal(Encoding.ASCII.GetBytes(Script(frame)), File.ReadAllBytes(path));
                var child = OwnedChild.Start(CommandImage,
                    ["/d", "/q", "/e:on", "/v:off", "/c", path],
                    root, captureStandardError: true);
                return new(root, script, child, parent);
            }
            catch (Exception failure)
            {
                try { script.Dispose(); }
                catch (Exception cleanup) { throw new AggregateException(failure, cleanup); }
                throw;
            }
        }

        private static string CreateRoot()
        {
            var temporary = Environment.GetEnvironmentVariable("RUNNER_TEMP")
                ?? throw new InvalidOperationException("Disposable runner temp is required.");
            Assert.True(Path.IsPathFullyQualified(temporary));
            var root = Path.Combine(Path.GetFullPath(temporary),
                $"hermes-setup-adapter-{Guid.NewGuid():N}");
            // Keep the sole cmd argument literal, without shell expansion or quoting.
            Assert.True(root.All(value => char.IsAsciiLetterOrDigit(value) ||
                value is ':' or '\\' or '/' or '.' or '-' or '_'));
            Assert.False(Directory.Exists(root));
            Directory.CreateDirectory(root);
            return root;
        }

        private static FileStream PinScript(string root, Frame frame)
        {
            var path = Path.Combine(root, "owned.cmd");
            var text = Script(frame);
            Assert.True(text.All(char.IsAscii));
            using (var writer = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                writer.Write(Encoding.ASCII.GetBytes(text));
            // Deny modification and deletion until the child has been reaped.
            return new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }

        private static string Script(Frame frame) => string.Join("\r\n",
            "@echo off",
            "setlocal EnableExtensions DisableDelayedExpansion",
            "set /p \"HERMES_SETUP_BOUND=\"",
            "echo " + Report.ToJson(),
            "1>&2 echo Hermes Setup: None.",
            frame switch
            {
                Frame.Clean => "rem finite clean frame",
                Frame.StdoutNoise => "echo trailing-stdout",
                Frame.StderrNoise => "1>&2 echo trailing-stderr",
                Frame.Hold => "set /p \"HERMES_SETUP_HOLD=\"",
                _ => throw new ArgumentOutOfRangeException(nameof(frame))
            },
            "exit /b 0",
            "");

        internal async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            RequireIdentity();
            var adapter = (ISetupChild)child;
            _stdout = adapter.StandardOutput;
            _stderr = adapter.StandardError;
            // Both set/p prompts are empty. Only the first receives an input line.
            await child.SendAsync("bound", cancellationToken);
        }

        private void RequireIdentity()
        {
            var adapter = (ISetupChild)child;
            Assert.False(adapter.HasExited);
            Assert.True(adapter.IsOwned);
            Assert.Equal(Path.GetFullPath(CommandImage),
                child.Identity.ExecutablePath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Files.Hash(CommandImage), child.Identity.ExecutableSha256);
            using var actual = Process.GetProcessById(child.Identity.ProcessId);
            Assert.Equal(child.Identity, Files.Identity(actual));
        }

        internal async Task RequireConsumedAsync()
        {
            // Check before fixture cleanup can rescue any consumer cleanup defect.
            Assert.True(((ISetupChild)child).HasExited);
            Assert.True(((ISetupChild)child).IsOwned);
            Assert.True(_stdout is { CanRead: false });
            Assert.True(_stderr is { CanRead: false });
            Assert.True(Drains.Task.IsCompletedSuccessfully);
            var drains = await Drains.Task;
            Assert.All(drains.Tasks, task => Assert.True(task.IsCompleted));
        }

        internal async Task FinishAsync(Exception? failure)
        {
            var cleanup = Task.Run(CleanupAsync);
            _ = cleanup.ContinueWith(completed => { _ = completed.Exception; },
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (Exception error)
            {
                if (failure is { } initiating)
                    throw new AggregateException(initiating, error);
                throw;
            }
            if (failure is { } original)
                ExceptionDispatchInfo.Capture(original).Throw();
        }

        private async Task CleanupAsync()
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var adapter = (ISetupChild)child;
            await Task.WhenAll(
                Task.Run(() => ReapAsync(child, deadline.Token)),
                Task.Run(adapter.CloseStandardOutput),
                Task.Run(adapter.CloseStandardError));
            Assert.True(adapter.HasExited);
            child.CloseInput();
            child.Dispose();
            script.Dispose();
            RemoveRoot(root);
            Assert.True(parent.SameFinalState(StartupDebugParent.Read()));
        }

        private static async Task ReapAsync(
            OwnedChild child, CancellationToken cancellationToken)
        {
            var adapter = (ISetupChild)child;
            if (!adapter.HasExited)
            {
                Assert.True(adapter.IsOwned);
                try { adapter.Kill(); }
                catch (Exception error) when (
                    (error is InvalidOperationException or Win32Exception) && adapter.HasExited)
                {
                    // Only a checked natural-exit race is accepted.
                }
            }
            await child.WaitAsync(cancellationToken);
            Assert.True(adapter.HasExited);
        }

        private static void RemoveRoot(string root)
        {
            File.Delete(Path.Combine(root, "owned.cmd"));
            Directory.Delete(root, recursive: false);
        }
    }
}
