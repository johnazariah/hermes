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
                return SetupExecution.Refused(Verb(command), created.FailureCode);
            await using var ownedApplication = application;
            return await application.ExecuteAsync(command, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return SetupExecution.Unknown(Verb(command), SetupCode.Cancelled); }
        catch (SetupIoException error)
        { return SetupExecution.Refused(Verb(command), error.Code); }
        catch (Exception error) when (AggregateResult.Expected(error))
        { return SetupExecution.Unknown(Verb(command), SetupCode.Unavailable); }
    }

    internal static Task<int> NativeRefusalAsync(
        IReadOnlyList<string> arguments, TextWriter output, TextWriter diagnostics) =>
        SetupCommandLine.Parse(arguments) is ParsedCommand(var command)
            ? WriteAsync(SetupExecution.Refused(Verb(command), SetupCode.Unavailable),
                output, diagnostics)
            : throw new InvalidOperationException("Native effects preceded command parsing.");

    private static async Task<int> WriteAsync(
        SetupExecution execution, TextWriter output, TextWriter diagnostics)
    {
        await output.WriteLineAsync(execution.Report.ToJson());
        await diagnostics.WriteLineAsync($"Hermes Setup: {execution.Code}.");
        return (int)execution.ExitCode;
    }

    private static SetupVerb Verb(SetupCommand command) => command switch
    {
        InstallCommand => SetupVerb.Install,
        MaintenanceCommand maintenance => maintenance.Operation,
        _ => throw new ArgumentException("Unsupported setup command.", nameof(command))
    };
}
