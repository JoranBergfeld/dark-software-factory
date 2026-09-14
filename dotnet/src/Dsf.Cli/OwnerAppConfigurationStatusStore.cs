using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class OwnerAppConfigurationStatusStore(
    IAzureCliRunner runner,
    HttpClient httpClient,
    ICliTerminal? terminal = null,
    Func<TimeSpan, CancellationToken, Task>? authorizationRetryDelay = null)
    : IOwnerBootstrapStatusStore
{
    // Leave a margin beyond Azure's documented 15-minute RBAC propagation window.
    private const int AuthorizationRetries = 40;
    private const string Audience = "https://appconfig.azure.com";
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(30);

    public async Task WriteAsync(
        OwnerAuthority authority,
        OwnerBootstrapRequest request,
        OwnerBootstrapStatus status,
        CancellationToken cancellationToken)
    {
        var value = JsonSerializer.Serialize(status);
        if (value.Contains("PRIVATE KEY", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Bootstrap status must not contain private-key material.");
        }

        var key = ProductConfigurationKeys.OwnerBootstrapStatus(request.AppName);
        var uri = new Uri(
            $"{authority.AppConfigEndpoint.TrimEnd('/')}/kv/{Uri.EscapeDataString(key)}?api-version=1.0");
        var payload = JsonSerializer.Serialize(new { value, content_type = "application/json" });

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = await AccessTokenAsync(cancellationToken);
            using var message = new HttpRequestMessage(HttpMethod.Put, uri)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await httpClient.SendAsync(message, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                if (attempt > 0)
                {
                    terminal?.WriteLine("[dsf] App Configuration data-plane write succeeded.");
                }

                return;
            }

            if (response.StatusCode != HttpStatusCode.Forbidden || attempt == AuthorizationRetries)
            {
                var guidance = response.StatusCode switch
                {
                    HttpStatusCode.Forbidden =>
                        $"Access is still denied after {AuthorizationRetries} authorization retries. "
                        + "Verify App Configuration Data Owner for the signed-in operator and the authorized network path "
                        + "(public access policy, private endpoint and DNS). Do not relax network policy. "
                        + "Resolve access, then rerun the same bootstrap command.",
                    HttpStatusCode.Unauthorized =>
                        "Run az login for the intended tenant and subscription, then rerun bootstrap.",
                    _ => "Resolve the App Configuration service error, then rerun bootstrap.",
                };
                throw new InvalidOperationException(
                    $"Owner App Configuration status write to '{authority.AppConfigEndpoint}' failed: "
                    + $"HTTP {(int)response.StatusCode} ({response.StatusCode}). {guidance}");
            }

            if (attempt == 0)
            {
                terminal?.WriteLine(
                    "[dsf] Waiting for App Configuration authorization: new role assignments can take up to 15 minutes. "
                    + "Allowing 20 minutes of retry waits. HTTP 403 can also indicate network restrictions; Ctrl+C cancels.");
            }

            terminal?.WriteLine(
                $"[dsf] App Configuration HTTP 403; retry {attempt + 1}/{AuthorizationRetries} in 30 seconds...");
            await (authorizationRetryDelay ?? Task.Delay)(RetryInterval, cancellationToken);
        }
    }

    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            ["account", "get-access-token", "--resource", Audience, "--query", "accessToken", "--output", "tsv"],
            cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            throw new InvalidOperationException(
                $"Could not acquire an Azure App Configuration token for '{Audience}' "
                + $"(az account get-access-token exit code {result.ExitCode}). "
                + "Run az login for the intended tenant and subscription.");
        }

        return result.StandardOutput.Trim();
    }
}
