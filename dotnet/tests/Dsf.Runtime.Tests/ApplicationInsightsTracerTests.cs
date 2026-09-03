using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The tracer parses the connection string once, forwards every event to the
/// telemetry gateway with the parsed ingestion endpoint and instrumentation key,
/// and turns a gateway failure into a message naming the event -- so a caller
/// (the conveyor line) can decide whether a telemetry failure should be audited
/// and swallowed, rather than eating the failure itself.
/// </summary>
public sealed class ApplicationInsightsTracerTests
{
    private sealed class RecordingGateway : ITelemetryGateway
    {
        public (string IngestionEndpoint, string InstrumentationKey, string Name, IReadOnlyDictionary<string, string?> Properties)? Sent
        {
            get; private set;
        }

        public Task SendAsync(
            string ingestionEndpoint,
            string instrumentationKey,
            string name,
            IReadOnlyDictionary<string, string?> properties,
            CancellationToken cancellationToken)
        {
            Sent = (ingestionEndpoint, instrumentationKey, name, properties);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingGateway(string reason) : ITelemetryGateway
    {
        public Task SendAsync(
            string ingestionEndpoint,
            string instrumentationKey,
            string name,
            IReadOnlyDictionary<string, string?> properties,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(reason);
    }

    [Fact]
    public async Task Parses_the_instrumentation_key_and_ingestion_endpoint_from_the_connection_string()
    {
        var gateway = new RecordingGateway();
        var tracer = new ApplicationInsightsTracer(
            "InstrumentationKey=abc-123;IngestionEndpoint=https://region.in.applicationinsights.azure.com/", gateway);

        await tracer.TraceAsync("station.start", new Dictionary<string, string?> { ["station"] = "s1_triage" }, CancellationToken.None);

        Assert.NotNull(gateway.Sent);
        Assert.Equal("https://region.in.applicationinsights.azure.com/", gateway.Sent!.Value.IngestionEndpoint);
        Assert.Equal("abc-123", gateway.Sent.Value.InstrumentationKey);
        Assert.Equal("station.start", gateway.Sent.Value.Name);
        Assert.Equal("s1_triage", gateway.Sent.Value.Properties["station"]);
    }

    [Fact]
    public async Task Defaults_to_the_public_ingestion_endpoint_when_the_connection_string_carries_none()
    {
        var gateway = new RecordingGateway();
        var tracer = new ApplicationInsightsTracer("InstrumentationKey=abc-123", gateway);

        await tracer.TraceAsync("run.start", new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Equal("https://dc.services.visualstudio.com/", gateway.Sent!.Value.IngestionEndpoint);
    }

    [Fact]
    public void Rejects_a_connection_string_missing_an_instrumentation_key()
    {
        Assert.Throws<InvalidOperationException>(
            () => new ApplicationInsightsTracer("IngestionEndpoint=https://region.example/", new RecordingGateway()));
    }

    [Fact]
    public async Task A_gateway_failure_is_reported_naming_the_event()
    {
        var tracer = new ApplicationInsightsTracer("InstrumentationKey=abc-123", new ThrowingGateway("503 Service Unavailable"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tracer.TraceAsync("run.start", new Dictionary<string, string?>(), CancellationToken.None));

        Assert.Contains("run.start", exception.Message);
        Assert.Contains("503 Service Unavailable", exception.Message);
    }
}
