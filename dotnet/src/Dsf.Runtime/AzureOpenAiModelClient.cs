using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>
/// Sends one chat completion to an Azure OpenAI deployment. The real
/// implementation talks to the data plane with the runtime's managed identity;
/// tests substitute a hand-written double rather than a live deployment.
/// </summary>
internal interface IModelCompletionGateway
{
    Task<string> CompleteAsync(string endpoint, string deployment, string prompt, CancellationToken cancellationToken);
}

/// <summary>
/// The Azure OpenAI data-plane gateway: an Entra-authenticated chat completion
/// request, using the same <see cref="DefaultAzureCredential"/> path as the rest
/// of the runtime so a Container App needs no API keys.
/// </summary>
internal sealed class AzureOpenAiCompletionGateway(TokenCredential? credential = null, HttpClient? httpClient = null)
    : IModelCompletionGateway
{
    private const string ApiVersion = "2024-06-01";
    private static readonly string[] Scopes = ["https://cognitiveservices.azure.com/.default"];

    private readonly TokenCredential credential = credential ?? new DefaultAzureCredential();
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public async Task<string> CompleteAsync(
        string endpoint, string deployment, string prompt, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(new TokenRequestContext(Scopes), cancellationToken);
        var uri = new Uri(
            new Uri(EnsureTrailingSlash(endpoint)),
            $"openai/deployments/{deployment}/chat/completions?api-version={ApiVersion}");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(new
            {
                messages = new[] { new { role = "user", content = prompt } },
                max_tokens = 400,
                temperature = 0.2,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Azure OpenAI refused a completion at deployment '{deployment}' ({(int)response.StatusCode}): {body}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new InvalidOperationException(
                $"Azure OpenAI answered no choices for deployment '{deployment}': {body}");
        }

        return choices[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private static string EnsureTrailingSlash(string url) => url.EndsWith('/') ? url : url + "/";
}

/// <summary>
/// The model client synthesis and council reason with, backed by a real Azure
/// OpenAI chat completions deployment over the runtime's existing
/// <c>AZURE_OPENAI_ENDPOINT</c>/<c>AZURE_OPENAI_DEPLOYMENT</c> settings and
/// managed-identity auth. A failed completion surfaces as an
/// <see cref="InvalidOperationException"/> naming the deployment and endpoint, so
/// the conveyor line's existing per-station error handling turns it into an
/// audited terminal run rather than a silently skipped reasoning step.
/// </summary>
internal sealed class AzureOpenAiModelClient(string endpoint, string deployment, IModelCompletionGateway gateway)
    : IModelClient
{
    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        try
        {
            return await gateway.CompleteAsync(endpoint, deployment, prompt, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"model completion against deployment '{deployment}' at '{endpoint}' failed: {exception.Message}",
                exception);
        }
    }
}
