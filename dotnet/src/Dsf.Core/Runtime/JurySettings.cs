using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsf.Core.Runtime;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record JurorModelSettings(
    string Name,
    [property: JsonRequired] string Provider,
    [property: JsonRequired] string Family,
    [property: JsonRequired] string Endpoint,
    [property: JsonRequired] string Deployment)
{
    public bool MatchesModel(string model) =>
        model.StartsWith(Family + "-", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Three independently deployed model families, authenticated using Azure managed identity.</summary>
public sealed record JurySettings(IReadOnlyList<JurorModelSettings> Jurors, TimeSpan Timeout)
{
    public const string ModelsKey = "DSF_JURY_MODELS";
    public const string TimeoutSeconds = "DSF_JURY_TIMEOUT_SECONDS";
    public static IReadOnlyList<string> Keys { get; } = [ModelsKey, TimeoutSeconds];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JurySettings Read(IReadOnlyDictionary<string, string?> environment)
    {
        if (!environment.TryGetValue(ModelsKey, out var json) || string.IsNullOrWhiteSpace(json))
        {
            throw Invalid(ModelsKey, "must contain an explicit JSON roster of three diverse models");
        }

        var timeout = 120;
        if (environment.TryGetValue(TimeoutSeconds, out var text) && !string.IsNullOrWhiteSpace(text)
            && !int.TryParse(text, out timeout))
        {
            throw Invalid(TimeoutSeconds, "must be between 1 and 600 seconds");
        }

        JurorModelSettings[]? jurors;
        try
        {
            jurors = JsonSerializer.Deserialize<JurorModelSettings[]>(json, JsonOptions);
        }
        catch (JsonException)
        {
            throw Invalid(ModelsKey, "must be a JSON array with provider, family, endpoint and deployment on every model");
        }

        return Create(jurors ?? [], timeout);
    }

    public static JurySettings Create(IReadOnlyList<JurorModelSettings> jurors, int timeoutSeconds = 120)
    {
        if (timeoutSeconds is < 1 or > 600)
        {
            throw Invalid(TimeoutSeconds, "must be between 1 and 600 seconds");
        }

        if (jurors is null || jurors.Count != 3)
        {
            throw Invalid(ModelsKey, "requires exactly three independently configured models");
        }

        var normalized = new List<JurorModelSettings>(3);
        for (var index = 0; index < jurors.Count; index++)
        {
            var juror = jurors[index];
            if (juror is null || string.IsNullOrWhiteSpace(juror.Provider) || string.IsNullOrWhiteSpace(juror.Family)
                || string.IsNullOrWhiteSpace(juror.Endpoint) || string.IsNullOrWhiteSpace(juror.Deployment))
            {
                throw Invalid(ModelsKey, $"model {index + 1} requires provider, family, endpoint and deployment");
            }

            juror = juror with
            {
                Name = string.IsNullOrWhiteSpace(juror.Name) ? $"juror-{index + 1}" : juror.Name.Trim(),
                Provider = juror.Provider.Trim().ToLowerInvariant(),
                Family = juror.Family.Trim().ToLowerInvariant(),
                Endpoint = juror.Endpoint.Trim().TrimEnd('/'),
                Deployment = juror.Deployment.Trim(),
            };
            if ((juror.Provider, juror.Family) is not (("openai", "gpt") or ("deepseek", "deepseek") or ("xai", "grok")))
            {
                throw Invalid(ModelsKey,
                    $"model {index + 1} provider/family must be openai/gpt, deepseek/deepseek or xai/grok");
            }

            if (!Uri.TryCreate(juror.Endpoint, UriKind.Absolute, out var endpoint)
                || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.AbsolutePath != "/"
                || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0 || endpoint.UserInfo.Length > 0)
            {
                throw Invalid(ModelsKey,
                    $"model {index + 1} endpoint must be an HTTPS Azure resource root without credentials, query or path");
            }

            normalized.Add(juror with { Endpoint = endpoint.GetLeftPart(UriPartial.Authority) });
        }

        if (normalized.Select(juror => juror.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3
            || normalized.Select(juror => juror.Family).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3
            || normalized.Select(juror => $"{juror.Endpoint}/{juror.Deployment}")
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3)
        {
            throw Invalid(ModelsKey, "requires distinct names, model families and endpoint/deployment targets");
        }

        return new JurySettings(normalized.ToArray(), TimeSpan.FromSeconds(timeoutSeconds));
    }

    public string SerializeModels() => JsonSerializer.Serialize(Jurors, JsonOptions);

    private static RuntimeConfigurationException Invalid(string key, string message) => new($"{key} {message}", [key]);
}
