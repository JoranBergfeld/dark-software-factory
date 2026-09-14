using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Dsf.Core.Runtime;
using Dsf.FeatureCouncil.Conveyor;

namespace Dsf.Runtime;

/// <summary>Azure's v1 chat-completions API supports GPT, DeepSeek and Grok with Entra authentication.</summary>
internal sealed class AzureFoundryJuryModelClient(
    JurorModelSettings settings, TokenCredential? credential = null, HttpClient? httpClient = null) : IModelClient
{
    private readonly TokenCredential credential = credential ?? new DefaultAzureCredential();
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();

    public async Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(["https://ai.azure.com/.default"]), cancellationToken);
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"{settings.Endpoint.TrimEnd('/')}/openai/v1/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = settings.Deployment,
                messages = new[] { new { role = "user", content = prompt } },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"juror '{settings.Name}' deployment '{settings.Deployment}' at '{settings.Endpoint}' "
                + $"refused completion ({(int)response.StatusCode})");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        if (!document.RootElement.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String || !settings.MatchesModel(model.GetString()!))
        {
            throw new InvalidOperationException(
                $"juror '{settings.Name}' expected model family '{settings.Family}', "
                + $"but deployment '{settings.Deployment}' reported '{model}'");
        }

        return ModelCompletionResponse.ReadText(document.RootElement, $"juror '{settings.Name}'");
    }
}
