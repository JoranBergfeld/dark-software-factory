using System.Text.RegularExpressions;
using Dsf.Core.Onboarding;

namespace Dsf.Cli;

internal sealed class BoundaryInputException(string detail) : Exception(detail);

internal static class BoundarySelectionInput
{
    public static ApplicationBoundarySelection Normalize(ApplicationBoundarySelection value)
    {
        if (value.SchemaVersion != 1) Invalid("schemaVersion must be 1");
        var tenant = GuidValue(value.TenantId, "tenantId");
        var subscription = GuidValue(value.SubscriptionId, "subscriptionId");
        var repository = Text(value.Repository, "repository").ToLowerInvariant();
        if (!Regex.IsMatch(repository, @"^[a-z0-9][a-z0-9-]*/[a-z0-9_.-]+$") || repository.Split('/')[1] is "." or "..")
            Invalid("repository must be OWNER/REPO");
        try
        {
            new RepositoryAttachmentReviewRequest(repository, value.OwnerAppConfigEndpoint?.Trim(), value.OwnerKeyVaultUri?.Trim()).Validate();
        }
        catch (InvalidOperationException exception)
        {
            throw new BoundaryInputException(exception.Message);
        }
        var ownerEndpoint = Endpoint(value.OwnerAppConfigEndpoint, "ownerAppConfigEndpoint");
        var vault = Endpoint(value.OwnerKeyVaultUri, "ownerKeyVaultUri");
        var product = Name(value.ProductKey, "productKey");
        var environment = ApplicationBoundaryPolicy.EnvironmentName(Name(value.ApplicationEnvironment, "applicationEnvironment"));
        var apps = Ids(value.ApplicationResourceIds, "applicationResourceIds");
        if (apps.Length == 0) Invalid("applicationResourceIds must select at least one individual resource");
        if (value.EvidenceBackends is null || value.EvidenceBackends.Count == 0) Invalid("evidenceBackends must select dedicated workspace evidence");
        var backends = value.EvidenceBackends!.Select(backend =>
        {
            if (backend is null) Invalid("evidenceBackends cannot contain null");
            var id = Id(backend!.ResourceId, "evidenceBackends.resourceId");
            if (backend.Tables is null || backend.Tables.Count == 0) Invalid("each evidence backend requires explicit tables");
            var tables = backend.Tables!.Select(table => Text(table, "tables"))
                .Select(table => ApplicationBoundaryPolicy.SupportedTables.FirstOrDefault(t => t.Equals(table, StringComparison.OrdinalIgnoreCase)) ?? table)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (tables.Any(t => !Regex.IsMatch(t, @"^[A-Za-z][A-Za-z0-9_]{0,127}$"))) Invalid("tables must be individual table names, not queries");
            return new EvidenceBackendSelection { ResourceId = id, Tables = tables };
        }).OrderBy(b => b.ResourceId, StringComparer.Ordinal).ToArray();
        if (backends.Select(b => b.ResourceId).Distinct().Count() != backends.Length) Invalid("duplicate evidence backend entries are ambiguous; combine their tables explicitly");
        var models = Ids(value.ModelResourceIds, "modelResourceIds");
        if (apps.Intersect(backends.Select(b => b.ResourceId)).Any()
            || models.Intersect(apps.Concat(backends.Select(b => b.ResourceId))).Any())
            Invalid("application resources, evidence backends and reused model prerequisites must be separate selections");
        if (value.AutomationDeclarations is null || value.AutomationDeclarations.Count != 3
            || Enum.GetValues<RepositoryMutationClass>().Any(m => !value.AutomationDeclarations.TryGetValue(m, out var declaration) || !Enum.IsDefined(declaration)))
            Invalid("automationDeclarations must explicitly cover ProposalIssue, CharterPullRequest and LabelCreation");
        var automationOwner = value.AutomationOwner?.Trim() ?? "";
        if (value.AutomationDeclarations!.Values.Any(d => d != RepositoryAutomationDeclaration.Unknown))
            automationOwner = Text(automationOwner, "automationOwner");
        var review = value.BoundaryReview is { } r ? r with
        {
            SelectionFingerprint = Text(r.SelectionFingerprint, "boundaryReview.selectionFingerprint").ToLowerInvariant(),
            AccountableOwner = Text(r.AccountableOwner, "boundaryReview.accountableOwner"),
        } : null;
        return value with
        {
            Repository = repository, OwnerAppConfigEndpoint = ownerEndpoint, OwnerKeyVaultUri = vault,
            TenantId = tenant, SubscriptionId = subscription, ProductKey = product, ApplicationEnvironment = environment,
            ApplicationResourceIds = apps, EvidenceBackends = backends, ModelResourceIds = models,
            FactoryResourceGroup = Name(value.FactoryResourceGroup, "factoryResourceGroup"),
            FactoryLocation = Name(value.FactoryLocation, "factoryLocation"),
            AutomationOwner = automationOwner, BoundaryReview = review,
        };

        string[] Ids(IReadOnlyList<string>? ids, string field)
        {
            if (ids is null) Invalid(field + " must be an explicit array (empty only for model prerequisites)");
            return ids!.Select(id => Id(id, field)).Distinct().Order(StringComparer.Ordinal).ToArray();
        }
        string Id(string? id, string field) => ResourceId(id, subscription, field);
    }

    internal static string ResourceId(string? id, string subscription, string field)
    {
        var canonical = Text(id, field).TrimEnd('/').ToLowerInvariant();
        if (!Regex.IsMatch(canonical,
                @"^/subscriptions/[0-9a-f-]{36}/resourcegroups/[a-z0-9_.()-]+/providers/[a-z0-9.]+(?:/[a-z0-9_.()-]+/[a-z0-9_.()-]+)+$")
            || !canonical.StartsWith("/subscriptions/" + subscription + "/", StringComparison.Ordinal))
            Invalid(field + " must contain individual ARM resource IDs in the selected subscription, never groups, URLs or another subscription");
        return canonical;
    }

    internal static string GuidValue(string? value, string field) => Guid.TryParse(value?.Trim(), out var id) && id != Guid.Empty
        ? id.ToString() : throw new BoundaryInputException($"Invalid planning input: {field} must be an explicit nonempty GUID.");
    internal static string Name(string? value, string field)
    {
        var name = Text(value, field).ToLowerInvariant();
        if (!Regex.IsMatch(name, @"^[a-z0-9][a-z0-9_-]{0,89}$")) Invalid(field + " must be an explicit name using letters, digits, hyphens or underscores");
        return name;
    }
    private static string Endpoint(string? value, string field) => new Uri(Text(value, field)).GetLeftPart(UriPartial.Authority).ToLowerInvariant();
    private static string Text(string? value, string field)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 2048 || text.Any(c => char.IsControl(c)
                || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format))
            Invalid(field + " is required and must be bounded nonsecret text without control characters");
        return text!;
    }
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Invalid(string detail) => throw new BoundaryInputException("Invalid planning input: " + detail + ".");
}
