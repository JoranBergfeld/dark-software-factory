using System.CommandLine;
using System.Net;
using System.Text.Json;
using Dsf.Cli;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class BootstrapStatusFailureTests
{
    private static readonly OwnerAuthority Authority = new(
        "https://owner.vault.azure.net/", "https://owner.azconfig.io");
    private static readonly OwnerBootstrapRequest Request = new(
        "dsf-test", "rg-test", "owner", "owner", "swedencentral");
    private static readonly OwnerBootstrapStatus Status = new(
        OwnerBootstrapStage.Planned, DateTimeOffset.UnixEpoch, []);

    [Fact]
    public async Task Write_status_persists_a_non_secret_document_in_owner_app_configuration()
    {
        var handler = new StatusHandler(HttpStatusCode.OK);
        var runner = TokenRunner();
        var client = new AzureCliOwnerBootstrapClient(
            runner, appConfigHttpClient: new HttpClient(handler),
            authorizationRetryDelay: (_, _) => throw new InvalidOperationException("Unexpected retry."));

        await client.WriteAsync(Authority, Request, Status, CancellationToken.None);

        var body = Assert.Single(handler.Bodies);
        Assert.DoesNotContain("PRIVATE KEY", body);
        using var payload = JsonDocument.Parse(body);
        Assert.Equal(JsonSerializer.Serialize(Status), payload.RootElement.GetProperty("value").GetString());
        Assert.Contains("dsf%2Fowner%2Fbootstrap%2Fdsf-test%2Fstatus", handler.Uri!.AbsoluteUri);
        Assert.Equal("account", Assert.Single(runner.Invocations)[0]);
    }

    [Fact]
    public async Task Empty_body_forbidden_retries_the_same_status_write_until_authorized()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var handler = new StatusHandler(HttpStatusCode.Forbidden, HttpStatusCode.OK);
        var delays = new List<TimeSpan>();
        var runner = TokenRunner();
        var client = new AzureCliOwnerBootstrapClient(
            runner, terminal, new HttpClient(handler),
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        await client.WriteAsync(Authority, Request, Status, CancellationToken.None);

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(handler.Bodies[0], handler.Bodies[1]);
        Assert.Equal(TimeSpan.FromSeconds(30), Assert.Single(delays));
        Assert.Equal(HttpMethod.Put, handler.Method);
        Assert.Equal(
            "https://owner.azconfig.io/kv/dsf%2Fowner%2Fbootstrap%2Fdsf-test%2Fstatus?api-version=1.0",
            handler.Uri!.AbsoluteUri);
        Assert.Equal("Bearer", handler.AuthScheme);
        Assert.Equal("test-access-token", handler.AuthToken);
        using var payload = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal(JsonSerializer.Serialize(Status), payload.RootElement.GetProperty("value").GetString());
        Assert.Equal("application/json", payload.RootElement.GetProperty("content_type").GetString());
        Assert.Contains("HTTP 403", terminal.Output);
        Assert.Contains("role", terminal.Output);
        Assert.DoesNotContain("test-access-token", terminal.Output + terminal.Error);
        Assert.Equal(2, runner.Invocations.Count);
        Assert.All(runner.Invocations, invocation => Assert.Contains("https://appconfig.azure.com", invocation));
    }

    [Fact]
    public async Task Persistent_forbidden_is_bounded_and_reports_access_guidance()
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        var delays = new List<TimeSpan>();
        var client = new AzureCliOwnerBootstrapClient(
            TokenRunner(), appConfigHttpClient: new HttpClient(handler),
            authorizationRetryDelay: (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.WriteAsync(Authority, Request, Status, CancellationToken.None));

        Assert.Equal(41, handler.Bodies.Count);
        Assert.Equal(40, delays.Count);
        Assert.All(delays, delay => Assert.Equal(TimeSpan.FromSeconds(30), delay));
        Assert.Contains("HTTP 403", exception.Message);
        Assert.Contains(Authority.AppConfigEndpoint, exception.Message);
        Assert.Contains("App Configuration Data Owner", exception.Message);
        Assert.Contains("network", exception.Message);
        Assert.DoesNotContain("test-access-token", exception.Message);
        Assert.DoesNotContain("JSONDecodeError", exception.Message);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task Non_authorization_propagation_failures_are_not_retried(HttpStatusCode statusCode)
    {
        var handler = new StatusHandler(statusCode);
        var client = new AzureCliOwnerBootstrapClient(
            TokenRunner(), appConfigHttpClient: new HttpClient(handler),
            authorizationRetryDelay: (_, _) => throw new InvalidOperationException("Unexpected retry."));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.WriteAsync(Authority, Request, Status, CancellationToken.None));

        Assert.Single(handler.Bodies);
        Assert.Contains($"HTTP {(int)statusCode}", exception.Message);
    }

    [Fact]
    public async Task Cancellation_during_authorization_wait_stops_without_another_write()
    {
        var handler = new StatusHandler(HttpStatusCode.Forbidden);
        using var cancellation = new CancellationTokenSource();
        var client = new AzureCliOwnerBootstrapClient(
            TokenRunner(), appConfigHttpClient: new HttpClient(handler),
            authorizationRetryDelay: (_, token) =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.WriteAsync(Authority, Request, Status, cancellation.Token));

        Assert.Single(handler.Bodies);
    }

    [Fact]
    public async Task Command_exceptions_use_dsf_error_output_instead_of_framework_tracebacks()
    {
        var terminal = new ScriptedTerminal(new TerminalCapabilities(false, false, false), []);
        var root = new RootCommand();
        root.SetAction((_, _) => Task.FromException(
            new InvalidOperationException("App Configuration status write failed: HTTP 403.")));

        var exitCode = await CliApplication.InvokeAsync(root.Parse([]), terminal, CancellationToken.None);

        Assert.Equal(1, exitCode);
        Assert.Equal(
            "[dsf] error: App Configuration status write failed: HTTP 403." + Environment.NewLine,
            terminal.Error);
    }

    [Fact]
    public async Task Token_failure_stops_before_sending_a_status_request()
    {
        var runner = new RecordingAzureCliRunner(new AzureCliInvocationResult(1, "", "login failure"));
        var handler = new StatusHandler(HttpStatusCode.OK);
        var client = new AzureCliOwnerBootstrapClient(runner, appConfigHttpClient: new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.WriteAsync(Authority, Request, Status, CancellationToken.None));

        Assert.Empty(handler.Bodies);
        Assert.Contains("az login", exception.Message);
    }

    [Fact]
    public async Task Private_key_material_is_rejected_before_token_acquisition()
    {
        var runner = TokenRunner();
        var handler = new StatusHandler(HttpStatusCode.OK);
        var client = new AzureCliOwnerBootstrapClient(runner, appConfigHttpClient: new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.WriteAsync(
                Authority, Request, Status with { Error = "-----BEGIN PRIVATE KEY-----" }, CancellationToken.None));

        Assert.Empty(runner.Invocations);
        Assert.Empty(handler.Bodies);
    }

    private static RecordingAzureCliRunner TokenRunner() =>
        new(Enumerable.Repeat(new AzureCliInvocationResult(0, "test-access-token", ""), 41).ToArray());

    private sealed class StatusHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public Uri? Uri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string? AuthScheme { get; private set; }
        public string? AuthToken { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            Method = request.Method;
            AuthScheme = request.Headers.Authorization?.Scheme;
            AuthToken = request.Headers.Authorization?.Parameter;
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(statuses[Math.Min(Bodies.Count - 1, statuses.Length - 1)])
            {
                Content = new StringContent(""),
            };
        }
    }
}
