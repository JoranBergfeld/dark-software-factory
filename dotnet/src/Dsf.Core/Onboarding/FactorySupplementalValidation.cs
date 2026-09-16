using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dsf.Core.Onboarding;

public static class FactorySupplementalValidation
{
    public const string OperationKind = "decide-supplemental-operation";
    public const string SecretsRole = "4633458b-17de-408a-b874-0445c86b69e6";
    public const string SearchDataRole = "1407120a-92aa-4202-b7e9-c0e197c71c8f";
    public const string Coverage = "Supplemental evidence only; legitimate empty results do not prove application telemetry. Fresh dedicated Azure application telemetry remains mandatory. No activation or council preview asserted.";
    public static void ValidateSelection(SupplementalSelection selection, string subscription)
    {
        if (selection.SchemaVersion != 1 || selection.Sources.Count is < 1 or > 2 || selection.Sources.Any(s => s is null)
            || selection.Sources.Select(s => s.Kind).Distinct(StringComparer.Ordinal).Count() != selection.Sources.Count
            || !Regex.IsMatch(selection.RuntimeImage, "^[a-z0-9.-]+/[a-zA-Z0-9_./-]+@sha256:[0-9a-f]{64}$")
            || selection.RuntimeImage.Length > 256 || !Guid.TryParseExact(selection.AudienceClientId, "D", out var client) || client == Guid.Empty)
            throw new JsonException("Exact bounded supplemental selection required.");
        foreach (var source in selection.Sources) ValidateSource(source, subscription);
    }

    public static void ValidateSource(SupplementalSourceSelection source, string subscription)
    {
        if (source.Kind is not ("webiq" or "foundryiq") || !source.PublicQueryApproved
            || string.IsNullOrWhiteSpace(source.Query) || source.Query.Length > 1000
            || source.Query.Any(c => c is < ' ' or > '~'))
            throw new JsonException("Operator-reviewed public-safe bounded query required; no private input inference.");
        if (source.Kind == "webiq")
        {
            if (source.SecretName != "webiq-api-key" || source.SecretVersion is not { Length: 32 }
                || !source.SecretVersion.All(char.IsAsciiHexDigit) || source.SearchResourceId is not null
                || source.KnowledgeBase is not null || source.KnowledgeBaseETag is not null
                || source.MetadataRoleDefinitionId is not null || source.DedicatedSearchService || source.KnowledgeSources.Count != 0)
                throw new JsonException("Only operator-seeded versioned product webiq-api-key is supported; no direct/environment/owner key.");
        }
        else if (source.SecretName is not null || source.SecretVersion is not null || !source.DedicatedSearchService
            || source.SearchResourceId is null || !Regex.IsMatch(source.SearchResourceId,
                "^/subscriptions/" + Regex.Escape(subscription) + "/resourcegroups/[a-z0-9_.()-]+/providers/microsoft.search/searchservices/[a-z0-9-]+$")
            || !Name(source.KnowledgeBase) || !ETag(source.KnowledgeBaseETag)
            || source.MetadataRoleDefinitionId is null || !Regex.IsMatch(source.MetadataRoleDefinitionId,
                "^/subscriptions/" + Regex.Escape(subscription) + "/providers/microsoft.authorization/roledefinitions/[0-9a-f-]{36}$")
            || source.MetadataRoleDefinitionId.EndsWith("/" + SecretsRole, StringComparison.Ordinal)
            || source.MetadataRoleDefinitionId.EndsWith("/" + SearchDataRole, StringComparison.Ordinal)
            || source.KnowledgeSources.Count is < 1 or > 4 || source.KnowledgeSources.Any(s => s is null)
            || source.KnowledgeSources.Select(s => s.Name).Distinct(StringComparer.Ordinal).Count() != source.KnowledgeSources.Count
            || source.KnowledgeSources.Select(s => s.IndexName).Distinct(StringComparer.Ordinal).Count() != source.KnowledgeSources.Count
            || source.KnowledgeSources.Any(s => !Name(s.Name) || !Name(s.IndexName) || !ETag(s.SourceETag) || !ETag(s.IndexETag)))
            throw new JsonException("Existing dedicated Search knowledge base, exact searchIndex sources/indexes/ETags and narrow metadata role required; other source modes unsupported.");
    }

