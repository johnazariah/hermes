using Hermes.Servicing;

namespace Hermes.Setup;

internal static class SetupEntryPoint
{
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> arguments, bool windows, ISetupApplicationFactory factory,
        TextWriter output, TextWriter diagnostics, CancellationToken cancellationToken)
    {
        if (SetupCommandLine.Parse(arguments) is not ParsedCommand(var command))
        {
            await diagnostics.WriteLineAsync("Hermes Setup: invalid arguments.");
            return (int)SetupExitCode.InvalidArguments;
        }
        var execution = windows
            ? await ExecuteAsync(command, factory, cancellationToken)
            : SetupExecution.Refused(Verb(command), SetupCode.Unavailable);
        return await WriteAsync(execution, output, diagnostics);
    }

    private static async Task<SetupExecution> ExecuteAsync(
        SetupCommand command, ISetupApplicationFactory factory,
        CancellationToken cancellationToken)
    {
        try
        {
            var created = factory.Create();
            if (created is not Effect<SetupApplication>.Success(var application))
                return SetupExecution.Refused(Verb(command), created.FailureCode,
                    created.Evidence ?? new(SetupEvidenceStage.Composition, SetupEvidenceCause.Rejected));
            await using var ownedApplication = application;
            return await application.ExecuteAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return SetupExecution.Unknown(Verb(command), SetupCode.Cancelled); }
        catch (SetupIoException error)
        {
            return SetupExecution.Refused(Verb(command), error.Code,
                error.Evidence ?? new(SetupEvidenceStage.Entry, SetupEvidenceCause.Rejected));
        }
        catch (Exception error) when (AggregateResult.Expected(error))
        { return SetupExecution.Unknown(Verb(command), SetupCode.Unavailable); }
    }

    internal static Task<int> NativeRefusalAsync(
        IReadOnlyList<string> arguments, TextWriter output, TextWriter diagnostics,
        StartupRefusalEvidence? nativeEvidence, int hResult,
        AdmissionInvariant invariant = AdmissionInvariant.None) =>
        SetupCommandLine.Parse(arguments) is ParsedCommand(var command)
            ? WriteAsync(SetupExecution.Refused(Verb(command), SetupCode.Unavailable,
                    SetupRefusalEvidence.FromAdmission(nativeEvidence, hResult, invariant)),
                output, diagnostics)
            : throw new InvalidOperationException("Native effects preceded command parsing.");

    private static async Task<int> WriteAsync(
        SetupExecution execution, TextWriter output, TextWriter diagnostics)
    {
        await output.WriteLineAsync(execution.Report.ToJson());
        await diagnostics.WriteLineAsync($"Hermes Setup: {execution.Code}.");
        if (execution.Evidence is { } evidence &&
            (execution.Code is SetupCode.InvalidEvidence or SetupCode.Unavailable ||
             execution.Code == SetupCode.MediaInvalid && evidence.IsCandidateMedia))
        {
            var line = evidence.ToDiagnosticLine();
            try
            {
                await diagnostics.WriteLineAsync(line);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Optional evidence cannot replace the refusal already reported above.
            }
        }
        return (int)execution.ExitCode;
    }

    private static SetupVerb Verb(SetupCommand command) => command switch
    {
        InstallCommand => SetupVerb.Install,
        MaintenanceCommand maintenance => maintenance.Operation,
        _ => throw new ArgumentException("Unsupported setup command.", nameof(command))
    };
}
