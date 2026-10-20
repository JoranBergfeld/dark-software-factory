using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Azure;
using Azure.Core.Pipeline;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class SourceProgressException(SourceState state, string reason) : SourceAcquisitionException(state, reason);

internal sealed class SourceProgressStore
{
    private const string ContentType = "application/vnd.dsf.source+json;version=1";
    private const int MaximumListingPages = 1024;
    private readonly ConfigurationClient client;
    private readonly Uri endpoint;
    private readonly string purpose;
    private readonly FactorySourceAccess access;
    private string RecordContentType => purpose == "source" ? ContentType : "application/vnd.dsf.intent+json;version=1";
    internal SourceProgressStore(FactorySourceAccess access, HttpClient http, string purpose = "source")
    {
        if (purpose is not ("source" or "intent")) throw new ArgumentException("Unsupported progress namespace.", nameof(purpose));
        this.purpose = purpose;
        this.access = access;
        var options = new ConfigurationClientOptions { Retry = { MaxRetries = 0 }, Transport = new HttpClientTransport(http) };
        endpoint = new(FactorySourceValidation.ProgressEndpoint(access.Manifest.Identity.FactoryId));
        client = new(endpoint, access.Credential, options);
    }

    internal async Task RequirePristineAsync(CancellationToken token)
    {
        await foreach (var _ in ListAsync(keysOnly: true, token))
            throw new SourceProgressException(SourceState.Unknown, "MissingAuthoritativeCheckpoint");
    }

    internal async Task<IReadOnlyDictionary<string, ConfigurationSetting>> ReadNamespaceAsync(CancellationToken token)
    {
        var records = new Dictionary<string, ConfigurationSetting>(StringComparer.Ordinal);
        long bytes = 0;
        await foreach (var record in ListAsync(keysOnly: false, token))
        {
            bytes += RecordSize(record.Key, record.Value);
            if (records.Count >= FactorySourceValidation.MaximumRecords || bytes > FactorySourceValidation.MaximumMetadataBytes)
                throw new SourceProgressException(SourceState.Unknown, "ProgressAccountingLimit");
            if (!records.TryAdd(record.Key, record))
                throw new SourceProgressException(SourceState.Unknown, "DuplicateProgressRecord");
        }
        return records;
    }

    private async IAsyncEnumerable<ConfigurationSetting> ListAsync(bool keysOnly, [EnumeratorCancellation] CancellationToken token)
    {
        var selector = new SettingSelector { KeyFilter = purpose + "/*", LabelFilter = "*" };
        if (keysOnly) selector.Fields = SettingFields.Key;
        var pages = 0;
        await using var listing = client.GetConfigurationSettingsAsync(selector, token).AsPages().GetAsyncEnumerator(token);
        while (await MoveNextAsync(listing))
        {
            var page = listing.Current;
            pages++;
            if (page.GetRawResponse().Content.ToMemory().Length > 4 * 1024 * 1024)
                throw new SourceProgressException(SourceState.Unknown, "ProgressListingPageLimit");
            using var document = JsonDocument.Parse(page.GetRawResponse().Content.ToMemory(),
                new JsonDocumentOptions { AllowDuplicateProperties = false });
            if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() != page.Values.Count
                || page.GetRawResponse().Headers.TryGetValue("Link", out var link) && !string.IsNullOrEmpty(link)
                    && string.IsNullOrEmpty(page.ContinuationToken))
                throw new SourceProgressException(SourceState.Unknown, "InvalidProgressListing");
            foreach (var setting in page.Values)
            {
                if (setting.Key is null || !setting.Key.StartsWith(purpose + "/", StringComparison.Ordinal))
                    throw new SourceProgressException(SourceState.Unknown, "InvalidProgressListing");
                if (!keysOnly) Check(setting, setting.Key);
                yield return setting;
            }
            if (!string.IsNullOrEmpty(page.ContinuationToken))
            {
                if (pages >= MaximumListingPages)
                    throw new SourceProgressException(SourceState.Unknown, "ProgressListingLimit");
                var next = new Uri(endpoint, page.ContinuationToken);
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(next.Query);
                if (next.Scheme != endpoint.Scheme || next.Authority != endpoint.Authority || next.UserInfo.Length != 0
                    || next.Fragment.Length != 0 || next.AbsolutePath != "/kv"
                    || query["key"].Count != 1 || query["key"] != purpose + "/*" || query["label"] != "*"
                    || keysOnly && query["$select"] != "key")
                    throw new SourceProgressException(SourceState.Unknown, "UntrustedProgressContinuation");
            }
        }
    }

    private static async Task<bool> MoveNextAsync(IAsyncEnumerator<Page<ConfigurationSetting>> listing)
    {
        try { return await listing.MoveNextAsync(); }
        catch (RequestFailedException e) when (e.Status is 401 or 403)
        { throw new SourceProgressException(SourceState.Blocked, "ProgressPermissionDenied"); }
        catch (Exception e) when (e is RequestFailedException or HttpRequestException or JsonException or FormatException or InvalidOperationException or ArgumentException)
        { throw new SourceProgressException(SourceState.Unknown, "ProgressListingUnconfirmed"); }
    }

    internal async Task<ConfigurationSetting?> ReadAsync(string key, CancellationToken token)
    {
        try
        {
            var record = (await client.GetConfigurationSettingAsync(key, cancellationToken: token)).Value;
            Check(record, key);
            return record;
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
    }

    internal async Task<ConfigurationSetting> WriteAsync<T>(string key, T value, ETag? etag, CancellationToken token)
    {
        if (access.BeforeWork is not null) await access.BeforeWork(token);
        var json = JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
        if (RecordSize(key, json) > FactorySourceValidation.RecordBytes)
            throw new SourceAcquisitionException(SourceState.Partial, "MetadataRecordLimit");
        var setting = new ConfigurationSetting(key, json, null, etag ?? default) { ContentType = RecordContentType };
        try
        {
            var result = etag is null ? await client.AddConfigurationSettingAsync(setting, token)
                : await client.SetConfigurationSettingAsync(setting, onlyIfUnchanged: true, token);
            Check(result.Value, key);
            if (result.Value.Value != json) throw new SourceProgressException(SourceState.Unknown, "ProgressWriteUnconfirmed");
            return result.Value;
        }
        catch (RequestFailedException e) when (e.Status is 409 or 412)
        { throw new SourceProgressException(SourceState.Blocked, "ProgressConflict"); }
        catch (RequestFailedException e) when (e.Status is 401 or 403)
        { throw new SourceProgressException(SourceState.Blocked, "ProgressPermissionDenied"); }
        catch (Exception e) when (e is RequestFailedException or HttpRequestException or OperationCanceledException)
        { throw new SourceProgressException(SourceState.Unknown, "ProgressWriteUnconfirmed; inspect trusted remote checkpoint, no current hold asserted."); }
    }

    internal static T Parse<T>(ConfigurationSetting record)
    {
        try { return JsonSerializer.Deserialize<T>(record.Value, FactorySourceValidation.RecordJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new SourceProgressException(SourceState.Unknown, "InvalidProgressRecord"); }
    }
    internal static int RecordSize(string key, string value) => Encoding.UTF8.GetByteCount(key + value + ContentType) + 1024;
    private void Check(ConfigurationSetting record, string key)
    {
        if (record.Key != key || record.Label is not null || record.ContentType != RecordContentType || record.ETag.ToString().Length == 0
            || RecordSize(record.Key, record.Value) > FactorySourceValidation.RecordBytes)
            throw new SourceProgressException(SourceState.Unknown, "InvalidProgressRecord");
    }
}
