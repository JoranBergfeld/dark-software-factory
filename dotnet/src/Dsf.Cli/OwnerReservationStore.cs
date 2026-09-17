using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;
using Dsf.Core.Runtime;

namespace Dsf.Cli;

internal sealed class ReservationStoreException(FactoryReservationState state, string detail) : Exception(detail)
{
    public FactoryReservationState State { get; } = state;
}

/// <summary>Only per-key+label conditional writes, authenticated as the selected Azure CLI operator.</summary>
internal sealed class OwnerReservationStore : IFactoryCharterEvidenceReader
{
    public const string ContentType = "application/vnd.dsf.reservation+json;version=1";
    public const int RecordLimit = 10 * 1024;
    private readonly ConfigurationClient client;
    private readonly Func<Guid?, ConfigurationClient> createClient;

    public OwnerReservationStore(string endpoint, string tenant, string subscription, IAzureCliRunner azure, HttpClient? http, string? expectedActor = null)
    {
        createClient = correlation =>
        {
            var options = new ConfigurationClientOptions { Retry = { MaxRetries = 0 } };
            if (http is not null) options.Transport = new HttpClientTransport(http);
            if (correlation is not null) options.AddPolicy(new CorrelationPolicy(correlation.Value), HttpPipelinePosition.PerRetry);
            return new ConfigurationClient(new Uri(endpoint), new OperatorCredential(azure, tenant, subscription, expectedActor), options);
        };
        client = createClient(null);
    }

    public async Task<ConfigurationSetting?> ReadAsync(string key, CancellationToken token)
    {
        try
        {
            var record = (await client.GetConfigurationSettingAsync(key, cancellationToken: token)).Value;
            VerifyRecord(record, key);
            return record;
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ReservationStoreException(FactoryReservationState.Unknown, "Owner reservation read timed out; absence was not established."); }
        catch (Exception e) when (e is RequestFailedException or HttpRequestException or AuthenticationFailedException
            or JsonException or FormatException or InvalidOperationException)
        { throw new ReservationStoreException(FactoryReservationState.Unknown, "Owner reservation read unavailable; provider details withheld."); }
    }

    async Task<CharterEvidenceRecord?> IFactoryCharterEvidenceReader.ReadAsync(string key, CancellationToken token)
    {
        var record = await ReadAsync(key, token);
        return record is null ? null : new(record.Key, record.Label, record.Value, record.ETag.ToString(), record.ContentType);
    }

    Task IFactoryCharterEvidenceReader.RequirePristineAsync(string prefix, CancellationToken token) =>
        RequirePristineAsync(prefix, token);

    internal async Task RequirePristineAsync(string prefix, CancellationToken token)
    {
        try
        {
            var selector = new SettingSelector { KeyFilter = prefix + "*", LabelFilter = "*", Fields = SettingFields.Key };
            await using var pages = client.GetConfigurationSettingsAsync(selector, token).AsPages().GetAsyncEnumerator(token);
            if (!await pages.MoveNextAsync()) throw new JsonException();
            var page = pages.Current;
            var response = page.GetRawResponse();
            if (response.Content.ToMemory().Length > 4 * 1024 * 1024) throw new JsonException();
            using var document = JsonDocument.Parse(response.Content.ToMemory(), new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() != 0 || page.Values.Count != 0 || !string.IsNullOrEmpty(page.ContinuationToken)
                || response.Headers.TryGetValue("Link", out var link) && !string.IsNullOrEmpty(link))
                throw new ReservationStoreException(FactoryReservationState.Unknown,
                    "Authoritative charter coordinator missing or namespace absence unconfirmed; surviving history forbids recreation.");
        }
        catch (Exception e) when (e is RequestFailedException or HttpRequestException or AuthenticationFailedException
            or JsonException or FormatException or InvalidOperationException)
        { throw new ReservationStoreException(FactoryReservationState.Unknown, "Charter namespace absence unconfirmed; no new operation."); }
    }