    public static void ValidateHostConfiguration(SupplementalHostConfiguration config)
    {
        if (config.FactoryId == Guid.Empty || !Id(config.TenantId) || !Id(config.OperatorPrincipalId)
            || !Id(config.SourcePrincipalId) || !Id(config.SourceClientId) || config.SourcePrincipalId == config.OperatorPrincipalId
            || config.ConfigurationRevision.Length != 64 || !config.ConfigurationRevision.All(char.IsAsciiHexDigit))
            throw new JsonException("Explicit native supplemental identity/configuration required.");
        var match = Regex.Match(config.SourceIdentityId, "^/subscriptions/([0-9a-f-]{36})/resourcegroups/[a-z0-9_.()-]+/providers/microsoft.managedidentity/userassignedidentities/"
            + Prefix(config.FactoryId) + "-(foundryiq|webiq)$");
        if (!match.Success || !Id(match.Groups[1].Value) || match.Groups[2].Value != config.Source.Kind) throw new JsonException();
        ValidateSource(config.Source, match.Groups[1].Value);
        if (config.Source.Kind == "webiq"
            ? config.SecretUri != "https://" + Prefix(config.FactoryId) + "-vault.vault.azure.net/secrets/webiq-api-key/" + config.Source.SecretVersion
            : config.SecretUri is not null)
            throw new JsonException("Only exact operator-seeded product-vault source secret permitted.");
        static bool Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    }

