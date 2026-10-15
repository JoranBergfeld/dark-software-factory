using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dsf.Core.Runtime;

namespace Dsf.Core.Onboarding;

/// <summary>Shared trusted preparation, effect-provenance and token-admission invariants.</summary>
public static class FactoryGitHubAuthorityValidation
{
    public const string SigningRisk = "The factory key holder can mint broader App-authorized tokens. "
        + "Downscoping is normal discipline, NOT signing-key containment. GitHub issue-write is coarser than DSF allowed behavior; "
        + "no publication caller is eligible while inactive. Only the GitHub authentication module receives the signing key. "
        + "Approve the exact copy, bindings and host separately; no owner rotation, permission upgrades or repository writes.";
    public static IReadOnlyList<string> BindingNames { get; } = Array.AsReadOnly(new[] { "auth", "code", "council", "source", "vault", "hosting" });

    public static string ReceiptHash(FoundationOperation operation) => Hash(JsonSerializer.Serialize(operation, FactoryGitHubJson.Options));

    public static string ApprovalFingerprint(FactoryGitHubAuthority authority) => Hash(JsonSerializer.Serialize(authority with
    {
        OperationId = Guid.Empty, State = GitHubAuthorityState.Partial, Stage = "preview", AcceptedSigningRisk = false,
        ApprovalFingerprint = null, DestinationSecretUri = null, HostEndpoint = null, OperationCount = 0, Proof = null,
        CompletedEffectHashes = new Dictionary<string, string>(), Detail = SigningRisk,
    }, FactoryGitHubJson.Options));

