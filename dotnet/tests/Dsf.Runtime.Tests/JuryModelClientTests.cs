using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Dsf.Core.Runtime;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class JuryModelClientTests
{
    [Theory]
    [InlineData("length", "GO: truncated rationale")]
    [InlineData("content_filter", "GO: filtered")]
    [InlineData("stop", "")]
    public async Task Incomplete_provider_completions_are_errors(string finishReason, string content)
    {
        var client = new AzureFoundryJuryModelClient(
            new JurorModelSettings("juror-1", "openai", "gpt", "https://models.example", "judge"),
            new JuryCredential(), new HttpClient(new CompletionHandler("gpt-4.1", finishReason, content)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CompleteAsync("judge", CancellationToken.None));
    }

    [Theory]
    [InlineData("length", "GO: truncated rationale")]
    [InlineData("content_filter", "GO: filtered")]
    [InlineData("stop", "")]
    public async Task Incomplete_deliberation_completions_are_errors(string finishReason, string content)
    {
        var gateway = new AzureOpenAiCompletionGateway(new JuryCredential(),
            new HttpClient(new CompletionHandler("gpt-4.1", finishReason, content)));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gateway.CompleteAsync("https://models.example", "judge", "deliberate", CancellationToken.None));
    }

    [Theory]
    [InlineData("openai", "gpt", "gpt-4.1-2025-04-14")]
    [InlineData("deepseek", "deepseek", "DeepSeek-V3")]
    [InlineData("xai", "grok", "grok-3")]
    public async Task Uses_distinct_configured_deployment_with_managed_identity_and_checks_actual_model(
        string provider, string family, string model)
    {
        var handler = new CompletionHandler(model);
        var credential = new JuryCredential();
        var client = new AzureFoundryJuryModelClient(
            new JurorModelSettings("juror", provider, family, "https://models.example", "judge-" + family),
            credential, new HttpClient(handler));

        Assert.Equal("GO: supported by evidence", await client.CompleteAsync("judge", CancellationToken.None));
        Assert.Equal("https://models.example/openai/v1/chat/completions", handler.Uri);
        Assert.Equal("judge-" + family, handler.Deployment);
        Assert.Equal("Bearer test-token", handler.Authorization);
        Assert.Equal(["https://ai.azure.com/.default"], credential.Scopes);
    }

    [Fact]
    public async Task Cannot_label_the_same_gpt_model_as_a_different_family()
    {
        var client = new AzureFoundryJuryModelClient(
            new JurorModelSettings("juror-2", "deepseek", "deepseek", "https://models.example", "alias"),
            new JuryCredential(), new HttpClient(new CompletionHandler("gpt-4.1")));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.CompleteAsync("judge", CancellationToken.None));

        Assert.Contains("juror-2", exception.Message);
        Assert.Contains("gpt-4.1", exception.Message);
        Assert.Contains("deepseek", exception.Message);
    }

    private sealed class JuryCredential : TokenCredential
    {
        public string[] Scopes { get; private set; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Scopes = requestContext.Scopes;
            return new AccessToken("test-token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class CompletionHandler(
        string model, string finishReason = "stop", string content = "GO: supported by evidence") : HttpMessageHandler
    {
        public string? Uri { get; private set; }
        public string? Deployment { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri!.ToString();
            Authorization = request.Headers.Authorization?.ToString();
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Deployment = body.RootElement.TryGetProperty("model", out var deployment) ? deployment.GetString() : null;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    model,
                    choices = new[] { new { finish_reason = finishReason, message = new { content } } },
                }), Encoding.UTF8, "application/json"),
            };
        }
    }
}