    public static string Prefix(Guid factory) => "df-" + factory.ToString("N")[..14];
    public static string IdentityTarget(SupplementalConfiguration config, string kind) =>
        config.FactoryGroup + "/providers/microsoft.managedidentity/userassignedidentities/" + Prefix(config.FactoryId) + "-" + kind;
    public static string HostTarget(SupplementalConfiguration config, string kind) =>
        config.FactoryGroup + "/providers/microsoft.app/containerapps/" + Prefix(config.FactoryId) + "-" + kind;
    public static string SearchEndpoint(SupplementalSourceSelection source) =>
        "https://" + source.SearchResourceId!.Split('/').Last() + ".search.windows.net";
    public static string SecretUri(SupplementalConfiguration config, SupplementalSourceSelection source) =>
        "https://" + config.Vault!.Target.Split('/').Last() + ".vault.azure.net/secrets/" + source.SecretName + "/" + source.SecretVersion;
    public static IReadOnlyList<SupplementalGrant> Grants(SupplementalConfiguration config, SupplementalSourceSelection source)
    {
        var subscription = config.FactoryGroup.Split('/')[2];
        var roles = "/subscriptions/" + subscription + "/providers/microsoft.authorization/roledefinitions/";
        var scopes = source.Kind == "webiq"
            ? new[] { (Scope: config.Vault!.Target + "/secrets/" + source.SecretName, Role: roles + SecretsRole) }
            : new[] { (Scope: source.SearchResourceId!, Role: roles + SearchDataRole), (Scope: source.SearchResourceId!, Role: source.MetadataRoleDefinitionId!) };
        return scopes.Select(pair =>
        {
            var name = new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                config.FactoryId + ":" + source.Kind + ":" + pair.Scope + ":" + pair.Role))[..16]);
            var secret = pair.Role == roles + SecretsRole;
            var data = pair.Role == roles + SearchDataRole;
            return new SupplementalGrant(pair.Scope + "/providers/microsoft.authorization/roleassignments/" + name, pair.Scope, pair.Role)
            {
                Actions = secret || data ? [] :
                [
                    "Microsoft.Search/searchServices/read", "Microsoft.Search/searchServices/indexes/read",
                    "Microsoft.Search/searchServices/knowledgebases/read", "Microsoft.Search/searchServices/knowledgesources/read",
                ],
                DataActions = secret
                    ? ["Microsoft.KeyVault/vaults/secrets/getSecret/action", "Microsoft.KeyVault/vaults/secrets/readMetadata/action"]
                    : data ? ["Microsoft.Search/searchServices/indexes/documents/read"] : [],
            };
        }).ToArray();
    }

    public static IReadOnlyList<SupplementalSourceResources> Resources(SupplementalConfiguration config) =>
        config.Selection.Sources.Select(source => new SupplementalSourceResources(source.Kind, IdentityTarget(config, source.Kind),
            HostTarget(config, source.Kind), Grants(config, source))).ToArray();

    public static IReadOnlyList<(string Target, string Api)> Targets(SupplementalConfiguration config) =>
        config.Selection.Sources.SelectMany(source => new[] { (IdentityTarget(config, source.Kind), "2024-11-30") }
            .Concat(Grants(config, source).Select(g => (g.Target, "2022-04-01")))
            .Concat([(HostTarget(config, source.Kind), "2025-01-01"), (HostTarget(config, source.Kind) + "/authconfigs/current", "2025-01-01")])).ToArray();

    public static SupplementalHostConfiguration HostConfiguration(FactoryManifest manifest, FactorySupplemental authority, FoundationOperation identity, string kind)
    {
        var config = authority.Configuration!;
        var source = config.Selection.Sources.Single(s => s.Kind == kind);
        return new(config.FactoryId, manifest.Identity.TenantId, manifest.Identity.AzureActorId, identity.Target,
            identity.PrincipalId!, identity.ClientId!, authority.ApprovalFingerprint!, source,
            kind == "webiq" ? SecretUri(config, source) : null);
    }

    public static FactorySupplemental Read(FactoryManifest manifest, string value)
    {
        var authority = JsonSerializer.Deserialize<FactorySupplemental>(value, FactoryGitHubJson.Options) ?? throw new JsonException();
        var config = authority.Configuration ?? throw new JsonException();
        ValidateSelection(config.Selection, manifest.Identity.SubscriptionId);
        if (authority.OperationId == Guid.Empty || authority.OperationId != manifest.SupplementalOperationId
            || authority.ApprovalFingerprint != manifest.SupplementalApprovalFingerprint
            || FactoryTelemetryValidation.Hash(config) != authority.ApprovalFingerprint
            || config.ResourceApprovalSha256 != FactoryTelemetryValidation.Hash(Resources(config))
            || config.FactoryId != manifest.Identity.FactoryId || config.ProfilePin != manifest.ProfilePin
            || config.PlanFingerprint != manifest.Identity.PlanFingerprint || config.TemplateSha256 is not { Length: 64 }
            || config.ProductEndpoint != "https://dsfo-" + config.FactoryId.ToString("N") + ".azconfig.io"
            || config.Hosting.Target != config.FactoryGroup + "/providers/microsoft.app/managedenvironments/" + Prefix(config.FactoryId) + "-hosting"
            || config.Vault is not null && config.Vault.Target != config.FactoryGroup + "/providers/microsoft.keyvault/vaults/" + Prefix(config.FactoryId) + "-vault"
            || authority.OperationCount < 0 || authority.OperationCount > Targets(config).Count
            || !Enum.IsDefined(authority.State) || authority.State == SupplementalState.NotSelected
            || authority.Checks.Any(c => c is null)
            || authority.Stage is not ("preparation" or "prepared" or "checking" or "observed" or "held")
            || authority.State == SupplementalState.ReadySupplemental && (authority.Stage != "observed" || authority.CheckId is null
                || !authority.Checks.Select(c => c.Kind).SequenceEqual(config.Selection.Sources.Select(s => s.Kind))
                || authority.Checks.Any(c => c.State != SupplementalState.ReadySupplemental))) throw new JsonException();
        return authority;
    }

    public static void ValidateProof(FactorySupplemental authority, FoundationOperation identity, SupplementalProof proof, DateTimeOffset now)
    {
        var config = authority.Configuration!;
        var source = config.Selection.Sources.Single(s => s.Kind == proof.Kind);
        if (proof.FactoryId != config.FactoryId || proof.ConfigurationRevision != authority.ApprovalFingerprint
            || identity.Target != IdentityTarget(config, source.Kind)
            || proof.CheckId != authority.CheckId || proof.CheckId == Guid.Empty || proof.SourcePrincipalId != identity.PrincipalId
            || proof.Coverage != Coverage || proof.SecretVersion != source.SecretVersion || !Enum.IsDefined(proof.State) || !Enum.IsDefined(proof.Reason)
            || proof.ObservedAt > now || proof.ObservedAt < now.AddMinutes(-2) || proof.Records.Count > 16
            || proof.Records.Any(r => r is null || !r.Reference.StartsWith(source.Kind + ":", StringComparison.Ordinal)
                || r.Reference.Length > 320 || r.Reference.Any(char.IsControl)
                || r.ContentSha256.Length != 64 || !r.ContentSha256.All(char.IsAsciiHexDigit))
            || proof.State == SupplementalState.ReadySupplemental
                && (proof.NativeSourceRevision is not { Length: 64 } || !proof.NativeSourceRevision.All(char.IsAsciiHexDigit)
                    || proof.Reason != (proof.Records.Count == 0 ? SupplementalReason.Empty : SupplementalReason.None))
            || proof.State != SupplementalState.ReadySupplemental && proof.Records.Count != 0)
            throw new JsonException("Exact complete request-bound supplemental proof required.");
    }
    private static bool Name(string? value) => value is { Length: >= 1 and <= 64 } && Regex.IsMatch(value, "^[a-z0-9][a-z0-9_-]*$");
    private static bool ETag(string? value) => value is { Length: >= 1 and <= 80 } && value.All(c => c is >= ' ' and <= '~');
}
