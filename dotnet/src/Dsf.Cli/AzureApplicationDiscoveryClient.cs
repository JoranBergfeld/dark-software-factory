using System.Text.Json;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

/// <summary>The tenant/subscription the operator's Azure CLI identity is currently using.</summary>
internal sealed record AzureIdentityContext(string TenantId, string SubscriptionId, string SubscriptionName, string User);

/// <summary>
/// Read-only Azure discovery for onboarding preview. Every member lists or reads; the
/// port deliberately exposes no mutation so a preview cannot acquire, tag, or provision.
/// </summary>
internal interface IAzureApplicationDiscoveryClient
{
    Task<AzureIdentityContext?> GetSignedInContextAsync(CancellationToken cancellationToken);

    Task<AzureApplicationInventory> DiscoverAsync(
        string tenantId,
        string subscriptionId,
        CancellationToken cancellationToken);
}

/// <summary>Discovers application resources and evidence backends through the operator's <c>az</c> CLI.</summary>
internal sealed class AzureCliApplicationDiscoveryClient(IAzureCliRunner runner) : IAzureApplicationDiscoveryClient
{
    private const string EvidenceBackendType = "microsoft.operationalinsights/workspaces";

    internal static AzureCliApplicationDiscoveryClient FromEnvironment() =>
        new(new SystemAzureCliRunner());

    public async Task<AzureIdentityContext?> GetSignedInContextAsync(CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(["account", "show", "-o", "json"], cancellationToken);
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        return new AzureIdentityContext(
            Text(root, "tenantId"),
            Text(root, "id"),
            Text(root, "name"),
            root.TryGetProperty("user", out var user) ? Text(user, "name") : string.Empty);
    }

    public async Task<AzureApplicationInventory> DiscoverAsync(
        string tenantId,
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(
            ["resource", "list", "--subscription", subscriptionId, "-o", "json"],
            cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Azure discovery failed for subscription {subscriptionId}: {result.StandardError.Trim()}");
        }

        var resources = new List<ApplicationResourceCandidate>();
        var backends = new List<EvidenceBackendCandidate>();
        using var document = JsonDocument.Parse(
            string.IsNullOrWhiteSpace(result.StandardOutput) ? "[]" : result.StandardOutput);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var type = Text(element, "type");
            // A resource with no readable tag collection is not treated as unclaimed:
            // the planner rejects it instead of guessing.
            var unreadable = !element.TryGetProperty("tags", out var tagsElement);
            var tags = ReadTags(unreadable ? default : tagsElement);
            if (string.Equals(type, EvidenceBackendType, StringComparison.OrdinalIgnoreCase))
            {
                backends.Add(new EvidenceBackendCandidate
                {
                    ResourceId = Text(element, "id"),
                    Name = Text(element, "name"),
                    Kind = "loganalytics",
                    SubscriptionId = subscriptionId,
                    TenantId = tenantId,
                    Environment = Tag(tags, "environment", "env"),
                    Shared = IsTrue(Tag(tags, "dsf-shared", "shared")),
                    ExistingClaimSignal = Tag(tags, "dsf-product", "dsf-factory-id"),
                    MetadataUnreadable = unreadable,
                });
                continue;
            }

            resources.Add(new ApplicationResourceCandidate
            {
                ResourceId = Text(element, "id"),
                Name = Text(element, "name"),
                Type = type,
                ResourceGroup = Text(element, "resourceGroup"),
                SubscriptionId = subscriptionId,
                TenantId = tenantId,
                Environment = Tag(tags, "environment", "env"),
                Shared = IsTrue(Tag(tags, "dsf-shared", "shared")),
                ExistingClaimSignal = Tag(tags, "dsf-product", "dsf-factory-id"),
                MetadataUnreadable = unreadable,
            });
        }

        return new AzureApplicationInventory
        {
            TenantId = tenantId,
            SubscriptionId = subscriptionId,
            Resources = resources,
            EvidenceBackends = backends,
        };
    }

    private static IReadOnlyDictionary<string, string> ReadTags(JsonElement tags)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (tags.ValueKind != JsonValueKind.Object)
        {
            return values;
        }

        foreach (var property in tags.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.ToString();
        }

        return values;
    }

    private static string Tag(IReadOnlyDictionary<string, string> tags, params string[] names)
    {
        foreach (var name in names)
        {
            if (tags.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return string.Empty;
    }

    private static bool IsTrue(string value) =>
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
