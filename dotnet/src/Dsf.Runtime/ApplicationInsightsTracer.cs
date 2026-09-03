using System.Net.Http.Json;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Sends one telemetry event to Application Insights. The real implementation
/// posts to the ingestion endpoint's public track API; tests substitute a
/// recording double rather than a live ingestion endpoint.
/// </summary>
internal interface ITelemetryGateway
{
    Task SendAsync(
        string ingestionEndpoint,
        string instrumentationKey,
        string name,
        IReadOnlyDictionary<string, string?> properties,
        CancellationToken cancellationToken);
}

/// <summary>
/// The Application Insights ingestion gateway: posts a custom event envelope to
/// the connection string's ingestion endpoint over its public HTTP track API --
/// the same transport the Application Insights SDKs use, without adding a full
/// SDK dependency for a single event shape.
/// </summary>
internal sealed class ApplicationInsightsTelemetryGateway(HttpClient? httpClient = null) : ITelemetryGateway
{
    private const string TrackApiVersion = "2";
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public async Task SendAsync(
        string ingestionEndpoint,
        string instrumentationKey,
        string name,
        IReadOnlyDictionary<string, string?> properties,
        CancellationToken cancellationToken)
    {
        var envelope = new
        {
            name = "Microsoft.ApplicationInsights.Event",
            time = DateTimeOffset.UtcNow.ToString("o"),
            iKey = instrumentationKey,
            data = new
            {
                baseType = "EventData",
                baseData = new
                {
                    ver = TrackApiVersion,
                    name,
                    properties,
                },
            },
        };

        var uri = new Uri(new Uri(EnsureTrailingSlash(ingestionEndpoint)), "v2/track");
        using var response = await httpClient.PostAsJsonAsync(uri, envelope, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Application Insights ingestion refused event '{name}' ({(int)response.StatusCode}): {body}");
        }
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

/// <summary>
/// The tracer the conveyor line reports run and station boundaries through,
/// backed by a real Application Insights ingestion endpoint parsed from the
/// runtime's existing <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> setting.
/// A dry run must have no external side effects, so an event traced for one
/// (carrying a <c>dryRun</c> property of <c>"True"</c>, set by the conveyor
/// line from <see cref="ConveyorRun.DryRun"/>) is never posted to the
/// ingestion endpoint.
/// </summary>
internal sealed class ApplicationInsightsTracer : ITracer
{
    private readonly string ingestionEndpoint;
    private readonly string instrumentationKey;
    private readonly ITelemetryGateway gateway;

    public ApplicationInsightsTracer(string connectionString, ITelemetryGateway gateway)
    {
        this.gateway = gateway;
        (ingestionEndpoint, instrumentationKey) = Parse(connectionString);
    }

    public async Task TraceAsync(
        string name, IReadOnlyDictionary<string, string?> properties, CancellationToken cancellationToken)
    {
        if (properties.TryGetValue("dryRun", out var dryRun) && string.Equals(dryRun, "True", StringComparison.Ordinal))
        {
            // A dry run must have no external side effects: never post this
            // event to the Application Insights ingestion endpoint. The event
            // still lives in the run's own in-memory audit trail via the
            // conveyor line's checkpointing -- this only suppresses the
            // external telemetry post.
            return;
        }

        try
        {
            await gateway.SendAsync(ingestionEndpoint, instrumentationKey, name, properties, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException($"Application Insights telemetry send for '{name}' failed: {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Parses the <c>Key1=Value1;Key2=Value2</c> connection string shape for the
    /// two fields a track-API event needs: the instrumentation key, and the
    /// ingestion endpoint (defaulting to the public one when the connection
    /// string does not carry its own, matching the official SDKs' behaviour).
    /// </summary>
    private static (string IngestionEndpoint, string InstrumentationKey) Parse(string connectionString)
    {
        var parts = connectionString
            .Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(part => part.Length == 2)
            .ToDictionary(part => part[0].Trim(), part => part[1].Trim(), StringComparer.OrdinalIgnoreCase);

        if (!parts.TryGetValue("InstrumentationKey", out var key) || string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "APPLICATIONINSIGHTS_CONNECTION_STRING is missing its 'InstrumentationKey' field.");
        }

        var endpoint = parts.TryGetValue("IngestionEndpoint", out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : "https://dc.services.visualstudio.com/";
        return (endpoint, key);
    }
}
