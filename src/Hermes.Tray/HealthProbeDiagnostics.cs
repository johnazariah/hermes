using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Hermes.Tray;

[JsonConverter(typeof(JsonStringEnumConverter<HealthProbeOutcome>))]
internal enum HealthProbeOutcome
{
    Healthy, InvalidIdentity, HttpStatus, InvalidJson,
    ConnectionRefused, Timeout, TransportFailure, Cancelled
}

internal sealed record HealthProbePolicy(TimeSpan ConnectTimeout, TimeSpan RequestTimeout)
{
    public static HealthProbePolicy Default { get; } =
        new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(8));

    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero || RequestTimeout <= TimeSpan.Zero ||
            ConnectTimeout > RequestTimeout)
            throw new ArgumentException(
                "Health budgets must be positive, with connect timeout no greater than request timeout.",
                nameof(RequestTimeout));
    }
}

internal sealed record HealthProbeDiagnostic(
    int Port, HealthProbeOutcome Outcome, HealthObservation Observation,
    long ElapsedMilliseconds, long ConnectBudgetMilliseconds, long RequestBudgetMilliseconds,
    [property: JsonConverter(typeof(JsonStringEnumConverter<SocketError>))]
    SocketError? SocketError = null,
    [property: JsonConverter(typeof(JsonStringEnumConverter<HttpRequestError>))]
    HttpRequestError? HttpError = null,
    int? StatusCode = null, string? Failure = null);

internal interface IHealthDiagnostics
{
    HealthProbeDiagnostic? FirstDiagnostic { get; }
    HealthProbeDiagnostic? LastDiagnostic { get; }
}
