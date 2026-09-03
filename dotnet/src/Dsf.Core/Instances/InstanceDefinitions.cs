using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsf.Core.Instances;

/// <summary>
/// Reads and writes versioned instance definitions under the conventional
/// <c>config/instances/&lt;product&gt;.json</c> path.
/// Anything that is not a supported definition — a legacy execution manifest,
/// an unknown schema version, or malformed JSON — fails loudly with guidance to
/// regenerate the file rather than being silently coerced.
/// </summary>
public static class InstanceDefinitions
{
    /// <summary>The only schema version this build reads or writes.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Directory holding per-product instance definitions.</summary>
    public static string DirectoryFor(string repoRoot) => Path.Combine(repoRoot, "config", "instances");

    /// <summary>Conventional path of one product's instance definition.</summary>
    public static string PathFor(string repoRoot, string product) =>
        Path.Combine(DirectoryFor(repoRoot), $"{product}.json");

    public static string Serialize(InstanceDefinition definition) =>
        JsonSerializer.Serialize(definition, SerializerOptions) + "\n";

    /// <summary>Writes a definition to its conventional path and returns that path.</summary>
    public static string Write(InstanceDefinition definition, string repoRoot)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var path = PathFor(repoRoot, definition.Product.Key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Serialize(definition));
        return path;
    }

    /// <summary>Reads and validates the definition at <paramref name="path"/>.</summary>
    public static InstanceDefinition Read(string path)
    {
        if (!File.Exists(path))
        {
            throw new InstanceDefinitionException(Guidance($"No instance definition found at '{path}'."));
        }

        return Parse(File.ReadAllText(path), path);
    }

    /// <summary>Validates and materializes a definition from JSON. <paramref name="source"/> names the file for errors.</summary>
    public static InstanceDefinition Parse(string json, string source)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InstanceDefinitionException(
                Guidance($"Instance definition '{source}' is not valid JSON: {exception.Message}"),
                exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InstanceDefinitionException(
                    Guidance($"Instance definition '{source}' must be a JSON object."));
            }

            if (!root.TryGetProperty("schemaVersion", out var version))
            {
                var legacy = root.TryGetProperty("spec", out _) || root.TryGetProperty("plan", out _);
                throw new InstanceDefinitionException(Guidance(
                    legacy
                        ? $"Instance definition '{source}' is a legacy execution manifest (spec/plan/steps), which is no longer supported."
                        : $"Instance definition '{source}' has no root 'schemaVersion'; legacy files are no longer supported."));
            }

            if (version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out var schemaVersion)
                || schemaVersion != CurrentSchemaVersion)
            {
                var found = version.ValueKind == JsonValueKind.Number ? version.GetRawText() : version.ToString();
                throw new InstanceDefinitionException(Guidance(
                    $"Instance definition '{source}' declares schemaVersion {found}; " +
                    $"this dsf build only supports schemaVersion {CurrentSchemaVersion}."));
            }
        }

        InstanceDefinition? definition;
        try
        {
            definition = JsonSerializer.Deserialize<InstanceDefinition>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            throw new InstanceDefinitionException(
                Guidance($"Instance definition '{source}' does not match schemaVersion {CurrentSchemaVersion}: {exception.Message}"),
                exception);
        }

        return definition
            ?? throw new InstanceDefinitionException(Guidance($"Instance definition '{source}' is empty."));
    }

    private static string Guidance(string problem) =>
        $"{problem} Delete it and regenerate the configuration with " +
        "`dsf new --product <product> --dry-run --write-plan`.";
}

/// <summary>Raised when an instance definition is missing, unreadable, legacy, or of an unsupported schema version.</summary>
public sealed class InstanceDefinitionException : Exception
{
    public InstanceDefinitionException()
    {
    }

    public InstanceDefinitionException(string message)
        : base(message)
    {
    }

    public InstanceDefinitionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
