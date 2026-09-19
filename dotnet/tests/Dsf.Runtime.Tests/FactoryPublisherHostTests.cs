using System.Net;
using System.Net.Http.Json;
using Dsf.Core.Runtime;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.Runtime.Tests;

public sealed class FactoryPublisherHostTests
{
    [Theory]
    [InlineData("serve-publisher", "/publish")]
    [InlineData("serve-publication-context", "/publication-context")]
    public async Task Installed_native_publication_hosts_reject_untrusted_requests_before_provider_access(string verb, string route)
    {
        var runner = new UntrustedRunner(route);
        var error = new StringWriter();
        var settings = new Dictionary<string, string?>
        {
            ["DSF_PRODUCT"] = "product",
            ["AZURE_APPCONFIG_ENDPOINT"] = "https://original.azconfig.io",
            ["DSF_FACTORY_PROFILE_PIN"] = new('a', 64),
            ["AZURE_CLIENT_ID"] = "11111111-1111-1111-1111-111111111111",
        };
        var result = await RuntimeCliApplication.InvokeAsync([verb], settings, new StringWriter(), error,
            RuntimeDependencies.Production() with { WebHostRunner = runner }, CancellationToken.None);
        Assert.True(runner.Invoked, error.ToString());
        Assert.Equal(0, result);
        Assert.Equal(HttpStatusCode.Forbidden, runner.Status);
        Assert.Contains("NativeCallerRequired", runner.Response);
    }

    private sealed class UntrustedRunner(string route) : IWebHostRunner
    {
        internal bool Invoked { get; private set; }
        internal HttpStatusCode Status { get; private set; }
        internal string Response { get; private set; } = "";
        public async Task RunAsync(WebApplication application, CancellationToken cancellationToken)
        {
            Invoked = true;
            application.Urls.Clear();
            application.Urls.Add("http://127.0.0.1:0");
            await application.StartAsync(cancellationToken);
            using var http = new HttpClient();
            using var response = await http.PostAsJsonAsync(application.Urls.Single() + route,
                new { title = "untrusted payload cannot authorize mutation", liveFiling = true }, cancellationToken);
            Status = response.StatusCode;
            Response = await response.Content.ReadAsStringAsync(cancellationToken);
            await application.StopAsync(cancellationToken);
        }
    }
}
