using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core.Pipeline;
using Azure.Data.AppConfiguration;
using Dsf.Core.Onboarding;

namespace Dsf.Runtime;

internal sealed class SourceProgressException(SourceState state, string reason) : SourceAcquisitionException(state, reason);

internal sealed class SourceProgressStore
{
    private const string ContentType = "application/vnd.dsf.source+json;version=1";
    private readonly ConfigurationClient client;
    internal SourceProgressStore(FactorySourceAccess access, HttpClient http)
    {
        var options = new ConfigurationClientOptions { Retry = { MaxRetries = 0 }, Transport = new HttpClientTransport(http) };
        client = new(new Uri(FactorySourceValidation.ProgressEndpoint(access.Manifest.Identity.FactoryId)), access.Credential, options);
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
        var json = JsonSerializer.Serialize(value, FactoryGitHubJson.Options);
        if (RecordSize(key, json) > FactorySourceValidation.RecordBytes)
            throw new SourceAcquisitionException(SourceState.Partial, "MetadataRecordLimit");
        var setting = new ConfigurationSetting(key, json, null, etag ?? default) { ContentType = ContentType };
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
    private static void Check(ConfigurationSetting record, string key)
    {
        if (record.Key != key || record.Label is not null || record.ContentType != ContentType || record.ETag.ToString().Length == 0
            || RecordSize(record.Key, record.Value) > FactorySourceValidation.RecordBytes)
            throw new SourceProgressException(SourceState.Unknown, "InvalidProgressRecord");
    }
}
