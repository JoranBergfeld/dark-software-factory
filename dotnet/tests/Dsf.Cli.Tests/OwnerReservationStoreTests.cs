using System.Net;
using System.Text;
using System.Text.Json;
using Azure;
using Dsf.Cli;
using Dsf.Core.Onboarding;
using Xunit;

namespace Dsf.Cli.Tests;

public sealed class OwnerReservationStoreTests
{
    [Fact]
    public async Task Registry_adapter_explicitly_requests_JSON_for_the_selected_operator_token()
    {
        var azure = new BoundaryPlanFixture();
        using var http = new HttpClient(new ContractHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Reply(HttpStatusCode.NotFound, "{}");
        }));
        var store = new OwnerReservationStore("https://owner.azconfig.io", BoundaryPlanFixture.Tenant,
            BoundaryPlanFixture.Subscription, azure, http, BoundaryPlanFixture.Principal);

        var record = await store.ReadAsync("key", CancellationToken.None);

        Assert.Null(record);
        Assert.Equal(
            ["account", "get-access-token", "--subscription", BoundaryPlanFixture.Subscription,
                "--resource", "https://azconfig.io", "--output", "json"],
            Assert.Single(azure.Calls));
    }

    [Theory]
    [InlineData(false, "json")]
    [InlineData(true, "json")]
    [InlineData(false, "last_modified")]
    [InlineData(true, "last_modified")]
    [InlineData(false, "locked")]
    [InlineData(true, "locked")]
    [InlineData(false, "value")]
    [InlineData(true, "value")]
    public async Task Malformed_provider_receipts_are_unknown_not_operator_input_failures(bool write, string field)
    {
        var record = new Dictionary<string, object?>
        {
            ["key"] = "key", ["label"] = null, ["value"] = "value", ["etag"] = "current",
            ["content_type"] = OwnerReservationStore.ContentType, ["locked"] = false,
            ["last_modified"] = "2026-09-15T23:00:00Z", ["tags"] = new Dictionary<string, string>(),
        };
        record[field] = field == "value" ? new[] { "secret-provider-body" } : "secret-provider-body";
        var body = field == "json" ? "{secret-provider-body" : JsonSerializer.Serialize(record);
        var attempts = 0;
        using var http = new HttpClient(new ContractHandler(_ =>
        {
            attempts++;
            return Reply(HttpStatusCode.OK, body);
        }));
        var store = Store(http);

        var error = await Assert.ThrowsAsync<ReservationStoreException>(async () =>
        {
            if (write) await store.WriteAsync("key", "value", null, CancellationToken.None);
            else await store.ReadAsync("key", CancellationToken.None);
        });

        Assert.Equal(FactoryReservationState.Unknown, error.State);
        Assert.Equal(1, attempts);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("secret-provider-body", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unrelated_adapter_errors_are_not_masked_as_malformed_provider_receipts(bool write)
    {
        using var http = new HttpClient(new ContractHandler(_ => throw new NotSupportedException("unexpected implementation failure")));
        var store = Store(http);

        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            if (write) await store.WriteAsync("key", "value", null, CancellationToken.None);
            else await store.ReadAsync("key", CancellationToken.None);
        });
    }

    [Theory]
    [InlineData("key")]
    [InlineData("label")]
    [InlineData("value")]
    [InlineData("etag")]
    public async Task Successful_status_code_without_the_exact_conditional_record_is_unknown(string field)
    {
        var record = new Dictionary<string, object?>
        {
            ["key"] = "key", ["label"] = null, ["value"] = "value", ["etag"] = "current",
            ["content_type"] = OwnerReservationStore.ContentType, ["locked"] = false, ["tags"] = new Dictionary<string, string>(),
        };
        record[field] = field == "etag" ? null : "different";
        using var http = new HttpClient(new ContractHandler(_ => Reply(HttpStatusCode.OK, JsonSerializer.Serialize(record))));

        var error = await Assert.ThrowsAsync<ReservationStoreException>(() => Store(http).WriteAsync("key", "value", null, CancellationToken.None));

        Assert.Equal(FactoryReservationState.Unknown, error.State);
    }

    [Fact]
    public async Task Read_timeout_is_explicit_unknown_not_a_missing_record_or_raw_transport_error()
    {
        using var http = new HttpClient(new ContractHandler(_ => throw new TaskCanceledException("secret-transport-body")));
        var store = Store(http);

        var error = await Assert.ThrowsAsync<ReservationStoreException>(() => store.ReadAsync("key", CancellationToken.None));

        Assert.Equal(FactoryReservationState.Unknown, error.State);
        Assert.DoesNotContain("secret-transport-body", error.Message);
    }

    [Fact]
    public async Task Sdk_creates_only_absent_exact_unlabelled_key_and_updates_only_the_supplied_opaque_etag()
    {
        var requests = new List<(string? Match, string? None)>();
        using var http = new HttpClient(new ContractHandler(request =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal("/kv/a%2Fb", request.RequestUri!.AbsolutePath);
            Assert.DoesNotContain("label=", request.RequestUri.Query);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            requests.Add((request.Headers.TryGetValues("If-Match", out var matches) ? matches.Single() : null,
                request.Headers.TryGetValues("If-None-Match", out var nones) ? nones.Single() : null));
            return Reply(HttpStatusCode.OK, """
                {"key":"a/b","label":null,"value":"data","content_type":"application/json","etag":"opaque:A-1",
                 "last_modified":"2026-09-15T23:00:00Z","locked":false,"tags":{}}
                """);
        }));
        var store = Store(http);

        var created = await store.WriteAsync("a/b", "data", null, CancellationToken.None);
        await store.WriteAsync("a/b", "data", created.ETag, CancellationToken.None);

        Assert.Equal([(null, "*"), ("\"opaque:A-1\"", null)], requests);
    }

    [Theory]
    [InlineData(409, FactoryReservationState.Blocked)]
    [InlineData(412, FactoryReservationState.Blocked)]
    [InlineData(400, FactoryReservationState.Failed)]
    [InlineData(401, FactoryReservationState.Failed)]
    [InlineData(403, FactoryReservationState.Failed)]
    [InlineData(413, FactoryReservationState.Failed)]
    [InlineData(429, FactoryReservationState.Unknown)]
    [InlineData(500, FactoryReservationState.Unknown)]
    [InlineData(503, FactoryReservationState.Unknown)]
    public async Task Conditional_provider_outcomes_are_redacted_and_never_automatically_retried(int status, FactoryReservationState expected)
    {
        var attempts = 0;
        using var http = new HttpClient(new ContractHandler(_ =>
        {
            attempts++;
            return Reply((HttpStatusCode)status, """{"error":{"message":"secret-provider-body"}}""");
        }));

        var error = await Assert.ThrowsAsync<ReservationStoreException>(
            () => Store(http).WriteAsync("key", "{}", new ETag("previous"), CancellationToken.None));

        Assert.Equal(expected, error.State);
        Assert.Equal(1, attempts);
        Assert.DoesNotContain("secret-provider-body", error.Message);
    }

    [Fact]
    public async Task Record_budget_counts_multibyte_value_key_and_metadata_before_dispatch()
    {
        var attempts = 0;
        using var http = new HttpClient(new ContractHandler(_ => { attempts++; return Reply(HttpStatusCode.OK, "{}"); }));

        var error = await Assert.ThrowsAsync<ReservationStoreException>(
            () => Store(http).WriteAsync(new string('k', 512), new string('\u20ac', 3000), null, CancellationToken.None));

        Assert.Equal(FactoryReservationState.Failed, error.State);
        Assert.Equal(0, attempts);
    }

    private static OwnerReservationStore Store(HttpClient http) => new("https://owner.azconfig.io",
        BoundaryPlanFixture.Tenant, BoundaryPlanFixture.Subscription, new BoundaryPlanFixture(), http);
    private static HttpResponseMessage Reply(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/vnd.microsoft.appconfig.kv+json"),
    };
    private sealed class ContractHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
