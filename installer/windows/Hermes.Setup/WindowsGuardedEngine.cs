using System.Runtime.Versioning;
using Hermes.Installer.Control;
using Hermes.Servicing;

namespace Hermes.Setup;

[SupportedOSPlatform("windows")]
internal sealed class WindowsGuardedEngine : IGuardedEngine
{
    public async Task<Effect<IPreparedMsiCall>> PrepareAsync(
        ISetupLease lease, EngineRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var root = CurrentScope();
            if (!MatchesRoot(root, lease, request))
                return Effect<IPreparedMsiCall>.Fail(SetupCode.RootMismatch);
            var call = await GuardedInstallerCall.StartAsync(
                new ClosedOwnership(lease), ControlPlanFor(request), cancellationToken);
            return Effect<IPreparedMsiCall>.Ok(new PreparedCall(call, request.Step));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return Effect<IPreparedMsiCall>.Fail(SetupCode.Cancelled); }
        catch (Exception error) when (PreparationFailure(error))
        { return Effect<IPreparedMsiCall>.Fail(SetupCode.MsiPreparationFailed); }
    }

    private static UserInstallationScope CurrentScope() =>
        UserInstallationScope.FromKnownFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static bool MatchesRoot(
        UserInstallationScope root, ISetupLease lease, EngineRequest request) =>
        lease.IsHeldAndClosed &&
        SetupIdentity.SamePath(root.GatePath, lease.GatePath) &&
        SetupIdentity.SamePath(root.GatePath, ControlPaths.Current().GatePath) &&
        SetupIdentity.SamePath(root.AppDirectory, request.Scope.AppDirectory) &&
        SetupIdentity.SamePath(root.AppDirectory, request.Step.Package.AppDirectory);

    private static ControlPlan ControlPlanFor(EngineRequest request) => new(
        request.Step.Package.Identity.ProductCode,
        request.Step.Operation,
        request.Scope.AppDirectory,
        request.Step.NestedRemovalProducts,
        request.Step.Operation == MaintenanceKind.Install ? request.Media.PackagePath : null,
        request.Step.AllowRollback,
        request.Step.Quiet);

    private static bool PreparationFailure(Exception error) =>
        error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or System.ComponentModel.Win32Exception;

    private sealed class ClosedOwnership(ISetupLease lease) : IClosedGateOwnership
    {
        public string GatePath => lease.GatePath;
        public bool IsHeldAndClosed => lease.IsHeldAndClosed;
    }

    private sealed class PreparedCall(
        GuardedInstallerCall call, EngineStep step) : IPreparedMsiCall
    {
        public Guid RunId => call.RunId;

        public Effect<EngineReceipt> Execute()
        {
            try { return Effect<EngineReceipt>.Ok(Convert(call.Execute())); }
            catch (IOException)
            { return Effect<EngineReceipt>.Fail(SetupCode.JournalPublicationFailed); }
            catch (UnauthorizedAccessException)
            { return Effect<EngineReceipt>.Fail(SetupCode.Unavailable); }
            catch (InvalidOperationException)
            { return Effect<EngineReceipt>.Fail(SetupCode.MsiCallFailed); }
        }

        private EngineReceipt Convert(InstallerCallReceipt receipt) => new(
            receipt.RunId, receipt.ApiEntered, receipt.ApiReturned,
            receipt.MsiExitCode, receipt.ControlFaulted,
            HasFence(receipt, GuardPhase.Preflight),
            HasFence(receipt, GuardPhase.Transaction));

        private bool HasFence(InstallerCallReceipt receipt, GuardPhase phase) =>
            receipt.Fences.Any(fence =>
                fence.RunId == RunId && fence.OwnSession &&
                fence.ProductCode == step.Package.Identity.ProductCode &&
                fence.Phase == phase);

        public ValueTask DisposeAsync() => call.DisposeAsync();
    }
}