    internal async Task<IReadOnlyDictionary<string, ConfigurationSetting>> ReadNamespaceAsync(string prefix, CancellationToken token)
    {
        var records = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        var bytes = 0;
        var count = 0;
        try
        {
            var selector = new SettingSelector { KeyFilter = prefix + "*", LabelFilter = "*" };
            await foreach (var page in client.GetConfigurationSettingsAsync(selector, token).AsPages())
            {
                if (++count > 64 || (bytes += page.GetRawResponse().Content.ToMemory().Length) > 4 * 1024 * 1024)
                    throw new JsonException();
                using var native = JsonDocument.Parse(page.GetRawResponse().Content.ToMemory(),
                    new JsonDocumentOptions { AllowDuplicateProperties = false });
                if (native.RootElement.GetProperty("items").GetArrayLength() != page.Values.Count) throw new JsonException();
                foreach (var record in page.Values)
                {
                    VerifyRecord(record, record.Key);
                    if (!record.Key.StartsWith(prefix, StringComparison.Ordinal) || !records.TryAdd(record.Key, record)
                        || records.Count > 1024) throw new JsonException();
                    CheckSize(record.Key, ProductConfigurationKeys.NoLabel, record.Value, record.ContentType);
                }
            }
            if (count == 0) throw new JsonException();
            return records;
        }
        catch (Exception e) when (e is RequestFailedException or HttpRequestException or AuthenticationFailedException
            or JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
        { throw new ReservationStoreException(FactoryReservationState.Unknown, "Complete unlabelled product record namespace unconfirmed; no absence or current authority asserted."); }
    }

    public async Task<ConfigurationSetting> WriteAsync(string key, string value, ETag? revision, CancellationToken token, Guid? correlation = null,
        string? contentType = null)
    {
        CheckSize(key, ProductConfigurationKeys.NoLabel, value, contentType);
        var setting = new ConfigurationSetting(key, value, null, revision ?? default) { ContentType = contentType ?? ContentType };
        try
        {
            token.ThrowIfCancellationRequested();
            var writer = correlation is null ? client : createClient(correlation);
            var record = revision is null
                ? (await writer.AddConfigurationSettingAsync(setting, token)).Value
                : (await writer.SetConfigurationSettingAsync(setting, onlyIfUnchanged: true, token)).Value;
            VerifyRecord(record, key);
            if (record.Value != value || record.ContentType != setting.ContentType)
                throw new ReservationStoreException(FactoryReservationState.Unknown, "Owner response did not confirm the exact conditional value; reconcile remote state.");
            return record;
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        { throw new ReservationStoreException(FactoryReservationState.Blocked, "Conditional owner key/label conflict; no overwrite or release."); }
        catch (RequestFailedException e) when (e.Status is 400 or 401 or 403 or 413)
        { throw new ReservationStoreException(FactoryReservationState.Failed, "Owner rejected the conditional write; no provisioning. Provider details withheld."); }
        catch (AuthenticationFailedException)
        { throw new ReservationStoreException(FactoryReservationState.Failed, "Current Azure CLI registry identity is unavailable or differs from the reviewed operator; no write dispatched."); }
        catch (Exception e) when (e is RequestFailedException or HttpRequestException or OperationCanceledException
            or JsonException or FormatException or InvalidOperationException)
        { throw new ReservationStoreException(FactoryReservationState.Unknown, "Conditional write response uncertain; inspect the same operation, never replace identity."); }
    }

    private static void VerifyRecord(ConfigurationSetting record, string key)
    {
        if (record.Key != key || record.Label is not null || string.IsNullOrEmpty(record.ETag.ToString()) || record.Value is null)
            throw new ReservationStoreException(FactoryReservationState.Unknown, "Owner response did not identify the exact unlabelled record and revision; provider details withheld.");
    }

    public static void CheckSize(string key, string label, string value, string? contentType = null)
    {
        if (RecordSizeBytes(key, label, value) + Encoding.UTF8.GetByteCount(contentType ?? ContentType) - Encoding.UTF8.GetByteCount(ContentType) > RecordLimit)
            throw new ReservationStoreException(FactoryReservationState.Failed, "Reservation record exceeds the bounded 10 KB App Configuration budget; nothing was truncated.");
    }

    // Count UTF-8 keys/labels/value/content-type and conservatively budget service metadata.
    public static int RecordSizeBytes(string key, string label, string value) =>
        Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(label)
        + Encoding.UTF8.GetByteCount(value) + Encoding.UTF8.GetByteCount(ContentType) + 1024;

    internal sealed class AuthenticationFailedException : Exception;

    private sealed class CorrelationPolicy(Guid correlation) : HttpPipelineSynchronousPolicy
    {
        public override void OnSendingRequest(HttpMessage message)
        {
            message.Request.ClientRequestId = correlation.ToString("D");
            message.Request.Headers.SetValue("x-ms-client-request-id", correlation.ToString("D"));
        }
    }

    internal sealed class OperatorCredential(IAzureCliRunner azure, string tenant, string subscription, string? expectedActor,
        string resource = "https://azconfig.io") : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var result = await azure.RunAsync(["account", "get-access-token", "--subscription", subscription,
                "--resource", resource, "--output", "json"], cancellationToken);
            if (result.ExitCode != 0) throw new AuthenticationFailedException();
            try
            {
                using var json = JsonDocument.Parse(result.StandardOutput);
                var body = json.RootElement;
                if (Guid.Parse(AzureBoundaryReader.Text(body, "tenant")).ToString() != tenant
                    || Guid.Parse(AzureBoundaryReader.Text(body, "subscription")).ToString() != subscription)
                    throw new AuthenticationFailedException();
                var token = AzureBoundaryReader.Text(body, "accessToken");
                var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
                using var claims = JsonDocument.Parse(Convert.FromBase64String(part.PadRight((part.Length + 3) / 4 * 4, '=')));
                var actor = Guid.Parse(AzureBoundaryReader.Text(claims.RootElement, "oid"));
                if (Guid.Parse(AzureBoundaryReader.Text(claims.RootElement, "tid")).ToString() != tenant
                    || actor == Guid.Empty || expectedActor is not null && actor.ToString() != expectedActor)
                    throw new AuthenticationFailedException();
                var expiry = DateTimeOffset.FromUnixTimeSeconds(claims.RootElement.GetProperty("exp").GetInt64());
                if (expiry <= DateTimeOffset.UtcNow) throw new AuthenticationFailedException();
                return new(token, expiry);
            }
            catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or KeyNotFoundException or IndexOutOfRangeException or ArgumentOutOfRangeException)
            { throw new AuthenticationFailedException(); }
        }
    }
}
