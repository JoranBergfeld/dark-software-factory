using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dsf.Core.Onboarding;

public enum GitHubAuthorityState { Partial, Verified, Blocked, Failed, Canceled, Unknown }

public sealed record FactoryGitHubAuthority(
    int SchemaVersion, Guid FactoryId, Guid OperationId, string PlanFingerprint,
    GitHubAuthorityState State, string Stage,
    long AppId, long InstallationId, long AccountId, long RepositoryId,
    IReadOnlyDictionary<string, string> ExpectedPermissions, IReadOnlyDictionary<string, string> ExpectedAppPermissions,
    string ExpectedInstallationRepositorySelection,
    string SourceVaultId, string SourceSecretVersion, string DestinationVaultId,
    string? DestinationSecretUri, string AuthIdentityId, string AuthPrincipalId, string AuthClientId,
    string CodePrincipalId, string CodeClientId, bool AcceptedSigningRisk)
{
    public string? ApprovalFingerprint { get; init; }
    public string? RuntimeImage { get; init; }
    public string? AudienceClientId { get; init; }
    public string? KeyCopyTemplateSha256 { get; init; }
    public string? HostTemplateSha256 { get; init; }
    public IReadOnlyDictionary<string, string> BindingIncarnations { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> OtherWorkloadPrincipalIds { get; init; } = [];
    public string? HostingEnvironmentId { get; init; }
    public string? HostResourceId { get; init; }
    public string? HostEndpoint { get; init; }
    public string? FactoryResourceGroupId { get; init; }
    public Guid FoundationOperationId { get; init; }
    public IReadOnlyDictionary<string, string> BindingReceiptHashes { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> CompletedEffectHashes { get; init; } = new Dictionary<string, string>();
    public int OperationCount { get; init; }
    public string Detail { get; init; } = "Inactive authentication preparation only; publication is not authorized.";
    public GitHubAuthorityProof? Proof { get; init; }
}

public sealed record GitHubAuthorityProof(
    GitHubAuthorityState State, string Operation, string ResponsibleAdmin, string Detail)
{
    public Guid FactoryId { get; init; }
    public Guid OperationId { get; init; }
    public DateTimeOffset ObservedAt { get; init; }
    public long AppId { get; init; }
    public long InstallationId { get; init; }
    public long AccountId { get; init; }
    public long RepositoryId { get; init; }
    public string? Repository { get; init; }
    public string? DefaultBranch { get; init; }
    public string? InstallationRepositorySelection { get; init; }
    public IReadOnlyDictionary<string, string>? GrantedPermissions { get; init; }
    public IReadOnlyDictionary<string, string>? AppPermissions { get; init; }
    public IReadOnlyDictionary<string, string>? EffectivePermissions { get; init; }
    public DateTimeOffset? TokenExpiresAt { get; init; }
    public string? KeyVersionUri { get; init; }
    public string? AuthPrincipalId { get; init; }
}

public static class FactoryGitHubJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        IgnoreReadOnlyProperties = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };
}
