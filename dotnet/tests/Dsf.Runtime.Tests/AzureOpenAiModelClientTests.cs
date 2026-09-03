using Xunit;

namespace Dsf.Runtime.Tests;

/// <summary>
/// The model client wraps the completion gateway: it forwards the configured
/// endpoint/deployment and prompt, returns whatever text the gateway answered,
/// and turns a gateway failure into a message that names the deployment and
/// endpoint -- so a failed model call surfaces as an operator-actionable error
/// through the conveyor's existing per-station error path, not a raw HTTP
/// exception.
/// </summary>
public sealed class AzureOpenAiModelClientTests
{
    private sealed class RecordingGateway : IModelCompletionGateway
    {
        public (string Endpoint, string Deployment, string Prompt)? Requested { get; private set; }

        public Task<string> CompleteAsync(
            string endpoint, string deployment, string prompt, CancellationToken cancellationToken)
        {
            Requested = (endpoint, deployment, prompt);
            return Task.FromResult("a synthesized completion");
        }
    }

    private sealed class ThrowingGateway(string reason) : IModelCompletionGateway
    {
        public Task<string> CompleteAsync(
            string endpoint, string deployment, string prompt, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(reason);
    }

    [Fact]
    public async Task Forwards_the_configured_endpoint_and_deployment_with_the_prompt()
    {
        var gateway = new RecordingGateway();
        var client = new AzureOpenAiModelClient("https://acme-openai.example", "gpt-deploy", gateway);

        var result = await client.CompleteAsync("summarize this evidence", CancellationToken.None);

        Assert.Equal("a synthesized completion", result);
        Assert.Equal(("https://acme-openai.example", "gpt-deploy", "summarize this evidence"), gateway.Requested);
    }

    [Fact]
    public async Task A_gateway_failure_is_reported_naming_the_deployment_and_endpoint()
    {
        var client = new AzureOpenAiModelClient(
            "https://acme-openai.example", "gpt-deploy", new ThrowingGateway("401 Unauthorized"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CompleteAsync("summarize this evidence", CancellationToken.None));

        Assert.Contains("gpt-deploy", exception.Message);
        Assert.Contains("https://acme-openai.example", exception.Message);
        Assert.Contains("401 Unauthorized", exception.Message);
    }
}
