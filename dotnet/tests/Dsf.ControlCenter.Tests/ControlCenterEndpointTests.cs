using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Dsf.ControlCenter;
using Microsoft.AspNetCore.Builder;
using Xunit;

namespace Dsf.ControlCenter.Tests;

/// <summary>
/// End-to-end checks over the real Control Center web process: product-first
/// governance reads, cookie + CSRF protected browser writes, validated numeric
/// policy inputs, and unsupported writes rendered disabled instead of silently
/// doing nothing.
/// </summary>
public sealed class ControlCenterEndpointTests
{
    private const string OperatorToken = "operator-secret";

    private static ControlCenterSettings Settings => new(
        OwnerAppConfigEndpoint: "https://owner.azconfig.io",
        OperatorToken: OperatorToken,
        Host: "127.0.0.1",
        Port: 0,
        RequireSecureCookies: false);

    private static RecordingProductPolicyAuthority SeededAuthority()
    {
        var authority = new RecordingProductPolicyAuthority();
        authority.Products.Add(new ProductSummary("wayfinder", "acme/wayfinder", "https://wayfinder.azconfig.io"));
        authority.Products.Add(new ProductSummary("atlas", "acme/atlas", "https://atlas.azconfig.io"));
        authority.Policies["wayfinder"] = new ProductPolicy(
            "wayfinder",
            "acme/wayfinder",
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                ["sentry"] = true,
                ["grafana"] = false,
                ["foundryiq"] = false,
                ["webiq"] = false,
                ["incidents"] = false,
                ["azuremonitor"] = false,
            },
            0.72d);
        return authority;
    }

    private sealed class Harness : IAsyncDisposable
    {
        public required WebApplication App { get; init; }

        public required HttpClient Client { get; init; }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }

    private static async Task<Harness> StartAsync(RecordingProductPolicyAuthority authority)
    {
        var app = ControlCenterApp.Build(Settings, authority);
        await app.StartAsync();
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(app.Urls.First().Replace("[::]", "127.0.0.1", StringComparison.Ordinal)),
        };
        return new Harness { App = app, Client = client };
    }

    private static FormUrlEncodedContent Form(params (string Key, string Value)[] fields) =>
        new(fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    private static async Task SignInAsync(HttpClient client)
    {
        var response = await client.PostAsync("/session", Form(("operator_token", OperatorToken)), CancellationToken.None);
        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
    }

    private static async Task<string> CsrfTokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path, CancellationToken.None);
        var match = Regex.Match(html, "name=\"csrf_token\" value=\"(?<token>[^\"]+)\"");
        Assert.True(match.Success, $"no CSRF token rendered on {path}");
        return match.Groups["token"].Value;
    }

    [Fact]
    public async Task Root_redirects_to_the_product_list()
    {
        await using var harness = await StartAsync(SeededAuthority());

        var response = await harness.Client.GetAsync("/", CancellationToken.None);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/products", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Signed_out_browser_gets_the_sign_in_page_not_the_product_list()
    {
        await using var harness = await StartAsync(SeededAuthority());

        var response = await harness.Client.GetAsync("/products", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("operator_token", html, StringComparison.Ordinal);
        Assert.DoesNotContain("wayfinder", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Wrong_operator_token_does_not_create_a_session()
    {
        await using var harness = await StartAsync(SeededAuthority());

        var response = await harness.Client.PostAsync("/session", Form(("operator_token", "guess")), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var products = await harness.Client.GetAsync("/products", CancellationToken.None);
        Assert.Equal(HttpStatusCode.Unauthorized, products.StatusCode);
    }

    [Fact]
    public async Task Signed_in_operator_sees_the_products_from_the_configuration_authority()
    {
        await using var harness = await StartAsync(SeededAuthority());
        await SignInAsync(harness.Client);

        var html = await harness.Client.GetStringAsync("/products", CancellationToken.None);

        Assert.Contains("wayfinder", html, StringComparison.Ordinal);
        Assert.Contains("atlas", html, StringComparison.Ordinal);
        Assert.Contains("acme/wayfinder", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Product_page_renders_the_effective_policy()
    {
        await using var harness = await StartAsync(SeededAuthority());
        await SignInAsync(harness.Client);

        var html = await harness.Client.GetStringAsync("/products/wayfinder", CancellationToken.None);

        Assert.Contains("0.72", html, StringComparison.Ordinal);
        Assert.Contains("sentry", html, StringComparison.Ordinal);
        Assert.Contains("grafana", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_writes_render_disabled_with_an_explanation()
    {
        await using var harness = await StartAsync(SeededAuthority());
        await SignInAsync(harness.Client);

        var html = await harness.Client.GetStringAsync("/products/wayfinder", CancellationToken.None);

        foreach (var control in UnsupportedControls.All)
        {
            Assert.Contains(WebUtility.HtmlEncode(control.Name), html, StringComparison.Ordinal);
            Assert.Contains(WebUtility.HtmlEncode(control.Reason), html, StringComparison.Ordinal);
            Assert.Contains(WebUtility.HtmlEncode(control.SupportedAlternative), html, StringComparison.Ordinal);
        }

        Assert.Contains("disabled", html, StringComparison.Ordinal);
        // Disabled controls must not be wired to a write endpoint at all.
        Assert.DoesNotContain("/critic-weights", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/triggers", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/dry-run", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unsupported_write_endpoints_do_not_exist()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);
        await SignInAsync(harness.Client);
        var csrf = await CsrfTokenAsync(harness.Client, "/products/wayfinder");

        foreach (var path in new[]
                 {
                     "/products/wayfinder/critic-weights",
                     "/products/wayfinder/triggers",
                     "/products/wayfinder/dry-run",
                 })
        {
            var response = await harness.Client.PostAsync(
                path,
                Form(("csrf_token", csrf), ("value", "1")),
                CancellationToken.None);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Empty(authority.ThresholdWrites);
        Assert.Empty(authority.AgentWrites);
    }

    [Fact]
    public async Task Browser_write_without_a_session_is_rejected()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);

        var response = await harness.Client.PostAsync(
            "/products/wayfinder/threshold",
            Form(("value", "0.8"), ("csrf_token", "anything")),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(authority.ThresholdWrites);
    }

    [Fact]
    public async Task Bearer_token_alone_cannot_perform_a_browser_write()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/products/wayfinder/threshold")
        {
            Content = Form(("value", "0.8"), ("csrf_token", "anything")),
        };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OperatorToken);

        var response = await harness.Client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(authority.ThresholdWrites);
    }

    [Fact]
    public async Task Browser_write_without_a_matching_csrf_token_is_rejected()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);
        await SignInAsync(harness.Client);

        var missing = await harness.Client.PostAsync(
            "/products/wayfinder/threshold",
            Form(("value", "0.8")),
            CancellationToken.None);
        var wrong = await harness.Client.PostAsync(
            "/products/wayfinder/threshold",
            Form(("value", "0.8"), ("csrf_token", "not-the-token")),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Empty(authority.ThresholdWrites);
    }

    [Fact]
    public async Task Cookie_plus_csrf_write_reaches_the_configuration_authority()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);
        await SignInAsync(harness.Client);
        var csrf = await CsrfTokenAsync(harness.Client, "/products/wayfinder");

        var response = await harness.Client.PostAsync(
            "/products/wayfinder/threshold",
            Form(("value", "0.8"), ("csrf_token", csrf)),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal("/products/wayfinder", response.Headers.Location?.ToString());
        Assert.Equal(("wayfinder", 0.8d), Assert.Single(authority.ThresholdWrites));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1.4")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task Invalid_numeric_policy_input_is_rejected_before_the_write(string value)
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);
        await SignInAsync(harness.Client);
        var csrf = await CsrfTokenAsync(harness.Client, "/products/wayfinder");

        var response = await harness.Client.PostAsync(
            "/products/wayfinder/threshold",
            Form(("value", value), ("csrf_token", csrf)),
            CancellationToken.None);
        var body = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("confidence threshold", body, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(authority.ThresholdWrites);
    }

    [Fact]
    public async Task Agent_enablement_write_requires_cookie_and_csrf_and_reaches_the_authority()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);
        await SignInAsync(harness.Client);
        var csrf = await CsrfTokenAsync(harness.Client, "/products/wayfinder");

        var response = await harness.Client.PostAsync(
            "/products/wayfinder/agents",
            Form(("kind", "grafana"), ("enabled", "true"), ("csrf_token", csrf)),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.SeeOther, response.StatusCode);
        Assert.Equal(("wayfinder", "grafana", true), Assert.Single(authority.AgentWrites));
    }

    [Fact]
    public async Task Unknown_agent_kind_is_rejected_before_the_write()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);
        await SignInAsync(harness.Client);
        var csrf = await CsrfTokenAsync(harness.Client, "/products/wayfinder");

        var response = await harness.Client.PostAsync(
            "/products/wayfinder/agents",
            Form(("kind", "pagerduty"), ("enabled", "true"), ("csrf_token", csrf)),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(authority.AgentWrites);
    }

    [Fact]
    public async Task Sign_out_invalidates_the_session()
    {
        await using var harness = await StartAsync(SeededAuthority());
        await SignInAsync(harness.Client);
        var csrf = await CsrfTokenAsync(harness.Client, "/products/wayfinder");

        var signOut = await harness.Client.PostAsync("/sign-out", Form(("csrf_token", csrf)), CancellationToken.None);
        var afterwards = await harness.Client.GetAsync("/products", CancellationToken.None);

        Assert.Equal(HttpStatusCode.SeeOther, signOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
    }

    [Fact]
    public async Task Unreachable_configuration_authority_is_reported_not_hidden()
    {
        var authority = SeededAuthority();
        authority.ListFailure = new InvalidOperationException(
            "failed to read App Configuration at 'https://owner.azconfig.io': network down");
        await using var harness = await StartAsync(authority);
        await SignInAsync(harness.Client);

        var response = await harness.Client.GetAsync("/products", CancellationToken.None);
        var html = await response.Content.ReadAsStringAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("https://owner.azconfig.io", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Api_reads_require_a_bearer_token()
    {
        await using var harness = await StartAsync(SeededAuthority());

        var anonymous = await harness.Client.GetAsync("/api/products", CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Fact]
    public async Task Api_serves_products_and_policy_to_automated_clients()
    {
        await using var harness = await StartAsync(SeededAuthority());
        harness.Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OperatorToken);

        var products = await harness.Client.GetFromJsonAsync<List<ProductSummaryPayload>>("/api/products", CancellationToken.None);
        var policy = await harness.Client.GetFromJsonAsync<ProductPolicyPayload>("/api/products/wayfinder", CancellationToken.None);

        Assert.NotNull(products);
        Assert.Contains(products!, p => p.Key == "wayfinder" && p.GitHubRepository == "acme/wayfinder");
        Assert.NotNull(policy);
        Assert.Equal(0.72d, policy!.ConfidenceThreshold);
        Assert.True(policy.AgentEnablement["sentry"]);
    }

    [Fact]
    public async Task Api_writes_validate_numeric_input_and_require_a_bearer_token()
    {
        var authority = SeededAuthority();
        await using var harness = await StartAsync(authority);

        var anonymous = await harness.Client.PostAsJsonAsync(
            "/api/products/wayfinder/threshold",
            new { value = 0.9d },
            CancellationToken.None);

        harness.Client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", OperatorToken);
        var invalid = await harness.Client.PostAsJsonAsync(
            "/api/products/wayfinder/threshold",
            new { value = 4.2d },
            CancellationToken.None);
        var valid = await harness.Client.PostAsJsonAsync(
            "/api/products/wayfinder/threshold",
            new { value = 0.9d },
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, valid.StatusCode);
        Assert.Equal(("wayfinder", 0.9d), Assert.Single(authority.ThresholdWrites));
    }

    private sealed record ProductSummaryPayload(string Key, string GitHubRepository, string AppConfigEndpoint);

    private sealed record ProductPolicyPayload(
        string Product,
        string GitHubRepository,
        Dictionary<string, bool> AgentEnablement,
        double ConfidenceThreshold);
}