    public static FactoryGitHubAuthority ReadPreparation(FactoryManifest manifest, IReadOnlyDictionary<string, string> values)
    {
        var authority = Read<FactoryGitHubAuthority>(values, ProductConfigurationKeys.FactoryGitHubAuthority);
        var identity = manifest.Identity;
        if (manifest.SchemaVersion != 1 || manifest.Kind != "decide-factory-manifest"
            || manifest.Authority != "product-app-configuration" || manifest.Publication != "Inactive"
            || authority.SchemaVersion != 1 || authority.FactoryId != identity.FactoryId
            || authority.OperationId == Guid.Empty || authority.OperationId != manifest.GitHubAuthorityOperationId
            || authority.FoundationOperationId == Guid.Empty || authority.PlanFingerprint != identity.PlanFingerprint
            || authority.RepositoryId != identity.RepositoryId || !authority.AcceptedSigningRisk || !Enum.IsDefined(authority.State)
            || authority.OperationCount is < 0 or > 5 || !IsHash(authority.ApprovalFingerprint)
            || authority.ApprovalFingerprint != manifest.GitHubAuthorityApprovalFingerprint
            || authority.ApprovalFingerprint != ApprovalFingerprint(authority)
            || authority.AppId <= 0 || authority.InstallationId <= 0 || authority.AccountId <= 0
            || authority.ExpectedInstallationRepositorySelection is not ("all" or "selected")
            || !IsHash(authority.KeyCopyTemplateSha256) || !IsHash(authority.HostTemplateSha256)
            || !IsGuid(authority.AudienceClientId)
            || authority.RuntimeImage is null || !Regex.IsMatch(authority.RuntimeImage, "^[a-z0-9.-]+/[a-zA-Z0-9_./-]+@sha256:[0-9a-f]{64}$")
            || !Regex.IsMatch(authority.SourceVaultId, "^/subscriptions/[0-9a-f-]{36}/resourcegroups/[a-z0-9_.()-]+/providers/microsoft.keyvault/vaults/[a-z0-9-]{3,24}$")
            || !Regex.IsMatch(authority.SourceSecretVersion, "^[0-9a-f]{32}$")
            || authority.FactoryResourceGroupId is null
            || !Regex.IsMatch(authority.FactoryResourceGroupId, "^/subscriptions/" + Regex.Escape(identity.SubscriptionId) + "/resourcegroups/[a-z0-9_.()-]+$")
            || authority.BindingIncarnations.Count != 6 || authority.BindingReceiptHashes.Count != 6
            || authority.OtherWorkloadPrincipalIds.Count != 2)
            throw new JsonException("Invalid approved GitHub preparation.");
        var prefix = "df-" + identity.FactoryId.ToString("N")[..14];
        var group = authority.FactoryResourceGroupId ?? throw new JsonException();
        var expected = new[]
        {
            ("auth", group + "/providers/microsoft.managedidentity/userassignedidentities/" + prefix + "-github", "factory:github-auth"),
            ("code", group + "/providers/microsoft.managedidentity/userassignedidentities/" + prefix + "-code", "factory:code-intent"),
            ("council", group + "/providers/microsoft.managedidentity/userassignedidentities/" + prefix + "-council", "factory:council"),
            ("source", group + "/providers/microsoft.managedidentity/userassignedidentities/" + prefix + "-source", "factory:source-azuremonitor"),
            ("vault", group + "/providers/microsoft.keyvault/vaults/" + prefix + "-vault", (string?)null),
            ("hosting", group + "/providers/microsoft.app/managedenvironments/" + prefix + "-hosting", null),
        };
        var bindings = new Dictionary<string, FoundationOperation>(StringComparer.Ordinal);
        foreach (var (name, target, logical) in expected)
        {
            var binding = Read<FoundationOperation>(values, ProductConfigurationKeys.GitHubAuthorityBinding(authority.OperationId, name));
            if (!Complete(binding) || binding.Kind != "decide-foundation-operation" || binding.FactoryId != authority.FactoryId
                || binding.OperationId != authority.FoundationOperationId || binding.Index < 0
                || binding.Target != target || binding.ResourceId != target || binding.LogicalIdentity != logical
                || binding.DeploymentTarget != group + "/providers/microsoft.resources/deployments/df-" + authority.FoundationOperationId.ToString("N")
                || !ProviderCreation(binding) || !DateTimeOffset.TryParse(binding.Incarnation, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                || created < binding.StartedAt || authority.BindingIncarnations.GetValueOrDefault(target) != binding.Incarnation
                || authority.BindingReceiptHashes.GetValueOrDefault(name) != ReceiptHash(binding))
                throw new JsonException("Invalid original foundation binding receipt.");
            bindings.Add(name, binding);
        }
        if (authority.AuthIdentityId != bindings["auth"].Target
            || authority.AuthPrincipalId != bindings["auth"].PrincipalId || authority.AuthClientId != bindings["auth"].ClientId
            || authority.CodePrincipalId != bindings["code"].PrincipalId || authority.CodeClientId != bindings["code"].ClientId
            || authority.OtherWorkloadPrincipalIds[0] != bindings["council"].PrincipalId
            || authority.OtherWorkloadPrincipalIds[1] != bindings["source"].PrincipalId
            || bindings.Take(4).Any(b => !IsGuid(b.Value.PrincipalId) || !IsGuid(b.Value.ClientId))
            || bindings.Take(4).Select(b => b.Value.PrincipalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4
            || bindings.Take(4).Select(b => b.Value.ClientId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 4
            || authority.DestinationVaultId != bindings["vault"].Target || authority.HostingEnvironmentId != bindings["hosting"].Target
            || authority.HostResourceId != group + "/providers/microsoft.app/containerapps/" + prefix + "-github-auth")
            throw new JsonException("Invalid workload/configuration binding.");
        return authority;
    }

    public static void ValidateObservation(FactoryGitHubAuthority authority)
    {
        if (authority.Stage is not ("host-verification" or "verified") || authority.DestinationSecretUri is null
            || !Regex.IsMatch(authority.DestinationSecretUri, "^https://" + Regex.Escape(authority.DestinationVaultId.Split('/').Last())
                + @"\.vault\.azure\.net/secrets/github-app-private-key/[0-9a-f]{32}$")
            || authority.HostEndpoint is null || !Regex.IsMatch(authority.HostEndpoint, @"^https://[a-z0-9.-]+\.azurecontainerapps\.io$"))
            throw new JsonException("Deployed observation is not prepared.");
    }

    public static IReadOnlyList<FoundationOperation> ReadCompletedEffects(FactoryGitHubAuthority authority, IReadOnlyDictionary<string, string> values)
    {
        if (authority.OperationCount != 5 || authority.CompletedEffectHashes.Count != 5) throw new JsonException("Incomplete effect references.");
        var operations = Enumerable.Range(0, 5).Select(i =>
        {
            var operation = Read<FoundationOperation>(values, ProductConfigurationKeys.GitHubAuthorityOperation(authority.OperationId, i));
            if (!Complete(operation) || operation.Kind != "decide-github-operation" || operation.FactoryId != authority.FactoryId
                || operation.OperationId != authority.OperationId || operation.Index != i
                || authority.CompletedEffectHashes.GetValueOrDefault(i.ToString(CultureInfo.InvariantCulture)) != ReceiptHash(operation))
                throw new JsonException("Missing, stale or foreign effect receipt.");
            return operation;
        }).ToArray();
        foreach (var index in new[] { 0, 3, 4 })
            if (!ProviderCreation(operations[index])) throw new JsonException("Missing provider creation evidence.");
        var group = authority.FactoryResourceGroupId ?? throw new JsonException();
        var deployment = group + "/providers/microsoft.resources/deployments/gh-" + authority.OperationId.ToString("N");
        var roles = group[..group.IndexOf("/resourcegroups/", StringComparison.Ordinal)] + "/providers/microsoft.authorization/roledefinitions/";
        var secret = authority.DestinationVaultId + "/secrets/github-app-private-key";
        var config = group + "/providers/microsoft.appconfiguration/configurationstores/dsfo-" + authority.FactoryId.ToString("N");
        if (operations[0].Target != deployment + "-key" || operations[0].ResourceId != secret
            || operations[0].Endpoint != authority.DestinationSecretUri
            || operations[0].Incarnation?.StartsWith("key-version:" + authority.DestinationSecretUri + ";created:", StringComparison.Ordinal) != true
            || operations[3].Target != deployment + "-host" || operations[3].ResourceId != authority.HostResourceId
            || operations[4].Target != authority.HostResourceId + "/authconfigs/current" || operations[4].ResourceId != operations[4].Target
            || operations[4].DeploymentTarget != operations[3].Target || operations[4].ClientRequestId != operations[3].ClientRequestId
            || operations[4].ProviderCorrelationId != operations[3].ProviderCorrelationId)
            throw new JsonException("Invalid key/host creation binding.");
        foreach (var (index, scope, definition) in new[]
        {
            (1, secret, "4633458b-17de-408a-b874-0445c86b69e6"), (2, config, "516239f1-63e1-4d78-a4de-a74fb236a071"),
        })
        {
            var role = operations[index];
            if (role.RoleScope != scope || role.RoleDefinitionId != roles + definition || role.Condition is not null
                || role.ConditionVersion is not (null or "2.0")
                || role.PrincipalId != authority.AuthPrincipalId || role.LogicalIdentity != "factory:github-auth"
                || role.ResourceId != role.Target || !role.Target.StartsWith(scope + "/providers/microsoft.authorization/roleassignments/", StringComparison.Ordinal)
                || !IsGuid(role.Target.Split('/').Last()) || !DateTimeOffset.TryParse(role.Incarnation, CultureInfo.InvariantCulture, DateTimeStyles.None, out var created)
                || created < role.StartedAt)
                throw new JsonException("Invalid exact authentication grant.");
        }
        return operations;
    }

    public static void ValidateVerified(FactoryGitHubAuthority authority)
    {
        if (authority.State != GitHubAuthorityState.Verified || authority.Stage != "verified"
            || authority.Proof is not { } proof || !HasVerifiedScope(authority, proof))
            throw new JsonException("Authority is not verified for workload tokens.");
    }

    public static bool HasVerifiedScope(FactoryGitHubAuthority authority, GitHubAuthorityProof proof) =>
        proof.State == GitHubAuthorityState.Verified && proof.Operation == "deployed-github-authentication"
        && proof.FactoryId == authority.FactoryId && proof.OperationId == authority.OperationId
        && proof.AppId == authority.AppId && proof.InstallationId == authority.InstallationId
        && proof.AccountId == authority.AccountId && proof.RepositoryId == authority.RepositoryId
        && proof.AuthPrincipalId == authority.AuthPrincipalId && proof.KeyVersionUri == authority.DestinationSecretUri
        && proof.ObservedAt != default && proof.TokenExpiresAt is { } expiry && expiry > proof.ObservedAt
        && expiry - proof.ObservedAt <= TimeSpan.FromHours(1).Add(TimeSpan.FromSeconds(30))
        && !string.IsNullOrWhiteSpace(proof.Repository) && !string.IsNullOrWhiteSpace(proof.DefaultBranch)
        && proof.InstallationRepositorySelection == authority.ExpectedInstallationRepositorySelection
        && PermissionsMatch(proof.AppPermissions, authority.ExpectedAppPermissions)
        && PermissionsMatch(proof.GrantedPermissions, authority.ExpectedPermissions)
        && proof.EffectivePermissions?.Count == 2 && proof.EffectivePermissions.GetValueOrDefault("metadata") == "read"
        && proof.EffectivePermissions.GetValueOrDefault("contents") == "read";

    private static bool PermissionsMatch(IReadOnlyDictionary<string, string>? actual, IReadOnlyDictionary<string, string> expected) =>
        actual is not null && actual.Count == expected.Count && actual.All(p => expected.GetValueOrDefault(p.Key) == p.Value);
    private static bool Complete(FoundationOperation operation) => operation.SchemaVersion == 1
        && operation.State == FoundationOperationState.Completed && operation.Disposition == FoundationResourceDisposition.Created
        && operation.ClientRequestId != Guid.Empty && operation.StartedAt != default && IsHash(operation.RequestSha256)
        && !string.IsNullOrWhiteSpace(operation.Incarnation);
    private static bool ProviderCreation(FoundationOperation operation) => IsGuid(operation.ProviderRequestId)
        && IsGuid(operation.ProviderCorrelationId) && operation.ProviderOperationId is { } id && Regex.IsMatch(id, "^[a-fA-F0-9-]{1,128}$");
    private static bool IsGuid(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static T Read<T>(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value) || Encoding.UTF8.GetByteCount(value) >= 10 * 1024) throw new JsonException("Missing or oversized authority record.");
        return JsonSerializer.Deserialize<T>(value, FactoryGitHubJson.Options) ?? throw new JsonException();
    }
}
