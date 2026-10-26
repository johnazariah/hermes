namespace Hermes.Servicing.Acceptance;

internal sealed unsafe partial class WindowsRestrictedOwnerTokens
    : IAcceptanceSourceControlFactory
{
    private const int SourceElevationTypeInformation = 18;

    public IAcceptanceSourceControlScope OpenSourceControl()
    {
        RequireDisposableJob();
        var source = OpenCurrentPrimary();
        try { return new SourceControlScope(source); }
        catch (Exception original)
        {
            AcceptanceSourceControlOrchestration.Throw(
                AcceptanceSourceControlOrchestration.Combine(original,
                    AcceptanceSourceControlOrchestration.Capture(source.Dispose)));
            throw;
        }
    }

    private static AcceptanceSourceHighFacts ReadSourceHighFacts(
        SafeHandle candidate, SafeAccessTokenHandle source)
    {
        var (enabled, denyOnly) = ReadAdministrator(candidate);
        return new(ReadInt(candidate, TypeInformation),
            ReadInt(candidate, SourceElevationTypeInformation),
            ReadInt(candidate, ElevationInformation), ReadIntegrity(candidate),
            enabled, denyOnly, SameOwner(candidate, source), SameSession(candidate, source));
    }

    private sealed class SourceControlScope : IAcceptanceSourceControlScope
    {
        private readonly IAcceptanceSourceToken _source;
        private readonly AcceptanceParentSecuritySnapshot _snapshot;
        private readonly AcceptanceSourceControlLifetime _lifetime;
        private readonly OwnedInertChild _child = new(
            powerShellProbe: true, powerShellConsole: AcceptancePowerShellProbeConsole.NoWindow);
        private bool _sourceHigh;
        private bool _childHigh;
        private bool _resumed;

        internal SourceControlScope(IAcceptanceSourceToken source)
        {
            _source = source;
            _snapshot = ReadSecuritySnapshot(SourceHandle(source));
            _lifetime = new(_child.Dispose, VerifyHeldSource, _source.Dispose,
                () => _source.IsClosed && _child.IsClosed);
        }

        public bool IsClosed => _lifetime.IsClosed;

        public AcceptanceSourceControlProgress Progress
        {
            get
            {
                var io = _child.PowerShellIoFacts;
                return new(_sourceHigh, _childHigh, _resumed, new(
                    io.ProcessCreated, io.ImageMatched, io.SourceBytes, io.StdinEof,
                    io.PipesClosed, io.DrainsClosed, io.Output.Total, io.Error.Total,
                    (int)io.Output.State, (int)io.Error.State,
                    io.Output.Error, io.Error.Error));
            }
        }

        public void VerifySourceHighSnapshot()
        {
            if (_lifetime.IsDisposed)
                throw RestrictedOwnerToken.Failure(6);
            VerifyHeldSource();
        }

        private void VerifyHeldSource()
        {
            Instance.RequireDisposableJob();
            if (Instance.ReadThreadToken() != MsiContextRead.Absent)
                throw RestrictedOwnerToken.Failure(13);
            var held = SourceHandle(_source);
            ReadSourceHighFacts(held, held).Require();
            if (!_snapshot.SameFinalState(ReadSecuritySnapshot(held)) ||
                !_snapshot.SameFinalState(Instance.ReadParentSecurity()))
                throw RestrictedOwnerToken.Failure(13);
            _sourceHigh = true;
        }

        public void CreateKillOnCloseJob() => _child.CreateKillOnCloseJob();
        public void CreateSuspended() => _child.CreateSourceSuspended(_source);
        public void AssignJob() => _child.AssignJob();

        public void VerifyChildHighSnapshotAndThreadAbsence()
        {
            _child.VerifySourcePrimary(_source, _snapshot);
            if (_child.ReadThreadToken() != MsiContextRead.Absent)
                throw RestrictedOwnerToken.Failure(13);
            _childHigh = true;
        }

        public void ReadObjectAccess() => _ = _child.ReadObjectAccess();

        public void Resume()
        {
            _child.Resume();
            _resumed = true;
        }

        public uint Wait(CancellationToken cancellationToken) =>
            _child.Wait(cancellationToken);

        public void Dispose() => _lifetime.Dispose();
    }

    private sealed partial class OwnedInertChild
    {
        private bool _sourceControl;

        internal void CreateSourceSuspended(IAcceptanceSourceToken source)
        {
            Require(ChildState.JobReady);
            if (!powerShellProbe || ownedSelfControl ||
                powerShellConsole != AcceptancePowerShellProbeConsole.NoWindow ||
                desktop != AcceptanceDesktopSelection.Automatic)
                throw RestrictedOwnerToken.Failure(13);
            _sourceControl = true;
            var (application, command, script) = OpenPowerShellProbe();
            _powerShellSource = script;
            Launch(SourceHandle(source), application, command, SystemDirectory(), null);
            _state = ChildState.Suspended;
        }

        internal void VerifySourcePrimary(
            IAcceptanceSourceToken source, AcceptanceParentSecuritySnapshot expected)
        {
            Require(ChildState.Assigned);
            if (!_sourceControl)
                throw RestrictedOwnerToken.Failure(13);
            _primary = OpenChildPrimary();
            var candidate = Handle(_primary);
            ReadSourceHighFacts(candidate, SourceHandle(source)).Require();
            if (!expected.SameAuthority(ReadSecuritySnapshot(candidate)))
                throw RestrictedOwnerToken.Failure(13);
            _state = ChildState.PrimaryVerified;
        }

        private System.ComponentModel.Win32Exception PowerShellCreationFailure()
        {
            var error = Marshal.GetLastPInvokeError();
            return _sourceControl && error is 5 or 1314
                ? new AcceptanceSourceControlCreationDeniedException(error)
                : RestrictedOwnerToken.Failure(error);
        }
    }
}
